# Corrected Implementation Plan: LLM Free-Text Chat Integration

## Overview
Add real-time LLM-powered conversation to the existing LilithMod BepInEx plugin. The player opens a text input via a hotkey, types a message, and Lilith replies in her normal dialogue bubble using a response from a remote OpenAI‑compatible API. The implementation extends the plugin with a new `MonoBehaviour` component, reusing proven rendering machinery and respecting strict IL2CPP threading constraints.

## New Classes and File Additions

### 1. `Message.cs`
A plain C# class (not IL2CPP) representing a conversation turn. Used only on the managed side.

```csharp
public class Message
{
    public string Role { get; set; }   // "system", "user", "assistant"
    public string Content { get; set; }
}
```

### 2. `ChatResult.cs`
A plain C# result carrier for the thread‑safe queue. Holds only managed types – never used with Il2Cpp objects.

```csharp
public sealed class ChatResult
{
    public bool Ok { get; set; }
    public string Text { get; set; }   // valid reply when Ok == true
    public string Error { get; set; }  // error description when Ok == false
}
```

### 3. `LlmChatController.cs`
A `MonoBehaviour` registered via `BasePlugin.AddComponent`. Contains all UI, networking, and dialogue injection logic. Does not inherit from `DumpDatabaseBehaviour`.

### 4. `LlmConfig.cs` (optional static holder)
Static class holding parsed configuration values, filled by `LilithModPlugin.Load()` before any component is added.

---

## Component Registration and Lifetime

- In `LilithModPlugin.Load()`:
  1. Read configuration keys (see **Configuration** section) and store them in `LlmConfig` static fields or pass them to a method on `LlmChatController`.
  2. Call `AddComponent<LlmChatController>()`.
- The component lives for the entire game session. It handles its own destruction on application quit.

---

## Injection of Reserved Dialogue Node

### Node Id: `9500000`

### Procedure (executed once, when `DialogueManager` is ready):
1. In `LlmChatController.Start()`, launch a coroutine `EnsureDefaultNode()`.
2. Coroutine waits until `DialogueManager.s_instance` is not null (timeout 30s, log error and abort if timed out).
3. After instance found, wait one frame (`yield return null`) to ensure all databases have completed `Awake()`/`OnEnable()`.
4. Call `DialogueManager.s_instance.TryGetNode(9500000, out _)`.
   - If true, the node already exists – do nothing more.
   - If false, create the node and inject it:
     a. Locate the `DialogueDatabase` with name `"DialogueNode"` (or fallback to `_databases[0]` if not found).
     b. Create a new `DialogueNode` with:
        - `id = 9500000`
        - `speaker = "lilith"`
        - `lineId = 0`
        - `text = ""` (initial empty)
        - `emotion = ""`
        - `duration = 5.0f`
        - `actionType = (LilithActionType)(-1)`
        - `nextStateType = (DialogueStateType)0`
        - `nextStateDuration = 0f`
        - `soundId = ""`
        - `nextId = -1`
        - `playerLineInteraction = ""`
        - `triggerTypes = new Il2CppSystem.Collections.Generic.List<DialogueTriggerType>()`
        - `playerStates = new Il2CppSystem.Collections.Generic.List<string>()`
        - `options = new Il2CppSystem.Collections.Generic.List<DialogueOption>()`
        - `playerLineOptions = new Il2CppSystem.Collections.Generic.List<DialoguePlayerLineOption>()`
        - `conditions = new DialogueCondition { timeRangeStart = "00:00", timeRangeEnd = "23:59", dateMMdd = "" }`
        - `baseWeight = 1`
     c. Add the node to the target database’s `nodes` list.
     d. Call `targetDb.BuildIndex()`.
     e. Call `DialogueManager.s_instance.BuildIndex()`.
     f. Call `DialogueManager.s_instance.RegisterNodeWeight(newNode)`.
     g. Store a reference to the node (obtained via `TryGetNode`) in `_replyNode`.
5. If node could not be injected after all steps, log error and disable LLM chat functionality.

---

## User Interface Setup

### Canvas and Components
- Created in `LlmChatController.Awake()`.
- Root GameObject: `"LlmChatCanvas"`.
  - `Canvas` (Screen Space – Overlay, `sortingOrder = 100`).
  - `CanvasScaler` (UI Scale Mode = Scale With Screen Size, Reference Resolution 1920×1080).
  - `GraphicRaycaster`.
  - `CanvasGroup` (controls visibility/raycast).
- Child: `"InputPanel"` (RectTransform, anchored to bottom-center, 400×60).
  - `Image` component (color: black with ~80% alpha).
