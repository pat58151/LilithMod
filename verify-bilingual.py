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
# Build outside the live plugin folder.
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

# Exercise memory behavior in an isolated output folder.
memory_harness = os.path.join(ROOT, "tests", "MemoryStoreHarness", "MemoryStoreHarness.csproj")
r = subprocess.run(
    [DOTNET, "run", "--project", memory_harness, "-c", "Release"],
    capture_output=True, text=True, cwd=ROOT,
)
if r.returncode != 0 or "MEMORY HARNESS PASS" not in r.stdout:
    print("VERIFY FAIL - memory harness failed")
    print((r.stdout + r.stderr).strip())
    sys.exit(1)
print("[verify-bilingual] Memory behavior passed")


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
foreground = read(MOD_DIR, "ForegroundActivity.cs")
memory = read(MOD_DIR, "MemoryStore.cs")
memory_vectorizer = read(MOD_DIR, "MemoryVectorizer.cs")
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
installer = read(ROOT, "installer", "LilithMod.iss")

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

# -- 4. Synthesis and playback overlap ----------------------------------------
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
# Bubble and audio routes must share one gate.
check("HoldingForSynthesis" in game_voice and "EverAvailable" in game_voice,
      "The synthesis grace window must be one predicate in GameVoiceCoordinator")
# Voice availability must be known before the first-frame greeting.
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
check("ForegroundActivity.Context" in dynamic and "ForegroundActivity.Poll" in chat and
      "CfgForegroundAwareness" in plugin,
      "Transient foreground awareness must be polled and added to dynamic context")
check("appmanifest_*.acf" in foreground and "libraryfolders.vdf" in foreground and
      "DiscordCanary" in foreground,
      "Foreground awareness must resolve local Steam games and Discord variants")
check("Path.GetFileName(executable)" in foreground and '"app:" + executableName' in foreground,
      "Unknown foreground applications must fall back to their executable filename")
check('processName.Equals("Code"' in foreground and "Visual Studio Code" in foreground,
      "VS Code must be reported by its friendly name instead of Code.exe")
check("GetWindowText" not in foreground and "MainWindowTitle" not in foreground,
      "Foreground awareness must never inspect sensitive window, channel, or document titles")
check("ModInputActive" in foreground and "__keep_previous__" in foreground and
      "ModInputActive" in window_focus,
      "Opening Lilith's own input must preserve the previously focused game or Discord")
check("MaxConversations" in memory and "MaxInteractions" in memory and "MEMORY.md" in memory,
      "Conversations and interactions must be capped separately, or pats evict talking")
check("RecordLongTerm" in memory and "MaxLongTerm" in memory,
      "Notes must be able to leave a long-term entry")
check("public static int Forget(" in memory and "ForgetFact" in memory and
      "CorrectFact" in memory and "ForgetAll" in memory and "PurgeBackups" in memory,
      "Memory must support correction and forgetting without stale backups")
check('case "forget_memory"' in chat and 'case "forget_fact"' in chat and
      'case "forget_all_memory"' in chat and 'case "update_memory"' in chat and
      "RemoveCurrentMemoryCommand" in chat,
      "Conversational memory actions must not re-save forget commands")
check("StartsWith(\"[\")" in memory,
      "The legacy single-list memory.json must still load, or existing installs forget")
check("QualifyingConversationContext" in memory and "QualifyingConversationContext" in chat,
      "Notes must be written from conversations only, not from pats")
check("conversationsAlreadyInHistory" in memory and "completedHistoryTurns" in chat,
      "Persisted conversations already present in session history must be excluded")
check("QualifyingConversationContext" in memory and "ClearQualifyingConversations" in chat,
      "Notes must use and then clear their own qualifying conversation buffer")
check("RelevantEpisodes" in memory and "MaxRelevantLongTerm" in memory,
      "Long-term memory must be selected for relevance instead of sent wholesale")
