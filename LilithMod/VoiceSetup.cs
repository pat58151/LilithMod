using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LilithMod
{
    internal sealed class VoiceProfile
    {
        public string Language;
        public string CacheIdentity;
        public string GptWeights;
        public string SovitsWeights;
        public string RefAudioPath;
        public string PromptText;
        public string PromptLanguage;
        public string WarmUpText;
    }

    internal static class VoiceSetup
    {
        private static readonly Dictionary<string, VoiceProfile> Profiles =
            new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);

        internal static bool Loaded { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static bool ReplaceGameVoice { get; private set; }
        internal static string SpokenLanguage { get; private set; } = "ja";
        internal static string SubtitleLanguage { get; private set; } = "en";
        internal static string Endpoint { get; private set; }
        internal static string RuntimePath { get; private set; }
        internal static string ServerConfig { get; private set; }
        internal static string FolderPath => Path.Combine(PluginPath(), "voice-setup");
        internal static string ConfigPath => Path.Combine(FolderPath, "voice-config.ini");

        internal static void Load()
        {
            EnsureFiles();
            if (!File.Exists(ConfigPath)) return;

            var values = ParseIni(ConfigPath);
            Enabled = ReadBool(values, "voice", "enabled", true);
            ReplaceGameVoice = ReadBool(values, "voice", "replacegamevoice", true);
            SpokenLanguage = NormalizeLanguage(Read(values, "voice", "spokenlanguage", "ja"));
            SubtitleLanguage = NormalizeLanguage(Read(values, "voice", "subtitlelanguage", "en"));
            Endpoint = Read(values, "voice", "endpoint", "http://127.0.0.1:9880/tts");
            // Empty rather than a developer machine's path. There is no correct
            // default for where someone installed the voice runtime, and a wrong
            // absolute path fails less clearly than an unset one: unset is reported
            // as "complete voice-config.ini", which is the actual next step.
            RuntimePath = ResolvePath(Read(values, "voice", "runtimepath", string.Empty));
            ServerConfig = ResolvePath(Read(values, "voice", "serverconfig", string.Empty));

            Profiles.Clear();
            foreach (string language in new[] { "ja", "en", "zh" })
            {
                string section = "profile." + language;
                Profiles[language] = new VoiceProfile
                {
                    Language = language,
                    CacheIdentity = Read(values, section, "cacheidentity", language + "-custom-v1"),
                    GptWeights = ResolvePath(Read(values, section, "gptweights", string.Empty)),
                    SovitsWeights = ResolvePath(Read(values, section, "sovitsweights", string.Empty)),
                    RefAudioPath = ResolvePath(Read(values, section, "refaudiopath", string.Empty)),
                    PromptText = Read(values, section, "prompttext", string.Empty),
                    PromptLanguage = NormalizeLanguage(Read(values, section, "promptlanguage", language)),
                    WarmUpText = Read(values, section, "warmuptext", DefaultWarmUp(language))
                };
            }
            Loaded = true;
        }

        internal static VoiceProfile Profile(string language = null)
        {
            language = NormalizeLanguage(language ?? SpokenLanguage);
            return Profiles.TryGetValue(language, out VoiceProfile profile) ? profile : null;
        }

        internal static void EnsureFiles()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                string example = Path.Combine(FolderPath, "voice-config.example.ini");
                if (!File.Exists(ConfigPath) && File.Exists(example))
                    File.Copy(example, ConfigPath, false);
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger?.LogWarning("[VoiceSetup] Could not prepare setup folder: " + ex.Message);
            }
        }

        internal static string NormalizeLanguage(string language)
        {
            if (!string.IsNullOrEmpty(language) && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (!string.IsNullOrEmpty(language) && language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            return "ja";
        }

        private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string section = string.Empty;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                if (!result.TryGetValue(section, out Dictionary<string, string> entries))
                {
                    entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[section] = entries;
                }
                entries[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            return result;
        }

        private static string Read(Dictionary<string, Dictionary<string, string>> values,
            string section, string key, string fallback)
        {
            return values.TryGetValue(section, out Dictionary<string, string> entries) &&
                   entries.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static bool ReadBool(Dictionary<string, Dictionary<string, string>> values,
            string section, string key, bool fallback)
        {
            return bool.TryParse(Read(values, section, key, fallback.ToString()), out bool value)
                ? value
                : fallback;
        }

        private static string ResolvePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(FolderPath, value));
        }

        private static string PluginPath()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        }

        private static string DefaultWarmUp(string language)
        {
            if (language == "zh") return "嗯……莉莉丝一直都在这里哦。";
            if (language == "en") return "Mm... Lilith is right here.";
            return "うん……リリスはずっとここにいるわ。";
        }
    }
}
