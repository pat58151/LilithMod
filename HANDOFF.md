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
   Or hold **F8** and speak.
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
| `LlmChatController.cs` | F11 UI, DeepSeek calls, chat history, reply parsing, subtitles |
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
| `D:\Lilith\backup-preinstall-20260720-1508\` | configs incl. API key, dumps, reference WAVs | 7 MB |

---

## 5. How the voice path works

```
F11 -> LlmChatController -> one LLM call
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

10. **A running listener survives deleting its `.py` file.** Python loads the
    source once, so an old `runtime/*.py` helper keeps running - holding the
    microphone and writing handoff files nothing reads - long after the file is
    gone and the launcher has been repointed. Editing `start-lilith.ps1` only
    takes effect on the *next* launcher run. After changing anything under
    `runtime/`, check `Get-CimInstance Win32_Process -Filter "Name='python.exe'"`
    and kill the stale process explicitly. This is the same class of mistake as
    gotcha 1: believing a code change took effect without confirming which
    process is actually running.

11. **The API key** lives in `BepInEx\config\LilithMod.cfg`, is user-supplied,
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
- Audio was generated, cached, and played without an error, but no human ear
  check was performed during automated verification.
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
- Letters: every third meaningful conversation can create a DeepSeek-written note
  through `NoteImageSaver` and notify the native inbox.
- Settings: masked DeepSeek key bar, reveal toggle, voice replacement,
  60% click-through, wake words, and ambient speech under the native Lilith tab.
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
`.wake-runtime` venv, and `--backend faster-whisper` remains only as a CPU
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

### Follow-up changes, 2026-07-21

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
