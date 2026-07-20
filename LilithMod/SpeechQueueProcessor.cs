using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _workerThread;
        private Thread _warmUpThread;

        // Repeated identical failures are collapsed; see the catch in ProcessLoop.
        private string _lastFailure;
        private int _suppressedFailures;

        public SpeechQueueProcessor(TtsClient ttsClient, VoicePlayer voicePlayer)
        {
            _ttsClient = ttsClient ?? throw new ArgumentNullException(nameof(ttsClient));
            _voicePlayer = voicePlayer ?? throw new ArgumentNullException(nameof(voicePlayer));
        }

        /// <summary>
        /// Subtitles to show, in the order their audio plays. Written by the voice
        /// thread, drained by LlmChatController on the Unity main thread.
        /// </summary>
        public ConcurrentQueue<string> SubtitleQueue { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// Raised once when synthesis fails and the remaining sentences are abandoned,
        /// so the main thread can fall back to showing the whole reply. Kept separate
        /// from SubtitleQueue: a magic string in a queue of user-visible text is a
        /// defect waiting to be displayed.
        /// </summary>
        public ConcurrentQueue<bool> VoiceFailureQueue { get; } = new ConcurrentQueue<bool>();

        /// <summary>Thread-safe enqueue for the Unity main thread.</summary>
        public void Enqueue(Utterance utterance)
        {
            if (utterance == null || string.IsNullOrEmpty(utterance.JaText))
                return;
            _queue.Enqueue(utterance);
        }

        /// <summary>
        /// Convenience overload for the malformed-reply fallback, where there is no
        /// Japanese/English split and the text is simply spoken as-is.
        /// </summary>
        public void Enqueue(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            _queue.Enqueue(new Utterance { JaText = text, EnText = null });
        }

        /// <summary>
        /// Abandon whatever is still queued for the previous reply. The sentence
        /// already inside PlaySync is allowed to finish - cutting audio mid-word is
        /// worse than a short overlap, and cancellation is observed between sentences.
        /// </summary>
        public void CancelCurrent()
        {
            while (_queue.TryDequeue(out _)) { }
            while (SubtitleQueue.TryDequeue(out _)) { }
            while (VoiceFailureQueue.TryDequeue(out _)) { }
            _abandonRun = true;
        }

        private volatile bool _abandonRun;

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

            foreach (var sentence in WarmUpSentences)
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
                    _ttsClient.SynthesizeAsync(sentence, CancellationToken.None).GetAwaiter().GetResult();
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
        /// Drains the queue one sentence at a time, overlapping synthesis with playback.
        ///
        /// The overlap is structural rather than incidental: synthesis of the next
        /// sentence is started BEFORE PlaySync blocks on the current one, so it runs
        /// during playback. Each sentence is dequeued exactly once, and its subtitle is
        /// enqueued immediately before its audio, so subtitle and audio cannot drift
        /// apart by a sentence.
        /// </summary>
        private void ProcessLoop()
        {
            // Block until warm-up completes.
            _warmUpComplete.Wait();

            Utterance next = null;
            Task<byte[]> nextSynth = null;

            while (!_cts.IsCancellationRequested)
            {
                Utterance current;
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
                            Thread.Sleep(100);
                            continue;
                        }
                        // First sentence of a reply: nothing was pre-synthesised, so
                        // this one is paid for up front.
                        _abandonRun = false;
                        currentWav = _ttsClient.SynthesizeAsync(current.JaText, _cts.Token)
                            .GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    next = null;
                    nextSynth = null;
                    ReportSynthesisFailure(ex);
                    continue;
                }

                // A new reply arrived while this one was being synthesised.
                if (_abandonRun)
                {
                    _abandonRun = false;
                    next = null;
                    nextSynth = null;
                    continue;
                }

                // The subtitle belongs to the audio that is about to play.
                if (!string.IsNullOrEmpty(current.EnText))
                    SubtitleQueue.Enqueue(current.EnText);

                // Start the next sentence BEFORE blocking on playback. This is the
                // whole point: synthesis of N+1 runs while N is being heard.
                if (_queue.TryDequeue(out next))
                {
                    var pending = next;
                    nextSynth = Task.Run(
                        () => _ttsClient.SynthesizeAsync(pending.JaText, _cts.Token)
                            .GetAwaiter().GetResult());
                    LilithModPlugin.Logger.LogInfo(
                        "[Voice] synth started for next sentence while current one plays.");
                }
                else
                {
                    next = null;
                    nextSynth = null;
                }

                try
                {
                    _voicePlayer.PlaySync(currentWav);
                }
                catch (Exception playEx)
                {
                    LilithModPlugin.Logger.LogWarning(
                        $"[Voice] Playback error: {playEx.Message}");
                }
            }
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
            _cts.Cancel();
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
            _warmUpComplete.Dispose();
            _ttsClient.Dispose();
            _voicePlayer.Dispose();
        }
    }
}
