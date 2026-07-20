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

        private float _nextProbe;
        private Task<bool> _probe;
        private bool _reported;

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
