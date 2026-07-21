using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LilithMod
{
    /// <summary>
    /// Owns the speech queue and a dedicated background thread that drains it.
    /// Blocks on a warm-up signal before processing real utterances.
    /// All synthesis and playback happen off the Unity main thread.
    /// </summary>
    public class SpeechQueueProcessor : IDisposable
    {
        private readonly ConcurrentQueue<Utterance> _queue = new ConcurrentQueue<Utterance>();
        private readonly TtsClient _ttsClient;
        private readonly VoicePlayer _voicePlayer;
        private readonly ManualResetEventSlim _warmUpComplete = new ManualResetEventSlim(false);
        private readonly AutoResetEvent _queueAvailable = new AutoResetEvent(false);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _workerThread;
        private Thread _warmUpThread;
        private readonly string _playbackLockPath;

        // Repeated identical failures are collapsed; see the catch in ProcessLoop.
        private string _lastFailure;
        private int _suppressedFailures;

        public SpeechQueueProcessor(TtsClient ttsClient, VoicePlayer voicePlayer)
        {
            _ttsClient = ttsClient ?? throw new ArgumentNullException(nameof(ttsClient));
            _voicePlayer = voicePlayer ?? throw new ArgumentNullException(nameof(voicePlayer));
            string pluginDirectory = Path.GetDirectoryName(typeof(SpeechQueueProcessor).Assembly.Location) ?? ".";
            _playbackLockPath = Path.Combine(pluginDirectory, "voice-output.active");
        }

        /// <summary>
        /// Subtitles to show, in the order their audio plays. Written by the voice
        /// thread, drained by LlmChatController on the Unity main thread.
        /// </summary>
        public ConcurrentQueue<SubtitleCue> SubtitleQueue { get; } = new ConcurrentQueue<SubtitleCue>();
        internal ConcurrentQueue<NativeDialogueCue> NativeDialogueQueue { get; } =
            new ConcurrentQueue<NativeDialogueCue>();

        /// <summary>
        /// Raised once when synthesis fails and the remaining sentences are abandoned,
        /// so the main thread can fall back to showing the whole reply. Kept separate
        /// from SubtitleQueue: a magic string in a queue of user-visible text is a
        /// defect waiting to be displayed.
        /// </summary>
        public ConcurrentQueue<bool> VoiceFailureQueue { get; } = new ConcurrentQueue<bool>();
        public ConcurrentQueue<bool> ReplyFinishedQueue { get; } = new ConcurrentQueue<bool>();

        /// <summary>Thread-safe enqueue for the Unity main thread.</summary>
        public void Enqueue(Utterance utterance)
        {
            if (utterance == null || string.IsNullOrEmpty(utterance.JaText))
                return;
            _queue.Enqueue(utterance);
            _queueAvailable.Set();
        }

        /// <summary>
        /// Convenience overload for the malformed-reply fallback, where there is no
        /// Japanese/English split and the text is simply spoken as-is.
        /// </summary>
        public void Enqueue(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            _queue.Enqueue(new Utterance
            {
                JaText = text,
                EnText = text,
                Language = PersonaPrompt.CurrentVoiceLanguage(),
                EndOfReply = true
            });
            _queueAvailable.Set();
        }

        /// <summary>
        /// Abandon whatever is still queued for the previous reply. The sentence
        /// already inside PlaySync is allowed to finish - cutting audio mid-word is
        /// worse than a short overlap, and cancellation is observed between sentences.
        /// </summary>
        public void CancelCurrent()
        {
            // Cancelled, never discarded: a discarded cue leaked the coordinator's
            // pending entry forever and left its bubble suppressed with no re-show.
            // A cancelled one still flows through and clears that entry.
            int held = NativeDialogueQueue.Count;
            for (int i = 0; i < held && NativeDialogueQueue.TryDequeue(out NativeDialogueCue heldCue); i++)
            {
                heldCue.Cancel();
                NativeDialogueQueue.Enqueue(heldCue);
            }
            while (_queue.TryDequeue(out Utterance dropped))
            {
                if (dropped.NativeDialogue == null) continue;
                dropped.NativeDialogue.Cancel();
                NativeDialogueQueue.Enqueue(dropped.NativeDialogue);
            }
            while (SubtitleQueue.TryDequeue(out SubtitleCue cue))
            {
                cue.MarkDisplayed();
            }
            while (VoiceFailureQueue.TryDequeue(out _)) { }
            while (ReplyFinishedQueue.TryDequeue(out _)) { }
            _abandonRun = true;
        }

        private volatile bool _abandonRun;

        /// <summary>
        /// Hands a dropped utterance's cue back cancelled: pending entry cleared and
        /// thread released, but the line is neither shown nor voiced. Every path that
        /// discards an utterance must call this.
        /// </summary>
        private void AbandonNativeCue(Utterance utterance)
        {
            if (utterance?.NativeDialogue == null) return;
            utterance.NativeDialogue.Cancel();
            NativeDialogueQueue.Enqueue(utterance.NativeDialogue);
        }

        /// <summary>Signal that the warm-up batch is done (or timed out).</summary>
        public void SignalWarmUpComplete()
        {
            _warmUpComplete.Set();
        }

        /// <summary>
        /// Start the background worker thread and a separate warm-up thread.
        /// Must be called once after construction.
        /// </summary>
        public void Start()
        {
            // Start warm-up on its own short-lived thread. Kept in a field so Dispose
            // can wait for it - otherwise it may call Set() on a disposed event.
            _warmUpThread = new Thread(RunWarmUp)
            {
                IsBackground = true,
                Name = "LilithVoice.WarmUp"
            };
            _warmUpThread.Start();

            // Start the main processing thread.
            _workerThread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = "LilithVoice.Processor"
            };
            _workerThread.Start();
        }

        // ---- Warm-up -------------------------------------------------------

        private static readonly string[] WarmUpSentences = new[]
        {
            "ちょっと待って、今調整しているところだ。",
            "なるほど、面白い質問ですね。少し考えさせてください。",
            "うん……今ちょっと考え事をしてたの。",
        };

        private void RunWarmUp()
        {
            var stopwatch = Stopwatch.StartNew();
            int timeoutMs = VoiceConfig.WarmUpTimeoutSeconds * 1000;

            string[] sentences = string.IsNullOrWhiteSpace(VoiceConfig.WarmUpText)
                ? WarmUpSentences
                : new[] { VoiceConfig.WarmUpText };
            foreach (var sentence in sentences)
            {
                if (stopwatch.ElapsedMilliseconds > timeoutMs)
                {
                    LilithModPlugin.Logger.LogWarning(
                        "[Voice] Warm-up timed out before completing all sentences.");
                    break;
                }

                try
                {
                    // Synthesize but discard the audio – we only want to warm the model.
                    _ttsClient.SynthesizeAsync(sentence, VoiceConfig.TextLang, CancellationToken.None).GetAwaiter().GetResult();
                    // Proof the service is up, and it lands during Load() - early
                    // enough for the greeting, which the Update() probe never was.
                    VoiceServiceMonitor.NoteServiceAnswered();
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning(
                        $"[Voice] Warm-up sentence failed (continuing): {ex.Message}");
                    // Continue to the next sentence.
                }
            }

            LilithModPlugin.Logger.LogInfo("[Voice] Warm-up phase finished.");
            _warmUpComplete.Set();
        }

        // ---- Main processing loop -----------------------------------------

        /// <summary>
        /// Drains the queue one sentence at a time, overlapping synthesis with
        /// playback: the next sentence is synthesised BEFORE PlaySync blocks on the
        /// current one. Subtitles are enqueued immediately before their audio so the
        /// two cannot drift apart.
        /// </summary>
        private void ProcessLoop()
        {
            // Block until warm-up completes.
            _warmUpComplete.Wait();

            Utterance next = null;
            Task<byte[]> nextSynth = null;

            while (!_cts.IsCancellationRequested)
            {
                Utterance current = null;
                byte[] currentWav;

                try
                {
                    if (next != null)
                    {
                        // Already in flight since before the previous PlaySync.
                        current = next;
                        currentWav = nextSynth.GetAwaiter().GetResult();
                        next = null;
                        nextSynth = null;
                    }
                    else
                    {
                        if (!_queue.TryDequeue(out current))
                        {
                            WaitHandle.WaitAny(new[] { _queueAvailable, _cts.Token.WaitHandle });
                            continue;
                        }
                        // First sentence of a reply: nothing was pre-synthesised, so
                        // this one is paid for up front.
                        _abandonRun = false;
                        currentWav = _ttsClient.SynthesizeAsync(current.JaText, current.Language, _cts.Token)
                            .GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AbandonNativeCue(next);
                    next = null;
                    nextSynth = null;
                    if (current?.NativeDialogue != null)
                    {
                        NativeDialogueQueue.Enqueue(current.NativeDialogue);
                        LilithModPlugin.Logger.LogWarning(
                            $"[Voice] Game-line synthesis failed; showing text without voice: {ex.Message}");
                    }
                    else
                    {
                        ReportSynthesisFailure(ex);
                    }
                    continue;
                }

                // A new reply arrived while this one was being synthesised. A native
                // utterance abandoned here was in the worker's hands, not the queue,
                // so CancelCurrent could not route its cue - do it now or the
                // coordinator's pending entry leaks and the bubble stays suppressed.
                if (_abandonRun)
                {
                    _abandonRun = false;
                    AbandonNativeCue(current);
                    AbandonNativeCue(next);
                    next = null;
                    nextSynth = null;
                    continue;
                }

                // Start the next sentence BEFORE blocking on playback. This is the
                // whole point: synthesis of N+1 runs while N is being heard.
                if (_queue.TryDequeue(out next))
                {
                    var pending = next;
                    nextSynth = Task.Run(
                        () => _ttsClient.SynthesizeAsync(pending.JaText, pending.Language, _cts.Token)
                            .GetAwaiter().GetResult());
                    LilithModPlugin.Logger.LogInfo(
                        "[Voice] synth started for next sentence while current one plays.");
                }
                else
                {
                    next = null;
                    nextSynth = null;
                }

                // Audio is ready. Hand its subtitle to Unity and wait for the bubble
                // refresh before starting playback. This prevents early subtitles and
                // makes the text and voice begin on the same main-thread frame.
                SubtitleCue subtitleCue = null;
                if (current.NativeDialogue != null)
                {
                    NativeDialogueQueue.Enqueue(current.NativeDialogue);
                    current.NativeDialogue.WaitUntilDisplayed(_cts.Token);
                    // The coordinator declined to re-show this line - superseded by a
                    // newer one, or abandoned by a cancel. Its audio must not play
                    // under whatever is on the bubble now.
                    if (current.NativeDialogue.Cancelled)
                        continue;
                }
                if (!current.SuppressSubtitle && !string.IsNullOrEmpty(current.EnText))
                {
                    subtitleCue = new SubtitleCue(current.EnText);
                    SubtitleQueue.Enqueue(subtitleCue);
                    try
                    {
                        subtitleCue.WaitUntilDisplayed(_cts.Token);
                    }
                    finally
                    {
                        subtitleCue.Dispose();
                    }
                }

                // Cancellation may have removed the cue before Unity displayed it.
                // Do not play orphaned audio under the next reply. current's cue, if
                // any, already went through the coordinator above; only next's needs
                // routing.
                if (_abandonRun)
                {
                    _abandonRun = false;
                    AbandonNativeCue(next);
                    next = null;
                    nextSynth = null;
                    continue;
                }

                try
                {
                    SetPlaybackActive(true);
                    try
                    {
                        _voicePlayer.PlaySync(currentWav);
                    }
                    finally
                    {
                        SetPlaybackActive(false);
                    }
                    if (current.EndOfReply)
                        ReplyFinishedQueue.Enqueue(true);
                }
                catch (Exception playEx)
                {
                    LilithModPlugin.Logger.LogWarning(
                        $"[Voice] Playback error: {playEx.Message}");
                }
            }
        }

        private void SetPlaybackActive(bool active)
        {
            try
            {
                if (active)
                    File.WriteAllText(_playbackLockPath, "active");
                else if (File.Exists(_playbackLockPath))
                    File.Delete(_playbackLockPath);
            }
            catch { }
        }

        /// <summary>
        /// Abandons the rest of the reply and tells the main thread to show it in full.
        /// Repeated identical failures are collapsed, so a TTS service that is simply
        /// not running cannot bury the rest of the log.
        /// </summary>
        private void ReportSynthesisFailure(Exception ex)
        {
            while (_queue.TryDequeue(out _)) { }
            VoiceFailureQueue.Enqueue(true);

            string kind = ex.GetType().Name + ":" + ex.Message;
            if (kind != _lastFailure)
            {
                _lastFailure = kind;
                _suppressedFailures = 0;
                LilithModPlugin.Logger.LogWarning(
                    $"[Voice] Speech failed, continuing without voice: {ex.Message}");
            }
            else if (++_suppressedFailures % 20 == 0)
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Voice] Still failing ({_suppressedFailures} more): {ex.Message}");
            }
        }

        // ---- Shutdown -----------------------------------------------------

        public void Dispose()
        {
            SetPlaybackActive(false);
            _cts.Cancel();
            _queueAvailable.Set();
            _warmUpComplete.Set(); // unblock the processor if it's still waiting
            try
            {
                _workerThread?.Join(3000);
                // Join the warm-up thread too: it calls _warmUpComplete.Set() when it
                // finishes, and disposing the event out from under it would throw
                // ObjectDisposedException on a background thread - the same shape of
                // bug as the CancellationTokenSource one that made chat die silently.
                _warmUpThread?.Join(3000);
            }
            catch (Exception)
            {
                // Best effort.
            }
            _cts.Dispose();
            _queueAvailable.Dispose();
            _warmUpComplete.Dispose();
            _ttsClient.Dispose();
            _voicePlayer.Dispose();
        }
    }
}
