using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime;
using Newtonsoft.Json;
using UnityEngine;

namespace LilithMod
{
    public class DumpDatabaseBehaviour : MonoBehaviour
    {
        private float _elapsed;
        private bool _done;
        private readonly float _initialDelay = 1f;
        private readonly float _timeout = 30f;

        public DumpDatabaseBehaviour(System.IntPtr ptr) : base(ptr) { }

        public void Awake()
        {
            _elapsed = 0f;
            _done = false;
        }

        public void Update()
        {
            if (_done)
                return;

            _elapsed += Time.deltaTime;

            if (_elapsed < _initialDelay)
                return;

            if (DialogueManager.s_instance != null)
            {
                try
                {
                    PerformDump();
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogError($"[LilithMod] Unhandled exception during dump: {ex}");
                }
                _done = true;
                Destroy(this); // component only - the GameObject is BepInEx's shared manager
                return;
            }

            if (_elapsed >= _timeout)
            {
                LilithModPlugin.Logger.LogWarning("[LilithMod] DialogueManager instance not found within timeout; aborting dump.");
                _done = true;
                Destroy(this); // component only - the GameObject is BepInEx's shared manager
            }
        }

        private void PerformDump()
        {
            string dumpDir = Path.Combine(
                Path.GetDirectoryName(typeof(LilithModPlugin).Assembly.Location),
                "dump");
            Directory.CreateDirectory(dumpDir);

            int totalNodes = 0;
            int totalPlayerLines = 0;
            int totalDialogueLines = 0;

            // --- Dialogue Nodes ---
            try
            {
                var databases = DialogueManager.s_instance._databases;
                if (databases == null)
                {
                    LilithModPlugin.Logger.LogWarning("[LilithMod] _databases is null; skipping dialogue node dump.");
                }
                else
                {
                    for (int dbIdx = 0; dbIdx < databases.Length; dbIdx++)
                    {
                        var db = databases[dbIdx];
                        if (db == null)
                            continue;

                        var nodeDtos = new List<DialogueNodeDto>();

                        var nodes = db.nodes;
                        if (nodes != null)
                        {
                            for (int i = 0; i < nodes.Count; i++)
                            {
                                var node = nodes[i];
                                if (node == null)
                                    continue;

                                totalNodes++;

                                var dto = new DialogueNodeDto
                                {
                                    id = node.id,
                                    speaker = node.speaker,
                                    baseWeight = node.baseWeight,
                                    triggerTypes = ConvertTriggerTypes(node.triggerTypes),
                                    lineId = node.lineId,
                                    text = node.text,
                                    emotion = node.emotion,
                                    duration = node.duration,
                                    actionType = (int)node.actionType,
                                    nextStateType = (int)node.nextStateType,
                                    nextStateDuration = node.nextStateDuration,
                                    soundId = node.soundId,
                                    conditions = ConvertConditions(node.conditions),
                                    options = ConvertOptions(node.options),
                                    nextId = node.nextId,
                                    playerStates = ConvertStringList(node.playerStates),
                                    playerLineInteraction = node.playerLineInteraction,
                                    playerLineOptions = ConvertPlayerLineOptions(node.playerLineOptions)
                                };

                                nodeDtos.Add(dto);
                            }
                        }

                        string json = JsonConvert.SerializeObject(nodeDtos, Formatting.Indented);
                        string safeName = SanitizeFileName(db.databaseName ?? "unknown");
                        string filePath = Path.Combine(dumpDir, $"dialogue_nodes_{safeName}.json");
                        File.WriteAllText(filePath, json);
                        LilithModPlugin.Logger.LogInfo($"[LilithMod] Wrote {nodeDtos.Count} nodes to {Path.GetFileName(filePath)}");
                    }
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LilithMod] Error dumping dialogue nodes: {ex}");
            }

            // --- Player Line Database ---
            try
            {
                var playerDb = DialogueManager.s_instance.GetPlayerLineDatabase();
                if (playerDb == null)
                {
                    LilithModPlugin.Logger.LogWarning("[LilithMod] GetPlayerLineDatabase() returned null; skipping player lines dump.");
                }
                else
                {
                    var entryDtos = new List<PlayerLineEntryDto>();
                    var entries = playerDb.entries;
                    if (entries != null)
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var entry = entries[i];
                            if (entry == null)
                                continue;

                            totalPlayerLines++;

                            entryDtos.Add(new PlayerLineEntryDto
                            {
                                id = entry.id,
                                LineID = entry.LineID,
                                groupId = entry.groupId,
                                playerStates = ConvertStringList(entry.playerStates),
                                text = entry.text,
                                viewLimit = entry.viewLimit
                            });
                        }
                    }

                    string json = JsonConvert.SerializeObject(entryDtos, Formatting.Indented);
                    string filePath = Path.Combine(dumpDir, "player_lines.json");
                    File.WriteAllText(filePath, json);
                    LilithModPlugin.Logger.LogInfo($"[LilithMod] Wrote {entryDtos.Count} player line entries to player_lines.json");
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LilithMod] Error dumping player lines: {ex}");
            }

