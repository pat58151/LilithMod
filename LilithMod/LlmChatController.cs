using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Utils;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithMod
{
    /// <summary>
    /// Injectable MonoBehaviour that provides free-text LLM chat.
    /// All UI, networking, and dialogue injection are handled here.
    /// </summary>
    // Partial so untracked DevHooks.cs can add local-only hooks; its call sites are
    // partial methods and compile to nothing when that file is absent.
    public partial class LlmChatController : MonoBehaviour
    {
        // ========== Configuration (populated from LilithModPlugin statics) ==========
        private static string BaseUrl => LilithModPlugin.CfgBaseUrl.Value;
        private static string ApiKey => LilithModPlugin.CfgApiKey.Value;
        private static string Model => LilithModPlugin.CfgModel.Value;
        private static int MaxHistoryTurns => LilithModPlugin.CfgMaxHistoryTurns.Value;
        private static int TimeoutSeconds => LilithModPlugin.CfgTimeoutSeconds.Value;
        private static string Hotkey => LilithModPlugin.CfgHotkey.Value;

        // ========== Required Il2Cpp constructor ==========
        public LlmChatController(System.IntPtr ptr) : base(ptr) { }

        // ========== Internal state ==========
        private bool _chatDisabled;
        private bool _hotkeyValid;
        private bool _uiReady;
        private float _initElapsed;
        private int _vkHotkey = -1;
        private const int VkEscape = 0x1B;
        private const int VkReturn = 0x0D;
        private const int VkNumpadEnter = 0x0D; // Win32 does not separate numpad Enter
        private const float InitTimeoutSeconds = 60f;
        private const float InputFontSize = 18f;
        // Proportions of the game's dialogue bar: wide and thin, not a chat rectangle.
        private const float PanelWidth = 640f;
        private const float PanelHeight = 44f;
        private const float LeftMarginFraction = 0.02f;   // text starts here
        private const float RightMarginFraction = 0.05f;  // text is cut off here
        private const float VerticalPadding = 4f;

        private RectTransform _textAreaRect;
        private RectTransform _inputTextRect;
        private RectTransform _panelRect;
        private Image _panelImage;

        private GameObject _canvas;
        private CanvasGroup _canvasGroup;
        private TMP_InputField _inputField;
        private TextMeshProUGUI _placeholderText;
        private TextMeshProUGUI _inputText;

        private DialogueNode _replyNode;
        private List<Message> _history;
        private ConcurrentQueue<ChatResult> _replyQueue = new ConcurrentQueue<ChatResult>();

        private CancellationTokenSource _cts;
        private Task _currentRequest;
        private HttpClient _httpClient;
        private LiveInformationService _liveInformation;
        private readonly ConcurrentQueue<string> _letterQueue = new ConcurrentQueue<string>();
        private static readonly ConcurrentQueue<string> InteractionQueue = new ConcurrentQueue<string>();
        private float _nextAmbientAt;
        private string _pendingInteraction;
        private float _interactionReplyAt;
        private bool _letterInFlight;
        private string _speechCommandPath;
        private string _pushToTalkTriggerPath;
        private readonly Queue<string> _speechCommandQueue = new Queue<string>();
        private float _nextSpeechPoll;
        private string _configuredHotkey;
        private string _configuredPushToTalkKey;
        private int _vkPushToTalk = -1;
        private bool _speechListening;
        private string _lastAppliedPartial;
        private bool _userTypedWhileListening;
        private string _pendingSpeechCommand;
        private bool _speechAwaitingReply;
        private float _speechSubmitAt;
        private const string SpeechPartialMarker = "__LILITH_PTT_PARTIAL__";
        private float _replyHideAt;
        private bool _replyPlaybackActive;

        // ========== Fallback lines ==========
        private static readonly string[] FallbackLines = new[]
        {
            "...Sorry, it feels like I can't reach you right now.",
            "The words won't come. Some kind of interference.",
            "I'm trying to speak, but something's blocking me.",
            "I can hear you, but I can't respond properly. Give me a moment.",
            "Something's wrong with the connection. Just... stay with me.",
            "It's like my voice is lost in static. I'm sorry.",
            "I can't find the words. Maybe we can try again in a bit."
        };

        // ========== Unity lifecycle ==========
        /// <summary>
        /// The live controller, for static callers needing its HTTP client and key.
        /// </summary>
        private static LlmChatController _instance;

        private void Awake()
        {
            _instance = this;
            ApplyConfiguredHotkey(Hotkey, true);

            // UI construction is deliberately NOT done here. Awake() runs when BepInEx
            // attaches the component, which is before the game's first scene exists - so
            // there is no EventSystem yet and building the UI now would disable chat
            // permanently. Update() retries until the scene is up. Same failure mode as
            // creating a GameObject in BasePlugin.Load().

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(TimeoutSeconds + 10); // a bit beyond our cancellation timeout
            _liveInformation = new LiveInformationService();
            _ = _liveInformation.InitializeAsync();
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _speechCommandPath = Path.Combine(pluginDir, "speech-command.txt");
            _pushToTalkTriggerPath = Path.Combine(pluginDir, "push-to-talk.active");
            SpeechInputService.Initialize();
            // A trigger left behind by a crash would make the listener record forever.
            ClearPushToTalkTrigger();

            // Initialise conversation.
            _history = new List<Message>
            {
                new Message { Role = "system", Content = BuildSystemPrompt() }
            };
            ScheduleNextAmbient();
        }

        private void Update()
        {
            ApplyConfiguredHotkey(Hotkey, false);
            if (_chatDisabled || !_hotkeyValid)
                return;

            if (!_uiReady)
            {
                TryDeferredInit();
                return;
            }

            // --- Hotkey toggle ---
            // Win32 GetAsyncKeyState, NOT Unity: the pet window is WS_EX_NOACTIVATE, so it
            // never receives key messages and Unity input is permanently silent here.
            // Polled exactly once - IsKeyDown consumes the transition.
            bool hotkeyPressed = !SettingsBridge.CapturingChatKey &&
                                 _vkHotkey > 0 && WindowFocus.IsKeyDown(_vkHotkey);

            // No key, no chat. The box would open and every send would fail, so the
            // hotkey is inert rather than misleading - the greyed settings row and
            // this warning say why.
            if (hotkeyPressed && !HasApiKey)
            {
                WarnMissingApiKey();
                hotkeyPressed = false;
            }

            bool toggle = hotkeyPressed;

            if (toggle)
            {
                TogglePanel();
                if (LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    LilithModPlugin.Logger.LogInfo(
                        $"[LlmChat] Toggled. visible={IsPanelVisible()} "
                        + $"canvas={(_canvas != null)} field={(_inputField != null)}");
                }
            }

            // Escape closes, Enter submits. Once the panel is open the window has been made
            // focusable, so Unity input works again - but these are polled globally too so
            // they behave identically whether or not focus actually landed.
            if (IsPanelVisible() && WindowFocus.IsKeyDown(VkEscape))
            {
                if (_speechListening) StopListening(false);
                HidePanel();
            }

            if (IsPanelVisible()
                && (WindowFocus.IsKeyDown(VkReturn) || WindowFocus.IsKeyDown(VkNumpadEnter)))
            {
                OnPlayerSubmit(_inputField != null ? _inputField.text : null);
            }

            // Clicking the box re-focuses it. Detected through Win32 rather than uGUI
            // pointer events, because once the window has lost focus Unity receives no
            // input at all - the very situation this needs to recover from.
            if (IsPanelVisible() && PointerFocus.ClickedInside(_panelRect))
                FocusInputField();

            if (IsPanelVisible())
                ScrollInputText();

            // --- Drain reply queue (main thread only) ---
            while (_replyQueue.TryDequeue(out ChatResult result))
                HandleChatResult(result);

            while (_letterQueue.TryDequeue(out string letter))
                SaveLetter(letter);

            PollNoteTestFile();

            DrainInteractions();
            TrySendQueuedUserMessage();
            TryInteractionReply();
            PollPushToTalkKey();
            PollSpeechCommand();
            TrySubmitSpeechCommand();
            TryAmbientRemark();

            DrainVoiceQueues();
            TryHideReplyBubble();
        }

        private void ApplyConfiguredHotkey(string value, bool initial)
        {
            string name = value?.Trim();
            if (!initial && string.Equals(name, _configuredHotkey, StringComparison.OrdinalIgnoreCase)) return;
            int key = WindowFocus.VirtualKeyFromName(name);
            if (key <= 0)
            {
                if (initial)
                {
                    name = "F7";
                    key = WindowFocus.VirtualKeyFromName(name);
                    LilithModPlugin.Logger.LogWarning("[LlmChat] Invalid chat key; using F7.");
                }
                else return;
            }
            _configuredHotkey = name;
            _vkHotkey = key;
            _hotkeyValid = true;
        }

        /// <summary>
        /// Moves subtitles and voice failures from the voice thread onto the main
        /// thread, where Unity APIs may actually be called.
        /// </summary>
        private void DrainVoiceQueues()
        {
            var processor = LilithModPlugin.VoiceProcessor;
            if (processor == null)
                return;

            // If synthesis gave up, show the whole reply once and drop the rest of
            // the per-sentence subtitles - with no audio there is nothing pacing
            // them, so they would otherwise flash past within a frame or two.
            bool failed = false;
            while (processor.VoiceFailureQueue.TryDequeue(out _))
                failed = true;

            if (failed)
            {
                _replyPlaybackActive = false;
                while (processor.SubtitleQueue.TryDequeue(out SubtitleCue cue))
                {
                    cue.MarkDisplayed();
                }
                if (_currentReplyEnglish != null && _currentReplyEnglish.Count > 0)
                {
                    DisplayReplyText(string.Join(" ", _currentReplyEnglish));
                    _currentReplyEnglish = null;
                    _replyHideAt = Time.unscaledTime + 6f;
                }
                return;
            }

            while (processor.ReplyFinishedQueue.TryDequeue(out _))
            {
                _replyPlaybackActive = false;
                _speechEndedAt = Time.unscaledTime;
                _replyHideAt = Time.unscaledTime + 1.0f;
            }

            // Before the subtitle drain, so a sentence arriving this frame wins over
            // the restored one.
            if (_restoreReplyBubble)
            {
                _restoreReplyBubble = false;
                if (_replyPlaybackActive && !string.IsNullOrEmpty(_lastDisplayedSentence))
                {
                    DisplayReplyText(_lastDisplayedSentence);
                    LilithModPlugin.Logger.LogInfo(
                        "[LlmChat] Reply bubble restored after a native dialogue displaced it mid-reply.");
                }
            }

            while (processor.SubtitleQueue.TryDequeue(out SubtitleCue cue))
            {
                DisplayReplyText(cue.Text);
                cue.MarkDisplayed();
            }
        }

        /// <summary>
        /// Puts text in the bubble. Assigning node.text alone does not refresh a
        /// dialogue that is already on screen; StartDialogue is what the game reacts
        /// to, and is the path the reply display has always used.
        /// </summary>
        private void DisplayReplyText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (!DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
            {
                LilithModPlugin.Logger.LogError(
                    "[LlmChat] Reply node 9500000 disappeared. Cannot display reply.");
                return;
            }

            _replyNode.text = text;
            _lastDisplayedSentence = text;
            DialogueManager.s_instance.StartDialogue(9500000);
            _replyHideAt = 0f;
        }

        private string _lastDisplayedSentence;
        private bool _restoreReplyBubble;

        /// <summary>
        /// The game's own dialogue start tears the reply bubble down before the mod's
        /// gate can decline the line, so a touch mid-reply leaves her voice playing
        /// under an empty bubble. The coordinator requests a restore; it runs on the
        /// next Update rather than inside the ShowNode prefix, because a nested
        /// StartDialogue from inside the game's own StartDialogue corrupts the
        /// manager's current-dialogue state.
        /// </summary>
        internal static void RequestReplyBubbleRestore()
        {
            if (_instance != null)
                _instance._restoreReplyBubble = true;
        }

        private void TryHideReplyBubble()
        {
            if (_replyHideAt <= 0f || Time.unscaledTime < _replyHideAt) return;
            _replyHideAt = 0f;
            try
            {
                var manager = DialogueManager.s_instance;
                if (manager != null && manager.IsDialogueActive && manager.CurrentNode != null &&
                    manager.CurrentNode.id == 9500000)
                    manager.ForceEndDialogue();
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[LlmChat] Could not hide reply bubble: " + ex.Message);
            }
        }

        // Keeps the END of the line visible. TMP_InputField's own horizontal scrolling does
        // not engage on a hand-built hierarchy, so the line is slid left by however much it
        // overruns the viewport: the head passes under the RectMask2D and out of sight while
        // freshly typed text stays at the right edge.
        private void ScrollInputText()
        {
            if (_inputText == null || _inputTextRect == null || _textAreaRect == null)
                return;

            float viewportWidth = _textAreaRect.rect.width;
            float textWidth = _inputText.preferredWidth;
            float overrun = textWidth - viewportWidth;

            // While the line still fits, keep it centred so short messages grow outward from
            // the middle. Once it overruns, pin the right edge to the viewport and let the
            // head slide out under the mask.
            float x = overrun > 0f
                ? -overrun
                : (viewportWidth - textWidth) * 0.5f;
            var pos = _inputTextRect.anchoredPosition;
            if (!Mathf.Approximately(pos.x, x))
                _inputTextRect.anchoredPosition = new Vector2(x, pos.y);

            // Width must track the content, otherwise the glyphs beyond the original rect
            // width are never laid out and the tail simply stops rendering.
            float w = Mathf.Max(viewportWidth, textWidth);
            if (!Mathf.Approximately(_inputTextRect.sizeDelta.x, w))
                _inputTextRect.sizeDelta = new Vector2(w, _inputTextRect.sizeDelta.y);
        }

        // TMP inherits the font asset's default point size, far too large here. Pin it
        // and keep one row, so long input scrolls sideways instead of growing.
        // TMP's default RectTransform is small and centred, not filling its parent;
        // left alone the field cannot scroll and clips the tail instead of the head.
        private static void StretchToParent(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
        }

        private static void ApplyTextMetrics(TextMeshProUGUI t, bool centred)
        {
            if (t == null) return;
            t.enableAutoSizing = false;
            t.fontSize = InputFontSize;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            // The placeholder is centred as a prompt; the typed line is left-aligned so it
            // grows rightwards and can be scrolled.
            t.alignment = centred ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
        }

        // Waits for the game's scene to come up, then builds the UI once. Gives up after a
        // timeout so a broken scene does not mean polling forever.
        private void TryDeferredInit()
        {
            _initElapsed += Time.deltaTime;

            var eventSystem = UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
            if (eventSystem == null)
            {
                if (_initElapsed >= InitTimeoutSeconds)
                {
                    LilithModPlugin.Logger.LogError(
                        "[LlmChat] No EventSystem appeared within "
                        + InitTimeoutSeconds + "s. LLM chat disabled.");
                    _chatDisabled = true;
                }
                return;
            }

            try
            {
                BuildChatUILayout();
                if (_chatDisabled)
                    return;

                MonoBehaviourExtensions.StartCoroutine(this, FinishUISetup());
                MonoBehaviourExtensions.StartCoroutine(this, EnsureDefaultNode());
                _uiReady = true;
                LilithModPlugin.Logger.LogInfo(
                    $"[LlmChat] Ready after {_initElapsed:F1}s. Press '{Hotkey}' to chat.");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogError($"[LlmChat] Initialisation failed: {ex}");
                _chatDisabled = true;
            }
        }

        private void OnDestroy()
        {
            // Safety net: if the component dies while the panel is open, the window would
            // otherwise be left non-click-through for the rest of the session.
            WindowFocus.RestoreWindow();
            CancelCurrentRequest();
            // CancelCurrentRequest already nulled the field; this guard covers the case
            // where the owning task disposed it first.
            try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
            _httpClient?.Dispose();
            _liveInformation?.Dispose();
        }

        // ========== UI construction ==========
        private void BuildChatUILayout()
        {
            // Verify an EventSystem exists.
            var eventSystem = UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
            if (eventSystem == null)
            {
                LilithModPlugin.Logger.LogError("[LlmChat] No EventSystem found. LLM chat disabled.");
                _chatDisabled = true;
                return;
            }

            _canvas = new GameObject("LlmChatCanvas");
            DontDestroyOnLoad(_canvas);
            var canvasComp = _canvas.AddComponent<Canvas>();
            canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasComp.sortingOrder = 100;

            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            _canvas.AddComponent<GraphicRaycaster>();
            _canvasGroup = _canvas.AddComponent<CanvasGroup>();

            var inputPanel = new GameObject("InputPanel");
            inputPanel.transform.SetParent(_canvas.transform, false);
            var panelRect = inputPanel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0f);
            panelRect.anchorMax = new Vector2(0.5f, 0f);
            panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0, 20);
            panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _panelRect = panelRect;
            _panelImage = inputPanel.AddComponent<Image>();
            var panelImg = _panelImage;
            panelImg.color = new Color(0, 0, 0, 0.8f);

            // InputField GameObject (component added later after font).
            var inputFieldGo = new GameObject("InputField");
            inputFieldGo.transform.SetParent(inputPanel.transform, false);
            var inputFieldRect = inputFieldGo.AddComponent<RectTransform>();
            inputFieldRect.anchorMin = Vector2.zero;
            inputFieldRect.anchorMax = Vector2.one;
            inputFieldRect.offsetMin = Vector2.zero;
            inputFieldRect.offsetMax = Vector2.zero;

            var textArea = new GameObject("Text Area");
            textArea.transform.SetParent(inputFieldGo.transform, false);
            var textAreaRect = textArea.AddComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            // Margins as a fraction of panel width so they hold if the panel is resized.
            // The right margin is deliberately wider than the left: it is the point where
            // text is cut off, and a 1% cut sits so close to the border it reads as the
            // text touching the edge.
            float leftInset = PanelWidth * LeftMarginFraction;
            float rightInset = PanelWidth * RightMarginFraction;
            textAreaRect.offsetMin = new Vector2(leftInset, VerticalPadding);
            textAreaRect.offsetMax = new Vector2(-rightInset, -VerticalPadding);

            // Clip the text to the box. Without this the line simply draws past the panel
            // border once it is too long. A real TMP_InputField prefab carries this on its
            // Text Area; with it in place the field scrolls to keep the caret visible, so
            // leading characters slide out of view instead of overflowing.
            textArea.AddComponent<RectMask2D>();
            _textAreaRect = textAreaRect;

            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(textArea.transform, false);
            StretchToParent(placeholderGo);
            _placeholderText = placeholderGo.AddComponent<TextMeshProUGUI>();
            _placeholderText.text = "Say something···";
            _placeholderText.fontStyle = FontStyles.Italic;
            _placeholderText.color = Color.grey;
            ApplyTextMetrics(_placeholderText, true);

            // The input line is anchored to the LEFT edge with a left pivot, not stretched.
            // A stretched rect cannot be slid sideways, and sliding it is how the head of a
            // long line is moved out of view (see ScrollInputText).
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(textArea.transform, false);
            _inputTextRect = textGo.AddComponent<RectTransform>();
            _inputTextRect.anchorMin = new Vector2(0f, 0f);
            _inputTextRect.anchorMax = new Vector2(0f, 1f);
            _inputTextRect.pivot = new Vector2(0f, 0.5f);
            _inputTextRect.offsetMin = new Vector2(0f, 0f);
            _inputTextRect.offsetMax = new Vector2(0f, 0f);
            _inputText = textGo.AddComponent<TextMeshProUGUI>();
            _inputText.color = Color.white;
            ApplyTextMetrics(_inputText, false);

            // Initially hidden.
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
        }

        private IEnumerator FinishUISetup()
        {
            yield return null; // one frame to let game UI spawn

            // Pick a font asset directly rather than via FindObjectOfType<TextMeshProUGUI>,
            // which returns OUR OWN freshly-built text first and yields TMP's default
            // LiberationSans - a Latin-only font that renders Chinese as blank boxes.
            // The game ships CJK fonts (QingSongShouXieTi2-2, TEGUSE_Kanaka); prefer any
            // non-Liberation asset and fall back to whatever exists.
            TMP_FontAsset chosen = null;
            TMP_FontAsset fallback = null;
            var fonts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>());
            for (int i = 0; i < fonts.Length; i++)
            {
                var fa = fonts[i].TryCast<TMP_FontAsset>();
                if (fa == null) continue;
                if (fallback == null) fallback = fa;
                if (fa.name != null && fa.name.IndexOf("Liberation", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    chosen = fa;
                    break;
                }
            }
            if (chosen == null) chosen = fallback;

            if (chosen == null)
            {
                LilithModPlugin.Logger.LogError("[LlmChat] Failed to obtain a game font. LLM chat disabled.");
                Destroy(_canvas);
                _chatDisabled = true;
                yield break;
            }

            LilithModPlugin.Logger.LogInfo($"[LlmChat] Font acquired: '{chosen.name}'.");

            _placeholderText.font = chosen;
            _inputText.font = chosen;

            // Re-apply after the font swap: assigning a TMP_FontAsset can pull in that
            // asset's own default point size and undo the sizing set at construction.
            ApplyTextMetrics(_placeholderText, true);
            ApplyTextMetrics(_inputText, false);

            // Now add TMP_InputField and wire it up.
            var inputFieldGo = _placeholderText.transform.parent.parent.gameObject; // "Text Area" -> "InputField"
            var inputField = inputFieldGo.AddComponent<TMP_InputField>();
            // No LINQ here: GetComponentsInChildren returns an Il2CppArrayBase, which does
            // not carry the System.Linq extension methods.
            RectTransform textArea = null;
            var rects = inputFieldGo.GetComponentsInChildren<RectTransform>();
            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i] != null && rects[i].name == "Text Area")
                {
                    textArea = rects[i];
                    break;
                }
            }
            inputField.textViewport = textArea;
            inputField.textComponent = _inputText;
            inputField.placeholder = _placeholderText;
            inputField.contentType = TMP_InputField.ContentType.Standard;
            inputField.lineType = TMP_InputField.LineType.SingleLine;
            inputField.characterLimit = 256;

            // Submission is handled by polling the Enter key in Update() rather than
            // subscribing to onSubmit. Attaching a managed method to an Il2Cpp UnityEvent
            // requires delegate marshalling that is fragile across interop regenerations;
            // polling a key we already read each frame avoids that entirely.
            _inputField = inputField;

            // Adopt the look of one of the game's own input fields. Done last so it can see
            // the finished field, and it deliberately leaves layout/scrolling settings alone.
            GameStyle.Apply(_panelImage, _inputText, _placeholderText,
                            _canvas != null ? _canvas.transform : null);

            // Layout metrics must win over anything the donor styling brought with it.
            ApplyTextMetrics(_placeholderText, true);
            ApplyTextMetrics(_inputText, false);

            LilithModPlugin.Logger.LogInfo("[LlmChat] Input field constructed and wired.");
        }

        // ========== Node injection ==========
        private IEnumerator EnsureDefaultNode()
        {
            float timeout = Time.time + 30f;
            while (DialogueManager.s_instance == null)
            {
                if (Time.time > timeout)
                {
                    LilithModPlugin.Logger.LogError("[LlmChat] DialogueManager never appeared. LLM chat disabled.");
                    _chatDisabled = true;
                    yield break;
                }
                yield return null;
            }

            yield return null; // let Awake/OnEnable settle

            if (DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
            {
                LilithModPlugin.Logger.LogInfo("[LlmChat] LLM reply node already exists.");
                yield break;
            }

            // Locate target database.
            DialogueDatabase targetDb = null;
            foreach (var db in DialogueManager.s_instance._databases)
            {
                if (db.databaseName == "DialogueNode")
                {
                    targetDb = db;
                    break;
                }
            }
            if (targetDb == null && DialogueManager.s_instance._databases.Length > 0)
                targetDb = DialogueManager.s_instance._databases[0];

            if (targetDb == null)
            {
                LilithModPlugin.Logger.LogError("[LlmChat] Could not find a suitable DialogueDatabase. LLM chat disabled.");
                _chatDisabled = true;
                yield break;
            }

            // Create Il2Cpp lists.
            var triggerTypes = new Il2CppSystem.Collections.Generic.List<DialogueTriggerType>();
            var playerStates = new Il2CppSystem.Collections.Generic.List<string>();
            var options = new Il2CppSystem.Collections.Generic.List<DialogueOption>();
            var playerLineOptions = new Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption>();

            var newNode = new DialogueNode
            {
                id = 9500000,
                speaker = "lilith",
                lineId = 0,
                text = "",
                emotion = "",
                duration = 5.0f,
                actionType = (LilithActionType)(-1),
                nextStateType = (DialogueStateType)0,
                nextStateDuration = 0f,
                soundId = "",
                nextId = -1,
                playerLineInteraction = "",
                triggerTypes = triggerTypes,
                playerStates = playerStates,
                options = options,
                playerLineOptions = playerLineOptions,
                conditions = new DialogueCondition { timeRangeStart = "00:00", timeRangeEnd = "23:59", dateMMdd = "" },
                baseWeight = 1
            };

            targetDb.nodes.Add(newNode);
            targetDb.BuildIndex();
            DialogueManager.s_instance.BuildIndex();
            DialogueManager.s_instance.RegisterNodeWeight(newNode);

            if (!DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
            {
                LilithModPlugin.Logger.LogError("[LlmChat] Failed to retrieve newly created node 9500000. LLM chat disabled.");
                _chatDisabled = true;
            }
        }

        // ========== Panel visibility ==========
        private bool IsPanelVisible()
        {
            return _canvasGroup != null && _canvasGroup.alpha > 0.5f;
        }

        private void TogglePanel()
        {
            if (IsPanelVisible())
            {
                // Closing the box also abandons any speech in flight. Otherwise the
                // microphone stays open behind a hidden panel and the transcript
                // re-opens it a moment later, which reads as the chat key opening
                // the speech bar by itself.
                if (_speechListening) StopListening(false);
                _pendingSpeechCommand = null;
                _speechCommandQueue.Clear();
                HidePanel();
            }
            else
            {
                ShowPanel();
            }
        }

        private void ShowPanel()
        {
            if (_canvasGroup == null) return;

            _canvasGroup.alpha = 1;
            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
            if (_inputField != null)
                _inputField.text = "";

            FocusInputField();
        }

        // Re-takes OS focus and re-arms the text field. Used both when opening the panel and
        // when the user clicks back onto it after focus went elsewhere.
        private void FocusInputField()
        {
            // Strip WS_EX_NOACTIVATE/TRANSPARENT and foreground the window so keystrokes
            // (and IME composition) actually reach the input field. Reverted in HidePanel.
            WindowFocus.EnableTyping();

            if (_inputField == null) return;

            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
                es.SetSelectedGameObject(_inputField.gameObject);

            _inputField.ActivateInputField();
            _inputField.caretPosition = _inputField.text != null ? _inputField.text.Length : 0;
        }

        private void HidePanel()
        {
            if (_canvasGroup == null) return;
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _inputField?.DeactivateInputField();
            if (_inputField != null) _inputField.text = "";

            // Must always run: leaving the style modified permanently breaks the pet's
            // click-through behaviour.
            WindowFocus.RestoreWindow();
        }

        // ========== Input submit ==========
        private void OnPlayerSubmit(string text)
        {
            string trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return; // keep panel open

            _pendingSpeechCommand = null;
            // Submitting by hand ends the utterance too, or the microphone would stay
            // open behind the reply and land a stray transcript on top of it.
            if (_speechListening) StopListening(false);
            HidePanel();
            bool nativeActionHandled = TryApplyImmediateNativeAction(trimmed);

            // Held until she stops talking. Sending straight away cancelled the rest
            // of her current reply mid-word, which reads as a fault rather than as
            // being interrupted. A later message replaces an earlier waiting one:
            // the newest is what the player still means.
            if (SpeechStillFinishing)
            {
                _pendingUserMessage = trimmed;
                _pendingUserMessageNativeAction = nativeActionHandled;
                _pendingUserMessageAt = Time.unscaledTime;
                return;
            }

            SendUserMessage(trimmed, false, nativeActionHandled);
        }

        private void TrySendQueuedUserMessage()
        {
            if (string.IsNullOrEmpty(_pendingUserMessage)) return;
            if (SpeechStillFinishing &&
                Time.unscaledTime - _pendingUserMessageAt < QueuedMessageMaxWaitSeconds)
                return;

            string message = _pendingUserMessage;
            bool nativeActionHandled = _pendingUserMessageNativeAction;
            _pendingUserMessage = null;
            _pendingUserMessageNativeAction = false;
            SendUserMessage(message, false, nativeActionHandled);
        }

        private static void ApplyImmediateNativeCancellation(string text)
        {
            string value = text?.ToLowerInvariant() ?? string.Empty;
            bool cancel = value.Contains("cancel") || value.Contains("stop") ||
                value.Contains("turn off") || value.Contains("dismiss") ||
                value.Contains("never mind") || value.Contains("ยกเลิก") ||
                value.Contains("キャンセル") || value.Contains("止め") ||
                value.Contains("取消") || value.Contains("关闭");
            if (!cancel) return;

            try
            {
                bool timerNamed = value.Contains("timer") || value.Contains("タイマー") || value.Contains("计时");
                bool alarmNamed = value.Contains("alarm") || value.Contains("アラーム") || value.Contains("闹钟");
                // Speech recognition often returns only "cancel it". With no named
                // target, cancelling both native schedulers is the safe interpretation.
                if (timerNamed || !alarmNamed)
                {
                    TimerSystem.Instance?.Cancel();
                    LilithModPlugin.Logger.LogInfo("[NativeAction] Timer cancelled immediately.");
                }
                if (alarmNamed || !timerNamed)
                {
                    AlarmSystem.CancelAlarm();
                    LilithModPlugin.Logger.LogInfo("[NativeAction] Alarm cancelled immediately.");
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[NativeAction] Immediate cancellation failed: " + ex.Message);
            }
        }

        private static bool TryApplyImmediateNativeAction(string text)
        {
            string value = text?.ToLowerInvariant() ?? string.Empty;

            // App launch is checked first, before the timer/alarm parsing, so a phrase
            // like "open steam" fires immediately and never falls through to the alarm
            // clock parser. Only allowed names launch; anything else is left to the LLM,
            // which omits the action and lets her decline in words. Gated by config.
            if (LilithModPlugin.CfgAllowOpenApps != null && LilithModPlugin.CfgAllowOpenApps.Value)
            {
                // Browser searches are launches, not information requests: this opens
                // an encoded Google URL and never downloads or reads the result page.
                Match searchMatch = Regex.Match(text.Trim(),
                    @"^(?:lilith[\s,.!~]+)?(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?(?:(?:open\s+(?:google|(?:the\s+)?browser)\s+and\s+)?search(?:\s+(?:google|the\s+web))?(?:\s+for)?|google)\s+(?<query>.+?)(?:\s+on\s+google)?[\s.!?~]*$",
                    RegexOptions.IgnoreCase);
                if (searchMatch.Success)
                {
                    string query = searchMatch.Groups["query"].Value.Trim();
                    if (AppLauncher.GateOpen && AppLauncher.TrySearch(query))
                    {
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Browser search opened.");
                        return true;
                    }
                }

                // Lazy name + trailing punctuation class: a spoken transcript arrives as
                // "Open Discord." and the period must not become part of the name.
                Match appMatch = Regex.Match(value.Trim(),
                    @"^(?:lilith[\s,.!~]+)?(?:please\s+)?(?:open|launch|start)\s+(?:the\s+)?(?<app>[a-z0-9_.\-]+?)[\s.!?~]*$",
                    RegexOptions.IgnoreCase);
                if (appMatch.Success)
                {
                    string appName = appMatch.Groups["app"].Value.Trim().ToLowerInvariant();
                    if (AppLauncher.GateOpen && AppLauncher.GetAllowedNames().Contains(appName) && AppLauncher.TryOpen(appName))
                    {
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Local app launch for '" + appName + "'.");
                        return true;
                    }
                }
            }

            bool timerNamed = value.Contains("timer") || value.Contains("タイマー") || value.Contains("计时");
            bool alarmNamed = value.Contains("alarm") || value.Contains("アラーム") || value.Contains("闹钟");
            bool cancel = value.Contains("cancel") || value.Contains("stop") ||
                value.Contains("turn off") || value.Contains("dismiss") || value.Contains("never mind") ||
                value.Contains("キャンセル") || value.Contains("止め") || value.Contains("取消") || value.Contains("关闭");
            try
            {
                bool unnamedCancel = Regex.IsMatch(value,
                    @"^\s*(?:please\s+)?(?:cancel|stop|dismiss|turn\s+off)(?:\s+(?:it|that|this))?[\s.!?]*$") ||
                    value.Trim() == "never mind";
                if (cancel && (timerNamed || alarmNamed || unnamedCancel))
                {
                    if (timerNamed || !alarmNamed)
                    {
                        TimerSystem.Instance?.Cancel();
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Timer cancelled immediately.");
                    }
                    if (alarmNamed || !timerNamed)
                    {
                        AlarmSystem.CancelAlarm();
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Alarm cancelled immediately.");
                    }
                    return true;
                }

                if (TryParseDuration(value, out double seconds))
                {
                    if (timerNamed)
                    {
                        TimerSystem timer = TimerSystem.Instance;
                        if (timer == null) return false;
                        timer.StartCountdown((float)seconds, false);
                        LilithModPlugin.Logger.LogInfo($"[NativeAction] Timer started locally for {seconds:0} seconds.");
                        return true;
                    }
                    if (alarmNamed)
                    {
                        DateTime alarm = DateTime.Now.AddSeconds(seconds);
                        AlarmSystem.SetAlarm(new Il2CppSystem.DateTime(alarm.Ticks));
                        LilithModPlugin.Logger.LogInfo($"[NativeAction] Alarm set locally for {alarm:yyyy-MM-dd HH:mm:ss}.");
                        return true;
                    }
                }

                if (alarmNamed && TryParseAlarmClock(value, out DateTime alarmTime))
                {
                    AlarmSystem.SetAlarm(new Il2CppSystem.DateTime(alarmTime.Ticks));
                    LilithModPlugin.Logger.LogInfo($"[NativeAction] Alarm set locally for {alarmTime:yyyy-MM-dd HH:mm:ss}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[NativeAction] Immediate action failed: " + ex.Message);
            }
            return false;
        }

        private static bool TryParseDuration(string value, out double seconds)
        {
            seconds = 0;
            string normalized = Regex.Replace(value,
                @"\b(one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty|forty-five|sixty)\b",
                match => WordNumber(match.Value).ToString(CultureInfo.InvariantCulture));
            Match duration = Regex.Match(normalized,
                @"(?<n>\d+(?:\.\d+)?)\s*(?<u>seconds?|secs?|minutes?|mins?|hours?|hrs?|秒|分間?|時間)",
                RegexOptions.IgnoreCase);
            if (!duration.Success || !double.TryParse(duration.Groups["n"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out double amount)) return false;
            string unit = duration.Groups["u"].Value.ToLowerInvariant();
            seconds = unit.StartsWith("h") || unit == "時間" ? amount * 3600 :
                unit.StartsWith("m") || unit.StartsWith("分") ? amount * 60 : amount;
            return seconds >= 1 && seconds <= 7 * 24 * 60 * 60;
        }

        private static int WordNumber(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "one": return 1; case "two": return 2; case "three": return 3;
                case "four": return 4; case "five": return 5; case "six": return 6;
                case "seven": return 7; case "eight": return 8; case "nine": return 9;
                case "ten": return 10; case "fifteen": return 15; case "twenty": return 20;
                case "thirty": return 30; case "forty": return 40; case "forty-five": return 45;
                case "sixty": return 60; default: return 0;
            }
        }

        private static bool TryParseAlarmClock(string value, out DateTime alarm)
        {
            alarm = default;
            Match clock = Regex.Match(value,
                @"\b(?:at|for)\s+(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>a\.?m\.?|p\.?m\.?)?\b",
                RegexOptions.IgnoreCase);
            if (!clock.Success || !int.TryParse(clock.Groups["h"].Value, out int hour)) return false;
            int minute = 0;
            if (clock.Groups["m"].Success && !int.TryParse(clock.Groups["m"].Value, out minute)) return false;
            string ampm = clock.Groups["ampm"].Value.ToLowerInvariant();
            if (ampm.StartsWith("p") && hour < 12) hour += 12;
            if (ampm.StartsWith("a") && hour == 12) hour = 0;
            if (hour > 23 || minute > 59) return false;
            DateTime now = DateTime.Now;
            alarm = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Local);
            if (value.Contains("tomorrow")) alarm = alarm.AddDays(1);
            else if (alarm <= now) alarm = alarm.AddDays(1);
            return true;
        }

        internal static void RecordInteraction(string kind)
        {
            if (!string.IsNullOrWhiteSpace(kind)) InteractionQueue.Enqueue(kind);
        }

        private void SendUserMessage(string userInput, bool ambient, bool nativeActionHandled = false)
        {
            // Cancel previous request.
            CancelCurrentRequest();

            // Create new CTS with timeout.
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            var token = _cts.Token;
            var capturedCts = _cts; // for disposal in task

            // Append user message.
            lock (_history)
            {
                _history[0].Content = BuildSystemPrompt();
                if (!ambient) _history.Add(new Message { Role = "user", Content = userInput });
            }

            // Clone history for the request (thread-safe copy).
            List<Message> messagesSnapshot;
            lock (_history) messagesSnapshot = new List<Message>(_history);
            string requestPersona = messagesSnapshot[0].Content;

            // Talking resets the idle clock: a remark seconds after she answered reads
            // as talking to herself. Only the ambient schedule - _lastSpontaneousAt also
            // gates interaction replies, and muting those would read as ignoring you.
            if (!ambient) ScheduleNextAmbient();

            // The log carries no timestamps, so "it feels slower than it used to" has
            // never been checkable. Measured from the moment the message is accepted,
            // which is what the player actually waits through.
            _replyStartedAt = Time.unscaledTime;

            _currentRequest = Task.Run(async () =>
            {
                try
                {
                    if (!nativeActionHandled && NeedsLiveInformation(userInput) && !ambient)
                    {
                        string liveContext = await _liveInformation.BuildContextAsync(userInput, token);
                        messagesSnapshot[0] = new Message
                        {
                            Role = "system",
                            Content = requestPersona + "\n\n" + liveContext
                        };
                    }
                    string reply = await RequestCompletionAsync(
                        messagesSnapshot, ambient ? userInput : null, token);
                    if (!token.IsCancellationRequested)
                    {
                        // An empty completion would otherwise render as a blank bubble.
                        if (string.IsNullOrWhiteSpace(reply))
                            _replyQueue.Enqueue(new ChatResult { Ok = false, Error = "model returned an empty reply", UserInput = userInput, Ambient = ambient, NativeActionHandled = nativeActionHandled });
                        else
                            _replyQueue.Enqueue(new ChatResult { Ok = true, Text = reply, UserInput = userInput, Ambient = ambient, NativeActionHandled = nativeActionHandled });
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        _replyQueue.Enqueue(new ChatResult { Ok = false, Error = ex.Message, UserInput = userInput, Ambient = ambient, NativeActionHandled = nativeActionHandled });
                }
                finally
                {
                    capturedCts?.Dispose();
                }
            }, token);
        }

        private void CancelCurrentRequest()
        {
            // The owning task disposes its own CTS in a finally block, so by the time a
            // second message is sent this field may reference an already-disposed source.
            // Cancel() then throws ObjectDisposedException, which Il2CppInterop swallows at
            // the trampoline - silently killing chat after exactly one exchange.
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished and cleaned up; nothing to cancel.
            }
            finally
            {
                _cts = null;
            }
        }

        private async Task<string> RequestCompletionAsync(List<Message> messages, string ambientPrompt, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("API key is not configured.");

            // response_format keeps the model from wrapping the reply in markdown fences.
            // The parser tolerates fences anyway, because providers that ignore this
            // field are still expected to work.
            var payload = new JObject
            {
                ["model"] = Model,
                ["messages"] = JArray.FromObject(
                    messages.ConvertAll(m => new { role = m.Role, content = m.Content })),
                ["stream"] = false,
                ["max_tokens"] = 256,
                ["response_format"] = JObject.FromObject(new { type = "json_object" })
            };

            if (!string.IsNullOrWhiteSpace(ambientPrompt))
            {
                ((JArray)payload["messages"]).Add(JObject.FromObject(new
                {
                    role = "user",
                    content = "Respond naturally to this current event without claiming the player said it: " + ambientPrompt
                }));
            }

            // V4 Flash defaults to thinking mode, which adds hidden reasoning latency
            // that this short, structured dialogue task does not need. Keep this field
            // DeepSeek-only so other OpenAI-compatible providers remain supported.
            if (BaseUrl.IndexOf("api.deepseek.com", StringComparison.OrdinalIgnoreCase) >= 0)
                payload["thinking"] = JObject.FromObject(new { type = "disabled" });

            string reply = await SendCompletionAsync(payload, token);
            string displayLanguage = PersonaPrompt.CurrentDisplayLanguage();
            if (ReplyUsesRequestedDisplayLanguage(reply, displayLanguage)) return reply;

            LilithModPlugin.Logger.LogWarning(
                $"[LlmChat] Model used the wrong shown language; requesting {displayLanguage} correction.");
            // This fires on every reply, which doubles the API calls and roughly
            // doubles the wait. Whether the model really is emitting Japanese in
            // shown, or the check rejects a shape it should accept, cannot be told
            // apart without seeing the reply - so say what came back.
            if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                LilithModPlugin.Logger.LogInfo(
                    "[LlmChat] Rejected reply was: " +
                    (reply == null ? "<null>" : reply.Length > 400 ? reply.Substring(0, 400) + "..." : reply));
            var correctionPayload = (JObject)payload.DeepClone();
            var correctionMessages = (JArray)correctionPayload["messages"];
            correctionMessages.Add(JObject.FromObject(new { role = "assistant", content = reply }));
            correctionMessages.Add(JObject.FromObject(new
            {
                role = "user",
                content = "Correct only the language error. Return the same meaning and JSON structure, but every shown field must be English only. Keep spoken Japanese."
            }));
            reply = await SendCompletionAsync(correctionPayload, token);
            if (!ReplyUsesRequestedDisplayLanguage(reply, displayLanguage))
                throw new InvalidOperationException("DeepSeek repeatedly returned Japanese text for the English subtitle field.");
            return reply;
        }

        private static bool ReplyUsesRequestedDisplayLanguage(string reply, string displayLanguage)
        {
            if (string.IsNullOrWhiteSpace(displayLanguage) ||
                !displayLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                string trimmed = reply.Trim();
                if (trimmed.StartsWith("```"))
                {
                    int firstBreak = trimmed.IndexOf('\n');
                    int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                    if (firstBreak > 0 && lastFence > firstBreak)
                        trimmed = trimmed.Substring(firstBreak + 1, lastFence - firstBreak - 1).Trim();
                }
                JToken root = JToken.Parse(trimmed);
                JArray lines = root as JArray;
                if (lines == null && root is JObject obj)
                    lines = obj["lines"] as JArray ?? obj.Properties()
                        .Select(p => p.Value).OfType<JArray>().FirstOrDefault();
                if (lines == null || lines.Count == 0) return false;
                foreach (JToken line in lines)
                {
                    string shown = (string)line["shown"] ?? (string)line["en"];
                    if (string.IsNullOrWhiteSpace(shown) || ContainsCjk(shown)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool ContainsCjk(string text)
        {
            foreach (char c in text)
                if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff'))
                    return true;
            return false;
        }

        /// <summary>
        /// Japanese for one line the game built at runtime. Used only by
        /// DynamicLineCache, and only once per distinct line - the result is kept.
        /// </summary>
        internal static async Task<string> TranslateLineToJapaneseAsync(
            string source, CancellationToken token)
        {
            var instance = _instance;
            if (instance == null || string.IsNullOrWhiteSpace(ApiKey) ||
                string.IsNullOrWhiteSpace(source))
                return null;

            string reply = await instance.RequestTextCompletionAsync(
                "You translate one short line of game dialogue into natural Japanese. " +
                "It is spoken by Lilith, a soft-spoken companion character, to the player. " +
                "Reply with the Japanese only: no quotes, no romaji, no explanation, no alternatives.",
                source, 120, token).ConfigureAwait(false);

            return reply?.Trim().Trim('"', '「', '」');
        }

        private async Task<string> RequestTextCompletionAsync(
            string systemPrompt, string userPrompt, int maxTokens, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("DeepSeek API key is not configured.");

            var payload = new JObject
            {
                ["model"] = Model,
                ["messages"] = JArray.FromObject(new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }),
                ["stream"] = false,
                ["max_tokens"] = maxTokens
            };
            if (BaseUrl.IndexOf("api.deepseek.com", StringComparison.OrdinalIgnoreCase) >= 0)
                payload["thinking"] = JObject.FromObject(new { type = "disabled" });
            return await SendCompletionAsync(payload, token);
        }

        private async Task<string> SendCompletionAsync(JObject payload, CancellationToken token)
        {
            string lastFinishReason = "unknown";
            int lastCompletionTokens = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var requestPayload = (JObject)payload.DeepClone();
                if (attempt > 0)
                {
                    requestPayload["max_tokens"] = Math.Max(
                        512, (int?)requestPayload["max_tokens"] ?? 0);
                    requestPayload["temperature"] = 0.6;
                    var retryMessages = requestPayload["messages"] as JArray;
                    retryMessages?.Add(JObject.FromObject(new
                    {
                        role = "system",
                        content = "The previous completion was empty. Return a complete, non-empty answer now. Follow the requested output format exactly."
                    }));
                    // Some compatible endpoints intermittently return blank content
                    // while enforcing JSON mode. The parser already tolerates plain
                    // and fenced JSON, so the final attempt can safely omit it.
                    if (attempt == 2) requestPayload.Remove("response_format");
                    await Task.Delay(150 * attempt, token);
                }

                string jsonPayload = JsonConvert.SerializeObject(requestPayload);
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/chat/completions"))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    var callTimer = System.Diagnostics.Stopwatch.StartNew();
                    using (var response = await _httpClient.SendAsync(request, token))
                    {
                        response.EnsureSuccessStatusCode();
                        string responseBody = await response.Content.ReadAsStringAsync();
                        callTimer.Stop();
                        var json = JObject.Parse(responseBody);
                        var choice = json["choices"]?[0];
                        var content = choice?["message"]?["content"]?.ToString();
                        lastFinishReason = choice?["finish_reason"]?.ToString() ?? "missing";
                        lastCompletionTokens = (int?)json["usage"]?["completion_tokens"] ?? 0;
                        if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                            LilithModPlugin.Logger.LogInfo(
                                $"[LlmChat] API call took {callTimer.ElapsedMilliseconds} ms " +
                                $"(prompt_tokens={(int?)json["usage"]?["prompt_tokens"] ?? 0}, " +
                                $"completion_tokens={lastCompletionTokens}).");
                        if (!string.IsNullOrWhiteSpace(content)) return content;

                        // reasoning_content distinguishes the two causes. V4 Flash is a
                        // reasoning model, and the request asks for thinking to be
                        // disabled; if reasoning came back anyway, that field is not
                        // taking effect and the token budget is being spent before any
                        // answer is written. Empty reasoning means something else.
                        int reasoningLength =
                            (choice?["message"]?["reasoning_content"]?.ToString() ?? string.Empty).Length;
                        LilithModPlugin.Logger.LogWarning(
                            $"[LlmChat] Empty API content on attempt {attempt + 1}/3; "
                            + $"finish={lastFinishReason}, completion_tokens={lastCompletionTokens}, "
                            + $"reasoning_chars={reasoningLength}.");
                    }
                }
            }

            throw new InvalidOperationException(
                $"DeepSeek returned empty content after 3 attempts "
                + $"(finish={lastFinishReason}, completion_tokens={lastCompletionTokens}).");
        }

        // ========== Queue processing on main thread ==========
        private void HandleChatResult(ChatResult result)
        {
            if (_speechAwaitingReply && !result.Ambient)
            {
                _speechAwaitingReply = false;
                HidePanel();
            }

            if (result.Ok)
            {
                // Abandon anything still queued from the previous reply, otherwise its
                // audio keeps playing under this reply's subtitles and the two stay
                // mismatched for the rest of the session.
                LilithModPlugin.VoiceProcessor?.CancelCurrent();
                _currentReplyEnglish = null;
                _replyPlaybackActive = false;
                // A restore fired now must not resurrect a sentence from the reply
                // that was just cancelled.
                _lastDisplayedSentence = null;
                _restoreReplyBubble = false;

                if (!result.NativeActionHandled)
                    ExecuteNativeAction(result.Text);

                var utterances = ParseUtterances(result.Text);

                if (utterances == null)
                {
                    // Not the bilingual shape - an older prompt still in the cfg, or a
                    // model that ignored the format. Speak and show it as-is.
                    if (!result.Ambient)
                    {
                        lock (_history) _history.Add(new Message { Role = "assistant", Content = result.Text });
                        TrimHistory();
                        RememberAndMaybeWrite(result.UserInput, result.Text, result.NativeActionHandled);
                    }

                    if (VoiceConfig.Enabled && LilithModPlugin.VoiceProcessor != null)
                    {
                        _currentReplyEnglish = new System.Collections.Generic.List<string> { result.Text };
                        _replyPlaybackActive = true;
                        // Chunked for the same reason as the bilingual path: without
                        // it a long plain-text reply is one long silence.
                        var plain = UtteranceChunker.Chunk(new Utterance
                        {
                            JaText = result.Text,
                            EnText = result.Text,
                            Language = PersonaPrompt.CurrentVoiceLanguage(),
                        });
                        for (int i = 0; i < plain.Count; i++)
                        {
                            plain[i].EndOfReply = i == plain.Count - 1;
                            LilithModPlugin.VoiceProcessor.Enqueue(plain[i]);
                        }
                        LilithModPlugin.Logger.LogInfo(
                            $"[LlmChat] Plain-text reply queued for synchronized voice ({plain.Count} piece(s)).");
                    }
                    else
                    {
                        DisplayReplyText(result.Text);
                        LilithModPlugin.Logger.LogInfo("[LlmChat] LLM reply displayed (plain text, voice off).");
                    }
                    return;
                }

                if (utterances.Count == 0)
                {
                    LilithModPlugin.Logger.LogWarning("[LlmChat] Model returned an empty sentence list.");
                    DisplayReplyText(FallbackLines[UnityEngine.Random.Range(0, FallbackLines.Length)]);
                    return;   // deliberately not added to history
                }

                // History keeps what she actually said. Storing the raw JSON instead
                // would feed the model its own markup and triple the token cost of
                // every later turn.
                var spoken = new System.Collections.Generic.List<string>();
                var english = new System.Collections.Generic.List<string>();
                foreach (var u in utterances)
                {
                    spoken.Add(u.JaText);
                    if (!string.IsNullOrEmpty(u.EnText)) english.Add(u.EnText);
                }

                lock (_history)
                {
                    if (!result.Ambient)
                        _history.Add(new Message { Role = "assistant", Content = string.Join(" ", spoken) });
                }
                if (!result.Ambient)
                {
                    TrimHistory();
                    RememberAndMaybeWrite(result.UserInput, string.Join(" ", spoken),
                        result.NativeActionHandled);
                }

                if (!VoiceConfig.Enabled || LilithModPlugin.VoiceProcessor == null)
                {
                    // No audio to pace the subtitles, so show the reply in one piece.
                    DisplayReplyText(string.Join(" ", english));
                    _replyHideAt = Time.unscaledTime + 6f;
                    LilithModPlugin.Logger.LogInfo("[LlmChat] LLM reply displayed (voice off).");
                    return;
                }

                // Kept so a mid-reply synthesis failure can fall back to the full text.
                _currentReplyEnglish = english;
                _replyPlaybackActive = true;

                // A long line is one synthesis request, so the player waits through
                // the whole reply before hearing any of it. Split first, then queue:
                // the first piece is all the wait, and the rest are synthesised
                // while she is already speaking.
                var queued = new System.Collections.Generic.List<Utterance>();
                foreach (var u in utterances)
                    queued.AddRange(UtteranceChunker.Chunk(u));

                for (int i = 0; i < queued.Count; i++)
                {
                    queued[i].EndOfReply = i == queued.Count - 1;
                    LilithModPlugin.VoiceProcessor.Enqueue(queued[i]);
                }

                LilithModPlugin.Logger.LogInfo(
                    $"[LlmChat] LLM reply queued ({queued.Count} synchronized piece(s) " +
                    $"from {utterances.Count} line(s)) after {Time.unscaledTime - _replyStartedAt:0.0}s.");
            }
            else
            {
                LilithModPlugin.Logger.LogWarning($"[LlmChat] LLM request failed: {result.Error}");

                string fallback = FallbackLines[UnityEngine.Random.Range(0, FallbackLines.Length)];
                if (DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
                {
                    _replyNode.text = fallback;
                    DialogueManager.s_instance.StartDialogue(9500000);
                    _replyHideAt = Time.unscaledTime + 6f;
                }
                else
                {
                    LilithModPlugin.Logger.LogError("[LlmChat] Cannot display fallback: node 9500000 missing.");
                }
                // Do NOT add fallback to history.
            }
        }

        /// <summary>Safety net against a model that returns a wall of sentences.</summary>
        private const int MaxUtterancesPerReply = 5;

        /// <summary>
        /// The English lines of the reply being spoken, so a synthesis failure part way
        /// through can still show the whole thing. Null when nothing is in flight.
        /// </summary>
        private System.Collections.Generic.List<string> _currentReplyEnglish;

        private static void ExecuteNativeAction(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                string json = text.Trim();
                if (json.StartsWith("```"))
                {
                    int firstBreak = json.IndexOf('\n');
                    int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
                    if (firstBreak > 0 && lastFence > firstBreak)
                        json = json.Substring(firstBreak + 1, lastFence - firstBreak - 1).Trim();
                }
                if (!json.StartsWith("{")) return;

                var action = JObject.Parse(json)["action"] as JObject;
                string type = ((string)action?["type"])?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(type)) return;

                switch (type)
                {
                    case "timer":
                    {
                        double seconds = (double?)action["seconds"] ?? 0;
                        if (seconds < 1 || seconds > 7 * 24 * 60 * 60)
                            throw new InvalidOperationException("Timer duration is outside the supported range.");
                        TimerSystem timer = TimerSystem.Instance;
                        if (timer == null) throw new InvalidOperationException("Lilith timer is unavailable.");
                        // The LLM reply is the spoken confirmation. The native announce
                        // uses the game display language and would add an English voice.
                        timer.StartCountdown((float)seconds, false);
                        LilithModPlugin.Logger.LogInfo($"[NativeAction] Timer started for {seconds:0} seconds.");
                        break;
                    }
                    case "alarm":
                    {
                        string value = (string)action["local_time"];
                        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out DateTime local))
                            throw new InvalidOperationException("Alarm time was not valid local time.");
                        local = DateTime.SpecifyKind(local, DateTimeKind.Local);
                        if (local <= DateTime.Now || local > DateTime.Now.AddYears(1))
                            throw new InvalidOperationException("Alarm time is outside the supported range.");
                        AlarmSystem.SetAlarm(new Il2CppSystem.DateTime(local.Ticks));
                        LilithModPlugin.Logger.LogInfo($"[NativeAction] Alarm set for {local:yyyy-MM-dd HH:mm:ss}.");
                        break;
                    }
                    case "timer_cancel":
                        TimerSystem.Instance?.Cancel();
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Timer cancelled.");
                        break;
                    case "alarm_cancel":
                        AlarmSystem.CancelAlarm();
                        LilithModPlugin.Logger.LogInfo("[NativeAction] Alarm cancelled.");
                        break;
                    case "open_app":
                    {
                        string app = (string)action["app"];
                        if (string.IsNullOrWhiteSpace(app))
                            throw new InvalidOperationException("open_app action missing app name.");
                        if (LilithModPlugin.CfgAllowOpenApps == null || !LilithModPlugin.CfgAllowOpenApps.Value)
                            throw new InvalidOperationException("App opening is disabled.");
                        if (!AppLauncher.TryOpen(app))
                            throw new InvalidOperationException($"Could not open app '{app}'.");
                        break;
                    }
                    case "search_web":
                    {
                        string query = (string)action["query"];
                        if (string.IsNullOrWhiteSpace(query))
                            throw new InvalidOperationException("search_web action missing query.");
                        if (LilithModPlugin.CfgAllowOpenApps == null || !LilithModPlugin.CfgAllowOpenApps.Value)
                            throw new InvalidOperationException("Browser search is disabled.");
                        if (!AppLauncher.TrySearch(query))
                            throw new InvalidOperationException("Could not open browser search.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[NativeAction] Could not apply LLM action: " + ex.Message);
            }
        }

        /// <summary>
        /// Pulls the sentence pairs out of a reply. Returns null when the text is not
        /// the bilingual shape at all, which the caller treats as plain text rather
        /// than an error - an existing cfg may still hold the older prompt.
        /// </summary>
        private System.Collections.Generic.List<Utterance> ParseUtterances(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                // Tolerated shapes, in order of likelihood:
                //   {"lines":[{"ja":..,"en":..}]}   - what json_object mode returns
                //   [{"ja":..,"en":..}]             - a bare array
                //   ```json ... ```                 - a model that fenced it anyway
                string trimmed = text.Trim();

                if (trimmed.StartsWith("```"))
                {
                    int firstBreak = trimmed.IndexOf('\n');
                    int lastFence = trimmed.LastIndexOf("```", System.StringComparison.Ordinal);
                    if (firstBreak > 0 && lastFence > firstBreak)
                        trimmed = trimmed.Substring(firstBreak + 1, lastFence - firstBreak - 1).Trim();
                }

                Newtonsoft.Json.Linq.JArray array = null;

                if (trimmed.StartsWith("["))
                {
                    array = Newtonsoft.Json.Linq.JArray.Parse(trimmed);
                }
                else if (trimmed.StartsWith("{"))
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(trimmed);
                    foreach (var property in obj.Properties())
                    {
                        if (property.Value is Newtonsoft.Json.Linq.JArray candidate)
                        {
                            array = candidate;
                            break;
                        }
                    }
                }

                if (array == null)
                    return null;

                var list = new System.Collections.Generic.List<Utterance>();
                foreach (var item in array)
                {
                    string spoken = (string)item["spoken"] ?? (string)item["ja"];
                    string shown = (string)item["shown"] ?? (string)item["en"];
                    if (string.IsNullOrWhiteSpace(spoken))
                        continue;

                    list.Add(new Utterance
                    {
                        JaText = spoken.Trim(),
                        EnText = (shown ?? string.Empty).Trim(),
                        Language = PersonaPrompt.CurrentVoiceLanguage()
                    });
                    if (list.Count >= MaxUtterancesPerReply)
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[LlmChat] Reply had more than {MaxUtterancesPerReply} sentences; truncated.");
                        break;
                    }
                }

                // Parsed as JSON but carried no usable sentence: an empty list is a
                // real answer here, so return it rather than falling back to raw JSON.
                return list;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private void TrimHistory()
        {
            lock (_history)
            {
                // Keep system message, remove oldest user+assistant pair if exceeding MaxHistoryTurns.
                // Excluding system message, count of user+assistant pairs.
                int pairCount = (_history.Count - 1) / 2;
                while (pairCount > MaxHistoryTurns && _history.Count >= 4)
                {
                    // Remove the oldest pair.
                    _history.RemoveAt(1); // user
                    _history.RemoveAt(1); // assistant
                    pairCount--;
                }
            }
        }

        private static string BuildSystemPrompt()
        {
            string prompt = PersonaPrompt.Build(
                PersonaPrompt.CurrentVoiceLanguage(), PersonaPrompt.CurrentDisplayLanguage());
            string memory = MemoryStore.Context();
            return string.IsNullOrEmpty(memory) ? prompt : prompt + "\n" + memory;
        }

        private static bool NeedsLiveInformation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.ToLowerInvariant();
            string[] terms =
            {
                "weather", "forecast", "temperature", "current time", "what time", "today's news",
                "latest", "right now", "search the web", "look up", "天気", "気温", "今何時", "現在", "最新",
                "天气", "气温", "几点", "现在", "最新", "新闻"
            };
            foreach (string term in terms)
                if (value.Contains(term)) return true;
            return false;
        }

        /// <summary>
        /// Whether an exchange counts toward a note. Length is the floor, but the
        /// message must be mostly words rather than a URL, and she must have answered -
        /// raw length alone let a pasted link qualify.
        /// </summary>
        private static bool IsSubstantialExchange(string user, string lilith, bool nativeActionHandled)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(lilith)) return false;

            // Errands are not conversations. Setting a timer, asking the forecast, or
            // sending her to search the web should never build toward a keepsake -
            // the note is supposed to come out of talking to her, not using her.
            if (nativeActionHandled) return false;
            if (NeedsLiveInformation(user)) return false;
            if (MentionsTimerOrAlarm(user)) return false;

            string trimmed = user.Trim();
            if (trimmed.Length < LilithModPlugin.CfgNoteMinMessageLength.Value) return false;
            if (trimmed.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (trimmed.IndexOf('\\') >= 0) return false;

            int letters = 0;
            foreach (char c in trimmed)
                if (char.IsLetter(c)) letters++;
            // CJK has no spaces, so word counting does not travel; letter density
            // separates prose from a pasted token either way.
            return letters * 2 >= trimmed.Length;
        }

        /// <summary>
        /// Whether the player was talking about themselves or her. Bare first person is
        /// deliberately not enough - a feeling, life event, or the bond must be named.
        /// </summary>
        private static bool IsPersonalExchange(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return false;
            string value = user.ToLowerInvariant();
            string[] markers =
            {
                // Feeling and state.
                "i feel", "i felt", "i'm feeling", "im feeling", "makes me", "made me",
                "i'm tired", "im tired", "exhausted", "lonely", "alone", "sad", "upset",
                "anxious", "scared", "afraid", "worried", "stressed", "happy", "glad",
                "proud", "relieved", "hurt", "angry", "crying", "cried",
                // Life and days.
                "my day", "today i", "my mom", "my dad", "my family", "my friend",
                "my job", "my work", "at work", "my school", "growing up", "i used to",
                "i've been", "ive been", "i keep thinking", "i remember when", "i dreamed",
                "i can't sleep", "i cant sleep",
                // The bond itself.
                "love you", "miss you", "thank you for", "you matter", "you're real",
                "youre real", "i need you", "stay with me", "don't leave", "dont leave",
                "you helped", "because of you",
                // Japanese.
                "寂しい", "さびしい", "つらい", "辛い", "疲れた", "不安", "怖い", "嬉しい",
                "悲しい", "泣", "好き", "愛して", "会いたい", "ありがとう", "そばに",
                "いなくならないで", "夢を見た", "今日は",
                // Simplified Chinese.
                "寂寞", "孤独", "难过", "累了", "不安", "害怕", "开心", "高兴", "想你",
                "喜欢你", "爱你", "谢谢你", "陪我", "别走", "因为你", "今天我"
            };
            foreach (string marker in markers)
                if (value.Contains(marker)) return true;
            return false;
        }

        /// <summary>
        /// Catches timer and alarm talk the native handler did not claim - the LLM
        /// may action it instead, and either way it is an errand, not a conversation.
        /// </summary>
        private static bool MentionsTimerOrAlarm(string text)
        {
            string value = text?.ToLowerInvariant() ?? string.Empty;
            return value.Contains("timer") || value.Contains("alarm") ||
                   value.Contains("remind me") || value.Contains("wake me") ||
                   value.Contains("タイマー") || value.Contains("アラーム") ||
                   value.Contains("计时") || value.Contains("闹钟");
        }

        private void RememberAndMaybeWrite(string user, string lilith, bool nativeActionHandled)
        {
            MemoryStore.RecordConversation(user, lilith);
            if (_letterInFlight) return;
            if (!IsSubstantialExchange(user, lilith, nativeActionHandled)) return;

            double windowHours = LilithModPlugin.CfgNoteWindowHours.Value;
            NoteJournal.RecordQualifying(windowHours, IsPersonalExchange(user));
            if (!NoteJournal.ShouldWrite(
                    LilithModPlugin.CfgNoteMinConversations.Value,
                    windowHours,
                    LilithModPlugin.CfgNoteCooldownHours.Value,
                    LilithModPlugin.CfgNoteChance.Value))
                return;

            _letterInFlight = true;
            string letterMemory = MemoryStore.ConversationContext();
            float noteRoll = UnityEngine.Random.value;
            string lengthRule;
            int letterMaxTokens;
            // Landing the rare roll after a stretch of errands would produce a
            // declaration about nothing, so the talking has to have been personal.
            bool loveLetter = noteRoll < 0.05f &&
                NoteJournal.PersonalCount(windowHours) >= LoveLetterPersonalMinimum;
            // Budgets are generous because the failure mode is a note that stops
            // mid-sentence, and Japanese and Chinese cost far more tokens per
            // sentence than English does. Overshooting costs nothing.
            if (noteRoll < 0.05f)
            {
                // Measured, not guessed: this brackets a median of ~520 rendered
                // characters, which is about as close to the 500 target as a word
                // count gets. The ceiling drives the result far more than the floor
                // - 90-140 medians 574 and 90-110 medians 427 on the same floor -
                // and roughly +-100 characters of spread is inherent either way.
                lengthRule = "Write one long, flowing sentence of roughly 90 to 115 words.";
                letterMaxTokens = 420;
            }
            else if (noteRoll < 0.20f)
            {
                int sentences = UnityEngine.Random.Range(4, 8);
                lengthRule = $"Write exactly {sentences} short sentences.";
                letterMaxTokens = 340;
            }
            else
            {
                int sentences = UnityEngine.Random.Range(2, 4);
                lengthRule = $"Write exactly {sentences} short sentences.";
                letterMaxTokens = 220;
            }
            string letterPersona = PersonaPrompt.BuildLetter(
                PersonaPrompt.CurrentDisplayLanguage(), loveLetter);
            if (loveLetter)
                LilithModPlugin.Logger.LogInfo("[Letters] This one is a love letter.");
            Task.Run(async () =>
            {
                try
                {
                    string letter = await RequestTextCompletionAsync(
                        letterPersona,
                        "Write a personal letter from Lilith after these meaningful interactions. " +
                        lengthRule + " " +
                        "Use only the current game display language required by the system prompt. " +
                        "No JSON, markdown, title, translation, or stage directions. " +
                        "Do not sign it; the note already carries her signature.\n" +
                        letterMemory,
                        letterMaxTokens,
                        CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(letter))
                    {
                        _letterQueue.Enqueue(letter);
                        // Only now: a failed request must not consume the cooldown,
                        // or a transient outage silently costs a keepsake.
                        NoteJournal.MarkWritten();
                        await RecordLongTermSummaryAsync(letterMemory);
                    }
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning($"[Letters] Could not write note: {ex.Message}");
                }
                finally { _letterInFlight = false; }
            });
        }

        /// <summary>
        /// Distils the stretch of talking a note came out of into one line of
        /// long-term memory. Written in the third person and about the player, not
        /// about her feelings: this is the record she is later allowed to allude to,
        /// and prose in her own voice would come back out as a quotation.
        ///
        /// Runs after the note is queued and never blocks it - failing here costs a
        /// memory, not the keepsake the player is about to receive.
        /// </summary>
        private async Task RecordLongTermSummaryAsync(string letterMemory)
        {
            if (string.IsNullOrWhiteSpace(letterMemory)) return;
            try
            {
                string summary = await RequestTextCompletionAsync(
                    "You summarise conversations into a single line for later recall.",
                    "From the exchanges below, write ONE sentence in English, under 30 words, " +
                    "naming what the player talked about and anything they revealed about their " +
                    "life, mood, or circumstances. Third person, factual, no quotes, no roleplay, " +
                    "no commentary on Lilith. If nothing personal was said, reply exactly NOTHING.\n" +
                    letterMemory,
                    120,
                    CancellationToken.None);

                if (string.IsNullOrWhiteSpace(summary)) return;
                summary = summary.Trim();
                if (summary.StartsWith("NOTHING", StringComparison.OrdinalIgnoreCase)) return;

                MemoryStore.RecordLongTerm(summary);
                LilithModPlugin.Logger.LogInfo("[Memory] Long-term entry written alongside the note.");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Memory] Could not summarise for long-term memory: {ex.Message}");
            }
        }

        /// <summary>
        /// Development-only hook, implemented in the untracked DevHooks.cs. With no
        /// implementation present - any machine but this one, and every release -
        /// the compiler removes this call entirely.
        /// </summary>
        partial void PollNoteTestFile();

        /// <summary>
        /// Removes a trailing sign-off. The note image draws her signature itself,
        /// and a model told not to sign still does now and then - belt and braces,
        /// because the duplicate is only visible once the note is rendered.
        /// </summary>
        private static string StripSignature(string letter)
        {
            if (string.IsNullOrWhiteSpace(letter)) return letter;
            string trimmed = letter.TrimEnd();
            foreach (string name in new[] { "Lilith", "リリス", "莉莉丝", "莉莉絲" })
            {
                if (!trimmed.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                string body = trimmed.Substring(0, trimmed.Length - name.Length)
                    .TrimEnd(' ', '\t', '\r', '\n', '-', '—', ',', '、', '~', '～');
                // Only when it reads as a sign-off. "...gave me a soul. Lilith"
                // signs off; "you already know Lilith" is a sentence.
                if (body.Length > 0 && ".!?。！？…".IndexOf(body[body.Length - 1]) >= 0)
                    return body;
            }
            return letter;
        }

        private static void SaveLetter(string letter)
        {
            letter = StripSignature(letter);
            try
            {
                // SaveNote returns the path it wrote, or null/empty when it declined.
                // Ignoring that made a silent no-op look like success.
                string saved = NoteImageSaver.SaveNote(letter, false);
                if (string.IsNullOrEmpty(saved))
                {
                    LilithModPlugin.Logger.LogWarning(
                        $"[Letters] SaveNote wrote nothing for a {letter?.Length ?? 0} char note.");
                    return;
                }
                NoteInbox.NotifySaved();
                LilithModPlugin.Logger.LogInfo($"[Letters] Lilith left a note: {saved}");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Letters] Could not save note: {ex.Message}");
            }
        }


        // Two, so a single stray "love you" cannot arm a love letter on its own.
        private const int LoveLetterPersonalMinimum = 2;

        /// <summary>
        /// Floor between ANY two unprompted utterances. Separate timers let ambient and
        /// interaction each stay in budget while the pair did not, giving back-to-back
        /// spontaneous speech. One shared timestamp is what actually bounds it.
        /// </summary>
        private const float SpontaneousGapSeconds = 180f;

        /// <summary>
        /// Quiet window after the game's own dialogue. Measured, the game speaks far more
        /// than the mod does, so landing on a native line is the likeliest overlap.
        /// </summary>
        private const float NativeDialogueQuietSeconds = 8f;

        /// <summary>
        /// Rolled when an interaction reply fires; a miss drops it silently, so she
        /// notices most handling, not all. Ambient is not rolled - its interval is
        /// already the rarity there.
        /// </summary>
        private const float InteractionReplyChance = 0.7f;

        /// <summary>Lower, because being handled while asleep should mostly not wake her.</summary>
        private const float SleepingInteractionReplyChance = 0.4f;

        /// <summary>Ambient intervals stretch by this much while she sleeps.</summary>
        private const float SleepingAmbientMultiplier = 1.5f;

        /// <summary>
        /// Whether she is currently asleep. Wrapped because the character instance is
        /// not guaranteed to exist, and a throw here would take out the whole tick.
        /// </summary>
        private static bool IsSleeping()
        {
            try
            {
                if (LilithModPlugin.CfgForceSleeping != null &&
                    LilithModPlugin.CfgForceSleeping.Value)
                    return true;
                var character = CharacterController.s_activeInstance;
                return character != null && character.IsSleep;
            }
            catch
            {
                return false;
            }
        }

        private float _replyStartedAt;

        /// <summary>
        /// Quiet after her voice stops before an interaction reply may follow, so two
        /// utterances do not run together.
        /// </summary>
        private const float InteractionAfterSpeechSeconds = 1f;

        private float _speechEndedAt = -600f;

        /// <summary>
        /// True while she is still speaking, or within the beat just after. Anything
        /// that would start a new reply waits on this: cutting her off mid-sentence
        /// reads as a malfunction rather than an interruption.
        /// </summary>
        private bool SpeechStillFinishing =>
            _replyPlaybackActive ||
            Time.unscaledTime - _speechEndedAt < InteractionAfterSpeechSeconds;

        private string _pendingUserMessage;
        private bool _pendingUserMessageNativeAction;
        private float _pendingUserMessageAt;

        /// <summary>
        /// A held message is sent regardless once this long has passed. Waiting on
        /// playback means a stuck _replyPlaybackActive would swallow what the player
        /// typed and never answer it, which is far worse than talking over her.
        /// </summary>
        private const float QueuedMessageMaxWaitSeconds = 20f;

        private static float _lastNativeDialogueAt = -600f;

        /// <summary>Called from the dialogue gate whenever the game itself speaks.</summary>
        internal static void NoteNativeDialogue()
        {
            _lastNativeDialogueAt = Time.unscaledTime;
        }

        private float _lastSpontaneousAt = -600f;

        private bool SpontaneousReady =>
            Time.unscaledTime - _lastSpontaneousAt >= SpontaneousGapSeconds;

        private void DrainInteractions()
        {
            while (InteractionQueue.TryDequeue(out string kind))
            {
                MemoryStore.RecordInteraction(kind);
                // Cooldown between reactions to being interacted with. Long enough that
                // repeated petting does not turn into a running commentary; the
                // interaction is still remembered even when the reply is skipped.
                if (!AmbientAllowed || !SpontaneousReady) continue;
                _pendingInteraction = kind;
                _interactionReplyAt = Time.unscaledTime + 3f;
            }
        }

        private void TryInteractionReply()
        {
            if (string.IsNullOrEmpty(_pendingInteraction) || Time.unscaledTime < _interactionReplyAt ||
                (_currentRequest != null && !_currentRequest.IsCompleted)) return;
            // Handling her triggers the game's own drag and touch dialogue, so a
            // reply fired immediately lands on top of it. Held, not dropped - the
            // reply still makes sense a moment after she finishes.
            if (Time.unscaledTime - _lastNativeDialogueAt < NativeDialogueQuietSeconds) return;

            // _currentRequest covers only the API call, which finishes seconds before the
            // audio. Without this the reply lands mid-sentence and CancelCurrent drops the
            // rest. Held, not dropped - the interaction is still pending when she stops.
            if (SpeechStillFinishing) return;

            string kind = _pendingInteraction;
            _pendingInteraction = null;
            // Re-checked here, not just when queued: an ambient remark may have
            // landed during the three second delay before this fires.
            if (!SpontaneousReady) return;
            // Rolled once, at the moment of firing, and a miss discards the pending
            // interaction. Rolling every frame while it waits would come up true
            // eventually and make the chance meaningless.
            bool sleeping = IsSleeping();
            float replyChance = sleeping ? SleepingInteractionReplyChance : InteractionReplyChance;
            float roll = UnityEngine.Random.value;
            // Which branch was taken, not just the outcome. The chance itself cannot
            // practically be sampled - a miss costs 11 s before the next attempt but
            // a hit costs the 180 s cooldown - so whether the sleeping path was
            // chosen has to be readable from a single poke.
            if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                LilithModPlugin.Logger.LogInfo(
                    $"[Ambient] Interaction '{kind}': sleeping={sleeping}, " +
                    $"chance={replyChance:0.##}, roll={roll:0.##}, " +
                    $"{(roll < replyChance ? "replying" : "staying quiet")}.");
            if (roll >= replyChance) return;
            _lastSpontaneousAt = Time.unscaledTime;
            SendUserMessage("The player just interacted with Lilith: " + kind, true);
        }

        /// <summary>
        /// Ambient remarks and interaction replies are unprompted, so without a key
        /// they can only surface an error the player never asked for.
        /// </summary>
        private static bool AmbientAllowed =>
            LilithModPlugin.CfgAmbientEnabled.Value && HasApiKey;

        private void TryAmbientRemark()
        {
            if (!LilithModPlugin.CfgAmbientEnabled.Value) return;
            if (!HasApiKey)
            {
                // Rescheduled rather than left due, or pasting a key mid-session would
                // be answered by an immediate remark out of nowhere.
                ScheduleNextAmbient();
                return;
            }
            if (Time.unscaledTime < _nextAmbientAt ||
                (_currentRequest != null && !_currentRequest.IsCompleted)) return;
            if (Time.unscaledTime - _lastNativeDialogueAt < NativeDialogueQuietSeconds)
            {
                // The game is mid-sentence. Deliberately NOT rescheduled: hold the
                // remark and let it through a moment later, rather than pushing it
                // out by another full interval for an eight second overlap.
                return;
            }
            if (SpeechStillFinishing)
            {
                // She is still speaking. Held, not rescheduled - for the same reason
                // as the native-dialogue case above, pushing it out by a full interval
                // would be a heavy penalty for a few seconds of overlap.
                return;
            }
            if (!SpontaneousReady)
            {
                // Due, but she has spoken unbidden too recently. Push it out rather
                // than dropping it, so the remark arrives late instead of never.
                ScheduleNextAmbient();
                return;
            }
            ScheduleNextAmbient();
            _lastSpontaneousAt = Time.unscaledTime;
            SendUserMessage("Make one spontaneous remark suited to the current time, posture, and recent memory.", true);
        }

        private void ScheduleNextAmbient()
        {
            int min = Math.Max(1, LilithModPlugin.CfgAmbientMinMinutes.Value);
            int max = Math.Max(min, LilithModPlugin.CfgAmbientMaxMinutes.Value);
            // Both bounds stretch while she sleeps, so the whole window moves out
            // rather than just becoming wider - a sleeping remark should be rarer,
            // not more erratic. Sampled here rather than at fire time: waking during
            // a long wait should not suddenly shorten it.
            float scale = IsSleeping() ? SleepingAmbientMultiplier : 1f;
            _nextAmbientAt = Time.unscaledTime + UnityEngine.Random.Range(min * 60f, max * 60f) * scale;
        }

        /// <summary>
        /// Toggles speech recognition. The trigger file tells the external transcriber to
        /// listen; it ends on silence. Pressing again cancels, since silence submits.
        /// </summary>
        private void PollPushToTalkKey()
        {
            SpeechInputService.Refresh(Time.unscaledTime);

            // Without the listener the key would open the bar, show "Listening~", and
            // wait forever for a transcript nobody is writing. Without an API key the
            // transcript would arrive and then fail to send. Both make the key inert.
            if (!LilithModPlugin.CfgPushToTalkEnabled.Value ||
                !SpeechInputService.IsAvailable || !HasApiKey)
            {
                if (_speechListening) StopListening(true);
                if (_vkPushToTalk > 0 && !HasApiKey && WindowFocus.IsKeyDown(_vkPushToTalk))
                    WarnMissingApiKey();
                return;
            }

            ApplyConfiguredPushToTalkKey(LilithModPlugin.CfgPushToTalkKey.Value, false);
            if (_vkPushToTalk <= 0) return;

            // Rising edge: this key toggles, it is no longer held down to talk.
            if (SettingsBridge.CapturingChatKey || !WindowFocus.IsKeyDown(_vkPushToTalk))
                return;

            if (_speechListening) StopListening(true);
            else StartListening();
        }

        private static bool HasApiKey =>
            !string.IsNullOrWhiteSpace(LilithModPlugin.CfgApiKey.Value);

        private static float _nextApiKeyWarning;

        /// <summary>Explains an inert key once a minute rather than on every press.</summary>
        private static void WarnMissingApiKey()
        {
            if (Time.unscaledTime < _nextApiKeyWarning) return;
            _nextApiKeyWarning = Time.unscaledTime + 60f;
            LilithModPlugin.Logger.LogWarning(
                "[LlmChat] No DeepSeek API key set; chat keys are disabled. "
                + "Add one under Settings / Me.");
        }

        private void StartListening()
        {
            _speechListening = true;
            _lastAppliedPartial = null;
            _userTypedWhileListening = false;
            ShowPanel();
            if (_inputField != null)
            {
                _inputField.text = string.Empty;
                _inputField.caretPosition = 0;
            }
            // Focus the field so the recognised text can be corrected, or replaced by
            // typing, without needing a click first.
            FocusInputField();
            if (_placeholderText != null) _placeholderText.text = "Listening~";
            try
            {
                // The trigger carries the game's display language, so recognition
                // follows the language setting without restarting the listener.
                File.WriteAllText(_pushToTalkTriggerPath,
                    PersonaPrompt.CurrentDisplayLanguage() ?? string.Empty);
            }
            catch (IOException ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Speech] Could not start listening: {ex.Message}");
                _speechListening = false;
            }
        }

        /// <summary>
        /// Stops the microphone. <paramref name="cancelled"/> distinguishes the user
        /// toggling off - which also closes an untouched panel - from listening ending
        /// because a transcript arrived.
        /// </summary>
        private void StopListening(bool cancelled)
        {
            _speechListening = false;
            if (_placeholderText != null) _placeholderText.text = "Say something···";
            ClearPushToTalkTrigger();
            if (cancelled && !_userTypedWhileListening &&
                string.IsNullOrWhiteSpace(_inputField?.text) &&
                string.IsNullOrEmpty(_pendingSpeechCommand))
                HidePanel();
        }

        private void ClearPushToTalkTrigger()
        {
            try
            {
                if (File.Exists(_pushToTalkTriggerPath)) File.Delete(_pushToTalkTriggerPath);
            }
            catch (IOException ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Speech] Could not clear the push-to-talk trigger: {ex.Message}");
            }
        }

        private void ApplyConfiguredPushToTalkKey(string value, bool initial)
        {
            string name = value?.Trim();
            if (!initial &&
                string.Equals(name, _configuredPushToTalkKey, StringComparison.OrdinalIgnoreCase))
                return;

            _configuredPushToTalkKey = name;
            int key = WindowFocus.VirtualKeyFromName(name);

            // One key cannot both open the chat box and hold the microphone open.
            if (key > 0 && key == _vkHotkey)
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Speech] Push-to-talk key '{name}' is already the open-chat key; "
                    + "push-to-talk is disabled until one of them changes.");
                key = -1;
            }
            else if (key <= 0)
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Speech] Unrecognised push-to-talk key '{name}'. Expected F1-F12, A-Z, or 0-9.");
            }

            // Rebinding mid-utterance would leave the microphone on with no key that
            // can turn it off again.
            if (_vkPushToTalk > 0 && key != _vkPushToTalk && _speechListening)
                StopListening(true);
            _vkPushToTalk = key;
        }

        private void PollSpeechCommand()
        {
            if (!LilithModPlugin.CfgPushToTalkEnabled.Value || Time.unscaledTime < _nextSpeechPoll)
                return;
            _nextSpeechPoll = Time.unscaledTime + 0.15f;
            try
            {
                // Drain the timestamped transcript files in capture order.
                string directory = Path.GetDirectoryName(_speechCommandPath) ?? ".";
                string stem = Path.GetFileNameWithoutExtension(_speechCommandPath);
                string[] queued = Directory.GetFiles(directory, stem + ".*.txt");
                Array.Sort(queued, StringComparer.Ordinal);
                foreach (string path in queued)
                    EnqueueSpeechFile(path);

                TryBeginNextSpeechCommand();
            }
            catch (IOException) { }
        }

        private void EnqueueSpeechFile(string path)
        {
            if (!File.Exists(path)) return;
            string command = File.ReadAllText(path).Trim();
            File.Delete(path);

            if (command.StartsWith(SpeechPartialMarker, StringComparison.Ordinal))
            {
                // Interim text only lands while still listening. A partial arriving
                // afterwards belongs to an utterance already transcribed in full, and
                // would replace the final text with a worse version of it.
                if (!_speechListening) return;
                if (NoteUserTyping()) return;

                string partial = command.Substring(SpeechPartialMarker.Length)
                    .TrimStart('\r', '\n', ':', ' ');
                if (_uiReady && _inputField != null && !string.IsNullOrWhiteSpace(partial))
                {
                    _inputField.text = partial;
                    _inputField.caretPosition = partial.Length;
                    _lastAppliedPartial = partial;
                }
                return;
            }

            NoteUserTyping();
            bool wasListening = _speechListening;
            if (wasListening) StopListening(false);

            if (string.IsNullOrWhiteSpace(command))
            {
                // Nothing audible. Close the panel the key press opened, unless the user
                // has typed something into it in the meantime.
                if (!_userTypedWhileListening && string.IsNullOrWhiteSpace(_inputField?.text) &&
                    string.IsNullOrEmpty(_pendingSpeechCommand))
                    HidePanel();
                return;
            }

            // Typing outranks recognition: once the user has edited the field, the
            // transcript is stale and replacing their text would discard real input.
            // They submit with Enter instead.
            if (_userTypedWhileListening)
            {
                LilithModPlugin.Logger.LogInfo(
                    "[Speech] Transcript discarded; the field was edited while listening.");
                return;
            }

            _speechCommandQueue.Enqueue(command);
        }

        /// <summary>
        /// Detects that the field no longer holds the partial we last wrote into it,
        /// which can only mean the user typed. Latches, because a later partial must not
        /// silently re-take the field once they have started editing.
        /// </summary>
        private bool NoteUserTyping()
        {
            if (_userTypedWhileListening) return true;
            if (_inputField == null) return false;

            string current = _inputField.text ?? string.Empty;
            string expected = _lastAppliedPartial ?? string.Empty;
            if (string.Equals(current, expected, StringComparison.Ordinal)) return false;

            _userTypedWhileListening = true;
            return true;
        }

        private void TryBeginNextSpeechCommand()
        {
            if (!_uiReady || _inputField == null || !string.IsNullOrEmpty(_pendingSpeechCommand) ||
                (_currentRequest != null && !_currentRequest.IsCompleted) ||
                _replyPlaybackActive ||
                _speechCommandQueue.Count == 0) return;

            string command = _speechCommandQueue.Dequeue();
            ShowPanel();
            _inputField.text = command;
            _inputField.caretPosition = command.Length;
            _pendingSpeechCommand = command;
            // Brief pause so the recognised text is readable before it is sent.
            _speechSubmitAt = Time.unscaledTime + 0.6f;
        }

        private void TrySubmitSpeechCommand()
        {
            if (string.IsNullOrEmpty(_pendingSpeechCommand) || Time.unscaledTime < _speechSubmitAt)
                return;
            string shown = _inputField?.text?.Trim();
            _pendingSpeechCommand = null;
            if (string.IsNullOrEmpty(shown)) return;

            // Keep the recognised speech visible while DeepSeek is working. The result
            // handler closes this panel just before Lilith's reply is handed to voice.
            _speechAwaitingReply = true;
            bool nativeActionHandled = TryApplyImmediateNativeAction(shown);
            SendUserMessage(shown, false, nativeActionHandled);
        }
    }
}
