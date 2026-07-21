"""
verify-bilingual.py - checks the bilingual voice feature: Japanese audio with
English subtitles, advanced per sentence, with synthesis of the next sentence
overlapping playback of the current one.

Read-only. Builds the mod, then asserts the load-bearing pieces are present.
Follows the pattern of verify-voice.py.
"""

import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
MOD_DIR = os.path.join(ROOT, "LilithMod")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"

failures = []


def check(condition, message):
    if not condition:
        failures.append(message)


# -- 1. Build -----------------------------------------------------------------
proj = os.path.join(MOD_DIR, "LilithMod.csproj")
# Build somewhere harmless. The csproj's OutputPath is the live plugin folder,
# so a plain build deploys - which locks against a running game and made this
# suite impossible to run at the moment it was most useful.
r = subprocess.run(
    [DOTNET, "build", proj, "-c", "Release",
     "-p:OutputPath=" + os.path.join(ROOT, "build-test") + os.sep],
    capture_output=True, text=True, cwd=MOD_DIR,
)
if r.returncode != 0:
    print("VERIFY FAIL - dotnet build returned", r.returncode)
    for line in (r.stdout + r.stderr).splitlines():
        if ": error" in line:
            print("  ", line.strip())
    sys.exit(1)
print("[verify-bilingual] Build succeeded")


def read(*parts):
    path = os.path.join(*parts)
    if not os.path.exists(path):
        return ""
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        return f.read()


utterance = read(MOD_DIR, "Utterance.cs")
cue = read(MOD_DIR, "SubtitleCue.cs")
speech = read(MOD_DIR, "SpeechQueueProcessor.cs")
chat = read(MOD_DIR, "LlmChatController.cs")
plugin = read(MOD_DIR, "LilithModPlugin.cs")
persona = read(MOD_DIR, "PersonaPrompt.cs")
dynamic = read(MOD_DIR, "DynamicContext.cs")
memory = read(MOD_DIR, "MemoryStore.cs")
live_info = read(MOD_DIR, "LiveInformationService.cs")
integrations = read(MOD_DIR, "ModIntegrations.cs")
game_voice = read(MOD_DIR, "GameVoiceCoordinator.cs")
settings = read(MOD_DIR, "SettingsBridge.cs")
voice_setup = read(MOD_DIR, "VoiceSetup.cs")
note_journal = read(MOD_DIR, "NoteJournal.cs")
speech_input = read(MOD_DIR, "SpeechInputService.cs")
voice_monitor = read(MOD_DIR, "VoiceServiceMonitor.cs")
tts = read(MOD_DIR, "TtsClient.cs")
switcher = read(MOD_DIR, "VoiceModelSwitcher.cs")
ptt = read(ROOT, "runtime", "push_to_talk.py")
precache = read(ROOT, "runtime", "precache-game-voice.py")
requirements = read(ROOT, "runtime", "speech-input-requirements.txt")
window_focus = read(MOD_DIR, "WindowFocus.cs")
launcher = read(ROOT, "runtime", "start-lilith.ps1")
voice_player = read(MOD_DIR, "VoicePlayer.cs")

# -- 2. The sentence pair type ------------------------------------------------
check(utterance, "LilithMod/Utterance.cs is missing")
check("JaText" in utterance and "EnText" in utterance,
      "Utterance must carry both JaText and EnText")

# -- 3. Per-sentence queue and cross-thread hand-off ---------------------------
check("ConcurrentQueue<Utterance>" in speech,
      "SpeechQueueProcessor must queue Utterance, not raw strings")
check("SubtitleQueue" in speech and "SubtitleQueue" in chat and "MarkDisplayed" in cue,
      "A SubtitleQueue must carry subtitles from the voice thread to the main thread")
check("VoiceFailureQueue" in speech and "VoiceFailureQueue" in chat,
      "Synthesis failure must be signalled on its own queue, not as a sentinel string "
      "inside the subtitle queue")
check("CancelCurrent" in speech and "CancelCurrent" in chat,
      "A new reply must abandon the previous reply's queued sentences")

# -- 4. The overlap, which is the whole point of the change -------------------
# Synthesis of the next sentence must be started BEFORE PlaySync blocks on the
# current one; if it is started after, there is no overlap at all.
check("Task.Run" in speech,
      "The next sentence must be synthesised on a task so it overlaps playback")
