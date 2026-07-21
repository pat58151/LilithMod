# Handoff: the installer exists now

Written 2026-07-21, same day as `INSTALLER-HANDOFF.md`. That brief has been
executed. This documents what was built, what was verified, and what was not.

Release: https://github.com/pattarapongsinpat/LilithMod/releases/tag/v1.0.0.0
Assets: `LilithMod-Setup-1.0.0.0.exe` (26.5 MB) and `LilithMod-1.0.0.0.zip`.
Commit: 4987444.

---

## 1. What was built

Two files, under `installer\`:

- `LilithMod.iss` — Inno Setup 6 script. All logic lives in `[Code]` (Pascal).
- `build-installer.ps1` — extracts `dist\LilithMod-<version>.zip` into
  `installer\payload\` (gitignored), sanity-checks it, compiles with ISCC.

The installer **embeds** the release zip rather than building it — the simpler
option the brief offered. Version is parsed from the zip filename and passed as
`/DAppVersion`; nothing is hardcoded twice.

Rebuild: `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1`
(optionally `-ZipPath`, `-Version`). It picks the newest
`dist\LilithMod-<version>.zip`. ISCC is probed on PATH, then
`%LOCALAPPDATA%\Programs\Inno Setup 6`, then `%ProgramFiles(x86)%\Inno Setup 6`.
This machine has it via `winget install JRSoftware.InnoSetup` (user-local
install, NOT Program Files (x86) — an empty `Inno Setup 6` folder exists there,
don't be fooled by it).

## 2. How each section of the brief was implemented

- **Finding the game (§2)** — `FindGameDir()` in Pascal mirrors
  `Find-GameFolder` in `reapply-mod.ps1`: HKCU `SteamPath`, HKLM WOW6432Node
  `InstallPath`, then every `"path"` in `libraryfolders.vdf` (with `\\` → `\`
  unescape). Pre-fills the directory page; fallback is the user picking a
  folder. `NextButtonClick` refuses any folder without `Lilith.exe`.
- **The doorstop switch (§3)** — `AssertDoorstopSwitch()` runs at
  `ssPostInstall`: finds the `ignore_disable_switch` line, rewrites if needed,
  then **re-reads the file and verifies** the exact line exists. Failure is a
  `mbCriticalError` dialog with manual-fix instructions (install can't be
  aborted at that stage, so loud is the best available). The payload already
  ships `= true`, and `build-installer.ps1` refuses to build a payload that
  doesn't — so this is the third assert the brief asked for, plus a fourth at
  build time. Rewrite uses `SaveStringsToUTF8File` because the shipped ini has
  a UTF-8 BOM and Doorstop demonstrably tolerates it.
- **Launch (§3)** — `[Run]` uses `steam://run/4643090` via shellexec. The exe
  is never launched.
- **Install order (§4)** — `PrepareToInstall` loops a Retry/Cancel dialog while
  `Lilith.exe` runs (WMI `Win32_Process` query; if WMI is broken it proceeds
  and lets the file-copy lock error surface). `ssInstall` backs up
  `LilithMod.cfg` to `LilithMod.cfg.bak-<timestamp>` beside it. Files merge
  into `{app}`. **`LilithMod.cfg` is never written** (§7 respected).
- **First-launch UX (§6)** — the finished page (`FinishedLabel` +
  `FinishedLabelNoIcons` in `[Messages]`) warns: slow first launch / don't
  force-quit, API key needed for F7/F8, voice optional and not included,
  "looks unmodded → restart Steam fully".
- **Uninstall (§8)** — `InitializeUninstall` warns (default No) if
  `BepInEx\plugins\` holds anything besides `LilithMod`, since removing the
  loader kills those mods. After uninstall, one prompt (default: keep) offers
  to delete `memory.json`, `notes.json`, `custom\`, `voice-cache\`,
  `LilithMod.cfg` + its `.bak-*`. Inno only removes files it installed, so all
  of those survive uninstall untouched unless the user opts in.
- **Nothing bundled that must not be (§5)** — the payload is the verified
  release zip, nothing added. `verify-package.py` PASS (256 files) against the
  exact zip embedded.
- **Optional components (§11)** — not touched. The installer installs chat
  only; voice/speech remain manual per their READMEs. No Startup shortcut is
  created (would change `ServiceBootstrap` behaviour).

Details that aren't in the brief: `PrivilegesRequired=admin` (default Steam
library is under Program Files (x86)); `UninstallFilesDir={app}\BepInEx` so
`unins000.*` doesn't litter the game root; AppId GUID
`{C7A9F2D4-5B31-4E86-9A0D-2F6C1E7B8A43}` — keep it stable across versions or
upgrades will stack uninstall entries.

## 3. What was verified

- `build-installer.ps1` end-to-end: stage, sanity-check, ISCC compile, exit
  codes. Clean build, 26.5 MB output.
- `verify-package.py` PASS on the embedded zip.
- Payload sanity asserts: `winhttp.dll`, `doorstop_config.ini`,
  `LilithMod.dll` present; `ignore_disable_switch = true` regex-verified.

## 4. What was NOT verified — the same §10 risk, now one layer deeper

- **The installer UI has never been run.** Not silently, not interactively.
  A silent test needs UAC elevation nobody was present to click. Every `[Code]`
  path — detection, the running-game loop, the backup, the post-install assert,
  both uninstall prompts — compiles but has zero runtime executions.
  **First thing anyone should do: run `dist\LilithMod-Setup-1.0.0.0.exe` on
  this machine.** It merges the same files over the live install; the cfg
  backup happens first; risk is low.
- Clean-machine install: still never done (§10 unchanged).
- SmartScreen/AV behaviour: untested, unsigned. Release notes tell users to
  click through; signing remains worth considering.
- Uninstall: never executed. The other-plugins warning and the data prompt are
  untested code.

## 5. Release mechanics

Tag `v1.0.0.0` on master, created with `gh release create`. Release notes cover
install, the slow first launch, the API key, Steam-only launching, SmartScreen,
and what is deliberately absent. For the next version: run
`runtime\package-mod.ps1`, `python verify-package.py`, then
`installer\build-installer.ps1`, then `gh release create v<version>` with both
assets. Nothing bumps versions automatically.
