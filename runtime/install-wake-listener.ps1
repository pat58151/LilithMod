[CmdletBinding()]
param([string]$ProjectFolder = "D:\Lilith")

$ErrorActionPreference = "Stop"
$python = "py"
$venv = Join-Path $ProjectFolder ".wake-runtime"
& $python -3.12 -m venv $venv
$pip = Join-Path $venv "Scripts\pip.exe"
& $pip install --disable-pip-version-check -r (Join-Path $ProjectFolder "runtime\wake-listener-requirements.txt")
Write-Host "Push-to-talk listener installed at $venv"
