using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LilithMod
{
    /// <summary>
    /// Talks to the local GPT-SoVITS HTTP service at the configured endpoint.
    /// Uses a single static HttpClient; all methods are thread-safe.
    /// </summary>
    public class TtsClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly VoiceConfigData _cfg;

        /// <summary>Snapshot of VoiceConfig values captured at construction time.</summary>
        private sealed class VoiceConfigData
        {
            public string Endpoint;
            public string RefAudioPath;
            public string PromptText;
            public string TextLang;
            public string PromptLang;
            public float FragmentInterval;
            public string TextSplitMethod;
            public string CacheIdentity;
        }

        public TtsClient()
        {
            _cfg = new VoiceConfigData
            {
                Endpoint = VoiceConfig.Endpoint,
                RefAudioPath = VoiceConfig.RefAudioPath,
                PromptText = VoiceConfig.PromptText,
                TextLang = VoiceConfig.TextLang,
                PromptLang = VoiceConfig.PromptLang,
                FragmentInterval = VoiceConfig.FragmentInterval,
                TextSplitMethod = VoiceConfig.TextSplitMethod,
                CacheIdentity = VoiceConfig.CacheIdentity,
            };

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(VoiceConfig.TimeoutSeconds);
        }

        /// <summary>
        /// POST the text to the TTS endpoint and return the synthesised WAV bytes.
        /// Throws on any failure (network, timeout, HTTP error, unexpected content type).
        /// </summary>
        public async Task<byte[]> SynthesizeAsync(string text, CancellationToken token = default)
        {
            return await SynthesizeAsync(text, null, token);
        }

        public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken token = default)
        {
            string effectiveLanguage = string.IsNullOrEmpty(language) ? _cfg.TextLang : language;
            VoiceModelSwitcher.EnsureLanguage(effectiveLanguage, token);
            string cachePath = CachePath(text, effectiveLanguage);
            if (File.Exists(cachePath))
                return File.ReadAllBytes(cachePath);

            var requestBody = new
            {
                text = text,
                text_lang = effectiveLanguage,
                ref_audio_path = _cfg.RefAudioPath,
                prompt_text = _cfg.PromptText,
                prompt_lang = _cfg.PromptLang,
                media_type = "wav",
                streaming_mode = false,
                // MIOpen's batched path falls back to undersized workspaces on
                // this ROCm stack. Serial inference is 4-10x faster here.
                parallel_infer = false,
                text_split_method = _cfg.TextSplitMethod,
                fragment_interval = _cfg.FragmentInterval,
            };

            string json = JsonConvert.SerializeObject(requestBody);
            // Cache hits returned above, so this only ever times real synthesis.
            var timer = System.Diagnostics.Stopwatch.StartNew();

            using (var request = new HttpRequestMessage(HttpMethod.Post, _cfg.Endpoint))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, token))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        string message = errorBody;
                        try
                        {
                            var errJson = JObject.Parse(errorBody);
                            var msg = errJson["message"]?.ToString();
                            if (!string.IsNullOrEmpty(msg))
                                message = msg;
                        }
                        catch
                        {
                            // Use raw body as the message.
                        }
                        throw new TtsException($"TTS service returned {(int)response.StatusCode}: {message}");
                    }

                    byte[] audio = await response.Content.ReadAsByteArrayAsync();
                    timer.Stop();
                    ReportLatency(text, effectiveLanguage, audio.Length, timer.ElapsedMilliseconds);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                        File.WriteAllBytes(cachePath, audio);
                    }
                    catch (IOException) { }
                    return audio;
                }
            }
        }

        /// <summary>
        /// A warm sentence synthesises in roughly 2-3 s. Past this it is slow
        /// enough to hear, and the 2026-07-21 regression sat at 19-30 s.
        /// </summary>
        private const long SlowSynthesisMs = 8000;

        /// <summary>
        /// Records how long synthesis took. The slowdown that prompted this was
        /// invisible until bad enough to hear, hours after the conditions causing it
        /// were gone, and was never reproduced. A line per synthesis timestamps a
        /// recurrence while it happens instead of reconstructing it afterwards.
        /// </summary>
        private static void ReportLatency(string text, string language, int bytes, long ms)
        {
            string detail =
                $"{ms} ms for {text?.Length ?? 0} chars ({language}), {bytes / 1024} KB audio";
            if (ms >= SlowSynthesisMs)
                LilithModPlugin.Logger.LogWarning(
                    $"[Voice] Slow synthesis: {detail}. Expected 2-3 s; see PROBLEM.md.");
            else
                LilithModPlugin.Logger.LogInfo($"[Voice] Synthesized in {detail}.");
        }

        private string CachePath(string text, string language)
        {
            string modelIdentity = (_cfg.CacheIdentity ?? language) + "\n";
            string material = modelIdentity + language + "\n" + text + "\n" + _cfg.RefAudioPath + "\n" + _cfg.PromptText;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            var name = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash) name.Append(value.ToString("x2"));
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            return Path.Combine(root, "voice-cache", language, name + ".wav");
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>Wraps TTS-related exceptions so callers can catch them specifically.</summary>
    public class TtsException : Exception
    {
        public TtsException(string message) : base(message) { }
        public TtsException(string message, Exception inner) : base(message, inner) { }
    }
}
