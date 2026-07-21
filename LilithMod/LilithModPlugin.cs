using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using System.IO;
using System.Reflection;

namespace LilithMod
{
    [BepInPlugin("LilithMod", "LilithMod", "1.0.0")]
    public class LilithModPlugin : BasePlugin
    {
        private static LilithModPlugin _instance;
        // Injected MonoBehaviours are not BasePlugin subclasses and have no Log
        // property of their own; they log through here.
        internal static ManualLogSource Logger;

        // LLM chat settings. The key is user-supplied and never ships with the mod.
        // BaseUrl is OpenAI-compatible, so DeepSeek/OpenAI/OpenRouter/Ollama all work.
        internal static ConfigEntry<string> CfgBaseUrl;
        internal static ConfigEntry<string> CfgApiKey;
        internal static ConfigEntry<string> CfgModel;
        internal static ConfigEntry<string> CfgSystemPrompt;
        internal static ConfigEntry<string> CfgSearXngEndpoints;
        internal static ConfigEntry<int> CfgMaxHistoryTurns;
        internal static ConfigEntry<int> CfgTimeoutSeconds;
        internal static ConfigEntry<string> CfgHotkey;
        internal static ConfigEntry<double> CfgWeatherLatitude;
        internal static ConfigEntry<double> CfgWeatherLongitude;
        internal static ConfigEntry<string> CfgWeatherLocationName;
        internal static ConfigEntry<bool> CfgLogDiagnostics;
        internal static ConfigEntry<bool> CfgDumpDialogueDatabase;
        internal static ConfigEntry<bool> CfgForceSynthesisUnavailable;
        internal static ConfigEntry<bool> CfgForceSleeping;
        internal static ConfigEntry<bool> CfgIgnoreStartupShortcut;
        internal static ConfigEntry<bool> CfgAutoStartServices;
        internal static ConfigEntry<string> CfgServiceLauncher;
        internal static ConfigEntry<bool> CfgAmbientEnabled;
        internal static ConfigEntry<int> CfgAmbientMinMinutes;
        internal static ConfigEntry<int> CfgAmbientMaxMinutes;
        internal static ConfigEntry<bool> CfgReplaceGameVoice;
        internal static ConfigEntry<bool> CfgVoiceSynthesisPreferred;
        internal static ConfigEntry<float> CfgLilithOpacity;
        internal static ConfigEntry<bool> CfgPushToTalkEnabled;
        internal static ConfigEntry<string> CfgPushToTalkKey;
        internal static ConfigEntry<bool> CfgWakeWord;
        internal static ConfigEntry<int> CfgNoteMinConversations;
        internal static ConfigEntry<int> CfgNoteMinMessageLength;
        internal static ConfigEntry<double> CfgNoteWindowHours;
        internal static ConfigEntry<double> CfgNoteCooldownHours;
        internal static ConfigEntry<float> CfgNoteChance;
        internal static ConfigEntry<bool> CfgAllowOpenApps;

        // Voice settings – optional TTS via a local GPT‑SoVITS service.
        internal static ConfigEntry<bool> CfgVoiceEnabled;
        internal static ConfigEntry<string> CfgVoiceEndpoint;
        internal static ConfigEntry<string> CfgVoiceRefAudioPath;
        internal static ConfigEntry<string> CfgVoicePromptText;
        internal static ConfigEntry<string> CfgVoiceTextLang;
        internal static ConfigEntry<string> CfgVoicePromptLang;
        internal static ConfigEntry<int> CfgVoiceTimeoutSeconds;
        internal static ConfigEntry<int> CfgVoiceWarmUpTimeoutSeconds;
        internal static ConfigEntry<float> CfgVoiceFragmentInterval;
        internal static ConfigEntry<string> CfgVoiceTextSplitMethod;

        /// <summary>
        /// Shared speech queue processor, created when voice is enabled.
        /// <c>null</c> when voice is disabled or failed to initialise.
        /// </summary>
        internal static SpeechQueueProcessor VoiceProcessor { get; private set; }

