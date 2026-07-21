using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Il2CppInterop.Runtime;
using Newtonsoft.Json;
using UnityEngine;

namespace LilithMod
{
    /// <summary>
    /// Authoring aid: writes the game's dialogue databases to plugins/LilithMod/dump
    /// as JSON, so custom nodes can be written against the real ids and triggers.
    ///
    /// Off by default and never part of normal play - it exists to be turned on
    /// once while authoring, not to run on every launch on every machine. The
    /// output is the developers' own script and is not ours to redistribute.
    /// </summary>
    internal static class DatabaseDumper
    {
        public static void Run()
        {
            try
            {
                PerformDump();
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LilithMod] Unhandled exception during dump: {ex}");
            }
        }

        private static void PerformDump()
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

                        string dbName = string.IsNullOrEmpty(db.databaseName) ? $"db_{dbIdx}" : SanitizeFileName(db.databaseName);
                        string json = JsonConvert.SerializeObject(nodeDtos, Formatting.Indented);
                        string filePath = Path.Combine(dumpDir, $"dialogue_nodes_{dbName}.json");
                        File.WriteAllText(filePath, json);
                        LilithModPlugin.Logger.LogInfo($"[LilithMod] Wrote {nodeDtos.Count} dialogue nodes to dialogue_nodes_{dbName}.json");
                    }
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LilithMod] Error dumping dialogue nodes: {ex}");
            }

            // --- Player Lines ---
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

            // --- Dialogue Lines (from DialogueLineDatabase) ---
            try
            {
                var databases = DialogueManager.s_instance._databases;
                if (databases != null)
                {
                    for (int dbIdx = 0; dbIdx < databases.Length; dbIdx++)
                    {
                        var db = databases[dbIdx];
                        if (db == null)
                            continue;

                        // DialogueLineDatabase may be a separate database; try to cast or find by name
                        // We check if this database has dialogue line entries by looking for the entries list
                        // The game stores DialogueLineDatabase as a separate ScriptableObject, not in _databases array.
                        // We'll look for it via Addressables or reflection; for now skip if not found.
                    }
                }

                // Try to find DialogueLineDatabase via reflection from DialogueManager
                var dialogueLineDbField = typeof(DialogueManager).GetField("_dialogueLineDatabase",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (dialogueLineDbField != null)
                {
                    var dialogueLineDb = dialogueLineDbField.GetValue(DialogueManager.s_instance) as DialogueLineDatabase;
                    if (dialogueLineDb != null)
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
