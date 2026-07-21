<#
.SYNOPSIS
    Installs the GPT-SoVITS voice synthesis base into %LOCALAPPDATA%\LilithMod.

.DESCRIPTION
    Downloads everything her voice needs except the voice itself: a managed
    Python 3.10, PyTorch (CPU, or CUDA when an NVIDIA card is present), the
    GPT-SoVITS server source, and its pretrained base models. No voice model is
    installed - add one per voice-setup\README.txt afterwards.

    Idempotent: every step checks what already exists, so re-running repairs a
    failed download instead of starting over. Run by the LilithMod installer
    when its "voice synthesis base" box is ticked, or by hand.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install-voice-synth.ps1
#>
[CmdletBinding()]
param(
    # The game's BepInEx\plugins\LilithMod folder. Empty means "find it".
    [string]$PluginFolder = "",
    # Where the runtimes live. The mod finds the launcher through this layout.
    [string]$LocalDataRoot = (Join-Path $env:LOCALAPPDATA "LilithMod"),
    # Pin a GPT-SoVITS source snapshot here if main ever breaks compatibility.
    [string]$SourceZipUrl = "https://github.com/RVC-Boss/GPT-SoVITS/archive/refs/heads/main.zip"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

function Find-PluginFolder {
    # Beside this script when it runs from the installed plugin folder.
    $parent = Split-Path -Parent $scriptDir
    if (Test-Path (Join-Path $parent "LilithMod.dll")) { return $parent }

    # Otherwise the Steam discovery used everywhere else in this project.
    $relative = "steamapps\common\The NOexistenceN of Lilith"
    $steam = $null
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam")) {
        try {
            $value = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($value.SteamPath) { $steam = $value.SteamPath.Replace("/", "\"); break }
            if ($value.InstallPath) { $steam = $value.InstallPath; break }
        }
        catch { }
    }
    $roots = New-Object System.Collections.Generic.List[string]
    if ($steam) {
        $roots.Add($steam)
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+?)"') { $roots.Add($Matches[1].Replace("\\", "\")) }
            }
        }
    }
    foreach ($root in $roots) {
        $candidate = Join-Path $root $relative
        if (Test-Path (Join-Path $candidate "Lilith.exe")) {
            return Join-Path $candidate "BepInEx\plugins\LilithMod"
        }
    }
    return $null
}

