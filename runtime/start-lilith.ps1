[CmdletBinding()]
param(
    # Empty means "find it": Steam can install to any drive, so the library list
    # is asked rather than a path assumed.
    [string]$GameFolder = "",
    # The repository this script lives in.
    [string]$ProjectFolder = (Split-Path -Parent $PSScriptRoot),
    [switch]$ServicesOnly
)

$ErrorActionPreference = "Stop"

function Find-GameFolder {
    $relative = "steamapps\common\The NOexistenceN of Lilith"

    $steam = $null
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam")) {
        try {
            $value = (Get-ItemProperty -Path $key -ErrorAction Stop)
            if ($value.SteamPath) { $steam = $value.SteamPath.Replace("/", "\"); break }
            if ($value.InstallPath) { $steam = $value.InstallPath; break }
        }
        catch { }
    }

    $roots = New-Object System.Collections.Generic.List[string]
    if ($steam) { $roots.Add($steam) }

    # Additional libraries are listed in libraryfolders.vdf as "path" entries.
    if ($steam) {
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+?)"') {
                    $roots.Add($Matches[1].Replace("\\", "\"))
                }
            }
        }
    }

    foreach ($root in $roots) {
        $candidate = Join-Path $root $relative
        if (Test-Path (Join-Path $candidate "Lilith.exe")) { return $candidate }
    }
    return $null
}

if (-not $GameFolder) {
    $GameFolder = Find-GameFolder
    if (-not $GameFolder) {
        throw "Could not find the game. Pass -GameFolder ""<path to The NOexistenceN of Lilith>""."
    }
}
$configPath = Join-Path $GameFolder "BepInEx\config\LilithMod.cfg"
$pluginFolder = Join-Path $GameFolder "BepInEx\plugins\LilithMod"
$voiceFolder = Join-Path $pluginFolder "voice-setup"
$voiceConfig = Join-Path $voiceFolder "voice-config.ini"
$voiceExample = Join-Path $voiceFolder "voice-config.example.ini"
$gameExe = Join-Path $GameFolder "Lilith.exe"
$languageState = Join-Path $pluginFolder "tts-language.txt"
$startupLog = Join-Path $pluginFolder "service-startup.log"

New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
function Write-StartupLog([string]$message) {
    Add-Content -LiteralPath $startupLog -Value ("{0:O} {1}" -f [DateTimeOffset]::Now, $message) -Encoding utf8
}

# Keep BepInEx logging on disk without opening a second console window.
$bepInExConfig = Join-Path $GameFolder "BepInEx\config\BepInEx.cfg"
if (Test-Path $bepInExConfig) {
    $lines = Get-Content -LiteralPath $bepInExConfig
    $inConsoleSection = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[(.+)\]\s*$') { $inConsoleSection = $matches[1] -ieq 'Logging.Console'; continue }
        if ($inConsoleSection -and $lines[$i] -match '^\s*Enabled\s*=') { $lines[$i] = 'Enabled = false'; break }
    }
    Set-Content -LiteralPath $bepInExConfig -Value $lines -Encoding utf8
}

function Read-LegacySetting([string]$name, [string]$fallback) {
    if (-not (Test-Path $configPath)) { return $fallback }
    $match = Select-String -Path $configPath -Pattern ("^" + [regex]::Escape($name) + "\s*=\s*(.*)$") | Select-Object -Last 1
    if ($null -eq $match) { return $fallback }
    return $match.Matches[0].Groups[1].Value.Trim()
}

function Read-IniSetting([string]$section, [string]$name, [string]$fallback) {
    if (-not (Test-Path $voiceConfig)) { return $fallback }
    $current = ""
    foreach ($raw in Get-Content -LiteralPath $voiceConfig -Encoding utf8) {
        $line = $raw.Trim()
        if ($line -match '^\[(.+)\]$') { $current = $matches[1]; continue }
        if ($current -ieq $section -and $line -match ('^' + [regex]::Escape($name) + '\s*=\s*(.*)$')) {
            return $matches[1].Trim()
        }
    }
    return $fallback
}

function Test-Port([int]$port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try { $client.Connect("127.0.0.1", $port); return $true }
    catch { return $false }
    finally { $client.Dispose() }
}

if (-not (Test-Path $voiceConfig) -and (Test-Path $voiceExample)) {
    Copy-Item -LiteralPath $voiceExample -Destination $voiceConfig
}