- Child of InputPanel: `"InputField"` (GameObject with `RectTransform` full‑stretch).
  - **Create the child hierarchy BEFORE adding the `TMP_InputField` component.**
  - Child: `"Text Area"` (RectTransform, full stretch).
    - `"Placeholder"` (TextMeshProUGUI, text “Type a message…”, italic, grey).
    - `"Text"` (TextMeshProUGUI, normal style).
  - After the children exist, add `TMP_InputField` component to the `InputField` GameObject and then assign:
    - `inputField.textViewport = "Text Area" RectTransform`
    - `inputField.textComponent = "Text" TextMeshProUGUI`
    - `inputField.placeholder = "Placeholder" TextMeshProUGUI`
  - Then set `inputField.contentType = TMP_InputField.ContentType.Standard`, `lineType = TMP_InputField.LineType.SingleLine`, `characterLimit = 256`.
- The `CanvasGroup` is set inactive (`alpha=0, interactable=false, blocksRaycasts=false`) initially.

### Font Acquisition
- Immediately after UI creation, run a coroutine that waits one frame and then:
  - Use `UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()` (or `Resources.FindObjectsOfTypeAll`) to obtain a live `TextMeshProUGUI` instance.
  - Extract its `font` (TMP_FontAsset).
  - Apply the font to both `Placeholder` and `Text` components **BEFORE they are attached to the InputField properties** (assign font to the TextMeshProUGUI objects directly after creation).
  - If no font can be found, log error and destroy the UI, setting a flag to permanently disable chat.

### Input Lock / Event System
- **Do NOT create a new EventSystem.** Use the existing one via `Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>()`. If none exists (unlikely), log error and disable chat.
- While the input panel is visible, call `inputField.ActivateInputField()`. When hidden, call `DeactivateInputField()`.

---

## Hotkey Handling

### Primary path – New Input System
- Add a reference to `<game>\BepInEx\interop\Unity.InputSystem.dll` in the .csproj.
- Configuration entry `Hotkey` is a string representing a `UnityEngine.KeyCode` (e.g. `"T"`, `"BackQuote"`).
- Map the `KeyCode` name to the InputSystem `Key` enum. Use `Enum.TryParse<UnityEngine.InputSystem.Key>(hotkeyName, ignoreCase: true, out var inputSystemKey)`.
- In `Update()`, read `Keyboard.current?[inputSystemKey].wasPressedThisFrame` (null‑safe).
- If the press is registered, toggle panel visibility.

### Fallback path – Legacy Input
- Only used as a guarded fallback. Wrap the legacy `Input.GetKeyDown(hotkey)` in a try/catch.
- Do NOT call it every frame unconditionally; instead, detect once in `Awake()` whether legacy Input is available by testing `Input.GetKeyDown(KeyCode.None)` inside try/catch. If it throws, set `_legacyInputAvailable = false` and never try again.
- Only in `Update()` if `_legacyInputAvailable` and the new Input System did not detect a press, then try the legacy path (with an inner try/catch to be safe).
- If both paths fail to detect the hotkey, log one clear error in `Awake()` naming the problem and disable chat.

### Toggle logic
- If panel is hidden: show it (`CanvasGroup.alpha = 1`, `interactable = true`, `blocksRaycasts = true`), clear input text, call `ActivateInputField()`.
- If panel is shown: hide it, deactivate input field, cancel any pending input.
- Additionally, when panel is visible, if `Escape` key is pressed (detected via InputSystem `Keyboard.current.escapeKey.wasPressedThisFrame`), hide the panel.
- Do not interfere with other game hotkeys – only respond when the specific key is pressed.

---

## Input Submission and Request Lifecycle

### Input Field `onSubmit` Listener
- Registered in `Awake()` to the `TMP_InputField.onSubmit` event using `AddListener`.
- Callback signature: `void OnPlayerSubmit(string text)`.
- Behavior:
  1. Trim the text. If empty, ignore and keep panel open.
  2. Hide the panel immediately (hide and deactivate input).
  3. Clear the input field text.
  4. Call `SendUserMessage(trimmedText)`.

