using System;
using System.Collections.Generic;
using UnityEngine;

namespace LilithMod
{
    public sealed class GameVoiceCoordinator : MonoBehaviour
    {
        private static GameVoiceCoordinator _instance;
        private static bool _allowOriginalShow;
        private readonly HashSet<long> _pendingNodes = new HashSet<long>();
        private int _dynamicAlarmLine;
        private float _alarmDialogueUntil;
        private static readonly string[] AlarmEnglish =
        {
            "Chime, chime! Time's up! You asked Lilith to remind you.",
            "Time's up. Go do what you need to do. Lilith will be watching.",
            "It's alarm time. Don't pretend you forgot. You chose this time yourself.",
            "Hey, wake up! It's the time you told Lilith about."
        };

        public GameVoiceCoordinator(IntPtr ptr) : base(ptr) { }

        private void Awake()
        {
            _instance = this;
        }

        internal static bool AllowShowNode(DialogueBubbleUI bubble, DialogueNode node)
        {
            // Node 9500000 is the mod's already-synchronised reply bubble. Replacing it
            // again would queue the English subtitle as a second TTS line.
            if (node != null && node.id == 9500000) return true;
            if (_allowOriginalShow || !ModIntegrations.VoiceReplacementEnabled() ||
                _instance == null || bubble == null || node == null)
            {
                // Which early-out fired matters: a line that slips through here keeps
                // the game's own voice, which is Chinese, and that is indistinguishable
                // from a bug elsewhere unless the reason is recorded.
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    string why =
                        node == null ? "node null" :
                        bubble == null ? "bubble null" :
                        _allowOriginalShow ? "re-show of a line already replaced" :
                        _instance == null ? "coordinator not awake" :
                        !LilithModPlugin.CfgReplaceGameVoice.Value ? "ReplaceGameVoice off" :
                        !VoiceConfig.Enabled ? "voice disabled" :
                        LilithModPlugin.VoiceProcessor == null ? "voice processor not ready" :
                        "unknown";
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Original voice kept for line {(node == null ? -1 : node.lineId)} " +
                        $"(id {(node == null ? -1 : node.id)}): {why}.");
                }
                return true;
            }
            return _instance.QueueNode(bubble, node);
        }

        private bool QueueNode(DialogueBubbleUI bubble, DialogueNode node)
        {
            long key = node.Pointer.ToInt64();
            if (_pendingNodes.Contains(key)) return false;

            string language = PersonaPrompt.CurrentVoiceLanguage();
            string text = null;
            if (node.lineId > 0)
                DialogueTextCatalog.TryGet(node.lineId, language, out text);
            // TimerSystem and AlarmSystem build their ringing dialogue at runtime with
            // lineId 0 in the UI language. Never feed that English text to Japanese TTS.
            bool alarmRinging = AudioManager.IsTimerAlarmRinging;
            if (alarmRinging)
                _alarmDialogueUntil = Time.unscaledTime + 180f;
            if (node.lineId <= 0 && (alarmRinging || Time.unscaledTime < _alarmDialogueUntil) &&
                language.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            {
                string original = node.text?.ToLowerInvariant() ?? string.Empty;
                bool acknowledgement = original.Contains("alarm's off") || original.Contains("alarm is off") ||
                    original.Contains("alarm off") || original.Contains("got it") || original.Contains("cancel");
                bool snooze = original.Contains("leave it to me") || original.Contains("again") ||
                    original.Contains("snooze") || original.Contains("minute") || original.Contains("extend");
                int alarmIndex = _dynamicAlarmLine++ % 4;
                int alarmLineId = acknowledgement ? 2951001 : snooze ? 2951002 : 2051005 + alarmIndex;
                DialogueTextCatalog.TryGet(alarmLineId, language, out text);
                string subtitleLanguage = PersonaPrompt.CurrentDisplayLanguage();
                if (subtitleLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    node.text = acknowledgement ? "Mm-hm... the alarm is off now." :
                        snooze ? "Okay, leave it to Lilith. I'll remind you again in fifteen minutes." :
                        AlarmEnglish[alarmIndex];
                else if (subtitleLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                    node.text = text;
                else if (DialogueTextCatalog.TryGet(alarmLineId, subtitleLanguage, out string localized))
                    node.text = localized;
            }
            if (string.IsNullOrWhiteSpace(text))
                text = node.text;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var cue = new NativeDialogueCue(bubble, node, key);
            _pendingNodes.Add(key);
            LilithModPlugin.VoiceProcessor.Enqueue(new Utterance
            {
                JaText = text,
                EnText = null,
                Language = language,
                SuppressSubtitle = true,
                NativeDialogue = cue
            });
            if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                LilithModPlugin.Logger.LogInfo($"[Voice] Holding line {node.lineId} until {language} audio is ready.");
            return false;
        }

        private void Update()
        {
            var processor = LilithModPlugin.VoiceProcessor;
            if (processor == null) return;
            while (processor.NativeDialogueQueue.TryDequeue(out NativeDialogueCue cue))
            {
                _pendingNodes.Remove(cue.Key);
                try
                {
                    _allowOriginalShow = true;
                    if (cue.Bubble != null && cue.Node != null)
                        cue.Bubble.ShowNode(cue.Node);
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Voice] Native dialogue handoff failed: {ex.Message}");
                }
                finally
                {
                    _allowOriginalShow = false;
                    cue.MarkDisplayed();
                    if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    {
                        // Pairs with the "Holding line" entry. A held line with no
                        // matching re-show is a bubble that will never be handed back
                        // to the game, which is the shape to look for when one sticks
                        // on screen forever.
                        LilithModPlugin.Logger.LogInfo(
                            $"[Voice] Re-showed line {(cue.Node == null ? -1 : cue.Node.lineId)} " +
                            $"after audio; {_pendingNodes.Count} still held.");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
