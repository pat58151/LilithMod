[CmdletBinding()]
param([string]$ProjectFolder = "")

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not populated inside param() defaults on PowerShell 5.1, so
# the repository root is resolved here instead of on the parameter.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ProjectFolder) { $ProjectFolder = Split-Path -Parent $scriptDir }
$launcher = Join-Path $ProjectFolder "runtime\start-lilith.ps1"
$shell = New-Object -ComObject WScript.Shell

$desktop = [Environment]::GetFolderPath("Desktop")
$gameShortcut = $shell.CreateShortcut((Join-Path $desktop "Lilith AI.lnk"))
$gameShortcut.TargetPath = "powershell.exe"
$gameShortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
$gameShortcut.WorkingDirectory = $ProjectFolder
$gameShortcut.WindowStyle = 7
$gameShortcut.Save()

$startup = [Environment]::GetFolderPath("Startup")
$serviceShortcut = $shell.CreateShortcut((Join-Path $startup "Lilith AI services.lnk"))
$serviceShortcut.TargetPath = "powershell.exe"
$serviceShortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`" -ServicesOnly"
$serviceShortcut.WorkingDirectory = $ProjectFolder
$serviceShortcut.WindowStyle = 7
$serviceShortcut.Save()

Write-Host "Lilith launcher and persistent service startup installed."
