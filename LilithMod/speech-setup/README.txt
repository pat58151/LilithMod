LILITH SPEECH INPUT SETUP
=========================

This is about TALKING TO Lilith. For her voice, see the voice-setup folder
next to this one - the two are independent and either works without the other.

Press F8, speak, then stop. After 2.5 seconds of silence what you said is
transcribed and sent. Press F8 again to cancel. F7 opens the same bar to type
instead. Both keys are rebindable under Settings -> Controls.

Everything runs on your computer. No audio leaves the machine.


WHAT YOU NEED
-------------
Python 3.10 or newer, about 2 GB of disk for the speech model, and a
microphone. A GPU helps a great deal but is not required.


INSTALL
-------
From the mod's repository folder:

    powershell -ExecutionPolicy Bypass -File runtime\install-speech-input.ps1

That creates a .speech-runtime environment and installs what is needed. Or do
it by hand into any Python you like:

    pip install faster-whisper sounddevice numpy silero-vad


RUN IT
------
    python runtime\push_to_talk.py ^
      --output  "<plugin folder>\speech-command.txt" ^
      --trigger "<plugin folder>\push-to-talk.active"

<plugin folder> is BepInEx\plugins\LilithMod inside the game directory - the
folder this file was installed into.

The first run downloads a speech model and takes a few minutes. When it prints
"Speech listener ready" it is working.

runtime\start-lilith.ps1 starts this for you along with everything else, if you
would rather not run it by hand.

The Push to talk setting greys out whenever this process is not running, and
re-enables itself within a few seconds of it coming back. If the setting is
grey, this is not running.


USING A GPU
-----------
  NVIDIA    add: --device cuda --compute-type float16
  AMD       faster-whisper cannot use your GPU. Its backend is CUDA-only and
            has no ROCm build. If you have a working ROCm build of PyTorch,
            use --backend transformers instead, which reaches the GPU through
            it. Otherwise it runs on CPU.
  Neither   the default is CPU. Expect a few seconds per sentence.


ACCURACY
--------
  --whisper-model      small is fast, large-v3 is accurate,
                       openai/whisper-large-v3-turbo is a good middle with
                       --backend transformers.

  --language           pin the recognition language. Auto-detection on short
                       audio is unreliable and a wrong guess ruins the whole
                       transcript. The mod overrides this per utterance with
                       your current game display language.

  --vocabulary         bias recognition toward words it would otherwise mangle,
                       such as her name. Keep the list short and specific: the
                       model can also start emitting these words on unclear
                       audio.

  --silence            seconds of quiet that end an utterance. Default 2.5.

  --vad                silero (default) classifies speech directly and needs no
                       tuning. energy is a fallback that compares loudness
                       against the room, and needs different settings on every
                       microphone.


TROUBLESHOOTING
---------------
The Push to talk setting is greyed out
  The listener is not running. Start it, and the setting returns by itself
  within a few seconds.

Nothing happens when I press F8
  Same cause. Check the listener's console: it prints "Listening." on F8.

It hears nothing, or says almost nothing was voiced
  The console prints the levels it measured after every attempt. If the voiced
  figure is near zero, your microphone is not the default input device, or its
  volume is very low.

It transcribes "thank you" or "okay" from silence
  Whisper invents stock phrases when handed near-silence. The listener filters
  the common ones and uses voice detection to avoid decoding silence at all.
  Persisting usually means a very high microphone gain.

Her name comes out as "release" or "Lily"
  Add it to --vocabulary. Recognition is biased toward those words, and common
  mishearings of her name are corrected when they open a sentence.

Words appear while I speak, then change when I stop
  Expected. Interim text is a fast guess; the final transcript is decoded more
  carefully once the utterance ends. Typing at any point discards both and
  keeps what you typed.
