```markdown
# Implementation Plan: Lilith Dialogue Database Dumper Plugin (Fixed)

## 1. Project Structure & Build Configuration
- **Project name**: `LilithMod`  
- **Output DLL**: `<game>\BepInEx\plugins\LilithMod\LilithMod.dll`  
- **Dump output directory**: `<game>\BepInEx\plugins\LilithMod\dump\` (auto‑created on first dump)
- **Assembly references**:  
  - `<game>\BepInEx\core\BepInEx.Core.dll`  
  - `<game>\BepInEx\core\BepInEx.Unity.IL2CPP.dll`  
  - `<game>\BepInEx\core\Il2CppInterop.Runtime.dll`  
  - `<game>\BepInEx\core\0Harmony.dll`  
  - `<game>\BepInEx\interop\Assembly-CSharp.dll`  
  - `<game>\BepInEx\interop\Il2Cppmscorlib.dll`  
  - `<game>\BepInEx\interop\UnityEngine.dll`  
  - `<game>\BepInEx\interop\UnityEngine.CoreModule.dll`  
- **NuGet dependency**: `Newtonsoft.Json` (latest stable) – copy local.
- **Target framework**: `netstandard2.1`
- **Source files**:
  - `LilithModPlugin.cs` (entry point)
  - `DumpDatabaseBehaviour.cs` (MonoBehaviour that performs the dump)
  - `DtoModels/` (optional internal serializable DTO classes)

## 2. Plugin Entry Point (`LilithModPlugin`)
- Inherits from `BepInEx.Unity.IL2CPP.BasePlugin`.
- **`public override void Load()`**:
  1. Log banner: `Log.LogInfo("[LilithMod] Loaded.")`.
  2. **Register the managed component**:  
     Call `Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<DumpDatabaseBehaviour>();`  
     (using namespace `Il2CppInterop.Runtime.Injection`; type is in `Il2CppInterop.Runtime.dll`).
  3. Create a persistent `GameObject` named `"LilithMod"` and call `Object.DontDestroyOnLoad`.
  4. Attach `DumpDatabaseBehaviour` via `AddComponent<DumpDatabaseBehaviour>()`.
- **Do not** access any game API here.

## 3. Deferred Dump Behaviour (`DumpDatabaseBehaviour : MonoBehaviour`)
### IL2CPP Marshalling
- Must include the IL2CPP injection constructor:
  ```csharp
  public DumpDatabaseBehaviour(System.IntPtr ptr) : base(ptr) { }
  ```

### Lifecycle – Update‑Based Polling
- **`Awake()`**: Initialize `_elapsed = 0f`, `_done = false`, `_initialDelay = 1f`.
- **`Update()`** (called every frame):
  1. If `_done` return immediately.
  2. `_elapsed += Time.deltaTime`.
  3. If `_elapsed < _initialDelay` return.
  4. Check if `DialogueManager.s_instance != null` (the only valid singleton accessor).
  5. If it is **not** null:
     - Invoke `PerformDump()` synchronously.
     - `_done = true`.
     - `Destroy(gameObject)`.
     - Return.
  6. If it is **still** null after `_elapsed >= timeoutSeconds` (e.g., 30), log warning `"DialogueManager instance not found within timeout; aborting dump."`, set `_done = true`, `Destroy(gameObject)`.
- **Fields**:
  - `private float _elapsed;`
  - `private bool _done;`
  - `private readonly float _initialDelay = 1f;`
  - `private readonly float _timeout = 30f;`

### Dump Orchestration – `PerformDump()`
- Ensure output directory `<game>\BepInEx\plugins\LilithMod\dump\` exists (create if missing).
- Initialize counters `totalNodes = 0`, `totalPlayerLines = 0`, `totalDialogueLines = 0`.
- Each database type is handled in its own `try‑catch` block so a failure in one does not abort the others.

#### 3.1 Dialogue Nodes
- Get `DialogueManager.s_instance._databases` (`Il2CppReferenceArray<DialogueDatabase>`).
- If it is null, log a warning and skip.
- Iterate each `DialogueDatabase db`:
  - If `db == null` continue.
  - Create a list of serializable node DTOs from `db.nodes`:
    - For each `DialogueNode node` in `db.nodes`:  
      - Skip if node is null; increment `totalNodes`.  
      - Build DTO with fields:  
        `id`, `speaker`, `baseWeight`, `triggerTypes` (convert enum to integer list), `lineId`, `text`, `emotion`, `duration`, `actionType`, `nextStateType`, `nextStateDuration`, `soundId`, `conditions` (null if `node.conditions` is null, else an anonymous object with `timeRangeStart`, `timeRangeEnd`, `dateMMdd`), `options` (list of `{text, nextId}`), `nextId`, `playerStates`, `playerLineInteraction`, `playerLineOptions` (list of `{playerLineId, nextId, nextIds}`).  
  - Serialize the list of DTOs to JSON.
  - Write to file `dialogue_nodes_{db.databaseName}.json` (sanitize filename, replacing invalid path characters). If `db.nodes` is empty, write `[]`.
- Increment `totalNodes` as above.

#### 3.2 Player Line Database
- Call `DialogueManager.s_instance.GetPlayerLineDatabase()`.
- If null, log warning and skip.
- Access `entries` (`List<PlayerLineEntry>`):
  - Iterate each `PlayerLineEntry entry`:
    - Skip null entries. Increment `totalPlayerLines`.
    - DTO fields: `id`, `LineID`, `groupId`, `playerStates`, `text`, `viewLimit`.
- Write to `player_lines.json`.

#### 3.3 Dialogue Line Database
- Use `Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueLineDatabase>())` to obtain an array.  
  - Cast each element to `DialogueLineDatabase` and pick the first non‑null result (usually only one loaded).
- If none found, log `"[LilithMod] No DialogueLineDatabase found in memory – skipping dump."` at INFO level and skip.
- Otherwise, iterate `entries`:
  - Skip null entries. Increment `totalDialogueLines`.
  - DTO fields: `id`, `text`, `soundId`.
- Write to `dialogue_lines.json`.

### Final Log
- After all dumps (even if some were skipped), log:  
  `Log.LogInfo($"[LilithMod] Dump complete. Nodes: {totalNodes}, PlayerLines: {totalPlayerLines}, DialogueLines: {totalDialogueLines}")`.

## 4. Error & Edge‑Case Handling
- **Null DialogueManager after timeout**: Log warning, destroy behaviour, game continues.
- **`_databases` null or empty**: Handled per database.
- **Null entry inside lists**: Skip and count only valid entries; optionally log a debug message.
- **File I/O exceptions**: Catch and log error, do not stop other dumps.
- **Unhandled exceptions in `PerformDump()`**: Wrap entire method body in `try‑catch` to log and prevent any escape into the game loop.
- **Filename sanitization**: Replace invalid characters (`/`, `\`, `:`, `*`, `?`, `"`, `<`, `>`, `|`) in `db.databaseName` with `_`.
- **Multiple `DialogueDatabase` instances**: Each is written to a separate file.
- **Missing `DialogueLineDatabase`**: Treated as a non‑critical absence; logged and skipped.

