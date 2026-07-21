# Handoff: build an installer for LilithMod

Written 2026-07-21. Everything here is for whoever writes `installer.exe`.
Nothing in this repository writes one yet, and this document does not either.

Read `PORTABILITY.md` alongside this. That one is the audit of what was made
machine-independent; this one is the brief.

---

## 0. The one-paragraph version

Ship a Windows installer that finds the game, copies the contents of the release
zip into the game folder, guarantees one line in `doorstop_config.ini`, and gets
out of the way. Chat works immediately with an API key the user pastes in-game.
Voice and speech input are large optional extras that must stay optional and
must never be bundled.

---

## 1. What is being installed

`runtime\package-mod.ps1` produces `dist\LilithMod-<version>.zip`, ~36 MB, 256
files. Its layout, relative to the game folder:

```
winhttp.dll                       Doorstop proxy - the injection point
doorstop_config.ini               see section 3, the critical file
.doorstop_version
changelog.txt
INSTALL.txt                       manual instructions the installer replaces
dotnet\                           187 files, BepInEx's runtime
BepInEx\
  core\                           BepInEx 6.0.0-be.785 x64 IL2CPP
  plugins\LilithMod\
    LilithMod.dll                 the mod
    *.dll                         NAudio, Newtonsoft.Json, AngleSharp, ...
    help\OVERVIEW.txt             en, plus OVERVIEW.ja.txt / OVERVIEW.zh.txt
    voice-setup\README.txt        voice instructions + voice-config.example.ini
    speech-setup\README.txt       push-to-talk instructions
```

An installer may either embed this zip or build it. Embedding is simpler and is
what the version number is for.

**The mod is IL2CPP-specific.** BepInEx 6.0.0-be.785 x64 IL2CPP is the tested
build. Do not substitute the Mono flavour or a 5.x release; neither loads.

---

## 2. Finding the game

Do not assume a drive letter. Reference implementations exist in both languages
already - PowerShell in `reapply-mod.ps1`, Python in
`runtime\precache-game-voice.py` - and both were executed on a real machine.

1. `HKCU\Software\Valve\Steam` → `SteamPath`
2. Parse `steamapps\libraryfolders.vdf` for every `"path"` entry
3. In each, look for `steamapps\common\The NOexistenceN of Lilith\Lilith.exe`
4. Fall back to asking the user

Steam App ID is **4643090**. That is the game's identity, not a machine detail,
and is safe to hardcode.

---

## 3. `ignore_disable_switch = true` - the single most important step

**If you get one thing right, this is it.**

Steam passes `DOORSTOP_DISABLE=TRUE` to the game on essentially every launch.
With the default `ignore_disable_switch = false`, Doorstop honours it, the mod
silently does not load, **and every log looks perfectly healthy**. This cost a
full debugging session before it was understood, and it is undiagnosable from
the user's side.

So after writing `doorstop_config.ini`:

```ini
ignore_disable_switch = true
```

**Assert it afterwards. Do not assume a search-and-replace matched.** The
packager originally regex-replaced this and never checked; if a future BepInEx
renames or drops the key, the rewrite silently does nothing and every install is
broken in the way described above. `package-mod.ps1` and `verify-package.py` now
both assert it, and the installer must be the third place that does.

Related trap: launching `Lilith.exe` directly while Steam is closed can leave
Steam itself holding `DOORSTOP_DISABLE=TRUE`, which then poisons later launches
until Steam is fully restarted. **The installer's "launch now" button, if it has
one, must go through `steam://run/4643090`, never the exe.**

---

## 4. Install steps, in order

1. Close the game if running. `LilithMod.dll` is locked while `Lilith.exe` lives,
   and a copy over it fails with a file-in-use error.
2. Locate the game folder (section 2).
3. Back up any existing `BepInEx\config\LilithMod.cfg` before overwriting
   anything. **It holds the user's API key.**
4. Extract the archive into the game folder, merging with what is there.
5. Assert `ignore_disable_switch = true` (section 3).
6. **Do not write `LilithMod.cfg`.** The mod generates it on first run with
   correct defaults. Writing one yourself pins stale defaults forever - see
   section 7.
7. Tell the user the first launch is slow, and why (section 6).
8. Offer to launch via Steam.

---

## 5. What must never be bundled

Non-negotiable, and a licensing boundary rather than a preference:

- **Extracted game audio** (`.wav` from the game's assets)
- **Any voice model trained on it** (`.ckpt`, `.pth`, `.safetensors`)
- **The dialogue catalogue** (`dialogue\*.tsv`) - the game's own script

These are the game developers' content. They are gitignored, excluded by
`package-mod.ps1`, and asserted absent by `verify-package.py`. The release build
uses `-p:IncludeDialogueCatalog=false`; a release DLL is ~195 KB against ~428 KB
for a local one, which is the fastest way to spot a catalogue that leaked in.

Also never ship: `LilithMod.cfg` (API key), `memory.json`, `notes.json`, cached
voice audio, `.pdb`, `.log`.

The user supplies their own voice model. `voice-setup\README.txt` explains how.

---

## 6. Things that look like bugs and are not

Worth surfacing in the installer UI, because each one reads as a failure:

- **The first launch takes minutes.** BepInEx generates `interop\` from
  `GameAssembly.dll` on first run. A user who force-quits here can abandon
  BepInEx's startup mutex and break the *next* launch too. Say "this is normal,
  do not close it" explicitly.
- **The game window appears unmodded** almost always means section 3, or the
  Steam poisoning described there. Fully exit game *and* Steam, restart Steam.
- **No voice until set up.** She writes but does not speak out of the box. This
  is by design, not a broken install.
- **F7/F8 do nothing without an API key**, and their settings rows grey out.
  Deliberate: a chat box whose every message fails is worse than one that is
  plainly unavailable.

---

## 7. Config defaults - a trap worth understanding

`Config.Bind` **keeps whatever value is already in the `.cfg`**. Changing a
default in code therefore has no effect on any existing install; it only affects
a fresh one.

Consequences for the installer:

- Never write `LilithMod.cfg` yourself. You would be pinning today's defaults
  permanently for that user.
- An upgrade path that preserves the old `.cfg` also preserves every old
  default. That is the right trade - it keeps the API key - but it means "fixed
  in this version" may not reach existing users. If a default ever *must*
  change, the mod has to migrate it explicitly, and that is the mod's job, not
  the installer's.
- Preserve `[Debug]` flags on upgrade like any other. They default to `false`,
  and the mod logs a loud warning whenever one is on.

---

## 8. Uninstall

No uninstall path exists today. If you write one:

- Removing `BepInEx\` leaves `memory.json`, `notes.json` and the voice cache
  behind. **Ask before deleting those.** They are the only irreplaceable things
  the mod creates - her memory of the user's conversations and the notes she
  wrote them. Losing them is not recoverable and not what "uninstall the mod"
  ordinarily implies.
- `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt` and
  `dotnet\` are BepInEx's, not the game's, and are safe to remove - but only if
  no other BepInEx mod is installed. Check `BepInEx\plugins\` for anything that
  is not `LilithMod\` before removing the loader.
- Never touch anything else in the game folder.

---

## 9. Verifying the result

`python verify-package.py` unpacks the actual release zip somewhere unrelated
and checks what a stranger receives: plugin and Doorstop present,
`ignore_disable_switch` genuinely set, help in all three languages, and none of
the content from section 5, no credentials, no paths from the build machine. Run
it against whatever the installer embeds.

It caught two real faults on its first run - the unasserted doorstop rewrite,
and the shipped assembly carrying the developer's folder layout in its embedded
`.pdb` path.

**What it cannot do is prove the mod runs anywhere else.** See section 10.

---

## 10. Known unknowns - please treat these as untested

Stated plainly so nobody inherits false confidence:

- **The release zip has never been installed on a clean machine.** Not once. It
  is assembled and its contents verified, but every install bug this project has
  hit was environmental and only appeared on a real run. This is the single
  biggest risk in shipping.
- **Antivirus and SmartScreen are untested.** An unsigned installer that writes
  into a Steam folder and may launch PowerShell is a plausible false positive.
  Signing is worth considering.
- **No non-Windows support**, and none is planned. The mod uses Win32 key
  polling and Windows audio APIs directly.
- **Only one game version has been tested.** No version check exists. A game
  update that moves the IL2CPP symbols the mod patches would break it, probably
  with a Harmony exception at startup rather than anything graceful.

---

## 11. Optional components - keep them optional

Voice and speech input each need a multi-gigabyte local Python stack
(GPT-SoVITS for synthesis, a Whisper-class listener for input). Both are set up
by hand today, per `voice-setup\README.txt` and `speech-setup\README.txt`.

**An installer must not require either to install a chat mod.** The mod runs
chat-only with neither present, greying the rows that depend on them, and that
is a supported configuration rather than a degraded one.

If the installer offers to set them up, it should be a clearly separate, clearly
optional second stage - and it inherits section 5: it may install the *engine*,
never a model trained on the game's audio.

`ServiceBootstrap` starts these services with the game when no Startup shortcut
exists, so an installer that creates such a shortcut changes mod behaviour.
`ServiceBootstrap.cs` documents both branches.
