using System;
using HarmonyLib;
using UnityEngine;

namespace LilithMod
{
    // Optional authoring aid, off by default. The game raises DialogueTriggerType values
    // that are not obvious from the enum name alone (touch triggers do not fire while the
    // pet is asleep, for instance), so content authors need a way to discover which
    // trigger a given interaction actually produces. Enable via the plugin config.
    public static class DebugProbe
    {
        public static int FirstInjectedNodeId = -1;

        public static void Install(Harmony harmony)
        {
            var mgr = typeof(DialogueManager);

            harmony.Patch(
                AccessTools.Method(mgr, nameof(DialogueManager.StartDialogueByTrigger)),
                postfix: new HarmonyMethod(typeof(DebugProbe), nameof(LogTrigger)));

            harmony.Patch(
                AccessTools.Method(mgr, nameof(DialogueManager.BeginDialogue)),
                prefix: new HarmonyMethod(typeof(DebugProbe), nameof(LogBegin)));

            LilithModPlugin.Logger.LogInfo("[LilithMod] Trigger logging enabled.");
        }

        public static void LogTrigger(DialogueTriggerType triggerType, bool __result)
        {
            LilithModPlugin.Logger.LogInfo(
                $"[PROBE] StartDialogueByTrigger({triggerType} = {(int)triggerType}) -> {__result}");
        }

        public static void LogBegin(DialogueNode node)
        {
            if (node == null)
            {
                LilithModPlugin.Logger.LogInfo("[PROBE] BeginDialogue(null)");
                return;
            }
            LilithModPlugin.Logger.LogInfo(
                $"[PROBE] BeginDialogue id={node.id} lineId={node.lineId} text='{node.text}'");
        }
    }

}
