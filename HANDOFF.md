# Handoff: injected callbacks never dispatch (BepInEx IL2CPP)

Written for a fresh reader with no prior context. Everything below was observed
directly on this machine. Where something is inferred rather than measured, it
says so.

---

## 1. The project

A BepInEx mod for **The NOexistenceN of Lilith**, a Unity IL2CPP desktop-pet
game. The mod adds three features, all of which were working end to end at one
point:

1. A dialogue-database dumper (writes JSON dumps of the game's dialogue tables).
2. JSON-driven injection of custom dialogue nodes.
3. Free-text LLM chat: press **F11**, type, Lilith answers via an
   OpenAI-compatible API. A local GPT-SoVITS TTS voice was wired in but has
   never been verified in-game.

**F11 opening the chat box is the acceptance test.** It currently does nothing.

---

## 2. Environment

| | |
|---|---|
| Game path | `D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith` |
| Steam AppID / depot | `4643090` / `4643091` |
| Current game BuildID | `24275097` (published 2026-07-19, reinstalled clean 2026-07-20 15:10) |
| Previous BuildID | `24242545`, depot manifest `3037869171313872550` |
| Unity | `2021.3.45f2`, IL2CPP |
| BepInEx | `6.0.0-be.785` (x64 IL2CPP), zip cached at `D:\Lilith\tools\bepinex785.zip` |
| Il2CppInterop | `1.5.3` (bundled with be.785) |
| Repo | `D:\Lilith`, branch `master`, HEAD `84ed1a2` |
| Last known-good mod commit | `8e1f94b` |
| Build command | `"C:\Program Files\dotnet\dotnet.exe" build D:\Lilith\LilithMod\LilithMod.csproj -c Release` |

The csproj writes straight into the game's plugin folder, so a build is a deploy.
Close the game before building or the DLL is locked.

Verbose BepInEx logging is currently **on** (`LogLevels = All`,
`LogChannels = All` in `BepInEx\config\BepInEx.cfg`). Keep it on; the decisive
evidence only appears at Debug level.

---

## 3. The symptom

The plugin loads. `Load()` runs. Types register. Then nothing ever ticks.

Concretely, in `BepInEx\LogOutput.log` you will see the plugin load and its
`Awake()` log lines, and **the log then stops** — no per-frame activity, and no
Unity log forwarding despite `UnityLogListening = true`.

The dumper writes its files from `Update()`, so **an empty
`BepInEx\plugins\LilithMod\dump\` after ~45 s of runtime is the fastest
automated check** that the fault is present. No keypress needed.

### The split

- `Awake()` **runs.** This is not evidence that dispatch works — `AddComponent`
  invokes `Awake` directly and synchronously.
- `Update()` **never runs**, on any component, on any host GameObject.
- Managed → native calls **work fine**: `AddComponent`, `new GameObject`,
  `DontDestroyOnLoad`, static field reads all succeed.
- Native → managed dispatch is **dead**, with exactly one exception (below).

---

## 4. Callback surface, measured

Every row was built, run, and read out of the log. Only one path works.

| Path | Registers? | Fires? |
|---|---|---|
| `SceneManager.sceneLoaded` | yes | **YES** |
| Injected MonoBehaviour `Update()` | n/a | no |
| Harmony postfix on `DialogueManager.Update` | yes | no |
| Harmony postfix on `ArchiveRuntimeTracker.Update` | yes | no |
| Harmony postfix on `SteamPlaytimeSync.Tick` | yes | no |
| `InputSystem.onAfterUpdate` (registered in `Load()`) | yes | no |
| `InputSystem.onAfterUpdate` (registered after scene load) | yes | no |
| `RenderPipelineManager.beginFrameRendering` | yes | no |
| `PlayerLoop` `updateDelegate` (with `type` set) | yes | no |
| `Camera.onPreRender` / `onPostRender` | yes | no (expected — URP) |

`Camera.onPreRender`/`onPostRender` are legacy callbacks and are inert under URP,
which this game uses, so those two are not evidence of anything.

`Harmony.GetAllPatchedMethods()` reports the patches as applied. The patched
methods demonstrably run every frame — the game's own `Player.log`
(`C:\Users\User\AppData\LocalLow\Nino\Lilith\Player.log`) shows
`ArchiveRuntimeTracker:Update()` and `SteamPlaytimeSync:Tick(Single)` in live
stack traces during the same session. The postfixes still never execute.

---

## 5. Named cause (probable, not proven)

From the Debug-level log at startup:

```
[Warning:Il2CppInterop] Class::Init signatures have been exhausted, using a substitute!
[Debug  :Il2CppInterop] Picked mono_class_instance_size as a Class::Init substitute
```

`mono_class_instance_size` returns a size and initialises nothing. If injected
and patched classes never get initialised, Unity never builds the class method
cache it uses for per-frame callbacks — which fits the observed split exactly
(`Awake` direct-invoked and fine, everything deferred dead).

Interop generation is also degraded on this build:

```
[Info :Il2CppInteropGen] Failed to restore 2 fields
[Info :Il2CppInteropGen] Failed to restore 1136 methods
[Info :Il2CppInteropGen] IL unstrip statistics: 7438 successful, 1359 failed
```

**Caveat, and it matters:** the `Class::Init` warning is extremely common across
Unity 2021.2+ games where BepInEx mods work fine (see BepInEx issues #474, #624,
#455). So the warning alone does not prove causation. It is the best-fitting
explanation found, not a confirmed diagnosis.

The detour layer itself is **healthy** — `DobbyDetour` prepares trampolines
successfully at startup and BepInEx's own `runtime_invoke` hook installs.

---

## 6. Ruled out by test, not by argument

Each of these was actually performed and the fault still reproduced:

- **Our code.** Built commit `8e1f94b` (the tree that last worked) in a separate
  git worktree. Fails identically. The mod source is exonerated.
- **BepInEx version.** Tested be.785, be.780, and be.764. be.764 bundles
  Il2CppInterop **1.5.1** rather than 1.5.3, so this is not a 1.5.3 regression.
  be.785 is the newest build published (2026-06-28); there is nothing newer to try.
- **Game install.** Full Steam uninstall + reinstall.
- **The other mod** (`LilithTextInjector`, from `github.com/mimimi6666/Lilith-AI-Mod`).
  Not present on the clean slate. Its BepInEx tree, plugin and 2 GB voice runtime
  are parked at `D:\Lilith\_dirty-bepinex-20260720-1515`.
- **Shadow assemblies.** Moved all `NAudio*` and `System.*` DLLs out of the plugin
  folder (they are NAudio dependencies; `System.Runtime.CompilerServices.Unsafe`
  shadowing MonoMod's copy was a plausible detour-breaker). No change.
- **Windows policy.** No ASR rules, no IFEO entries for `Lilith.exe`, no
  `AppInit_DLLs`, Smart App Control off, ACG (`BlockDynamicCode`) and CFG both
  **OFF** on the live process. Dynamic code generation is permitted.
- **Environment.** No `DOTNET_*` / `COMPlus_*` / `CORECLR_*` vars at machine,
  user, or process level.
- **Loader files.** `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and
  the `dotnet/` CoreCLR runtime at the game root all hash/date-match our be.785
  zip. No stale loader.
- **A reboot.**

### Notably, the other mod is broken the same way

Decompiling `LilithTextInjector.dll` shows it drives itself from
`[HarmonyPatch(typeof(DialogueManager), "Update")]`. Patching that exact method
from our mod registers and never fires. **Their mod cannot work on this build
either.** Its installer is not the culprit.

---

## 7. The anomaly nobody has explained

The mod demonstrably worked on **this same game build**. Evidence: the dumper's
output files at
`D:\Lilith\_dirty-bepinex-20260720-1515\BepInEx\plugins\LilithMod\dump\` carry
`CreationTime 2026-07-20 01:25:41` and `LastWriteTime 2026-07-20 04:06:41`. The
dumper only writes from `Update()`, so `Update()` was dispatching at 01:25 and
again at 04:06 on 2026-07-20. Game build `24275097` was installed 2026-07-19
04:20:56, i.e. **before** both of those runs (per Steam's
`content_log.txt`).

So this is not simply "the game updated and broke IL2CPP interop". Something
changed between 04:06 on 2026-07-20 and now that survives a clean game
reinstall, a fresh BepInEx, and a reboot — or the timestamp evidence is
misleading in a way not yet identified.

**This contradiction is the most valuable thread for a fresh reader.** Do not
assume the prior investigation framed it correctly.

---

## 8. Reproducing in one minute

```powershell
$g = "D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith"
Get-Process Lilith -EA SilentlyContinue | Stop-Process -Force
Remove-Item "$g\BepInEx\LogOutput.log" -Force -EA SilentlyContinue
Remove-Item "$g\BepInEx\plugins\LilithMod\dump" -Recurse -Force -EA SilentlyContinue
Start-Process "$g\Lilith.exe" -WorkingDirectory $g
Start-Sleep -Seconds 45
Select-String -Path "$g\BepInEx\LogOutput.log" -Pattern "DIAG|FIRED|Class::Init"
(Get-ChildItem "$g\BepInEx\plugins\LilithMod\dump" -EA SilentlyContinue).Count
```

Expected on the fault: `sceneLoaded` fires, nothing else does, dump count `0`.

Note: launching via Steam (`steam://rungameid/4643090`) at one point produced
**no BepInEx log at all**. That was self-inflicted — repeated `Stop-Process
-Force` abandoned BepInEx's startup mutex
(`System.Threading.AbandonedMutexException` in a
`preloader_*.log` at the game root). Prefer launching `Lilith.exe` directly, and
be aware that force-killing can poison the next launch.

---

## 9. Diagnostic scaffolding currently in the tree

These are temporary and should be deleted once the cause is found:

- `LilithMod/DiagHeartbeat.cs` — Harmony postfixes on `DialogueManager.Update`,
  `ArchiveRuntimeTracker.Update`, `SteamPlaytimeSync.Tick`; dumps
  `Harmony.GetAllPatchedMethods()`.
- `LilithMod/DiagDelegateProbe.cs` — `Camera.*`, `RenderPipelineManager`,
  `InputSystem.onAfterUpdate`, `SceneManager.sceneLoaded`; re-registers the
  per-frame hooks after scene load.
- `LilithMod/DiagTickProbe.cs` — `PlayerLoop` injection.
- `LilithModPlugin.Load()` — `[DIAG]` lines around `AddComponent`, plus a
  `DontDestroyOnLoad` call added while chasing the `scene=''` lead (the BepInEx
  manager object belongs to no scene; forcing it into the DDOL scene did **not**
  restore dispatch, so that call is not a fix and can go).
- `LilithMod.csproj` — a `DOTween` reference added for a probe that was dropped
  (`DOVirtual.Float` is stripped from the game's IL2CPP build).

---

## 10. Leads not yet tried

1. **MelonLoader** instead of BepInEx. A different IL2CPP loader with its own
   bootstrap; it may resolve `Class::Init` where this one does not. This is the
   single most promising untried option.
2. **Force the correct `Class::Init`.** Rather than accept Il2CppInterop's
   substitute, locate the real `il2cpp::vm::Class::Init` in `GameAssembly.dll`
   and supply it — via a BepInEx preloader patcher, or a local Il2CppInterop
   build. Directly targets the suspected cause; confirms or kills the theory.
3. **Roll the game back** to BuildID `24242545` via the Steam console
   (`download_depot 4643090 4643091 3037869171313872550`). Weak on the evidence,
   since the mod worked *after* that update — but it would settle whether the
   game binary is involved at all.
4. **Re-examine section 7.** Establish independently whether the mod really did
   run at 01:25 / 04:06 on 2026-07-20 (e.g. check dump file *contents* against
   the current game's dialogue tables — if they match the current build, the
   files really were produced by a working run on it).
5. **File upstream.** This is a clean, well-evidenced BepInEx / Il2CppInterop
   report for Unity 2021.3.45f2 if it turns out to be genuinely their bug.

---

## 11. Assets worth not destroying

- `D:\Lilith\backup-preinstall-20260720-1508\` — configs (including the LLM API
  key), 26 dialogue dumps, 11 reference WAVs, dialogue TSVs.
- `D:\Lilith\training\weights\` — a fine-tuned GPT-SoVITS Japanese voice model
  (~1.2 GB). Expensive to reproduce.
- `D:\Lilith\voice-data\` — 752 audio clips extracted from a second game, plus
  transcripts.
- `D:\Lilith\_dirty-bepinex-20260720-1515\` — the pre-clean-slate BepInEx tree
  (8.7 GB), including the other mod's DLL and the dump files whose timestamps
  section 7 rests on. **Do not delete until section 7 is resolved.**
- `C:\Users\User\Downloads\packages\` — the other mod's installer packages
  (2.3 GB), so nothing needs re-downloading.

`voice-data/`, `training/`, `backup*/` and `_dirty-bepinex-*/` are gitignored.
Extracted game audio belongs to the game's developers and must not be committed
or redistributed. The API key lives in
`BepInEx\config\LilithMod.cfg` and must never be committed or hardcoded.