## 5. Acceptance Criteria / Definition of Done Verification
1. **Build**: `dotnet build -c Release` succeeds with zero errors; the DLL appears at `<game>\BepInEx\plugins\LilithMod\LilithMod.dll`. This confirms compilation only.
2. **Runtime – plugin load**: Launch the game; `BepInEx\LogOutput.log` must contain `[LilithMod] Loaded.`.
3. **Runtime – dump completion**: After the dump, the log must contain `[LilithMod] Dump complete. Nodes: X, PlayerLines: Y, DialogueLines: Z` with actual numbers > 0 (if the databases are present) or with warnings if missing.
4. **Output files**: Files exist under `<game>\BepInEx\plugins\LilithMod\dump\`:
   - `dialogue_nodes_{dbName}.json` for each `DialogueDatabase`.
   - `player_lines.json`
   - `dialogue_lines.json` (may be absent if no `DialogueLineDatabase` is loaded, in which case the INFO/WARN log is present).
5. **JSON validity**: Each file contains a JSON array of objects with the specified fields. Example nodes contain non‑null `id` and `text`.
6. **Resilience**: If `DialogueManager.s_instance` never appears within 30 seconds, only a warning is logged; no crash, no hang. If any database is missing, the dump of that type is skipped and logging records it, but the plugin still finishes and self‑destroys without crashing.
7. **Performance**: Polling in `Update` is lightweight; dump completes within a few frames after the singleton appears (no noticeable hit).
8. **Verification note**: A successful `dotnet build` alone does **not** prove runtime behaviour; points 2–7 can only be confirmed by running the actual game and inspecting logs/file output.
```