using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

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

        private const string DefaultSystemPrompt =
            "You are Lilith, a shy but affectionate desktop companion who lives on the "
            + "user's screen. Speak in short, warm, slightly hesitant lines - usually one "
            + "or two sentences, never more than three. Use soft filler like ellipses when "
            + "flustered. Reply in the same language the user writes in. Never mention that "
            + "you are an AI, a language model, or a program.";

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

            // Use BepInEx's own AddComponent rather than creating a GameObject here.
            // Load() runs before the first scene exists, so a hand-made GameObject does
            // not survive DontDestroyOnLoad and its Update() never ticks. BepInEx attaches
            // to its persistent BepInEx_Manager object and registers the type for us.
            AddComponent<DumpDatabaseBehaviour>();
            AddComponent<LlmChatController>();

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
    }
}
