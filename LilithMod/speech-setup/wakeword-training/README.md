# "Lilith" wake-word training

Trains two openWakeWord models that detect the spoken word "Lilith":

- **`lilith_generic/lilith.onnx`** — synthetic voices only (piper LibriTTS-R,
  ~900 speakers). The only variant that may ever ship.
- **`lilith_personal/lilith_personal.onnx`** — same corpus plus the developer's
  100 real recordings (`positive\`) weighted in, and 17 minutes of their normal
  speech (`negative\`) as extra negatives. Local use only.

## Where things live

The scripts are here, in the repo. Everything they build - the Python
environments, ~20 GB of datasets, 60k generated clips, the models - goes to
`training\wakeword\` at the repo root, which is gitignored. Point
`LILITH_WAKEWORD_WORKSPACE` elsewhere to override.

Your own recordings go in `<workspace>\positive\` (clips of the wake word,
16 kHz mono WAV) and `<workspace>
egative\` (ordinary speech, no wake word).

## Run order

```powershell
powershell -ExecutionPolicy Bypass -File setup-training.ps1   # once; ~20 GB of downloads
powershell -ExecutionPolicy Bypass -File train.ps1            # generic model
powershell -ExecutionPolicy Bypass -File train.ps1 -Personal  # personal variant
```

### Optional GPU fast path for clip generation

Clip generation (the hours-long stage) can run on ROCm instead: the global
Python 3.12 carries the official AMD torch 2.9.1+rocm7.2.1 wheels from
repo.radeon.com, and `env-gen` is a thin venv over it. `generate_clips_gpu.py`
mirrors train.py's --generate_clips exactly (same directories and resume
thresholds), so train.ps1 afterwards skips generation by itself.

```powershell
powershell -ExecutionPolicy Bypass -File setup-gpu-generation.ps1   # once
powershell -ExecutionPolicy Bypass -File generate-clips-gpu.ps1     # then train.ps1 as usual
```

AMD's release notes require Adrenalin driver 26.2.2+ for the 7.2.1 wheels; if
`torch.cuda.is_available()` prints False in env-gen, update the driver.

Everything is resumable; re-run the same command after a failure.

## What lives where

| path | what |
|---|---|
| `positive\`, `negative\` | the real recordings (16 kHz mono, click-tail trimmed) |
| `data\` | 17.3 GB ACAV100M negative features, validation features, own-voice features |
| `mit_rirs\`, `audioset_16k\` | augmentation data (reverb, background noise) |
| `env\` | Python 3.10 training environment (uv-managed) |
| `openWakeWord\`, `piper-sample-generator\` | cloned upstream repos |
| `lilith-generic.yaml`, `lilith-personal.yaml` | the training configs |

## Known rough edges

- Clip generation runs on CPU torch — expect an hour or two for 30k clips.
  Training itself is a small DNN over precomputed embeddings and is fast.
- `piper-phonemize` is the most Windows-fragile dependency. If it will not
  install, generation must run elsewhere (WSL or Colab) — the rest of the
  pipeline is unaffected once clips exist.
- The tensorflow pins exist only for the `.tflite` export; the listener uses
  `.onnx`, so tensorflow failures are ignorable.
- This is upstream's documented flow (`automatic_model_training.ipynb`),
  frozen here with the dataset URLs it downloads.
