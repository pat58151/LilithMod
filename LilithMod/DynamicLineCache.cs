using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace LilithMod
{
    /// <summary>
    /// Japanese for the dialogue the game builds at runtime.
    ///
    /// Those nodes carry lineId 0 and text already localised to the UI language, so
    /// the catalogue cannot translate them - there is no id to look up. Speaking that
    /// text directly made her read English aloud in a Japanese voice; keeping the
    /// original voice left them silent, because the game has no clip for them either.
    ///
    /// So the Japanese is fetched once and kept. A line is silent the first time it
    /// appears and spoken every time after, which is quick to converge because these
    /// are the reactions that repeat most - touch, drag, refusal.
    ///
    /// Translation is deliberately never awaited by the dialogue path: a line held
    /// while an API call runs would stall the bubble behind it.
    /// </summary>
    internal static class DynamicLineCache
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _japanese;
        private static readonly HashSet<string> InFlight = new HashSet<string>();

        private static string CachePath =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "dynamic-lines.ja.json");

        internal static bool TryGet(string source, out string japanese)
        {
            japanese = null;
            if (string.IsNullOrWhiteSpace(source)) return false;

            lock (Gate)
            {
                Load();
                return _japanese.TryGetValue(source.Trim(), out japanese) &&
                       !string.IsNullOrWhiteSpace(japanese);
            }
        }

        /// <summary>
        /// Fetches the Japanese in the background if it is not already known. Returns
        /// immediately; the result is only visible to a later occurrence of the line.
        /// </summary>
        internal static void RequestTranslation(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            string key = source.Trim();

            lock (Gate)
            {
                Load();
                // Already known, or a request for it is already running. Without the
                // second check a line repeating every few seconds would queue a
                // request per occurrence while the first was still in flight.
                if (_japanese.ContainsKey(key) || InFlight.Contains(key)) return;
                InFlight.Add(key);
            }

            Task.Run(async () =>
            {
                try
                {
                    string japanese = await LlmChatController.TranslateLineToJapaneseAsync(
                        key, CancellationToken.None).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(japanese)) return;

                    lock (Gate)
                    {
                        Load();
                        _japanese[key] = japanese.Trim();
                        Save();
                    }
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Learned Japanese for a runtime line; it will be spoken from now on.");
                }
                catch (Exception ex)
                {
                    // Never fatal: the line simply stays silent and may be retried the
                    // next time it appears.
                    LilithModPlugin.Logger.LogWarning(
                        "[Voice] Could not translate a runtime line: " + ex.Message);
                }
                finally
                {
                    lock (Gate) InFlight.Remove(key);
                }
            });
        }

        private static void Load()
        {
            if (_japanese != null) return;
            _japanese = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(CachePath)) return;
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    File.ReadAllText(CachePath));
                if (loaded != null) _japanese = loaded;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[Voice] Could not read the runtime-line cache, starting empty: " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(CachePath,
                    JsonConvert.SerializeObject(_japanese, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[Voice] Could not write the runtime-line cache: " + ex.Message);
            }
        }
    }
}