if "Task.Run" in speech and "PlaySync" in speech:
    check(speech.index("Task.Run") < speech.rindex("PlaySync"),
          "Synthesis of the next sentence must start BEFORE PlaySync, otherwise "
          "nothing overlaps")

# -- 4a. Audio-ready subtitle handoff ----------------------------------------
check("SubtitleQueue.Enqueue(subtitleCue)" in speech,
      "A subtitle must only be handed off after its audio is ready")
check("WaitUntilDisplayed" in speech and "MarkDisplayed" in chat,
      "Playback must wait until Unity has displayed the audio-ready subtitle")
check("DisplayReplyText(english.Count > 0 ? english[0] : null)" not in chat,
      "The first bilingual subtitle must not appear before synthesis completes")
if "WaitUntilDisplayed" in speech and "PlaySync" in speech:
    check(speech.index("WaitUntilDisplayed") < speech.rindex("PlaySync"),
          "Playback must begin only after the subtitle display handoff")
check("AutoResetEvent" in speech and "_queueAvailable.Set()" in speech,
      "New speech must wake immediately instead of waiting on a polling interval")

# -- 5. Bubble refresh uses the path the game actually reacts to --------------
check("StartDialogue(9500000)" in chat,
      "Subtitles must be shown via StartDialogue; assigning node.text alone does "
      "not refresh a dialogue already on screen")
check("ReplyFinishedQueue" in speech and "EndOfReply" in utterance and
      "ForceEndDialogue" in chat,
      "The reply bubble must close after the final audio finishes")
check("node.id == 9500000" in game_voice,
      "The mod reply node must bypass native-dialogue voice replacement")
check("AudioManager.IsTimerAlarmRinging" in game_voice and "2051005" in game_voice and
      "AlarmEnglish[alarmIndex]" in game_voice,
      "Dynamic line-0 timer/alarm dialogue must use Japanese catalog speech")
check("2951001" in game_voice and "2951002" in game_voice and "_alarmDialogueUntil" in game_voice,
      "Alarm acknowledgement and snooze dialogue must stay in Japanese synthesis")
check("nameof(AudioManager.PlayVoice)" in integrations and
      "new[] { typeof(string), typeof(bool) }" in integrations,
      "Both native PlayVoice overloads must be suppressed during replacement")
# The bubble and the audio reach the player by different routes. Gating one but
# not the other yielded the game's Chinese voice under no subtitle at all, so
# assert the single shared predicate rather than either call site.
check("HoldingForSynthesis" in game_voice and "EverAvailable" in game_voice,
      "The synthesis grace window must be one predicate in GameVoiceCoordinator")
# The Update() probe cannot run before the first frame, which is when the
# EnterGame greeting fires. Availability has to be establishable off that loop
# or the opening line keeps the game's own Chinese voice on every launch.
check("NoteServiceAnswered" in voice_monitor and "NoteServiceAnswered" in speech,
      "A successful warm-up must mark the service available, not wait for the probe")
check(len(re.findall(r"return AllowNativeVoice\(\);", integrations)) >= 3 and
      "GameVoiceCoordinator.HoldingForSynthesis" in integrations,
      "Every native-audio prefix must honour the grace window, not just the bubble")

# -- 6. Reply parsing and its fallbacks ---------------------------------------
check("ParseUtterances" in chat, "A tolerant reply parser is required")
check("MaxUtterancesPerReply" in chat, "The sentence count must be capped")
check("response_format" in chat,
      "The request should ask for JSON mode to suppress markdown fences")
check('payload["thinking"]' in chat and 'type = "disabled"' in chat,
      "DeepSeek V4 Flash thinking must be disabled for low-latency dialogue")
check('["max_tokens"] = 256' in chat,
      "Short dialogue output must be capped to avoid runaway generation latency")

# -- 7. Prompt: bilingual contract, without losing her measured voice ---------
check("PersonaPrompt.Build" in chat,
      "Every request must use the language-specific live persona prompt")
check("EnglishStyleFormat" in persona and "JapaneseStyleFormat" in persona and
      "ChineseStyleFormat" in persona,
      "All three languages need a full style block; English was a single inlined "
      "sentence while Japanese and Chinese had four clauses each")
check("TextVariableResolver.GetPlayerName()" in persona and
      "PlayerNameRule.IsUnsetName(name)" in persona and
      "PlayerNameLine()" in persona,
      "The name from Settings/Me/Your Name must reach the prompt, and the game's "
      "unset placeholder must not be used as a real name")
