"""Stages the openWakeWord training datasets next to this script.

Everything checks before it downloads, so a failed run resumes. Layout:

    data/openwakeword_features_ACAV100M_2000_hrs_16bit.npy   (17.3 GB negatives)
    data/validation_set_features.npy                          (false-positive val)
    mit_rirs/*.wav                (room impulse responses, for augmentation)
    audioset_16k/*.wav            (background noise, for augmentation)
"""

import os
import subprocess

# Artifacts live outside the repo; override with LILITH_WAKEWORD_WORKSPACE.
_REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
HERE = os.environ.get("LILITH_WAKEWORD_WORKSPACE") or os.path.join(_REPO, "training", "wakeword")
os.makedirs(HERE, exist_ok=True)
DATA = os.path.join(HERE, "data")
os.makedirs(DATA, exist_ok=True)

from huggingface_hub import hf_hub_download  # noqa: E402


def fetch(repo_id, filename, repo_type="dataset"):
    target = os.path.join(DATA, os.path.basename(filename))
    if os.path.exists(target) and os.path.getsize(target) > 0:
        print(f"already here: {os.path.basename(target)}")
        return target
    print(f"downloading {filename} from {repo_id} ...")
    path = hf_hub_download(repo_id=repo_id, filename=filename, repo_type=repo_type,
                           local_dir=DATA)
    return path


# --- precomputed negative features ------------------------------------------
fetch("davidscripka/openwakeword_features", "validation_set_features.npy")
fetch("davidscripka/openwakeword_features",
      "openwakeword_features_ACAV100M_2000_hrs_16bit.npy")

# --- room impulse responses ---------------------------------------------------
rir_dir = os.path.join(HERE, "mit_rirs")
if not os.path.isdir(rir_dir) or not os.listdir(rir_dir):
    os.makedirs(rir_dir, exist_ok=True)
    print("downloading MIT room impulse responses ...")
    import datasets
    import soundfile as sf
    import numpy as np
    rir = datasets.load_dataset("davidscripka/MIT_environmental_impulse_responses",
                                split="train", streaming=True)
    count = 0
    for row in rir:
        name = os.path.join(rir_dir, os.path.basename(row["audio"]["path"]))
        sf.write(name, (np.asarray(row["audio"]["array"]) * 32767).astype("int16"), 16000)
        count += 1
    print(f"wrote {count} RIR files")
else:
    print("RIRs already staged")

# --- background noise (one AudioSet shard, converted to 16 kHz wav) ----------
# The upstream repo repacked its tars into parquet shards with embedded flac
# bytes (data/bal_train/NN.parquet), so the audio is pulled out of the table.
bg_dir = os.path.join(HERE, "audioset_16k")
if not os.path.isdir(bg_dir) or len(os.listdir(bg_dir)) < 100:
    os.makedirs(bg_dir, exist_ok=True)
    parquet_path = os.path.join(DATA, "09.parquet")
    if not os.path.exists(parquet_path):
        print("downloading AudioSet shard ...")
        hf_hub_download(repo_id="agkphysics/AudioSet", filename="data/bal_train/09.parquet",
                        repo_type="dataset", local_dir=DATA)
        staged = os.path.join(DATA, "data", "bal_train", "09.parquet")
        if os.path.exists(staged):
            os.replace(staged, parquet_path)
    import pyarrow.parquet as pq
    table = pq.read_table(parquet_path, columns=["audio"])
    rows = table.column("audio").to_pylist()
    print(f"converting {len(rows)} clips to 16 kHz wav ...")
    tmp_flac = os.path.join(DATA, "_clip.flac")
    for i, row in enumerate(rows):
        name = os.path.splitext(os.path.basename(row.get("path") or f"clip{i}"))[0]
        out = os.path.join(bg_dir, name + ".wav")
        if os.path.exists(out):
            continue
        with open(tmp_flac, "wb") as f:
            f.write(row["bytes"])
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", tmp_flac,
                        "-ar", "16000", "-ac", "1", out], check=False)
        if i % 200 == 0:
            print(f"  {i}/{len(rows)}")
    if os.path.exists(tmp_flac):
        os.remove(tmp_flac)
    print("background noise staged")
else:
    print("background noise already staged")

print("data preparation complete")
