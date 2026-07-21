using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace LilithMod
{
    /// <summary>
    /// Plays raw WAV bytes synchronously using NAudio's <see cref="WaveOutEvent"/>.
    /// Safe to call from any thread; blocks until playback completes.
    /// </summary>
    public class VoicePlayer : IDisposable
    {
        private readonly object _gate = new object();
        private WaveOutEvent _currentOutput;

        /// <summary>
        /// Play the given WAV bytes and block until audio finishes.
        /// Returns only after the sound has fully played or an error occurs.
        /// </summary>
        public void PlaySync(byte[] wavBytes)
        {
            if (wavBytes == null || wavBytes.Length == 0)
                return;

            using (var ms = new MemoryStream(wavBytes))
            using (var reader = new WaveFileReader(ms))
            using (var output = new WaveOutEvent())
            {
                var done = new ManualResetEventSlim(false);
                Exception playbackError = null;

                output.PlaybackStopped += (sender, args) =>
                {
                    // NAudio reports device failures here rather than throwing.
                    playbackError = args.Exception;
                    done.Set();
                };

                output.Init(reader);
                lock (_gate) _currentOutput = output;
                try
                {
                    output.Play();

                    // Never wait unbounded: if PlaybackStopped is not raised - a device
                    // failure or a wedged driver - an infinite wait would park the voice
                    // thread forever and silently drop every later reply. Allow the clip's
                    // own duration plus a margin, then give up.
                    var limit = reader.TotalTime + TimeSpan.FromSeconds(10);
                    if (!done.Wait(limit))
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[Voice] Playback did not signal completion within {limit.TotalSeconds:F0}s; abandoning this clip.");
                    }
                    else if (playbackError != null)
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[Voice] Playback device error: {playbackError.Message}");
                    }
                }
                finally
                {
                    lock (_gate)
                        if (ReferenceEquals(_currentOutput, output)) _currentOutput = null;
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                try { _currentOutput?.Stop(); }
                catch { }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
