using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace LilithMod
{
    /// <summary>
    /// Bookkeeping for how often Lilith leaves a note. Persisted because rarity is
    /// the point: an in-memory count reset each launch, so frequent restarts starved
    /// it and rare ones left it unthrottled. Neither shows up within one session.
    /// </summary>
    internal static class NoteJournal
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static State _state = new State();

        private sealed class State
        {
            public DateTime LastNoteUtc { get; set; } = DateTime.MinValue;
            // Timestamps rather than a running count: the conversations have to be
            // part of one stretch of talking, not six exchanges spread over weeks.
            public List<DateTime> QualifyingUtc { get; set; } = new List<DateTime>();
            // The subset that was personal. Absent from journals written before love
            // letters existed, which deserialize to an empty list.
            public List<DateTime> PersonalUtc { get; set; } = new List<DateTime>();
            public int NotesWritten { get; set; }
        }

        public static void Initialize()
        {
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _path = Path.Combine(root, "notes.json");
            lock (Gate)
            {
                try
                {
                    if (File.Exists(_path))
                        _state = JsonConvert.DeserializeObject<State>(File.ReadAllText(_path)) ?? new State();
                    // An explicit null in the file beats the field initializer.
                    if (_state.QualifyingUtc == null) _state.QualifyingUtc = new List<DateTime>();
                    if (_state.PersonalUtc == null) _state.PersonalUtc = new List<DateTime>();
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Letters] Could not read note journal: {ex.Message}");
                    _state = new State();
                }
            }
        }

        /// <summary>Counts one exchange substantial enough to be worth remembering.</summary>
        public static void RecordQualifying(double windowHours, bool personal = false)
        {
            lock (Gate)
            {
                _state.QualifyingUtc.Add(DateTime.UtcNow);
                if (personal) _state.PersonalUtc.Add(DateTime.UtcNow);
                Prune(windowHours);
                Save();
            }
        }

        /// <summary>How many exchanges still inside the window were personal.</summary>
        public static int PersonalCount(double windowHours)
        {
            lock (Gate)
            {
                Prune(windowHours);
                return _state.PersonalUtc.Count;
            }
        }

        /// <summary>Drops timestamps that have fallen out of the window.</summary>
        private static void Prune(double windowHours)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-windowHours);
            _state.QualifyingUtc.RemoveAll(stamp => stamp < cutoff);
            _state.PersonalUtc.RemoveAll(stamp => stamp < cutoff);
        }

        /// <summary>
        /// Whether a note is due. Three gates, deliberately: enough has to have
        /// happened, enough time has to have passed, and then it still has to land
        /// a roll - so a note arrives unpredictably rather than on a schedule the
        /// player can feel. A failed roll does NOT clear the count, so eligibility
        /// persists and the note simply comes later.
        /// </summary>
        public static bool ShouldWrite(int minimumConversations, double windowHours,
                                      double cooldownHours, float chance)
        {
            lock (Gate)
            {
                Prune(windowHours);
                if (_state.QualifyingUtc.Count < minimumConversations) return false;
                if (_state.LastNoteUtc != DateTime.MinValue &&
                    (DateTime.UtcNow - _state.LastNoteUtc).TotalHours < cooldownHours) return false;
                return UnityEngine.Random.value < chance;
            }
        }

        public static void MarkWritten()
        {
            lock (Gate)
            {
                _state.LastNoteUtc = DateTime.UtcNow;
                _state.QualifyingUtc.Clear();
                _state.PersonalUtc.Clear();
                _state.NotesWritten++;
                Save();
                LilithModPlugin.Logger.LogInfo(
                    $"[Letters] Note {_state.NotesWritten} written; counter reset.");
            }
        }

        public static string Describe()
        {
            lock (Gate)
            {
                string last = _state.LastNoteUtc == DateTime.MinValue
                    ? "never"
                    : _state.LastNoteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                return $"qualifying={_state.QualifyingUtc.Count} personal={_state.PersonalUtc.Count} " +
                       $"last={last} total={_state.NotesWritten}";
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllText(_path, JsonConvert.SerializeObject(_state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Letters] Could not save note journal: {ex.Message}");
            }
        }
    }
}
