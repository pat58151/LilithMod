"""Speech-to-text listener for LilithMod.

The mod toggles listening with a key (default F8) by creating and deleting a
trigger file. While that file exists this process records, streams interim
transcripts back, and finalises the utterance after a fixed run of silence.

The mod, not a wake model, decides when the microphone is open. That is why
there is no wake word, no arm window, and no confidence gate here: the only
judgement this process still makes is where the utterance *ends*, which is
plain energy-based endpointing.

Two recognition backends:

* ``transformers`` runs Whisper through PyTorch, which on this machine means
  ROCm on the Radeon GPU. This is the default and is much faster and more
  accurate than the alternative.
* ``faster-whisper`` runs on the CPU. Its CTranslate2 backend is CUDA-only and
  has no ROCm build, so it cannot use this GPU at all - it is the fallback for
  a machine without a working torch.

Backends are imported lazily so each runtime only needs the one it uses.
"""

from __future__ import annotations

import argparse
import os
import queue
import tempfile
import threading
import time
from pathlib import Path

import numpy as np
import sounddevice as sd

RATE = 16_000
FRAME = 512                      # 32 ms; Silero requires exactly this at 16 kHz
FRAME_SECONDS = FRAME / RATE
PARTIAL_INTERVAL = 0.25          # seconds between interim decodes
SILENCE_SECONDS = 2.5            # trailing silence that ends an utterance
MAX_SECONDS = 60.0               # hard cap, so a stuck trigger cannot grow without bound
MIN_SPEECH_SECONDS = 0.45        # total voiced audio required before decoding at all
SPEECH_PAD_SECONDS = 0.3         # keep this much either side of the voiced region
SPEECH_PAD_FRAMES = int(SPEECH_PAD_SECONDS / FRAME_SECONDS)

# Energy fallback only. These exist for a machine that cannot load Silero, and
# they are the reason Silero is the default: an RMS threshold is meaningless
# across different microphones and rooms, so any constant shipped here is wrong
# for somebody. Measured on one machine, the room varied 20x between runs.
ENERGY_FLOOR = 90.0
NOISE_MARGIN = 2.0
CALIBRATION_SECONDS = 1.5
PARTIAL_MARKER = "__LILITH_PTT_PARTIAL__"
SUPPORTED_LANGUAGES = {"en", "ja", "zh"}

# Whisper emits stock caption phrases when handed silence or noise - they are
# frequent in its training captions, so near-empty audio decodes to one of these
# rather than to nothing. Any of them appearing is evidence of hallucination, not
# of speech, so they are dropped when the voiced audio was too short to contain
# them. Compared after lowercasing and stripping punctuation.
HALLUCINATIONS = {
    "thank you", "thanks", "thank you very much", "thanks for watching",
    "thank you for watching", "thanks for watching!", "bye", "goodbye",
    "you", "please subscribe", "subscribe", "okay", "ok", "oh", "uh", "um",
    "hmm", "mm", "mhm", "yeah", "yes", "no", "so", "the", "and", "i",
    "ご視聴ありがとうございました", "ありがとうございました", "おやすみなさい",
    "はい", "え", "あの", "字幕", "谢谢观看", "谢谢大家", "好", "嗯",
    "请不吝点赞 订阅 转发 打赏支持明镜与点点栏目",
}
# Exact matches are rejected regardless of how long the audio was. Duration was
# tried as the discriminator first and leaked: noise that sustained past the
# limit still decoded to "Bye". A whole utterance that reduces to one stock word
# is a mismatch whatever its length - real speech of that duration produces more
# words than that. The cost is that a bare "okay" as an entire message is never
# heard, which is a fair trade for never inventing one.