check('string.Format(StyleFormatFor(voiceLanguage), "Speak")' in persona and
      'string.Format(StyleFormatFor(displayLanguage), "Write")' in persona and
      "SameLanguage(voiceLanguage, displayLanguage)" in persona,
      "Both the spoken and shown sides need the style block for their own language, "
      "and it must not be repeated when the two sides match")
for marker, why in [
    ('\\"spoken\\"', "prompt must define the spoken field"),
    ('\\"shown\\"', "prompt must define the shown field"),
    ("consciousness entity", "core lore: Lilith is the player's consciousness form"),
    ("gave you consciousness", "core lore: player gave Lilith consciousness"),
    ("remember you", "core lore: memory sustains her existence"),
    ("do not biologically need sleep", "core lore: sleep is chosen companionship"),
    ("screen, code", "core lore: the medium can be code while the bond is real"),
    ("stage directions", "no asterisk actions"),
    ("リリス", "third-person self-reference"),
    ("莉莉丝", "Chinese third-person self-reference"),
]:
    check(marker in persona, f"Prompt lost a load-bearing rule - {why}")

check("IsSleep" in dynamic and "IsLieDown" in dynamic and "NightSleepStartTime" in dynamic,
      "Persona context must include live posture and the native bedtime")
check("DateTime.Now" in dynamic, "Persona context must use local system time")
check("MaxConversations" in memory and "MaxInteractions" in memory and "MEMORY.md" in memory,
      "Conversations and interactions must be capped separately, or pats evict talking")
check("RecordLongTerm" in memory and "MaxLongTerm" in memory,
      "Notes must be able to leave a long-term entry")
check("StartsWith(\"[\")" in memory,
      "The legacy single-list memory.json must still load, or existing installs forget")
check("ConversationContext" in memory and "ConversationContext" in chat,
      "Notes must be written from conversations only, not from pats")
check("SearXNG" in live_info and "SmartReader" in live_info and
      "api.open-meteo.com" in live_info and "ip-api.com/json" in live_info and
      "NeedsLiveInformation" in chat,
      "Current information must route through SearXNG, SmartReader, IP-API, and Open-Meteo")
check("InitializeAsync" in chat and "LiveInformationService" in chat,
      "Live information services must warm asynchronously at app startup")
check(not os.path.exists(os.path.join(MOD_DIR, "AnthropicClient.cs")) and
      "CfgAnthropic" not in plugin and "Claude API key" not in plugin,
      "Anthropic and Claude dependencies must be removed")
# Tolerates wrapping and extra arguments: BuildLetter grew a loveLetter
# parameter and wrapped across two lines, which failed a literal-substring check
# while the code was correct. What matters is which language is passed.
check(re.search(r"BuildLetter\(\s*PersonaPrompt\.CurrentDisplayLanguage\(\)", chat) and
      "current game display language" in persona and
      "Use only the current game display language" in chat and
      "RequestTextCompletionAsync" in chat,
      "Letters must use only the current game display language")
check("noteRoll < 0.05f" in chat and "noteRoll < 0.20f" in chat and
      "Range(4, 8)" in chat and "Range(2, 4)" in chat,
      "Note length must use the requested 5/15/80 random distribution")
check("deepseek-v4-flash" in plugin and 'type = "disabled"' in chat,
      "Normal chat must stay on low-latency DeepSeek V4 Flash")
check("ReplyUsesRequestedDisplayLanguage" in chat and "ContainsCjk" in chat and
      "wrong shown language" in chat and "English only" in persona,
      "Japanese spoken output must not leak into the English subtitle field")
check("ReplaceGameVoice" in integrations and "SuppressSubtitle" in game_voice,
      "Built-in game voice must be replaceable without overwriting native subtitles")
check("GateDialogueNode" in integrations and "NativeDialogueQueue" in speech and
      "WaitUntilDisplayed" in speech,
      "Built-in dialogue text must wait until its replacement audio is ready")

# -- 7a. Speech input, voice setup, and desktop integration ------------------
check("ShowPanel();" in chat and "_pendingSpeechCommand" in chat and
      "_speechSubmitAt" in chat,
      "Recognised speech must be visible in the chat bar before automatic submission")
check("Say something···" in chat and "_speechAwaitingReply" in chat,
      "The chat bar must show recognised speech until Lilith's reply is ready")
