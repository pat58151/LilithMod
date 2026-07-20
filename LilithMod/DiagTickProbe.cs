using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.LowLevel;

namespace LilithMod
{
    /// <summary>
    /// Temporary diagnostic. Looks for a per-frame tick that does not depend on Unity
    /// dispatching a method on an injected class, which is broken on this game build.
    ///
    /// Two candidates:
    ///  - PlayerLoop: registered through method calls rather than a static field write, so
    ///    it does not depend on the target class already being initialised.
    ///  - DOTween: the game drives it from its own MonoBehaviour, which dispatches normally,
    ///    and its callbacks are ordinary delegates.
    /// </summary>
    internal static class DiagTickProbe
    {
        private static bool _playerLoop;

        public static void Install()
        {
            TryIt("PlayerLoop", InstallPlayerLoop);
        }

        private static void InstallPlayerLoop()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var subs = loop.subSystemList;

            var ours = new PlayerLoopSystem
            {
                // Unity validates the system tree and discards entries with no type, so this
                // must be set even though the type is only an identifier here.
                type = Il2CppType.Of<DumpDatabaseBehaviour>(),
                updateDelegate = DelegateSupport.ConvertDelegate<PlayerLoopSystem.UpdateFunction>(
                    new Action(() => Once(ref _playerLoop, "PlayerLoop updateDelegate"))),
            };

            // Append our system at the top level; a root entry with only an updateDelegate is
            // invoked once per frame.
            var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerLoopSystem>(
                subs.Length + 1);
            for (int i = 0; i < subs.Length; i++) grown[i] = subs[i];
            grown[subs.Length] = ours;

            loop.subSystemList = grown;
            PlayerLoop.SetPlayerLoop(loop);
            LilithModPlugin.Logger.LogInfo($"[DIAG] PlayerLoop: appended to {subs.Length} root systems.");
        }

        private static void TryIt(string what, Action go)
        {
            try { go(); }
            catch (Exception ex) { LilithModPlugin.Logger.LogWarning($"[DIAG] {what} failed: {ex.Message}"); }
        }

        private static void Once(ref bool flag, string what)
        {
            if (flag) return;
            flag = true;
            LilithModPlugin.Logger.LogInfo($"[DIAG] *** FIRED: {what} ***");
        }
    }
}
