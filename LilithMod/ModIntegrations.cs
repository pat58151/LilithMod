using System;
using HarmonyLib;
using UI.TraySetting;
using UnityEngine;
using UnityEngine.UI;

namespace LilithMod
{
    internal static class ModIntegrations
    {
        private static DialogueLineDatabase _voiceDatabase;
        private static string _voiceLanguage;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(LilithInteractionLog), nameof(LilithInteractionLog.Record)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(OnInteraction)));
            harmony.Patch(
                AccessTools.Method(typeof(TraySettingView), nameof(TraySettingView.EnsureV101Rows)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(OnSettingsBuilt)));
            harmony.Patch(
                AccessTools.Method(typeof(TraySettingView), nameof(TraySettingView.OnGameVoiceButtonChanged)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(OnNativeVoiceLanguageChanged)));
            harmony.Patch(
                AccessTools.Method(typeof(DialogueManager), nameof(DialogueManager.PlayNodeVoice)),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceGameVoice)));
            harmony.Patch(
                AccessTools.Method(typeof(DialogueBubbleUI), nameof(DialogueBubbleUI.ShowNode)),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(GateDialogueNode)));
            harmony.Patch(
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayVoiceBySoundId),
                    new[] { typeof(string), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceVoiceBySoundId)));
            harmony.Patch(
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayVoiceBySoundId),
                    new[] { typeof(string), typeof(LilithActionType), typeof(bool), typeof(LilithActionType) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceVoiceBySoundId)));
            harmony.Patch(
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayVoice),
                    new[] { typeof(AudioClip), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceResolvedDialogueVoice)));
            harmony.Patch(
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayVoice),
                    new[] { typeof(string), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceVoiceByName)));
            harmony.Patch(
                AccessTools.Method(typeof(DialogueBubbleUI), nameof(DialogueBubbleUI.Start)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(MakeDialogueBubbleTransparent)));
            harmony.Patch(
                AccessTools.Method(typeof(DialogueBubbleUI), nameof(DialogueBubbleUI.ShowOptions)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(MakeDialogueOptionsTransparent)));
            harmony.Patch(
                AccessTools.Method(typeof(PlayerLineController), nameof(PlayerLineController.Awake)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(MakePlayerChoicesTransparent)));
            harmony.Patch(
                AccessTools.Method(typeof(OptionButtonAnim), nameof(OptionButtonAnim.PrepareBeforeShow)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(MakeOptionBackgroundTransparent)));
            harmony.Patch(
                AccessTools.Method(typeof(OptionButtonAnim), nameof(OptionButtonAnim.OnEnterComplete)),
                postfix: new HarmonyMethod(typeof(ModIntegrations), nameof(MakeOptionBackgroundTransparent)));
            LilithModPlugin.Logger.LogInfo("[LilithMod] Memory, settings, and voice integrations installed.");
        }

        public static void OnInteraction(string kind)
        {
            LlmChatController.RecordInteraction(kind);
        }

        public static void OnSettingsBuilt(TraySettingView __instance)
        {
            SettingsBridge.QueueBuild(__instance);
        }

        public static void OnNativeVoiceLanguageChanged(TraySettingChanged.GameLocalizationVoiceType voiceType)
        {
            SettingsBridge.SetVoiceLanguageFromNative(voiceType);
        }

        public static bool ReplaceGameVoice(DialogueNode node)
        {
            return AllowNativeVoice();
        }

        /// <summary>
        /// Whether the game's own audio should play. Normally that is simply "the mod
        /// is not replacing it", but during the synthesis grace window neither side
        /// should be heard: the mod cannot synthesise yet, and letting the native clip
        /// through means Chinese audio under a subtitle the bubble gate already
        /// dropped. Both halves of a line live or die together.
        /// </summary>
        private static bool AllowNativeVoice()
        {
            return !VoiceReplacementEnabled() && !GameVoiceCoordinator.HoldingForSynthesis;
        }

        public static bool GateDialogueNode(DialogueBubbleUI __instance, DialogueNode node)
        {
            // Every node the game puts on screen passes through here, so this is
            // where "the game just said something" is observable. 9500000 is this
            // mod's own reply node and must not count as the game speaking.
            if (node != null && node.id != ModReplyNodeId)
                LlmChatController.NoteNativeDialogue();
            return GameVoiceCoordinator.AllowShowNode(__instance, node);
        }

        private const int ModReplyNodeId = 9500000;

        public static bool ReplaceVoiceBySoundId(string soundId)
        {
            return AllowNativeVoice();
        }

        public static bool ReplaceVoiceByName(string voiceName)
        {
            return AllowNativeVoice();
        }

        public static bool ReplaceResolvedDialogueVoice(AudioClip clip, bool isDialogueLine)
        {
            // Only dialogue is ours to suppress; other audio through this path is
            // sound effects, which the mod has no business silencing.
            if (!isDialogueLine) return true;
            return AllowNativeVoice();
        }

        internal static bool VoiceReplacementEnabled()
        {
            return LilithModPlugin.CfgReplaceGameVoice.Value && VoiceConfig.Enabled &&
                LilithModPlugin.VoiceProcessor != null;
        }

        public static void MakeDialogueBubbleTransparent(DialogueBubbleUI __instance)
        {
            if (__instance?._bubbleButton != null)
                MakeButtonTransparent(__instance._bubbleButton);
        }

        public static void MakeDialogueOptionsTransparent(DialogueBubbleUI __instance)
        {
            if (__instance?._optionInstances == null) return;
            for (int i = 0; i < __instance._optionInstances.Count; i++)
            {
                var option = __instance._optionInstances[i];
                if (option == null) continue;
                MakeButtonTransparent(option.GetComponent<Button>());
                MakeOptionBackgroundTransparent(option.GetComponent<OptionButtonAnim>());
            }
        }

        public static void MakePlayerChoicesTransparent(PlayerLineController __instance)
        {
            if (__instance?._buttons == null) return;
            for (int i = 0; i < __instance._buttons.Count; i++)
                MakeButtonTransparent(__instance._buttons[i]);
        }

        public static void MakeOptionBackgroundTransparent(OptionButtonAnim __instance)
        {
            if (__instance?._backgroundImage == null) return;
            Color color = __instance._backgroundImage.color;
            color.a = CurrentUiOpacity();
            __instance._backgroundImage.color = color;
        }

        public static void ApplyDialogueOpacity(float opacity)
        {
            var bubbles = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<DialogueBubbleUI>());
            for (int i = 0; i < bubbles.Length; i++)
            {
                var bubble = bubbles[i].TryCast<DialogueBubbleUI>();
                if (bubble == null) continue;
                if (bubble._bubbleButton != null)
                    MakeButtonTransparent(bubble._bubbleButton, opacity);
                if (bubble._optionInstances == null) continue;
                for (int j = 0; j < bubble._optionInstances.Count; j++)
                {
                    var option = bubble._optionInstances[j];
                    if (option == null) continue;
                    MakeButtonTransparent(option.GetComponent<Button>(), opacity);
                    var anim = option.GetComponent<OptionButtonAnim>();
                    if (anim?._backgroundImage != null)
                    {
                        Color color = anim._backgroundImage.color;
                        color.a = opacity;
                        anim._backgroundImage.color = color;
                    }
                }
            }
        }

        private static void MakeButtonTransparent(Button button)
        {
            MakeButtonTransparent(button, CurrentUiOpacity());
        }

        private static void MakeButtonTransparent(Button button, float opacity)
        {
            if (button == null) return;
            if (button.targetGraphic != null)
                button.targetGraphic.color = WithAlpha(button.targetGraphic.color, opacity);
            var image = button.GetComponent<Image>();
            if (image != null)
            {
                Color color = image.color;
                color.a = opacity;
                image.color = color;
            }

            ColorBlock colors = button.colors;
            colors.normalColor = WithAlpha(colors.normalColor, opacity);
            colors.highlightedColor = WithAlpha(colors.highlightedColor, opacity);
            colors.pressedColor = WithAlpha(colors.pressedColor, opacity);
            colors.selectedColor = WithAlpha(colors.selectedColor, opacity);
            colors.disabledColor = WithAlpha(colors.disabledColor, opacity);
            button.colors = colors;
        }

        private static float CurrentUiOpacity()
        {
            return Mathf.Clamp(LilithModPlugin.CfgLilithOpacity.Value, 0.2f, 1f);
        }

        private static Color WithAlpha(Color color, float opacity)
        {
            color.a = opacity;
            return color;
        }
    }
}
