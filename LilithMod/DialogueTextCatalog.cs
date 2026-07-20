using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace LilithMod
{
    internal static class DialogueTextCatalog
    {
        private static readonly object Sync = new object();
        private static Dictionary<int, string> _japanese;
        private static Dictionary<int, string> _chinese;
        private static readonly Dictionary<string, int> SoundIds =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryGet(int lineId, string language, out string text)
        {
            bool japanese = language != null &&
                language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
            Dictionary<int, string> catalog;
            lock (Sync)
            {
                if (japanese)
                    catalog = _japanese ?? (_japanese = Load("LilithMod.Dialogue.ja.tsv"));
                else
                    catalog = _chinese ?? (_chinese = Load("LilithMod.Dialogue.zh.tsv"));
            }
            return catalog.TryGetValue(lineId, out text) && !string.IsNullOrWhiteSpace(text);
        }

        internal static bool TryGetBySoundId(string soundId, string language, out int lineId, out string text)
        {
            lineId = 0;
            text = null;
            if (string.IsNullOrWhiteSpace(soundId)) return false;
            lock (Sync)
            {
                if (_japanese == null) _japanese = Load("LilithMod.Dialogue.ja.tsv");
                if (_chinese == null) _chinese = Load("LilithMod.Dialogue.zh.tsv");
                string key = NormalizeSoundId(soundId);
                if (!SoundIds.TryGetValue(soundId, out lineId) &&
                    !SoundIds.TryGetValue(key, out lineId))
                    return false;
            }
            return TryGet(lineId, language, out text);
        }

        private static Dictionary<int, string> Load(string resourceName)
        {
            var result = new Dictionary<int, string>();
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException($"Embedded dialogue catalog is missing: {resourceName}");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int firstTab = line.IndexOf('\t');
                        if (firstTab <= 0 || !int.TryParse(line.Substring(0, firstTab), out int lineId))
                            continue;
                        int secondTab = line.IndexOf('\t', firstTab + 1);
                        if (secondTab < 0 || secondTab + 1 >= line.Length)
                            continue;
                        string soundId = line.Substring(firstTab + 1, secondTab - firstTab - 1).Trim();
                        string text = line.Substring(secondTab + 1).Trim();
                        if (text.Length > 0)
                            result[lineId] = text;
                        if (soundId.Length > 0)
                        {
                            SoundIds[soundId] = lineId;
                            SoundIds[NormalizeSoundId(soundId)] = lineId;
                        }
                    }
                }
            }
            LilithModPlugin.Logger.LogInfo($"[Voice] Loaded {result.Count} lines from {resourceName}.");
            return result;
        }

        private static string NormalizeSoundId(string soundId)
        {
            string value = soundId.Replace('\\', '/');
            int slash = value.LastIndexOf('/');
            if (slash >= 0) value = value.Substring(slash + 1);
            int dot = value.LastIndexOf('.');
            if (dot > 0) value = value.Substring(0, dot);
            return value;
        }
    }
}
