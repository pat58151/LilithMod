"""Turns the recorded own-voice negatives into openWakeWord feature windows.

Reads every wav in negative/, slices it into 1.28 s windows with 50% overlap,
runs them through openWakeWord's melspectrogram + embedding models, and writes
data/user_negative_features.npy with shape (N, 16, 96) - the same layout as
the precomputed ACAV100M negatives, so train.py can mix them per batch.
"""

import glob
import os

import numpy as np
import soundfile as sf

# Artifacts live outside the repo; override with LILITH_WAKEWORD_WORKSPACE.
_REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
HERE = os.environ.get("LILITH_WAKEWORD_WORKSPACE") or os.path.join(_REPO, "training", "wakeword")
os.makedirs(HERE, exist_ok=True)

import openwakeword.utils as oww_utils  # noqa: E402

# 1.28 s at 16 kHz; matches one row of the precomputed feature arrays.
SAMPLES = 20480
STEP = SAMPLES // 2

oww_utils.download_models()
F = oww_utils.AudioFeatures()

clips = []
for path in sorted(glob.glob(os.path.join(HERE, "negative", "*.wav"))):
    audio, rate = sf.read(path, dtype="int16")
    if rate != 16000:
        raise SystemExit(f"{path} is {rate} Hz; expected 16000")
    if audio.ndim > 1:
        audio = audio[:, 0]
    for start in range(0, max(len(audio) - SAMPLES, 1), STEP):
        window = audio[start:start + SAMPLES]
        if len(window) == SAMPLES:
            clips.append(window)

if not clips:
    raise SystemExit("no negative wavs found")

print(f"featurizing {len(clips)} windows ...")
features = F.embed_clips(np.array(clips), batch_size=128)
out = os.path.join(HERE, "data", "user_negative_features.npy")
np.save(out, features)
print(f"wrote {out} with shape {features.shape}")
