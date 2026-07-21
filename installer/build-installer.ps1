<#
.SYNOPSIS
    Builds dist\LilithMod-Setup-<version>.exe from the release zip.

.DESCRIPTION
    Extracts dist\LilithMod-<version>.zip (made by runtime\package-mod.ps1) into
    installer\payload\ and compiles LilithMod.iss against it with Inno Setup 6.
    Run verify-package.py against the zip first; this script only sanity-checks
    the payload, it does not repeat the full audit.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
[CmdletBinding()]
param(
    # Empty means "newest dist\LilithMod-<version>.zip".
    [string]$ZipPath = "",
    # Empty means "parse from the zip filename".
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$RepoDir = Split-Path $PSScriptRoot -Parent
$DistDir = Join-Path $RepoDir "dist"
$Payload = Join-Path $PSScriptRoot "payload"
$Iss     = Join-Path $PSScriptRoot "LilithMod.iss"

if (-not $ZipPath) {
    $candidates = Get-ChildItem $DistDir -Filter "LilithMod-*.zip" |
        Where-Object { $_.Name -match '^LilithMod-\d+(\.\d+)*\.zip$' } |
        Sort-Object LastWriteTime -Descending
    if (-not $candidates) { throw "No LilithMod-<version>.zip in $DistDir. Run runtime\package-mod.ps1 first." }
    $ZipPath = $candidates[0].FullName
}
if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }

if (-not $Version) {
    if ((Split-Path $ZipPath -Leaf) -match '^LilithMod-(\d+(\.\d+)*)\.zip$') { $Version = $Matches[1] }
    else { throw "Cannot parse a version from '$ZipPath'. Pass -Version." }
}

$Iscc = $null
$isccCandidates = @(
    (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
)
foreach ($c in $isccCandidates) {
    if ($c -and (Test-Path $c)) { $Iscc = $c; break }
}
if (-not $Iscc) { throw "ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup)." }

Write-Host "==> Staging payload from $(Split-Path $ZipPath -Leaf)" -ForegroundColor Cyan
if (Test-Path $Payload) { Remove-Item $Payload -Recurse -Force }
Expand-Archive -Path $ZipPath -DestinationPath $Payload

# Sanity checks: the injection point, the mod, and the one line that decides
# whether any of it loads (PORTABILITY.md, "What an installer must do", step 3).
foreach ($rel in @("winhttp.dll", "doorstop_config.ini", "BepInEx\plugins\LilithMod\LilithMod.dll")) {
    if (-not (Test-Path (Join-Path $Payload $rel))) { throw "Payload is missing $rel - not a valid release zip." }
}
$doorstop = Get-Content (Join-Path $Payload "doorstop_config.ini") -Raw
if ($doorstop -notmatch '(?m)^\s*ignore_disable_switch\s*=\s*true\s*$') {
    throw "Payload doorstop_config.ini does not set ignore_disable_switch = true. Refusing to build."
}

Write-Host "==> Compiling installer (version $Version)" -ForegroundColor Cyan
& $Iscc "/DAppVersion=$Version" /Qp $Iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$Out = Join-Path $DistDir "LilithMod-Setup-$Version.exe"
if (-not (Test-Path $Out)) { throw "ISCC reported success but $Out does not exist." }
$mb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "==> Built $Out ($mb MB)" -ForegroundColor Green
