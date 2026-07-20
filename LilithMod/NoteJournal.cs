using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace LilithMod
{
    /// <summary>
    /// Bookkeeping for how often Lilith leaves a note.
    ///
    /// Persisted, because the whole point is rarity: when the count lived in a
    /// field it reset on every launch, so restarting often meant a note could
    /// almost never accumulate, and restarting rarely meant nothing throttled it
    /// across sessions. Both failure modes are invisible from inside one session.
    /// </summary>
    internal static class NoteJournal
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static State _state = new State();

        private sealed class State
        {
            public DateTime LastNoteUtc { get; set; } = DateTime.MinValue;
            public int QualifyingSinceLastNote { get; set; }
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
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Letters] Could not read note journal: {ex.Message}");
                    _state = new State();
                }
            }
        }

        /// <summary>Counts one exchange substantial enough to be worth remembering.</summary>
        public static void RecordQualifying()
        {
            lock (Gate)
            {
                _state.QualifyingSinceLastNote++;
                Save();
            }
        }

        /// <summary>
        /// Whether a note is due. Three gates, deliberately: enough has to have
        /// happened, enough time has to have passed, and then it still has to land
        /// a roll - so a note arrives unpredictably rather than on a schedule the
        /// player can feel. A failed roll does NOT clear the count, so eligibility
        /// persists and the note simply comes later.
        /// </summary>
        public static bool ShouldWrite(int minimumConversations, double cooldownHours, float chance)
        {
            lock (Gate)
            {
                if (_state.QualifyingSinceLastNote < minimumConversations) return false;
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
                _state.QualifyingSinceLastNote = 0;
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
                return $"qualifying={_state.QualifyingSinceLastNote} last={last} total={_state.NotesWritten}";
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
