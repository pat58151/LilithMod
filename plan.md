# Implementation Plan: Lilith Dialogue Database Dumper Plugin

## 1. Project Structure & Build Configuration
- **Project name**: `LilithDatabaseDumper`
- **Output**: `<game>\BepInEx\plugins\LilithDatabaseDumper.dll`
- **Assembly references**:
  - `<game>\BepInEx\core\BepInEx.Core.dll`
  - `<game>\BepInEx\core\BepInEx.Unity.IL2CPP.dll`
  - `<game>\BepInEx\core\Il2CppInterop.Runtime.dll`
  - `<game>\BepInEx\core\0Harmony.dll`
  - `<game>\BepInEx\interop\Assembly-CSharp.dll`
  - `<game>\BepInEx\interop\Il2Cppmscorlib.dll`
  - `<game>\BepInEx\interop\UnityEngine.dll`
  - `<game>\BepInEx\interop\UnityEngine.CoreModule.dll`
- **NuGet dependencies**: `Newtonsoft.Json` (latest stable) – copy local into output.
- **Target framework**: `netstandard2.1`
- **Build command**: `dotnet build -c Release`; post-build copy to plugins folder can be manual or defined by output path directly.
- **Source files**:
  - `LilithDatabaseDumperPlugin.cs`
  - `DumpDatabaseBehaviour.cs`
  - `DtoModels/ ` (optional internal classes)

## 2. Plugin Entry Point (`LilithDatabaseDumperPlugin`)
- Inherits from `BepInEx.Unity.IL2CPP.BasePlugin`.
- `public override void Load()`:
  - Log a distinctive banner using `Log.LogInfo("[LilithDatabaseDumper] Loaded.")`.
  - Create a persistent `GameObject` named `"LilithDatabaseDumper"` and call `Object.DontDestroyOnLoad`.
  - Attach a `DumpDatabaseBehaviour` component to that `GameObject`.
  - **Do not** attempt to access any game API (DialogueManager, databases) here; they are not yet initialized.

## 3. Deferred Dump Behaviour (`DumpDatabaseBehaviour : MonoBehaviour`)
### Lifecycle
- `Awake()` / `Start()`: Invoke a coroutine `WaitAndDump()`.
- `WaitAndDump()` coroutine:
  - Suspend for a short initial delay (e.g., 1 second) to allow scene loading.
  - Enter a polling loop:
    - If `DialogueManager.Instance` is not null, break.
    - Yield return `null` (wait one frame).
  - If `DialogueManager.Instance` remains null after a configurable **timeout** (e.g., 30 seconds), log a warning (`"DialogueManager not found within timeout"`) and self-destroy (`Destroy(gameObject)`), yield break.
  - Once singleton exists, call `PerformDump()`.
  - After `PerformDump()`, destroy this component and its GameObject (`Destroy(gameObject)`).

### Dump Orchestration – `PerformDump()`
- Create the output directory: `<game>\BepInEx\plugins\LilithMod\dump\` (auto-create if missing).
- Initialize three counters: `totalNodes = 0`, `totalPlayerLines = 0`, `totalDialogueLines = 0`.
- Use `try-catch` for each database type separately so one failure does not abort others.

#### 3.1 Dialogue Nodes
- Obtain `DialogueManager.Instance._databases` (type: `Il2CppReferenceArray<DialogueDatabase>`).
- Iterate over each `DialogueDatabase db`.
- For each `db`, iterate `db.nodes`.
  - Skip if node is null.
  - Increment `totalNodes`.
  - Build a serializable DTO with fields:
    - `id`, `speaker`, `baseWeight`, `triggerTypes` (convert enum list to integer names), `lineId`, `text`, `emotion`, `duration`, `actionType`, `nextStateType`, `nextStateDuration`, `soundId`, `conditions` (null if not serializable), `options` (list of `{text, nextId}`), `nextId`, `playerStates`, `playerLineInteraction`, `playerLineOptions` (list of `{playerLineId, nextId, nextIds}`).
- Serialize the DTO list to JSON and write to file `dialogue_nodes_{db.databaseName}.json` (sanitize filename). If `db.nodes` is empty, write empty array.

#### 3.2 Player Line Database
- Call `DialogueManager.Instance.GetPlayerLineDatabase()`.
- If null, log warning and skip.
- Iterate `entries` list.
  - Skip null entries.
  - Increment `totalPlayerLines`.
  - DTO fields: `id`, `LineID`, `groupId`, `playerStates`, `text`, `viewLimit`.
- Write to `player_lines.json`.

#### 3.3 Dialogue Line Database
- Use `Resources.FindObjectsOfTypeAll<DialogueLineDatabase>()` to get all instances. Assume we want the first (usually the only one).
- If none found, log warning and skip.
- Iterate `entries` list.
  - Skip null.
  - Increment `totalDialogueLines`.
  - DTO fields: `id`, `text`, `soundId`.
- Write to `dialogue_lines.json`.

### 3.4 Final Logging
- Log `Log.LogInfo($"[LilithDatabaseDumper] Dump complete. Nodes: {totalNodes}, PlayerLines: {totalPlayerLines}, DialogueLines: {totalDialogueLines}")`.
- If any database was missing, log the corresponding warning.

## 4. Error & Edge-Case Handling
- **Null DialogueManager after timeout**: Log warning, destroy behaviour – game continues.
- **Null database fields**: `Il2CppReferenceArray` or `List` may be null; treat as empty.
- **Null entries inside lists**: Skip and log a debug message.
- **File I/O exceptions**: Catch and log error, do not prevent other databases from dumping.
- **Unhandled exceptions** in `PerformDump()`: Wrap entire method in `try-catch` to log and prevent crash; game must not be affected.
- **Multiple DialogueDatabase instances**: Each is written to a separate file; no overwriting.
- **Filename sanitization**: Replace invalid characters (e.g., `/`, `\`, `:`) in `db.databaseName`.

## 5. Acceptance Criteria / DoD Verification
1. Build: `dotnet build -c Release` succeeds with zero errors; DLL appears in plugins folder.
2. Game launch: BepInEx log shows `[LilithDatabaseDumper] Loaded.` and after dump, the line with exact counts.
3. Dump files exist under `BepInEx\plugins\LilithMod\dump\`:
   - `dialogue_nodes_{dbName}.json` for each database present.
   - `player_lines.json`
   - `dialogue_lines.json`
4. JSON content validity: Contains arrays of objects with non-null `id` and non-empty `text` (where applicable). Sample nodes extracted from API defined types.
5. Absence handling: If DialogueManager is never created (timeout), only a warning logged; no crash. If any database missing, file not created but other dumps proceed.
6. No game hang, crash, or noticeable performance impact; dump completes within a few frames after finding singleton.