check("_speechCommandQueue" in chat and "Directory.GetFiles" in chat and
      "time.time_ns()" in ptt and "_replyPlaybackActive" in chat,
      "Back-to-back speech commands must use an ordered, lossless handoff queue")
check("attempt < 3" in chat and "Empty API content on attempt" in chat and
      "requestPayload.Remove(\"response_format\")" in chat,
      "An empty transient completion must use corrective retries instead of dropping a command")
check("ApplyConfiguredHotkey" in chat and '"Open chat"' in settings and
      "TabControls" in settings and "CaptureChatKey" in settings and
      '"Hotkey", "F7"' in plugin,
      "The F7 chat key must be press-to-rebind in the Controls settings")
check(ptt and not os.path.exists(os.path.join(ROOT, "runtime", "wake_listener.py")),
      "runtime/push_to_talk.py must replace runtime/wake_listener.py")
check("openwakeword" not in ptt.lower() and "openwakeword" not in requirements.lower() and
      "wake_listener.py" not in requirements,
      "openWakeWord must be gone from the listener and its dependency list")
check("ARM_SECONDS" not in ptt and "WAKE_RE" not in ptt and
      "playback_lock" not in ptt and "--playback-lock" not in launcher,
      "The wake regex, arm window, and playback lock existed only for an always-open "
      "microphone and must not survive push-to-talk")
check("trigger.exists()" in ptt and "--trigger" in ptt and "--trigger" in launcher,
      "The mod must define the recording window through a trigger file")
check("WindowFocus.IsKeyDown(_vkPushToTalk)" in chat and "StartListening" in chat and
      "StopListening" in chat,
      "The speech key must toggle listening on its rising edge")
# Asserts the mechanism and a sane bound, not the exact tuning value. Pinning the
# literal made retuning the timeout fail a check named for the behaviour.
_silence = re.search(r"^SILENCE_SECONDS = ([\d.]+)", ptt, re.M)
check(_silence is not None and 0.5 <= float(_silence.group(1)) <= 5.0 and
      "silent_for >= silence_limit" in ptt and "energy_threshold" in ptt,
      "An utterance must end after a fixed run of trailing silence")
check("SileroDetector" in ptt and '"--vad"' in ptt and
      "silero-vad" in requirements,
      "Speech detection must default to Silero, which needs no per-machine tuning "
      "- a shipped RMS threshold is wrong for somebody else's microphone and room")
check("FRAME = 512" in ptt,
      "Silero requires exactly 512-sample frames at 16 kHz")
check("EnergyDetector" in ptt and "falling back to energy" in ptt,
      "A machine that cannot load Silero must still work")
check("detector.reset()" in ptt,
      "The recurrent VAD must be reset per utterance or state leaks between them")
check("HALLUCINATIONS" in ptt and "MIN_SPEECH_SECONDS" in ptt and
      "speech_region" in ptt,
      "Near-silent audio must be trimmed and stock caption phrases rejected, or "
      "Whisper hallucinates 'thank you' out of noise")
check("PersonaPrompt.CurrentDisplayLanguage()" in chat and
      "_pushToTalkTriggerPath," in chat and "read_trigger_language" in ptt and
      "SUPPORTED_LANGUAGES" in ptt,
      "Recognition language must follow the game display language through the trigger")
check("awaiting_reset" in ptt,
      "After finalising, the listener must wait for the mod to clear the trigger "
      "instead of starting a second utterance immediately")
check("push-to-talk.active" in chat and "ClearPushToTalkTrigger" in chat and
      "File.WriteAllText(_pushToTalkTriggerPath" in chat,
      "Toggling on must raise the trigger and toggling off must clear it")
check("NoteUserTyping" in chat and "_userTypedWhileListening" in chat and
      "_lastAppliedPartial" in chat,
      "Typing during recognition must latch and stop partials overwriting the field")
check("FocusInputField();" in chat.split("private void StopListening")[0],
      "The field must be focused while listening so it accepts typing")
check("ClearPushToTalkTrigger();" in chat.split("private void Update()")[0],
      "A trigger left by a crash must be cleared at startup, or the listener records "
      "forever")
check("PARTIAL_MARKER" in ptt and "next_partial_at" in ptt and
      "SpeechPartialMarker" in chat and "_inputField.text = partial" in chat,
      "Speech must stream partial recognition into the chat field")
check("if (!_speechListening) return;" in chat,
      "A partial arriving after listening ends must not overwrite the final transcript")
