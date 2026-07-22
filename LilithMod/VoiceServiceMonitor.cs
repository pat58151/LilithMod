using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;

namespace LilithMod
{
    public sealed class VoiceServiceMonitor : MonoBehaviour
    {
        public VoiceServiceMonitor(IntPtr ptr) : base(ptr) { }

        internal static bool IsAvailable { get; private set; }

        /// <summary>Whether the background probe has resolved the startup state.</summary>
        internal static bool AvailabilityKnown { get; private set; }

        /// <summary>
        /// Whether the service has answered even once this session. "Not up yet" and
        /// "not installed" both read as unavailable, but only the first is worth
        /// waiting through - dialogue in that window would use the game's Chinese
        /// voice. Usually set by NoteServiceAnswered during Load(); the probe below
        /// only establishes it on a genuine cold start.
        /// </summary>
        internal static bool EverAvailable { get; private set; }

        private float _nextProbe;
        private Task<bool> _probe;
        private bool _reported;

        /// <summary>
        /// Records that the service answered a real request, from whichever thread saw
        /// it. Availability used to come only from the probe in Update(), which cannot
        /// tick before the first frame - when the EnterGame greeting fires. The probe
        /// lost that race every launch, so the opening line kept the game's Chinese
        /// voice however long the service had been up. Warm-up finishes during Load(),
        /// and a completed synthesis beats a socket connect as evidence.
        /// </summary>
        internal static void NoteServiceAnswered()
        {
            if (LilithModPlugin.CfgForceSynthesisUnavailable != null &&
                LilithModPlugin.CfgForceSynthesisUnavailable.Value)
                return;
            bool first = !EverAvailable;
            IsAvailable = true;
            EverAvailable = true;

            bool preferred = LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
                             LilithModPlugin.CfgVoiceSynthesisPreferred.Value;

            // Logged where it happens. The probe's own "service available" message
            // lands hundreds of lines later, after the first dialogue, and reading
            // that as the moment availability was established is what sent three
            // separate diagnoses of the Chinese greeting down the wrong path.
            if (first)
                LilithModPlugin.Logger.LogInfo(
                    "[Voice] Synthesis answered a real request; available from here.");
            // Compared before assigning: the setter persists the file, and this runs on
            // the warm-up thread.
            if (preferred && LilithModPlugin.CfgReplaceGameVoice != null &&
                !LilithModPlugin.CfgReplaceGameVoice.Value)
                LilithModPlugin.CfgReplaceGameVoice.Value = true;
        }

        private void Update()
        {
            if (_probe != null && _probe.IsCompleted)
            {
                bool available = false;
                try { available = _probe.Result; }
                catch { }
                _probe = null;
                ApplyAvailability(available);
            }

            if (Time.unscaledTime < _nextProbe || _probe != null) return;
            _nextProbe = Time.unscaledTime + 2f;
            _probe = Task.Run(ProbeAsync);
        }

        private void OnApplicationQuit()
        {
            ServiceBootstrap.Stop();
        }

        private static async Task<bool> ProbeAsync()
        {
            if (LilithModPlugin.CfgForceSynthesisUnavailable != null &&
                LilithModPlugin.CfgForceSynthesisUnavailable.Value)
                return false;
            if (!VoiceConfig.Enabled || LilithModPlugin.VoiceProcessor == null ||
                !Uri.TryCreate(VoiceConfig.Endpoint, UriKind.Absolute, out Uri endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                return false;

            try
            {
                using (var client = new TcpClient())
                {
                    Task connect = client.ConnectAsync(endpoint.Host, endpoint.Port);
                    Task done = await Task.WhenAny(connect, Task.Delay(1000)).ConfigureAwait(false);
                    return done == connect && client.Connected;
                }
            }
            catch { return false; }
        }

        private void ApplyAvailability(bool available)
        {
            bool wasAvailable = IsAvailable;
            bool changed = IsAvailable != available;
            IsAvailable = available;
            AvailabilityKnown = true;
            if (available) EverAvailable = true;
            bool preferred = LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
                             LilithModPlugin.CfgVoiceSynthesisPreferred.Value;
            bool effective = preferred && available;
            if (LilithModPlugin.CfgReplaceGameVoice.Value != effective)
                LilithModPlugin.CfgReplaceGameVoice.Value = effective;
            if (wasAvailable && !available)
                LlmChatController.StopSynthPlaybackForNativeVoice();
            if (!_reported || changed)
            {
                _reported = true;
                if (available)
                    LilithModPlugin.Logger.LogInfo(preferred
                        ? "[Voice] Synthesis service available; saved preference restored."
                        : "[Voice] Synthesis service available; native voice remains selected.");
                else
                    LilithModPlugin.Logger.LogWarning(
                        preferred
                            ? "[Voice] Synthesis service unavailable; keeping synthesis selected and speech silent until it returns."
                            : "[Voice] Synthesis service unavailable; native Chinese remains selected.");
            }
        }
    }
}
