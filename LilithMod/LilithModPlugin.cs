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
        }
    }
}
