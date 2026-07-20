"""
verify-voice.py - checks the TTS voice feature: required files, load-bearing
source elements, and a clean build.

Read-only by design. An earlier version edited LlmChatController.cs from inside
this script, which double-inserted the enqueue call on the second run and made
every reply speak twice.
"""

import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
MOD_DIR = os.path.join(ROOT, "LilithMod")

# ── 1. Build ────────────────────────────────────────────────────────────────
proj = os.path.join(MOD_DIR, "LilithMod.csproj")
r = subprocess.run(
    # Full path: dotnet is not on PATH for subprocesses in this environment.
    [r"C:\Program Files\dotnet\dotnet.exe", "build", proj, "-c", "Release"],
    capture_output=True, text=True, cwd=MOD_DIR,
)
if r.returncode != 0:
    print("VERIFY FAIL - dotnet build returned", r.returncode)
    for line in (r.stdout + r.stderr).splitlines():
        if "error " in line.lower():
            print("  ", line)
    sys.exit(1)
print("[verify-voice] Build succeeded")

# ── 2. Source checks ────────────────────────────────────────────────────────
# Gather all C# source text under LilithMod/
src = ""
for root, dirs, files in os.walk(MOD_DIR):
    for fn in files:
        if fn.endswith(".cs"):
            fp = os.path.join(root, fn)
            with open(fp, "r", encoding="utf-8", errors="ignore") as f:
                src += f.read()

# Check the .csproj for NAudio
with open(proj, "r", encoding="utf-8") as f:
    proj_text = f.read()
if "NAudio" not in proj_text:
    print("VERIFY FAIL - NAudio not found in LilithMod.csproj")
    sys.exit(1)

# Required markers in source
required = [
    ("ref_audio_path", "ref_audio_path"),
    ("text_lang", "text_lang"),
    ("fragment_interval", "fragment_interval"),
    ("PlaybackStopped", "PlaybackStopped"),
    ("Newtonsoft.Json", "Newtonsoft.Json"),
    ("SpeechQueueProcessor", "SpeechQueueProcessor"),
    ("VoiceConfig.Enabled", "VoiceConfig.Enabled"),
    ("VoiceProcessor.Enqueue", "VoiceProcessor.Enqueue"),
    ("TtsClient", "TtsClient"),
    ("VoicePlayer", "VoicePlayer"),
    ("WaveOutEvent", "WaveOutEvent"),
    ("WaveFileReader", "WaveFileReader"),
    ("ManualResetEventSlim", "ManualResetEventSlim"),
    ("WarmUpSentences", "WarmUpSentences"),
]

absent = [label for label, pat in required if pat not in src]
if absent:
    print("VERIFY FAIL - required source elements missing: " + "; ".join(absent))
    sys.exit(1)

# ── 3. base64 check (should NOT be present in new TTS code) ─────────────────
# We allow base64 in the rest of the codebase, but flag if it appears in
# TtsClient, VoicePlayer, or SpeechQueueProcessor.
tts_files = ["TtsClient.cs", "VoicePlayer.cs", "SpeechQueueProcessor.cs"]
base64_found = False
for fn in tts_files:
    fp = os.path.join(MOD_DIR, fn)
    if os.path.exists(fp):
        with open(fp, "r", encoding="utf-8") as f:
            if "base64" in f.read().lower():
                print(f"VERIFY WARN - 'base64' found in {fn} (should not be used for TTS)")
                base64_found = True
# Not a hard fail because it might be in a comment, but worth noting.
if not base64_found:
    print("[verify-voice] No base64 in TTS files (good)")

# ── 4. Output artifacts ─────────────────────────────────────────────────────
out_dir = r"D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith\BepInEx\plugins\LilithMod"
mod_dll = os.path.join(out_dir, "LilithMod.dll")
naudio_dll = os.path.join(out_dir, "NAudio.dll")

if not os.path.exists(mod_dll):
    print(f"VERIFY FAIL - {mod_dll} not found after build")
    sys.exit(1)

if not os.path.exists(naudio_dll):
    # NAudio might be named differently or merged; check for any NAudio* file
    found_naudio = False
    for fn in os.listdir(out_dir):
        if "NAudio" in fn or "naudio" in fn:
            found_naudio = True
            print(f"[verify-voice] Found NAudio assembly: {fn}")
            break
    if not found_naudio:
        print(f"VERIFY FAIL - No NAudio assembly found in {out_dir}")
        print("  Contents:", os.listdir(out_dir))
        sys.exit(1)
else:
    print(f"[verify-voice] NAudio.dll present at {naudio_dll}")

print("[verify-voice] All checks passed")
print("VERIFY PASS")
