using HarmonyLib;

namespace LilithMod
{
    /// <summary>
    /// Temporary diagnostic. Proves whether a Harmony postfix on a game method that runs
    /// every frame can drive our code, independently of whether Unity dispatches Update()
    /// to injected MonoBehaviour types.
    /// </summary>
    internal static class DiagHeartbeat
    {
        private static int _frames;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ArchiveRuntimeTracker), "Update");
            if (target == null)
            {
                LilithModPlugin.Logger.LogError("[DIAG] ArchiveRuntimeTracker.Update not found.");
                return;
            }

            var postfix = AccessTools.Method(typeof(DiagHeartbeat), nameof(Beat));
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            LilithModPlugin.Logger.LogInfo("[DIAG] heartbeat patch installed.");

            // A second target, in case ArchiveRuntimeTracker.Update is not the method the
            // native update loop actually calls.
            var tick = AccessTools.Method(typeof(SteamPlaytimeSync), "Tick");
            if (tick != null)
            {
                harmony.Patch(tick, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DiagHeartbeat), nameof(BeatTick))));
                LilithModPlugin.Logger.LogInfo("[DIAG] SteamPlaytimeSync.Tick patched too.");
            }
            else
            {
                LilithModPlugin.Logger.LogWarning("[DIAG] SteamPlaytimeSync.Tick not found.");
            }

            // The target their mod patches. DialogueManager is central to the pet loop, so if
            // any il2cpp Update is dispatched and patchable, this is it.
            var dm = AccessTools.Method(typeof(DialogueManager), "Update");
            if (dm != null)
            {
                harmony.Patch(dm, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DiagHeartbeat), nameof(BeatDialogue))));
                LilithModPlugin.Logger.LogInfo("[DIAG] DialogueManager.Update patched.");
            }
            else
            {
                LilithModPlugin.Logger.LogWarning("[DIAG] DialogueManager.Update not found.");
            }

            // Did the detours actually land?
            var patched = Harmony.GetAllPatchedMethods();
            int n = 0;
            foreach (var m in patched) { n++; LilithModPlugin.Logger.LogInfo($"[DIAG] patched: {m.DeclaringType?.Name}.{m.Name}"); }
            LilithModPlugin.Logger.LogInfo($"[DIAG] total patched methods: {n}");
        }

        private static bool _dialogueLogged;

        private static void BeatDialogue()
        {
            if (_dialogueLogged) return;
            _dialogueLogged = true;
            LilithModPlugin.Logger.LogInfo("[DIAG] *** FIRED: DialogueManager.Update postfix ***");
        }

        private static void BeatTick()
        {
            if (_frames == 0)
            {
                _frames = -1;
                LilithModPlugin.Logger.LogInfo("[DIAG] SteamPlaytimeSync.Tick postfix fired.");
            }
        }

        private static void Beat()
        {
            _frames++;
            // Log the first tick, then roughly once a minute, so the log stays readable.
            if (_frames == 1 || _frames % 3600 == 0)
                LilithModPlugin.Logger.LogInfo($"[DIAG] heartbeat frame {_frames}");
        }
    }
}