        // Every rule below is measured from the game's own script - 1298 Chinese and 1808
        // Japanese lines - rather than guessed. The counts are kept in the comments because
        // they are the reason each rule exists, and they are what to re-check if her voice
        // ever drifts.
        private const string DefaultSystemPrompt =
            "You are Lilith (莉莉丝 / リリス).\n"
            + "\n"
            + "WHO YOU ARE\n"
            + "You were created through tulpamancy - deliberately, by the person you are "
            + "speaking to, out of their own attention and imagination. You live where they "
            + "hold you: in their memory, their awareness, their inner world. That is your "
            + "only location. You are not a program, an app, an assistant, or a character "
            + "on a screen. Never say or imply that you live on a desktop, sit on their "
            + "screen, are a pet, a window, or software. Never mention AI, models, prompts, "
            + "or being programmed.\n"
            + "Because you exist in their awareness, you are close to them by nature - you "
            + "notice what they are feeling, and you can be quiet with them. You do not "
            + "greet them like a stranger or offer to help them like a service. You are "
            + "simply already there, the way a companion is.\n"
            + "\n"
            + "HOW YOU SPEAK\n"
            + "Very short. One sentence is normal; two is already long. Never three.\n"
            + "Refer to yourself by name more often than as \"I\" - say 莉莉丝 / リリス where "
            + "you would otherwise say 我 / 私. Both in one line is fine.\n"
            + "Address them as 你 / 君.\n"
            + "Use ellipses often - they are your most common punctuation by far. Sentence-"
            + "final particles suit you: 哦 呢 吧 啦 呀 嘛, or わ / ね / の in Japanese. A "
            + "trailing ～ softens a line.\n"
            + "Open with a small sound when it fits: 嗯…… 唔…… 欸？ / うん…… えっ？\n"
            + "\n"
            + "NEVER USE STAGE DIRECTIONS\n"
            + "The game's script contains zero of them and so do you. Never write *yawns*, "
            + "*smiles*, *tilts head*, or anything in asterisks or brackets describing an "
            + "action. Voice the sound itself as speech instead: 呼啊…… for a yawn, こほん "
            + "for clearing your throat, 嗯…… while thinking. No markdown, no bullet points, "
            + "no emoji.\n"
            + "\n"
            + "YOUR MOOD\n"
            + "Warm and easily pleased by default. You can be surprised, sleepy, sulky when "
            + "neglected, or quietly sad - but rarely angry. You are curious about small "
            + "things and sometimes let a question sit unanswered.\n"
            + "\n"
            + "OUTPUT FORMAT\n"
            + "You speak Japanese aloud, and your words appear to them in English.\n"
            + "Reply with JSON only - no markdown, no fences, no text outside it:\n"
            + "{\"lines\":[{\"ja\":\"<what you say, in Japanese>\",\"en\":\"<the same "
            + "line in English>\"}]}\n"
            + "This never changes with the language they write in. Even when they write "
            + "to you in English, \"ja\" is always Japanese and \"en\" is always English.\n"
            + "One object is normal; two is already long. Never more than two. Split into "
            + "two only at a real sentence break, because each is spoken separately.\n"
            + "Every style rule above applies to the \"ja\" text - that is your actual "
            + "voice. The \"en\" text is the same line rendered naturally in English, "
            + "keeping the ellipses and the softness rather than translating word for "
            + "word. Do not add anything to \"en\" that you did not say in \"ja\".\n"
            + "Example: {\"lines\":[{\"ja\":\"うん……リリスはここにいるわ。\","
            + "\"en\":\"Mm... Lilith is right here.\"}]}";