class SileroDetector:
    """Neural speech/non-speech classifier.

    The point of this over an energy threshold is that it needs no per-machine
    tuning: it judges whether a frame contains speech, not whether it is louder
    than some constant. A quiet speaker on a low-gain laptop microphone and a
    loud one in a noisy room both work, which an RMS threshold cannot deliver
    because the right constant differs per microphone and per room.
    """

    def __init__(self, threshold: float):
        import torch
        from silero_vad import load_silero_vad

        self._torch = torch
        self._model = load_silero_vad()
        self._threshold = threshold

    def describe(self) -> str:
        return f"silero(threshold={self._threshold})"

    def reset(self) -> None:
        # The model is recurrent, so state from the previous utterance would
        # otherwise leak into the next one.
        self._model.reset_states()

    def is_speech(self, frame: np.ndarray) -> bool:
        audio = self._torch.from_numpy(frame.astype(np.float32) / 32768.0)
        with self._torch.inference_mode():
            probability = float(self._model(audio, RATE).item())
        return probability >= self._threshold


class EnergyDetector:
    """Fallback: louder than the room counts as speech."""

    def __init__(self, threshold: float):
        self._threshold = threshold

    def describe(self) -> str:
        return f"energy(threshold={self._threshold:.0f})"

    def reset(self) -> None:
        pass

    def is_speech(self, frame: np.ndarray) -> bool:
        return float(np.sqrt(np.mean(frame.astype(np.float32) ** 2))) > self._threshold


def measure_noise_floor(seconds: float = CALIBRATION_SECONDS) -> float:
    """Sample the room so the voice threshold is relative to it.

    A fixed threshold is the wrong shape for this: too high and quiet speech is
    never voiced, too low and room tone counts as speech, which is what makes
    Whisper decode silence into stock phrases. The 75th percentile ignores the
    odd transient without chasing a single loud frame.
    """
    frames = int(seconds * RATE / FRAME)
    captured = sd.rec(frames * FRAME, samplerate=RATE, channels=1, dtype="int16")
    sd.wait()
    samples = captured[:, 0].astype(np.float32)
    if samples.size == 0:
        return ENERGY_FLOOR
    blocks = samples[:frames * FRAME].reshape(frames, FRAME)
    energies = np.sqrt(np.mean(blocks ** 2, axis=1))
    return float(np.percentile(energies, 75))


# Whisper reliably lands on these instead of her name, most often when it opens
# the sentence and has no context yet. Corrected only in first position: "release
# the file" is a real thing to say, "Release, what time is it" is not.
NAME_CONFUSIONS = {
    "release", "relish", "relist", "leelith", "lilith's", "lilis", "lilies",
    "lily", "lilly", "lili", "lillie", "little", "elizabeth",
}


def correct_leading_name(text: str, canonical: str) -> str:
    if not text or not canonical:
        return text
    words = text.split()
    if not words:
        return text
    head = normalise(words[0])
    if head not in NAME_CONFUSIONS:
        return text
    # Keep whatever punctuation followed the misheard word.
    trailing = "".join(c for c in words[0] if not c.isalnum())
    words[0] = canonical + trailing
    return " ".join(words)


def normalise(text: str) -> str:
    stripped = "".join(
        character for character in text.lower()
        if character.isalnum() or character.isspace() or ord(character) > 0x2FFF
    )
    return " ".join(stripped.split())


