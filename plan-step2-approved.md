## Implementation Plan: JSON-driven Custom Dialogue Injection for LilithMod

### 1. Overview
Extend the existing `LilithMod` BepInEx plugin with a custom content injection system.  
The plugin will auto-load JSON files from `<plugins>/LilithMod/custom/` and:

1. **Expand a simplified authoring format** into the real game schema (DialogueNode, PlayerLineEntry, DialoguePlayerLineOption, etc.).
2. **Inject verbatim any raw content** matching the game’s exact schema.
3. **Allocate safe IDs**, index injected data into the game’s databases, and verify injection.

**Key design decision:** The game loads `DialogueLineDatabase` on demand via Addressables per locale; no instance exists at startup. Therefore custom nodes **do not** create `DialogueLineEntry` records. Instead, they set `lineId = 0` and rely on the inline `text` field for display. This is the simplest approach and avoids dangling references. If inline text fails to render (the primary unverified runtime risk), the fallback is to load the active locale’s `DialogueLineDatabase` via Addressables and inject line entries there. That fallback is **not implemented now** – it is only noted as a contingency.

Injection runs after the existing database dump (on the same polling component), ensuring the game’s native data is already loaded.

### 2. Authoring Format Specification

**File discovery:** Every `*.json` file inside `custom/` folder will be loaded.  
**Encoding:** UTF-8.

**Root schema:**
```json
{
  "nodes": [ ... ],
  "raw": { "nodes": [ ... ], "playerLines": [ ... ] }
}
```

All top-level keys are optional; missing keys are equivalent to empty arrays.

#### 2.1 Simplified `nodes`

| Field        | Required | Type            | Description |
|--------------|----------|-----------------|-------------|
| `key`        | Yes      | string          | Unique stable identifier across all custom files. Used by `goto`. |
| `trigger`    | No       | string          | Exact name of `DialogueTriggerType` enum (e.g. `"TouchHand"`). Omitted triggers are only reachable via `goto`/`reply`. |
| `say`        | Yes      | string          | The line Lilith speaks. |
| `emotion`    | No       | string          | Emotion identifier. Default: empty string. |
| `duration`   | No       | number (float)  | Display duration. Default: `3.0` if omitted. |
| `weight`     | No       | integer         | Base weight for weighted random selection. Default: `1`. |
| `choices`    | No       | array           | Player choices. If empty or absent, the node has no player interaction. |

**Choice object:**

| Field   | Required | Type   | Description |
|---------|----------|--------|-------------|
| `text`  | Yes      | string | Player line text shown in UI. |
| `reply` | No       | string | Lilith’s response. Plugin will generate a one-shot follow‑up node containing this line. Mutually exclusive with `goto`. |
| `goto`  | No       | string | `key` of another custom node to branch to. Mutually exclusive with `reply`. |

**Choice validation:**  
- Every choice must have exactly one of `reply` or `goto`. If none or both → warn and skip that choice.  
- `goto` target must resolve to an existing `key` defined in any custom file. Unresolved → warn and skip that choice.

#### 2.2 Raw passthrough `raw`

`raw.nodes` and `raw.playerLines` are optional arrays containing objects that **exactly** match the game’s schema as found in the reference dumps.  
- `raw.nodes` → array of `DialogueNode` objects (all fields present).  
- `raw.playerLines` → array of `PlayerLineEntry` objects.  

These objects are injected **verbatim** into the game databases with **no expansion**. The plugin will only check for ID collisions (see §3.1) and skip any object that would collide.

### 3. ID Allocation & Collision Prevention

#### 3.1 Safe base ranges and counters
- **DialogueNode `.id`** : ≥ 9 000 000  
- **PlayerLineEntry `.id`** : ≥ 900 000  
- **PlayerLineEntry `.LineID`** : ≥ 9 000 000  

Before allocation, scan **all existing entries** in the live databases (`DialogueDatabase` and `PlayerLineDatabase`) and collect the maximum used ID in each category. The actual starting ID is `max(existing_max, base - 1) + 1`. Keep separate counters for each ID category.

**Custom node `lineId`** is always **0** (no dynamic line entry).  
**groupdId** for choices is **derived** from the owning node’s `id` (same for all choices of that node). No separate counter.

#### 3.2 Collision handling
- Maintain a **global set** of all assigned IDs (node id, playerLineId, LineID).  
- For **expanded** content, allocate IDs sequentially, incrementing the counter and checking against the set.  
- For **raw** content, check the provided IDs against the same global set and existing game IDs. If any ID already exists → warn and skip the entire object.

