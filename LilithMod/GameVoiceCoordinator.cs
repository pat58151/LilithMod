using System;
using System.Collections.Generic;
using UnityEngine;

namespace LilithMod
{
    public sealed class GameVoiceCoordinator : MonoBehaviour
    {
        private static GameVoiceCoordinator _instance;
        private static bool _allowOriginalShow;

        /// <summary>Quiet period for native dialogue after Lilith speaks.</summary>
        private const float NativeSuppressedAfterModSeconds = 4f;

        private static float _modSpokeAt = -600f;

        /// <summary>How the next native audio callbacks should handle this line.</summary>
        private const float NativeAudioDecisionSeconds = 2f;

        private static float _nativeAudioDecisionAt = -600f;
        private static bool _nativeAudioAllow;

        private static bool DecisionFresh =>
            Time.unscaledTime < _nativeAudioDecisionAt + NativeAudioDecisionSeconds;

        internal static bool NativeAudioAllowed => DecisionFresh && _nativeAudioAllow;

        /// <summary>Whether the current native line is being replaced.</summary>
        internal static bool NativeAudioSuppressed => DecisionFresh && !_nativeAudioAllow;

        private static void AllowNativeAudioForThisLine()
        {
            _nativeAudioDecisionAt = Time.unscaledTime;
            _nativeAudioAllow = true;
        }

        private static void SuppressNativeAudioForThisLine()
        {
            _nativeAudioDecisionAt = Time.unscaledTime;
            _nativeAudioAllow = false;
        }

        /// <summary>Maximum startup wait before native voice resumes.</summary>
        private const float StartupVoiceGraceSeconds = 15f;

        /// <summary>Startup wait when the voice service is loading from cold.</summary>
        private const float ColdStartVoiceGraceSeconds = 30f;

        private static float VoiceGraceSeconds =>
            ServiceBootstrap.StartedServices ? ColdStartVoiceGraceSeconds : StartupVoiceGraceSeconds;

        /// <summary>Holds both native text and audio while synthesis starts.</summary>
        internal static bool HoldingForSynthesis =>
            !_allowOriginalShow && !VoiceServiceMonitor.EverAvailable &&
            SynthesisPreferred() && DialogueTextCatalog.Available &&
            Time.unscaledTime < VoiceGraceSeconds;

        private static bool SynthesisPreferred()
        {
            return LilithModPlugin.CfgVoiceSynthesisPreferred != null &&
                   LilithModPlugin.CfgVoiceSynthesisPreferred.Value &&
                   VoiceConfig.Enabled;
        }

        /// <summary>Whether cached replacement is configured.</summary>
        private static bool CacheReplacementPossible()
        {
            return SynthesisPreferred() && DialogueTextCatalog.Available &&
                   LilithModPlugin.VoiceProcessor != null;
        }
        private readonly HashSet<long> _pendingNodes = new HashSet<long>();

        /// <summary>Newest dialogue node for each bubble.</summary>
        private readonly Dictionary<long, long> _latestNodeForBubble = new Dictionary<long, long>();
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
            if (node != null && node.id == 9500000)
            {
                _modSpokeAt = Time.unscaledTime;
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    LilithModPlugin.Logger.LogInfo(
                        "[Voice] Mod reply bubble seen; native dialogue held off for " +
                        $"{NativeSuppressedAfterModSeconds:0.#}s.");
                return true;
            }

