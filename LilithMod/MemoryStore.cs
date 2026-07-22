using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace LilithMod
{
    /// <summary>Stores recent context, episodes, and stable facts.</summary>
    internal static class MemoryStore
    {
        private const int MaxConversations = 8;
        private const int MaxInteractions = 5;
        private const int MaxLongTerm = 60;
        private const int MaxSemanticFacts = 60;
        private const int MaxPendingNoteConversations = 40;
        private const int MaxRelevantLongTerm = 4;
        private const int MaxRelevantFacts = 6;

        private static readonly object Gate = new object();
        private static readonly HashSet<string> ForgetStopWords = new HashSet<string>(
            new[] { "the", "this", "that", "what", "said", "told", "about", "player",
                    "their", "from", "with", "memory", "remember", "forget" },
            StringComparer.OrdinalIgnoreCase);
        private static State _state = new State();
        private static string _jsonPath;
        private static string _markdownPath;

        internal sealed class MemoryItem
        {
            public string Id { get; set; }
            public DateTime AtUtc { get; set; }
            public string Kind { get; set; }
            public string Summary { get; set; }
            public List<string> Topics { get; set; } = new List<string>();
            public List<string> People { get; set; } = new List<string>();
            public List<string> SourceConversationIds { get; set; } = new List<string>();
            public double Importance { get; set; } = 0.5;
            public double EmotionalWeight { get; set; } = 0.5;
            public DateTime? LastRecalledUtc { get; set; }
            public int RecallCount { get; set; }
        }

        internal sealed class SemanticFact
        {
            public string Id { get; set; }
            public DateTime AtUtc { get; set; }
            public string Key { get; set; }
            public string Statement { get; set; }
            public List<string> Topics { get; set; } = new List<string>();
            public List<string> SourceConversationIds { get; set; } = new List<string>();
            public double Confidence { get; set; } = 0.7;
            public DateTime? LastRecalledUtc { get; set; }
            public int RecallCount { get; set; }
        }

        internal sealed class EpisodeData
        {
            public string Summary { get; set; }
            public List<string> Topics { get; set; } = new List<string>();
            public List<string> People { get; set; } = new List<string>();
            public double Importance { get; set; } = 0.5;
            [JsonProperty("emotional_weight")]
            public double EmotionalWeight { get; set; } = 0.5;
        }

        internal sealed class FactData
        {
            public string Key { get; set; }
            public string Statement { get; set; }
            public List<string> Topics { get; set; } = new List<string>();
            public double Confidence { get; set; } = 0.7;
        }

        internal sealed class ConversationSnapshot
        {
            public string Context { get; set; } = string.Empty;
            public List<string> ConversationIds { get; set; } = new List<string>();
            public DateTime ThroughUtc { get; set; } = DateTime.MinValue;
        }

        private sealed class State
        {
            public List<MemoryItem> Conversations { get; set; } = new List<MemoryItem>();
            public List<MemoryItem> Interactions { get; set; } = new List<MemoryItem>();
            public List<MemoryItem> LongTerm { get; set; } = new List<MemoryItem>();
            public List<MemoryItem> PendingNoteConversations { get; set; } = new List<MemoryItem>();
            public List<SemanticFact> SemanticFacts { get; set; } = new List<SemanticFact>();
            public DateTime DurableConsolidatedThroughUtc { get; set; } = DateTime.MinValue;
        }

        public static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _jsonPath = Path.Combine(root, "memory.json");
            _markdownPath = Path.Combine(root, "MEMORY.md");
            lock (Gate)
            {
                bool recoveredFromBackup = false;
                _state = new State();
                if (File.Exists(_jsonPath))
                {
                    try { Load(File.ReadAllText(_jsonPath)); }
                    catch (Exception ex)
                    {
                        LilithModPlugin.Logger.LogWarning($"[Memory] Could not load memory: {ex.Message}");
                        _state = new State();
                        string backup = _jsonPath + ".bak";
                        if (File.Exists(backup))
                        {
                            try
                            {
                                Load(File.ReadAllText(backup));
                                recoveredFromBackup = true;
                                LilithModPlugin.Logger.LogWarning("[Memory] Recovered memory from the backup.");
                            }
                            catch (Exception backupEx)
                            {
                                LilithModPlugin.Logger.LogWarning(
                                    $"[Memory] Could not load the backup: {backupEx.Message}");
                                _state = new State();
                            }
                        }
                    }
                }
                TrimAll();
                if (recoveredFromBackup) Save();
            }
        }

        /// <summary>Loads current and legacy memory formats.</summary>
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
                    NormalizeItems(new List<MemoryItem> { item });
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
            if (_state.PendingNoteConversations == null)
                _state.PendingNoteConversations = new List<MemoryItem>();
            if (_state.SemanticFacts == null) _state.SemanticFacts = new List<SemanticFact>();
            NormalizeItems(_state.Conversations);
            NormalizeItems(_state.Interactions);
            NormalizeItems(_state.LongTerm);
            NormalizeItems(_state.PendingNoteConversations);
            foreach (SemanticFact fact in _state.SemanticFacts)
            {
                if (string.IsNullOrEmpty(fact.Id)) fact.Id = Guid.NewGuid().ToString("N");
                if (fact.Topics == null) fact.Topics = new List<string>();
                if (fact.SourceConversationIds == null) fact.SourceConversationIds = new List<string>();
            }
        }

        public static void RecordConversation(string user, string lilith, bool qualifiesForNote)
        {
            string summary = $"Player: {Compact(user, 240)} | Lilith: {Compact(lilith, 240)}";
            if (string.IsNullOrWhiteSpace(summary)) return;
            lock (Gate)
            {
                var item = new MemoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    AtUtc = DateTime.UtcNow,
                    Kind = "conversation",
                    Summary = summary
                };
                _state.Conversations.Add(item);
                if (qualifiesForNote)
                {
                    _state.PendingNoteConversations.Add(new MemoryItem
                    {
                        AtUtc = item.AtUtc,
                        Id = item.Id,
                        Kind = item.Kind,
                        Summary = item.Summary
                    });
                }
                TrimAll();
                Save();
            }
        }

        public static void RecordInteraction(string kind)
        {
            Add(_state.Interactions, MaxInteractions, "interaction", Compact(kind, 120));
        }

        /// <summary>Promotes a note summary to long-term memory.</summary>
        public static void RecordLongTerm(string summary)
        {
            RecordEpisode(new EpisodeData { Summary = summary }, new List<string>());
        }

        public static void RecordEpisode(EpisodeData episode, List<string> sourceConversationIds)
        {
            if (episode == null || string.IsNullOrWhiteSpace(episode.Summary)) return;
            lock (Gate)
            {
                var item = new MemoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    AtUtc = DateTime.UtcNow,
                    Kind = "episode",
                    Summary = Compact(episode.Summary, 500),
                    Topics = CompactList(episode.Topics, 16, 60),
                    People = CompactList(episode.People, 10, 60),
                    SourceConversationIds = CompactList(sourceConversationIds, 40, 40),
                    Importance = Clamp01(episode.Importance),
                    EmotionalWeight = Clamp01(episode.EmotionalWeight)
                };

                MemoryItem duplicate = FindDuplicateEpisode(item);
                if (duplicate == null) _state.LongTerm.Add(item);
                else
                {
                    duplicate.AtUtc = item.AtUtc;
                    duplicate.Summary = item.Summary;
                    MergeUnique(duplicate.Topics, item.Topics, 16);
                    MergeUnique(duplicate.People, item.People, 10);
                    MergeUnique(duplicate.SourceConversationIds, item.SourceConversationIds, 40);
                    duplicate.Importance = Math.Max(duplicate.Importance, item.Importance);
                    duplicate.EmotionalWeight = Math.Max(duplicate.EmotionalWeight, item.EmotionalWeight);
                }
                TrimDurableMemory();
                Save();
            }
        }

        public static void RecordSemanticFacts(List<FactData> facts, List<string> sourceConversationIds)
        {
            if (facts == null || facts.Count == 0) return;
            lock (Gate)
            {
                foreach (FactData input in facts)
                {
                    if (input == null || string.IsNullOrWhiteSpace(input.Statement)) continue;
                    string key = Compact(input.Key, 80).ToLowerInvariant();
                    SemanticFact existing = null;
                    if (!string.IsNullOrEmpty(key))
                        existing = _state.SemanticFacts.Find(f =>
                            string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                        existing = _state.SemanticFacts.Find(f =>
                            MemoryVectorizer.Similarity(f.Statement, input.Statement) >= 0.88f);

                    if (existing == null)
                    {
                        _state.SemanticFacts.Add(new SemanticFact
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            AtUtc = DateTime.UtcNow,
                            Key = key,
                            Statement = Compact(input.Statement, 300),
                            Topics = CompactList(input.Topics, 12, 60),
                            SourceConversationIds = CompactList(sourceConversationIds, 40, 40),
                            Confidence = Clamp01(input.Confidence)
                        });
                    }
                    else
                    {
                        // Stable keys replace outdated facts.
                        existing.AtUtc = DateTime.UtcNow;
                        existing.Statement = Compact(input.Statement, 300);
                        existing.Confidence = Clamp01(input.Confidence);
                        MergeUnique(existing.Topics, CompactList(input.Topics, 12, 60), 12);
                        MergeUnique(existing.SourceConversationIds,
                            CompactList(sourceConversationIds, 40, 40), 40);
                    }
                }
                TrimDurableMemory();
                Save();
            }
        }

        /// <summary>Builds restrained, query-relevant memory context.</summary>
        public static string Context(int conversationsAlreadyInHistory, string currentMessage)
        {
            lock (Gate)
            {
                var lines = new List<string>();

                int conversationCount = Math.Max(
                    0, _state.Conversations.Count - Math.Max(0, conversationsAlreadyInHistory));
                if (conversationCount > 0)
                {
                    lines.Add("Recent conversations:");
                    for (int i = 0; i < conversationCount; i++)
                    {
                        MemoryItem item = _state.Conversations[i];
                        lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                    }
                }

                if (_state.Interactions.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add("Recent interactions:");
                    foreach (MemoryItem item in _state.Interactions)
                        lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                }

                List<MemoryItem> relevantLongTerm = RelevantEpisodes(currentMessage);
                if (relevantLongTerm.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add(
                        "Relevant episodes you remember from further back. Hold these back. Never raise one " +
                        "yourself, never list them, never say you remember them. Only if the player " +
                        "brings that subject up directly may you let it show, and then as a light " +
                        "allusion in a single clause - not a recap. Most conversations should pass " +
                        "with no sign of them at all.");
                    foreach (MemoryItem item in relevantLongTerm)
                        lines.Add($"- {item.AtUtc.ToLocalTime():d}: {item.Summary}");
                }

                List<SemanticFact> relevantFacts = RelevantFacts(currentMessage);
                if (relevantFacts.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(string.Empty);
                    lines.Add(
                        "Relevant things the player has established. Use them quietly when useful. " +
                        "Do not list them or announce that they came from memory.");
                    foreach (SemanticFact fact in relevantFacts)
                        lines.Add("- " + fact.Statement);
                }

                if (relevantLongTerm.Count > 0 || relevantFacts.Count > 0)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (MemoryItem item in relevantLongTerm)
                    {
                        item.LastRecalledUtc = now;
                        item.RecallCount++;
                    }
                    foreach (SemanticFact fact in relevantFacts)
                    {
                        fact.LastRecalledUtc = now;
                        fact.RecallCount++;
                    }
                    Save();
                }

                return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
            }
        }

        /// <summary>Returns conversation-only context for notes.</summary>
        public static string QualifyingConversationContext(DateTime cutoffUtc)
        {
            return QualifyingConversationSnapshot(cutoffUtc).Context;
        }

        public static ConversationSnapshot QualifyingConversationSnapshot(DateTime cutoffUtc)
        {
            lock (Gate)
            {
                var snapshot = new ConversationSnapshot();
                if (_state.PendingNoteConversations.Count == 0) return snapshot;
                var lines = new List<string> { "Recent conversations:" };
                foreach (MemoryItem item in _state.PendingNoteConversations)
                    if (item.AtUtc >= cutoffUtc)
                    {
                        lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                        snapshot.ConversationIds.Add(item.Id);
                        if (item.AtUtc > snapshot.ThroughUtc) snapshot.ThroughUtc = item.AtUtc;
                    }
                snapshot.Context = lines.Count == 1 ? string.Empty : string.Join("\n", lines);
                return snapshot;
            }
        }

        public static ConversationSnapshot DurableMemorySnapshot(
            DateTime cutoffUtc, int minimumConversations, double sessionGapHours)
        {
            lock (Gate)
            {
                var segment = new List<MemoryItem>();
                DateTime previousUtc = DateTime.MinValue;
                foreach (MemoryItem item in _state.PendingNoteConversations)
                {
                    if (item.AtUtc < cutoffUtc || item.AtUtc <= _state.DurableConsolidatedThroughUtc)
                        continue;
                    if (segment.Count > 0 &&
                        (item.AtUtc - previousUtc).TotalHours >= sessionGapHours)
                    {
                        if (segment.Count >= minimumConversations)
                            return BuildSnapshot(segment);
                        segment.Clear();
                    }
                    segment.Add(item);
                    previousUtc = item.AtUtc;
                }
                return segment.Count >= minimumConversations
                    ? BuildSnapshot(segment)
                    : new ConversationSnapshot();
            }
        }

        private static ConversationSnapshot BuildSnapshot(List<MemoryItem> items)
        {
            var snapshot = new ConversationSnapshot();
            var lines = new List<string> { "Recent conversations:" };
            foreach (MemoryItem item in items)
            {
                lines.Add($"- {item.AtUtc.ToLocalTime():g}: {item.Summary}");
                snapshot.ConversationIds.Add(item.Id);
                if (item.AtUtc > snapshot.ThroughUtc) snapshot.ThroughUtc = item.AtUtc;
            }
            snapshot.Context = string.Join("\n", lines);
            return snapshot;
        }

        public static void MarkDurableConsolidated(DateTime throughUtc)
        {
            if (throughUtc == DateTime.MinValue) return;
            lock (Gate)
            {
                if (throughUtc > _state.DurableConsolidatedThroughUtc)
                    _state.DurableConsolidatedThroughUtc = throughUtc;
                Save();
            }
        }

        public static void ClearQualifyingConversations()
        {
            lock (Gate)
            {
                _state.PendingNoteConversations.Clear();
                Save();
            }
        }

        public static int Forget(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            lock (Gate)
            {
                var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MemoryItem item in _state.LongTerm)
                    if (MatchesQuery(query, SearchText(item)))
                        AddSourceIds(sourceIds, item.SourceConversationIds);
                foreach (SemanticFact fact in _state.SemanticFacts)
                    if (MatchesQuery(query, FactSearchText(fact)))
                        AddSourceIds(sourceIds, fact.SourceConversationIds);

                int removed = 0;
                removed += _state.LongTerm.RemoveAll(item => MatchesQuery(query, SearchText(item)));
                removed += _state.SemanticFacts.RemoveAll(fact =>
                    MatchesQuery(query, FactSearchText(fact)));
                removed += _state.Conversations.RemoveAll(item =>
                    sourceIds.Contains(item.Id ?? string.Empty) ||
                    MatchesQuery(query, item.Summary));
                removed += _state.PendingNoteConversations.RemoveAll(item =>
                    sourceIds.Contains(item.Id ?? string.Empty) ||
                    MatchesQuery(query, item.Summary));

                if (removed > 0)
                {
                    Save();
                    PurgeBackups();
                }
                return removed;
            }
        }

        public static int ForgetFact(string key, string query)
        {
            lock (Gate)
            {
                var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Predicate<SemanticFact> matches;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    string normalizedKey = Compact(key, 80).ToLowerInvariant();
                    matches = fact => string.Equals(
                        fact.Key, normalizedKey, StringComparison.OrdinalIgnoreCase);
                }
                else matches = fact => !string.IsNullOrWhiteSpace(query) &&
                    MatchesQuery(query, FactSearchText(fact));

                foreach (SemanticFact fact in _state.SemanticFacts)
                    if (matches(fact)) AddSourceIds(sourceIds, fact.SourceConversationIds);
                int removed = _state.SemanticFacts.RemoveAll(matches);
                if (!string.IsNullOrWhiteSpace(query) || sourceIds.Count > 0)
                {
                    bool useQueryFallback = sourceIds.Count == 0 &&
                        !string.IsNullOrWhiteSpace(query);
                    removed += _state.Conversations.RemoveAll(item =>
                        sourceIds.Contains(item.Id ?? string.Empty) ||
                        (useQueryFallback && MatchesQuery(query, item.Summary)));
                    removed += _state.PendingNoteConversations.RemoveAll(item =>
                        sourceIds.Contains(item.Id ?? string.Empty) ||
                        (useQueryFallback && MatchesQuery(query, item.Summary)));
                }
                if (removed > 0)
                {
                    Save();
                    PurgeBackups();
                }
                return removed;
            }
        }

        public static void CorrectFact(FactData fact)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Statement)) return;
            RecordSemanticFacts(new List<FactData> { fact }, new List<string>());
            lock (Gate) PurgeBackups();
        }

        public static int ForgetAll()
        {
            lock (Gate)
            {
                int removed = _state.Conversations.Count + _state.Interactions.Count +
                    _state.LongTerm.Count + _state.PendingNoteConversations.Count +
                    _state.SemanticFacts.Count;
                _state = new State();
                Save();
                PurgeBackups();
                return removed;
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
            Trim(_state.PendingNoteConversations, MaxPendingNoteConversations);
            TrimDurableMemory();
        }

        private static void Trim(List<MemoryItem> ring, int cap)
        {
            while (ring.Count > cap) ring.RemoveAt(0);
        }

        private static void Save()
        {
            try
            {
                AtomicWrite(_jsonPath, JsonConvert.SerializeObject(_state, Formatting.Indented));

                var lines = new List<string> { "# Lilith memory", string.Empty };
                AppendSection(lines, "Recent conversations", _state.Conversations, MaxConversations);
                AppendSection(lines, "Recent interactions", _state.Interactions, MaxInteractions);
                AppendEpisodeSection(lines);
                AppendFactSection(lines);
                AtomicWrite(_markdownPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Memory] Could not save memory: {ex.Message}");
            }
        }

        private static void AtomicWrite(string path, string contents)
        {
            string temp = path + ".tmp";
            string backup = path + ".bak";
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
                File.WriteAllText(temp, contents);
                if (File.Exists(path)) File.Replace(temp, path, backup, true);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static void PurgeBackups()
        {
            foreach (string path in new[]
            {
                _jsonPath + ".bak", _jsonPath + ".tmp",
                _markdownPath + ".bak", _markdownPath + ".tmp"
            })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning(
                        $"[Memory] Could not remove stale backup '{Path.GetFileName(path)}': {ex.Message}");
                }
            }
        }

        private static List<MemoryItem> RelevantEpisodes(string currentMessage)
        {
            var matches = new List<ScoredEpisode>();
            if (string.IsNullOrWhiteSpace(currentMessage)) return new List<MemoryItem>();
            DateTime now = DateTime.UtcNow;
            foreach (MemoryItem item in _state.LongTerm)
            {
                float relevance = MemoryVectorizer.Similarity(currentMessage, SearchText(item));
                if (HasDirectMatch(currentMessage, item.Topics) ||
                    HasDirectMatch(currentMessage, item.People))
                    relevance = Math.Max(relevance, 0.8f);
                if (relevance < 0.07f) continue;
                double recency = Recency(item.AtUtc, now);
                double score = relevance * 0.72 + item.Importance * 0.14 +
                    item.EmotionalWeight * 0.08 + recency * 0.06;
                matches.Add(new ScoredEpisode { Item = item, Score = score });
            }
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            var result = new List<MemoryItem>();
            for (int i = 0; i < Math.Min(MaxRelevantLongTerm, matches.Count); i++)
                result.Add(matches[i].Item);
            result.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
            return result;
        }

        private static List<SemanticFact> RelevantFacts(string currentMessage)
        {
            var matches = new List<ScoredFact>();
            if (string.IsNullOrWhiteSpace(currentMessage)) return new List<SemanticFact>();
            DateTime now = DateTime.UtcNow;
            foreach (SemanticFact fact in _state.SemanticFacts)
            {
                string search = fact.Statement + " " + string.Join(" ", fact.Topics);
                float relevance = MemoryVectorizer.Similarity(currentMessage, search);
                if (HasDirectMatch(currentMessage, fact.Topics))
                    relevance = Math.Max(relevance, 0.8f);
                if (relevance < 0.07f) continue;
                double score = relevance * 0.82 + fact.Confidence * 0.12 +
                    Recency(fact.AtUtc, now) * 0.06;
                matches.Add(new ScoredFact { Item = fact, Score = score });
            }
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            var result = new List<SemanticFact>();
            for (int i = 0; i < Math.Min(MaxRelevantFacts, matches.Count); i++)
                result.Add(matches[i].Item);
            return result;
        }

        private static string SearchText(MemoryItem item)
        {
            return (item.Summary ?? string.Empty) + " " +
                string.Join(" ", item.Topics ?? new List<string>()) + " " +
                string.Join(" ", item.People ?? new List<string>());
        }

        private static string FactSearchText(SemanticFact fact)
        {
            return (fact.Key ?? string.Empty) + " " + (fact.Statement ?? string.Empty) + " " +
                string.Join(" ", fact.Topics ?? new List<string>());
        }

        internal static bool MatchesQuery(string query, string candidate)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
                return false;
            string target = candidate.Normalize().ToLowerInvariant();
            foreach (Match match in Regex.Matches(
                query.Normalize().ToLowerInvariant(), @"[\p{L}\p{N}]+"))
            {
                string term = match.Value;
                bool cjk = term.IndexOfAny(new[] { 'あ', 'ア', '一', '龯' }) >= 0 ||
                    Regex.IsMatch(term, @"[\u3040-\u30ff\u3400-\u9fff]");
                if ((!cjk && term.Length < 3) || ForgetStopWords.Contains(term)) continue;
                string pattern = cjk
                    ? Regex.Escape(term)
                    : @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])";
                if (Regex.IsMatch(target, pattern, RegexOptions.IgnoreCase)) return true;
            }
            return MemoryVectorizer.Similarity(query, candidate) >= 0.11f;
        }

        private static void AddSourceIds(HashSet<string> target, List<string> sourceIds)
        {
            if (sourceIds == null) return;
            foreach (string id in sourceIds)
                if (!string.IsNullOrWhiteSpace(id)) target.Add(id);
        }

        private static bool HasDirectMatch(string query, List<string> terms)
        {
            if (string.IsNullOrWhiteSpace(query) || terms == null) return false;
            foreach (string term in terms)
                if (!string.IsNullOrWhiteSpace(term) && term.Length >= 2 &&
                    query.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static double Recency(DateTime atUtc, DateTime now)
        {
            double days = Math.Max(0, (now - atUtc).TotalDays);
            return 1.0 / (1.0 + days / 30.0);
        }

        private static void TrimDurableMemory()
        {
            while (_state.LongTerm.Count > MaxLongTerm)
            {
                int remove = 0;
                double lowest = double.MaxValue;
                for (int i = 0; i < _state.LongTerm.Count; i++)
                {
                    MemoryItem item = _state.LongTerm[i];
                    double retention = item.Importance * 0.45 + item.EmotionalWeight * 0.25 +
                        Math.Min(1, item.RecallCount / 5.0) * 0.15 +
                        Recency(item.AtUtc, DateTime.UtcNow) * 0.15;
                    if (retention < lowest) { lowest = retention; remove = i; }
                }
                _state.LongTerm.RemoveAt(remove);
            }
            while (_state.SemanticFacts.Count > MaxSemanticFacts)
            {
                int remove = 0;
                double lowest = double.MaxValue;
                for (int i = 0; i < _state.SemanticFacts.Count; i++)
                {
                    SemanticFact fact = _state.SemanticFacts[i];
                    double retention = fact.Confidence * 0.7 +
                        Math.Min(1, fact.RecallCount / 5.0) * 0.15 +
                        Recency(fact.AtUtc, DateTime.UtcNow) * 0.15;
                    if (retention < lowest) { lowest = retention; remove = i; }
                }
                _state.SemanticFacts.RemoveAt(remove);
            }
        }

        private static MemoryItem FindDuplicateEpisode(MemoryItem candidate)
        {
            foreach (MemoryItem existing in _state.LongTerm)
                if (MemoryVectorizer.Similarity(SearchText(existing), SearchText(candidate)) >= 0.86f)
                    return existing;
            return null;
        }

        private static void NormalizeItems(List<MemoryItem> items)
        {
            foreach (MemoryItem item in items)
            {
                if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                if (item.Topics == null) item.Topics = new List<string>();
                if (item.People == null) item.People = new List<string>();
                if (item.SourceConversationIds == null)
                    item.SourceConversationIds = new List<string>();
            }
        }

        private static List<string> CompactList(List<string> values, int maxItems, int maxLength)
        {
            var result = new List<string>();
            if (values == null) return result;
            foreach (string value in values)
            {
                string compact = Compact(value, maxLength);
                if (string.IsNullOrEmpty(compact) || result.Exists(x =>
                    string.Equals(x, compact, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(compact);
                if (result.Count == maxItems) break;
            }
            return result;
        }

        private static void MergeUnique(List<string> target, List<string> additions, int cap)
        {
            if (target == null || additions == null) return;
            foreach (string value in additions)
            {
                if (!target.Exists(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                    target.Add(value);
                if (target.Count == cap) break;
            }
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0.5;
            return Math.Max(0, Math.Min(1, value));
        }

        private sealed class ScoredEpisode
        {
            public MemoryItem Item;
            public double Score;
        }

        private sealed class ScoredFact
        {
            public SemanticFact Item;
            public double Score;
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

        private static void AppendEpisodeSection(List<string> lines)
        {
            lines.Add("## Episodes");
            lines.Add($"Keeps up to {MaxLongTerm}, ranked by importance and use.");
            lines.Add(string.Empty);
            if (_state.LongTerm.Count == 0) lines.Add("_(nothing yet)_");
            foreach (MemoryItem item in _state.LongTerm)
            {
                string tags = item.Topics.Count == 0 ? string.Empty :
                    " [" + string.Join(", ", item.Topics) + "]";
                lines.Add($"- {item.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {item.Summary}{tags}");
            }
            lines.Add(string.Empty);
        }

        private static void AppendFactSection(List<string> lines)
        {
            lines.Add("## Established facts");
            lines.Add($"Keeps up to {MaxSemanticFacts}, replacing older values with the same key.");
            lines.Add(string.Empty);
            if (_state.SemanticFacts.Count == 0) lines.Add("_(nothing yet)_");
            foreach (SemanticFact fact in _state.SemanticFacts)
                lines.Add($"- {fact.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}: {fact.Statement}");
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