check("RecordEpisode" in memory and "RecordSemanticFacts" in memory and
      "SourceConversationIds" in memory,
      "Durable memory must separate sourced episodes from stable semantic facts")
check("MemoryVectorizer.Similarity" in memory and "NormalizationForm.FormKC" in memory_vectorizer,
      "Durable recall must use the local multilingual feature embedding")
check("TokenConcepts" in memory_vectorizer and 'AddConcept("work"' in memory_vectorizer and
      'AddConcept("anxiety"' in memory_vectorizer and 'AddConcept("relationship"' in memory_vectorizer,
      "Local vectors must expand curated personal-topic and emotion synonyms")
check("Importance" in memory and "EmotionalWeight" in memory and "RecallCount" in memory,
      "Episode ranking must include significance and recall metadata")
check("RecordDurableMemoryAsync" in chat and 'root["episode"]' in chat and
      'root["facts"]' in chat,
      "Durable consolidation must extract a structured episode and stable facts")
check("CfgEpisodicMemoryInterval" in plugin and "DurableMemorySnapshot" in chat and
      "MarkDurableConsolidated" in chat,
      "Episodic consolidation must run independently of rare notes")
check('"WindowHours", 12.0' in plugin and '"SessionGapHours", 2.0' in plugin and
      "sessionGapHours" in memory,
      "Episodic memory must use its own 12-hour window and split sessions after two hours")
check("File.Replace" in memory and '".bak"' in memory,
      "Memory saves must be atomic and recoverable from a backup")
check("MEMORY.md.bak" in installer and "memory.json.bak" in installer,
      "Permanent uninstall must remove readable mirrors and recovery copies")
check("SearXNG" in live_info and "SmartReader" in live_info and
      "api.open-meteo.com" in live_info and "ip-api.com/json" in live_info and
      "NeedsLiveInformation" in chat,
      "Current information must route through SearXNG, SmartReader, IP-API, and Open-Meteo")
check("InitializeAsync" in chat and "LiveInformationService" in chat,
      "Live information services must warm asynchronously at app startup")
check(not os.path.exists(os.path.join(MOD_DIR, "AnthropicClient.cs")) and
      "CfgAnthropic" not in plugin and "Claude API key" not in plugin,
      "Anthropic and Claude dependencies must be removed")
# Check the language argument without depending on formatting.
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
# Check bounded playback without pinning its tuning value.
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
# Check wrapped folder labels without depending on call formatting.
def _folder_label_args(field):
    match = re.search(r"SetWrappedLabel\(" + field + r",(.*?)\);", settings, re.S)
    return re.findall(r'"((?:[^"\\]|\\.)*)"', match.group(1)) if match else []

_voice_labels = _folder_label_args("_voiceFolderLabel")
_speech_labels = _folder_label_args("_speechFolderLabel")
check(len(_voice_labels) >= 3 and all("\\n" in v for v in _voice_labels) and
      len(_speech_labels) >= 3 and all("\\n" in v for v in _speech_labels),
      "The folder rows must be labelled over two lines in every language")

# The native synthesis row remains single-line.
check(re.search(r"_synthesisLabels\[i\]\.enableWordWrapping = false", settings)
      is not None,
      "The vocal synthesis row must not word-wrap")

# The mod owns localization after replacing the native label.
check(re.search(r"ApplySynthesisLabel\(language\)", settings) is not None and
      "音声合成" in settings,
      "The vocal synthesis row must follow the game's UI language")

# Cloned rows use the game UI language, not the configured subtitle language.
check("RefreshLabels()" in settings and "_labelLanguage" in settings and
      re.search(r"UiLanguage\(\).*?TextVariableResolver\.CurrentLanguage\(\)",
                settings, re.S) is not None,
      "Added settings rows must follow the game's own UI language at runtime")

# Help intentionally stays in English.
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

# -- Voice and interaction invariants -----------------------------------------
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