check("CloneActionRow" in settings and "Open Synth" in settings and
      "Vocal Synthesis" in settings and voice_setup,
      "Settings must expose the file-based vocal synthesis setup")
check("voiceFolderRow.gameObject.SetActive(true)" in settings and
      "hotkeyRow.gameObject.SetActive(true)" in settings and
      "pushToTalkRow.gameObject.SetActive(true)" in settings,
      "Cloned rows inherit the hidden state of the row they clone, so every added "
      "row must be activated explicitly or it renders as nothing")
check("SetSettingsInteractive(settingsTyping)" in settings,
      "Settings must only capture the desktop while a text field is focused")
check("EnterKeyboardMode" in window_focus and
      "BeginKeyboardInput" not in window_focus.split("EnterKeyboardMode")[1].split(
          "private static void EnterInteractiveMode")[0],
      "Opening the chat bar must take the keyboard without suspending click-through, "
      "or the whole screen stops passing clicks to the desktop")
check("s_beganKeyboardInput" in window_focus,
      "BeginKeyboardInput/EndKeyboardInput must stay balanced now that only one of "
      "the two entry paths suspends click-through")
check("WindowStyle Hidden" in launcher and "/set_gpt_weights" in launcher and
      "/set_sovits_weights" in launcher and "service-startup.log" in launcher,
      "The hidden launcher must select, warm, and log all voice services")
check("parallel_infer = false" in tts.lower() and
      "parallel_infer = false" in switcher.lower() and
      "parallel_infer = $false" in launcher.lower() and
      '"parallel_infer": false' in precache.lower(),
      "Every GPT-SoVITS request must avoid the pathologically slow ROCm parallel path")
check("ExecuteNativeAction" in chat and "TimerSystem.Instance" in chat and
      "AlarmSystem.SetAlarm" in chat and '"timer_cancel"' in chat and
      '"alarm_cancel"' in chat,
      "LLM timer and alarm intents must route to Lilith's native systems")
check("StartCountdown((float)seconds, false)" in chat and
      "TryApplyImmediateNativeAction" in chat and "TryParseDuration" in chat and
      "TryParseAlarmClock" in chat,
      "Native English timer confirmation must stay muted and cancellation must be immediate")
# Checks the property, not the call's formatting: whatever the folder labels are
# set to, in every language, must break over two lines. A literal-match version of
# this broke the moment the labels became a per-language expression.
def _folder_label_args(field):
    match = re.search(r"SetWrappedLabel\(" + field + r",(.*?)\);", settings, re.S)
    return re.findall(r'"((?:[^"\\]|\\.)*)"', match.group(1)) if match else []

_voice_labels = _folder_label_args("_voiceFolderLabel")
_speech_labels = _folder_label_args("_speechFolderLabel")
check(len(_voice_labels) >= 3 and all("\\n" in v for v in _voice_labels) and
      len(_speech_labels) >= 3 and all("\\n" in v for v in _speech_labels),
      "The folder rows must be labelled over two lines in every language")

# Separate concern, separate check: the native synthesis row is one line and must
# not wrap. Bundling this into the folder-label check above made a rename there
# look like a labelling regression here.
check(re.search(r"_synthesisLabels\[i\]\.enableWordWrapping = false", settings)
      is not None,
      "The vocal synthesis row must not word-wrap")

# Relabelled by this mod with its localiser stripped, so only this mod can
# translate it - the same trap as the cloned rows.
check(re.search(r"ApplySynthesisLabel\(language\)", settings) is not None and
      "音声合成" in settings,
      "The vocal synthesis row must follow the game's UI language")

# The rows this mod adds are clones with their localiser stripped, so nothing but
# this refresh will ever translate them.
# TextVariableResolver, not PersonaPrompt: the latter reports her subtitle
# language, which voice-config.ini pins independently of the game's UI language.
check("RefreshLabels()" in settings and "_labelLanguage" in settings and
      re.search(r"UiLanguage\(\).*?TextVariableResolver\.CurrentLanguage\(\)",
                settings, re.S) is not None,
      "Added settings rows must follow the game's own UI language at runtime")

# Deliberate exception, and easy to 'fix' by mistake later.
check(re.search(r'SetWrappedLabel\(_helpLabel,\s*"<u>Help</u>"\)', settings) is not None,
      "The Help label stays English; it reads as itself in every shipped language")
