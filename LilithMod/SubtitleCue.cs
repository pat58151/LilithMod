using System;
using System.Threading;

namespace LilithMod
{
    /// <summary>
    /// Carries an audio-ready subtitle to Unity's main thread and lets the voice
    /// thread wait until the bubble has actually been refreshed.
    /// </summary>
    public sealed class SubtitleCue : IDisposable
    {
        private readonly ManualResetEventSlim _displayed = new ManualResetEventSlim(false);

        public SubtitleCue(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public void MarkDisplayed()
        {
            _displayed.Set();
        }

        public void WaitUntilDisplayed(CancellationToken token)
        {
            _displayed.Wait(token);
        }

        public void Dispose()
        {
            _displayed.Dispose();
        }
    }
}