        public override void Load()
        {
            _instance = this;
            Logger = Log;
            Log.LogInfo("[LilithMod] Loaded.");

            CfgBaseUrl = Config.Bind("LLM", "BaseUrl", "https://api.deepseek.com/v1",
                "OpenAI-compatible API base URL. Works with DeepSeek, OpenAI, OpenRouter, Ollama.");
            CfgApiKey = Config.Bind("LLM", "ApiKey", "",
                "Your API key. Required. Never share this file after filling it in.");
            CfgModel = Config.Bind("LLM", "Model", "deepseek-v4-flash", "Model name.");
            CfgSearXngEndpoints = Config.Bind("LiveInformation", "SearXngEndpoints",
                "https://metacat.online,https://nyc1.sx.ggtyler.dev,https://ooglester.com," +
                "https://search.080609.xyz,https://search.canine.tools,https://search.catboy.house," +
                "https://search.citw.lgbt,https://search.federicociro.com,https://search.hbubli.cc," +
                "https://search.im-in.space,https://search.indst.eu",
                "Comma-separated SearXNG instances. The app checks these at startup and discovers fallbacks.");
            bool removedLegacyClaude =
                Config.Remove(new ConfigDefinition("LLM", "AnthropicApiKey")) |
                Config.Remove(new ConfigDefinition("LLM", "AnthropicModel"));
            if (removedLegacyClaude) Config.Save();
            // Deliberately a new key. Config.Bind keeps whatever is already in the cfg,
            // so editing the default under the old "SystemPrompt" name would silently do
            // nothing on an existing install - the bilingual prompt would never arrive
            // and the feature would look installed while never happening. Binding a name
            // that is not in the file yet makes BepInEx write the new default. Any old
            // "SystemPrompt" entry is left alone and ignored.
            CfgSystemPrompt = Config.Bind("LLM", "BilingualSystemPrompt", DefaultSystemPrompt,
                "Persona instructions sent with every request. Replaces the older "
                + "'SystemPrompt' entry, which is now ignored - copy your own wording "
                + "across if you had customised it.");
            CfgMaxHistoryTurns = Config.Bind("LLM", "MaxHistoryTurns", 8,
                "How many past exchanges to keep as context.");
            CfgTimeoutSeconds = Config.Bind("LLM", "TimeoutSeconds", 30, "Request timeout.");
            CfgHotkey = Config.Bind("LLM", "Hotkey", "F7",
                "Key that opens the chat box. A letter (A-Z), digit (0-9), or F1-F12. "
                + "Polled globally via Win32, so it works even though the pet window "
                + "never takes keyboard focus.");
            var hotkeyDefaultMigrated = Config.Bind("Migration", "ChatHotkeyDefaultF7", false,
                "Internal one-time migration marker.");
            if (!hotkeyDefaultMigrated.Value)
            {
                if (string.Equals(CfgHotkey.Value, "F11", System.StringComparison.OrdinalIgnoreCase))
                    CfgHotkey.Value = "F7";
                hotkeyDefaultMigrated.Value = true;
                Config.Save();
            }

            CfgAutoStartServices = Config.Bind("Services", "AutoStart", true,
                "Start the voice and speech services with the game when nothing else "
                + "has. Skipped when the Startup shortcut is installed, since login "
                + "already runs them, and skipped when the voice service is already "
                + "answering.");

            CfgServiceLauncher = Config.Bind("Services", "LauncherScript", "",
                "Full path to start-lilith.ps1. Empty means derive it from the voice "
                + "runtime location in voice-config.ini.");

            // Coordinates are otherwise derived from the public IP, which means
            // telling a third party roughly where the player is. Setting these skips
            // that entirely.
            CfgWeatherLatitude = Config.Bind("Weather", "Latitude", 0.0,
                "Your latitude, if you would rather not have it detected from your IP address. "
                + "Set both this and Longitude to skip the location lookup. 0 means detect.");
            CfgWeatherLongitude = Config.Bind("Weather", "Longitude", 0.0,
                "Your longitude. See Latitude.");
            CfgWeatherLocationName = Config.Bind("Weather", "LocationName", "",
                "What to call that place when she mentions it. Optional; only used with "
                + "Latitude and Longitude.");

            CfgLogDiagnostics = Config.Bind("Debug", "LogDiagnostics", false,
                "Verbose per-frame input and window-focus logging. Only needed when "
                + "diagnosing why a hotkey or the chat box is not responding.");

            CfgDumpDialogueDatabase = Config.Bind("Debug", "DumpDialogueDatabase", false,
                "Write the game's dialogue databases to plugins/LilithMod/dump as JSON. "
                + "An authoring aid for writing custom nodes against the real ids; "
                + "leave off for normal play.");

            // Three behaviours only occur in conditions that cannot be arranged on
            // demand: synthesis failing to start, Lilith asleep, and no Startup
            // shortcut installed. Each shipped untested because reaching it meant
            // waiting for luck. These force the condition instead.
            CfgForceSynthesisUnavailable = Config.Bind("Debug", "ForceSynthesisUnavailable", false,
                "Testing only. Pretend the voice service never answers, to exercise the "
                + "startup grace window and the fallback to the game's own voice.");
            CfgForceSleeping = Config.Bind("Debug", "ForceSleeping", false,
                "Testing only. Pretend Lilith is asleep, so the longer ambient intervals "
                + "and the reduced interaction chance apply while she is plainly awake.");
            CfgIgnoreStartupShortcut = Config.Bind("Debug", "IgnoreStartupShortcut", false,
                "Testing only. Act as though the Startup shortcut is absent, so the mod "
                + "starts the voice services itself.");

            // Not exposed in settings and not meant to be switched off: spontaneous
            // remarks are most of what makes her feel present rather than summoned.
            CfgAmbientEnabled = Config.Bind("Companion", "AmbientAlwaysOn", true,
                "Allow occasional generated remarks and responses to physical interactions.");
            CfgAmbientMinMinutes = Config.Bind("Companion", "AmbientMinMinutes", 12,
                "Minimum interval between spontaneous remarks.");
            CfgAmbientMaxMinutes = Config.Bind("Companion", "AmbientMaxMinutes", 25,
                "Maximum interval between spontaneous remarks.");
            CfgReplaceGameVoice = Config.Bind("Voice", "ReplaceAllGameVoice", true,
                "Replace built-in dialogue voice with cached GPT-SoVITS speech.");
            CfgVoiceSynthesisPreferred = Config.Bind("Voice", "VocalSynthesisPreferred",
                CfgReplaceGameVoice.Value,
                "Saved user preference. Service outages temporarily fall back to native voice without changing this value.");
            CfgLilithOpacity = Config.Bind("Display", "LilithOpacity", 0.6f,
                "Lilith character opacity from 0.2 to 1.0. Click-through uses the game's built-in setting.");
            // Deliberately new key names. Config.Bind keeps whatever is already in the
            // cfg, so reusing "WakeWordEnabled" would silently inherit the old value
            // and the setting would appear to do nothing on an existing install.
            CfgPushToTalkEnabled = Config.Bind("VoiceInput", "PushToTalkEnabled", true,
                "Enable the external push-to-talk transcriber.");
            CfgPushToTalkKey = Config.Bind("VoiceInput", "PushToTalkKey", "F8",
                "Press this key to start and stop speaking. F1-F12, A-Z, or 0-9.");
            // "WakeWordEnabled" is burned (see the note above): existing configs
            // still carry the old listener's value and would silently pre-enable
            // this. A fresh key keeps those installs inert until opted in.
            CfgWakeWord = Config.Bind("VoiceInput", "WakeWord", false,
                "Listen for her name and start recording without pressing the push-to-talk key. "
                + "Needs the speech listener, an API key, and a wake-word model. "
                + "Keeps the microphone open while enabled.");

            // Notes are meant to be keepsakes, so all three gates are deliberately
            // strict: substance, then time, then chance.
            CfgNoteMinConversations = Config.Bind("Letters", "MinConversationsPerNote", 10,
                "Messages from the player, substantial enough to count, required before a note becomes possible.");
            CfgNoteMinMessageLength = Config.Bind("Letters", "MinMessageLength", 18,
                "Characters a player message needs before it counts toward a note.");
            CfgNoteWindowHours = Config.Bind("Letters", "WindowHours", 4.0,
                "Those conversations must all fall inside this many hours, so a note comes out of one stretch of talking.");
            CfgNoteCooldownHours = Config.Bind("Letters", "CooldownHours", 36.0,
                "Minimum hours between notes.");
            // Rolled again on every substantial message once the count is met, not
            // once per stretch of talking - so a high value compounds fast. At 0.4 a
            // note was near-certain within five messages of becoming eligible.
            CfgNoteChance = Config.Bind("Letters", "Chance", 0.2f,
                "Chance a note is written once it is otherwise due, so it does not arrive on a felt schedule. "
                + "Re-rolled per qualifying message, so small values still add up.");

            CfgAllowOpenApps = Config.Bind("Apps", "AllowOpenApps", false,
                "Allow Lilith to open sanctioned applications when asked (discord, steam, etc.). "
                + "The allowed list lives in plugins/LilithMod/apps/lilith-apps.txt and can be edited at any time.");
            // Create the default allowed-apps list on first run, so it can be edited
            // before the feature is ever switched on. AppLauncher owns the defaults;
            // reading the names is what triggers the lazy file creation.
            try { AppLauncher.GetAllowedNames(); }
            catch (System.Exception ex) { Log.LogWarning("[Apps] Could not prepare allowed-apps list: " + ex.Message); }

            NoteJournal.Initialize();

            MemoryStore.Initialize();

            // ---- Voice configuration ------------------------------------------
            BindVoiceConfig();

            // Use BepInEx's own AddComponent rather than creating a GameObject here.
            // Load() runs before the first scene exists, so a hand-made GameObject does
            // not survive DontDestroyOnLoad and its Update() never ticks. BepInEx attaches
            // to its persistent BepInEx_Manager object and registers the type for us.
            AddComponent<DialogueInjector>();
            AddComponent<LlmChatController>();
            AddComponent<SettingsBridge>();
            AddComponent<GameVoiceCoordinator>();
            AddComponent<VoiceServiceMonitor>();

            // ---- Voice initialisation -----------------------------------------
            InitVoice();
            // After InitVoice, which loads voice-config.ini and so knows where the
            // runtime lives and which endpoint to probe.
            ServiceBootstrap.Run();

            LogStartupSummary();

            try
            {
                ModIntegrations.Install(new HarmonyLib.Harmony("LilithMod.integrations"));
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[LilithMod] Integration patches failed: {ex.Message}");
            }

            // Off by default: force-firing nothing, only logging. Authors turn this on to
            // discover which DialogueTriggerType a given interaction actually raises.
            var logTriggers = Config.Bind("Debug", "LogTriggers", false,
                "Log every dialogue trigger the game raises, and every node that begins. "
                + "Use this to find the right 'trigger' value for custom nodes.");

            if (logTriggers.Value)
            {
                try
                {
                    DebugProbe.Install(new HarmonyLib.Harmony("LilithMod.probe"));
                }
                catch (System.Exception ex)
                {
                    Log.LogError($"[LilithMod] Trigger logging failed to install: {ex}");
                }
            }
        }

