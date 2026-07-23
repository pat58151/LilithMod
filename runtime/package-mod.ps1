<#
.SYNOPSIS
    Builds a distributable zip of LilithMod.

.DESCRIPTION
    Produces a clean archive containing BepInEx and the plugin, and NOTHING
    derived from the game.

    This does not copy the installed plugin folder. That folder accumulates
    dialogue dumps, cached voice audio, conversation memory, notes, logs, and a
    config file holding an API key - none of which belong in a release. The
    build output is assembled from scratch instead.

    The dialogue catalogue is excluded too. It is the game's own script,
    compiled in only for local builds. Without it the mod leaves native dialogue
    alone and the game keeps its original voice - enforced by
    DialogueTextCatalog.Available, after a release build was found replacing
    those lines with text it did not have.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File runtime\package-mod.ps1
#>
[CmdletBinding()]
param(
    [string]$ProjectFolder = "",
    [string]$OutputZip = ""
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not populated inside param() defaults on PowerShell 5.1, so
# the repository root is resolved here instead.
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ProjectFolder) { $ProjectFolder = Split-Path -Parent $scriptDir }

$project = Join-Path $ProjectFolder "LilithMod\LilithMod.csproj"
$bepinexZip = Join-Path $ProjectFolder "tools\bepinex785.zip"
# PATH first, then the default install location, since the SDK can live anywhere.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

foreach ($required in @($project, $bepinexZip, $dotnet)) {
    if (-not (Test-Path $required)) { throw "Missing: $required" }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("lilithmod-package-" + [Guid]::NewGuid().ToString("N"))
$pluginOut = Join-Path $staging "BepInEx\plugins\LilithMod"
New-Item -ItemType Directory -Path $pluginOut -Force | Out-Null

try {
    Write-Host "Building without the dialogue catalogue..."
    # DebugType=none: symbols are stripped below anyway, and without this the
    # assembly still carries the full .pdb path from this machine, which puts the
    # developer's folder layout into a public download.
    # --no-incremental is load-bearing next to DebugType: without it MSBuild
    # reuses an intermediate assembly compiled with symbols, and the .pdb path
    # survives into the release despite the property.
    & $dotnet build $project -c Release `
        -p:IncludeDialogueCatalog=false `
        -p:DebugType=none `
        --no-incremental `
        -p:OutputPath="$pluginOut\" `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    # The build emits its own debug symbols and a build-time config; neither is
    # wanted in a release.
    Get-ChildItem $pluginOut -Include *.pdb, *.cfg -Recurse -File |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # Optional-component material: the installer's tick boxes run these scripts
    # from the plugin folder, and they copy the launcher trio to
    # %LOCALAPPDATA%\LilithMod. Placed before the guard below so anything wrong
    # here is audited like everything else.
    $speechSetupOut = Join-Path $pluginOut "speech-setup"
    $voiceSetupOut = Join-Path $pluginOut "voice-setup"
    $launcherOut = Join-Path $voiceSetupOut "launcher"
    New-Item -ItemType Directory -Path $speechSetupOut, $voiceSetupOut, $launcherOut -Force | Out-Null
    Copy-Item (Join-Path $scriptDir "push_to_talk.py") $speechSetupOut -Force
    Copy-Item (Join-Path $scriptDir "speech-input-requirements.txt") $speechSetupOut -Force
    Copy-Item (Join-Path $scriptDir "install-speech-input.ps1") $speechSetupOut -Force
    Copy-Item (Join-Path $scriptDir "install-voice-synth.ps1") $voiceSetupOut -Force
    Copy-Item (Join-Path $scriptDir "voice-synth-requirements.txt") `
        (Join-Path $voiceSetupOut "requirements-inference.txt") -Force
    Copy-Item (Join-Path $scriptDir "start-lilith.ps1") $launcherOut -Force
    Copy-Item (Join-Path $scriptDir "push_to_talk.py") $launcherOut -Force
    Copy-Item (Join-Path $ProjectFolder "start-tts.ps1") $launcherOut -Force

    # The generic model goes to speech-setup\lilith.onnx, where SpeechInputService
    # looks and the installer's tick box places it. Only the generic one ships;
    # the personal model stays local.
    $wakeModel = Join-Path $ProjectFolder "training\wakeword\lilith_generic\lilith.onnx"
    if (Test-Path $wakeModel) {
        Copy-Item $wakeModel (Join-Path $speechSetupOut "lilith.onnx") -Force
        Write-Host "Bundled the generic wake-word model."
    } else {
        Write-Host "No trained wake-word model found; the installer will omit that option."
    }

    # Guard rather than trust: if a dumped script, cached audio, or a config
    # holding a key ever reaches the staging folder, fail instead of shipping it.
    $forbidden = Get-ChildItem $pluginOut -Recurse -File | Where-Object {
        $_.Name -match '(?i)\.(wav|ckpt|pth|cfg|log)$' -or
        $_.Name -match '(?i)^(memory|notes|dialogue_nodes|player_lines)' -or
        $_.FullName -match '(?i)\\(dump|voice|cache)\\'
    }
    if ($forbidden) {
        $forbidden | ForEach-Object { Write-Warning "Refusing to package: $($_.FullName)" }
        throw "Files that must not be redistributed reached the staging folder."
    }

    Write-Host "Unpacking BepInEx..."
    Expand-Archive -Path $bepinexZip -DestinationPath $staging -Force

    # A direct first launch can restart through Steam and leak
    # DOORSTOP_DISABLE=TRUE into the visible game. The proxy must still inject
    # into that Steam-spawned process.
    $doorstopConfig = Join-Path $staging "doorstop_config.ini"
    $doorstopText = Get-Content -LiteralPath $doorstopConfig -Raw
    $doorstopText = $doorstopText -replace '(?m)^ignore_disable_switch\s*=\s*false\s*$', 'ignore_disable_switch = true'
    Set-Content -LiteralPath $doorstopConfig -Value $doorstopText -Encoding utf8

    # Assert rather than assume the rewrite matched. If a future BepInEx renames
    # or drops the key, the regex above quietly does nothing and every install
    # from this zip is silently unmodded while the logs look healthy - the single
    # worst failure this project has had, and invisible from here.
    if ((Get-Content -LiteralPath $doorstopConfig -Raw) -notmatch '(?m)^ignore_disable_switch\s*=\s*true\s*$') {
        throw "doorstop_config.ini has no 'ignore_disable_switch = true' after the rewrite. Steam will skip the mod."
    }

    # Bleeding-edge BepInEx defaults the log console to ON; a visible console on
    # every launch reads as a bug to end users. Pre-seed the config with it off;
    # BepInEx keeps existing values and fills in the rest on first run.
    $bepinexConfigDir = Join-Path $staging "BepInEx\config"
    New-Item -ItemType Directory -Path $bepinexConfigDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $bepinexConfigDir "BepInEx.cfg") -Encoding utf8 -Value @"
[Logging.Console]
Enabled = false
"@

    Copy-Item (Join-Path $PSScriptRoot "INSTALL.txt") (Join-Path $staging "INSTALL.txt") -Force

    if (-not $OutputZip) {
        $version = (Get-Item (Join-Path $pluginOut "LilithMod.dll")).VersionInfo.FileVersion
        if (-not $version) { $version = "dev" }
        $OutputZip = Join-Path $ProjectFolder ("dist\LilithMod-" + $version + ".zip")
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputZip) -Force | Out-Null
    if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }

    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $OutputZip
    $size = [Math]::Round((Get-Item $OutputZip).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Packaged: $OutputZip ($size MB)"
    Write-Host "Contains BepInEx and the plugin. No voice model, no game content."
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
