"""GPU clip generation: replicates train.py --generate_clips outside the
pinned training env, so the TTS-heavy stage can run on ROCm torch 2.9 while
augmentation and training stay in the torch 2.1 environment.

The four blocks below mirror openWakeWord/openwakeword/train.py lines 669-743
exactly - same directories, same parameters, same resume thresholds - so the
pinned train.py sees a finished generation stage and its own --generate_clips
becomes a no-op.

Run through generate-clips-gpu.ps1, or directly:
    env-gen\\Scripts\\python.exe generate_clips_gpu.py lilith-generic.yaml
"""

import importlib.util
import logging
import os
import sys
import types
import uuid

import torch
import yaml

logging.basicConfig(level=logging.INFO)

# Artifacts live outside the repo; override with LILITH_WAKEWORD_WORKSPACE.
_REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
HERE = os.environ.get("LILITH_WAKEWORD_WORKSPACE") or os.path.join(_REPO, "training", "wakeword")
os.makedirs(HERE, exist_ok=True)
config_path = sys.argv[1] if len(sys.argv) > 1 else "lilith-generic.yaml"
config = yaml.load(open(os.path.join(HERE, config_path)).read(), yaml.Loader)

sys.path.insert(0, os.path.abspath(os.path.join(HERE, config["piper_sample_generator_path"])))
from generate_samples import generate_samples  # noqa: E402

# data.py is loaded by path to skip the package __init__'s inference deps.
# `acoustics` is stubbed: it breaks on scipy 1.15+ (sph_harm removed) and is
# only used by augment_clips, which runs in the other env.
sys.modules.setdefault("acoustics", types.ModuleType("acoustics"))

_spec = importlib.util.spec_from_file_location(
    "oww_data", os.path.join(HERE, "openWakeWord", "openwakeword", "data.py"))
_oww_data = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_oww_data)
generate_adversarial_texts = _oww_data.generate_adversarial_texts

print(f"torch {torch.__version__}, gpu available: {torch.cuda.is_available()}")
if torch.cuda.is_available():
    print("Running on GPU. Measured 32x SLOWER than CPU on this ROCm stack - "
          "if that was not intended, stop and use generate-clips.ps1 without -Gpu.")
else:
    print("Running on CPU (the fast path here).")

# torch 2.6+ defaults weights_only=True; the piper checkpoint is a fully
# pickled model and cannot load that way. Safe here - setup-training.ps1
# fetches it from the official rhasspy release over HTTPS.
_torch_load = torch.load


def _load_trusted(*args, **kwargs):
    kwargs.setdefault("weights_only", False)
    return _torch_load(*args, **kwargs)


torch.load = _load_trusted

config["output_dir"] = os.path.abspath(os.path.join(HERE, config["output_dir"]))
os.makedirs(os.path.join(config["output_dir"], config["model_name"]), exist_ok=True)

positive_train = os.path.join(config["output_dir"], config["model_name"], "positive_train")
positive_test = os.path.join(config["output_dir"], config["model_name"], "positive_test")
negative_train = os.path.join(config["output_dir"], config["model_name"], "negative_train")
negative_test = os.path.join(config["output_dir"], config["model_name"], "negative_test")

# --- positive clips, training -------------------------------------------------
os.makedirs(positive_train, exist_ok=True)
n_current = len(os.listdir(positive_train))
if n_current <= 0.95 * config["n_samples"]:
    generate_samples(
        text=config["target_phrase"], max_samples=config["n_samples"] - n_current,
        batch_size=config["tts_batch_size"],
        noise_scales=[0.98], noise_scale_ws=[0.98], length_scales=[0.75, 1.0, 1.25],
        output_dir=positive_train, auto_reduce_batch_size=True,
        file_names=[uuid.uuid4().hex + ".wav" for _ in range(config["n_samples"])])
    torch.cuda.empty_cache()
else:
    print(f"positive_train already has ~{n_current} clips; skipping")

# --- positive clips, test -----------------------------------------------------
os.makedirs(positive_test, exist_ok=True)
n_current = len(os.listdir(positive_test))
if n_current <= 0.95 * config["n_samples_val"]:
    generate_samples(
        text=config["target_phrase"], max_samples=config["n_samples_val"] - n_current,
        batch_size=config["tts_batch_size"],
        noise_scales=[1.0], noise_scale_ws=[1.0], length_scales=[0.75, 1.0, 1.25],
        output_dir=positive_test, auto_reduce_batch_size=True)
    torch.cuda.empty_cache()
else:
    print(f"positive_test already has ~{n_current} clips; skipping")

# --- adversarial negatives, training -------------------------------------------
os.makedirs(negative_train, exist_ok=True)
n_current = len(os.listdir(negative_train))
if n_current <= 0.95 * config["n_samples"]:
    adversarial_texts = list(config["custom_negative_phrases"])
    for target_phrase in config["target_phrase"]:
        adversarial_texts.extend(generate_adversarial_texts(
            input_text=target_phrase,
            N=config["n_samples"] // len(config["target_phrase"]),
            include_partial_phrase=1.0,
            include_input_words=0.2))
    generate_samples(
        text=adversarial_texts, max_samples=config["n_samples"] - n_current,
        batch_size=config["tts_batch_size"] // 7,
        noise_scales=[0.98], noise_scale_ws=[0.98], length_scales=[0.75, 1.0, 1.25],
        output_dir=negative_train, auto_reduce_batch_size=True,
        file_names=[uuid.uuid4().hex + ".wav" for _ in range(config["n_samples"])])
    torch.cuda.empty_cache()
else:
    print(f"negative_train already has ~{n_current} clips; skipping")

# --- adversarial negatives, test ------------------------------------------------
os.makedirs(negative_test, exist_ok=True)
n_current = len(os.listdir(negative_test))
if n_current <= 0.95 * config["n_samples_val"]:
    adversarial_texts = list(config["custom_negative_phrases"])
    for target_phrase in config["target_phrase"]:
        adversarial_texts.extend(generate_adversarial_texts(
            input_text=target_phrase,
            N=config["n_samples_val"] // len(config["target_phrase"]),
            include_partial_phrase=1.0,
            include_input_words=0.2))
    generate_samples(
        text=adversarial_texts, max_samples=config["n_samples_val"] - n_current,
        batch_size=config["tts_batch_size"] // 7,
        noise_scales=[1.0], noise_scale_ws=[1.0], length_scales=[0.75, 1.0, 1.25],
        output_dir=negative_test, auto_reduce_batch_size=True)
    torch.cuda.empty_cache()
else:
    print(f"negative_test already has ~{n_current} clips; skipping")

print("clip generation complete")
