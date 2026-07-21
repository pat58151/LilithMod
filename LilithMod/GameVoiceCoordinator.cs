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
        /// Lets a declined line keep its own audio. The audio prefixes fire
        /// separately from the bubble gate and cannot tell which line was declined;
        /// without this they suppress it and the line plays silently.
        /// </summary>
        private static float _nativeAudioAllowedUntil = -600f;

        internal static bool NativeAudioAllowed => Time.unscaledTime < _nativeAudioAllowedUntil;

        private static void AllowNativeAudioForThisLine()
        {
            _nativeAudioAllowedUntil = Time.unscaledTime + 2f;
        }

        /// <summary>
        /// How long to wait for synthesis before accepting it is not coming. Rarely
        /// reached - warm-up marks it available during Load(). Overshooting drops
        /// native lines that should have played.
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
        /// Synthesis wanted, never answered yet, grace still open: drop the line
        /// whole. Bubble and audio must both read this - they travel separately, and
        /// gating one alone gave the game's Chinese voice under no subtitle.
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

        /// <summary>
        /// Newest node per bubble. A held cue that is no longer newest when its audio
        /// arrives is stale - re-showing it put old text over the animation playing
        /// now, which is what rapid touches produced.
        /// </summary>
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
                // This was suspected dead, because no suppression had ever been
                // logged in a session where she plainly spoke. Instrumenting it
                // disproved that: the line fires on every reply, so the guard is
                // live and the jumbled ordering had another cause.
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

            // Hold the game off briefly after she speaks, or it answers over the top
            // of her reply. _allowOriginalShow is excluded: that is a line already
            // synthesised and coming back to be shown.
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
                // reaction shown four seconds late is worse than one not shown. The
                // game already tore the previous bubble down before this gate ran,
                // so if that bubble was her reply, put it back.
                LlmChatController.RequestReplyBubbleRestore();
                return false;
            }
            // Past every drop gate, so this node will reach the screen - either now
            // or held until its audio. It supersedes anything still held on the same
            // bubble. Re-shows are excluded: handing a line back must not mark the
            // line itself stale.
            if (!_allowOriginalShow && node != null && bubble != null && _instance != null)
                _instance._latestNodeForBubble[bubble.Pointer.ToInt64()] = node.Pointer.ToInt64();
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
            // node.text is the game's own string - Chinese for scripted lines, the UI
            // language for runtime ones. Feeding either to the Japanese voice made her
            // read the wrong language aloud, so keep the original voice instead.
            // Runtime lines carry no id, so the Japanese is fetched once and kept:
            // silent the first time, spoken after. These repeat, so it converges fast.
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
            // Holding this line leaves the bubble the game just tore down empty for
            // the whole synthesis wait. If that bubble was her reply mid-playback,
            // restore it; the held line takes the bubble back when its audio lands.
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
                    // Superseded or abandoned while it waited for audio. Not
                    // re-shown - the newer line owns the bubble now. Cancel() keeps
                    // the voice thread from playing its audio, MarkDisplayed()
                    // releases that thread, and the pending entry above is already
                    // cleared, so nothing leaks.
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
