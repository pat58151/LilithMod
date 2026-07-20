[CmdletBinding()]
param([string]$ProjectFolder = "D:\Lilith")

$ErrorActionPreference = "Stop"
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