# -- Interaction replies wait for playback -----------------------------------
check("SpeechStillFinishing" in chat and "InteractionAfterSpeechSeconds" in chat,
      "An interaction reply must wait for playback to finish, then a beat, rather "
      "than cutting off the reply already being spoken")
check("_pendingUserMessage" in chat and "TrySendQueuedUserMessage" in chat,
      "A typed message must queue behind her current reply instead of cancelling it")
check("QueuedMessageMaxWaitSeconds" in chat,
      "A queued message needs a ceiling, or a stuck playback flag swallows it forever")
# Touch, typed, and ambient replies share the playback gate.
check(len(re.findall(r"SpeechStillFinishing", chat)) >= 4,
      "Every path that starts a reply must wait on SpeechStillFinishing, including "
      "the ambient remark")
check("if (!ambient) ScheduleNextAmbient();" in chat,
      "Talking to her must reset the idle clock, or a held ambient remark lands "
      "seconds after she finishes answering")
check("_speechEndedAt = Time.unscaledTime;" in chat,
      "The end of playback must be recorded, or the post-speech delay has no anchor")

# -- Distribution builds require translated dialogue -------------------------
catalog = read(MOD_DIR, "DialogueTextCatalog.cs")
check("internal static bool Available" in catalog,
      "DialogueTextCatalog must expose whether a catalogue exists at all")
check("DialogueTextCatalog.Available" in integrations,
      "Native voice replacement must be off entirely when there is no catalogue, so "
      "the bubble gate and the audio prefixes turn off together")
check("DialogueTextCatalog.Available" in game_voice,
      "A line must not be held for synthesis that cannot be translated")
# Never send untranslated node text to Japanese synthesis.
check("text = node.text;" not in game_voice,
      "Native dialogue must never fall back to node.text for synthesis; without a "
      "catalogue entry the original voice has to be kept instead")
# Declined replacement must preserve native audio.
check("NativeAudioAllowed" in game_voice and "NativeAudioAllowed" in integrations,
      "A line handed back unreplaced must keep its own audio, or it plays silently")
# A cache is an implementation detail, not an offline voice mode. When the
# synthesis service is unavailable the effective setting is native Chinese.
check("CacheReplacementPossible" not in game_voice and "cachedOnly" not in game_voice,
      "Cached synthesized voice must not play while synthesis is unavailable; fallback is "
      "native Chinese only")
check("VoiceServiceMonitor.IsAvailable" in integrations,
      "Game-voice replacement must require a currently available synthesis service")
check(integrations.count(
          "if (!VoiceReplacementEnabled() && !GameVoiceCoordinator.HoldingForSynthesis)") >= 2,
      "Effective native mode must override stale suppression in both dialogue and "
      "animation audio paths")
check(re.search(
          r"!_allowOriginalShow\s*&&\s*!replacing(?:(?!if \(_allowOriginalShow).)*"
          r"AllowNativeAudioForThisLine\(\);",
          game_voice, re.S),
      "A native fallback line must clear stale replacement suppression, or its "
      "Chinese subtitle appears without voice")
check("AvailabilityKnown" in voice_monitor and "AvailabilityKnown" in game_voice,
      "A failed startup probe must end the grace window and release native Chinese")
check("StopSynthPlaybackForNativeVoice" in voice_monitor,
      "Losing the synthesis service must cancel synthesized audio already queued")
check("PublishGameVoice" in voice_monitor and "ChineseSimplified" in voice_monitor,
      "Offline fallback must route the game's runtime voice database to Chinese, or "
      "idle and interaction lines with localized lookups are silent")
check("LanguageIsCurrent" in switcher and "LanguageIsCurrent" in tts,
      "A cache hit must confirm the running weights match the language, or she "
      "speaks cached audio in the wrong voice")
