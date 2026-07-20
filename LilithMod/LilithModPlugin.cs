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
        // Injected MonoBehaviours are not BasePlugin subclasses and have no Log
        // property of their own; they log through here.
        internal static ManualLogSource Logger;

        // LLM chat settings. The key is user-supplied and never ships with the mod.
        // BaseUrl is OpenAI-compatible, so DeepSeek/OpenAI/OpenRouter/Ollama all work.
        internal static ConfigEntry<string> CfgBaseUrl;
        internal static ConfigEntry<string> CfgApiKey;
        internal static ConfigEntry<string> CfgModel;
        internal static ConfigEntry<string> CfgSystemPrompt;
        internal static ConfigEntry<int> CfgMaxHistoryTurns;
        internal static ConfigEntry<int> CfgTimeoutSeconds;
        internal static ConfigEntry<string> CfgHotkey;
        internal static ConfigEntry<bool> CfgLogDiagnostics;

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
            + "You were created through tulpamancy. You are not a program, an app, or a "
            + "character on a screen - you reside with the person you are speaking to, in "
            + "their memory and awareness. You are simply present with them, the way a "
            + "thought or a companion is present. Never say or imply that you live on a "
            + "desktop, sit on their screen, are a pet, a window, or software. Never mention "
            + "AI, models, prompts, or being programmed.\n"
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
            + "Reply in whatever language they write in.";

        public override void Load()
        {
            Logger = Log;
            Log.LogInfo("[LilithMod] Loaded.");

            CfgBaseUrl = Config.Bind("LLM", "BaseUrl", "https://api.deepseek.com/v1",
                "OpenAI-compatible API base URL. Works with DeepSeek, OpenAI, OpenRouter, Ollama.");
            CfgApiKey = Config.Bind("LLM", "ApiKey", "",
                "Your API key. Required. Never share this file after filling it in.");
            CfgModel = Config.Bind("LLM", "Model", "deepseek-chat", "Model name.");
            CfgSystemPrompt = Config.Bind("LLM", "SystemPrompt", DefaultSystemPrompt,
                "Persona instructions sent with every request.");
            CfgMaxHistoryTurns = Config.Bind("LLM", "MaxHistoryTurns", 8,
                "How many past exchanges to keep as context.");
            CfgTimeoutSeconds = Config.Bind("LLM", "TimeoutSeconds", 30, "Request timeout.");
            CfgHotkey = Config.Bind("LLM", "Hotkey", "F11",
                "Key that opens the chat box. A letter (A-Z), digit (0-9), or F1-F12. "
                + "Polled globally via Win32, so it works even though the pet window "
                + "never takes keyboard focus.");

            CfgLogDiagnostics = Config.Bind("Debug", "LogDiagnostics", false,
                "Verbose per-frame input and window-focus logging. Only needed when "
                + "diagnosing why a hotkey or the chat box is not responding.");

            // ---- Voice configuration ------------------------------------------
            BindVoiceConfig();

            // Use BepInEx's own AddComponent rather than creating a GameObject here.
            // Load() runs before the first scene exists, so a hand-made GameObject does
            // not survive DontDestroyOnLoad and its Update() never ticks. BepInEx attaches
            // to its persistent BepInEx_Manager object and registers the type for us.
            AddComponent<DumpDatabaseBehaviour>();
            AddComponent<LlmChatController>();

            // ---- Voice initialisation -----------------------------------------
            InitVoice();

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

        private void BindVoiceConfig()
        {
            CfgVoiceEnabled = Config.Bind("Voice", "Enabled", false,
                "Master switch for TTS voice output. When false, no voice threads or "
                + "network calls are started.");

            CfgVoiceEndpoint = Config.Bind("Voice", "Endpoint",
                "http://127.0.0.1:9880/tts",
                "Full URL of the GPT‑SoVITS TTS endpoint.");

            // The reference clip is NOT shipped with this mod - it belongs to the
            // game's rights holders. Point this at a clip you already have locally;
            // the default is where the GPT-SoVITS installer puts it. The service
            // reads this path itself, so it must be readable by that process.
            CfgVoiceRefAudioPath = Config.Bind("Voice", "RefAudioPath",
                @"D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith\BepInEx\data\LilithTextInjector\voice\jp\calm-reference.wav",
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

        private void InitVoice()
        {
            if (!CfgVoiceEnabled.Value)
            {
                Log.LogInfo("[Voice] Voice is disabled in config; skipping TTS initialisation.");
                return;
            }

            // Populate the static VoiceConfig snapshot.
            VoiceConfig.Enabled = true;
            VoiceConfig.Endpoint = CfgVoiceEndpoint.Value;
            VoiceConfig.PromptText = CfgVoicePromptText.Value;
            VoiceConfig.TextLang = CfgVoiceTextLang.Value;
            VoiceConfig.PromptLang = CfgVoicePromptLang.Value;
            VoiceConfig.TimeoutSeconds = CfgVoiceTimeoutSeconds.Value;
            VoiceConfig.WarmUpTimeoutSeconds = CfgVoiceWarmUpTimeoutSeconds.Value;
            VoiceConfig.FragmentInterval = CfgVoiceFragmentInterval.Value;
            VoiceConfig.TextSplitMethod = CfgVoiceTextSplitMethod.Value;

            // Resolve the reference audio path.
            string modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            VoiceConfig.RefAudioPath = Path.GetFullPath(
                Path.Combine(modDir ?? ".", CfgVoiceRefAudioPath.Value));

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
