using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace LilithMod
{
    /// <summary>
    /// Owns the speech queue and a dedicated background thread that drains it.
    /// Blocks on a warm-up signal before processing real utterances.
    /// All synthesis and playback happen off the Unity main thread.
    /// </summary>
    public class SpeechQueueProcessor : IDisposable
    {
        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
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

        /// <summary>Thread-safe enqueue for the Unity main thread.</summary>
        public void Enqueue(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            _queue.Enqueue(text);
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

        private void ProcessLoop()
        {
            // Block until warm-up completes.
            _warmUpComplete.Wait();

            while (!_cts.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out string text))
                {
                    try
                    {
                        // Synthesize synchronously on this background thread.
                        byte[] wav = _ttsClient.SynthesizeAsync(text, _cts.Token)
                            .GetAwaiter().GetResult();

                        // Play blocks until audio finishes – serialises utterances.
                        _voicePlayer.PlaySync(wav);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A service that is simply not running fails on every reply.
                        // Log the first occurrence and then stay quiet, so a missing
                        // TTS service cannot bury the rest of the log.
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
                        // Continue – never break chat.
                    }
                }
                else
                {
                    // Idle – sleep briefly to avoid spinning.
                    Thread.Sleep(100);
                }
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
