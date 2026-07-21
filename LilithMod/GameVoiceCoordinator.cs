using System;
using System.Collections.Generic;
using UnityEngine;

namespace LilithMod
{
    public sealed class GameVoiceCoordinator : MonoBehaviour
    {
        private static GameVoiceCoordinator _instance;
        private static bool _allowOriginalShow;

        /// <summary>
        /// How long the game's own dialogue is held off after the mod speaks. The
        /// mirror of NativeDialogueQuietSeconds, which holds the mod off after the
        /// game speaks - shorter, because her reply is the one the player asked for.
        /// </summary>
        private const float NativeSuppressedAfterModSeconds = 4f;

        private static float _modSpokeAt = -600f;

        /// <summary>
        /// Set when a native line is handed back unreplaced, so its own audio is let
        /// through. The four audio prefixes fire separately from the bubble gate and
        /// cannot tell which line was declined - without this they keep suppressing
        /// on the assumption the mod is about to speak, and the line plays silently.
        /// The window only has to outlast the gap between the bubble and its audio.
        /// </summary>
        private static float _nativeAudioAllowedUntil = -600f;

        internal static bool NativeAudioAllowed => Time.unscaledTime < _nativeAudioAllowedUntil;

        private static void AllowNativeAudioForThisLine()
        {
            _nativeAudioAllowedUntil = Time.unscaledTime + 2f;
        }

        /// <summary>
        /// How long to wait for synthesis to come up before accepting that it is not
        /// going to. Rarely reached: a successful warm-up marks the service available
        /// during Load(), long before any dialogue. This covers the case where the
        /// service genuinely is not up, and overshooting it drops native lines that
        /// should have played.
        /// </summary>
        private const float StartupVoiceGraceSeconds = 15f;

        /// <summary>
        /// Longer when this process started the services itself: the model really is
        /// loading from cold, rather than having been warm since login.
        /// </summary>
        private const float ColdStartVoiceGraceSeconds = 30f;

        private static float VoiceGraceSeconds =>
            ServiceBootstrap.StartedServices ? ColdStartVoiceGraceSeconds : StartupVoiceGraceSeconds;

        /// <summary>
        /// True while synthesis is wanted, has never answered this session, and the
        /// grace window is still open - so a native line arriving now should be
        /// dropped whole rather than played.
        ///
        /// Both the bubble and the audio must read this. They travel by separate
        /// routes (ShowNode here, four AudioManager prefixes in ModIntegrations), and
        /// gating only one produced the game's Chinese voice under no subtitle at all.
        /// </summary>
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
            if (node != null && node.id == 9500000)
            {
                _modSpokeAt = Time.unscaledTime;
                // NativeSuppressedAfterModSeconds has never once fired in a session
                // where the mod plainly spoke, so this assignment is suspected never
                // to run - meaning her reply is not actually protected from the game
                // talking over it. If this line is absent from the log while a mod
                // bubble appears, the reply bubble reaches the screen by some path
                // other than DialogueBubbleUI.ShowNode and the guard is dead.
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    LilithModPlugin.Logger.LogInfo(
                        "[Voice] Mod reply bubble seen; native dialogue held off for " +
                        $"{NativeSuppressedAfterModSeconds:0.#}s.");
                return true;
            }

            // Synthesis is wanted but has never answered this session. Bounded by the
            // grace window so a machine with no synthesis at all still falls back to
            // the native voice, which is the designed behaviour - this covers only
            // "not up YET", never "not installed".
            if (node != null && bubble != null && HoldingForSynthesis)
            {
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Dropped native line {node.lineId} (id {node.id}); " +
                        "synthesis is preferred but has not come up yet.");
                }
                return false;
            }

            // The mod just spoke, so hold the game off for a moment. The other
            // direction already exists - ambient waits 8 s after native dialogue -
            // and without this the game answers over the top of her reply, which is
            // what makes handling her produce two overlapping lines.
            //
            // _allowOriginalShow is excluded: that is a line already synthesised and
            // coming back to be displayed, and dropping it would discard audio that
            // is about to play.
            if (node != null && bubble != null && !_allowOriginalShow &&
                Time.unscaledTime - _modSpokeAt < NativeSuppressedAfterModSeconds)
            {
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Suppressed native line {node.lineId} (id {node.id}); " +
                        "the mod spoke less than " +
                        $"{NativeSuppressedAfterModSeconds:0.#}s ago.");
                }
                // Dropped, not deferred: the game has no queue to hold it in, and a
                // reaction shown four seconds late is worse than one not shown.
                return false;
            }
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
            // No catalogue text for this line, so there is nothing correct to speak.
            // node.text is the game's own string: Chinese for scripted lines, and the
            // UI language - often English - for the ones it builds at runtime with
            // lineId 0. Handing either to the Japanese voice made her read the wrong
            // language aloud, which is what a touch reply in English turned out to be.
            // The original voice is the right answer whenever the catalogue cannot
            // supply the line.
            if (string.IsNullOrWhiteSpace(text))
            {
                AllowNativeAudioForThisLine();
                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                    LilithModPlugin.Logger.LogInfo(
                        $"[Voice] Original voice kept for line {node.lineId} (id {node.id}): " +
                        "no catalogue text, so nothing to synthesise.");
                return true;
            }

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
