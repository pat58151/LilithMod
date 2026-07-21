LILITH VOCAL SYNTHESIS SETUP
============================

This is about LILITH'S VOICE - making her speak aloud. For talking to her, see
the speech-setup folder next to this one. The two are independent and either
works without the other. With neither installed she still chats in text.

Nothing about the voice ships with the mod, and that is deliberate: a voice
model trained on the game's own audio is the developers' work, so it is not
mine to distribute. You supply a voice. Instructions below.

You need GPT-SoVITS and a voice model, about 2 GB. It runs entirely on your
computer and nothing is uploaded. A GPU makes it much faster but is not
required.


STEP 1. Install GPT-SoVITS
  Download the Windows integrated package from the GPT-SoVITS project
  (github.com/RVC-Boss/GPT-SoVITS - see its releases page). Unpack it
  anywhere, for example next to this folder.

  You want the package that includes its own Python. Building it from source
  works too but is considerably more effort.

STEP 2. Get a voice
  You need two files: a GPT weight (.ckpt) and a SoVITS weight (.pth).

  Pick one:
    a) Train your own. GPT-SoVITS ships a training UI. Roughly an hour of
       clean single-speaker audio produces a good result; ten minutes gives a
       usable one.
    b) Use a voice model someone else has shared, if its licence permits.
    c) Use the base pretrained model that comes with GPT-SoVITS. It will not
       sound like Lilith, but it verifies your setup works.

STEP 3. Prepare reference audio
  GPT-SoVITS clones the tone of a short sample every time it speaks.

    - 3 to 10 seconds, WAV
    - one speaker, no music, no background noise
    - calm and level - the reference sets the emotional colour of everything
    - write down its transcript EXACTLY, including punctuation

  A poor reference is the most common cause of bad output. If the voice sounds
  wrong, replace the reference before you touch anything else.

STEP 4. Start the server
  Run api_v2.py from the GPT-SoVITS folder. It listens on port 9880.

  IMPORTANT: set the console to UTF-8 first, or Japanese and Chinese text will
  crash the server with a UnicodeEncodeError that is reported as a misleading
  "400 tts failed":

      set PYTHONIOENCODING=utf-8
      runtime\python.exe api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS\configs\tts_infer.yaml

  Leave that window open. Loading the model takes around 40 seconds.

STEP 5. Configure the mod
  Copy voice-config.example.ini to voice-config.ini in this folder, then edit
  it. Paths may be relative to this folder, may use environment variables such
  as %USERPROFILE%, or may be absolute.

  At minimum set, under the profile for your spoken language:
    GptWeights, SovitsWeights, RefAudioPath, PromptText, PromptLanguage

  And under [Voice]:
    SpokenLanguage     what she speaks      ja, en, or zh
    SubtitleLanguage   what you read        ja, en, or zh

  These are independent. Japanese speech with English subtitles is a valid and
  intended combination.

STEP 6. Turn it on in game
  Settings -> Sound -> select "Vocal Synthesis" as the voice.

  If the option is greyed out, the server is not reachable. The mod checks
  every two seconds and re-enables the option by itself once it responds, so
  start the server and wait a moment rather than restarting the game.


-------------------------------------------------------------------------
CONFIG REFERENCE
-------------------------------------------------------------------------

[Voice]
  Enabled            false disables synthesis entirely. Chat still works.
  ReplaceGameVoice   also replace the game's own scripted dialogue voice.
                     Has no effect in a released build: replacing that dialogue
                     needs a translation of the game's script, which is the
                     developers' content and is not distributed. Her own replies
                     are unaffected and are spoken normally either way.
  SpokenLanguage     ja, en, or zh - what she says aloud.
  SubtitleLanguage   ja, en, or zh - what appears on screen.
  Endpoint           default http://127.0.0.1:9880/tts
  RuntimePath        GPT-SoVITS folder. Only used by the launcher script.
  ServerConfig       server yaml. Only used by the launcher script.

[Profile.ja] / [Profile.en] / [Profile.zh]
  CacheIdentity      names this voice. Synthesised audio is cached on disk, so
                     CHANGE THIS whenever you change weights or reference
                     audio - otherwise you keep hearing the old voice.
  GptWeights         .ckpt
  SovitsWeights      .pth
  RefAudioPath       your reference WAV
  PromptText         its exact transcript
  PromptLanguage     the language of the reference audio
  WarmUpText         spoken once at startup to warm the model, never heard


-------------------------------------------------------------------------
TROUBLESHOOTING
-------------------------------------------------------------------------

"Vocal Synthesis" is greyed out
  The server is not answering. Check its window is still open and that nothing
  else is using port 9880.

400 tts failed, or the server dies on Japanese text
  The console is not UTF-8. Set PYTHONIOENCODING=utf-8 before starting it.

The first line after starting the server is slow
  Loading the model takes around 40 seconds, and the first synthesis after that
  is slower than the rest. After that, expect roughly two to five seconds for a
  short line, longer for a long one - cost scales with the length of the text.

  If it is much slower than that and stays slow, see the note on parallel
  inference below.

Lines she has said before are instant, new ones are not
  Expected. Synthesised audio is cached on disk under voice-cache\, keyed by the
  text and the voice, so a repeated line is read from the file rather than
  generated again. Her chat replies are new text every time and can never be
  cached, so those always pay full synthesis time.

Everything is several times slower than the timings above
  On some GPU stacks - AMD/ROCm in particular - batched inference falls back to
  undersized workspaces and is many times slower than serial. The mod already
  sends parallel_infer=false on every request for this reason. If you drive the
  server yourself with your own script, send it too.

Still the old voice after changing weights
  Change CacheIdentity. Cached audio is reused until you do. The cache key
  covers the model name, the reference audio and its transcript, so changing
  any of those without changing CacheIdentity leaves you hearing the old voice
  for every line you have already heard.

  Old cache files are never deleted. If you change voices often, voice-cache\
  is safe to empty by hand - it only costs the time to generate lines again.

The voice sounds wrong, flat, or unstable
  Replace the reference audio. Short, clean, calm, correct transcript. This is
  the cause far more often than the model is.

Anything about speaking TO her
  See the speech-setup folder. That is a separate system with its own setup.


-------------------------------------------------------------------------
PRIVACY AND LICENSING
-------------------------------------------------------------------------

Everything here is local. Synthesis runs on your machine and no audio is
uploaded. Only your typed or spoken message text goes to the language model,
and your API key stays in the game's own config file.

Voice models trained on the game's audio are derived from the developers'
work. Keep them to yourself.