function Invoke-Download([string]$url, [string]$destination) {
    if ((Test-Path $destination) -and (Get-Item $destination).Length -gt 0) {
        Write-Host "  already here: $(Split-Path $destination -Leaf)"
        return
    }
    $folder = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $partial = $destination + ".partial"
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Write-Host "  downloading $(Split-Path $destination -Leaf) (attempt $attempt)..."
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $partial -TimeoutSec 3600
            if ((Get-Item $partial).Length -eq 0) { throw "empty response" }
            Move-Item -LiteralPath $partial -Destination $destination -Force
            return
        }
        catch {
            if (Test-Path $partial) { Remove-Item $partial -Force -ErrorAction SilentlyContinue }
            if ($attempt -eq 3) { throw "Download failed after 3 attempts: $url ($($_.Exception.Message))" }
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

function Get-Uv {
    $uv = Join-Path $LocalDataRoot "uv.exe"
    if (Test-Path $uv) { return $uv }
    $zip = Join-Path $LocalDataRoot "uv.zip"
    Invoke-Download "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip" $zip
    $extract = Join-Path $LocalDataRoot "uv-extract"
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $found = Get-ChildItem $extract -Recurse -Filter "uv.exe" | Select-Object -First 1
    if (-not $found) { throw "uv.exe was not inside the downloaded archive." }
    Move-Item -LiteralPath $found.FullName -Destination $uv -Force
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    return $uv
}

function Copy-LauncherScripts {
    # The launcher trio lives beside this script differently in the repo and in
    # the installed plugin folder; probe both shapes.
    $sources = @{
        "runtime\start-lilith.ps1"  = @((Join-Path $scriptDir "..\voice-setup\launcher\start-lilith.ps1"),
                                        (Join-Path $scriptDir "start-lilith.ps1"))
        "runtime\push_to_talk.py"   = @((Join-Path $scriptDir "..\voice-setup\launcher\push_to_talk.py"),
                                        (Join-Path $scriptDir "push_to_talk.py"))
        "start-tts.ps1"             = @((Join-Path $scriptDir "..\voice-setup\launcher\start-tts.ps1"),
                                        (Join-Path $scriptDir "..\start-tts.ps1"))
    }
    foreach ($target in $sources.Keys) {
        $destination = Join-Path $LocalDataRoot $target
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        foreach ($candidate in $sources[$target]) {
            if (Test-Path $candidate) {
                Copy-Item -LiteralPath $candidate -Destination $destination -Force
                break
            }
        }
        if (-not (Test-Path $destination)) {
            throw "Could not find a source for $target near $scriptDir."
        }
    }
}

function Set-VoiceConfigPaths([string]$pluginFolder) {
    $voiceSetup = Join-Path $pluginFolder "voice-setup"
    $ini = Join-Path $voiceSetup "voice-config.ini"
    $example = Join-Path $voiceSetup "voice-config.example.ini"
    if (-not (Test-Path $ini)) {
        if (-not (Test-Path $example)) { throw "voice-config.example.ini missing in $voiceSetup." }
        Copy-Item -LiteralPath $example -Destination $ini
    }
    $runtimePath = Join-Path $LocalDataRoot "voice-runtime"
    $serverConfig = Join-Path $runtimePath "config\ja-cpu.yaml"
    $lines = @(Get-Content -LiteralPath $ini -Encoding utf8)
    $section = ""
    $sawRuntime = $false
    $sawServer = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
        if ($section -ine "Voice") { continue }
        if ($trimmed -match '^RuntimePath\s*=') { $lines[$i] = "RuntimePath = $runtimePath"; $sawRuntime = $true }
        elseif ($trimmed -match '^ServerConfig\s*=') { $lines[$i] = "ServerConfig = $serverConfig"; $sawServer = $true }
    }
    if (-not ($sawRuntime -and $sawServer)) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -ieq "[Voice]") {
                $insert = @()
                if (-not $sawRuntime) { $insert += "RuntimePath = $runtimePath" }
                if (-not $sawServer) { $insert += "ServerConfig = $serverConfig" }
                $tail = if ($i + 1 -le $lines.Count - 1) { @($lines[($i + 1)..($lines.Count - 1)]) } else { @() }
                $lines = @($lines[0..$i]) + $insert + $tail
                break
            }
        }
    }
    Set-Content -LiteralPath $ini -Value $lines -Encoding utf8
    Write-Host "voice-config.ini points at $runtimePath"
}

# ---------------------------------------------------------------------------

if (-not $PluginFolder) { $PluginFolder = Find-PluginFolder }
if (-not $PluginFolder -or -not (Test-Path $PluginFolder)) {
    Write-Error "Cannot find the game's LilithMod plugin folder. Pass -PluginFolder."
    exit 1
}

Write-Host "=== LilithMod voice synthesis base ==="
Write-Host "Plugin folder : $PluginFolder"
Write-Host "Install root  : $LocalDataRoot"
Write-Host "This downloads several GB. Re-running resumes where it stopped."
Write-Host ""

