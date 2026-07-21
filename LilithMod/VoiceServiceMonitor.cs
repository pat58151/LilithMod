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

        /// <summary>
        /// Whether the service has answered even once this session. "Not up yet" and
        /// "not installed" both read as unavailable, but only the first is worth
        /// waiting through - the synthesis model takes tens of seconds to load, and
        /// dialogue that fires in that window would otherwise use the game's own
        /// voice, which is Chinese.
        /// </summary>
        internal static bool EverAvailable { get; private set; }

        private float _nextProbe;
        private Task<bool> _probe;
        private bool _reported;

        /// <summary>
        /// Records that the service answered a real request, from whichever thread saw
        /// it. Availability was previously discovered only by the probe in Update(),
        /// which does not tick until the Unity player loop starts - and the EnterGame
        /// greeting fires on that same first frame. The probe lost that race every
        /// launch, so the opening line always kept the game's Chinese voice, however
        /// long the service had already been up. Warm-up finishes during Load(), well
        /// before any dialogue, and a completed synthesis is stronger evidence than a
        /// socket connect.
        /// </summary>
        internal static void NoteServiceAnswered()
        {
            IsAvailable = true;
            EverAvailable = true;

            bool preferred = LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
                             LilithModPlugin.CfgVoiceSynthesisPreferred.Value;
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

        private static async Task<bool> ProbeAsync()
        {
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
            bool changed = IsAvailable != available;
            IsAvailable = available;
            if (available) EverAvailable = true;
            bool preferred = LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
                             LilithModPlugin.CfgVoiceSynthesisPreferred.Value;
            bool effective = preferred && available;
            if (LilithModPlugin.CfgReplaceGameVoice.Value != effective)
                LilithModPlugin.CfgReplaceGameVoice.Value = effective;

            if (!_reported || changed)
            {
                _reported = true;
                if (available)
                    LilithModPlugin.Logger.LogInfo(preferred
                        ? "[Voice] Synthesis service available; saved preference restored."
                        : "[Voice] Synthesis service available; native voice remains selected.");
                else
                    LilithModPlugin.Logger.LogWarning(
                        "[Voice] Synthesis service unavailable; using native voice without changing preference.");
            }
        }
    }
}
