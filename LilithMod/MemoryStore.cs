using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace LilithMod
{
    internal static class MemoryStore
    {
        private const int MaxRecent = 5;
        private static readonly object Gate = new object();
        private static readonly List<MemoryItem> Items = new List<MemoryItem>();
        private static string _jsonPath;
        private static string _markdownPath;

        internal sealed class MemoryItem
        {
            public DateTime AtUtc { get; set; }
            public string Kind { get; set; }
            public string Summary { get; set; }
        }

        public static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _jsonPath = Path.Combine(root, "memory.json");
            _markdownPath = Path.Combine(root, "MEMORY.md");
            lock (Gate)
            {
                Items.Clear();
                try
                {
                    if (File.Exists(_jsonPath))
                    {
                        var loaded = JsonConvert.DeserializeObject<List<MemoryItem>>(File.ReadAllText(_jsonPath));
                        if (loaded != null) Items.AddRange(loaded);
                    }
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Memory] Could not load recent memory: {ex.Message}");
                }
                Trim();
            }
        }

        public static void RecordConversation(string user, string lilith)
        {
            Add("conversation", $"Player: {Compact(user, 240)} | Lilith: {Compact(lilith, 240)}");
        }

        public static void RecordInteraction(string kind)
        {
            Add("interaction", Compact(kind, 120));
        }

        public static string Context()
        {
            lock (Gate)
            {
                if (Items.Count == 0) return string.Empty;
                var lines = new List<string> { "Five most recent remembered interactions:" };
                foreach (var item in Items)
                    lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                return string.Join("\n", lines);
            }
        }

        private static void Add(string kind, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return;
            lock (Gate)
            {
                Items.Add(new MemoryItem { AtUtc = DateTime.UtcNow, Kind = kind, Summary = summary });
                Trim();
                Save();
            }
        }

        private static void Trim()
        {
            while (Items.Count > MaxRecent) Items.RemoveAt(0);
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(_jsonPath, JsonConvert.SerializeObject(Items, Formatting.Indented));
                var lines = new List<string> { "# Lilith memory", "", "Only the five most recent interactions are retained.", "" };
                foreach (var item in Items)
                    lines.Add($"- {item.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {item.Summary}");
                File.WriteAllLines(_markdownPath, lines);
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Memory] Could not save recent memory: {ex.Message}");
            }
        }

        private static string Compact(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
