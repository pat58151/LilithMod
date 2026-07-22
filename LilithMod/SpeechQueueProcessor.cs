using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LilithMod
{
    /// <summary>Runs queued synthesis and playback outside Unity's main thread.</summary>
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

        /// <summary>Audio-ordered subtitles for Unity's main thread.</summary>
        public ConcurrentQueue<SubtitleCue> SubtitleQueue { get; } = new ConcurrentQueue<SubtitleCue>();
        internal ConcurrentQueue<NativeDialogueCue> NativeDialogueQueue { get; } =
            new ConcurrentQueue<NativeDialogueCue>();

        /// <summary>Signals that the full reply should be shown after voice failure.</summary>
        public ConcurrentQueue<bool> VoiceFailureQueue { get; } = new ConcurrentQueue<bool>();
        public ConcurrentQueue<bool> ReplyFinishedQueue { get; } = new ConcurrentQueue<bool>();

        /// <summary>Checks whether a line is cached.</summary>
        internal bool IsCached(string text, string language) => _ttsClient.IsCached(text, language);

        /// <summary>Thread-safe enqueue for the Unity main thread.</summary>
        public void Enqueue(Utterance utterance)
        {
            if (utterance == null || (!utterance.CompletionOnly && string.IsNullOrEmpty(utterance.JaText)))
                return;
            _queue.Enqueue(utterance);
            _queueAvailable.Set();
        }

        /// <summary>Ends a streamed reply after all queued speech.</summary>
        internal void CompleteStreamedReply()
        {
            Enqueue(new Utterance { CompletionOnly = true });
        }

        /// <summary>Queues plain text when no bilingual pair is available.</summary>
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

        /// <summary>Cancels queued speech between sentences.</summary>
        public void CancelCurrent(bool stopPlayback = false)
        {
            if (stopPlayback) _voicePlayer.Stop();
            // Route cancelled native cues so the coordinator can release them.
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
        internal bool PlaybackActive { get; private set; }

        /// <summary>Returns a cancelled native cue to the coordinator.</summary>
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

        /// <summary>Starts the worker and warm-up threads.</summary>
        public void Start()
        {
            // Keep the warm-up thread so Dispose can join it.
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
                    // Warm-up success also confirms service availability.
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

        /// <summary>Overlaps synthesis with playback while preserving subtitle order.</summary>
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
                        next = null;
                        if (current.CompletionOnly)
                        {
                            nextSynth = null;
                            if (_abandonRun) _abandonRun = false;
                            else ReplyFinishedQueue.Enqueue(true);
                            continue;
                        }
                        currentWav = nextSynth.GetAwaiter().GetResult();
                        nextSynth = null;
                    }
                    else
                    {
                        if (!_queue.TryDequeue(out current))
                        {
                            WaitHandle.WaitAny(new[] { _queueAvailable, _cts.Token.WaitHandle });
                            continue;
                        }
                        if (current.CompletionOnly)
                        {
                            if (_abandonRun) _abandonRun = false;
                            else ReplyFinishedQueue.Enqueue(true);
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

                // Return an in-flight native cue when a newer reply cancels it.
                if (_abandonRun)
                {
                    _abandonRun = false;
                    AbandonNativeCue(current);
                    AbandonNativeCue(next);
                    next = null;
                    nextSynth = null;
                    continue;
                }

                // Synthesize the next sentence during current playback.
                if (_queue.TryDequeue(out next))
                {
                    if (next.CompletionOnly)
                    {
                        nextSynth = null;
                    }
                    else
                    {
                        var pending = next;
                        nextSynth = Task.Run(
                            () => _ttsClient.SynthesizeAsync(pending.JaText, pending.Language, _cts.Token)
                                .GetAwaiter().GetResult());
                        LilithModPlugin.Logger.LogInfo(
                            "[Voice] synth started for next sentence while current one plays.");
                    }
                }
                else
                {
                    next = null;
                    nextSynth = null;
                }

                // Wait for Unity to display the subtitle before playback.
                SubtitleCue subtitleCue = null;
                if (current.NativeDialogue != null)
                {
                    NativeDialogueQueue.Enqueue(current.NativeDialogue);
                    current.NativeDialogue.WaitUntilDisplayed(_cts.Token);
                    // Never play audio for a cancelled or superseded line.
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

                // Return the prefetched cue if cancellation removed it from display.
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
            PlaybackActive = active;
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
