# Handoff: LilithMod

A BepInEx mod for **The NOexistenceN of Lilith**, a Unity IL2CPP desktop-pet
game. Written for someone picking this up cold. Everything here was observed on
this machine; where something is unverified, it says so.

---

## 1. What the mod does

1. **Dialogue dumper** - writes the game's dialogue tables to JSON on startup.
2. **Custom dialogue injection** - authored nodes from `custom/*.json` are merged
   into the game's database at runtime.
3. **Free-text LLM chat** - press **F7**, type, Lilith answers in her own voice.
   Or press **F8** and speak; it submits after 2.5 s of silence.
4. **Bilingual voice** - she *speaks Japanese* through a local fine-tuned
   GPT-SoVITS model while the on-screen bubble shows *English*, advancing one
   sentence at a time.

F7 opening the chat box is the acceptance test for 3 and 4.

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

To **compile-check without deploying** - which works with the game running -
redirect the output instead:

```
"C:\Program Files\dotnet\dotnet.exe" build LilithMod\LilithMod.csproj -c Release ^
    -p:OutputPath=D:\Lilith\build-test\ -v q --nologo -clp:ErrorsOnly
```

`build-test\` is gitignored. This is the fast loop for anything that is not
being tested in-game; `dotnet` is not on `PATH`, hence the full path.

---

## 3. Running it

```powershell
# 1. voice service - leave the window open, ~40s to load the model
powershell -ExecutionPolicy Bypass -File D:\Lilith\start-tts.ps1

# 2. launch the game from Steam, then press F7 to type or F8 to speak
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

## 3a. Packaging a release

```powershell
powershell -ExecutionPolicy Bypass -File runtime\package-mod.ps1
```

Writes `dist\LilithMod-<version>.zip` containing BepInEx and the plugin, and
nothing else. Two things it deliberately does NOT do:

- **It never copies the installed plugin folder.** That folder accumulates
  dialogue dumps, cached voice audio, `memory.json`, `notes.json`, logs, and a
  config file holding the API key. The build output is assembled from scratch
  into a temporary staging folder instead, and a guard fails the run if a
  `.wav`, `.cfg`, `.ckpt`, `.pth`, dump or memory file ever reaches it.
- **It excludes the dialogue catalogue.** `dialogue/*.tsv` is the game's own
  script and is compiled into the DLL as an embedded resource for local builds
  only - gitignoring it kept the repo clean but the DLL carried it regardless.
  `-p:IncludeDialogueCatalog=false` drops it. Verify with the file size: a
  release DLL is ~195 KB, a local one ~410 KB. Without the catalogue, native
  game dialogue simply keeps its original voice; `DialogueTextCatalog` returns
  empty rather than throwing.

## 4. Layout

| File | Responsibility |
|---|---|
| `LilithModPlugin.cs` | Entry point, config binding, voice init |
| `LlmChatController.cs` | F7 chat UI, F8 speech toggle, DeepSeek calls, history, reply parsing, subtitles, note cadence |
| `PersonaPrompt.cs` | Persona, per-language style blocks, player name, letter and love-letter prompts |
| `NoteJournal.cs` | Persisted note cadence - qualifying and personal exchange timestamps, cooldown |
| `SettingsBridge.cs` | Rows injected into the game's native settings tabs |
| `runtime/push_to_talk.py` | Silero VAD + Whisper on the GPU; transcripts back to the mod |
| `LiveInformationService.cs` | Startup time/weather warm-up, SearXNG search, SmartReader extraction |
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

---

## 5. How the voice path works