### 4. Expansion Logic (Simplified `nodes` → Game Objects)

The entire process is applied **after** all `.json` files have been parsed and key uniqueness validated.

1. **Build key‑to‑node mapping.** Validate that every `key` is unique across all loaded files. If duplicates are found, the second occurrence is skipped with a warning.  
2. For each simplified node entry:  
   a) **Allocate a node id** (`nodeId`). No separate line id.  
   b) Create a `DialogueNode` instance and set all fields (see §5). `lineId` = 0.  
   c) If `choices` are present and non‑empty → process each choice (see §4.1).  
   d) Store mapping `key → nodeId` for later `goto` resolution.  
   e) Add the constructed node and any generated player‑line entries to injection lists.

#### 4.1 Processing Choices
For each valid choice, the plugin creates **one** `PlayerLineEntry` and **one** `DialoguePlayerLineOption`.  

1. **Allocate** a player line id (`plId`) and a `LineID` (using PlayerLineEntry counters).  
2. **Create `PlayerLineEntry`:**
   - `id` = `plId`, `LineID` = `plLineId` (allocated), `groupId` = the **owning node’s assigned id**.  
   - `text` = choice’s `text`.  
   - `playerStates` = empty list.  
   - `viewLimit` = 0 (unlimited).  
3. **Resolve the target node id (`nextNodeId`)**:  
   - If `reply` → generate a **new** reply node (with allocated id) containing the reply text, no choices, no trigger.  
   - If `goto` → look up the target node’s id from the key‑to‑node map.  
4. **Create `DialoguePlayerLineOption`:**
   - `playerLineId` = `plId`.  
   - `nextId` = `nextNodeId`.  
   - `nextIds` = new `Il2CppSystem.Collections.Generic.List<int>` containing `[nextNodeId]`.  
5. **Append to node’s `playerLineOptions` list and build the `playerLineInteraction` string** (format: `"<plId>-<nextNodeId>"`, comma‑separated for all choices).  

#### 4.2 Reply Node Generation
A reply node is a **one‑shot, non‑triggerable** `DialogueNode` containing the reply text.  
- It receives a new `nodeId` and `lineId` = 0.  
- `triggerTypes` empty list.  
- No `choices`, no `playerLineOptions`, `playerLineInteraction` empty.  
- The node is injected into the same database and has no associated custom key.  
- No `DialogueLineEntry` is created.

### 5. Creation of Game‑Native Objects (Il2Cpp Interop)

All lists assigned to game fields must be `Il2CppSystem.Collections.Generic.List<T>`. Use the interop to instantiate them (e.g., `new Il2CppSystem.Collections.Generic.List<DialogueOption>()`).  
**Constructed objects (DialogueNode, DialoguePlayerLineOption, PlayerLineEntry) are created using their parameterless constructors** (the game’s internal IL2CPP types).

**DialogueNode fields:**
- `id` – allocated.  
- `lineId` – **0** (constant).  
- `speaker` – `"lilith"` (constant).  
- `baseWeight` – from `weight` (default 1).  
- `triggerTypes` – convert the `trigger` enum name to a `List<DialogueTriggerType>`. If `trigger` omitted, empty list.  
- `text` – the `say` string.  
- `emotion` – from `emotion` (default `""`).  
- `duration` – from `duration` (default 3.0).  
- `actionType` – `-1` (none).  
- `nextStateType` – `0` (none).  
- `nextStateDuration` – `0f`.  
- `soundId` – empty string.  
- `conditions` – a new `DialogueCondition` with `timeRangeStart = "00:00"`, `timeRangeEnd = "23:59"`, `dateMMdd = ""`.  
- `options` – empty `List<DialogueOption>`.  
- `nextId` – `-1`.  
- `playerStates` – empty `List<string>`.  
- `playerLineInteraction` – constructed string or empty.  
- `playerLineOptions` – populated list or empty.  

**For raw passthrough nodes:** create them exactly as provided in JSON, deserializing into the same fields, with all lists properly cast to Il2Cpp types. The raw node must supply all values (including `lineId`); the plugin does **not** override `lineId` to 0 for raw content.

### 6. Injection into Game Databases

The plugin must locate the correct `DialogueDatabase`:
- Iterate `DialogueManager.s_instance._databases`.  
- Pick the database whose `databaseName == "DialogueNode"`.  
- If not found, fall back to the first non‑null database, logging which was chosen.

