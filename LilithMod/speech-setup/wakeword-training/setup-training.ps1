<#
.SYNOPSIS
    One-time setup for training the "Lilith" wake-word models.

.DESCRIPTION
    Builds a Python 3.10 environment, clones openWakeWord and the
    piper-sample-generator fork it drives, downloads the TTS generator model,
    and stages the training datasets (17.3 GB of precomputed negatives is the
    big one). Idempotent - re-run to resume after a failed download.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File setup-training.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipData   # environment and repos only; no dataset downloads
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# Artifacts - env, datasets, clips, models - run to tens of GB and live
# outside the repo. Override with LILITH_WAKEWORD_WORKSPACE.
$repoRoot = (Resolve-Path (Join-Path $here "..\..\..")).Path
$ws = if ($env:LILITH_WAKEWORD_WORKSPACE) { $env:LILITH_WAKEWORD_WORKSPACE } else { Join-Path $repoRoot "training\wakeword" }
New-Item -ItemType Directory -Path $ws -Force | Out-Null

$repoRoot = Split-Path -Parent (Split-Path -Parent $here)
$uv = Join-Path $repoRoot "voice-runtime\uv.exe"
if (-not (Test-Path $uv)) { throw "uv.exe not found at $uv" }

$venv = Join-Path $ws "env"
$python = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $python)) {
    Write-Host "Creating Python 3.10 environment..."
    & $uv venv $venv --python 3.10 --python-preference managed
    if ($LASTEXITCODE -ne 0) { throw "uv venv failed." }
}

# Core pins first; the tensorflow trio is only for tflite export and may fail
# on some setups without costing the .onnx model, so it must not kill setup.
Write-Host "Installing training dependencies (core)..."
$core = Get-Content (Join-Path $here "requirements-train.txt") |
    Where-Object { $_ -notmatch '^(#|tensorflow|onnx_tf|protobuf)' -and $_.Trim() }
$coreFile = Join-Path $env:TEMP "wakeword-core-reqs.txt"
$core | Set-Content $coreFile -Encoding ascii
& $uv pip install --python $python --index-url https://download.pytorch.org/whl/cpu --extra-index-url https://pypi.org/simple -r $coreFile
if ($LASTEXITCODE -ne 0) { throw "Core dependency install failed." }

Write-Host "Installing tflite-export extras (failure here is non-fatal)..."
& $uv pip install --python $python protobuf==3.19.6 tensorflow-cpu==2.8.1 tensorflow_probability==0.16.0 onnx_tf==1.10.0
if ($LASTEXITCODE -ne 0) { Write-Warning "tflite extras failed to install; training still produces the .onnx model." }

foreach ($repo in @(
    @{ Name = "openWakeWord"; Url = "https://github.com/dscripka/openWakeWord" },
    @{ Name = "piper-sample-generator"; Url = "https://github.com/dscripka/piper-sample-generator" }
)) {
    $dest = Join-Path $here $repo.Name
    if (-not (Test-Path (Join-Path $dest ".git"))) {
        Write-Host "Cloning $($repo.Name)..."
        git clone --depth 1 $repo.Url $dest
        if ($LASTEXITCODE -ne 0) { throw "Clone failed: $($repo.Url)" }
    }
}

Write-Host "Installing openWakeWord (editable) and generator requirements..."
& $uv pip install --python $python -e (Join-Path $ws "openWakeWord")
if ($LASTEXITCODE -ne 0) { throw "openWakeWord install failed." }
$genReqs = Join-Path $ws "piper-sample-generator\requirements.txt"
if (Test-Path $genReqs) {
    & $uv pip install --python $python -r $genReqs
    if ($LASTEXITCODE -ne 0) { Write-Warning "generator requirements had failures; generation may still work if torch and piper-phonemize are present." }
}

# espeak-phonemizer needs Windows patches: UTF-8 build, the eSpeak NG dll
# instead of the Linux .so, and no glibc memstream. eSpeak NG: see README.
& $uv pip show --python $python espeak-phonemizer *> $null
if ($LASTEXITCODE -ne 0) {
    $env:PYTHONUTF8 = "1"
    & $uv pip install --python $python espeak-phonemizer
    if ($LASTEXITCODE -ne 0) { Write-Warning "espeak-phonemizer failed to install; clip generation will not run." }
}
$phonemizerInit = Join-Path $venv "Lib\site-packages\espeak_phonemizer\__init__.py"
if ((Test-Path $phonemizerInit) -and -not (Select-String -Path $phonemizerInit -Pattern "Patched for Windows" -Quiet)) {
    $body = Get-Content -LiteralPath $phonemizerInit -Raw
    $body = $body.Replace(
        "import ctypes`nimport re",
        "import ctypes`nimport os`nimport re")
    $body = $body.Replace(
        "        try:`n            self.lib_espeak = ctypes.cdll.LoadLibrary(`"libespeak-ng.so`")`n        except OSError:`n            # Try .so.1`n            self.lib_espeak = ctypes.cdll.LoadLibrary(`"libespeak-ng.so.1`")",
        @"
        # Patched for Windows by setup-training.ps1.
        _candidates = [
            "libespeak-ng.so",
            "libespeak-ng.so.1",
            r"C:\Program Files\eSpeak NG\libespeak-ng.dll",
            "libespeak-ng.dll",
            "espeak-ng.dll",
        ]
        _last_error = None
        for _name in _candidates:
            try:
                self.lib_espeak = ctypes.cdll.LoadLibrary(_name)
                break
            except OSError as e:
                _last_error = e
        if not self.lib_espeak:
            raise _last_error
"@)
    $body = $body.Replace(
        "        if self.stream_type == StreamType.MEMORY:`n            # Initialize libc for memory stream",
        @"
        if self.stream_type == StreamType.MEMORY and os.name == "nt":
            self.stream_type = StreamType.NONE

        if self.stream_type == StreamType.MEMORY:
            # Initialize libc for memory stream
"@.TrimEnd())
    Set-Content -LiteralPath $phonemizerInit -Value $body -Encoding utf8
    if (Select-String -Path $phonemizerInit -Pattern "Patched for Windows" -Quiet) {
        Write-Host "espeak-phonemizer patched for Windows."
    }
    else { Write-Warning "espeak-phonemizer patch did not apply; see training\wakeword\README.md." }
}

# The fork wants the v1.0.0 "libritts-high" checkpoint at this exact path and
# ships only its .json. The v2.0.0 file is not a drop-in.
$generatorModel = Join-Path $ws "piper-sample-generator\models\en-us-libritts-high.pt"
if (-not (Test-Path $generatorModel)) {
    Write-Host "Downloading the LibriTTS generator model..."
    New-Item -ItemType Directory -Path (Split-Path -Parent $generatorModel) -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 3600 `
        -Uri "https://github.com/rhasspy/piper-sample-generator/releases/download/v1.0.0/en-us-libritts-high.pt" `
        -OutFile $generatorModel
}

if (-not $SkipData) {
    Write-Host "Staging datasets (the 17.3 GB negatives file dominates)..."
    & $python (Join-Path $here "prepare_data.py")
    if ($LASTEXITCODE -ne 0) { throw "Data preparation failed - re-run to resume." }
}

Write-Host ""
Write-Host "Setup complete. Train with: powershell -File train.ps1  (add -Personal for your-voice variant)"
