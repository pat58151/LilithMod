using System;
using System.IO;
using System.Reflection;

namespace LilithMod
{
    /// <summary>
    /// Whether the external push-to-talk listener is running. It touches a heartbeat
    /// file every couple of seconds; stale means the process is gone. Checked rather
    /// than assumed: without it the key still opens the bar, shows "Listening~", and
    /// waits forever for a transcript nobody is producing.
    /// </summary>
    internal static class SpeechInputService
    {
        // Generous next to the 2 s touch interval: a decode can briefly stall the
        // listener's loop, and flickering the setting would be worse than
        // answering late.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(12);

        private static string _heartbeatPath;
        private static float _nextCheck;
        private static bool _available;

        internal static bool IsAvailable => _available;

        internal static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _heartbeatPath = Path.Combine(root, "push-to-talk.alive");
        }

        /// <summary>Cheap enough to call every frame; the stat is throttled.</summary>
        internal static void Refresh(float unscaledTime)
        {
            if (_heartbeatPath == null || unscaledTime < _nextCheck) return;
            _nextCheck = unscaledTime + 2f;

            bool available = false;
            try
            {
                var info = new FileInfo(_heartbeatPath);
                available = info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < StaleAfter;
            }
            catch (IOException)
            {
                available = false;
            }

            if (available != _available)
            {
                _available = available;
                LilithModPlugin.Logger.LogInfo(
                    available
                        ? "[Speech] Listener detected; push-to-talk enabled."
                        : "[Speech] Listener not responding; push-to-talk disabled.");
            }
        }
    }
}