**Steps:**
1. **Add each generated `DialogueNode`** to the chosen database’s `nodes` list (an `Il2CppSystem.Collections.Generic.List<DialogueNode>`).  
2. **Add each `PlayerLineEntry`** to `DialogueManager.s_instance.GetPlayerLineDatabase().entries`.  
3. **Call `db.BuildIndex()`**, **`playerDb.BuildIndex()`**, and **`DialogueManager.s_instance.BuildIndex()`** to rebuild the aggregated index.  
4. **For each injected `DialogueNode`**, call `DialogueManager.s_instance.RegisterNodeWeight(node)`.  

**Important:** There is no injection into `DialogueLineDatabase` because custom nodes use `lineId = 0` and rely on the inline `text` field.

### 7. Error Handling & Resilience

- The whole process runs inside a `try` block; any unhandled exception is caught, logged as error, and the plugin proceeds to the end (destroying the component). The game remains unaffected.  
- **Bad JSON file** → log warning, skip file.  
- **Missing `key` or `say`** → log warning, skip that node.  
- **Unknown trigger name** → log warning (`$"Unknown trigger '{triggerName}' in node key '{key}'"`), skip node.  
- **Invalid choice** (missing `text`, ambiguous `reply`/`goto`, unresolved `goto`) → log warning, skip that choice, but the node may still be injected with remaining valid choices.  
- **Raw content with colliding IDs** → log warning with the conflicting ID, skip the raw object.  
- Any skipped entry does not block the injection of other valid entries.

### 8. Logging

All logging goes through `LilithModPlugin.Logger`.  
- **Info**: number of custom files found, summary of items injected (`X` nodes, `Y` player lines).  
- **Warning**: every skipped/error case as described above, with specific details.  
- **Self‑check**: after injection, pick one injected node (e.g., the first expanded node) and log whether `DialogueManager.s_instance.TryGetNode(nodeId, out _)` succeeds. This serves as a runtime verification.

### 9. Integration with Existing Code

The injection logic will be placed inside `DumpDatabaseBehaviour`.  
- The class will keep its `Awake/Update` polling for `DialogueManager.s_instance`.  
- After the existing `PerformDump()` finishes successfully (or if dump fails, still attempt injection), call a new `PerformInjection()` method.  
- `PerformInjection()` implements all the steps above.  
- Once both dump and injection are complete (or after a hard timeout if required), `Destroy(this)` is called to clean up.

No new GameObject or manual scene management is needed; BepInEx’s own `AddComponent` mechanism is reused.

### 10. Acceptance Criteria

1. **Build**: `dotnet build -c Release` completes with zero errors.  
2. **Functional injection**:  
   - Place a valid `custom/sample.json` containing at least one node with `key` and a choice with `reply`.  
   - After the game loads, BepInEx log shows:
     - “Loaded X custom files”  
     - “Injected Y dialogue nodes, Z player line entries”  
     - “Self-check: node <id> reachable = True”  
   - No warnings for that sample.  
3. **Inline text rendering risk**: This is the primary unverified runtime behaviour. **In-game observation must confirm** that custom nodes with `lineId=0` display the inline `text` correctly. If they render blank, the fallback is to load the active locale’s `DialogueLineDatabase` via Addressables and inject `DialogueLineEntry` objects. That fallback is **not implemented at this stage**, but the plan acknowledges it.
4. **Error resilience**:  
   - Without the `custom/` folder → log “No custom folder found” and game runs as before.  
   - With a malformed JSON file → log warning, skip file, remaining valid files are injected.  
   - With a node using unknown trigger → log warning, that node skipped, others injected.  
   - With a raw node having an ID already in use → warning, raw node skipped.  
   - In all error cases, vanilla dialogue is fully functional (no crashes).  
5. **Self‑check**: `DialogueManager.s_instance.TryGetNode` returns `true` for at least one injected node ID. This is confirmed via the log.  
6. **No performance degradation**: injection happens once on startup; it does not run every frame and does not leak memory.

### 11. Dependencies & References

- Existing `LilithModPlugin` and `DumpDatabaseBehaviour` (modified).  
- `Il2CppInterop.Runtime` for creating IL2CPP collections.  
- `Newtonsoft.Json` (already included via NuGet) for JSON parsing.  
- Game assemblies: `DialogueManager`, `DialogueDatabase`, `DialogueNode`, `PlayerLineDatabase`, etc. (referenced from interop DLLs).