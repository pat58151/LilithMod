using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace LilithMod
{
    /// <summary>
    /// Three separate rings, deliberately. Conversations and pats used to share one
    /// five-slot list, so a few pats evicted everything she had actually been told.
    /// Long-term entries are written only when a note is, and are never evicted by
    /// either - they are the record that outlives a session's chatter.
    /// </summary>
    internal static class MemoryStore
    {
        private const int MaxConversations = 8;
        private const int MaxInteractions = 5;
        private const int MaxLongTerm = 40;

        private static readonly object Gate = new object();
        private static State _state = new State();
        private static string _jsonPath;
        private static string _markdownPath;

        internal sealed class MemoryItem
        {
            public DateTime AtUtc { get; set; }
            public string Kind { get; set; }
            public string Summary { get; set; }
        }

        private sealed class State
        {
            public List<MemoryItem> Conversations { get; set; } = new List<MemoryItem>();
            public List<MemoryItem> Interactions { get; set; } = new List<MemoryItem>();
            public List<MemoryItem> LongTerm { get; set; } = new List<MemoryItem>();
        }

        public static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _jsonPath = Path.Combine(root, "memory.json");
            _markdownPath = Path.Combine(root, "MEMORY.md");
            lock (Gate)
            {
                _state = new State();
                try
                {
                    if (File.Exists(_jsonPath)) Load(File.ReadAllText(_jsonPath));
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Memory] Could not load memory: {ex.Message}");
                    _state = new State();
                }
                TrimAll();
            }
        }

        /// <summary>
        /// Reads either shape. Before the rings were split the file was a bare array
        /// of items; those are sorted into the new lists by their Kind rather than
        /// discarded, so an existing install keeps what she remembered.
        /// </summary>
        private static void Load(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            if (json.TrimStart().StartsWith("["))
            {
                var legacy = JsonConvert.DeserializeObject<List<MemoryItem>>(json);
                if (legacy == null) return;
                foreach (MemoryItem item in legacy)
                {
                    if (item == null) continue;
                    if (item.Kind == "interaction") _state.Interactions.Add(item);
                    else _state.Conversations.Add(item);
                }
                LilithModPlugin.Logger.LogInfo("[Memory] Migrated the single-list memory file.");
                return;
            }

            _state = JsonConvert.DeserializeObject<State>(json) ?? new State();
            // An explicit null in the file beats the field initializer.
            if (_state.Conversations == null) _state.Conversations = new List<MemoryItem>();
            if (_state.Interactions == null) _state.Interactions = new List<MemoryItem>();
            if (_state.LongTerm == null) _state.LongTerm = new List<MemoryItem>();
        }

        public static void RecordConversation(string user, string lilith)
        {
            Add(_state.Conversations, MaxConversations, "conversation",
                $"Player: {Compact(user, 240)} | Lilith: {Compact(lilith, 240)}");
        }

        public static void RecordInteraction(string kind)
        {
            Add(_state.Interactions, MaxInteractions, "interaction", Compact(kind, 120));
        }

        /// <summary>
        /// Keeps what a note was about, once the note itself is written. This is the
        /// only thing that survives past the rolling windows above.
        /// </summary>
        public static void RecordLongTerm(string summary)
        {
            Add(_state.LongTerm, MaxLongTerm, "longterm", Compact(summary, 400));
        }

        /// <summary>
        /// The block appended to the system prompt. The long-term section carries its
        /// own restraint instruction: without one she opens every conversation by
        /// reciting what she remembers, which reads as a database, not a person.
        /// </summary>
        public static string Context()
        {
            lock (Gate)
            {
                var lines = new List<string>();

                if (_state.Conversations.Count > 0)
                {
                    lines.Add("Recent conversations:");
                    foreach (MemoryItem item in _state.Conversations)
                        lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                }

                if (_state.Interactions.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add("Recent interactions:");
                    foreach (MemoryItem item in _state.Interactions)
                        lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                }

                if (_state.LongTerm.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add(
                        "Things you remember from further back. Hold these back. Never raise one " +
                        "yourself, never list them, never say you remember them. Only if the player " +
                        "brings that subject up directly may you let it show, and then as a light " +
                        "allusion in a single clause - not a recap. Most conversations should pass " +
                        "with no sign of them at all.");
                    foreach (MemoryItem item in _state.LongTerm)
                        lines.Add($"- {item.AtUtc.ToLocalTime():d}: {item.Summary}");
                }

                return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
            }
        }

        /// <summary>
        /// What a note is written from: talking only. Pats are not what a keepsake is
        /// supposed to be about, and they used to crowd out the conversations here.
        /// </summary>
        public static string ConversationContext()
        {
            lock (Gate)
            {
                if (_state.Conversations.Count == 0) return string.Empty;
                var lines = new List<string> { "Recent conversations:" };
                foreach (MemoryItem item in _state.Conversations)
                    lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                return string.Join("\n", lines);
            }
        }

        private static void Add(List<MemoryItem> ring, int cap, string kind, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return;
            lock (Gate)
            {
                ring.Add(new MemoryItem { AtUtc = DateTime.UtcNow, Kind = kind, Summary = summary });
                Trim(ring, cap);
                Save();
            }
        }

        private static void TrimAll()
        {
            Trim(_state.Conversations, MaxConversations);
            Trim(_state.Interactions, MaxInteractions);
            Trim(_state.LongTerm, MaxLongTerm);
        }

        private static void Trim(List<MemoryItem> ring, int cap)
        {
            while (ring.Count > cap) ring.RemoveAt(0);
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(_jsonPath, JsonConvert.SerializeObject(_state, Formatting.Indented));

                var lines = new List<string> { "# Lilith memory", string.Empty };
                AppendSection(lines, "Recent conversations", _state.Conversations, MaxConversations);
                AppendSection(lines, "Recent interactions", _state.Interactions, MaxInteractions);
                AppendSection(lines, "Long term", _state.LongTerm, MaxLongTerm);
                File.WriteAllLines(_markdownPath, lines);
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Memory] Could not save memory: {ex.Message}");
            }
        }

        private static void AppendSection(List<string> lines, string title, List<MemoryItem> ring, int cap)
        {
            lines.Add($"## {title}");
            lines.Add($"Keeps the {cap} most recent.");
            lines.Add(string.Empty);
            if (ring.Count == 0) lines.Add("_(nothing yet)_");
            foreach (MemoryItem item in ring)
                lines.Add($"- {item.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {item.Summary}");
            lines.Add(string.Empty);
        }

        private static string Compact(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
