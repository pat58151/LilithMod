# Setup

The installer puts the mod in place and stops there. Four things it does not
do, in the order most people want them.

| | needed for | roughly |
|---|---|---|
| [API key](#1-the-api-key) | anything at all | 5 minutes |
| [Her voice](#2-her-voice) | hearing her speak | an hour, mostly downloading |
| [Speaking to her](#3-speaking-to-her) | F8 | 20 minutes |
| [Starting it all](#4-starting-it-all-by-itself) | not doing this by hand | 1 minute |

Only the first is required. With none of the rest she still chats in text.

---

## 1. The API key

She thinks with DeepSeek's language model, which is a paid API. It is
inexpensive but not free, and the key is yours, not bundled.

1. Sign up at [platform.deepseek.com](https://platform.deepseek.com).
2. Add credit. It is prepaid — with a zero balance every reply fails.
3. Open **API keys**, create one, and copy it. The full key is usually shown
   only at creation.
4. In game: **Settings / Me / DeepSeek API Key**. Paste it. It saves itself.

Press F7 and type something. If she answers, you are done.

The key is written to `BepInEx\config\LilithMod.cfg` in the game folder and is
sent to nothing but the API. Keys are checked when used, not when saved, so a
typo shows up as a failed reply rather than an error on paste — re-paste it
without surrounding spaces.

**Using a different provider.** `[LLM] BaseUrl` and `Model` in that same config
file take any OpenAI-compatible endpoint. The default is
`https://api.deepseek.com/v1` with `deepseek-v4-flash`.

---

## 2. Her voice

Out of the box she writes but does not speak. Her voice comes from
[GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS) running on your own
machine — nothing is uploaded, and a GPU makes it much faster but is not
required. Budget about 2 GB.

**No voice model is included, deliberately.** One trained on the game's audio is
the developers' work and is not mine to distribute, so you bring your own.

1. **Install GPT-SoVITS.** Take the Windows integrated package from its
   releases page — the one that bundles its own Python. Unpack it anywhere.
2. **Get a voice.** Two files, a GPT weight (`.ckpt`) and a SoVITS weight
   (`.pth`). Train your own with the UI it ships (an hour of clean
   single-speaker audio is plenty, ten minutes is usable), use a shared model
   whose licence permits it, or start with its base pretrained model just to
   confirm the plumbing works.
3. **Prepare reference audio.** A 3–10 second WAV, one speaker, no music, calm
   and level — it sets the emotional colour of everything she says — plus its
   exact transcript, punctuation included. *A poor reference is the most common
   cause of bad output. Replace it before touching anything else.*
4. **Configure the mod.** In game, **Settings / Sound / Open Synth Voice
   Folder**. Copy `voice-config.example.ini` to `voice-config.ini` and fill in
   the weights, reference WAV and transcript. `SpokenLanguage` and
   `SubtitleLanguage` are independent — `ja` speech under `en` subtitles is the
   intended default.
5. **Start the server**, from the GPT-SoVITS folder:

   ```
   set PYTHONIOENCODING=utf-8
   runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS\configs\tts_infer.yaml
   ```

   The UTF-8 line is not optional. Without it Japanese text crashes the server
   with an encoding error reported as a misleading `400 tts failed`. If you
   cloned this repository, `start-tts.ps1` does all of the above for you.

6. **Turn it on:** **Settings / Sound**, select *Vocal Synthesis*.

Greyed out means the server is not answering. The mod re-checks every two
seconds and enables the option by itself — start the server and wait rather
than restarting the game.

Loading the model takes ~40 seconds, and the first line after that is slow.
After that, two to five seconds for a short line. Lines she has said before are
instant because synthesised audio is cached; her chat replies are new text
every time and always pay full cost.

The full config reference and a longer troubleshooting list are in that same
folder, in `README.txt`.

> **Changed weights and still hear the old voice?** Change `CacheIdentity`. The
> cache is keyed by it, so audio is reused until you do.

---

## 3. Speaking to her

F8 listens, and submits about 2.5 seconds after you stop. Transcription is
local — no audio leaves the machine.

This part is **not in the release zip**; it needs the scripts from this
repository. Clone it, then from the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File runtime\install-speech-input.ps1
```

That builds a `.speech-runtime` environment with Python 3.12. Then run the
listener, pointing it at the plugin folder inside the game directory:

```powershell
python runtime\push_to_talk.py `
  --output  "<game>\BepInEx\plugins\LilithMod\speech-command.txt" `
  --trigger "<game>\BepInEx\plugins\LilithMod\push-to-talk.active"
```

The first run downloads a speech model and takes a few minutes. `Speech
listener ready` means it is working. The **Push to talk** setting is greyed out
whenever this process is not running and returns within seconds of it starting.

**On a GPU:** NVIDIA users add `--device cuda --compute-type float16`.
faster-whisper is CUDA-only, so on AMD use `--backend transformers` if you have
a working ROCm PyTorch, or accept CPU — a few seconds per sentence.

**If it mishears her name**, pass it with `--vocabulary`. More knobs, and the
rest of the troubleshooting, are in `speech-setup\README.txt` in the plugin
folder.

---

## 4. Starting it all by itself

Also repository-only. `runtime\start-lilith.ps1` brings up the voice server,
the speech listener and the game together, each hidden, with output going to
logs in the plugin folder.

```powershell
powershell -ExecutionPolicy Bypass -File runtime\install-startup.ps1
```

That adds a desktop shortcut and a sign-in entry pointing at the launcher.

---

## Uninstall

The installer has an uninstaller. By hand, delete from the game folder:

```
BepInEx\
winhttp.dll
doorstop_config.ini
.doorstop_version
```

The game returns to normal — the mod only reads its files and adds its own.

`BepInEx\plugins\LilithMod\` holds her memory, her notes and the voice cache.
Those are the only irreplaceable things here; copy them out first if you might
come back.

---

## When something is wrong

| | |
|---|---|
| **The game looks entirely unmodded** | Fully exit the game *and* Steam, restart Steam, launch again. Starting `Lilith.exe` directly while Steam is closed leaves Steam disabling the mod on every later launch. |
| **F7 does nothing** | No API key, or a zero balance. |
| **She replies but says nothing aloud** | Expected until section 2 is done. |
| **First launch seems frozen** | BepInEx is generating interop assemblies from the game. Let it finish — force-quitting can break the next launch too. |
| **Nothing loaded, no log** | `winhttp.dll` must sit directly beside `Lilith.exe`. |

Logs are `BepInEx\LogOutput.log`. It is overwritten on every launch, so copy it
before restarting.
