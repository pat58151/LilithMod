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
using Il2CppInterop.Runtime.Attributes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Input = UnityEngine.Input;

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
        private bool _legacyInputAvailable;
        private Key _inputSystemKey;
        private KeyCode _legacyKeyCode;
        private bool _hotkeyValid;

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

            if (!Enum.TryParse(hotkeyName, true, out _inputSystemKey))
            {
                LilithModPlugin.Logger.LogError($"[LlmChat] Invalid hotkey '{hotkeyName}' for Input System. LLM chat disabled.");
                _chatDisabled = true;
                return;
            }

            // Legacy is only a fallback, and its KeyCode names do not all match the Input
            // System's Key names. Failing to parse here must not disable chat.
            bool legacyKeyParsed = Enum.TryParse(hotkeyName, true, out _legacyKeyCode);

            try
            {
                Input.GetKeyDown(KeyCode.None);
                _legacyInputAvailable = legacyKeyParsed;
            }
            catch
            {
                _legacyInputAvailable = false;
            }

            // If both new Input System and legacy are unavailable, disable.
            // Keyboard.current may be null in some stripped builds; we'll try to fetch it.
            if (Keyboard.current == null && !_legacyInputAvailable)
            {
                LilithModPlugin.Logger.LogError("[LlmChat] No usable input system found. LLM chat disabled.");
                _chatDisabled = true;
                return;
            }

            _hotkeyValid = true;

            // Build UI (font is asynchronously assigned).
            BuildChatUILayout();
            MonoBehaviourExtensions.StartCoroutine(this, FinishUISetup());

            // Dialogue node injection.
            MonoBehaviourExtensions.StartCoroutine(this, EnsureDefaultNode());

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

            // --- Hotkey toggle ---
            bool toggle = false;
            try
            {
                // New Input System (primary)
                if (Keyboard.current != null)
                {
                    toggle = Keyboard.current[_inputSystemKey].wasPressedThisFrame;
                }
            }
            catch { /* silent */ }

            if (!toggle && _legacyInputAvailable)
            {
                try
                {
                    toggle = Input.GetKeyDown(_legacyKeyCode);
                }
                catch { /* silent */ }
            }

            if (toggle)
                TogglePanel();

            // Escape to close panel.
            if (IsPanelVisible() && Keyboard.current?.escapeKey.wasPressedThisFrame == true)
                HidePanel();

            // Enter submits. Polled rather than bound to onSubmit - see BuildChatUILayout.
            if (IsPanelVisible() && Keyboard.current != null
                && (Keyboard.current.enterKey.wasPressedThisFrame
                    || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
            {
                OnPlayerSubmit(_inputField != null ? _inputField.text : null);
            }

            // --- Drain reply queue (main thread only) ---
            while (_replyQueue.TryDequeue(out ChatResult result))
                HandleChatResult(result);
        }

        private void OnDestroy()
        {
            CancelCurrentRequest();
            _cts?.Dispose();
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
            panelRect.sizeDelta = new Vector2(400, 60);
            var panelImg = inputPanel.AddComponent<Image>();
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
            textAreaRect.offsetMin = Vector2.zero;
            textAreaRect.offsetMax = Vector2.zero;

            // Placeholder.
            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(textArea.transform, false);
            _placeholderText = placeholderGo.AddComponent<TextMeshProUGUI>();
            _placeholderText.text = "Type a message…";
            _placeholderText.fontStyle = FontStyles.Italic;
            _placeholderText.color = Color.grey;

            // Text.
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(textArea.transform, false);
            _inputText = textGo.AddComponent<TextMeshProUGUI>();
            _inputText.color = Color.white;

            // Initially hidden.
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
        }

        private IEnumerator FinishUISetup()
        {
            yield return null; // one frame to let game UI spawn

            // Obtain font from a live game TextMeshProUGUI.
            var gameText = UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>();
            if (gameText == null)
                gameText = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()[0] as TextMeshProUGUI;

            if (gameText == null || gameText.font == null)
            {
                LilithModPlugin.Logger.LogError("[LlmChat] Failed to obtain a game font. LLM chat disabled.");
                Destroy(_canvas);
                _chatDisabled = true;
                yield break;
            }

            _placeholderText.font = gameText.font;
            _inputText.font = gameText.font;

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
            {
                _inputField.text = "";
                _inputField.ActivateInputField();
            }
        }

        private void HidePanel()
        {
            if (_canvasGroup == null) return;
            _canvasGroup.alpha = 0;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _inputField?.DeactivateInputField();
            if (_inputField != null) _inputField.text = "";
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
            _cts?.Cancel();
            // Do not dispose here; disposed in task's finally.
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