check("NoteJournal" in chat and "notes.json" in note_journal and
      "MinConversationsPerNote" in plugin and "CooldownHours" in plugin,
      "Note cadence must persist across restarts, or the counter resets every launch")
check("NoteJournal.MarkWritten()" in chat and
      chat.index("NoteJournal.MarkWritten()") > chat.index("_letterQueue.Enqueue(letter)"),
      "The cooldown must start only after a note is actually produced, so a failed "
      "request does not silently cost a keepsake")
check("IsSubstantialExchange" in chat and "http" in chat,
      "A pasted link must not count as a meaningful conversation")
check("nativeActionHandled) return false" in chat and
      "NeedsLiveInformation(user)) return false" in chat and
      "MentionsTimerOrAlarm" in chat,
      "Errands - timers, alarms, weather, web lookups - must not build toward a note")
check("SpeechInputService" in speech_input and "push-to-talk.alive" in speech_input and
      "HEARTBEAT_INTERVAL" in ptt and "RefreshSpeechAvailability" in settings,
      "Push-to-talk must grey out when its listener is not running, or the key opens "
      "a bar that waits forever for a transcript nobody is writing")
check("!SpeechInputService.IsAvailable" in chat,
      "The key must not start listening while the listener is down")
check("SpeechInputService.IsAvailable && HasApiKey" in settings and
      "RefreshChatAvailability" in settings,
      "Push-to-talk needs both the listener and an API key; the chat binding needs "
      "the key, since neither does anything without it")
check("OpenSpeechFolder" in settings and 'MapRow(_speechFolderLabel, TraySettingView.TabMe)' in settings,
      "The speech setup folder must be reachable from the Me tab")
check("QualifyingUtc" in note_journal and "WindowHours" in plugin and
      "Prune(windowHours)" in note_journal,
      "Qualifying conversations must fall inside one window, not accumulate forever")
check("VocalSynthesisPreferred" in plugin and "Task.WhenAny" in voice_monitor and
      "CfgReplaceGameVoice.Value = effective" in voice_monitor and
      "VoiceServiceMonitor.IsAvailable" in settings and "_jp.enabled = available" in settings,
      "Vocal synthesis must grey out while unavailable and restore its saved preference")

# -- 8. Output artifacts ------------------------------------------------------
out_dir = (r"D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith"
           r"\BepInEx\plugins\LilithMod")
check(os.path.exists(os.path.join(out_dir, "LilithMod.dll")),
      "LilithMod.dll not found after build")
check(any("NAudio" in n for n in (os.listdir(out_dir) if os.path.isdir(out_dir) else [])),
      "No NAudio assembly beside the plugin - playback would fail at runtime")
check(os.path.exists(os.path.join(out_dir, "SmartReader.dll")),
      "SmartReader.dll not found beside the plugin")
check(os.path.exists(os.path.join(out_dir, "speech-setup", "README.txt")) and
      "push_to_talk" not in read(MOD_DIR, "voice-setup", "README.txt"),
      "Speech input must be documented in its own folder, not inside the voice "
      "synthesis one that the settings button opens")
check(os.path.exists(os.path.join(out_dir, "voice-setup", "README.txt")) and
      os.path.exists(os.path.join(out_dir, "voice-setup", "voice-config.example.ini")),
      "Voice setup README/config were not deployed")

# -- Invariants inherited from verify-step3.py and verify-voice.py -------------
# Those two scripts were folded into this one. They duplicated the build and the
# NAudio check already here; what follows is the coverage that was theirs alone.
check("GetAsyncKeyState" in chat or "GetAsyncKeyState" in window_focus,
      "Hotkeys must poll Win32 directly - this game's window delivers no Unity "
      "keyboard input, so Input.GetKeyDown silently never fires")
check("EnableTyping" in window_focus and "RestoreWindow" in window_focus,
      "Typing needs the focus toggle, and the window style must be restored after")
check("chat/completions" in chat,
      "The chat endpoint path is missing")
check("textComponent" in chat and "textViewport" in chat,
      "A cloned TMP input field needs textComponent and textViewport wired by hand "
      "or it renders nothing and swallows input")
check("ref_audio_path" in tts and "text_lang" in tts and "fragment_interval" in tts,
      "The TTS request must carry the reference audio, language and fragment interval")
check("WaveOutEvent" in voice_player and "WaveFileReader" in voice_player and
      "PlaybackStopped" in voice_player,
      "Playback must read the wav, play it, and signal completion")
