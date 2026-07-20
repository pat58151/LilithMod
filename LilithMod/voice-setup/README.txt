LILITH VOCAL SYNTHESIS SETUP
============================

This mod can speak with a synthesised voice and can listen to you. Both are
OPTIONAL. With neither installed, Lilith still chats normally in text.

Nothing about the voice ships with the mod, and that is deliberate: a voice
model trained on the game's own audio is the developers' work, so it is not
mine to distribute. You supply a voice. Instructions below.


WHAT YOU NEED
-------------
  Voice output (Lilith speaks)   GPT-SoVITS + a voice model. ~2 GB.
  Voice input  (you speak)       Python + Whisper. ~2 GB.

Both run entirely on your computer. Nothing is uploaded. A GPU makes both far
faster but neither requires one.


-------------------------------------------------------------------------
PART 1 - VOICE OUTPUT
-------------------------------------------------------------------------

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
PART 2 - VOICE INPUT (OPTIONAL)
-------------------------------------------------------------------------

Press F8, speak, stop. After 2.5 seconds of silence what you said is
transcribed and sent. Press F8 again to cancel. F7 opens the same bar to type.
Both keys are rebindable under Settings -> Controls.

INSTALL
  Needs Python 3.10 or newer, then:

      pip install faster-whisper sounddevice numpy silero-vad

  Run runtime\push_to_talk.py, pointing it at the mod's plugin folder:

      python runtime\push_to_talk.py ^
        --output  "<plugin folder>\speech-command.txt" ^
        --trigger "<plugin folder>\push-to-talk.active"

  <plugin folder> is BepInEx\plugins\LilithMod inside the game directory.

  The first run downloads a speech model (~1.5 GB) and takes a few minutes.

GPU
  NVIDIA:  add --device cuda --compute-type float16
  AMD:     faster-whisper cannot use your GPU - its backend is CUDA-only. If
           you have a working ROCm build of PyTorch, use
           --backend transformers instead, which reaches the GPU through it.
  Neither: the default runs on CPU. Expect a few seconds per sentence.

ACCURACY
  --language en           pin the language. Auto-detection on short audio is
                          unreliable and a wrong guess ruins the transcript.
                          The mod overrides this per utterance with your game
                          display language.
  --vocabulary "Lilith"   bias recognition toward names it would otherwise
                          mangle. Keep the list short - the model can also
                          start emitting these words on unclear audio.
  --whisper-model         small is fast, large-v3 is accurate.


-------------------------------------------------------------------------
CONFIG REFERENCE
-------------------------------------------------------------------------

[Voice]
  Enabled            false disables synthesis entirely. Chat still works.
  ReplaceGameVoice   also replace the game's own scripted dialogue voice.
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

The first line takes 10+ seconds, later ones are fast
  Normal. The model compiles per sentence length, so new lengths are slow once
  each. It settles as you use it.

Still the old voice after changing weights
  Change CacheIdentity. Cached audio is reused until you do.

The voice sounds wrong, flat, or unstable
  Replace the reference audio. Short, clean, calm, correct transcript. This is
  the cause far more often than the model is.

Nothing is transcribed when I speak
  Check push_to_talk.py's console. It prints "Listening." on F8 and the levels
  it heard. If it never prints, the trigger path is wrong.

She hears "thank you" when the room is silent
  Whisper invents stock phrases from silence. The listener filters these and
  uses voice detection to avoid transcribing near-silence; if it persists, your
  microphone gain is very high.


-------------------------------------------------------------------------
PRIVACY AND LICENSING
-------------------------------------------------------------------------

Everything here is local. Audio is never uploaded; recognition and synthesis
both run on your machine. Only your typed or spoken message text goes to the
language model, and your API key stays in the game's own config file.

Voice models trained on the game's audio are derived from the developers'
work. Keep them to yourself.
