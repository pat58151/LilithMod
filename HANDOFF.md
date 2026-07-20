# Handoff: LilithMod

A BepInEx mod for **The NOexistenceN of Lilith**, a Unity IL2CPP desktop-pet
game. Written for someone picking this up cold. Everything here was observed on
this machine; where something is unverified, it says so.

---

## 1. What the mod does

1. **Dialogue dumper** - writes the game's dialogue tables to JSON on startup.
2. **Custom dialogue injection** - authored nodes from `custom/*.json` are merged
   into the game's database at runtime.
3. **Free-text LLM chat** - press **F11**, type, Lilith answers in her own voice.
4. **Bilingual voice** - she *speaks Japanese* through a local fine-tuned
   GPT-SoVITS model while the on-screen bubble shows *English*, advancing one
   sentence at a time.

F11 opening the chat box is the acceptance test for 3 and 4.

---

## 2. Environment

| | |
|---|---|
| Game | `D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith` |
| Steam AppID / depot | `4643090` / `4643091`, BuildID `24275097` |
| Unity | `2021.3.45f2`, IL2CPP |
| BepInEx | `6.0.0-be.785` x64 IL2CPP (`D:\Lilith\tools\bepinex785.zip`) |
| Repo | `D:\Lilith`, branch `master` |
| Build | `"C:\Program Files\dotnet\dotnet.exe" build D:\Lilith\LilithMod\LilithMod.csproj -c Release` |

The csproj `OutputPath` points straight into the game's plugin folder, so
**building is deploying**. Close the game first or the DLL is locked.

---

## 3. Running it

```powershell
# 1. voice service - leave the window open, ~40s to load the model
powershell -ExecutionPolicy Bypass -File D:\Lilith\start-tts.ps1

# 2. launch the game from Steam, then press F11
```

Voice is optional. With `[Voice] Enabled = false` in
`BepInEx\config\LilithMod.cfg`, chat works and simply stays silent.

**Verify scripts** (each builds, then asserts load-bearing source markers):

```
python verify-bilingual.py   # the bilingual voice feature - the current one
python verify-voice.py       # the original voice plumbing
python verify-step3.py       # LLM chat
```

---

## 4. Layout

| File | Responsibility |
|---|---|
| `LilithModPlugin.cs` | Entry point, config binding, **persona prompt**, voice init |
| `LlmChatController.cs` | F11 UI, chat history, API call, reply parsing, subtitles |
| `DumpDatabaseBehaviour.cs` | Dialogue dump + custom-node injection |
| `Utterance.cs` | One sentence: `JaText` (spoken) + `EnText` (shown) |
| `SpeechQueueProcessor.cs` | Voice thread; overlaps synthesis with playback |
| `TtsClient.cs` | HTTP to GPT-SoVITS |
| `VoicePlayer.cs` | NAudio `WaveOutEvent` playback |
| `WindowFocus.cs`, `PointerFocus.cs` | Win32 focus/cursor work (see §6) |
| `GameStyle.cs` | Copies the game's own dialogue-bar styling |

Outside the repo, gitignored, **not reproducible cheaply**:

| Path | What | Size |
|---|---|---|
| `D:\Lilith\voice-runtime\` | GPT-SoVITS + bundled Python + pretrained models | ~2 GB |
| `D:\Lilith\training\weights\` | **fine-tuned JA voice model** | 1.2 GB |
| `D:\Lilith\voice-data\` | 752 clips extracted from a second game + transcripts | 337 MB |
| `D:\Lilith\backup-preinstall-20260720-1508\` | configs incl. API key, dumps, reference WAVs | 7 MB |

---

## 5. How the voice path works

```
F11 -> LlmChatController -> one LLM call
     -> {"lines":[{"ja":"…","en":"…"}, …]}
     -> parse; history stores the JA she actually said
     -> first EN shown immediately; all sentences queued to the voice thread

SpeechQueueProcessor (background thread), per sentence:
     enqueue EN subtitle   (main thread drains it and displays)
     start synthesis of the NEXT sentence   <- before PlaySync, this is the overlap
     PlaySync(current audio)                <- blocks