try {
    New-Item -ItemType Directory -Path $LocalDataRoot -Force | Out-Null
    $uv = Get-Uv
    Copy-LauncherScripts

    $voiceRuntime = Join-Path $LocalDataRoot "voice-runtime"
    $venvPython = Join-Path $voiceRuntime "python\Scripts\python.exe"
    $sovitsDir = Join-Path $voiceRuntime "gpt-sovits"

    # NVIDIA gets CUDA wheels; everything else runs CPU, which works and only
    # costs speed. AMD ROCm has no stock Windows wheel to offer here.
    $cuda = $null -ne (Get-Command "nvidia-smi" -ErrorAction SilentlyContinue)
    $torchIndex = if ($cuda) { "https://download.pytorch.org/whl/cu124" } else { "https://download.pytorch.org/whl/cpu" }

    if (-not (Test-Path $venvPython)) {
        Write-Host "Creating Python 3.10 environment..."
        & $uv venv (Join-Path $voiceRuntime "python") --python 3.10 --python-preference managed --relocatable
        if ($LASTEXITCODE -ne 0) { throw "uv venv failed." }
    }
    else { Write-Host "Python environment already present." }

    $torchInstalled = $false
    try { & $venvPython -c "import torch" 2>$null; $torchInstalled = ($LASTEXITCODE -eq 0) } catch { }
    if (-not $torchInstalled) {
        Write-Host "Installing PyTorch ($(if ($cuda) { 'CUDA' } else { 'CPU' }))..."
        & $uv pip install --python $venvPython torch==2.6.0 torchaudio==2.6.0 --index-url $torchIndex
        if ($LASTEXITCODE -ne 0) { throw "PyTorch installation failed." }
    }
    else { Write-Host "PyTorch already installed." }

    if (-not (Test-Path (Join-Path $sovitsDir "api_v2.py"))) {
        Write-Host "Downloading GPT-SoVITS source..."
        $sourceZip = Join-Path $LocalDataRoot "gpt-sovits-src.zip"
        Invoke-Download $SourceZipUrl $sourceZip
        $extract = Join-Path $LocalDataRoot "gpt-sovits-extract"
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -Path $sourceZip -DestinationPath $extract -Force
        $api = Get-ChildItem $extract -Recurse -Filter "api_v2.py" | Select-Object -First 1
        if (-not $api) { throw "api_v2.py not found in the GPT-SoVITS archive." }
        New-Item -ItemType Directory -Path (Split-Path -Parent $sovitsDir) -Force | Out-Null
        Move-Item -LiteralPath (Split-Path -Parent $api.FullName) -Destination $sovitsDir -Force
        Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $sourceZip -Force -ErrorAction SilentlyContinue
    }
    else { Write-Host "GPT-SoVITS source already present." }

    $requirements = Join-Path $scriptDir "requirements-inference.txt"
    if (-not (Test-Path $requirements)) {
        $requirements = Join-Path (Split-Path -Parent $scriptDir) "voice-runtime\requirements-inference.txt"
    }
    if (-not (Test-Path $requirements)) { throw "requirements-inference.txt not found beside this script." }
    # fastapi is near the end of the list, so its presence is a cheap proxy for
    # "the requirements install finished" without paying pip's resolve time on
    # every re-run.
    $depsInstalled = $false
    try { & $venvPython -c "import fastapi" 2>$null; $depsInstalled = ($LASTEXITCODE -eq 0) } catch { }
    if (-not $depsInstalled) {
        Write-Host "Installing GPT-SoVITS dependencies..."
        & $uv pip install --python $venvPython -r $requirements
        if ($LASTEXITCODE -ne 0) { throw "Dependency installation failed." }
    }
    else { Write-Host "GPT-SoVITS dependencies already installed." }

    Write-Host "Downloading pretrained base models..."
    $modelRoot = Join-Path $sovitsDir "GPT_SoVITS\pretrained_models"
    $models = @(
        "gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt",
        "gsv-v2final-pretrained/s2G2333k.pth",
        "chinese-hubert-base/config.json",
        "chinese-hubert-base/preprocessor_config.json",
        "chinese-hubert-base/pytorch_model.bin",
        "chinese-roberta-wwm-ext-large/config.json",
        "chinese-roberta-wwm-ext-large/tokenizer.json",
        "chinese-roberta-wwm-ext-large/pytorch_model.bin"
    )
    foreach ($relative in $models) {
        $destination = Join-Path $modelRoot ($relative.Replace("/", "\"))
        Invoke-Download ("https://huggingface.co/lj1995/GPT-SoVITS/resolve/main/" + $relative) $destination
    }

    $configDir = Join-Path $voiceRuntime "config"
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    $device = if ($cuda) { "cuda" } else { "cpu" }
    $half = if ($cuda) { "true" } else { "false" }
    @(
        "custom:",
        "  bert_base_path: GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large",
        "  cnhuhbert_base_path: GPT_SoVITS/pretrained_models/chinese-hubert-base",
        "  device: $device",
        "  is_half: $half",
        "  t2s_weights_path: GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt",
        "  version: v2",
        "  vits_weights_path: GPT_SoVITS/pretrained_models/gsv-v2final-pretrained/s2G2333k.pth"
    ) | Set-Content -LiteralPath (Join-Path $configDir "ja-cpu.yaml") -Encoding utf8

    Set-VoiceConfigPaths $PluginFolder

    Write-Host ""
    Write-Host "=== Voice synthesis base installed ==="
    Write-Host "Runtime: $voiceRuntime ($device)"
    Write-Host ""
    Write-Host "She does not have a voice yet - the base deliberately ships none."
    Write-Host "Add a GPT weight, a SoVITS weight and a reference clip per"
    Write-Host "voice-setup\README.txt, then fill in voice-config.ini."
    exit 0
}
catch {
    Write-Host ""
    Write-Error "Voice synthesis install failed: $($_.Exception.Message)"
    Write-Host "Re-run this script to resume; finished steps are skipped."
    exit 1
}