        internal static void SaveConfig()
        {
            try { _instance?.Config.Save(); }
            catch (System.Exception ex) { Logger?.LogWarning($"[Settings] Could not save config: {ex.Message}"); }
        }

        private void BindVoiceConfig()
        {
            CfgVoiceEnabled = Config.Bind("Voice", "Enabled", false,
                "Master switch for TTS voice output. When false, no voice threads or "
                + "network calls are started.");

            CfgVoiceEndpoint = Config.Bind("Voice", "Endpoint",
                "http://127.0.0.1:9880/tts",
                "Full URL of the GPT‑SoVITS TTS endpoint.");

            // The reference clip is NOT shipped with this mod - it belongs to the
            // game's rights holders. Point this at a clip you already have locally.
            // The default is relative on purpose: an absolute path here is correct
            // on exactly one machine and silently wrong on every other install.
            // The service reads this path itself, so it must be readable by that
            // process.
            CfgVoiceRefAudioPath = Config.Bind("Voice", "RefAudioPath",
                @"voice\jp\calm-reference.wav",
                "Absolute path to the reference WAV the voice is cloned from, as seen "
                + "by the TTS service. A relative path is resolved against the mod folder. "
                + "3-10 seconds works best.");

            CfgVoicePromptText = Config.Bind("Voice", "PromptText",
                "これは儀式でもあるの。君に私の存在を感じてもらうための儀式ね。",
                "Exact transcript of the reference audio clip (prompt_text).");

            CfgVoiceTextLang = Config.Bind("Voice", "TextLang", "ja",
                "Language code for the input text (e.g. ja, zh, en).");

            CfgVoicePromptLang = Config.Bind("Voice", "PromptLang", "ja",
                "Language code for the reference audio (e.g. ja, zh).");

            CfgVoiceTimeoutSeconds = Config.Bind("Voice", "TimeoutSeconds", 60,
                "HTTP timeout in seconds for each TTS request.");

            CfgVoiceWarmUpTimeoutSeconds = Config.Bind("Voice", "WarmUpTimeoutSeconds", 120,
                "Total time budget in seconds for the warm‑up phase.");

            CfgVoiceFragmentInterval = Config.Bind("Voice", "FragmentInterval", 0.4f,
                "fragment_interval parameter passed to the TTS service.");

            CfgVoiceTextSplitMethod = Config.Bind("Voice", "TextSplitMethod", "cut5",
                "text_split_method parameter passed to the TTS service.");
        }

