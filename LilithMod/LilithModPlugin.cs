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

            ClassInjector.RegisterTypeInIl2Cpp<DumpDatabaseBehaviour>();

            var go = new GameObject("LilithMod");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DumpDatabaseBehaviour>();
        }
    }
}