            // --- Dialogue Line Database ---
            try
            {
                var allDialogueLineDbs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueLineDatabase>());
                DialogueLineDatabase dialogueLineDb = null;
                if (allDialogueLineDbs != null)
                {
                    foreach (var obj in allDialogueLineDbs)
                    {
                        var casted = obj.TryCast<DialogueLineDatabase>();
                        if (casted != null)
                        {
                            dialogueLineDb = casted;
                            break;
                        }
                    }
                }

                if (dialogueLineDb == null)
                {
                    LilithModPlugin.Logger.LogInfo("[LilithMod] No DialogueLineDatabase found in memory – skipping dump.");
                }
                else
                {
                    var entryDtos = new List<DialogueLineEntryDto>();
                    var entries = dialogueLineDb.entries;
                    if (entries != null)
                    {
                        for (int i = 0; i < entries.Count; i++)
                        {
                            var entry = entries[i];
                            if (entry == null)
                                continue;

                            totalDialogueLines++;

                            entryDtos.Add(new DialogueLineEntryDto
                            {
                                id = entry.id,
                                text = entry.text,
                                soundId = entry.soundId
                            });
                        }
                    }

                    string json = JsonConvert.SerializeObject(entryDtos, Formatting.Indented);
                    string filePath = Path.Combine(dumpDir, "dialogue_lines.json");
                    File.WriteAllText(filePath, json);
                    LilithModPlugin.Logger.LogInfo($"[LilithMod] Wrote {entryDtos.Count} dialogue line entries to dialogue_lines.json");
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LilithMod] Error dumping dialogue lines: {ex}");
            }

            LilithModPlugin.Logger.LogInfo($"[LilithMod] Dump complete. Nodes: {totalNodes}, PlayerLines: {totalPlayerLines}, DialogueLines: {totalDialogueLines}");
        }

        #region Conversion Helpers

        private static List<int> ConvertTriggerTypes(Il2CppSystem.Collections.Generic.List<DialogueTriggerType> triggerTypes)
        {
            var result = new List<int>();
            if (triggerTypes != null)
            {
                for (int i = 0; i < triggerTypes.Count; i++)
                {
                    result.Add((int)triggerTypes[i]);
                }
            }
            return result;
        }

        private static List<string> ConvertStringList(Il2CppSystem.Collections.Generic.List<string> list)
        {
            var result = new List<string>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    result.Add(list[i]);
                }
            }
            return result;
        }

        private static DialogueConditionDto ConvertConditions(DialogueCondition conditions)
        {
            if (conditions == null)
                return null;

            return new DialogueConditionDto
            {
                timeRangeStart = conditions.timeRangeStart,
                timeRangeEnd = conditions.timeRangeEnd,
                dateMMdd = conditions.dateMMdd
            };
        }

        private static List<DialogueOptionDto> ConvertOptions(Il2CppSystem.Collections.Generic.List<DialogueOption> options)
        {
            var result = new List<DialogueOptionDto>();
            if (options != null)
            {
                for (int i = 0; i < options.Count; i++)
                {
                    var opt = options[i];
                    if (opt == null)
                        continue;

                    result.Add(new DialogueOptionDto
                    {
                        text = opt.text,
                        nextId = opt.nextId
                    });
                }
            }
            return result;
        }

        private static List<DialoguePlayerLineOptionDto> ConvertPlayerLineOptions(Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption> playerLineOptions)
        {
            var result = new List<DialoguePlayerLineOptionDto>();
            if (playerLineOptions != null)
            {
                for (int i = 0; i < playerLineOptions.Count; i++)
                {
                    var opt = playerLineOptions[i];
                    if (opt == null)
                        continue;

                    result.Add(new DialoguePlayerLineOptionDto
                    {
                        playerLineId = opt.playerLineId,
                        nextId = opt.nextId,
                        nextIds = ConvertIntList(opt.nextIds)
                    });
                }
            }
            return result;
        }

        private static List<int> ConvertIntList(Il2CppSystem.Collections.Generic.List<int> list)
        {
            var result = new List<int>();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    result.Add(list[i]);
                }
            }
            return result;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unnamed";

            char[] invalid = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            foreach (char c in invalid)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        #endregion

        #region DTO Classes

        private class DialogueNodeDto
        {
            public int id;
            public string speaker;
            public int baseWeight;
            public List<int> triggerTypes;
            public int lineId;
            public string text;
            public string emotion;
            public float duration;
            public int actionType;
            public int nextStateType;
            public float nextStateDuration;
            public string soundId;
            public DialogueConditionDto conditions;
            public List<DialogueOptionDto> options;
            public int nextId;
            public List<string> playerStates;
            public string playerLineInteraction;
            public List<DialoguePlayerLineOptionDto> playerLineOptions;
        }

        private class DialogueConditionDto
        {
            public string timeRangeStart;
            public string timeRangeEnd;
            public string dateMMdd;
        }

        private class DialogueOptionDto
        {
            public string text;
            public int nextId;
        }

        private class DialoguePlayerLineOptionDto
        {
            public int playerLineId;
            public int nextId;
            public List<int> nextIds;
        }

        private class PlayerLineEntryDto
        {
            public int id;
            public int LineID;
            public int groupId;
            public List<string> playerStates;
            public string text;
            public int viewLimit;
        }

        private class DialogueLineEntryDto
        {
            public int id;
            public string text;
            public string soundId;
        }

        #endregion
    }
}