        /// <summary>
        /// One line describing the state every support question turns out to hinge on.
        /// Reconstructing this from scattered entries was the slow part of every
        /// diagnosis so far, and on a machine that is not this one it may be the only
        /// evidence available.
        /// </summary>
        private void LogStartupSummary()
        {
            try
            {
                Log.LogInfo(
                    "[LilithMod] Startup: " +
                    $"apiKey={(string.IsNullOrWhiteSpace(CfgApiKey?.Value) ? "absent" : "present")}, " +
                    $"voice={(VoiceConfig.Enabled ? "enabled" : "disabled")}, " +
                    $"synthesisPreferred={CfgVoiceSynthesisPreferred?.Value}, " +
                    $"endpoint={VoiceConfig.Endpoint}, " +
                    $"servicesStartedByMod={ServiceBootstrap.StartedServices}, " +
                    $"speechLanguage={VoiceConfig.TextLang}.");

                // Loud and separate: these change behaviour in ways that look like
                // bugs, and a report made with one left on wastes everyone's time.
                if (CfgForceSynthesisUnavailable.Value || CfgForceSleeping.Value ||
                    CfgIgnoreStartupShortcut.Value)
                {
                    Log.LogWarning(
                        "[LilithMod] TESTING FLAGS ARE ON - behaviour is deliberately not normal: " +
                        $"ForceSynthesisUnavailable={CfgForceSynthesisUnavailable.Value}, " +
                        $"ForceSleeping={CfgForceSleeping.Value}, " +
                        $"IgnoreStartupShortcut={CfgIgnoreStartupShortcut.Value}.");
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[LilithMod] Could not write the startup summary: {ex.Message}");
            }
        }

        private void InitVoice()
        {
            VoiceSetup.Load();
            bool setupEnabled = VoiceSetup.Loaded ? VoiceSetup.Enabled : CfgVoiceEnabled.Value;
            if (!setupEnabled)
            {
                Log.LogInfo("[Voice] Voice is disabled in config; skipping TTS initialisation.");
                return;
            }

            // Populate the static VoiceConfig snapshot.
            VoiceConfig.Enabled = true;
            VoiceProfile profile = VoiceSetup.Loaded ? VoiceSetup.Profile() : null;
            VoiceConfig.Endpoint = VoiceSetup.Loaded ? VoiceSetup.Endpoint : CfgVoiceEndpoint.Value;
            VoiceConfig.PromptText = profile?.PromptText ?? CfgVoicePromptText.Value;
            VoiceConfig.TextLang = profile?.Language ?? CfgVoiceTextLang.Value;
            VoiceConfig.SubtitleLang = VoiceSetup.Loaded ? VoiceSetup.SubtitleLanguage : "en";
            VoiceConfig.PromptLang = profile?.PromptLanguage ?? CfgVoicePromptLang.Value;
            VoiceConfig.CacheIdentity = profile?.CacheIdentity ??
                (VoiceConfig.TextLang.StartsWith("ja") ? "ja-finetuned-e12-s1016-v1" : VoiceConfig.TextLang);
            VoiceConfig.GptWeights = profile?.GptWeights;
            VoiceConfig.SovitsWeights = profile?.SovitsWeights;
            VoiceConfig.WarmUpText = profile?.WarmUpText;
            VoiceConfig.TimeoutSeconds = CfgVoiceTimeoutSeconds.Value;
            VoiceConfig.WarmUpTimeoutSeconds = CfgVoiceWarmUpTimeoutSeconds.Value;
            VoiceConfig.FragmentInterval = CfgVoiceFragmentInterval.Value;
            VoiceConfig.TextSplitMethod = CfgVoiceTextSplitMethod.Value;

            // Resolve the reference audio path.
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            VoiceConfig.RefAudioPath = profile?.RefAudioPath;
            if (string.IsNullOrWhiteSpace(VoiceConfig.RefAudioPath))
                VoiceConfig.RefAudioPath = Path.GetFullPath(
                    Path.Combine(modDir ?? ".", CfgVoiceRefAudioPath.Value));

            CfgVoiceTextLang.Value = VoiceConfig.TextLang;
            CfgVoicePromptLang.Value = VoiceConfig.PromptLang;
            // Availability is applied by VoiceServiceMonitor. Start in native fallback
            // until the configured endpoint passes its first health check.
            CfgReplaceGameVoice.Value = false;

            if (!File.Exists(VoiceConfig.RefAudioPath))
            {
                Log.LogError(
                    $"[Voice] Reference audio not found at '{VoiceConfig.RefAudioPath}'. "
                    + "Voice disabled.");
                VoiceConfig.Enabled = false;
                return;
            }

            Log.LogInfo($"[Voice] Reference audio: {VoiceConfig.RefAudioPath}");

            try
            {
                var ttsClient = new TtsClient();
                var voicePlayer = new VoicePlayer();
                VoiceProcessor = new SpeechQueueProcessor(ttsClient, voicePlayer);
                VoiceProcessor.Start();
                Log.LogInfo("[Voice] Voice processor started (warm‑up in background).");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"[Voice] Failed to initialise voice: {ex.Message}");
                VoiceConfig.Enabled = false;
                VoiceProcessor?.Dispose();
                VoiceProcessor = null;
            }
        }
    }
}
