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

        private bool _tickLogged;

        public void Awake()
        {
            _elapsed = 0f;
            _done = false;
            LilithModPlugin.Logger.LogInfo("[DIAG] DumpDatabaseBehaviour.Awake reached.");
        }

        public void Update()
        {
            if (!_tickLogged)
            {
                _tickLogged = true;
                LilithModPlugin.Logger.LogInfo("[DIAG] DumpDatabaseBehaviour.Update first tick.");
            }

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

                try
                {
                    PerformInjection();
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogError($"[LilithMod] Unhandled exception during injection: {ex}");
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

        #region Injection Logic

        private void PerformInjection()
        {
            string pluginDir = Path.GetDirectoryName(typeof(LilithModPlugin).Assembly.Location);
            string customDir = Path.Combine(pluginDir, "custom");

            if (!Directory.Exists(customDir))
            {
                LilithModPlugin.Logger.LogInfo("[LilithMod] No custom folder found; skipping injection.");
                return;
            }

            var jsonFiles = Directory.GetFiles(customDir, "*.json", SearchOption.TopDirectoryOnly);
            if (jsonFiles.Length == 0)
            {
                LilithModPlugin.Logger.LogInfo("[LilithMod] No JSON files in custom folder; skipping injection.");
                return;
            }

            LilithModPlugin.Logger.LogInfo($"[LilithMod] Loaded {jsonFiles.Length} custom file(s).");

            // Parse all files
            var allSimplifiedNodes = new List<SimplifiedNode>();
            var allRawNodes = new List<RawNodeEntry>();
            var allRawPlayerLines = new List<RawPlayerLineEntry>();
            var keyToSimplifiedNode = new Dictionary<string, SimplifiedNode>();

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    string jsonText = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                    var customFile = JsonConvert.DeserializeObject<CustomFileFormat>(jsonText);
                    if (customFile == null)
                    {
                        LilithModPlugin.Logger.LogWarning($"[LilithMod] Failed to parse {Path.GetFileName(filePath)}; skipping file.");
                        continue;
                    }

                    if (customFile.nodes != null)
                    {
                        foreach (var node in customFile.nodes)
                        {
                            if (string.IsNullOrEmpty(node.key))
                            {
                                LilithModPlugin.Logger.LogWarning($"[LilithMod] Node in {Path.GetFileName(filePath)} has empty key; skipping node.");
                                continue;
                            }
                            if (string.IsNullOrEmpty(node.say))
                            {
                                LilithModPlugin.Logger.LogWarning($"[LilithMod] Node '{node.key}' in {Path.GetFileName(filePath)} has empty 'say'; skipping node.");
                                continue;
                            }
                            if (keyToSimplifiedNode.ContainsKey(node.key))
                            {
                                LilithModPlugin.Logger.LogWarning($"[LilithMod] Duplicate key '{node.key}' in {Path.GetFileName(filePath)}; skipping node.");
                                continue;
                            }
                            keyToSimplifiedNode[node.key] = node;
                            allSimplifiedNodes.Add(node);
                        }
                    }

                    if (customFile.raw != null)
                    {
                        if (customFile.raw.nodes != null)
                        {
                            allRawNodes.AddRange(customFile.raw.nodes);
                        }
                        if (customFile.raw.playerLines != null)
                        {
                            allRawPlayerLines.AddRange(customFile.raw.playerLines);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[LilithMod] Bad JSON in {Path.GetFileName(filePath)}: {ex.Message}; skipping file.");
                }
            }

            if (allSimplifiedNodes.Count == 0 && allRawNodes.Count == 0 && allRawPlayerLines.Count == 0)
            {
                LilithModPlugin.Logger.LogInfo("[LilithMod] No valid content to inject.");
                return;
            }

            // Collect existing IDs
            var existingNodeIds = new HashSet<int>();
            var existingPlayerLineIds = new HashSet<int>();
            var existingLineIds = new HashSet<int>();
            var globalAssignedIds = new HashSet<int>();

            // Scan existing dialogue databases
            var databases = DialogueManager.s_instance._databases;
            DialogueDatabase targetDb = null;

            if (databases != null)
            {
                for (int i = 0; i < databases.Length; i++)
                {
                    var db = databases[i];
                    if (db == null)
                        continue;

                    // Pick the one named "DialogueNode" or fall back to first
                    if (targetDb == null)
                        targetDb = db;
                    if (db.databaseName == "DialogueNode")
                    {
                        targetDb = db;
                    }

                    var nodes = db.nodes;
                    if (nodes != null)
                    {
                        for (int j = 0; j < nodes.Count; j++)
                        {
                            var node = nodes[j];
                            if (node != null)
                            {
                                existingNodeIds.Add(node.id);
                                globalAssignedIds.Add(node.id);
                            }
                        }
                    }
                }
            }

            if (targetDb == null)
            {
                LilithModPlugin.Logger.LogError("[LilithMod] No dialogue database found; cannot inject.");
                return;
            }
            LilithModPlugin.Logger.LogInfo($"[LilithMod] Using dialogue database: '{targetDb.databaseName}'");

            // Scan existing player line database
            var playerDb = DialogueManager.s_instance.GetPlayerLineDatabase();
            if (playerDb != null && playerDb.entries != null)
            {
                for (int i = 0; i < playerDb.entries.Count; i++)
                {
                    var entry = playerDb.entries[i];
                    if (entry != null)
                    {
                        existingPlayerLineIds.Add(entry.id);
                        existingLineIds.Add(entry.LineID);
                        globalAssignedIds.Add(entry.id);
                        globalAssignedIds.Add(entry.LineID);
                    }
                }
            }
            else
            {
                LilithModPlugin.Logger.LogError("[LilithMod] PlayerLineDatabase not available; cannot inject player lines.");
                return;
            }

            // Compute base IDs
            const int BaseNodeId = 9000000;
            const int BasePlayerLineId = 900000;
            const int BaseLineId = 9000000;

            int nextNodeId = Math.Max(GetMaxOrMin(existingNodeIds, BaseNodeId - 1), BaseNodeId - 1) + 1;
            int nextPlayerLineId = Math.Max(GetMaxOrMin(existingPlayerLineIds, BasePlayerLineId - 1), BasePlayerLineId - 1) + 1;
            int nextLineId = Math.Max(GetMaxOrMin(existingLineIds, BaseLineId - 1), BaseLineId - 1) + 1;

            // We also need to track IDs that we allocate during this session
            int AllocateNodeId()
            {
                while (globalAssignedIds.Contains(nextNodeId))
                    nextNodeId++;
                int id = nextNodeId;
                globalAssignedIds.Add(id);
                nextNodeId++;
                return id;
            }

            int AllocatePlayerLineId()
            {
                while (globalAssignedIds.Contains(nextPlayerLineId))
                    nextPlayerLineId++;
                int id = nextPlayerLineId;
                globalAssignedIds.Add(id);
                nextPlayerLineId++;
                return id;
            }

            int AllocateLineId()
            {
                while (globalAssignedIds.Contains(nextLineId))
                    nextLineId++;
                int id = nextLineId;
                globalAssignedIds.Add(id);
                nextLineId++;
                return id;
            }

            var nodesToInject = new List<DialogueNode>();
            var playerLinesToInject = new List<PlayerLineEntry>();

            // Build key -> nodeId mapping for goto resolution
            var keyToNodeId = new Dictionary<string, int>();

            // First pass: create nodes (without goto resolution)
            var simplifiedNodeToId = new Dictionary<string, int>(); // key -> allocated nodeId
            var replyNodesToAdd = new List<DialogueNode>(); // reply nodes generated during choice processing

            foreach (var sn in allSimplifiedNodes)
            {
                int nodeId = AllocateNodeId();
                simplifiedNodeToId[sn.key] = nodeId;
                keyToNodeId[sn.key] = nodeId;
            }

            // Second pass: build game objects
            foreach (var sn in allSimplifiedNodes)
            {
                try
                {
                    int nodeId = simplifiedNodeToId[sn.key];

                    // Validate trigger if present
                    List<DialogueTriggerType> triggerList = null;
                    if (!string.IsNullOrEmpty(sn.trigger))
                    {
                        try
                        {
                            var triggerType = (DialogueTriggerType)Enum.Parse(typeof(DialogueTriggerType), sn.trigger);
                            triggerList = new List<DialogueTriggerType> { triggerType };
                        }
                        catch
                        {
                            LilithModPlugin.Logger.LogWarning($"[LilithMod] Unknown trigger '{sn.trigger}' in node key '{sn.key}'; skipping node.");
                            continue;
                        }
                    }

                    var node = new DialogueNode();
                    node.id = nodeId;
                    node.lineId = 0; // inline text approach
                    node.speaker = "lilith";
                    node.baseWeight = sn.weight > 0 ? sn.weight : 1;
                    node.text = sn.say ?? "";
                    node.emotion = sn.emotion ?? "";
                    node.duration = sn.duration > 0f ? sn.duration : 3.0f;
                    node.actionType = (LilithActionType)(-1);
                    node.nextStateType = (DialogueStateType)(0);
                    node.nextStateDuration = 0f;
                    node.soundId = "";
                    node.nextId = -1;
                    node.playerLineInteraction = "";
                    node.playerStates = new Il2CppSystem.Collections.Generic.List<string>();
                    node.options = new Il2CppSystem.Collections.Generic.List<DialogueOption>();
                    node.playerLineOptions = new Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption>();

                    // Trigger types
                    var ilTriggerTypes = new Il2CppSystem.Collections.Generic.List<DialogueTriggerType>();
                    if (triggerList != null)
                    {
                        foreach (var t in triggerList)
                            ilTriggerTypes.Add(t);
                    }
                    node.triggerTypes = ilTriggerTypes;

                    // Conditions
                    var conditions = new DialogueCondition();
                    conditions.timeRangeStart = "00:00";
                    conditions.timeRangeEnd = "23:59";
                    conditions.dateMMdd = "";
                    node.conditions = conditions;

                    // Process choices
                    if (sn.choices != null && sn.choices.Count > 0)
                    {
                        var choiceStrings = new List<string>();

                        foreach (var choice in sn.choices)
                        {
                            if (string.IsNullOrEmpty(choice.text))
                            {
                                LilithModPlugin.Logger.LogWarning($"[LilithMod] Choice with empty text in node '{sn.key}'; skipping choice.");
                                continue;
                            }

                            bool hasReply = !string.IsNullOrEmpty(choice.reply);
                            bool hasGoto = !string.IsNullOrEmpty(choice.gotoKey);

                            if (hasReply == hasGoto)
                            {
                                LilithModPlugin.Logger.LogWarning($"[LilithMod] Choice in node '{sn.key}' must have exactly one of 'reply' or 'goto'; skipping choice.");
                                continue;
                            }

                            int? targetNodeId = null;

                            if (hasGoto)
                            {
                                if (!simplifiedNodeToId.TryGetValue(choice.gotoKey, out int gotoId))
                                {
                                    LilithModPlugin.Logger.LogWarning($"[LilithMod] Unresolved goto target '{choice.gotoKey}' in node '{sn.key}'; skipping choice.");
                                    continue;
                                }
                                targetNodeId = gotoId;
                            }
                            else if (hasReply)
                            {
                                // Generate reply node
                                int replyNodeId = AllocateNodeId();
                                targetNodeId = replyNodeId;

                                var replyNode = new DialogueNode();
                                replyNode.id = replyNodeId;
                                replyNode.lineId = 0;
                                replyNode.speaker = "lilith";
                                replyNode.baseWeight = 1;
                                replyNode.text = choice.reply ?? "";
                                replyNode.emotion = "";
                                replyNode.duration = 3.0f;
                                replyNode.actionType = (LilithActionType)(-1);
                                replyNode.nextStateType = (DialogueStateType)(0);
                                replyNode.nextStateDuration = 0f;
                                replyNode.soundId = "";
                                replyNode.nextId = -1;
                                replyNode.playerLineInteraction = "";
                                replyNode.playerStates = new Il2CppSystem.Collections.Generic.List<string>();
                                replyNode.options = new Il2CppSystem.Collections.Generic.List<DialogueOption>();
                                replyNode.playerLineOptions = new Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption>();
                                replyNode.triggerTypes = new Il2CppSystem.Collections.Generic.List<DialogueTriggerType>();

                                var replyConditions = new DialogueCondition();
                                replyConditions.timeRangeStart = "00:00";
                                replyConditions.timeRangeEnd = "23:59";
                                replyConditions.dateMMdd = "";
                                replyNode.conditions = replyConditions;

                                replyNodesToAdd.Add(replyNode);
                            }

                            if (targetNodeId.HasValue)
                            {
                                int plId = AllocatePlayerLineId();
                                int lineId = AllocateLineId();

                                // Create PlayerLineEntry
                                var plEntry = new PlayerLineEntry();
                                plEntry.id = plId;
                                plEntry.LineID = lineId;
                                plEntry.groupId = nodeId; // owning node's id
                                plEntry.text = choice.text;
                                plEntry.viewLimit = 0;
                                plEntry.playerStates = new Il2CppSystem.Collections.Generic.List<string>();
                                playerLinesToInject.Add(plEntry);

                                // Create DialoguePlayerLineOption
                                var option = new DialoguePlayerLineOption();
                                option.playerLineId = plId;
                                option.nextId = targetNodeId.Value;
                                var nextIdsList = new Il2CppSystem.Collections.Generic.List<int>();
                                nextIdsList.Add(targetNodeId.Value);
                                option.nextIds = nextIdsList;
                                node.playerLineOptions.Add(option);

                                choiceStrings.Add($"{plId}-{targetNodeId.Value}");
                            }
                        }

                        node.playerLineInteraction = string.Join(",", choiceStrings);
                    }

                    nodesToInject.Add(node);
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[LilithMod] Error expanding node '{sn.key}': {ex.Message}; skipping node.");
                }
            }

            // Add reply nodes to the injection list
            nodesToInject.AddRange(replyNodesToAdd);

            // Process raw nodes
            if (allRawNodes.Count > 0)
            {
                foreach (var rawNode in allRawNodes)
                {
                    try
                    {
                        int rawId = rawNode.id;
                        if (globalAssignedIds.Contains(rawId))
                        {
                            LilithModPlugin.Logger.LogWarning($"[LilithMod] Raw node id {rawId} already exists; skipping raw node.");
                            continue;
                        }
                        globalAssignedIds.Add(rawId);

                        var node = new DialogueNode();
                        node.id = rawNode.id;
                        node.speaker = rawNode.speaker ?? "lilith";
                        node.baseWeight = rawNode.baseWeight;
                        node.lineId = rawNode.lineId;
                        node.text = rawNode.text ?? "";
                        node.emotion = rawNode.emotion ?? "";
                        node.duration = rawNode.duration;
                        node.actionType = (LilithActionType)rawNode.actionType;
                        node.nextStateType = (DialogueStateType)rawNode.nextStateType;
                        node.nextStateDuration = rawNode.nextStateDuration;
                        node.soundId = rawNode.soundId ?? "";
                        node.nextId = rawNode.nextId;
                        node.playerLineInteraction = rawNode.playerLineInteraction ?? "";

                        // Trigger types
                        var ilTriggerTypes = new Il2CppSystem.Collections.Generic.List<DialogueTriggerType>();
                        if (rawNode.triggerTypes != null)
                        {
                            foreach (var t in rawNode.triggerTypes)
                                ilTriggerTypes.Add((DialogueTriggerType)t);
                        }
                        node.triggerTypes = ilTriggerTypes;

                        // Conditions
                        var conditions = new DialogueCondition();
                        if (rawNode.conditions != null)
                        {
                            conditions.timeRangeStart = rawNode.conditions.timeRangeStart ?? "00:00";
                            conditions.timeRangeEnd = rawNode.conditions.timeRangeEnd ?? "23:59";
                            conditions.dateMMdd = rawNode.conditions.dateMMdd ?? "";
                        }
                        else
                        {
                            conditions.timeRangeStart = "00:00";
                            conditions.timeRangeEnd = "23:59";
                            conditions.dateMMdd = "";
                        }
                        node.conditions = conditions;

                        // Options
                        var ilOptions = new Il2CppSystem.Collections.Generic.List<DialogueOption>();
                        if (rawNode.options != null)
                        {
                            foreach (var optDto in rawNode.options)
                            {
                                var opt = new DialogueOption();
                                opt.text = optDto.text ?? "";
                                opt.nextId = optDto.nextId;
                                ilOptions.Add(opt);
                            }
                        }
                        node.options = ilOptions;

                        // Player states
                        var ilPlayerStates = new Il2CppSystem.Collections.Generic.List<string>();
                        if (rawNode.playerStates != null)
                        {
                            foreach (var s in rawNode.playerStates)
                                ilPlayerStates.Add(s ?? "");
                        }
                        node.playerStates = ilPlayerStates;

                        // Player line options
                        var ilPlayerLineOptions = new Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption>();
                        if (rawNode.playerLineOptions != null)
                        {
                            foreach (var plOptDto in rawNode.playerLineOptions)
                            {
                                var plOpt = new DialoguePlayerLineOption();
                                plOpt.playerLineId = plOptDto.playerLineId;
                                plOpt.nextId = plOptDto.nextId;
                                var ilNextIds = new Il2CppSystem.Collections.Generic.List<int>();
                                if (plOptDto.nextIds != null)
                                {
                                    foreach (var nid in plOptDto.nextIds)
                                        ilNextIds.Add(nid);
                                }
                                plOpt.nextIds = ilNextIds;
                                ilPlayerLineOptions.Add(plOpt);
                            }
                        }
                        node.playerLineOptions = ilPlayerLineOptions;

                        nodesToInject.Add(node);
                    }
                    catch (Exception ex)
                    {
                        LilithModPlugin.Logger.LogWarning($"[LilithMod] Error injecting raw node: {ex.Message}; skipping.");
                    }
                }
            }

            // Process raw player lines
            if (allRawPlayerLines.Count > 0)
            {
                foreach (var rawPl in allRawPlayerLines)
                {
                    try
                    {
                        int rawId = rawPl.id;
                        int rawLineId = rawPl.LineID;

                        if (globalAssignedIds.Contains(rawId) || globalAssignedIds.Contains(rawLineId))
                        {
                            LilithModPlugin.Logger.LogWarning($"[LilithMod] Raw player line id {rawId} or LineID {rawLineId} already exists; skipping raw player line.");
                            continue;
                        }
                        globalAssignedIds.Add(rawId);
                        globalAssignedIds.Add(rawLineId);

                        var entry = new PlayerLineEntry();
                        entry.id = rawPl.id;
                        entry.LineID = rawPl.LineID;
                        entry.groupId = rawPl.groupId;
                        entry.text = rawPl.text ?? "";
                        entry.viewLimit = rawPl.viewLimit;

                        var ilPlayerStates = new Il2CppSystem.Collections.Generic.List<string>();
                        if (rawPl.playerStates != null)
                        {
                            foreach (var s in rawPl.playerStates)
                                ilPlayerStates.Add(s ?? "");
                        }
                        entry.playerStates = ilPlayerStates;

                        playerLinesToInject.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        LilithModPlugin.Logger.LogWarning($"[LilithMod] Error injecting raw player line: {ex.Message}; skipping.");
                    }
                }
            }

            int totalInjectedNodes = nodesToInject.Count;
            int totalInjectedPlayerLines = playerLinesToInject.Count;

            // Inject into databases
            if (totalInjectedNodes > 0)
            {
                var targetNodes = targetDb.nodes;
                if (targetNodes == null)
                {
                    targetNodes = new Il2CppSystem.Collections.Generic.List<DialogueNode>();
                    targetDb.nodes = targetNodes;
                }

                foreach (var node in nodesToInject)
                {
                    targetNodes.Add(node);
                }

                targetDb.BuildIndex();
            }

            if (totalInjectedPlayerLines > 0)
            {
                var playerEntries = playerDb.entries;
                if (playerEntries == null)
                {
                    playerEntries = new Il2CppSystem.Collections.Generic.List<PlayerLineEntry>();
                    playerDb.entries = playerEntries;
                }

                foreach (var pl in playerLinesToInject)
                {
                    playerEntries.Add(pl);
                }

                playerDb.BuildIndex();
            }

            DialogueManager.s_instance.BuildIndex();

            // Register weights
            foreach (var node in nodesToInject)
            {
                DialogueManager.s_instance.RegisterNodeWeight(node);
            }

            LilithModPlugin.Logger.LogInfo($"[LilithMod] Injected {totalInjectedNodes} dialogue nodes, {totalInjectedPlayerLines} player line entries.");

            // Self-check. Must sample a node that was actually injected, not merely
            // allocated an id: ids are assigned in a first pass before validation, so a
            // node rejected later (unknown trigger, etc.) burns its id and would report
            // unreachable even though injection succeeded.
            if (nodesToInject.Count > 0)
            {
                int sampleNodeId = nodesToInject[0].id;
                DebugProbe.FirstInjectedNodeId = sampleNodeId;
                bool found = DialogueManager.s_instance.TryGetNode(sampleNodeId, out _);
                LilithModPlugin.Logger.LogInfo($"[LilithMod] Self-check: node {sampleNodeId} reachable = {found}");
                if (!found)
                {
                    LilithModPlugin.Logger.LogWarning("[LilithMod] Self-check FAILED - injected nodes are not in the live index.");
                }
            }
        }

        private static int GetMaxOrMin(HashSet<int> set, int fallback)
        {
            if (set.Count == 0)
                return fallback;
            int max = fallback;
            foreach (var v in set)
            {
                if (v > max)
                    max = v;
            }
            return max;
        }

        #endregion

        #region Injection DTO Classes

        private class CustomFileFormat
        {
            public List<SimplifiedNode> nodes;
            public RawContent raw;
        }

        private class SimplifiedNode
        {
            public string key;
            public string trigger;
            public string say;
            public string emotion;
            public float duration = 3.0f;
            public int weight = 1;
            public List<SimplifiedChoice> choices;
        }

        private class SimplifiedChoice
        {
            public string text;
            public string reply;
            [JsonProperty("goto")]
            public string gotoKey;
        }

        private class RawContent
        {
            public List<RawNodeEntry> nodes;
            public List<RawPlayerLineEntry> playerLines;
        }

        private class RawNodeEntry
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
            public RawCondition conditions;
            public List<RawOption> options;
            public int nextId;
            public List<string> playerStates;
            public string playerLineInteraction;
            public List<RawPlayerLineOption> playerLineOptions;
        }

        private class RawCondition
        {
            public string timeRangeStart;
            public string timeRangeEnd;
            public string dateMMdd;
        }

        private class RawOption
        {
            public string text;
            public int nextId;
        }

        private class RawPlayerLineOption
        {
            public int playerLineId;
            public int nextId;
            public List<int> nextIds;
        }

        private class RawPlayerLineEntry
        {
            public int id;
            public int LineID;
            public int groupId;
            public List<string> playerStates;
            public string text;
            public int viewLimit;
        }

        #endregion

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