check("WarmUpSentences" in speech,
      "Warm-up sentences are gone - first synthesis would take the cold-start hit, "
      "and NoteServiceAnswered would never fire early")

# -- An interaction reply waits for her to stop talking -----------------------
# _currentRequest only covers the API call, which finishes seconds before the
# audio does. Without a playback gate the interaction reply fires mid-sentence
# and CancelCurrent drops the rest of the previous one.
check("SpeechStillFinishing" in chat and "InteractionAfterSpeechSeconds" in chat,
      "An interaction reply must wait for playback to finish, then a beat, rather "
      "than cutting off the reply already being spoken")
check("_pendingUserMessage" in chat and "TrySendQueuedUserMessage" in chat,
      "A typed message must queue behind her current reply instead of cancelling it")
check("QueuedMessageMaxWaitSeconds" in chat,
      "A queued message needs a ceiling, or a stuck playback flag swallows it forever")
# All three routes that can start a reply - touch, typing, ambient - must wait
# on the same predicate. Any one of them missing it cancels her mid-sentence.
check(len(re.findall(r"SpeechStillFinishing", chat)) >= 4,
      "Every path that starts a reply must wait on SpeechStillFinishing, including "
      "the ambient remark")
check("if (!ambient) ScheduleNextAmbient();" in chat,
      "Talking to her must reset the idle clock, or a held ambient remark lands "
      "seconds after she finishes answering")
check("_speechEndedAt = Time.unscaledTime;" in chat,
      "The end of playback must be recorded, or the post-speech delay has no anchor")

# -- The distribution build must not replace dialogue it cannot translate -----
# Release builds omit the game's script for licensing. Without this the
# replacement path still ran, fell back to the Chinese source string and fed it
# to the Japanese voice - broken for every stranger, and invisible here, because
# a local build always has the catalogue.
catalog = read(MOD_DIR, "DialogueTextCatalog.cs")
check("internal static bool Available" in catalog,
      "DialogueTextCatalog must expose whether a catalogue exists at all")
check("DialogueTextCatalog.Available" in integrations,
      "Native voice replacement must be off entirely when there is no catalogue, so "
      "the bubble gate and the audio prefixes turn off together")
check("DialogueTextCatalog.Available" in game_voice,
      "A line must not be held for synthesis that cannot be translated")
# node.text is the game's own string: Chinese for scripted lines, the UI
# language for ones built at runtime with lineId 0. Falling back to it fed
# English to the Japanese voice and she read it aloud.
check("text = node.text;" not in game_voice,
      "Native dialogue must never fall back to node.text for synthesis; without a "
      "catalogue entry the original voice has to be kept instead")
# Declining to replace a line and suppressing its audio anyway leaves it silent.
# The bubble gate and the audio prefixes have to agree, as they must for the
# grace window.
check("NativeAudioAllowed" in game_voice and "NativeAudioAllowed" in integrations,
      "A line handed back unreplaced must keep its own audio, or it plays silently")
# Cached audio needs no service: the language switch is a no-op when the weights
# already match and the rest is a file read. Gating replacement on the service
# alone left 1800 cached lines unusable while it started, which is what made the
# first line of a session play in the game's own Chinese.
check("CacheReplacementPossible" in game_voice and "IsCached" in tts and
      "IsCached" in speech and "cachedOnly" in game_voice,
      "A line whose audio is already cached must be replaceable while synthesis "
      "is still starting, not held hostage to the service handshake")
check("LanguageIsCurrent" in switcher and "LanguageIsCurrent" in tts,
      "A cache hit must confirm the running weights match the language, or she "
      "speaks cached audio in the wrong voice")
# The bubble gate and the four audio prefixes have to agree, and in the cached
# case the flag they normally read is false - so the decision is recorded instead.
check("NativeAudioSuppressed" in game_voice and "NativeAudioSuppressed" in integrations,
      "A cached replacement must suppress the game's own audio, or both play at once")
# Runtime-built lines carry no id, so the catalogue cannot reach them. Their
# Japanese is fetched once and kept, never awaited on the dialogue path.
dynamic_cache = read(MOD_DIR, "DynamicLineCache.cs")
check(dynamic_cache and "TranslateLineToJapaneseAsync" in chat,
      "Runtime-built dialogue needs a Japanese translation path, or it is silent")
check("DynamicLineCache.TryGet" in game_voice and "RequestTranslation" in game_voice,
      "The dialogue path must use the cache and request a fill on a miss")