# Bubble and native audio callbacks share the cached-replacement decision.
check("NativeAudioSuppressed" in game_voice and "NativeAudioSuppressed" in integrations,
      "A cached replacement must suppress the game's own audio, or both play at once")
check("PlayActionSEOrVoice" in integrations and "ReplaceActionVoice" in integrations,
      "The dialogue action-voice fallback must share replacement suppression, or "
      "farewell lines play their original Chinese voice under cached synthesized voice")
check("PlayAnimationAudio" in integrations and "ReplaceAnimationAudio" in integrations,
      "Sleep and farewell animation audio must be suppressed during replacement, or "
      "their bundled Chinese voice bypasses every dialogue-audio gate")
check(game_voice.count("AudioManager.StopVoice();") >= 2,
      "A held line must stop native voice immediately around its re-show before the "
      "external vocal synthesis player starts")
check(re.search(
          r"_allowOriginalShow = true;(?:(?!cue\.Bubble\.ShowNode).)*"
          r"SuppressNativeAudioForThisLine\(\);(?:(?!cue\.Bubble\.ShowNode).)*"
          r"cue\.Bubble\.ShowNode",
          game_voice, re.S),
      "Native-audio suppression must be refreshed immediately before a held line is "
      "re-shown, or a stale decision lets Chinese overlap the cached replacement")
check("SynthVoiceSelected" in chat and chat.count("SynthVoiceSelected") >= 3,
      "Generated replies must follow the selected voice mode, not merely an installed synthesizer")
check("StopSynthPlaybackForNativeVoice" in settings and "CancelAll(true)" in chat and
      "public void Stop()" in voice_player,
      "Selecting native Chinese must stop chat and game synth audio already queued or playing")
check("PlaybackActive" in speech and "PlaybackActive" in game_voice and
      "SuppressNativeAudioForThisLine();" in game_voice,
      "Native Chinese audio must remain suppressed for the full active synth clip")
check("_nativeQueue" in speech and "ProcessNativeLoop" in speech and
      "LilithVoice.NativeProcessor" in speech,
      "Game dialogue needs a priority worker, or slow ambient synthesis blocks normal lines "
      "and Goodbye until after exit")
check("_playbackGate" in voice_player and "PlaySyncExclusive" in voice_player,
      "Priority and chat workers must serialize the shared audio output")
# Runtime dialogue translation is cached asynchronously.
dynamic_cache = read(MOD_DIR, "DynamicLineCache.cs")
check(dynamic_cache and "TranslateLineToJapaneseAsync" in chat,
      "Runtime-built dialogue needs a Japanese translation path, or it is silent")
check("DynamicLineCache.TryGet" in game_voice and "RequestTranslation" in game_voice,
      "The dialogue path must use the cache and request a fill on a miss")
check("InFlight" in dynamic_cache,
      "A repeating line must not queue one translation request per occurrence")
# Cancelled native cues must return to the coordinator.
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

# -- Long replies are split before synthesis ---------------------------------
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
# Unequal bilingual sentence counts must not misalign subtitles.
check("shown.Count == spoken.Count" in chunker,
      "The bubble may only take turns when spoken and shown split into the SAME "
      "number of sentences; anything else pairs text with the wrong audio")
check("Put each sentence in its own object" in persona,
      "One sentence per object is what makes the split exact; splitting a "
      "multi-sentence object afterwards can only guess where the shown text divides")
check("A hint is not permission to tell the whole thing" in persona,
      "A hinted memory must draw an allusion, not the whole anecdote")
# Episodic events must not become permanent facts.
check("describes one past occasion, not how the world is now" in persona,
      "A memory's details must not be carried into a present-tense mention of the "
      "same subject")
# Sensitive memories are never volunteered.
check("This one is an exception to everything above" in persona,
      "The band's hidden layer must be exempt from the allusion rule, or the "
      "restraint above licences the hinting it is supposed to forbid")

# -- Weather privacy disclosure ----------------------------------------------
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
