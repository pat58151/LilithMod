using System;
using System.Collections.Generic;
using System.IO;
using LilithMod;
using Newtonsoft.Json;

internal static class Program
{
    private static readonly string Root = AppContext.BaseDirectory;

    private static void Main()
    {
        try
        {
            Clean();
            MemoryStore.Initialize();

            for (int i = 1; i <= 10; i++)
                MemoryStore.RecordConversation($"message-{i}", $"reply-{i}", true);
            MemoryStore.RecordConversation("errand", "done", false);

            string noteContext = MemoryStore.QualifyingConversationContext(DateTime.UtcNow.AddHours(-1));
            Require(Count(noteContext, "Player:") == 10, "note context lost qualifying conversations");
            Require(!noteContext.Contains("errand"), "note context included an unqualified errand");

            MemoryStore.ConversationSnapshot durable = MemoryStore.DurableMemorySnapshot(
                DateTime.UtcNow.AddHours(-1), 6, 2.0);
            Require(Count(durable.Context, "Player:") == 10 && durable.ConversationIds.Count == 10,
                "durable snapshot did not retain its source conversations");
            MemoryStore.MarkDurableConsolidated(durable.ThroughUtc);
            Require(MemoryStore.DurableMemorySnapshot(
                DateTime.UtcNow.AddHours(-1), 6, 2.0).Context == string.Empty,
                "consolidated conversations were selected a second time");

            string deduplicated = MemoryStore.Context(3, "unrelated");
            Require(Count(deduplicated, "Player:") == 5, "session conversations were not excluded");

            for (int i = 1; i <= 7; i++) MemoryStore.RecordInteraction($"pat-{i}");
            string interactions = MemoryStore.Context(8, "unrelated");
            Require(Count(interactions, "pat-") == 5, "interaction ring did not retain exactly five");

            MemoryStore.RecordLongTerm("The player likes hiking near Chiang Mai.");
            MemoryStore.RecordLongTerm("The player works a night shift.");
            string relevant = MemoryStore.Context(8, "Do you remember hiking?");
            Require(relevant.Contains("hiking"), "relevant long-term memory was not selected");
            Require(!relevant.Contains("night shift"), "irrelevant long-term memory was selected");

            Require(MemoryVectorizer.Similarity("My job is difficult", "My career is difficult") >
                    MemoryVectorizer.Similarity("My job is difficult", "I had a nightmare"),
                "English synonym expansion did not improve concept similarity");
            Require(MemoryVectorizer.Similarity("仕事が大変", "My career is difficult") >
                    MemoryVectorizer.Similarity("仕事が大変", "I had a nightmare"),
                "cross-language synonym expansion did not improve concept similarity");
            Require(MemoryVectorizer.Similarity("furious", "angry") >
                    MemoryVectorizer.Similarity("furious", "university"),
                "emotion synonym expansion did not improve concept similarity");

            MemoryStore.RecordEpisode(new MemoryStore.EpisodeData
            {
                Summary = "The player found their new job demanding.",
                Topics = new List<string> { "job" },
                Importance = 0.7
            }, new List<string> { "source-work" });
            Require(MemoryStore.Context(8, "My career has changed").Contains("new job"),
                "episode retrieval did not apply synonym expansion");

            MemoryStore.RecordEpisode(new MemoryStore.EpisodeData
            {
                Summary = "The player felt proud after receiving a promotion.",
                Topics = new System.Collections.Generic.List<string> { "昇進", "promotion", "work" },
                Importance = 0.9,
                EmotionalWeight = 0.8
            }, new System.Collections.Generic.List<string> { "source-1" });
            Require(MemoryStore.Context(8, "昇進について").Contains("promotion"),
                "multilingual topic retrieval did not find the episode");

            MemoryStore.RecordSemanticFacts(new System.Collections.Generic.List<MemoryStore.FactData>
            {
                new MemoryStore.FactData
                {
                    Key = "work.role", Statement = "The player works as a designer.",
                    Topics = new System.Collections.Generic.List<string> { "work", "job", "designer" }
                }
            }, new System.Collections.Generic.List<string> { "source-1" });
            MemoryStore.RecordSemanticFacts(new System.Collections.Generic.List<MemoryStore.FactData>
            {
                new MemoryStore.FactData
                {
                    Key = "work.role", Statement = "The player works as an engineer.",
                    Topics = new System.Collections.Generic.List<string> { "work", "job", "engineer" }
                }
            }, new System.Collections.Generic.List<string> { "source-2" });
            string currentRole = MemoryStore.Context(8, "What is my work role?");
            Require(currentRole.Contains("engineer") && !currentRole.Contains("designer"),
                "new semantic fact did not replace the contradictory old value");

            int forgottenFact = MemoryStore.ForgetFact("work.role", "work job engineer");
            string episodeAfterFactRemoval = MemoryStore.Context(8, "What changed with my job?");
            Require(forgottenFact == 1 && !episodeAfterFactRemoval.Contains("engineer") &&
                    episodeAfterFactRemoval.Contains("new job"),
                "fact removal erased an episode or retained the obsolete fact");

            MemoryStore.CorrectFact(new MemoryStore.FactData
            {
                Key = "work.role", Statement = "The player works as a researcher.",
                Topics = new List<string> { "work", "job", "researcher" }, Confidence = 1.0
            });
            string correctedRole = MemoryStore.Context(8, "What is my current job?");
            Require(correctedRole.Contains("researcher") && !correctedRole.Contains("engineer"),
                "explicit correction did not replace the old fact immediately");

            MemoryStore.RecordConversation(
                "My work as a researcher is going well.", "I am glad to hear that.", true);
            int forgottenWork = MemoryStore.Forget("work job career researcher");
            string forgottenContext = MemoryStore.Context(8, "Tell me about hiking and my career");
            Require(forgottenWork > 0 && !forgottenContext.Contains("researcher") &&
                    !forgottenContext.Contains("new job") && forgottenContext.Contains("hiking"),
                "topic forgetting did not remove matching memory while preserving other subjects");
            Require(!MemoryStore.QualifyingConversationContext(DateTime.MinValue)
                    .Contains("researcher"),
                "forgotten conversation remained eligible for a future note");
            string forgetBackup = Path.Combine(Root, "memory.json.bak");
            Require(!File.Exists(forgetBackup) ||
                    (!File.ReadAllText(forgetBackup).Contains("researcher") &&
                     !File.ReadAllText(forgetBackup).Contains("new job")),
                "topic forgetting left stale content in the backup file");

            MemoryStore.ClearQualifyingConversations();
            Require(MemoryStore.QualifyingConversationContext(DateTime.MinValue) == string.Empty,
                "qualifying conversations were not cleared after a note");

            string json = Path.Combine(Root, "memory.json");
            Require(File.Exists(json + ".bak"), "atomic save did not create a backup");
            File.WriteAllText(json, "{broken");
            MemoryStore.Initialize();
            Require(MemoryStore.Context(8, "hiking").Contains("hiking"),
                "corrupt primary memory did not recover from backup");
            Require(File.ReadAllText(json).TrimStart().StartsWith("{"),
                "backup recovery did not repair the primary memory file");

            Clean();
            File.WriteAllText(json,
                "[{\"AtUtc\":\"2026-01-01T00:00:00Z\",\"Kind\":\"conversation\",\"Summary\":\"Player: legacy | Lilith: retained\"}]");
            MemoryStore.Initialize();
            Require(MemoryStore.Context(0, "legacy").Contains("legacy"),
                "legacy memory format did not migrate");

            Clean();
            var pending = new List<object>();
            DateTime oldStart = DateTime.UtcNow.AddHours(-5);
            for (int i = 1; i <= 5; i++)
                pending.Add(new { Id = "old-" + i, AtUtc = oldStart.AddMinutes(i),
                    Kind = "conversation", Summary = "Player: old-" + i + " | Lilith: reply" });
            DateTime newStart = oldStart.AddHours(3);
            for (int i = 1; i <= 7; i++)
                pending.Add(new { Id = "new-" + i, AtUtc = newStart.AddMinutes(i),
                    Kind = "conversation", Summary = "Player: new-" + i + " | Lilith: reply" });
            File.WriteAllText(json, JsonConvert.SerializeObject(new
            {
                Conversations = Array.Empty<object>(), Interactions = Array.Empty<object>(),
                LongTerm = Array.Empty<object>(), PendingNoteConversations = pending,
                SemanticFacts = Array.Empty<object>(), DurableConsolidatedThroughUtc = DateTime.MinValue
            }));
            MemoryStore.Initialize();
            MemoryStore.ConversationSnapshot split = MemoryStore.DurableMemorySnapshot(
                DateTime.UtcNow.AddHours(-12), 6, 2.0);
            Require(Count(split.Context, "Player:") == 7 && split.Context.Contains("new-1") &&
                !split.Context.Contains("old-1"),
                "conversation gap did not split unrelated episodic sessions");

            MemoryStore.RecordLongTerm("A final memory that should be erased.");
            Require(MemoryStore.ForgetAll() > 0 &&
                    MemoryStore.Context(0, "final memory").Length == 0,
                "forget everything did not clear all memory layers");
            Require(!File.Exists(Path.Combine(Root, "memory.json.bak")),
                "forget everything left a recoverable stale backup");

            Console.WriteLine("MEMORY HARNESS PASS");
        }
        finally
        {
            Clean();
        }
    }

    private static int Count(string value, string marker)
    {
        int count = 0;
        int at = 0;
        while ((at = value.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += marker.Length;
        }
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Clean()
    {
        foreach (string name in new[]
        {
            "memory.json", "memory.json.bak", "memory.json.tmp",
            "MEMORY.md", "MEMORY.md.bak", "MEMORY.md.tmp"
        })
        {
            string path = Path.Combine(Root, name);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

namespace LilithMod
{
    internal static class LilithModPlugin
    {
        internal static readonly TestLogger Logger = new TestLogger();
    }

    internal sealed class TestLogger
    {
        public void LogInfo(string value) { }
        public void LogWarning(string value) { }
    }
}