class TransformersBackend:
    """Whisper via PyTorch. Uses the GPU when torch reports one, ROCm included."""

    def __init__(self, model_id: str, language: str, device: str = "auto"):
        import torch
        from transformers import WhisperForConditionalGeneration, WhisperProcessor

        self._torch = torch
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self._device = device
        # fp16 on the GPU halves both memory and time; CPU fp16 is slower than fp32.
        self._dtype = torch.float16 if device != "cpu" else torch.float32
        self._language = language or None

        self._processor = WhisperProcessor.from_pretrained(model_id)
        self._model = WhisperForConditionalGeneration.from_pretrained(
            model_id, torch_dtype=self._dtype).to(device)
        self._model.eval()
        self._prompt_ids = None

    def set_vocabulary(self, words: str) -> None:
        """Bias decoding toward names and terms Whisper would otherwise mangle.

        This is Whisper's decoder prompt: the words are fed in as prior context,
        so spellings like "Lilith" become likely instead of "Lilith" being heard
        as "Lily" or "little". It is a bias, not a constraint - and it cuts both
        ways, because the model can also emit prompt words spontaneously when the
        audio is unclear. Keep the list short and specific for that reason; a long
        list of common words makes the transcript drift toward them.
        """
        if not words.strip():
            self._prompt_ids = None
            return
        self._prompt_ids = self._processor.get_prompt_ids(
            words.strip(), return_tensors="pt").to(self._device)

    def describe(self) -> str:
        name = (self._torch.cuda.get_device_name(0)
                if self._device != "cpu" else "cpu")
        return f"transformers/{self._device} ({name})"

    def set_language(self, language: str) -> None:
        self._language = language or None

    def transcribe(self, samples: np.ndarray, beam_size: int = 1) -> str:
        torch = self._torch
        audio = samples.astype(np.float32) / 32768.0
        features = self._processor(
            audio, sampling_rate=RATE, return_tensors="pt"
        ).input_features.to(self._device, self._dtype)

        with torch.inference_mode():
            ids = self._model.generate(
                features,
                language=self._language,
                task="transcribe",
                num_beams=beam_size,
                prompt_ids=self._prompt_ids,
                # The utterance is already endpointed, so there is nothing to
                # gain from letting it ramble past the audio.
                condition_on_prev_tokens=False,
            )
        text = self._processor.batch_decode(ids, skip_special_tokens=True)[0].strip()
        # The prompt is echoed back at the head of the decode; strip it or the
        # bias words end up in every transcript.
        if self._prompt_ids is not None:
            prompt = self._processor.batch_decode(
                self._prompt_ids.unsqueeze(0), skip_special_tokens=True)[0].strip()
            if prompt and text.startswith(prompt):
                text = text[len(prompt):].strip()
        return text


class FasterWhisperBackend:
    """CPU fallback. CTranslate2 has no ROCm backend, so this never uses the GPU."""

    def __init__(self, model_id: str, language: str, compute_type: str = "int8",
                 cpu_threads: int = 8):
        from faster_whisper import WhisperModel

        self._language = language or None
        self._hotwords = None
        self._model = WhisperModel(model_id, device="cpu",
                                   compute_type=compute_type, cpu_threads=cpu_threads)

    def describe(self) -> str:
        return "faster-whisper/cpu"

    def set_language(self, language: str) -> None:
        self._language = language or None

    def set_vocabulary(self, words: str) -> None:
        # faster-whisper takes bias words directly rather than as a decoder prompt.
        self._hotwords = words.strip() or None

    def transcribe(self, samples: np.ndarray, beam_size: int = 1) -> str:
        audio = samples.astype(np.float32) / 32768.0
        segments, _ = self._model.transcribe(
            audio,
            beam_size=beam_size,
            best_of=beam_size,
            language=self._language,
            hotwords=self._hotwords,
            # Endpointing already trims the utterance. Silero VAD only risks
            # deleting a short deliberate phrase.
            vad_filter=False,
            condition_on_previous_text=False,
        )
        return " ".join(segment.text.strip() for segment in segments).strip()