### Sending a Request
- Maintain a `CancellationTokenSource _cts` (the current request’s token source) and a `Task _currentRequest`.
- Method `void SendUserMessage(string userInput)`:
  1. Cancel any in‑flight request: `_cts?.Cancel();`
     **Do not dispose `_cts` here.** The previous task still holds a reference to its own token; disposing would cause `ObjectDisposedException` on the background thread.
  2. Create a new `CancellationTokenSource` with a timeout from config (`TimeoutSeconds * 1000`). Store it in `_cts`.
  3. Add the user message to the conversation history.
  4. Capture a local copy of the token: `var token = _cts.Token;` (important – do not let the background task touch `_cts` directly).
  5. Launch a background task:
     ```csharp
     _currentRequest = Task.Run(async () =>
     {
         try
         {
             string reply = await RequestCompletionAsync(messages, token);
             if (!token.IsCancellationRequested)
                 _replyQueue.Enqueue(new ChatResult { Ok = true, Text = reply });
             // If cancelled, simply do nothing.
         }
         catch (Exception ex)
         {
             if (!token.IsCancellationRequested)
                 _replyQueue.Enqueue(new ChatResult { Ok = false, Error = ex.Message });
         }
         // Finally, dispose the CTS that this task owns
         finally
         {
             // Only dispose the one we created; it’s safe because the task is about to end.
             // But be careful: the CTS might have been reassigned already. To avoid races,
             // we dispose the local `token`'s source? Better to store the source in a local.
             // We'll store the current CTS in a local before Task.Run:
             // var localCts = _cts; then in finally: localCts?.Dispose();
             // However, _cts is replaced on next submit. But the old CTS is no longer
             // referenced except by this task. Safer: in the finally, dispose `_cts` if
             // it’s the same object we captured. We'll capture the CTS object itself.
         }
     }, token);
     ```
  6. The background task must **never** access `_cts` or any Unity object. It only uses the local `token` and `_replyQueue`.
  7. The `_currentRequest` reference is not used further; it’s kept to avoid UnobservedTaskException (fire‑and‑forget is safe because we enqueue the result). We don’t await it.

- **Important**: To safely dispose the old CTS, capture the previous `_cts` into a local variable inside `SendUserMessage` before assigning the new one, then pass it to the background task (or use a lambda that captures it). In the task’s `finally`, dispose that captured source if it’s not been disposed already. This ensures each CTS is disposed exactly once after its task completes, and never while in use.

- The `RequestCompletionAsync` method:
  - Constructs the request body (JSON with model, messages, stream: false).
  - Uses `HttpClient` with timeout set via the CancellationToken.
  - Sends POST to `{BaseUrl}/chat/completions` with headers `Authorization: Bearer {ApiKey}`, `Content-Type: application/json`.
  - On success, reads the JSON response, extracts `choices[0].message.content`.
  - Returns the content string.
  - All exceptions are caught in the outer task; on failure, the task enqueues `ChatResult` with `Ok = false` and the error message.

### Draining Queue in Update()
- `Update()`:
  - `while (_replyQueue.TryDequeue(out ChatResult result))`
  - If `result.Ok` is false (or null, but result is never null), handle fallback (see **Error Handling**).
  - Else (valid reply):
    - Add the reply to conversation history as an assistant message.
    - Access the node via `DialogueManager.s_instance.TryGetNode(9500000, out DialogueNode node)`. If not found, log error and abort.
    - Set `node.text = result.Text`.
    - Call `DialogueManager.s_instance.StartDialogue(9500000)`.
    - Log success.

### Thread Safety Rules
- `ConcurrentQueue<ChatResult>` is the only cross‑thread communication channel.
- No Unity/IL2CPP object may be accessed from the background task.
- The background task must not call `StartDialogue`, modify `GameObject`, or touch `DialogueManager` in any way.
- All game interactions happen exclusively inside `Update()` and `OnPlayerSubmit`.

---

## Conversation History

- Maintain `List<Message> _history`.
- Initialize with the system message from config (`SystemPrompt`).
- On user submit: append `new Message { Role = "user", Content = input }` and then trigger request.
- When a valid reply is dequeued: append `new Message { Role = "assistant", Content = result.Text }`.
- Once the history count (excluding system) exceeds `MaxHistoryTurns * 2`, remove the oldest user/assistant pair (i.e., keep the system prompt static and trim the conversation from the beginning, dropping user then assistant).
- The system message is always first and never trimmed.

---

## Configuration

### BepInEx Config Section: `[LLM]`
Defined in `LilithModPlugin.Load()` before adding components.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `BaseUrl` | string | `"https://api.deepseek.com/v1"` | Base URL for OpenAI‑compatible endpoint |
| `ApiKey` | string | `""` | API key (never hardcoded; users set in config) |
| `Model` | string | `"deepseek-chat"` | Model name sent in request body |
| `SystemPrompt` | string | (see original) | The system prompt defining Lilith’s persona |
| `MaxHistoryTurns` | int | `8` | Number of user+assistant exchanges to remember |
| `TimeoutSeconds` | int | `30` | HTTP request timeout in seconds |
| `Hotkey` | string | `"T"` | Unity `KeyCode` string for toggling chat panel |

