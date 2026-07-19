using BepInEx;
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

        public override void Load()
        {
            Logger = Log;
            Log.LogInfo("[LilithMod] Loaded.");

            // Use BepInEx's own AddComponent rather than creating a GameObject here.
            // Load() runs before the first scene exists, so a hand-made GameObject does
            // not survive DontDestroyOnLoad and its Update() never ticks. BepInEx attaches
            // to its persistent BepInEx_Manager object and registers the type for us.
            AddComponent<DumpDatabaseBehaviour>();

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
