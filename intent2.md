Add JSON-driven custom dialogue injection to the existing LilithMod BepInEx plugin
(step 2). The plugin already loads, attaches, and dumps the databases successfully;
extend it, do not rewrite it. Authors write a SIMPLIFIED format; the plugin expands it
into the game's real schema and injects at runtime. A raw passthrough escape hatch
covers anything the simple format cannot express.

## Authoring format (fixed by design - implement exactly this)
Files: `<plugins>\LilithMod\custom\*.json`. Every .json in that folder is loaded.

```json
{
  "nodes": [
    {
      "key": "handhold",
      "trigger": "TouchHand",
      "say": "Lilith's line",
      "emotion": "emoji_smile_4",
      "duration": 3.0,
      "weight": 1,
      "choices": [
        { "text": "player choice A", "reply": "Lilith's response A" },
        { "text": "player choice B", "goto": "other_key" }
      ]
    }
  ],
  "raw": { "nodes": [], "playerLines": [] }
}
```
- `key` - author-facing stable string id, unique per file set. Used by `goto`.
- `trigger` - a DialogueTriggerType ENUM NAME (e.g. "TouchHand", "PlayerInitiated",
  "Idle"). Reject unknown names with a warning; do not guess.
- `say` - required. `emotion`, `duration`, `weight`, `choices` optional.
- Each choice needs `text` plus EITHER `reply` (plugin creates a follow-up node holding
  that line) OR `goto` (branch to another node's `key`). If both or neither, warn and skip
  that choice.
- `raw` - optional. Objects here match the GAME schema exactly (see
  reference/example-choice-nodes.json and reference/example-player-lines.json) and are
  injected verbatim with no expansion, except that ID collisions are still checked.

## Hard constraints
- IDs are auto-assigned by the plugin, never by the author. Allocate from safe bases so a
  future game update cannot collide: DialogueNode.id and lineId from 9000000+,
  PlayerLineEntry.id and groupId from 900000+, PlayerLineEntry.LineID from 9000000+.
  Before assigning, scan the live databases and start above any existing id in range.
- A node's choices MUST populate BOTH representations consistently: the
  `playerLineInteraction` string ("<playerLineId>-<nextNodeId>,...") AND the
  `playerLineOptions` list. It is not known which one the runtime reads, so both must agree.
- Il2Cpp interop: game collection fields need Il2CppSystem collections
  (`Il2CppSystem.Collections.Generic.List<T>`), NOT System.Collections.Generic.List<T>.
  Assigning a managed list to an Il2Cpp field will not work.
- Inject nodes into the DialogueDatabase in `DialogueManager.s_instance._databases` whose
  `databaseName` is "DialogueNode" (the main one, ~1110 nodes). If absent, use the first
  non-null database and log which was chosen.
- After injecting, call `db.BuildIndex()`, `playerDb.BuildIndex()`, and
  `DialogueManager.s_instance.BuildIndex()` so the aggregate node index sees new nodes.
  Also call `RegisterNodeWeight(node)` for each injected node.
- Reuse the EXISTING Update()-polling attach pattern in DumpDatabaseBehaviour (wait for
  `DialogueManager.s_instance`, timeout, log). Do not create your own GameObject -
  `Load()` runs before the first scene and such an object never ticks.
- Injected MonoBehaviours have no `Log`; log through `LilithModPlugin.Logger`.
- No exception may escape into the game. Any bad file/field is a warning, skipped, and the
  rest still loads.

## Definition of done
- `dotnet build -c Release` succeeds with zero errors.
- With a sample custom file present, BepInEx\LogOutput.log reports how many custom nodes
  and player lines were injected, and reports each rejected item with the reason.
- After injection, `DialogueManager.s_instance.TryGetNode(<assigned id>, out _)` returns
  true for an injected node - proving it entered the live index, logged as a self-check.
- With NO custom folder, a malformed file, an unknown trigger name, or a bad choice, the
  plugin logs a warning and the game runs normally with vanilla dialogue unaffected.
