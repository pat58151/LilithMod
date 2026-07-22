<#
.SYNOPSIS
    Installs the speech input environment (F8 push-to-talk) for LilithMod.

.DESCRIPTION
    Builds a self-contained Python 3.12 environment with faster-whisper and
    Silero VAD under %LOCALAPPDATA%\LilithMod, and puts the launcher scripts in
    place so the mod starts the listener with the game. No system Python is
    required - uv downloads a managed one. Transcription is local; no audio
    leaves the machine.

    Idempotent: re-running repairs a failed install instead of starting over.
    Run by the LilithMod installer when its "speech input" box is ticked, or by
    hand.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install-speech-input.ps1
#>
[CmdletBinding()]
param(
    # The game's BepInEx\plugins\LilithMod folder. Empty means "find it".
    [string]$PluginFolder = "",
    [string]$LocalDataRoot = (Join-Path $env:LOCALAPPDATA "LilithMod")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

function Find-PluginFolder {
    $parent = Split-Path -Parent $scriptDir
    if (Test-Path (Join-Path $parent "LilithMod.dll")) { return $parent }
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
    if ((Test-Path $destination) -and (Get-Item $destination).Length -gt 0) { return }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
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

function Set-VoiceConfigRuntimePath([string]$pluginFolder) {
    # RuntimePath is what lets the mod's ServiceBootstrap find the launcher
    # (<parent of RuntimePath>\runtime\start-lilith.ps1), so the listener starts
    # with the game even when the voice base is not installed. The voice section
    # of the launcher skips itself while its files are missing.
    $voiceSetup = Join-Path $pluginFolder "voice-setup"
    $ini = Join-Path $voiceSetup "voice-config.ini"
    $example = Join-Path $voiceSetup "voice-config.example.ini"
    if (-not (Test-Path $ini)) {
        if (-not (Test-Path $example)) { return }
        Copy-Item -LiteralPath $example -Destination $ini
    }
    $runtimePath = Join-Path $LocalDataRoot "voice-runtime"
    $lines = @(Get-Content -LiteralPath $ini -Encoding utf8)
    $section = ""
    $sawRuntime = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $trimmed = $lines[$i].Trim()
        if ($trimmed -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
        if ($section -ine "Voice") { continue }
        if ($trimmed -match '^RuntimePath\s*=\s*$') { $lines[$i] = "RuntimePath = $runtimePath"; $sawRuntime = $true }
        elseif ($trimmed -match '^RuntimePath\s*=\s*\S') { $sawRuntime = $true }
    }
    if (-not $sawRuntime) {
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].Trim() -ieq "[Voice]") {
                $tail = if ($i + 1 -le $lines.Count - 1) { @($lines[($i + 1)..($lines.Count - 1)]) } else { @() }
                $lines = @($lines[0..$i]) + @("RuntimePath = $runtimePath") + $tail
                break
            }
        }
    }
    Set-Content -LiteralPath $ini -Value $lines -Encoding utf8
}

# ---------------------------------------------------------------------------

if (-not $PluginFolder) { $PluginFolder = Find-PluginFolder }
if (-not $PluginFolder -or -not (Test-Path $PluginFolder)) {
    Write-Error "Cannot find the game's LilithMod plugin folder. Pass -PluginFolder."
    exit 1
}

Write-Host "=== LilithMod speech input ==="
Write-Host "Plugin folder : $PluginFolder"
Write-Host "Install root  : $LocalDataRoot"
Write-Host "About 2 GB downloads on first run. Re-running resumes."
Write-Host ""

try {
    New-Item -ItemType Directory -Path $LocalDataRoot -Force | Out-Null
    $uv = Get-Uv
    Copy-LauncherScripts

    $venv = Join-Path $LocalDataRoot ".speech-runtime"
    $venvPython = Join-Path $venv "Scripts\python.exe"

    if (-not (Test-Path $venvPython)) {
        Write-Host "Creating Python 3.12 environment..."
        & $uv venv $venv --python 3.12 --python-preference managed
        if ($LASTEXITCODE -ne 0) { throw "uv venv failed." }
    }
    else { Write-Host "Python environment already present." }

    $requirements = Join-Path $scriptDir "speech-input-requirements.txt"
    if (-not (Test-Path $requirements)) { throw "speech-input-requirements.txt not found beside this script." }
    $depsInstalled = $false
    try { & $venvPython -c "import faster_whisper, openwakeword" 2>$null; $depsInstalled = ($LASTEXITCODE -eq 0) } catch { }
    if (-not $depsInstalled) {
        Write-Host "Installing speech recognition packages..."
        & $uv pip install --python $venvPython -r $requirements
        if ($LASTEXITCODE -ne 0) { throw "Dependency installation failed." }
    }
    else { Write-Host "Speech packages already installed." }

    # The launcher prefers voice-runtime when it exists because its Transformers
    # Whisper can use the GPU. Speech input has its own CPU environment, so install
    # only the listener-specific packages into that preferred runtime as well. Do
    # not apply the full requirements file there: its NumPy pin conflicts with
    # GPT-SoVITS.
    $wakeRuntimes = @($venvPython)
    $voicePython = Join-Path $LocalDataRoot "voice-runtime\python\Scripts\python.exe"
    if ((Test-Path $voicePython) -and $voicePython -ne $venvPython) {
        Write-Host "Adding speech listener packages to the voice runtime..."
        & $uv pip install --python $voicePython `
            sounddevice==0.5.2 silero-vad==6.2.1 openwakeword==0.6.0
        if ($LASTEXITCODE -ne 0) { throw "Voice-runtime speech dependencies failed." }
        $wakeRuntimes += $voicePython
    }

    # openWakeWord's wheel contains code only. Its shared ONNX mel-spectrogram
    # and embedding networks are downloaded once per environment.
    foreach ($wakePython in $wakeRuntimes) {
        Write-Host "Preparing wake-word feature models in $wakePython..."
        & $wakePython -c "from openwakeword.utils import download_models; download_models(model_names=['__features_only__'])"
        if ($LASTEXITCODE -ne 0) { throw "Wake-word feature model download failed." }
    }

    Set-VoiceConfigRuntimePath $PluginFolder

    Write-Host ""
    Write-Host "=== Speech input installed ==="
    Write-Host "Environment: $venv (CPU - the launcher upgrades itself to a GPU"
    Write-Host "runtime automatically if one is ever installed)."
    Write-Host ""
    Write-Host "The listener starts with the game. Its first run downloads the"
    Write-Host "speech model and takes a few minutes; the Push to talk setting"
    Write-Host "turns on by itself once 'Speech listener ready' appears in"
    Write-Host "push-to-talk.log in the plugin folder."
    exit 0
}
catch {
    Write-Host ""
    Write-Error "Speech input install failed: $($_.Exception.Message)"
    Write-Host "Re-run this script to resume; finished steps are skipped."
    exit 1
}
