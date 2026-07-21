# Portability audit, and what an installer has to do

Written 2026-07-21 as preparation for an installer. Nothing here writes one.

The mod has only ever run on one machine, so "works" and "works here" have not
been distinguishable. This is the sweep for the difference.

## Fixed in this pass

| where | was | now |
|---|---|---|
| `LilithModPlugin.cs` `RefAudioPath` default | absolute path into one dev machine's Steam library | `voice\jp\calm-reference.wav`, relative, resolved against the mod folder |
| `VoiceSetup.cs` `RuntimePath` / `ServerConfig` defaults | `D:\Lilith\voice-runtime...` | empty, which the launcher already reports as "complete voice-config.ini" |
| `reapply-mod.ps1` `-GameDir` default | hardcoded Steam path | Steam registry + `libraryfolders.vdf` discovery |
| `reapply-mod.ps1`, `package-mod.ps1` dotnet | `C:\Program Files\dotnet\dotnet.exe` | PATH first, then that path as fallback, and a clear throw if neither exists |
| `start-tts.ps1` `-Runtime` / `-Config` | `D:\Lilith\...` | derived from the script's own location |
| `precache-game-voice.py` `--project` / `--game` | `D:\Lilith`, `D:\SteamLibrary\...` | script-relative, and the same Steam discovery in Python |

Both discovery paths were executed on this machine and resolve correctly.
`dotnet` is **not** on PATH here, so the fallback is load-bearing rather than
decorative - worth keeping when refactoring.

The two config defaults matter more than they look. `Config.Bind` keeps whatever
is already in the `.cfg` (gotcha 2), so these changes are invisible on an
existing install and only take effect on a fresh one - which is precisely the
case that was broken and could never be noticed here.

## Deliberately left alone

- `127.0.0.1:9880` defaults. A local service on a fixed port is correct, and
  every one of them is overridable in config.
- `steam://run/4643090`. That is the game's app ID, not a machine detail.
- `HKCU\...\Run` and `libraryfolders.vdf` paths. Standard Windows/Steam
  locations.
- The reference WAV, voice models, and dialogue catalogue. These are the game
  developers' assets. They are gitignored and excluded from the release zip by
  `package-mod.ps1`, and that must stay true. They are for local use only and
  must never be committed or redistributed.

## What an installer must do

Ordered. Several steps are non-obvious and were each learned from a failure.

1. **Locate the game.** Steam registry (`HKCU\Software\Valve\Steam` →
   `SteamPath`), then every `"path"` in `steamapps\libraryfolders.vdf`, then
   look for `Lilith.exe`. Do not assume a drive. Reference implementations now
   exist in both PowerShell (`reapply-mod.ps1`) and Python
   (`precache-game-voice.py`).

2. **Install BepInEx 6.0.0-be.785 x64 IL2CPP** into the game folder.

3. **Set `ignore_disable_switch = true` in `doorstop_config.ini`. Not
   optional.** Steam passes `DOORSTOP_DISABLE=TRUE` to the game on essentially
   every launch; without this the mod silently does not load while BepInEx logs
   look perfectly healthy. This is the single highest-value thing the installer
   does. Verified live on this machine.
   Note the current implementations rewrite `ignore_disable_switch = false`
   by regex, which silently does nothing if a future BepInEx drops or renames
   the key. An installer should assert the setting is present afterwards
   rather than assume the rewrite matched.

4. **Copy the plugin.** `LilithMod.dll` plus its managed dependencies, the
   `help\`, `voice-setup\` and `speech-setup\` folders. Build with
   `-p:IncludeDialogueCatalog=false`; a release DLL is ~210 KB and a local one
   ~420 KB, which is the quickest check that the game's script did not ship.

   Without the catalogue the mod does not replace native dialogue at all, so the
   game keeps its own voice for its own lines. Her replies are unaffected. This
   is now enforced by `DialogueTextCatalog.Available` - see the note below.

5. **Do not write `LilithMod.cfg`.** Let the mod generate it on first run. The
   API key is user-supplied and belongs only in that file.

6. **First launch must go through Steam** (`steam://run/4643090`), never
   `Lilith.exe` directly. Launching the exe while Steam is closed is what
   poisons the environment in step 3.

7. **Voice and speech input are optional and must stay optional.** The mod runs
   chat-only with neither installed, greying the rows that need them. An
   installer should not require a multi-gigabyte ROCm/CUDA stack to install a
   chat mod.

## Not verified, and worth an installer's attention

- **The release zip has now been installed once**, on 2026-07-21, onto a freshly
  reinstalled game on the development machine. That is not a clean *machine* -
  same Steam, same drive layout, same OS - but it was a clean game folder and a
  clean `LilithMod.cfg`, so it was the first run against the shipped defaults
  rather than a config accumulated across every version. It found a real bug
  immediately; see below. A genuinely foreign machine is still untested.
- **BepInEx `interop\` is generated from `GameAssembly.dll` on first run.** It
  takes noticeably long, and a first launch that seems to hang is usually this.
  Worth telling the user rather than letting them force-quit - which can abandon
  BepInEx's startup mutex and break the *next* launch too (gotcha 6).
- **No uninstall path exists.** Removing `BepInEx\` leaves `memory.json`,
  `notes.json` and the voice cache. An uninstaller should ask before deleting
  those: they are the only irreplaceable things the mod creates.
- **Antivirus and SmartScreen.** An unsigned installer that writes into a Steam
  folder and launches PowerShell is a plausible false positive. Untested.

## Found by that first real install

The distribution build replaced native dialogue with text it did not have.
`TryGet` correctly returned false without the catalogue, but `QueueNode` fell
through to `node.text` - the game's Chinese source string - and handed it to the
Japanese voice. Every release install behaved that way; no local build could,
because a local build always has the catalogue.

Fixed by gating `VoiceReplacementEnabled` on `DialogueTextCatalog.Available`,
which turns the bubble gate and all four audio prefixes off together. Gating
only one would have yielded silent dialogue instead of wrong-language dialogue.

**The lesson is the reason this document exists.** Both verify suites passed,
the mod worked perfectly on the development machine, and the artifact users
would receive was broken. "Works" and "works here" are still not the same thing,
and only a real install distinguished them.
