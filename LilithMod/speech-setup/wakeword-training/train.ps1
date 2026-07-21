<#
.SYNOPSIS
    Trains the "Lilith" wake-word model(s).

.DESCRIPTION
    Default run: the generic model (synthetic voices only, the shippable one),
    in three stages - generate clips, augment, train. -Personal reuses the
    generic run's generated clips, injects the real recordings from positive\
    (duplicated so they weigh in against 30k synthetic), featurizes the
    own-voice negatives, and trains the personal variant.

    Stages are resumable: generation is skipped when the clips already exist.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File train.ps1
    powershell -ExecutionPolicy Bypass -File train.ps1 -Personal
#>
[CmdletBinding()]
param(
    [switch]$Personal,
    # How many copies of each real recording to inject; at 100 clips and one
    # augmentation round, 15 gives the personal voice a few percent of the
    # positive mass - sharpening without narrowing.
    [int]$UserClipDuplication = 15
)

$ErrorActionPreference = "Stop"
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# Artifacts - env, datasets, clips, models - run to tens of GB and live
# outside the repo. Override with LILITH_WAKEWORD_WORKSPACE.
$repoRoot = (Resolve-Path (Join-Path $here "..\..\..")).Path
$ws = if ($env:LILITH_WAKEWORD_WORKSPACE) { $env:LILITH_WAKEWORD_WORKSPACE } else { Join-Path $repoRoot "training\wakeword" }
New-Item -ItemType Directory -Path $ws -Force | Out-Null

Set-Location $ws

# The generator prints IPA phonemes; on this machine's cp874 console that is a
# fatal UnicodeEncodeError without these (same trap as start-tts.ps1).
$env:PYTHONIOENCODING = "utf-8"
$env:PYTHONUTF8 = "1"

$python = Join-Path $ws "env\Scripts\python.exe"
$trainScript = Join-Path $ws "openWakeWord\openwakeword\train.py"
foreach ($required in @($python, $trainScript)) {
    if (-not (Test-Path $required)) { throw "Missing $required - run setup-training.ps1 first." }
}

function Invoke-Stage([string]$config, [string]$stage) {
    Write-Host ""
    Write-Host "=== $config : $stage ===" -ForegroundColor Cyan
    & $python $trainScript --training_config $config $stage
    if ($LASTEXITCODE -ne 0) { throw "$stage failed for $config." }
}

if (-not $Personal) {
    # Generation is the long stage; skip it when a previous run already made
    # the clips (count is approximate on purpose - a resumed generation that
    # nearly finished is not worth redoing).
    $genDir = Join-Path $ws "lilith_generic"
    $existing = 0
    if (Test-Path $genDir) {
        $existing = @(Get-ChildItem $genDir -Recurse -Filter "*.wav" -ErrorAction SilentlyContinue).Count
    }
    if ($existing -lt 25000) {
        Invoke-Stage "lilith-generic.yaml" "--generate_clips"
    }
    else { Write-Host "Generated clips already present ($existing wavs); skipping generation." }
    Invoke-Stage "lilith-generic.yaml" "--augment_clips"
    Invoke-Stage "lilith-generic.yaml" "--train_model"
    Write-Host ""
    Write-Host "Generic model done: lilith_generic\lilith.onnx"
    exit 0
}

# ---- personal variant ------------------------------------------------------

$genDir = Join-Path $ws "lilith_generic"
if (-not (Test-Path $genDir)) {
    throw "Run the generic training first (train.ps1 without -Personal); the personal run reuses its generated clips."
}
$perDir = Join-Path $ws "lilith_personal"

if (-not (Test-Path (Join-Path $perDir "injected.marker"))) {
    Write-Host "Copying generated clips from the generic run..."
    robocopy $genDir $perDir /E /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE." }
    # Features computed for the generic set would shadow the injected clips.
    Get-ChildItem $perDir -Filter "*.npy" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

    # The real recordings join the synthetic positives; augmentation then
    # varies each copy independently, which is what makes duplication useful
    # rather than redundant.
    $positiveTrain = Get-ChildItem $perDir -Directory | Where-Object { $_.Name -match "positive" -and $_.Name -match "train" } | Select-Object -First 1
    $positiveTest = Get-ChildItem $perDir -Directory | Where-Object { $_.Name -match "positive" -and $_.Name -match "test" } | Select-Object -First 1
    if (-not $positiveTrain) { throw "Could not find the positive training clip folder under $perDir." }
    $userClips = @(Get-ChildItem (Join-Path $ws "positive") -Filter "*.wav")
    if ($userClips.Count -eq 0) { throw "No recordings in positive\." }
    Write-Host "Injecting $($userClips.Count) real recordings x$UserClipDuplication..."
    $testShare = [Math]::Max(1, [int]($userClips.Count / 10))
    for ($i = 0; $i -lt $userClips.Count; $i++) {
        $clip = $userClips[$i]
        if ($positiveTest -and $i -lt $testShare) {
            Copy-Item $clip.FullName (Join-Path $positiveTest.FullName ("user-" + $clip.Name))
            continue
        }
        for ($d = 0; $d -lt $UserClipDuplication; $d++) {
            Copy-Item $clip.FullName (Join-Path $positiveTrain.FullName ("user-$d-" + $clip.Name))
        }
    }
    Set-Content (Join-Path $perDir "injected.marker") "done"
}
else { Write-Host "Clips already copied and injected." }

if (-not (Test-Path (Join-Path $ws "data\user_negative_features.npy"))) {
    Write-Host "Featurizing own-voice negatives..."
    & $python (Join-Path $here "featurize_negatives.py")
    if ($LASTEXITCODE -ne 0) { throw "Negative featurization failed." }
}

Invoke-Stage "lilith-personal.yaml" "--augment_clips"
Invoke-Stage "lilith-personal.yaml" "--train_model"
Write-Host ""
Write-Host "Personal model done: lilith_personal\lilith_personal.onnx (local use only - never ship this one)"
