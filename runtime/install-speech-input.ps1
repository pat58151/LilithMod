[CmdletBinding()]
# Defaults to the repository this script lives in, so it works from any checkout
# rather than only the machine it was written on.
param([string]$ProjectFolder = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = "Stop"

$venv = Join-Path $ProjectFolder ".speech-runtime"
$requirements = Join-Path $PSScriptRoot "speech-input-requirements.txt"

if (-not (Test-Path $requirements)) {
    throw "Cannot find $requirements"
}

& py -3.12 -m venv $venv
if (-not $?) { throw "Could not create the virtual environment. Is Python 3.12 installed?" }

$pip = Join-Path $venv "Scripts\pip.exe"
& $pip install --disable-pip-version-check -r $requirements
if (-not $?) { throw "Dependency installation failed." }

Write-Host "Speech input installed at $venv"
Write-Host "This is the CPU fallback. When a GPU PyTorch is available the launcher"
Write-Host "prefers it automatically and this environment goes unused."