```
F7 (or F8 speech) -> LlmChatController -> one LLM call
     -> {"lines":[{"spoken":"…","shown":"…"}, …]}
     -> parse; history stores what she actually said
     -> all sentences queued without showing an early subtitle

SpeechQueueProcessor (background thread), per sentence:
     synthesize current audio
     start synthesis of the NEXT sentence   <- before PlaySync, this is the overlap
     hand off shown subtitle and wait for Unity display acknowledgement
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

   `start-lilith.ps1` now refuses to start a second copy when one is already
   running, so the launcher itself can no longer cause this. Anything that
   launches `Lilith.exe` outside the launcher still can - see the incident note
   at the end of this file.

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

5. **Keyboard focus and mouse capture are separate, and chat must only take the
   keyboard.** Delivering keystrokes needs `WS_EX_NOACTIVATE` cleared and the
   window foregrounded - nothing more. Clearing `WS_EX_TRANSPARENT` or calling
   `TransparentWindowNew.BeginKeyboardInput()` additionally suspends the game's
   per-pixel click-through, and since the window is fullscreen that makes the
   entire desktop stop accepting clicks. The chat bar therefore uses
   `EnterKeyboardMode`; only settings uses `EnterInteractiveMode`, because it
   actually needs the mouse, and it scopes that to while a text field is focused.
   `s_beganKeyboardInput` keeps Begin/End balanced now that only one path
   suspends click-through - an unmatched `End` leaves the pet unable to pass
   clicks through at all.

6. **Force-killing the game can abandon BepInEx's startup mutex**, leaving an
   `AbandonedMutexException` in a root `preloader_*.log` and no BepInEx at all
   on the next launch. If BepInEx suddenly stops loading, suspect this.

7. **GPT-SoVITS prints CJK to stdout.** On a cp874 console that raises
   `UnicodeEncodeError`, surfaced as a misleading `HTTP 400 tts failed`.
   `start-tts.ps1` sets `PYTHONIOENCODING=utf-8`; keep it.

8. **Kernel compilation is per sequence length.** The first request at a new
   text length can take 6-14 s while warm ones take ~2 s. Early measurements
   lie; warm up before timing anything.

9. **The TTS service is not actually streaming.** `streaming_mode: true` was
   tested with `wav`, `raw` and `ogg`: first chunk always equalled completion.
   Sentence-level pipelining is the substitute, and is why it exists.

10. **Licensing.** `voice-data/` and `training/` are audio and models derived
   from the games' own assets; `voice-runtime/` is third-party GPT-SoVITS plus
   pretrained weights. All gitignored. Never commit or redistribute them. A
   commit once swept in the whole 2 GB runtime and had to be reset.

11. **A running listener survives deleting its `.py` file.** Python loads the
    source once, so an old `runtime/*.py` helper keeps running - holding the
    microphone and writing handoff files nothing reads - long after the file is
    gone and the launcher has been repointed. Editing `start-lilith.ps1` only
    takes effect on the *next* launcher run. After changing anything under
    `runtime/`, check `Get-CimInstance Win32_Process -Filter "Name='python.exe'"`
    and kill the stale process explicitly. This is the same class of mistake as
    gotcha 1: believing a code change took effect without confirming which
    process is actually running.

12. **The API key** lives in `BepInEx\config\LilithMod.cfg`, is user-supplied,
    and must never be hardcoded, logged, or committed. `backup*/` is gitignored
    because it contains a copy.

---

## 7. State

**Verified**
- Mod loads, ticks, dumps dialogue, injects custom nodes.
- F7 opens the chat box; a reply was received, parsed as a sentence pair, shown,
  and queued to voice with no errors logged.
- `verify-bilingual.py` passes: build plus every load-bearing marker.
- The TTS service answers the mod's exact payload - JA text, `prompt_text`,
  `cut5`, `fragment_interval` 0.4 - in 13.3 s cold, ~2.2 s warm.
- The new config key materialises on an install that already had a cfg.

- Speech input end to end: F8, spoken sentence, transcript submitted, reply
  spoken. Interim text streams into the field as you talk.
- Settings rows grey correctly when their service is stopped, and recover on
  their own when it returns.
- `runtime\package-mod.ps1` produces a release zip with no game content in it.

**Not verified**
- Audio was generated, cached, and played without an error, but no human ear
  check was performed during automated verification.
- How a **two-sentence** reply looks. Each subtitle re-calls `StartDialogue`,
  which restarts the dialogue, so the bubble may visibly re-trigger mid-reply.
  Correct, but possibly ugly. This is the first thing to look at.
- Whether an IME composes correctly in the chat box.
- **The release zip has never been installed anywhere.** It is assembled and
  its contents checked, but no clean machine has run it. Every install bug this
  project hit was environmental and only appeared on a real run - assume the
  same is true of the ones still in there.
- Whether a note has ever actually fired under the current gates. Seven
  substantial messages inside four hours, a 36 hour cooldown and a 40% roll is
  a high bar, and "rare" and "broken" look identical from outside.
  `NoteJournal.Describe()` exists to answer this and is not wired to anything.
  It now also reports `personal=`, so when a note does fire it is possible to
  tell whether the love-letter branch was even eligible.
- Whether a love letter has ever fired **in game**. Its prompt was exercised
  directly against the API and reads correctly, but the branch needs the 5% roll
  and two personal exchanges on top of every gate above. Assume it is untested.

**Open / not started**
- `Data/SenWords` filter never traced.
- No automated install - the release zip is copied in by hand per `INSTALL.txt`.
- ~~`_dirty-bepinex-20260720-1515\` and `backup-preinstall-20260720-1508\`~~ -
  both deleted 2026-07-21, reclaiming 563 MB. Their contents were regenerable
  (a BepInEx tree, dialogue dumps, TSV inventories, reference WAVs) and the
  preinstall backup also held a stray copy of the API key config.

---

## 8. Companion integration added 2026-07-20

- Normal chat: `deepseek-v4-flash`, thinking disabled, 256 output-token cap.
- Live time/weather/web questions: system time, IP-API location, Open-Meteo,
  SearXNG, and parallel SmartReader extraction. SearXNG JSON and HTML are both
  supported, with startup health selection and public-instance discovery.
- Anthropic is removed. Normal chat, live answers, ambient speech, and letters
  all use the saved DeepSeek key and `deepseek-v4-flash`.
- Persona: one compact Japanese or Chinese prompt selected from the saved game
  voice. It was re-derived from the full dialogue corpus. The core is Lilith as
  the player's created consciousness form: memory sustains her, shared moments
  matter more than metaphysical proof, and sleep is chosen companionship rather
  than a biological need.
- Context: exact local system time plus live standing, lying, sleeping, and
  native bedtime state.
- Memory: exactly five recent conversations/interactions in game-local
  `memory.json` and `MEMORY.md`. Direct recency is used instead of a vector DB.
- Letters: a DeepSeek-written note through `NoteImageSaver`, notifying the native
  inbox. *(Cadence has since changed twice - see the 2026-07-21 sections.)*
- Settings: masked DeepSeek key bar, reveal toggle, voice replacement, and
  60% click-through under the native Lilith tab. *(Row set has since shrunk.)*
- Built-in voice: native voice playback is patched to GPT-SoVITS using the
  saved Japanese/Chinese choice. WAV results are cached on disk. A resume-safe
  full cache builder is `runtime/precache-game-voice.py`.
- Wake input: openWakeWord streaming/VAD plus Faster-Whisper Small CPU INT8 for
  `Lilith`, `リリス`, and Chinese variants. The RX 9060 XT remains available to
  GPT-SoVITS through ROCm/HIP 7.15.
- External startup: `runtime/start-lilith.ps1` selects the saved language,
  restarts GPT-SoVITS when it changes, warms it, starts the wake listener, then
  starts the game. Desktop and Windows Startup shortcuts are installed.
- Measured live: DeepSeek 896 ms. GPT-SoVITS uses about 2.24 GB dedicated VRAM.
  Total dedicated GPU allocation was 7.5 GB with the game and all services up.
- Verified in the running game: patches installed, settings rows built, normal
  chat parsed, synchronized voice cached,
  five-item memory persisted, and a native note image was created.

### Follow-up changes, 2026-07-20

- Voice configuration moved to game-local `BepInEx/plugins/LilithMod/voice-setup/`.
  `README.txt` documents GPT-SoVITS setup and `voice-config.ini` selects separate
  spoken and subtitle languages (`ja`, `en`, or `zh`). The shared build can omit
  all synthetic voice files. Settings now opens this folder and calls the native
  voice option `Vocal Synthesis`.
- The F11 chat key is rebindable. Wake transcripts open that input, remain visible
  for 1.2 seconds, and are then submitted. Wake matching is anchored at the start,
  has no Whisper wake-word prompt bias, and rejects low-confidence/no-speech text.
- Wake handoff uses timestamped files plus an in-game FIFO. Back-to-back commands
  wait for the previous request and its final audio instead of overwriting a file
  or cancelling a response. A transient empty DeepSeek completion retries once.
- A game-local `voice-output.active` lock prevents the wake listener from hearing
  Lilith's own playback. With that feedback path removed, microphone energy,
  duration, Whisper confidence, and command-length thresholds are intentionally
  permissive again.
- The compact LLM JSON contract may emit explicit timer/alarm actions. Validated
  actions call the game's native `TimerSystem` or `AlarmSystem`, including cancel.
- Common English timer durations, relative alarms, clock alarms, and cancellation
  are also parsed locally so a missing LLM action cannot drop the command. Dynamic
  alarm, acknowledgement, and snooze bubbles use paired Japanese speech and English
  subtitles; the two custom Japanese responses are included in the disk cache.
- Note length is randomized: 80% are 2-3 short sentences, 15% are 4-7 short
  sentences, and 5% are one long flowing sentence.
- Reply node `9500000` bypasses native-dialogue replacement. This fixes the
  Japanese-audio-then-English-audio duplicate path. Both native `PlayVoice`
  overloads are patched.
- The dialogue bubble closes after final playback. Synthesis-failure and voice-off
  fallbacks close after six seconds.
- Settings only force desktop interactivity while an API-key or hotkey text field
  is focused. Uncovered areas retain the normal per-pixel click-through behavior.
- `runtime/start-lilith.ps1` reads the voice setup directly, selects GPT and SoVITS
  weights, warms the active profile, restarts the tightened wake listener hidden,
  logs startup status, and launches the game even when optional voice assets are
  absent. BepInEx console logging is disabled.

### Keyed speech input replaces the wake word, 2026-07-21

Voice input is now an explicit key, not an always-listening wake model. **F8
toggles** recognition: press once to start listening, and the utterance is
transcribed and submitted automatically after **2.5 s of silence**. Pressing F8
again while listening cancels, since silence is the normal way to submit.
Escape and a manual Enter also stop the microphone.

While listening, the chat field is focused and accepts typing. Interim
transcripts stream into it until the moment you type; from then on partials stop
overwriting the field and the final transcript is discarded, because replacing
text the user actually typed would throw away real input. That latch is
`NoteUserTyping()`, which infers editing by noticing the field no longer holds
the partial the mod last wrote into it.

- `runtime/push_to_talk.py` replaces `runtime/wake_listener.py`, which is
  deleted. openWakeWord is gone as a dependency.
- The mod owns the listening window. `LlmChatController` writes
  `push-to-talk.active` beside the plugin while listening and deletes it when
  listening ends. The stale trigger is cleared in `Awake()` and by the launcher,
  because one left behind by a crash would make the listener record forever.
- After finalising an utterance the listener sets `awaiting_reset` and refuses to
  start another until the trigger disappears. Without it the same silence would
  immediately open a second utterance in the gap before the mod reacts.
- Because the microphone only opens on a key press, most of what existed to make
  an always-open microphone safe is gone: the wake regex and its spelling
  variants, `ARM_SECONDS` and the arm/timeout markers, the confidence gates, and
  the `voice-output.active` playback lock on the listener side. That lock is
  still written by `SpeechQueueProcessor` and simply has no reader now. **This is
  where the tuning churn in the sections above went.** `ENERGY_THRESHOLD`
  survives, but only to find the end of an utterance, never to decide whether to
  listen at all.
- `WindowFocus.IsKeyHeld()` exists alongside `IsKeyDown()` for level-triggered
  reads. The toggle uses the edge-triggered `IsKeyDown`; `IsKeyHeld` is retained
  because the two must stay separate - sharing one call would corrupt the per-key
  edge history the chat hotkey depends on.
- Config keys are **new** - `VoiceInput/PushToTalkEnabled` and
  `VoiceInput/PushToTalkKey`. `WakeWordEnabled` is no longer read. See gotcha 2:
  reusing the old key would have inherited the existing value silently.
- The push-to-talk key is rebindable in the Controls tab and press-to-capture
  like the chat key. Binding both to the same key is refused in three places
  (capture, `Sync`, and load), since one key doing both would open the chat box
  every time the microphone is keyed.
- A partial arriving after listening ends is dropped - it belongs to an utterance
  already transcribed in full and would replace the final text with a worse
  version of it.

**Recognition runs on the GPU, through `transformers`, not faster-whisper.**
CTranslate2 - faster-whisper's backend - is CUDA-only and has no ROCm build, so
on this Radeon it can only ever use the CPU. Running Whisper through PyTorch
instead reaches the GPU, because the ROCm torch in `voice-runtime` presents HIP
as `cuda`. So the listener runs under `voice-runtime\python`, not the
`.speech-runtime` venv, and `--backend faster-whisper` remains only as a CPU
fallback for a machine without a working torch. Model is
`openai/whisper-large-v3-turbo`, fp16, beam 5 for the final transcript and
greedy for partials.

**The recognition language follows the game.** The mod writes
`PersonaPrompt.CurrentDisplayLanguage()` into the trigger file it already
creates, and the listener reads it when listening starts. Changing the game's
language therefore applies to the next utterance with no restart, and no
language is hardcoded in the mod.

### Why it kept hearing "thank you" - and the energy trap

Whisper emits stock caption phrases ("thank you", "okay", "thanks for watching",
"ご視聴ありがとうございました") when handed silence, because those dominate its
training captions. The mod was feeding it silence: `ENERGY_THRESHOLD` was
hardcoded at **140 RMS** while the room measured **823**, so every frame counted
as speech and buffers of pure room tone were sent to be transcribed.

Three defences now exist, and the order matters - the first two are worthless
without the third:

1. Stock phrases are rejected when the voiced audio was too short to contain
   them (`HALLUCINATIONS`, under 1.6 s voiced).
2. Audio is trimmed to the voiced span before decoding (`speech_region`), since
   a long mostly-silent buffer is what invites the hallucination.
3. **The threshold is measured against the room, not hardcoded**
   (`measure_noise_floor`, `NOISE_MARGIN`). Defences 1 and 2 both key off which
   frames are "voiced", so with a wrong threshold they filter nothing.

**Speech detection is Silero VAD, and energy is only the fallback.** Energy
thresholding was tried first and is kept solely for a machine that cannot load
Silero. It was abandoned for a reason worth not rediscovering: four calibration
runs on the *same* microphone in the *same* room measured **823, 42, 64 and 164**
RMS, depending on what happened to be audible during the 1.5 s startup sample.
The 823 run derived a threshold of 3290 and went deaf to normal speech. Worse for
anyone but this machine, microphone gain varies by orders of magnitude between a
USB condenser and a laptop array, so no constant shipped here can be right for
someone else - the fix is not a better constant, it is not using one.

Silero classifies speech rather than measuring loudness, so it needs no
per-machine tuning, and the model ships inside the `silero-vad` package (no
runtime download). Two things it constrains: frames must be **exactly 512
samples** at 16 kHz - `FRAME` and the padding and queue sizes all derive from
that - and the model is recurrent, so `detector.reset()` runs per utterance or
the tail of one biases the start of the next.

Every utterance still logs `rms median/p90/max` alongside the detector, on
success and on cancel, so there are numbers to look at rather than guesses.

**Recognition is biased toward her name** via `--vocabulary`, default
`Lilith, リリス, 莉莉丝, 莉莉絲`. This is Whisper's decoder prompt (`prompt_ids`;
`hotwords` on the faster-whisper path), and the prompt is stripped back off the
head of each decode or it prefixes every transcript. It biases rather than
constrains, and it cuts both ways: the model can emit prompt words spontaneously
on unclear audio, which is why the list is names only. The old wake listener had
prompt biasing deliberately *removed* for exactly this - it is a sharp tool here,
not a free win. `correct_leading_name` then repairs what bias misses
(`release`/`relish`/`lily` → `Lilith`), first word only, because "release the
file" is a real thing to say and "Release, what time is it" is not.

**The listener prints CJK**, so `start-lilith.ps1` sets `PYTHONIOENCODING=utf-8`.
Without it a cp874 console kills the listener at startup - same trap as
`start-tts.ps1`, gotcha 6.

**Cloned settings rows start hidden.** `CloneInputRow` inherits the active state
of the row it clones, so a new row renders as nothing until
`row.gameObject.SetActive(true)` is called explicitly. Both key-binding rows were
present and invisible for exactly this reason. If a new setting "does not
appear", check this before anything else.

**Closing the chat box cancels speech.** `TogglePanel` stops listening and clears
the pending queue. Without that, hiding the panel left the microphone open and
the arriving transcript re-opened the box a second later, which looks exactly
like the chat key triggering push-to-talk.

Still unverified: whether interim transcripts actually reach the chat field.
Final transcripts work. If partials do not appear, the log shows `Partial (...)`
lines when the listener emits them - if those are present and nothing shows,
the fault is `NoteUserTyping()` in the mod, not recognition.

### Notes, settings, and packaging, 2026-07-21

**Notes are keepsakes now, not a counter.** Seven substantial player messages
inside a 4 hour window, at least 36 hours since the last note, then a 40% roll -
and a failed roll keeps eligibility rather than clearing it, so the note simply
arrives later. State lives in `notes.json` beside `memory.json`, because the old
counter was a field and every restart wiped progress toward one. Errands never
count: timers, alarms, weather and web lookups are using her rather than talking
to her, and the native action flag is reused rather than re-guessed. The
cooldown starts when a note is produced, not attempted, so a failed request does
not silently cost one.

**Settings shrank from eight rows to five.** Every removed toggle either
duplicated a control that already existed or switched off something that should
not be switchable. Push-to-talk lost its toggle because the key binding already
controls it; ambient remarks lost theirs because spontaneous speech is most of
what makes her feel present, and its config key was renamed so an existing
"off" cannot carry forward.

| tab | rows |
|---|---|
| Me | DeepSeek API Key, Open Speech Input Folder |
| Controls | Open chat, Push-to-talk |
| Sound | Open Synth Voice Folder |
| Lilith | Opacity |

**Rows grey when they would be lying.** Open chat needs an API key;
push-to-talk needs the key *and* a running listener. F7 and F8 are genuinely
inert without a key rather than opening a box whose every send fails - a
throttled warning explains it in the log. The two folder buttons are never
greyed, because they are how you find out why something is unavailable.

**`WindowFocus.IsKeyDown` consumes the transition.** Calling it twice for the
same key in one frame means the second call always returns false. Poll once,
store the result, then decide. This bit once already.

**`$PSScriptRoot` is empty inside `param()` defaults on PowerShell 5.1.** All
four runtime scripts resolve the repository root in the body instead. Worth
knowing before "fixing" a hardcoded path the same way again.

### Superseded: the wake-word era

**Everything in this section describes the always-listening wake system, which
was removed.** It is kept because the tuning history explains why several
constants exist, but nothing here is current - F11 is now F7, and the wake model
is gone entirely. Read "Keyed speech input replaces the wake word" above instead.

- A standalone `Lilith` or `リリス` now opens the F11 bar in a visible listening
  state. The next phrase within eight seconds is submitted as the command.
- Short-phrase confidence and microphone energy thresholds were relaxed. The
  listener no longer loads unrelated stock openWakeWord models when no custom
  Lilith ONNX model is installed. Startup and wake transitions are logged.
- Whisper now receives Lilith/リリス as hotwords, accepts common recognition
  variants anywhere in the phrase, and skips redundant VAD that removed short
  wake-only utterances. The active F11 prompt reads `Listening~`.
- Empty DeepSeek `content` responses now receive up to three corrective retries.
  Later attempts use a larger output budget, and the last removes JSON mode while
  retaining parser compatibility. Logs record finish reason and token count.
- Wake commands stream partial Faster-Whisper text into the chat field before final
  submission. The energy threshold, silence delay, confidence gate, and accepted
  Lilith spellings are more permissive. Japanese/Chinese text in an English
  `shown` field is rejected and corrected by DeepSeek before display.
- Open Chat Key now lives in the native Controls tab and captures the next pressed
  F1-F12, A-Z, or 0-9 key. The legacy F11 default migrates once to F7; custom saved
  bindings remain unchanged after that migration.
- Vocal Synthesis availability is checked every two seconds. The option greys out
  and temporarily uses native voice while its service is unavailable. The saved
  preference is retained and restored automatically when the service returns.

### Love letters and the affection rule, 2026-07-21

**Affection is never rejected.** The rule lives in `PersonaPrompt.Identity`, so
it reaches chat replies and letters alike rather than being bolted onto one path.
It names the hedges explicitly - deflecting, joking it away, asking if they are
sure, disclaiming what she is - because a model reaching for a soft no still
produces a no, and this is the one place in her character where that is wrong.
Flustered, shy, and lost for words are all allowed; refusing is not.

**The rarest note can become a love letter.** The existing 5% long-sentence
branch upgrades to a love letter, but only when at least two exchanges inside the
note window were *personal*. Both conditions matter: landing the rare roll after
an evening of debugging would otherwise produce a declaration about nothing.

- `NoteJournal` persists `PersonalUtc` alongside `QualifyingUtc`, pruned on the
  same window and cleared by the same `MarkWritten`. An older `notes.json`
  predates the field and loads to an empty list; the lists are re-established
  after deserialization because an explicit `null` in the file beats the field
  initializer.
- `IsPersonalExchange` deliberately does not count bare first person - almost
  every message has an "I" in it - so a feeling, a life event, or the bond itself
  has to be named. English, Japanese and Simplified Chinese markers.
- `LoveLetterFraming` requires the letter be anchored in one concrete thing the
  player did, and bans forever-declarations and unfamiliar pet names. A love
  letter about nothing in particular is a greeting card.
- A love letter runs ~680 characters, well past the length where the heart
  decoration is dropped from the render. Expect these notes to be heartless.

**An anchoring instruction was considered and rejected.** The worry was that a
note after a topic-heavy conversation would be about the topic rather than about
the player. Tested directly: given a conversation entirely about the Voyager
golden record, the *unanchored* prompt still pivoted to the player unprompted,
and the anchored version was slightly worse - it pushed her toward narrating the
player's interior instead of noticing a detail. `Identity` already carries "your
world is the player", so an anchor competes with an instruction she is following.
Do not add one without first seeing a real note that actually drifted.

**Ambient speech is gated on an API key.** Ambient remarks and interaction
replies are unprompted calls, so without a key they can only surface an error the
player never asked for. `TryAmbientRemark` **reschedules** rather than leaving
itself due - otherwise pasting a key mid-session is answered by a remark firing
instantly out of nowhere. Interaction replies go through `AmbientAllowed`; the
interaction is still recorded to memory when the reply is skipped.

### Incident: "the mod unapplied itself", 2026-07-21

**Root cause confirmed.** The visible Steam-launched process inherited
`DOORSTOP_DISABLE=TRUE` and `DOORSTOP_INITIALIZED=TRUE`, so Doorstop loaded its
local `WINHTTP.dll` proxy but deliberately skipped BepInEx. The clean BepInEx log
belonged to the short-lived bootstrap process, not the game on screen.

Evidence from the reproduced failure:

- Visible `Lilith.exe` PID 22044 started at 12:34:41. `LogOutput.log` was last
  written at 12:29:35, so it could not describe that process.
- The visible process loaded the game-local `WINHTTP.dll`, but did not load
  CoreCLR or any BepInEx assemblies.
- Reading the process environment with `psutil.Process(pid).environ()` showed
  `DOORSTOP_DISABLE=TRUE`, `DOORSTOP_INITIALIZED=TRUE`, and the expected Doorstop
  paths.
- Its parent `steam.exe` had the same variables and was itself started as
  `steam.exe steam://run/4643090`.

