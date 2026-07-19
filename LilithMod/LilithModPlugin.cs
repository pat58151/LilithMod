using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace LilithMod
{
    [BepInPlugin("LilithMod", "LilithMod", "1.0.0")]
    public class LilithModPlugin : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo("[LilithMod] Loaded.");

            ClassInjector.RegisterTypeInIl2Cpp<DumpDatabaseBehaviour>();

            var go = new GameObject("LilithMod");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DumpDatabaseBehaviour>();
        }
    }
}