            // Wait only while a configured service is still starting.
            if (node != null && bubble != null && HoldingForSynthesis)
            {
                // Cached audio needs no service, so try that before dropping.
                if (_instance != null && !_instance.QueueNode(bubble, node, cachedOnly: true))
                    return false;

                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Dropped native line {node.lineId} (id {node.id}); " +
                        "synthesis is preferred but has not come up yet, and this line is not cached.");
                }
                return false;
            }

            // Do not let native dialogue overlap Lilith's reply.
            bool modAudioPlaying = LilithModPlugin.VoiceProcessor != null &&
                LilithModPlugin.VoiceProcessor.PlaybackActive;
            if (node != null && bubble != null && !_allowOriginalShow &&
                (modAudioPlaying || Time.unscaledTime - _modSpokeAt < NativeSuppressedAfterModSeconds))
            {
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Suppressed native line {node.lineId} (id {node.id}); " +
                        (modAudioPlaying ? "synth playback is still active." :
                        "the mod spoke less than " +
                        $"{NativeSuppressedAfterModSeconds:0.#}s ago."));
                }
                // Native dialogue has no deferred queue, so restore Lilith's bubble.
                LlmChatController.RequestReplyBubbleRestore();
                SuppressNativeAudioForThisLine();
                return false;
            }
            // A new node supersedes older held nodes on the same bubble.
            if (!_allowOriginalShow && node != null && bubble != null && _instance != null)
                _instance._latestNodeForBubble[bubble.Pointer.ToInt64()] = node.Pointer.ToInt64();
            // Service unreachable, but a cached line still plays.
            bool replacing = ModIntegrations.VoiceReplacementEnabled();
            if (!replacing && !_allowOriginalShow && node != null && bubble != null &&
                _instance != null && CacheReplacementPossible() &&
                !_instance.QueueNode(bubble, node, cachedOnly: true))
            {
                return false;
            }
            if (_allowOriginalShow || !replacing ||
                _instance == null || bubble == null || node == null)
            {
                // Record why native voice was kept.
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    string why =
                        node == null ? "node null" :
                        bubble == null ? "bubble null" :
                        _allowOriginalShow ? "re-show of a line already replaced" :
                        _instance == null ? "coordinator not awake" :
                        !LilithModPlugin.CfgReplaceGameVoice.Value ? "ReplaceGameVoice off" :
                        !VoiceConfig.Enabled ? "voice disabled" :
                        !DialogueTextCatalog.Available ? "no dialogue catalogue in this build" :
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

        /// <summary>Queues replacement audio or allows the native line.</summary>
        /// <param name="cachedOnly">Use only existing cached audio.</param>
        private bool QueueNode(DialogueBubbleUI bubble, DialogueNode node, bool cachedOnly = false)
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
            // Runtime lines need a translated cache before Japanese synthesis.
            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(node.text))
            {
                if (DynamicLineCache.TryGet(node.text, out string learned))
                    text = learned;
                else
                    DynamicLineCache.RequestTranslation(node.text);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                AllowNativeAudioForThisLine();
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Original voice kept for line {node.lineId} (id {node.id}): " +
                        "no catalogue text, so nothing to synthesise.");
                return true;
            }

            // Keep native voice when neither cache nor synthesis is available.
            if (cachedOnly && !LilithModPlugin.VoiceProcessor.IsCached(text, language))
            {
                AllowNativeAudioForThisLine();
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Original voice kept for line {node.lineId} (id {node.id}): " +
                        "synthesis unreachable and no cached audio for this line.");
                return true;
            }

            var cue = new NativeDialogueCue(bubble, node, key);
            _pendingNodes.Add(key);
            // Record the decision for the audio prefixes, which cannot see it.
            SuppressNativeAudioForThisLine();
            LilithModPlugin.VoiceProcessor.Enqueue(new Utterance
            {
                JaText = text,
                EnText = null,
                Language = language,
                SuppressSubtitle = true,
                NativeDialogue = cue
            });
            if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                LilithModPlugin.Logger.LogInfo(
                    $"[Voice] Holding line {node.lineId} until {language} audio is ready" +
                    (cachedOnly ? " (from cache; synthesis not reachable)." : "."));
            // Restore the previous reply while this line waits for audio.
            LlmChatController.RequestReplyBubbleRestore();
            return false;
        }

        private void Update()
        {
            var processor = LilithModPlugin.VoiceProcessor;
            if (processor == null) return;
            while (processor.NativeDialogueQueue.TryDequeue(out NativeDialogueCue cue))
            {
                _pendingNodes.Remove(cue.Key);
                bool stale = cue.Cancelled;
                if (!stale && cue.Bubble != null && cue.Node != null &&
                    _latestNodeForBubble.TryGetValue(cue.Bubble.Pointer.ToInt64(), out long newest) &&
                    newest != cue.Key)
                {
                    stale = true;
                }
                if (stale)
                {
                    // Release cancelled cues without showing or playing them.
                    cue.Cancel();
                    cue.MarkDisplayed();
                    if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    {
                        LilithModPlugin.Logger.LogInfo(
                            $"[Voice] Skipped held line {(cue.Node == null ? -1 : cue.Node.lineId)}; " +
                            $"superseded before its audio was ready. {_pendingNodes.Count} still held.");
                    }
                    continue;
                }
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
                        // Pairs with the diagnostic entry created when the line was held.
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
