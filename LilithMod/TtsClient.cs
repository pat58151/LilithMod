using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
            var requestBody = new
            {
                text = text,
                text_lang = _cfg.TextLang,
                ref_audio_path = _cfg.RefAudioPath,
                prompt_text = _cfg.PromptText,
                prompt_lang = _cfg.PromptLang,
                media_type = "wav",
                streaming_mode = false,
                text_split_method = _cfg.TextSplitMethod,
                fragment_interval = _cfg.FragmentInterval,
            };

            string json = JsonConvert.SerializeObject(requestBody);

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

                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Wraps TTS-related exceptions so callers can catch them specifically.
    /// </summary>
    public class TtsException : Exception
    {
        public TtsException(string message) : base(message) { }
        public TtsException(string message, Exception inner) : base(message, inner) { }
    }
}
