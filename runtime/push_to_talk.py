"""Local speech-to-text listener for LilithMod.

A trigger file controls recording. The listener streams partial transcripts,
ends on detected silence, and supports PyTorch Whisper or faster-whisper.
Backends are loaded only when selected.
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
SILENCE_SECONDS = 1.5            # trailing silence that ends an utterance
NO_SPEECH_SECONDS = 2.5          # give up if the key was pressed but nothing was said
MAX_SECONDS = 60.0               # hard cap, so a stuck trigger cannot grow without bound
MIN_SPEECH_SECONDS = 0.45        # total voiced audio required before decoding at all
SPEECH_PAD_SECONDS = 0.3         # keep this much either side of the voiced region
SPEECH_PAD_FRAMES = int(SPEECH_PAD_SECONDS / FRAME_SECONDS)

# Energy fallback for systems that cannot load Silero.
ENERGY_FLOOR = 90.0
NOISE_MARGIN = 2.0
CALIBRATION_SECONDS = 1.5
PARTIAL_MARKER = "__LILITH_PTT_PARTIAL__"
HEARTBEAT_INTERVAL = 2.0         # seconds between liveness touches
SUPPORTED_LANGUAGES = {"en", "ja", "zh"}
ARM_SECONDS = 6.0                # wake detection opens one bounded command window
WAKE_THRESHOLD = 0.80
WAKE_LOG_THRESHOLD = 0.3        # log near misses so the threshold can be tuned
PLAYBACK_TAIL_SECONDS = 0.5      # ignore the room echo after Lilith stops speaking
WAKE_FLAG_STALE_SECONDS = 12.0   # fail closed if the game crashes

# Common Whisper hallucinations from silence and noise.
HALLUCINATIONS = {
    "thank you", "thanks", "thank you very much", "thanks for watching",
    "thank you for watching", "thanks for watching!", "bye", "goodbye",
    "you", "please subscribe", "subscribe", "okay", "ok", "oh", "uh", "um",
    "hmm", "mm", "mhm", "yeah", "yes", "no", "so", "the", "and", "i",
    "ご視聴ありがとうございました", "ありがとうございました", "おやすみなさい",
    "はい", "え", "あの", "字幕", "谢谢观看", "谢谢大家", "好", "嗯",
    "请不吝点赞 订阅 转发 打赏支持明镜与点点栏目",
}
# Exact matches are always rejected, including one-word acknowledgements.


class SileroDetector:
    """Neural speech detector that avoids microphone-specific RMS tuning."""

    def __init__(self, threshold: float):
        import torch
        from silero_vad import load_silero_vad

        self._torch = torch
        self._model = load_silero_vad()
        self._threshold = threshold

    def describe(self) -> str:
        return f"silero(threshold={self._threshold})"

    def reset(self) -> None:
        # Reset recurrent state between utterances.
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
    """Measure room noise for the energy-detector fallback."""
    frames = int(seconds * RATE / FRAME)
    captured = sd.rec(frames * FRAME, samplerate=RATE, channels=1, dtype="int16")
    sd.wait()
    samples = captured[:, 0].astype(np.float32)
    if samples.size == 0:
        return ENERGY_FLOOR
    blocks = samples[:frames * FRAME].reshape(frames, FRAME)
    energies = np.sqrt(np.mean(blocks ** 2, axis=1))
    return float(np.percentile(energies, 75))


# Leading-name errors commonly produced by Whisper.
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
    parser.add_argument("--no-speech", type=float, default=NO_SPEECH_SECONDS,
                        help="Give up and close if nothing is said after listening starts.")
    parser.add_argument("--vocabulary", default="",
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
    parser.add_argument("--wake-model", default="",
                        help="Optional openWakeWord ONNX model. Push-to-talk still "
                             "works when it is absent.")
    parser.add_argument("--wake-flag", default="",
                        help="Wake detection is enabled while this file exists.")
    parser.add_argument("--wake-threshold", type=float, default=WAKE_THRESHOLD,
                        help="openWakeWord score required to arm recording.")
    parser.add_argument("--wake-arm-seconds", type=float, default=ARM_SECONDS,
                        help="Maximum pause after the wake word before recording closes.")
    parser.add_argument("--playback-lock", default="",
                        help="File held while Lilith is speaking, to prevent self-triggering.")
    args = parser.parse_args()

    output = Path(args.output)
    trigger = Path(args.trigger) if args.trigger else output.with_name("push-to-talk.active")
    # File heartbeat used by the mod's availability check.
    heartbeat = trigger.with_name("push-to-talk.alive")
    silence_limit = max(0.5, args.silence)
    # Never give up before the silence endpoint can elapse.
    no_speech_limit = max(silence_limit, args.no_speech)
    language = args.language.strip()
    save_last = Path(args.save_last) if args.save_last else None
    wake_model_path = Path(args.wake_model) if args.wake_model else None
    wake_flag = (Path(args.wake_flag) if args.wake_flag
                 else output.parent / "speech-setup" / "wake-word.on")
    wake_ui = output.parent / "wake-listening.active"
    playback_lock = (Path(args.playback_lock) if args.playback_lock
                     else output.parent / "voice-output.active")
    wake_threshold = min(1.0, max(0.0, args.wake_threshold))
    wake_arm_seconds = max(no_speech_limit, args.wake_arm_seconds)
    try:
        wake_ui.unlink(missing_ok=True)
    except OSError:
        pass

    wake_model = None
    wake_name = ""
    if wake_model_path is not None and wake_model_path.is_file():
        try:
            from openwakeword.model import Model as WakeWordModel
            wake_model = WakeWordModel(
                wakeword_models=[str(wake_model_path)],
                inference_framework="onnx")
            wake_name = next(iter(wake_model.models))
            print(f"Wake word ready. model={wake_model_path} name={wake_name} "
                  f"threshold={wake_threshold:.2f} flag={wake_flag} "
                  f"playback_lock={playback_lock}", flush=True)
        except Exception as error:
            print(f"Wake word unavailable ({error}); push-to-talk only.", flush=True)
            wake_model = None
    elif wake_model_path is not None:
        print(f"Wake word model not found at {wake_model_path}; push-to-talk only.",
              flush=True)

    if args.backend == "transformers":
        asr = TransformersBackend(args.whisper_model, language, args.device)
    else:
        asr = FasterWhisperBackend(args.whisper_model, language,
                                   args.compute_type, args.cpu_threads)

    # Leave capture slack for decode stalls.
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
    # Wait for the mod to clear the trigger after finalization.
    awaiting_reset = False
    armed_until = 0.0
    wake_session = False
    wake_enabled_last = False
    playback_locked_last = False
    playback_tail_until = 0.0
    next_wake_score_log = 0.0

    def read_trigger_language() -> str:
        """Read recognition language for the next utterance."""
        try:
            requested = trigger.read_text(encoding="utf-8").strip().lower()
        except OSError:
            return language
        return requested if requested in SUPPORTED_LANGUAGES else language

    def speech_region(frames: list[np.ndarray], flags: list[bool]) -> np.ndarray:
        """Return the padded voiced span."""
        indices = [index for index, flag in enumerate(flags) if flag]
        if not indices:
            return np.concatenate(frames) if frames else np.zeros(0, dtype=np.int16)
        first = max(0, indices[0] - SPEECH_PAD_FRAMES)
        last = min(len(frames), indices[-1] + 1 + SPEECH_PAD_FRAMES)
        return np.concatenate(frames[first:last])

    # Decode partials off the capture thread.
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
            # Drop partials that finish after their utterance.
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
    # Use the first vocabulary entry as the canonical name.
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

    # Warm the selected Whisper backend.
    asr.transcribe(np.zeros(RATE, dtype=np.int16))
    print(f"Speech listener ready. vad={detector.describe()} backend={asr.describe()} "
          f"model={args.whisper_model} language={language or 'auto'} "
          f"beam={args.beam_size} vocabulary={args.vocabulary or 'none'} "
          f"silence={silence_limit}s "
          f"input={sd.default.device[0]} trigger={trigger}", flush=True)

    with sd.InputStream(samplerate=RATE, channels=1, dtype="int16",
                        blocksize=FRAME, callback=callback):
        next_heartbeat = 0.0
        while True:
            frame = audio_queue.get()
            now = time.monotonic()

            if now >= next_heartbeat:
                next_heartbeat = now + HEARTBEAT_INTERVAL
                try:
                    heartbeat.write_text(str(time.time()), encoding="utf-8")
                except OSError:
                    pass

            manual_on = trigger.exists()
            if manual_on and not listening:
                # An explicit F8 press wins over a simultaneous wake score and keeps
                # its original release-to-cancel behavior.
                armed_until = 0.0

            # The C# voice queue holds this file around every playback. Extend the
            # lock briefly after deletion so room echo cannot wake Lilith herself.
            lock_file_present = playback_lock.exists()
            if lock_file_present:
                playback_tail_until = now + PLAYBACK_TAIL_SECONDS
            playback_locked = lock_file_present or now < playback_tail_until

            try:
                wake_flag_fresh = (
                    wake_flag.exists()
                    and time.time() - wake_flag.stat().st_mtime < WAKE_FLAG_STALE_SECONDS
                )
            except OSError:
                wake_flag_fresh = False
            wake_enabled = wake_model is not None and wake_flag_fresh
            if (wake_model is not None and not manual_on and not listening
                    and not awaiting_reset):
                if playback_locked:
                    if not playback_locked_last:
                        wake_model.reset()
                elif wake_enabled:
                    prediction = wake_model.predict(frame)
                    score = float(prediction.get(wake_name, 0.0))
                    if score >= WAKE_LOG_THRESHOLD and now >= next_wake_score_log:
                        print(f"Wake score {score:.3f} ({wake_name}).", flush=True)
                        next_wake_score_log = now + 0.5
                    if score >= wake_threshold:
                        armed_until = now + wake_arm_seconds
                        wake_model.reset()
                        try:
                            wake_ui.write_text(str(time.time()), encoding="utf-8")
                        except OSError:
                            pass
                        print(f"Wake detected at {score:.3f}; listening armed for "
                              f"{wake_arm_seconds:.1f}s.", flush=True)
                elif wake_enabled_last:
                    wake_model.reset()
            playback_locked_last = playback_locked
            wake_enabled_last = wake_enabled

            # A wake-started session remains open until VAD ends it. ARM_SECONDS
            # bounds only the pause before speech begins, not the command length.
            on = manual_on or now < armed_until or wake_session

            if awaiting_reset:
                if not on:
                    awaiting_reset = False
                continue

            if on and not listening:
                listening = True
                wake_session = not manual_on
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
                # Manual toggle-off cancels the utterance.
                listening = False
                end_utterance()
                print(f"Listening cancelled. voiced={sum(voiced) * FRAME_SECONDS:.2f}s "
                      f"{report_energy(energies)}", flush=True)
                active = []
                voiced = []
                energies = []
                wake_session = False
                armed_until = 0.0
                if wake_model is not None:
                    wake_model.reset()
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
            # Close an unused recording automatically.
            unused_limit = wake_arm_seconds if wake_session else no_speech_limit
            gave_up = not had_speech and now - started_at >= unused_limit

            if not ended and not over_cap and not gave_up:
                # Send partial text only after sustained speech.
                if (speech_frames * FRAME_SECONDS >= MIN_SPEECH_SECONDS
                        and now >= next_partial_at):
                    # Skip partials instead of blocking microphone capture.
                    try:
                        partial_requests.put_nowait(
                            (speech_region(active, voiced), current_utterance_id()))
                    except queue.Full:
                        pass
                    next_partial_at = now + PARTIAL_INTERVAL
                continue

            frames, flags, levels = active, voiced, energies
            listening = False
            wake_session = False
            armed_until = 0.0
            active = []
            voiced = []
            energies = []
            if wake_model is not None:
                wake_model.reset()
            awaiting_reset = True
            try:
                finalise(frames, flags, levels,
                         "cap" if over_cap else "no speech" if gave_up else "silence")
            finally:
                try:
                    wake_ui.unlink(missing_ok=True)
                except OSError:
                    pass


if __name__ == "__main__":
    main()
