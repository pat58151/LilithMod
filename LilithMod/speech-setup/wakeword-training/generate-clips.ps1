<#
.SYNOPSIS
    Generates the wake-word training clips (CPU by default).

.DESCRIPTION
    Runs generate_clips.py in the env-gen environment. Resumable - already
    generated directories are skipped, matching train.py's own thresholds.
    Afterwards run train.ps1 as usual; its generation stage sees the clips and
    skips itself.

    CPU is the default: measured 32x faster than ROCm here, because MIOpen
    re-searches for a solver on every new sequence length. -Gpu re-enables
    the GPU path; re-measure before trusting it. See document/MIOPEN.md.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File generate-clips.ps1
    powershell -ExecutionPolicy Bypass -File generate-clips.ps1 -Config lilith-personal.yaml
#>
[CmdletBinding()]
param(
    [string]$Config = "lilith-generic.yaml",
    [switch]$Gpu
)

$ErrorActionPreference = "Stop"
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# Artifacts - env, datasets, clips, models - run to tens of GB and live
# outside the repo. Override with LILITH_WAKEWORD_WORKSPACE.
$repoRoot = (Resolve-Path (Join-Path $here "..\..\..")).Path
$ws = if ($env:LILITH_WAKEWORD_WORKSPACE) { $env:LILITH_WAKEWORD_WORKSPACE } else { Join-Path $repoRoot "training\wakeword" }
New-Item -ItemType Directory -Path $ws -Force | Out-Null

Set-Location $ws

$python = Join-Path $ws "env-gen\Scripts\python.exe"
if (-not (Test-Path $python)) { throw "env-gen missing - run setup-gpu-generation.ps1 first." }

# IPA on a cp874 console is a fatal UnicodeEncodeError without these.
$env:PYTHONIOENCODING = "utf-8"
$env:PYTHONUTF8 = "1"

if ($Gpu) {
    Write-Host "Generating on GPU (measured 32x slower here - see this script's help)." -ForegroundColor Yellow
    Remove-Item Env:\HIP_VISIBLE_DEVICES -ErrorAction SilentlyContinue
    Remove-Item Env:\CUDA_VISIBLE_DEVICES -ErrorAction SilentlyContinue
}
else {
    # "-1", not "": an empty string DELETES the variable on Windows, leaving
    # the GPU visible and the run silently on the slow path.
    Write-Host "Generating on CPU (faster than ROCm for this model)." -ForegroundColor Cyan
    $env:HIP_VISIBLE_DEVICES = "-1"
    $env:CUDA_VISIBLE_DEVICES = "-1"
}

& $python (Join-Path $here "generate_clips.py") $Config
if ($LASTEXITCODE -ne 0) { throw "Clip generation failed - re-run to resume." }
Write-Host "Done. Continue with train.ps1 (its generate stage will skip itself)."