```

Only the first sentence is paid for up front. Measured: a one-sentence reply is
~2.2 s to first sound, a two-sentence reply was ~7 s serially and is ~2.4 s
pipelined. The LLM leg is ~0.9 s.

Fallbacks, all defined and implemented:
- reply is not the JSON shape -> shown and spoken as plain text
- voice disabled -> whole reply shown at once, no audio
- synthesis fails mid-reply -> whole reply shown **once**, remaining subtitles
  dropped (with no audio there is nothing pacing them, so they would otherwise
  flash past in a frame)
- a new reply arrives mid-speech -> `CancelCurrent()` flushes the old queue; the
  sentence already inside `PlaySync` is allowed to finish

---

## 6. Gotchas that cost real time

Read this section before debugging anything.

1. **Verify process identity before believing a log.** The game is
   single-instance. Launching `Lilith.exe` directly while a Steam-launched copy
   is running produces a *second* process that initialises BepInEx, logs
   `Load()` / `Awake()`, hands off to the existing vanilla instance and exits.
   The result looks exactly like "injected callbacks are dead": startup logs
   appear, nothing ever ticks, Harmony patches never fire. This cost an entire
   session and produced a confident, wrong diagnosis blaming Il2CppInterop.
   **Confirm the PID writing the log is the PID rendering the game.**

2. **`Config.Bind` keeps whatever is already in the cfg.** Changing a default in
   code does nothing on an existing install. To actually ship a new prompt,
   bind a *new key name* - that is why the prompt lives under
   `BilingualSystemPrompt` and the old `SystemPrompt` entry is ignored. Silent
   no-ops here look exactly like a feature that "doesn't work".

3. **`node.text = …` does not refresh a dialogue already on screen.**
   `DialogueManager.StartDialogue(9500000)` is what the game reacts to. This is
   why each subtitle re-calls StartDialogue.

4. **`Class::Init signatures have been exhausted` is benign.** It appears on
   essentially every Unity 2021.2+ game including working ones. Do not build a
   theory on it (see item 1).

5. **Force-killing the game can abandon BepInEx's startup mutex**, leaving an
   `AbandonedMutexException` in a root `preloader_*.log` and no BepInEx at all
   on the next launch. If BepInEx suddenly stops loading, suspect this.

6. **GPT-SoVITS prints CJK to stdout.** On a cp874 console that raises
   `UnicodeEncodeError`, surfaced as a misleading `HTTP 400 tts failed`.
   `start-tts.ps1` sets `PYTHONIOENCODING=utf-8`; keep it.

7. **Kernel compilation is per sequence length.** The first request at a new
   text length can take 6-14 s while warm ones take ~2 s. Early measurements
   lie; warm up before timing anything.

8. **The TTS service is not actually streaming.** `streaming_mode: true` was
   tested with `wav`, `raw` and `ogg`: first chunk always equalled completion.
   Sentence-level pipelining is the substitute, and is why it exists.

9. **Licensing.** `voice-data/` and `training/` are audio and models derived
   from the games' own assets; `voice-runtime/` is third-party GPT-SoVITS plus
   pretrained weights. All gitignored. Never commit or redistribute them. A
   commit once swept in the whole 2 GB runtime and had to be reset.

10. **The API key** lives in `BepInEx\config\LilithMod.cfg`, is user-supplied,
    and must never be hardcoded, logged, or committed. `backup*/` is gitignored
    because it contains a copy.

---

## 7. State

**Verified**
- Mod loads, ticks, dumps dialogue, injects custom nodes.
- F11 opens the chat box; a reply was received, parsed as a sentence pair, shown,
  and queued to voice with no errors logged.
- `verify-bilingual.py` passes: build plus every load-bearing marker.
- The TTS service answers the mod's exact payload - JA text, `prompt_text`,
  `cut5`, `fragment_interval` 0.4 - in 13.3 s cold, ~2.2 s warm.
- The new config key materialises on an install that already had a cfg.

**Not verified**
- That audio was *audibly* heard in-game. The log shows it queued with no
  failures; nobody has confirmed by ear through the game itself.
- How a **two-sentence** reply looks. Each subtitle re-calls `StartDialogue`,
  which restarts the dialogue, so the bubble may visibly re-trigger mid-reply.
  Correct, but possibly ugly. This is the first thing to look at.
- Whether an IME composes correctly in the chat box.

**Open / not started**
- `Data/SenWords` filter never traced.
- No packaging or README for anyone else to install this.
- `_dirty-bepinex-20260720-1515\` (~6.7 GB after the runtime was moved out) is
  leftover and safe to delete.

---

## 8. Process note

Per the repo's CLAUDE.md, spec-able implementation is delegated to DeepSeek and
Claude keeps design, a short intent, and review. Two things to know:

- `pipeline.py plan` takes **no `-c` flag**, so it briefs the planner with zero
  context and will invent files. Use
  `implement_with_deepseek.py --plan -c <every relevant file>` instead.
- The agent stage (`deepseek_agent.py`) failed here by emitting malformed
  tool-call markup for 25 steps and writing nothing at all. If that recurs, the
  ladder's terminal stage is Opus authorship.
