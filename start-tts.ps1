<#
.SYNOPSIS
    Starts the local GPT-SoVITS TTS service that the mod's voice feature calls.

.DESCRIPTION
    Serves the fine-tuned Japanese Lilith voice on http://127.0.0.1:9880.
    Nothing is uploaded; the service reads the reference clip from disk and the
    mod POSTs text to it over loopback.

    Normally you do not run this yourself. runtime\start-lilith.ps1 launches it
    hidden and redirects its output to gpt-sovits.log and gpt-sovits-error.log
    in the plugin folder; those logs are where a startup failure shows up.

    Run it directly to watch it live, which is the reason to do so at all. It
    stays in the foreground until Ctrl+C. The first request after startup is
    slow (~30-60 s) because kernel compilation happens per sequence length; the
    mod warms it up in the background on launch.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\start-tts.ps1
#>
[CmdletBinding()]
param(
    # Empty means "derive from where this script lives". $PSScriptRoot is not
    # populated inside param() defaults on PowerShell 5.1, so these resolve below.
    [string]$Runtime = "",
    [string]$Config  = "",
    [string]$Address = "127.0.0.1",
    [int]$Port       = 9880
)

$ErrorActionPreference = "Stop"

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Runtime) { $Runtime = Join-Path $scriptDir "voice-runtime" }
if (-not $Config)  { $Config  = Join-Path $Runtime "config\ja-finetuned.yaml" }

$python = Join-Path $Runtime "python\Scripts\python.exe"
$appDir = Join-Path $Runtime "gpt-sovits"

foreach ($p in @($python, $Config, (Join-Path $appDir "api_v2.py"))) {
    if (-not (Test-Path $p)) { throw "Missing '$p'." }
}

# GPT-SoVITS prints CJK to stdout. On a cp874 console that raises
# UnicodeEncodeError, which the service reports as a misleading 'HTTP 400 tts
# failed' rather than an encoding problem. Force UTF-8.
$env:PYTHONIOENCODING = "utf-8"

# MIOpen picks a convolution solver per input shape, and speech is
# variable-length, so a machine that has not built up ~/.miopen yet pays that
# search on nearly every sentence. Measured on a cleared cache, six varied
# Japanese sentences, idle machine:
#
#   warm cache, default      median 2279 ms
#   cold cache, default      median 4745 ms   <- what a new install gets
#   cold cache, FAST         median 2523 ms
#
# FAST swaps the exhaustive search for heuristics and erases the gap, so the
# first session is as quick as the hundredth. It costs nothing once warm - the
# same six sentences measured identically on a warm cache either way.
# See document\MIOPEN.md.
$env:MIOPEN_FIND_MODE = "FAST"

# api_v2.py resolves bert_base_path / cnhuhbert_base_path relative to its own
# working directory, so it has to run from the app folder.
Push-Location $appDir
try {
    Write-Host "GPT-SoVITS on http://${Address}:${Port}  (Ctrl+C to stop)" -ForegroundColor Cyan
    Write-Host "Model: $Config" -ForegroundColor DarkGray
    & $python "api_v2.py" -a $Address -p $Port -c $Config
}
finally {
    Pop-Location
}