**Default `SystemPrompt`** remains as originally specified.

---

## Error Handling and Fallback

### Network / API Errors
- Any exception in the background HTTP task is caught; the queue receives a `ChatResult` with `Ok = false`.
- In `Update()`, when a result with `Ok == false` is dequeued:
  1. Log a warning with `result.Error`.
  2. Choose a random fallback line from a predefined list of 5‑7 in‑character “I’m sorry, I can’t reach you right now” phrases (hardcoded list as originally specified).
  3. Set `_replyNode.text` to the fallback line.
  4. Call `StartDialogue(9500000)`.
  5. Do NOT add the fallback line to conversation history.

### Empty API Key
- If `ApiKey` is empty or whitespace, the chat panel may still open but on submit, immediately treat as error (no request sent) and display a specific fallback line: `"My voice is sealed… I cannot speak."`.

### Timeout
- Handled by the CancellationTokenSource with timeout. The HttpClient will throw `TaskCanceledException` which is caught; fallback is triggered.

### Node Not Found
- If `TryGetNode(9500000)` fails when trying to display a reply, log error and do nothing.

### UI Font Not Found
- Log error, destroy the chat UI, and set `_chatDisabled = true`.

### Exception in Update()
- All `Update()` logic is wrapped in a try‑catch; exceptions are logged and discarded.

### Multiple Rapid Submissions
- When a new submit occurs, the previous request’s CancellationToken is cancelled (but not disposed). The old task will detect cancellation and not enqueue any result. The discarded task’s CTS is eventually disposed in its own finally block. No stale replies appear.

### Input System Failure
- If both new Input System and legacy Input fail (e.g. Keyboard.current is null and Input.GetKeyDown throws), log one clear error and disable chat permanently.

---

## Build and Reference Adjustments

- In the `.csproj`, add a reference to the interop assembly `Unity.InputSystem.dll`. Ensure the path points to `<game>\BepInEx\interop\Unity.InputSystem.dll`.
- Confirm that all other existing references (including `Unity.InputSystem.ForUI.dll` if used) are present.
- After building, verify the mod loads without missing type exceptions.

## Acceptance Criteria (updated)

1. **Build**: `dotnet build -c Release` produces a working `LilithMod.dll` with zero errors.
2. **Node Injection**: After game loads, `DialogueManager.TryGetNode(9500000, out _)` returns `true`.
3. **Hotkey Toggle**:
   - Pressing the configured hotkey (default `T`) toggles the text input panel.
   - Works reliably regardless of whether the game uses Legacy or new Input System.
   - If both input systems are unavailable, chat is gracefully disabled with a clear log message.
4. **Submission Flow**: Typing and Enter hides panel, logs send, and initiates HTTP request without freezing the game.
5. **Response Display**: Valid reply appears in Lilith’s dialogue bubble exactly as specified.
6. **Conversation Memory**: History maintained, contexts reflected.
7. **Failure Cases**:
   - Empty ApiKey: immediate fallback line.
   - Invalid BaseUrl or network loss: fallback after timeout.
   - Font missing: chat disabled with error.
   - DialogueManager unavailable: disabled after timeout.
8. **Thread safety**: No IL2CPP crashes, no disposed object usage, no cross‑thread Unity calls.
9. **No interference**: Existing game dialogues continue to work.

---

## Implementation Notes (corrected)

- **Input System**: Use `UnityEngine.InputSystem.Keyboard.current` as primary, with null‑safe access. Map `KeyCode` string to `Key` enum via `Enum.TryParse`. For legacy fallback, test availability once with a try/catch on `Input.GetKeyDown(KeyCode.None)`.
- **TMP InputField assembly**: Create children, add TextMeshProUGUI with fonts assigned, then add TMP_InputField and set its `textViewport`, `textComponent`, `placeholder` properties. Set other properties after assignment.
- **CTS disposal**: Each background task captures the CTS it owns. In its `finally` block, dispose that captured CTS object. The `SendUserMessage` method captures the previous CTS into a local, cancels it, and passes it to the new task via closure. Do not dispose from the main thread.
- **Result queue**: Use `ConcurrentQueue<ChatResult>`; no sentinel strings.
- **Logging**: `LilithModPlugin.Logger.LogInfo/LogWarning/LogError`.
- **JSON**: Use `Newtonsoft.Json`.
- **IL2CPP collections**: Use `Il2CppSystem.Collections.Generic.List<>` for node fields as originally specified.
- **Cooldown/cancel**: Ensure that when a new submit cancels the old one, the old task does not enqueue, and the old CTS is eventually disposed.