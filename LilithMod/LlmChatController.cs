using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
    public class LlmChatController : MonoBehaviour
    {
        // ========== Configuration (populated from LilithModPlugin statics) ==========
        private static string BaseUrl => LilithModPlugin.CfgBaseUrl.Value;
        private static string ApiKey => LilithModPlugin.CfgApiKey.Value;
        private static string Model => LilithModPlugin.CfgModel.Value;
        private static string SystemPrompt => LilithModPlugin.CfgSystemPrompt.Value;
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
        private const float PanelWidth = 400f;
        private const float PanelHeight = 60f;
        private const float LeftMarginFraction = 0.02f;   // text starts here
        private const float RightMarginFraction = 0.05f;  // text is cut off here
        private const float VerticalPadding = 8f;

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
        private void Awake()
        {
            // Parse hotkey.
            string hotkeyName = Hotkey?.Trim();
            if (string.IsNullOrEmpty(hotkeyName))
            {
                LilithModPlugin.Logger.LogError("[LlmChat] Hotkey is empty. LLM chat disabled.");
                _chatDisabled = true;
                return;
            }

            // Unity's input is never consulted for the hotkey - this window receives no
            // key messages at all - so only the Win32 virtual-key mapping matters.
            _vkHotkey = WindowFocus.VirtualKeyFromName(hotkeyName);
            if (_vkHotkey <= 0)
            {
                LilithModPlugin.Logger.LogError(
                    $"[LlmChat] Hotkey '{hotkeyName}' has no Win32 virtual-key mapping. "
                    + "Use a letter, digit, or F1-F12. LLM chat disabled.");
                _chatDisabled = true;
                return;
            }

            _hotkeyValid = true;

            // UI construction is deliberately NOT done here. Awake() runs when BepInEx
            // attaches the component, which is before the game's first scene exists - so
            // there is no EventSystem yet and building the UI now would disable chat
            // permanently. Update() retries until the scene is up. Same failure mode as
            // creating a GameObject in BasePlugin.Load().

            // HTTP client.
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(TimeoutSeconds + 10); // a bit beyond our cancellation timeout

            // Initialise conversation.
            _history = new List<Message>
            {
                new Message { Role = "system", Content = SystemPrompt }
            };
        }

        private void Update()
        {
            if (_chatDisabled || !_hotkeyValid)
                return;

            if (!_uiReady)
            {
                TryDeferredInit();
                return;
            }

            // --- Hotkey toggle ---
            // Polled through Win32 GetAsyncKeyState, NOT Unity. The pet window carries
            // WS_EX_NOACTIVATE|WS_EX_TRANSPARENT, so Windows never delivers key messages
            // to it and Unity's input (both new and legacy) is permanently silent here -
            // verified by probe. Global key state is the only thing that sees the press.
            bool toggle = _vkHotkey > 0 && WindowFocus.IsKeyDown(_vkHotkey);

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
                HidePanel();

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

        // TMP inherits the font asset's own default point size unless told otherwise, which
        // on the game's CJK asset is far too large for a 400x60 box - only a few characters
        // fit before it overflows. Pin the size and keep the line on one row so long input
        // scrolls horizontally instead of growing.
        // TextMeshProUGUI supplies its own default RectTransform - small and centred, not
        // filling its parent. Left that way the line sits inset from the left and the field
        // cannot scroll properly, so long input clips its tail instead of sliding its head
        // out of view.
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

            // Root canvas.
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

            // Input panel.
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

            // Text Area.
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

            // Placeholder.
            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(textArea.transform, false);
            StretchToParent(placeholderGo);
            _placeholderText = placeholderGo.AddComponent<TextMeshProUGUI>();
            _placeholderText.text = "Type a message…";
            _placeholderText.fontStyle = FontStyles.Italic;
            _placeholderText.color = Color.grey;
            ApplyTextMetrics(_placeholderText, true);

            // Text.
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
            GameStyle.Apply(_panelImage, _inputField, _inputText, _placeholderText,
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
                HidePanel();
            else
                ShowPanel();
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

            HidePanel();
            SendUserMessage(trimmed);
        }

        private void SendUserMessage(string userInput)
        {
            // Cancel previous request.
            CancelCurrentRequest();

            // Create new CTS with timeout.
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            var token = _cts.Token;
            var capturedCts = _cts; // for disposal in task

            // Append user message.
            lock (_history) _history.Add(new Message { Role = "user", Content = userInput });

            // Clone history for the request (thread-safe copy).
            List<Message> messagesSnapshot;
            lock (_history) messagesSnapshot = new List<Message>(_history);

            _currentRequest = Task.Run(async () =>
            {
                try
                {
                    string reply = await RequestCompletionAsync(messagesSnapshot, token);
                    if (!token.IsCancellationRequested)
                    {
                        // An empty completion would otherwise render as a blank bubble.
                        if (string.IsNullOrWhiteSpace(reply))
                            _replyQueue.Enqueue(new ChatResult { Ok = false, Error = "model returned an empty reply" });
                        else
                            _replyQueue.Enqueue(new ChatResult { Ok = true, Text = reply });
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        _replyQueue.Enqueue(new ChatResult { Ok = false, Error = ex.Message });
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

        private async Task<string> RequestCompletionAsync(List<Message> messages, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
                throw new InvalidOperationException("API key is not configured.");

            var payload = new
            {
                model = Model,
                messages = messages.ConvertAll(m => new { role = m.Role, content = m.Content }),
                stream = false
            };
            string jsonPayload = JsonConvert.SerializeObject(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/chat/completions"))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, token))
                {
                    response.EnsureSuccessStatusCode();
                    string responseBody = await response.Content.ReadAsStringAsync();
                    var json = JObject.Parse(responseBody);
                    var content = json["choices"]?[0]?["message"]?["content"]?.ToString();
                    if (content == null)
                        throw new InvalidOperationException("Unexpected API response structure: missing choices[0].message.content");
                    return content;
                }
            }
        }

        // ========== Queue processing on main thread ==========
        private void HandleChatResult(ChatResult result)
        {
            if (result.Ok)
            {
                // Append assistant to history.
                lock (_history) _history.Add(new Message { Role = "assistant", Content = result.Text });
                TrimHistory();

                // Update node and start dialogue.
                if (!DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
                {
                    LilithModPlugin.Logger.LogError("[LlmChat] Reply node 9500000 disappeared. Cannot display reply.");
                    return;
                }

                _replyNode.text = result.Text;
                DialogueManager.s_instance.StartDialogue(9500000);
                LilithModPlugin.Logger.LogInfo("[LlmChat] LLM reply displayed.");
            }
            else
            {
                LilithModPlugin.Logger.LogWarning($"[LlmChat] LLM request failed: {result.Error}");

                // Fallback.
                string fallback = FallbackLines[UnityEngine.Random.Range(0, FallbackLines.Length)];
                if (DialogueManager.s_instance.TryGetNode(9500000, out _replyNode))
                {
                    _replyNode.text = fallback;
                    DialogueManager.s_instance.StartDialogue(9500000);
                }
                else
                {
                    LilithModPlugin.Logger.LogError("[LlmChat] Cannot display fallback: node 9500000 missing.");
                }
                // Do NOT add fallback to history.
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
    }
}