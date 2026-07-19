"""Probe a local GPT-SoVITS service before wiring the mod against it.

Answers three questions the integration depends on, in order:
  1. Is anything listening on the voice ports?
  2. Does /tts accept our request shape?
  3. Does it return real audio, and how long does synthesis take?

Run this after installing GPT-SoVITS (standalone or via another mod's installer).
Whatever it prints is the contract the mod should be built against.

    python probe-tts.py --ref "C:\\path\\to\\reference.wav" --prompt-text "..."
"""
import argparse
import json
import socket
import struct
import sys
import time
import urllib.error
import urllib.request

DEFAULT_PORTS = [9880, 9881]  # zh, ja in the layout LilithVoiceHost uses


def port_open(host, port, timeout=1.0):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(timeout)
        return s.connect_ex((host, port)) == 0


def describe_wav(data):
    """Pull the format out of a RIFF header so we can report duration, not just size."""
    if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    pos = 12
    fmt = None
    while pos + 8 <= len(data):
        cid = data[pos:pos + 4]
        csz = struct.unpack("<I", data[pos + 4:pos + 8])[0]
        body = data[pos + 8:pos + 8 + csz]
        if cid == b"fmt " and len(body) >= 16:
            _, ch, rate, _, _, bits = struct.unpack("<HHIIHH", body[:16])
            fmt = (ch, rate, bits)
        elif cid == b"data" and fmt:
            ch, rate, bits = fmt
            per_sec = rate * ch * max(bits // 8, 1)
            return {"channels": ch, "rate": rate, "bits": bits,
                    "seconds": round(csz / per_sec, 2) if per_sec else None}
        pos += 8 + csz + (csz & 1)
    return None


def synth(base, payload, timeout):
    req = urllib.request.Request(
        base.rstrip("/") + "/tts",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read(), r.headers.get("Content-Type", "?"), time.time() - t0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9880)
    ap.add_argument("--ref", help="reference audio path, as the SERVICE sees it")
    ap.add_argument("--prompt-text", default="", help="transcript of the reference audio")
    ap.add_argument("--prompt-lang", default="zh")
    ap.add_argument("--text", default="你好，我在这里。")
    ap.add_argument("--text-lang", default="zh")
    ap.add_argument("--timeout", type=float, default=120.0)
    ap.add_argument("--out", default="tts-probe.wav")
    args = ap.parse_args()

    print("== ports ==")
    live = []
    for p in sorted(set(DEFAULT_PORTS + [args.port])):
        ok = port_open(args.host, p)
        print(f"  {args.host}:{p} {'OPEN' if ok else 'closed'}")
        if ok:
            live.append(p)
    if not live:
        print("\nNothing listening. Start GPT-SoVITS first:")
        print('  python api_v2.py -a 127.0.0.1 -p 9880 -c GPT_SoVITS/configs/tts_infer.yaml')
        return 1

    if not args.ref:
        print("\nPorts are up. Re-run with --ref to test synthesis:")
        print('  python probe-tts.py --ref "D:\\...\\reference.wav" --prompt-text "<transcript>"')
        return 0

    payload = {
        "text": args.text,
        "text_lang": args.text_lang,
        "ref_audio_path": args.ref,
        "prompt_text": args.prompt_text,
        "prompt_lang": args.prompt_lang,
        "media_type": "wav",
        "streaming_mode": False,
    }

    print(f"\n== synth on {args.port} ==")
    print("  " + json.dumps(payload, ensure_ascii=False))
    try:
        data, ctype, elapsed = synth(f"http://{args.host}:{args.port}", payload, args.timeout)
    except urllib.error.HTTPError as e:
        # The service reports its own failures as JSON, which is more useful than the status code.
        body = e.read().decode("utf-8", "replace")[:500]
        print(f"\nFAIL HTTP {e.code}: {body}")
        return 1
    except Exception as e:
        print(f"\nFAIL {type(e).__name__}: {e}")
        return 1

    print(f"  {len(data)} bytes, content-type {ctype}, {elapsed:.1f}s")
    info = describe_wav(data)
    if info:
        print(f"  {info['seconds']}s audio, {info['rate']}Hz {info['bits']}-bit x{info['channels']}")
        # Synthesis slower than playback means the reply lags the text; worth knowing early.
        if info["seconds"]:
            print(f"  realtime factor {elapsed / info['seconds']:.2f}x")
    else:
        print("  WARNING: not a WAV this parser recognises")

    with open(args.out, "wb") as f:
        f.write(data)
    print(f"\nWrote {args.out} - play it to confirm the voice is right.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