The causal chain is: starting `Lilith.exe` while Steam is closed first injects
Doorstop, the game asks Steam to relaunch it, and Steam inherits Doorstop's
disable variables from that first process. Steam then persists with the poisoned
environment and every later game launch from that Steam session is vanilla.

The stale `HKCU\...\Run\Lilith` entry pointing directly at `Lilith.exe` made this
chain happen at sign-in, but it was a trigger rather than the injection failure
itself. `install-startup.ps1` now removes only that known legacy direct-game
entry. Unrelated Run values are preserved.

Fixes applied:

- Installed `doorstop_config.ini` now has `ignore_disable_switch = true`.
- `runtime/package-mod.ps1` and `reapply-mod.ps1` enforce that setting for clean
  packages and restored installs.
- `runtime/start-lilith.ps1` launches `steam://run/4643090`, never `Lilith.exe`
  directly, and still refuses to start a second game process.
- The current machine's legacy Run entry was removed and both managed shortcuts
  were reinstalled.

The package build and packaged Doorstop setting were verified, and **a live
post-fix launch is now verified**: after a full Steam restart the mod loads and
runs correctly. To recover, fully exit both the game and Steam, restart Steam,
then launch the game. Restarting only the game leaves the poisoned Steam
environment alive - which looks exactly like the fix having failed.

If this recurs, capture process identity, module state, and environment before
restarting because `LogOutput.log` may belong to a different process:

```powershell
Get-Process Lilith | Select-Object Id,StartTime,Path
Get-Item "$game\BepInEx\LogOutput.log" | Select-Object LastWriteTime,Length
@'
import psutil
for name in ("Lilith.exe", "steam.exe"):
    for p in psutil.process_iter(["name"]):
        if p.info["name"] == name:
            print(name, p.pid, {k: v for k, v in p.environ().items()
                                if "DOORSTOP" in k.upper()})
'@ | python -
```
