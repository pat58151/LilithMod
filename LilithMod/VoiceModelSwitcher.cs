using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LilithMod
{
    internal static class VoiceModelSwitcher
    {
        private static readonly object Sync = new object();
        private static readonly ManualResetEventSlim Ready = new ManualResetEventSlim(true);
        private static string _requestedLanguage;
        private static bool _workerRunning;

        internal static void Request(string language)
        {
            language = Normalize(language);
            lock (Sync)
            {
                _requestedLanguage = language;
                if (StateIdentity() == DesiredIdentity(language) && !_workerRunning)
                {
                    Ready.Set();
                    return;
                }

                Ready.Reset();
                if (_workerRunning) return;
                _workerRunning = true;
                Task.Run((Func<Task>)RunSwitchLoopAsync);
            }
        }

        /// <summary>
        /// Whether the running service is already on this language, so a cached
        /// line can be played without contacting it. Cache files are keyed by
        /// language, but the weights still have to match or she would speak the
        /// cached audio in the wrong voice.
        /// </summary>
        internal static bool LanguageIsCurrent(string language)
        {
            return StateIdentity() == DesiredIdentity(Normalize(language));
        }

        internal static void EnsureLanguage(string language, CancellationToken token)
        {
            language = Normalize(language);
            string desired = DesiredIdentity(language);
            if (StateIdentity() == desired) return;

            Request(language);
            var timer = Stopwatch.StartNew();
            int timeoutMs = Math.Max(30, VoiceConfig.WarmUpTimeoutSeconds) * 1000;
            while (StateIdentity() != desired)
            {
                token.ThrowIfCancellationRequested();
                if (timer.ElapsedMilliseconds >= timeoutMs)
                    throw new TtsException($"Timed out switching GPT-SoVITS to {language}.");
                token.WaitHandle.WaitOne(100);
            }
        }

        private static async Task RunSwitchLoopAsync()
        {
            try
            {
                while (true)
                {
                    string language;
                    lock (Sync) language = _requestedLanguage;
                    await ApplyModelAsync(language);

                    lock (Sync)
                    {
                        if (_requestedLanguage != language) continue;
                        _workerRunning = false;
                        Ready.Set();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    _workerRunning = false;
                    Ready.Set();
                }
                LilithModPlugin.Logger.LogWarning($"[Voice] GPT-SoVITS model switch failed: {ex.Message}");
            }
        }

        private static async Task ApplyModelAsync(string language)
        {
            VoiceProfile profile = VoiceSetup.Profile(language);
            string gpt = profile?.GptWeights;
            string sovits = profile?.SovitsWeights;
            if (!File.Exists(gpt) || !File.Exists(sovits))
                throw new FileNotFoundException($"Voice weights for {language} are missing.");

            string endpoint = VoiceConfig.Endpoint ?? "http://127.0.0.1:9880/tts";
            var endpointUri = new Uri(endpoint);
            string origin = endpointUri.GetLeftPart(UriPartial.Authority);
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) })
            {
                using (var response = await http.GetAsync(
                    origin + "/set_gpt_weights?weights_path=" + Uri.EscapeDataString(gpt)))
                    response.EnsureSuccessStatusCode();
                using (var response = await http.GetAsync(
                    origin + "/set_sovits_weights?weights_path=" + Uri.EscapeDataString(sovits)))
                    response.EnsureSuccessStatusCode();

                string warmText = profile?.WarmUpText;
                if (string.IsNullOrWhiteSpace(warmText))
                    warmText = language == "zh" ? "嗯……莉莉丝一直都在这里哦。" :
                        language == "en" ? "Mm... Lilith is right here." :
                        "うん……リリスはずっとここにいるわ。";
                var body = new
                {
                    text = warmText,
                    text_lang = language,
                    ref_audio_path = profile?.RefAudioPath ?? VoiceConfig.RefAudioPath,
                    prompt_text = profile?.PromptText ?? VoiceConfig.PromptText,
                    prompt_lang = profile?.PromptLanguage ?? VoiceConfig.PromptLang,
                    media_type = "wav",
                    streaming_mode = false,
                    parallel_infer = false,
                    text_split_method = VoiceConfig.TextSplitMethod,
                    fragment_interval = VoiceConfig.FragmentInterval,
                };
                using (var request = new HttpRequestMessage(HttpMethod.Post, origin + "/tts"))
                {
                    request.Content = new StringContent(
                        JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request))
                        response.EnsureSuccessStatusCode();
                }
            }

            string statePath = StatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            File.WriteAllText(statePath, DesiredIdentity(language), new UTF8Encoding(false));
            LilithModPlugin.Logger.LogInfo($"[Voice] GPT-SoVITS is ready with the {language} model.");
        }

        private static string StateIdentity()
        {
            try
            {
                string path = StatePath();
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string StatePath()
        {
            string root = Path.GetDirectoryName(typeof(VoiceModelSwitcher).Assembly.Location) ?? ".";
            return Path.Combine(root, "tts-language.txt");
        }

        private static string Normalize(string language)
        {
            return VoiceSetup.NormalizeLanguage(language);
        }

        private static string DesiredIdentity(string language)
        {
            VoiceProfile profile = VoiceSetup.Profile(language);
            return profile?.CacheIdentity ?? Normalize(language);
        }
    }
}
