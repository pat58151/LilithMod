using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace LilithMod
{
    /// <summary>
    /// Starts the voice and speech services when nothing else has. The Startup
    /// shortcut is the better route - synthesis takes tens of seconds to load, so
    /// warm-before-launch decides whether her greeting is spoken at all. Without it
    /// the services never ran, which read as the voice being broken.
    /// </summary>
    internal static class ServiceBootstrap
    {
        private const string StartupShortcutName = "Lilith AI services.lnk";

        /// <summary>
        /// True when this process launched the services itself, meaning synthesis is
        /// loading its model right now rather than having been warm since login. The
        /// startup grace for native dialogue is longer in that case.
        /// </summary>
        internal static bool StartedServices { get; private set; }

        internal static void Run()
        {
            try
            {
                if (!LilithModPlugin.CfgAutoStartServices.Value) return;

                if (StartupShortcutInstalled())
                {
                    // Login already owns them. Starting a second copy here would
                    // fight the first over the microphone and the TTS port.
                    LilithModPlugin.Logger.LogInfo(
                        "[Services] Startup shortcut is installed; leaving the services to it.");
                    return;
                }

                if (VoiceServiceReachable())
                {
                    LilithModPlugin.Logger.LogInfo(
                        "[Services] Voice service is already answering; nothing to start.");
                    return;
                }

                string launcher = LauncherPath();
                if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
                {
                    LilithModPlugin.Logger.LogInfo(
                        "[Services] No launcher script found, so the services are not started " +
                        "automatically. Set Services/LauncherScript, or install the Startup " +
                        "shortcut, or start them by hand.");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + launcher + "\" -ServicesOnly",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                StartedServices = true;
                LilithModPlugin.Logger.LogInfo("[Services] Starting voice services: " + launcher);
            }
            catch (Exception ex)
            {
                // Never fatal: the mod is fully usable with the services started by
                // any other means, or not at all.
                LilithModPlugin.Logger.LogWarning("[Services] Could not start services: " + ex.Message);
            }
        }

        private static bool StartupShortcutInstalled()
        {
            try
            {
                if (LilithModPlugin.CfgIgnoreStartupShortcut != null &&
                    LilithModPlugin.CfgIgnoreStartupShortcut.Value)
                    return false;
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return !string.IsNullOrEmpty(startup) &&
                       File.Exists(Path.Combine(startup, StartupShortcutName));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A plain TCP probe, matching what VoiceServiceMonitor does. Answers "is
        /// something already listening" without waiting on a model to load.
        /// </summary>
        private static bool VoiceServiceReachable()
        {
            try
            {
                if (!Uri.TryCreate(VoiceConfig.Endpoint, UriKind.Absolute, out Uri endpoint))
                    return false;
                using (var client = new TcpClient())
                {
                    IAsyncResult connecting = client.BeginConnect(endpoint.Host, endpoint.Port, null, null);
                    if (!connecting.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500)))
                        return false;
                    client.EndConnect(connecting);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The configured launcher, or the one beside the voice runtime. Derived
        /// rather than hardcoded: the runtime can be installed anywhere, and an
        /// absolute path here would be right on one machine only.
        /// </summary>
        private static string LauncherPath()
        {
            string configured = LilithModPlugin.CfgServiceLauncher.Value;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().Trim('"');

            string runtime = VoiceSetup.Loaded ? VoiceSetup.RuntimePath : null;
            if (string.IsNullOrWhiteSpace(runtime)) return null;

            // voice-runtime sits beside the runtime\ folder in the project layout.
            string project = Path.GetDirectoryName(runtime);
            if (string.IsNullOrEmpty(project)) return null;
            return Path.Combine(project, "runtime", "start-lilith.ps1");
        }
    }
}