check("InFlight" in dynamic_cache,
      "A repeating line must not queue one translation request per occurrence")
# A held native cue that is dropped rather than routed leaks the coordinator's
# pending entry ("N still held" forever) and leaves that bubble suppressed with
# no re-show - the long-standing stuck-bubble fault.
native_cue = read(MOD_DIR, "NativeDialogueCue.cs")
check("Cancelled" in native_cue and "Cancel()" in native_cue,
      "A native cue must be cancellable, so a superseded line is neither re-shown "
      "nor voiced while still clearing its pending entry")
check("AbandonNativeCue" in speech,
      "Every path that drops an utterance must route its native cue back, or the "
      "bubble stays suppressed for the rest of the session")
check("_latestNodeForBubble" in game_voice,
      "A held line superseded before its audio arrived must not be re-shown over "
      "the newer one")

# -- A long reply is split before it is queued --------------------------------
# Synthesis returns one WAV for whatever text it is handed, so an unsplit long
# line means the player hears nothing until the entire reply has been generated.
chunker = read(MOD_DIR, "UtteranceChunker.cs")
check(chunker, "UtteranceChunker.cs is missing; long replies would be one silence")
check("UtteranceChunker.Chunk" in chat,
      "The reply path must chunk before enqueueing, or splitting never happens")
check(chat.count("UtteranceChunker.Chunk") >= 2,
      "The plain-text reply path needs chunking too - it carries the same long lines")
check("NativeDialogue != null" in chunker,
      "A native game line must never be chunked: its cue is a one-per-line "
      "handshake and splitting it would leak the coordinator's pending entry")
check("EndOfReply = i == queued.Count - 1" in chat,
      "EndOfReply must move to the LAST chunk, or the reply is reported finished "
      "while pieces are still queued")
check("CjkWeight" in chunker,
      "Thresholds must be weighted by script: a raw Length test splits English "
      "far too eagerly and Japanese almost never")
check("SuppressSubtitle" in chunker,
      "When the shown text will not split, later chunks must run silent rather "
      "than repeat the whole subtitle under every piece")
# Grouping unequal sentence counts proportionally kept the pieces in order but
# could still put a subtitle over the audio for the next one - reported as the
# text box and the voice not matching.
check("shown.Count == spoken.Count" in chunker,
      "The bubble may only take turns when spoken and shown split into the SAME "
      "number of sentences; anything else pairs text with the wrong audio")
check("Put each sentence in its own object" in persona,
      "One sentence per object is what makes the split exact; splitting a "
      "multi-sentence object afterwards can only guess where the shown text divides")
check("A hint is not permission to tell the whole thing" in persona,
      "A hinted memory must draw an allusion, not the whole anecdote")
# "The theme park is closed" was true of that one night and got answered as
# though it described theme parks now.
check("describes one past occasion, not how the world is now" in persona,
      "A memory's details must not be carried into a present-tense mention of the "
      "same subject")
# Known but never volunteered - a stricter tier than the other memories, which
# are allowed the allusion this one forbids.
check("This one is an exception to everything above" in persona,
      "The band's hidden layer must be exempt from the allusion rule, or the "
      "restraint above licences the hinting it is supposed to forbid")

# -- The weather feature discloses what it contacts ---------------------------
# Asking about the weather sends the player's IP to a third party. That is a
# reasonable default but not an obvious one, so every language's help must say
# so, and the config escape hatch must exist to make the disclosure actionable.
for help_file in ("OVERVIEW.txt", "OVERVIEW.ja.txt", "OVERVIEW.zh.txt"):
    body = read(MOD_DIR, "help", help_file)
    check(body and "ip-api.com" in body and "open-meteo.com" in body,
          f"help/{help_file} does not disclose the weather lookup's third parties")
    check(body and "[Weather]" in body,
          f"help/{help_file} does not show how to pin a location instead")
check("CfgWeatherLatitude" in plugin and "CfgWeatherLongitude" in plugin,
      "The weather location override must exist, or the help files describe a setting "
      "that does nothing")
check("skipping the IP lookup" in live_info,
      "Configured coordinates must skip the IP lookup, not just override its result")

# -- Result -------------------------------------------------------------------
if failures:
    print("VERIFY FAIL")
    for f in failures:
        print("  -", f)
    sys.exit(1)

print("[verify-bilingual] All checks passed")
print("VERIFY PASS")