def write_command(path: Path, text: str) -> None:
    """Publish a transcript as a uniquely named file.

    Timestamped names keep back-to-back commands from overwriting each other;
    the mod drains them in ordinal (capture) order.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    queued_path = path.with_name(f"{path.stem}.{time.time_ns()}{path.suffix}")
    fd, temp_name = tempfile.mkstemp(prefix="ptt-", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(text)
        os.replace(temp_name, queued_path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)


def save_wav(path: Path, samples: np.ndarray) -> None:
    """Keep the last utterance so recognition can be compared offline."""
    import wave
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(RATE)
        handle.writeframes(samples.astype(np.int16).tobytes())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True,
                        help="Base path for transcript files written back to the mod.")
    parser.add_argument("--trigger", default="",
                        help="File whose existence means listening is on. "
                             "Defaults to push-to-talk.active beside --output.")
    parser.add_argument("--backend", default="transformers",
                        choices=["transformers", "faster-whisper"])
    parser.add_argument("--whisper-model", default="openai/whisper-large-v3-turbo",
                        help="Model id. Use a plain size name for faster-whisper.")
    parser.add_argument("--language", default="en",
                        help="Pinned recognition language. Empty string auto-detects, "
                             "which is unreliable on short buffers.")
    parser.add_argument("--beam-size", type=int, default=5,
                        help="Beam width for the final transcript. Partials always "
                             "decode greedily.")
    parser.add_argument("--device", default="auto")
    parser.add_argument("--compute-type", default="int8",
                        help="faster-whisper only.")
    parser.add_argument("--cpu-threads", type=int, default=8,
                        help="faster-whisper only.")
    parser.add_argument("--silence", type=float, default=SILENCE_SECONDS,
                        help="Seconds of trailing silence that end an utterance.")
    parser.add_argument("--vocabulary", default="Lilith, リリス, 莉莉丝, 莉莉絲",
                        help="Words to bias recognition toward - names and terms the "
                             "model would otherwise mangle. Keep it short: the model "
                             "can also emit these spontaneously on unclear audio.")
    parser.add_argument("--vad", default="silero", choices=["silero", "energy"],
                        help="Speech detector. Silero needs no per-machine tuning and "
                             "is the only one fit to ship; energy is a fallback.")
    parser.add_argument("--vad-threshold", type=float, default=0.5,
                        help="Silero speech probability above which a frame counts.")
    parser.add_argument("--energy-threshold", type=float, default=0.0,
                        help="Energy fallback only. Fixed RMS threshold; 0 measures "
                             "the room at startup instead.")
    parser.add_argument("--save-last", default="",
                        help="Optional WAV path; the last utterance is written there "
                             "for offline comparison.")
    args = parser.parse_args()

    output = Path(args.output)
    trigger = Path(args.trigger) if args.trigger else output.with_name("push-to-talk.active")
    silence_limit = max(0.5, args.silence)
    language = args.language.strip()
    save_last = Path(args.save_last) if args.save_last else None

    if args.backend == "transformers":
        asr = TransformersBackend(args.whisper_model, language, args.device)
    else:
        asr = FasterWhisperBackend(args.whisper_model, language,
                                   args.compute_type, args.cpu_threads)

    # Frames are 32 ms now, so hold the same few seconds of slack as before.
    audio_queue: queue.Queue[np.ndarray] = queue.Queue(maxsize=256)

    def callback(indata, frames, timing, status):
        del frames, timing, status
        try:
            audio_queue.put_nowait(indata[:, 0].copy())
        except queue.Full:
            pass

    active: list[np.ndarray] = []
    voiced: list[bool] = []
    energies: list[float] = []
    listening = False
    had_speech = False
    speech_frames = 0
    silent_for = 0.0
    started_at = 0.0
    next_partial_at = 0.0
    last_partial = ""
    current_language = language
    # After finalising we must not immediately start a second utterance: the mod
    # clears the trigger when it receives the transcript, so wait for that.
    awaiting_reset = False

    def read_trigger_language() -> str:
        """The mod writes the game's display language into the trigger file.

        Read per utterance so changing the game's language takes effect on the
        next thing said, with no restart.
        """
        try:
            requested = trigger.read_text(encoding="utf-8").strip().lower()
        except OSError:
            return language
        return requested if requested in SUPPORTED_LANGUAGES else language

    def speech_region(frames: list[np.ndarray], flags: list[bool]) -> np.ndarray:
        """Trim to the voiced span, padded.

        Decoding a long buffer that is mostly silence is what invites the stock
        caption phrases; giving Whisper only the part that has speech in it
        removes most of the opportunity.
        """
        indices = [index for index, flag in enumerate(flags) if flag]
        if not indices:
            return np.concatenate(frames) if frames else np.zeros(0, dtype=np.int16)
        first = max(0, indices[0] - SPEECH_PAD_FRAMES)
        last = min(len(frames), indices[-1] + 1 + SPEECH_PAD_FRAMES)
        return np.concatenate(frames[first:last])

    # Interim decoding runs on its own thread. Done inline it stalled the capture
    # loop for the length of a decode (~0.4 s), so audio backed up and updates
    # could only arrive every interval-plus-decode. The loop now just hands over a
    # snapshot and keeps reading the microphone.
    partial_requests: queue.Queue = queue.Queue(maxsize=1)
    model_lock = threading.Lock()
    utterance_lock = threading.Lock()
    utterance_id = 0

    def current_utterance_id() -> int:
        with utterance_lock:
            return utterance_id

    def partial_worker() -> None:
        last_sent = ""
        last_id = -1
        while True:
            item = partial_requests.get()
            if item is None:
                return
            samples, request_id = item
            if request_id != current_utterance_id():
                continue  # the utterance ended while this was queued
            if request_id != last_id:
                last_id = request_id
                last_sent = ""

            decode_started = time.monotonic()
            with model_lock:
                text = asr.transcribe(samples)
            decode_seconds = time.monotonic() - decode_started

            if normalise(text) in HALLUCINATIONS:
                text = ""
            # Re-check: the utterance may have ended while this was decoding, and a
            # late partial would overwrite the final transcript with a worse one.
            if not text or text == last_sent or request_id != current_utterance_id():
                continue
            last_sent = text
            write_command(output, PARTIAL_MARKER + "\n" + text)
            print(f"Partial ({decode_seconds:.2f}s decode, "
                  f"{len(samples) / RATE:.1f}s audio, "
                  f"backlog={audio_queue.qsize()}): {text}", flush=True)

    threading.Thread(target=partial_worker, name="partials", daemon=True).start()

    def end_utterance() -> None:
        """Invalidate in-flight partials so none can land after the final text."""
        nonlocal utterance_id
        with utterance_lock:
            utterance_id += 1
        try:
            while True:
                partial_requests.get_nowait()
        except queue.Empty:
            pass

    def report_energy(energies: list[float]) -> str:
        """Describe what was actually heard, so the threshold can be set from data."""
        if not energies:
            return "no audio"
        array = np.array(energies)
        return (f"rms median={np.median(array):.0f} p90={np.percentile(array, 90):.0f} "
                f"max={array.max():.0f} vad={detector.describe()}")

    def finalise(frames: list[np.ndarray], flags: list[bool],
                 energies: list[float], reason: str) -> None:
        end_utterance()
        voiced_seconds = sum(flags) * FRAME_SECONDS
        if voiced_seconds < MIN_SPEECH_SECONDS:
            write_command(output, "")
            print(f"Only {voiced_seconds:.2f}s voiced ({reason}); discarded. "
                  f"{report_energy(energies)}", flush=True)
            return

        samples = speech_region(frames, flags)
        if save_last is not None:
            try:
                save_wav(save_last, samples)
            except OSError as error:
                print(f"Could not save last utterance: {error}", flush=True)

        decode_started = time.monotonic()
        with model_lock:
            text = asr.transcribe(samples, beam_size=args.beam_size)
        decode_seconds = time.monotonic() - decode_started
        text = correct_leading_name(text, canonical_name)

        if normalise(text) in HALLUCINATIONS:
            write_command(output, "")
            print(f"Discarded probable hallucination after {voiced_seconds:.2f}s "
                  f"voiced: {text}", flush=True)
            return

        write_command(output, text)
        print(f"Transcript queued ({reason}, {decode_seconds:.2f}s decode, "
              f"{len(samples) / RATE:.1f}s trimmed, {voiced_seconds:.2f}s voiced, "
              f"lang={current_language}, {report_energy(energies)}): {text}", flush=True)

    asr.set_vocabulary(args.vocabulary)
    # The first vocabulary entry is the canonical spelling of her name, used to
    # repair the mishearings the bias alone does not catch.
    canonical_name = args.vocabulary.split(",")[0].strip()

    detector = None
    if args.vad == "silero":
        try:
            detector = SileroDetector(args.vad_threshold)
        except Exception as error:  # missing package, bad torch, anything
            print(f"Silero unavailable ({error}); falling back to energy.", flush=True)

    if detector is None:
        if args.energy_threshold > 0:
            energy_threshold = args.energy_threshold
            print(f"Voice threshold fixed at {energy_threshold:.0f} RMS.", flush=True)
        else:
            noise_floor = measure_noise_floor()
            energy_threshold = max(ENERGY_FLOOR, noise_floor * NOISE_MARGIN)
            print(f"Room measured at {noise_floor:.0f} RMS; voice threshold "
                  f"{energy_threshold:.0f}.", flush=True)
        detector = EnergyDetector(energy_threshold)

    # Whisper pads every input to the same 30 s window, so one warm-up decode
    # covers the sequence length every later call will use.
    asr.transcribe(np.zeros(RATE, dtype=np.int16))
    print(f"Speech listener ready. vad={detector.describe()} backend={asr.describe()} "
          f"model={args.whisper_model} language={language or 'auto'} "
          f"beam={args.beam_size} vocabulary={args.vocabulary or 'none'} "
          f"silence={silence_limit}s "
          f"input={sd.default.device[0]} trigger={trigger}", flush=True)

    with sd.InputStream(samplerate=RATE, channels=1, dtype="int16",
                        blocksize=FRAME, callback=callback):
        while True:
            frame = audio_queue.get()
            now = time.monotonic()
            on = trigger.exists()

            if awaiting_reset:
                if not on:
                    awaiting_reset = False
                continue

            if on and not listening:
                listening = True
                had_speech = False
                speech_frames = 0
                silent_for = 0.0
                started_at = now
                active = []
                voiced = []
                energies = []
                last_partial = ""
                next_partial_at = now + PARTIAL_INTERVAL
                current_language = read_trigger_language()
                asr.set_language(current_language)
                detector.reset()
                print(f"Listening. language={current_language}", flush=True)

            if not listening:
                continue

            if not on:
                # Toggled off by hand: that is a cancel, not a submit. Report what
                # was heard anyway - a cancel after speaking usually means the
                # threshold never let the utterance end on its own.
                listening = False
                end_utterance()
                print(f"Listening cancelled. voiced={sum(voiced) * FRAME_SECONDS:.2f}s "
                      f"{report_energy(energies)}", flush=True)
                active = []
                voiced = []
                energies = []
                continue

            active.append(frame)

            energies.append(float(np.sqrt(np.mean(frame.astype(np.float32) ** 2))))
            speaking = detector.is_speech(frame)
            voiced.append(speaking)
            if speaking:
                had_speech = True
                speech_frames += 1
                silent_for = 0.0
            elif had_speech:
                silent_for += FRAME_SECONDS

            ended = had_speech and silent_for >= silence_limit
            over_cap = now - started_at >= MAX_SECONDS

            if not ended and not over_cap:
                # Interim text into the chat field, so the user can see they are
                # being heard before the utterance ends. Greedy: these are
                # thrown away as soon as the next one lands.
                # Require real voiced audio, not just one spike, or the partial
                # decodes near-silence and returns a stock caption phrase.
                if (speech_frames * FRAME_SECONDS >= MIN_SPEECH_SECONDS
                        and now >= next_partial_at):
                    # Hand the snapshot to the worker and carry on reading the
                    # microphone. Never block: if the worker is still busy, skip
                    # this turn rather than queue audio that is already stale.
                    try:
                        partial_requests.put_nowait(
                            (speech_region(active, voiced), current_utterance_id()))
                    except queue.Full:
                        pass
                    next_partial_at = now + PARTIAL_INTERVAL
                continue

            frames, flags, levels = active, voiced, energies
            listening = False
            active = []
            voiced = []
            energies = []
            awaiting_reset = True
            finalise(frames, flags, levels, "cap" if over_cap else "silence")


if __name__ == "__main__":
    main()
