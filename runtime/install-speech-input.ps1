[CmdletBinding()]
param([string]$ProjectFolder = "")

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not populated inside param() defaults on PowerShell 5.1, so
# the repository root is resolved here instead of on the parameter.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ProjectFolder) { $ProjectFolder = Split-Path -Parent $scriptDir }

$venv = Join-Path $ProjectFolder ".speech-runtime"
$requirements = Join-Path $scriptDir "speech-input-requirements.txt"

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
