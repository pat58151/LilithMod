"""Resume-safe GPT-SoVITS cache builder for every existing Lilith line."""

from __future__ import annotations

import argparse
import array
import csv
import hashlib
import io
import json
import sys
import time
import unicodedata
import urllib.error
import urllib.request
import wave
from pathlib import Path


def read_config(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        if "=" in raw and not raw.lstrip().startswith("#"):
            key, value = raw.split("=", 1)
            values[key.strip()] = value.strip()
    return values


def split_batch_wav(audio: bytes, expected: int, separator_seconds: float) -> list[bytes]:
    with wave.open(io.BytesIO(audio), "rb") as source:
        channels = source.getnchannels()
        sample_width = source.getsampwidth()
        sample_rate = source.getframerate()
        frames = source.readframes(source.getnframes())
    if channels != 1 or sample_width != 2:
        raise RuntimeError(f"unexpected WAV format: channels={channels} width={sample_width}")

    samples = array.array("h")
    samples.frombytes(frames)
    if sys.byteorder != "little":
        samples.byteswap()
    minimum_zeros = int(sample_rate * max(0.1, separator_seconds - 0.04))
    fragments: list[array.array] = []
    start = 0
    position = 0
    while position < len(samples):
        if samples[position] != 0:
            position += 1
            continue
        end = position + 1
        while end < len(samples) and samples[end] == 0:
            end += 1
        if end - position >= minimum_zeros:
            if position > start:
                fragments.append(samples[start:position])
            start = end
        position = end
    if start < len(samples):
        fragments.append(samples[start:])
    if len(fragments) != expected:
        raise RuntimeError(f"batch split produced {len(fragments)} WAVs; expected {expected}")

    result: list[bytes] = []
    for fragment in fragments:
        if sys.byteorder != "little":
            fragment.byteswap()
        buffer = io.BytesIO()
        with wave.open(buffer, "wb") as target:
            target.setnchannels(channels)
            target.setsampwidth(sample_width)
            target.setframerate(sample_rate)
            target.writeframes(fragment.tobytes())
        result.append(buffer.getvalue())
    return result


def has_spoken_content(text: str) -> bool:
    return any(unicodedata.category(char)[0] in ("L", "N") for char in text)


def silent_wav(seconds: float = 0.3, sample_rate: int = 32000) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(sample_rate)
        target.writeframes(b"\0\0" * int(seconds * sample_rate))
    return buffer.getvalue()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", default=r"D:\Lilith")
    parser.add_argument("--game", default=r"D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith")
    parser.add_argument("--language", choices=("ja", "zh"), default="ja")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--endpoint", default="http://127.0.0.1:9880/tts")
    parser.add_argument("--batch-size", type=int, default=8)
    args = parser.parse_args()

    project = Path(args.project)
    game = Path(args.game)
    config = read_config(game / "BepInEx" / "config" / "LilithMod.cfg")
    source = project / "LilithMod" / "dialogue" / f"{args.language}.tsv"
    cache = game / "BepInEx" / "plugins" / "LilithMod" / "voice-cache" / args.language
    cache.mkdir(parents=True, exist_ok=True)

    ref_audio = config.get("RefAudioPath", "")
    prompt_text = config.get("PromptText", "")
    prompt_lang = config.get("PromptLang", "ja")
    texts: list[str] = []
    with source.open(encoding="utf-8-sig", newline="") as handle:
        for row in csv.reader(handle, delimiter="\t"):
            if len(row) >= 3 and row[2].strip() and row[0].isdigit():
                texts.append(row[2].strip())
    texts = list(dict.fromkeys(texts))
    if args.limit: texts = texts[: args.limit]

    model_identity = "ja-finetuned-e12-s1016-v1\n" if args.language == "ja" else ""
    separator_seconds = 0.3

    def output_for(text: str) -> Path:
        material = f"{model_identity}{args.language}\n{text}\n{ref_audio}\n{prompt_text}".encode()
        return cache / (hashlib.sha256(material).hexdigest() + ".wav")

    def request_audio(batch_texts: list[str], batched: bool) -> bytes:
        payload = json.dumps({
            "text": "\n".join(batch_texts), "text_lang": args.language, "ref_audio_path": ref_audio,
            "prompt_text": prompt_text, "prompt_lang": prompt_lang, "media_type": "wav",
            "streaming_mode": False, "text_split_method": "cut0" if batched else "cut5",
            "batch_size": len(batch_texts) if batched else 1, "split_bucket": True,
            # ROCm/MIOpen's parallel path repeatedly falls back because its
            # convolution workspace is undersized. Serial is substantially
            # faster and still preserves batch separators for cache splitting.
            "parallel_infer": False, "fragment_interval": separator_seconds,
        }, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(
            args.endpoint, data=payload,
            headers={"Content-Type": "application/json"}, method="POST"
        )
        for attempt in range(1, 4):
            try:
                with urllib.request.urlopen(request, timeout=120) as response:
                    audio = response.read()
                if len(audio) < 44 or audio[:4] != b"RIFF" or audio[8:12] != b"WAVE":
                    raise RuntimeError("TTS returned an invalid WAV")
                return audio
            except urllib.error.HTTPError as error:
                detail = error.read().decode("utf-8", errors="replace")
                if attempt == 3:
                    raise RuntimeError(f"HTTP {error.code}: {detail}") from error
                time.sleep(attempt * 2)
            except Exception:
                if attempt == 3:
                    raise
                time.sleep(attempt * 2)

    missing = [(text, output_for(text)) for text in texts if not output_for(text).exists()]
    skipped = len(texts) - len(missing)
    made = 0
    print(
        f"catalog={len(texts)} missing={len(missing)} cached={skipped} "
        f"batch_size={args.batch_size} cache={cache}", flush=True
    )
    for offset in range(0, len(missing), args.batch_size):
        group = missing[offset:offset + args.batch_size]
        group_texts = [item[0] for item in group]
        if any(not has_spoken_content(text) for text in group_texts):
            wavs = [
                request_audio([text], batched=False) if has_spoken_content(text) else silent_wav()
                for text in group_texts
            ]
        else:
            try:
                combined = request_audio(group_texts, batched=len(group) > 1)
                wavs = split_batch_wav(combined, len(group), separator_seconds) if len(group) > 1 else [combined]
            except Exception as batch_error:
                print(f"batch_fallback offset={offset} reason={batch_error}", flush=True)
                wavs = [request_audio([text], batched=False) for text in group_texts]
        for (_, output), audio in zip(group, wavs):
            temporary = output.with_suffix(".tmp")
            temporary.write_bytes(audio)
            temporary.replace(output)
            made += 1
        print(
            f"{skipped + made}/{len(texts)} generated={made} cached={skipped}",
            flush=True,
        )
    print(f"complete total={len(texts)} generated={made} cached={skipped}", flush=True)


if __name__ == "__main__":
    main()