try {
    $voiceEnabled = (Read-IniSetting "Voice" "Enabled" "false") -ieq "true"
    if ($voiceEnabled) {
        $language = (Read-IniSetting "Voice" "SpokenLanguage" "ja").ToLowerInvariant()
        if ($language.StartsWith("zh")) { $language = "zh" }
        elseif ($language.StartsWith("en")) { $language = "en" }
        else { $language = "ja" }

        $section = "Profile.$language"
        $endpoint = Read-IniSetting "Voice" "Endpoint" "http://127.0.0.1:9880/tts"
        $endpointUri = [Uri]$endpoint
        $origin = $endpointUri.GetLeftPart([UriPartial]::Authority)
        $port = $endpointUri.Port
        $runtimePath = Read-IniSetting "Voice" "RuntimePath" (Join-Path $ProjectFolder "voice-runtime")
        $serverConfig = Read-IniSetting "Voice" "ServerConfig" (Join-Path $runtimePath "config\ja-finetuned.yaml")
        $voicePython = Join-Path $runtimePath "python\Scripts\python.exe"
        $apiScript = Join-Path $runtimePath "gpt-sovits\api_v2.py"
        $gptWeights = Read-IniSetting $section "GptWeights" ""
        $sovitsWeights = Read-IniSetting $section "SovitsWeights" ""
        $refAudio = Read-IniSetting $section "RefAudioPath" ""
        $promptText = Read-IniSetting $section "PromptText" ""
        $promptLang = Read-IniSetting $section "PromptLanguage" $language
        $warmText = Read-IniSetting $section "WarmUpText" "Lilith is here."
        $identity = Read-IniSetting $section "CacheIdentity" $language
        $required = @($voicePython, $apiScript, $serverConfig, $gptWeights, $sovitsWeights, $refAudio)
        $missing = @($required | Where-Object { [string]::IsNullOrWhiteSpace($_) -or -not (Test-Path -LiteralPath $_) })

        if ($missing.Count -gt 0) {
            Write-StartupLog "Vocal synthesis skipped. Complete voice-setup\voice-config.ini. Missing $($missing.Count) required file(s)."
        }
        else {
            $runningIdentity = if (Test-Path $languageState) { (Get-Content $languageState -Raw).Trim() } else { "" }
            if ((Test-Port $port) -and $runningIdentity -ne $identity) {
                Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.CommandLine -like "*api_v2.py*" -and $_.CommandLine -like "*-p $port*" } |
                    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
                $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
                while ((Test-Port $port) -and [DateTime]::UtcNow -lt $stopDeadline) { Start-Sleep -Milliseconds 100 }
            }

            if (-not (Test-Port $port)) {
                $ttsOut = Join-Path $pluginFolder "gpt-sovits.log"
                $ttsErr = Join-Path $pluginFolder "gpt-sovits-error.log"
                $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$ProjectFolder\start-tts.ps1`" -Runtime `"$runtimePath`" -Config `"$serverConfig`" -Port $port"
                Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $ttsOut -RedirectStandardError $ttsErr
            }

            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            while (-not (Test-Port $port)) {
                if ([DateTime]::UtcNow -gt $deadline) { throw "GPT-SoVITS did not become ready." }
                Start-Sleep -Milliseconds 250
            }

            Invoke-WebRequest -UseBasicParsing -Uri ($origin + "/set_gpt_weights?weights_path=" + [Uri]::EscapeDataString($gptWeights)) -TimeoutSec 180 | Out-Null
            Invoke-WebRequest -UseBasicParsing -Uri ($origin + "/set_sovits_weights?weights_path=" + [Uri]::EscapeDataString($sovitsWeights)) -TimeoutSec 180 | Out-Null
            $body = @{
                text = $warmText; text_lang = $language; ref_audio_path = $refAudio
                prompt_text = $promptText; prompt_lang = $promptLang; media_type = "wav"
                streaming_mode = $false; text_split_method = "cut5"; fragment_interval = 0.3
            } | ConvertTo-Json -Compress
            $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
            Invoke-WebRequest -UseBasicParsing -Uri ($origin + "/tts") -Method Post -ContentType "application/json; charset=utf-8" -Body $bodyBytes -TimeoutSec 180 | Out-Null
            [IO.File]::WriteAllText($languageState, $identity, [Text.UTF8Encoding]::new($false))
            Write-StartupLog "GPT-SoVITS ready. language=$language identity=$identity"
        }
    }
}
catch {
    Write-StartupLog ("Vocal synthesis startup failed: " + $_.Exception.Message)
}

try {
    $speechEnabled = Read-LegacySetting "PushToTalkEnabled" "true"
    # Prefer the ROCm runtime: transformers runs Whisper on the Radeon, while the
    # .speech-runtime venv is CPU-only (CTranslate2 has no ROCm backend).
    $rocmPython = Join-Path $ProjectFolder "voice-runtime\python\Scripts\python.exe"
    $cpuPython = Join-Path $ProjectFolder ".speech-runtime\Scripts\python.exe"
    if (Test-Path $rocmPython) {
        $speechPython = $rocmPython
        $backendArgs = "--backend transformers --whisper-model openai/whisper-large-v3-turbo"
    }
    else {
        $speechPython = $cpuPython
        $backendArgs = "--backend faster-whisper --whisper-model large-v3"
    }
    if ($speechEnabled -ieq "true" -and (Test-Path $speechPython)) {
        # Clear a trigger left behind by a crash, or the listener records immediately.
        $trigger = Join-Path $pluginFolder "push-to-talk.active"
        if (Test-Path $trigger) { Remove-Item $trigger -Force -ErrorAction SilentlyContinue }
        # The listener prints the bias vocabulary, which contains Japanese and
        # Chinese. On a cp874 console that raises UnicodeEncodeError and kills it
        # at startup - the same trap as start-tts.ps1.
        $env:PYTHONIOENCODING = "utf-8"
        $alreadyRunning = Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -like "*push_to_talk.py*" -or $_.CommandLine -like "*wake_listener.py*" }
        $alreadyRunning | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        $speechArgs = "`"$ProjectFolder\runtime\push_to_talk.py`" --output `"$pluginFolder\speech-command.txt`" --trigger `"$trigger`" $backendArgs --save-last `"$pluginFolder\last-utterance.wav`""
        Start-Process $speechPython -ArgumentList $speechArgs -WindowStyle Hidden -RedirectStandardOutput (Join-Path $pluginFolder "push-to-talk.log") -RedirectStandardError (Join-Path $pluginFolder "push-to-talk-error.log")
        Write-StartupLog "Push-to-talk listener started."
    }
}
catch {
    Write-StartupLog ("Push-to-talk startup failed: " + $_.Exception.Message)
}

if (-not $ServicesOnly) { Start-Process $gameExe }
