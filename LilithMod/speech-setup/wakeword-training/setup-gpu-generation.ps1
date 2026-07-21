<#
.SYNOPSIS
    Builds the GPU clip-generation environment on the global ROCm install.

.DESCRIPTION
    A Python 3.12 venv with --system-site-packages, so it sees the global
    ROCm torch 2.9 (installed from repo.radeon.com) without duplicating it,
    plus the generator's own dependencies. The patched espeak-phonemizer is
    copied from the pinned 3.10 training env, which setup-training.ps1 patched.

    Requires: setup-training.ps1 already run, and the global ROCm wheels
    installed into the system Python 3.12.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup-gpu-generation.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# Artifacts - env, datasets, clips, models - run to tens of GB and live
# outside the repo. Override with LILITH_WAKEWORD_WORKSPACE.
$repoRoot = (Resolve-Path (Join-Path $here "..\..\..")).Path
$ws = if ($env:LILITH_WAKEWORD_WORKSPACE) { $env:LILITH_WAKEWORD_WORKSPACE } else { Join-Path $repoRoot "training\wakeword" }
New-Item -ItemType Directory -Path $ws -Force | Out-Null


$venv = Join-Path $ws "env-gen"
$python = Join-Path $venv "Scripts\python.exe"

# Verify the global ROCm torch exists before building on it.
py -3.12 -c "import torch; assert 'rocm' in torch.__version__, torch.__version__"
if ($LASTEXITCODE -ne 0) { throw "Global Python 3.12 has no ROCm torch. Install the repo.radeon.com wheels first." }

if (-not (Test-Path $python)) {
    Write-Host "Creating Python 3.12 generation venv (system site packages)..."
    py -3.12 -m venv $venv --system-site-packages
    if ($LASTEXITCODE -ne 0) { throw "venv creation failed." }
}

Write-Host "Installing generator dependencies..."
$env:PYTHONUTF8 = "1"
# setuptools<81 is explicit and load-bearing: webrtcvad imports pkg_resources
# at module load, Python 3.12 venvs no longer ship setuptools at all, and
# setuptools 81+ removed pkg_resources. Installed into the venv so it wins over
# whatever the system Python carries.
& $python -m pip install --disable-pip-version-check `
    "setuptools<81" webrtcvad pyyaml tqdm pronouncing audiomentations torch-audiomentations `
    speechbrain mutagen acoustics soundfile espeak-phonemizer numpy
if ($LASTEXITCODE -ne 0) { throw "Dependency installation failed." }

# The Windows patches (dll names, no glibc memstream) live in the 3.10 env's
# copy, applied by setup-training.ps1. Same package version, direct copy.
$patched = Join-Path $ws "env\Lib\site-packages\espeak_phonemizer\__init__.py"
$target = Join-Path $venv "Lib\site-packages\espeak_phonemizer\__init__.py"
if ((Test-Path $patched) -and (Select-String -Path $patched -Pattern "Patched for Windows" -Quiet)) {
    Copy-Item $patched $target -Force
    Write-Host "Patched espeak-phonemizer copied into env-gen."
}
else { throw "Patched espeak-phonemizer not found in the training env; run setup-training.ps1 first." }

Write-Host "Verifying the chain..."
$env:PYTHONIOENCODING = "utf-8"
& $python -c "import torch; from espeak_phonemizer import Phonemizer; print('torch', torch.__version__, 'gpu', torch.cuda.is_available()); print(Phonemizer('en-us').phonemize('lilith'))"
if ($LASTEXITCODE -ne 0) { throw "Verification failed." }

Write-Host ""
Write-Host "GPU generation env ready. Run: powershell -File generate-clips-gpu.ps1"
