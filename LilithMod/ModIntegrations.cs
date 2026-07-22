using System;
using HarmonyLib;
using UI.TraySetting;
using UnityEngine;
using UnityEngine.UI;

namespace LilithMod
{
    internal static class ModIntegrations
    {
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
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayActionSEOrVoice),
                    new[] { typeof(LilithActionType), typeof(string) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceActionVoice)));
            harmony.Patch(
                AccessTools.Method(typeof(AudioManager), nameof(AudioManager.PlayAnimationAudio),
                    new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(ModIntegrations), nameof(ReplaceAnimationAudio)));
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
        /// Whether the game's native Chinese voice should play.
        /// </summary>
        private static bool AllowNativeVoice()
        {
            // Chinese is an explicit setting, not an outage fallback. When synthesis
            // is selected but temporarily unavailable, keep subtitles and stay
            // silent until the monitor restores synthesis.
            if (SynthesisSelected) return false;
            // Selecting Chinese cancels synthetic playback before this path runs.
            return true;
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

        public static bool ReplaceActionVoice(LilithActionType actionType, string animName)
        {
            // This route chooses an animation's bundled voice before ShowNode has
            // established a per-line decision. In synthesis mode that timing gap
            // leaked the Chinese touch voice. PlayActionSE is a separate method, so
            // use it directly to retain the interaction effect without native speech.
            if (SynthesisSelected)
            {
                AudioManager.PlayActionSE(actionType);
                return LogSuppressedNativeAudio("action voice", animName);
            }
            return AllowReplacementSensitiveAudio("action voice", animName);
        }

        public static bool ReplaceAnimationAudio(string animName)
        {
            // Animation voice can also run before the dialogue-node gate. Suppress
            // it for the whole selected synthesis mode, not only after a fresh
            // per-line decision exists.
            if (SynthesisSelected)
                return LogSuppressedNativeAudio("animation audio", animName);
            return AllowReplacementSensitiveAudio("animation audio", animName);
        }

        private static bool LogSuppressedNativeAudio(string route, string detail)
        {
            if (LilithModPlugin.CfgLogDiagnostics != null &&
                LilithModPlugin.CfgLogDiagnostics.Value)
            {
                LilithModPlugin.Logger.LogInfo(
                    $"[Voice] Suppressed native {route}" +
                    (string.IsNullOrEmpty(detail) ? "." : $" '{detail}'."));
            }
            return false;
        }

        private static bool AllowReplacementSensitiveAudio(string route, string detail)
        {
            return SynthesisSelected ? LogSuppressedNativeAudio(route, detail) : true;
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
            // The catalogue check governs the bubble gate and all four audio prefixes
            // together. Gating only one leaves the other half of a line suppressed -
            // silent dialogue instead of wrong-language dialogue.
            return LilithModPlugin.CfgReplaceGameVoice.Value && VoiceConfig.Enabled &&
                VoiceServiceMonitor.IsAvailable && LilithModPlugin.VoiceProcessor != null &&
                DialogueTextCatalog.Available;
        }

        internal static bool SynthesisSelected =>
            LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
            LilithModPlugin.CfgVoiceSynthesisPreferred.Value;

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
