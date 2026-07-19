Build the skeleton BepInEx 6 IL2CPP plugin for the Unity game "Lilith" (a desktop pet).
This is step 1 of a larger mod; its only job is to prove the runtime-injection approach works
and to give us readable game data. It must load under BepInEx and dump the game's live
dialogue databases to JSON on disk.

Environment (fixed, do not change):
- Game dir: D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith
- BepInEx 6.0.0-be.785 IL2CPP, already installed and confirmed working.
- Plugin DLL output goes to <game>\BepInEx\plugins\
- Reference DLLs: <game>\BepInEx\core\{BepInEx.Core,BepInEx.Unity.IL2CPP,Il2CppInterop.Runtime,0Harmony}.dll
  and <game>\BepInEx\interop\{Assembly-CSharp,Il2Cppmscorlib,UnityEngine,UnityEngine.CoreModule}.dll
- BepInEx 6 IL2CPP plugins derive from BepInEx.Unity.IL2CPP.BasePlugin with `public override void Load()`,
  NOT the BepInEx 5 BaseUnityPlugin. Target netstandard2.1. .NET SDK 9 is installed.
- The exact game API surface is in reference/game-api.txt (attached). Use it verbatim; do not invent members.

Key domain facts:
- DialogueManager is an ACSingletonBehaviour<DialogueManager> singleton. It exposes
  GetPlayerLineDatabase() and a public _databases field (Il2CppReferenceArray<DialogueDatabase>).
- DialogueDatabase.nodes, PlayerLineDatabase.entries, DialogueLineDatabase.entries are public mutable lists.
- The databases are NOT available at plugin Load() time; they only exist after the game has
  loaded its scene and constructed DialogueManager. The dump must therefore be deferred until
  the singleton actually exists, and must not busy-block the game.

Definition of done:
- `dotnet build -c Release` succeeds with zero errors and emits the plugin DLL.
- When the game runs, BepInEx\LogOutput.log contains the plugin's load banner and a line
  reporting how many dialogue nodes / player-line entries / dialogue-line entries were dumped.
- A JSON file per database is written under BepInEx\plugins\LilithMod\dump\, each containing
  the real field values from reference/game-api.txt schemas (ids and non-empty text present).
- If DialogueManager or any database is absent, the plugin logs a warning and the game still
  runs normally — no exception escapes into the game, no crash, no hang.
