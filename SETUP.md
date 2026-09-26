# Setup

The installer only installs the mod. This guide covers the rest.

| Step | Needed for | Time |
|---|---|---|
| [1. AI service](#1-ai-service) | everything | 5 minutes |
| [2. Her voice](#2-her-voice) | hearing her speak | about an hour, mostly downloads |
| [3. Speech input](#3-speech-input) | F8 | 20 minutes |
| [4. Auto start](#4-auto-start) | starting services with the game | 1 minute |

Only step 1 is required. Without the others she still chats in text.

---

## 1. AI service

The mod needs an AI service that uses the OpenAI API format. The default is
DeepSeek. It is paid but cheap. You bring your own key. You can also use a
local AI instead; see [Other AI services](#other-ai-services).

1. Sign up at [platform.deepseek.com](https://platform.deepseek.com).
2. Add credit. It is prepaid. With a zero balance every reply fails.
3. Open **API keys**, create a key, and copy it. The full key is usually shown
   only once.
4. In game, open **Settings / Me / API Key** and paste it. It saves
   automatically.

Press F7 and type something. If she answers, you are done.

The key is saved in `BepInEx\config\LilithMod.cfg` in the game folder. It is
sent only to the AI service. The key is not checked when you paste it. A wrong
key shows up as a failed reply. If that happens, paste it again without spaces.

### Other AI services

In game, **Settings / Other / Configure AI Service** opens a file with the
endpoint, the model, and an optional SearXNG server. It starts with DeepSeek
(`https://api.deepseek.com/v1`, `deepseek-v4-flash`).

Any service that uses the OpenAI API format works:

- Hosted: OpenAI, OpenRouter, Groq, Mistral, xAI, Gemini, Together, Moonshot,
  Qwen.
- Local: Ollama, LM Studio, llama.cpp, vLLM.

Local example:

```
BaseUrl = http://localhost:8080/v1
Model = qwen3.5:9b
```

Gemini example:

```
BaseUrl = https://generativelanguage.googleapis.com/v1beta/openai
Model = gemini-3.6-flash
```

Gemini URLs ending in `/v1` or `/v1beta` also work. The mod redirects them.

Hosted services need their key in **Settings / Me / API Key**. Local servers
need no key, and chat stays on your PC. Use an instruct model of about 7B or
larger. Smaller models and base models often break her reply format.

**Thinking models.** Some local servers turn on thinking by default, for
example for Qwen3. The mod asks local servers not to think, removes thinking
text from replies and notes, and allows longer replies once it sees a model
think. Thinking still adds seconds to each reply. For faster replies, use a
model without thinking, turn it off in the server, or start `llama-server`
with `--reasoning off`.

**AI on another PC.** Set `BaseUrl` to that PC's network address:

```
BaseUrl = http://192.168.1.14:1234/v1
Model = qwen3.5:9b
```

On the PC running the AI:

- The server must accept network connections. In LM Studio, turn on *Serve on
  Local Network*. For Ollama, set `OLLAMA_HOST=0.0.0.0`.
- Windows Firewall must allow the port. To test, open
  `http://<address>:<port>/v1/models` in a browser on the game PC.

The mod treats these as local servers: home network addresses (`192.168.x`,
`10.x`, `172.16-31.x`), Tailscale, local IPv6 addresses, `.local` names, and
plain PC names. If replies fail with an error about `response_format.type`,
update the mod.

**Local web search.** Run SearXNG yourself and add its address to the same
file:

```
SearXngUrl = http://127.0.0.1:8080
```

The mod uses this server first and falls back to public SearXNG servers. Leave
it blank to use public servers only.

---

## 2. Her voice

By default she does not speak. Her voice comes from
[GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS), which runs on your PC.
Nothing is uploaded. A GPU makes it much faster but is not required. It needs
about 2 GB of disk space.

**The installer can do steps 1 and 5.** Tick *voice synthesis base* during
setup. It downloads GPT-SoVITS, Python, and the base models into
`%LOCALAPPDATA%\LilithMod`, and sets the server to start with the game. You
still do steps 2 to 4, because no voice is included. To run the same script
later, use `voice-setup\install-voice-synth.ps1` in the plugin folder.

No voice model is included. You pick her voice. Train one from audio you
like, or use a model someone has shared.

1. **Install GPT-SoVITS.** Download the Windows package that includes Python
   from its releases page. Unzip it anywhere.
2. **Get a voice.** You need two files: a GPT weight (`.ckpt`) and a SoVITS
   weight (`.pth`). Options:
   - Train your own in the GPT-SoVITS UI. One hour of clean audio from one
     speaker is plenty. Ten minutes works.
   - Use a shared model whose license allows it.
   - Use the base model to test that everything works.
3. **Prepare reference audio.** One WAV file, 3 to 10 seconds, one speaker, no
   music, calm tone. Write down its exact transcript with punctuation. The tone
   of this clip sets the tone of everything she says. *A bad reference clip is
   the most common cause of bad output. Replace it before changing anything
   else.*
4. **Configure the mod.** In game, open **Settings / Lilith / Open Vocal
   Synthesis Folder**. Copy `voice-config.example.ini` to `voice-config.ini`.
   Fill in the weights, the reference WAV, and its transcript.
   - `SpokenLanguage`: the language of your voice model.
   - `SubtitleLanguage`: `auto` by default, which follows the game's language.
     Set `ja`, `en`, `zh`, `ru`, `th`, or `pt` to force one.
   - `ru`, `th`, and `pt` are subtitle only. GPT-SoVITS cannot speak them. She
     speaks your `SpokenLanguage` and writes replies, notes, and subtitles in
     the subtitle language.
5. **Start the server** from the GPT-SoVITS folder:

   ```
   set PYTHONIOENCODING=utf-8
   runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS\configs\tts_infer.yaml
   ```

   Do not skip the first line. Without it, Japanese text crashes the server
   with a misleading `400 tts failed` error. If you cloned this repository,
   `start-tts.ps1` does this step for you.

6. **Turn it on.** In **Settings / Language**, on the voice row, pick *Vocal
   Synthesis*. It is selected by default.

### How the voice behaves

- **Server down.** *Vocal Synthesis* turns grey but stays selected. Cached game
  lines still play. Other lines show subtitles with no voice. The mod checks
  the server every two seconds and resumes when it answers.
- **Speed.** Loading the model takes about 40 seconds. The first line after
  that is slow. After that, a short line takes 2 to 5 seconds.
- **Cache.** Game lines are cached because they repeat. Chat replies are never
  cached.
- **Super Lilith.** **Settings / Lilith / Super Lilith** turns on AI rewrites
  of the game's own dialogue. Each rewritten line and its voice are prepared
  ahead of time and deleted after playing once. Rewrites stay about the same
  length as the original line. If a rewrite fails, the original text is used.
- **Notes.** Default notes are always rewritten when an AI service is set up.

The full config reference and more troubleshooting are in `README.txt` in the
same folder.

> **Changed the weights but still hear the old voice?** Change
> `CacheIdentity`. The cache uses it as its key, so old audio is reused until
> you change it. On the next start the old clips are deleted and new ones are
> made as needed.

**Clearing the cache by hand.** Close the game. Run `clear-voice-cache.ps1` in
the plugin folder's `voice-setup` folder. Options:

- `-Scope audio`: cached game lines.
- `-Scope once`: prepared Super Lilith rewrites.
- `-Scope text`: learned translations and name pronunciations.
- `-Scope all` (default): all of the above.

---

## 3. Speech input

Press F8 and speak. Your speech is sent about 1.5 seconds after you stop.
Recognition runs on your PC. No audio leaves it.

After speech input is installed, **Settings / Me / Enable Push to talk**
pauses or resumes the listener. It does not affect the voice server. The switch
is disabled until speech input is installed.

**The installer can do this.** Tick *speech input* during setup and skip the
rest of this section. To install by hand, run this from the plugin folder or a
repository clone:

```powershell
powershell -ExecutionPolicy Bypass -File speech-setup\install-speech-input.ps1
```

It creates a Python 3.12 environment in `%LOCALAPPDATA%\LilithMod`. You do not
need Python installed. The listener then starts with the game. To run the
listener by hand:

```powershell
python runtime\push_to_talk.py `
  --output  "<game>\BepInEx\plugins\LilithMod\speech-command.txt" `
  --trigger "<game>\BepInEx\plugins\LilithMod\push-to-talk.active"
```

The first run downloads a speech model and takes a few minutes. When you see
`Speech listener ready`, it works. F8 does nothing while the listener is not
running.

**GPU.**

- NVIDIA: add `--device cuda --compute-type float16`.
- AMD: faster-whisper supports only NVIDIA. Use `--backend transformers` if you
  have ROCm PyTorch working. Otherwise it runs on the CPU at a few seconds per
  sentence.

**It mishears her name.** Add the name with `--vocabulary`. More options and
troubleshooting are in `speech-setup\README.txt` in the plugin folder.

---

## 4. Auto start

If you ticked either installer box, this already works. The mod finds the
launcher in `%LOCALAPPDATA%\LilithMod` and starts the services with the game.

From a repository clone, `runtime\start-lilith.ps1` starts the voice server,
the speech listener, and the game. Each runs hidden and writes logs to the
plugin folder. To start them at sign-in:

```powershell
powershell -ExecutionPolicy Bypass -File runtime\install-startup.ps1
```

This adds a desktop shortcut and a sign-in entry. Both start the services and
launch the game through Steam.

---

## Changing her personality

Her personality is in `BepInEx\config\LilithPersona.txt`. The mod creates it on
first run. Edits apply on her next reply. No restart needed. Lines starting
with `#` are ignored.

To restore the default, delete the file. The mod recreates it on next start.

The file sets who she is and what she remembers. Her language, reply format,
and actions are fixed in the mod, because editing them would stop her replies.
Custom personas are not supported. If she acts strangely, delete the file
before reporting a bug.

---

## What she sends out

All of this goes to the AI service from step 1. With a local server, none of it
leaves your PC. With a hosted service, it goes to that company under its terms.

- What you type or say, and the recent conversation.
- Her persona, and her notes about you.
- **Super Lilith** (*Settings / Lilith / Super Lilith*, off by default): the
  game's dialogue lines and default note text, so she can rewrite them. Turn
  it off to use the original game text.
- **Screenshots of your whole screen**, only when Super Lilith is on *and*
  `MultiModal = true` in the AI service file. A screenshot is sent when your
  message may be about your screen, and with some of her own remarks (20% by
  default). With `MultiModal = false`, the default, no screenshot is taken. To
  keep a vision model but stop screenshots, set `ScreenSight = false` under
  `[Companion]` in `BepInEx\config\LilithMod.cfg`, or lower
  `ScreenSightChance`.
- Her web searches. These go to SearXNG: public servers, or your own if you set
  `SearXngUrl`.

Other traffic:

- Your API key goes only to the AI service.
- Her voice and your speech recognition run on your PC.
- Asking about the weather sends your approximate location to `ip-api.com` and
  `open-meteo.com`.

---

## Uninstall

Use the uninstaller. To remove by hand, delete these from the game folder:

```
BepInEx\
winhttp.dll
doorstop_config.ini
.doorstop_version
```

The game returns to normal.

`BepInEx\plugins\LilithMod\` holds her memory, her notes, and the voice cache.
Copy it somewhere first if you might reinstall.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| **The game looks unmodded** | Close the game *and* Steam. Start Steam, then the game. Starting `Lilith.exe` directly while Steam is closed makes Steam disable the mod on later launches. |
| **F7 does nothing** | No API key, or zero balance. (Not needed with a local server.) With no key, the Chat key and Push to talk rows under Settings / Controls are grey. |
| **Push to talk is grey but Chat key is not** | The key is fine. The speech listener is not running, or **Settings / Me / Enable Push to talk** is off. See step 3. |
| **She repeats lines about static or interference** | She could not reach the AI. Check the key and balance. On a local server, check it is running with a model loaded. |
| **She replies but does not speak** | Normal until step 2 is done. |
| **First launch looks frozen** | BepInEx is preparing files from the game. Wait. Force quitting can break the next launch too. |
| **Nothing loads and there is no log** | `winhttp.dll` must be in the same folder as `Lilith.exe`. |

The log is `BepInEx\LogOutput.log`. It is overwritten on each launch, so copy
it before restarting.
