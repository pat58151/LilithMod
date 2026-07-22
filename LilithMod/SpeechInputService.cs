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
        private static string _wakeWordModelPath;
        private static string _wakeWordFlagPath;
        private static float _nextCheck;
        private static bool _available;
        private static bool _wakeWordModel;

        internal static bool IsAvailable => _available;

        /// <summary>
        /// Whether the wake word model is on disk. Separate from the listener: the
        /// listener runs fine without it and push-to-talk still works, but nothing
        /// answers to her name, so the setting would be a switch wired to nothing.
        /// </summary>
        internal static bool WakeWordModelAvailable => _wakeWordModel;

        internal static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _heartbeatPath = Path.Combine(root, "push-to-talk.alive");
            _wakeWordModelPath = Path.Combine(root, "speech-setup", "lilith.onnx");
            _wakeWordFlagPath = Path.Combine(root, "speech-setup", "wake-word.on");
            try { _wakeWordModel = File.Exists(_wakeWordModelPath); }
            catch (IOException) { _wakeWordModel = false; }
            SyncWakeWordFlag();
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

            // Re-stat rather than cache once: the model can arrive from an install
            // run after the game is already up.
            bool model = false;
            try { model = _wakeWordModelPath != null && File.Exists(_wakeWordModelPath); }
            catch (IOException) { model = false; }

            if (model != _wakeWordModel)
            {
                _wakeWordModel = model;
                LilithModPlugin.Logger.LogInfo(
                    model
                        ? "[Speech] Wake word model found; the setting is live."
                        : "[Speech] No wake word model; the setting stays greyed.");
            }

            SyncWakeWordFlag();
        }

        /// <summary>
        /// Publishes the live preference to the external listener. The model check is
        /// repeated here so a stale enabled preference can never arm a missing model.
        /// </summary>
        internal static void SyncWakeWordFlag()
        {
            if (string.IsNullOrEmpty(_wakeWordFlagPath)) return;
            bool enabled = _wakeWordModel && LilithModPlugin.CfgWakeWord != null &&
                           LilithModPlugin.CfgWakeWord.Value;
            try
            {
                bool exists = File.Exists(_wakeWordFlagPath);
                if (enabled)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_wakeWordFlagPath) ?? ".");
                    // Refresh this heartbeat during the normal availability poll.
                    // The listener rejects a stale flag after a game crash.
                    File.WriteAllText(_wakeWordFlagPath, DateTime.UtcNow.Ticks.ToString());
                }
                else if (!enabled && exists)
                {
                    File.Delete(_wakeWordFlagPath);
                }
            }
            catch (IOException ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[Speech] Could not update wake-word flag: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[Speech] Could not update wake-word flag: " + ex.Message);
            }
        }

        internal static void Shutdown()
        {
            if (string.IsNullOrEmpty(_wakeWordFlagPath)) return;
            try
            {
                if (File.Exists(_wakeWordFlagPath)) File.Delete(_wakeWordFlagPath);
            }
            catch { }
        }
    }
}
