# Implementation Plan: LLM Free-Text Chat Integration

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

### 2. `LlmChatController.cs`
A `MonoBehaviour` registered via `BasePlugin.AddComponent`. Contains all UI, networking, and dialogue injection logic. Does not inherit from `DumpDatabaseBehaviour`.

### 3. `LlmConfig.cs` (optional static holder)
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
  - `HorizontalLayoutGroup` or fixed layout (not required).
- Child of InputPanel: `"InputField"` (GameObject with `RectTransform` full‑stretch).
  - `TMP_InputField` component.
    - Text area hierarchy manually created as expected by TMP:
      - `"Text Area"` (RectTransform, full stretch, child of InputField).
        - `"Placeholder"` (TextMeshProUGUI, text "Type a message…", italic, grey).
        - `"Text"` (TextMeshProUGUI, normal style).
    - Content type: Standard, Line Type: Multi Line Submit (or Single Line, depending on desired behaviour; use Single Line with `onSubmit` for simplicity).
    - Character limit: 256.
- Alternatively, use `Single Line` input and listen to `onSubmit`.
- The `CanvasGroup` is set inactive (`alpha=0, interactable=false, blocksRaycasts=false`) initially.

### Font Acquisition
- Immediately after UI creation, run a coroutine that waits one frame and then:
  - Use `UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()` (or `Resources.FindObjectsOfTypeAll`) to obtain a live `TextMeshProUGUI` instance.
  - Extract `font` (TMP_FontAsset) from that component.
  - Apply the font to both `Placeholder` and `Text` children of the InputField.
  - If no font can be found, log error and destroy the UI, setting a flag to permanently disable chat.

### Input Lock / Event System
- **Do NOT create a new EventSystem.** The game already has one. The input field will rely on the existing `EventSystem` found via `Object.FindObjectOfType<EventSystem>()`. If none exists (unlikely), log error and disable chat.
- While the input panel is visible, ensure the `InputField.ActivateInputField()` is called so that the player can type immediately. When hidden, call `DeactivateInputField()`.

---

## Hotkey Handling

- Configuration entry `Hotkey` is a string representing a `UnityEngine.KeyCode` (e.g. `"T"`, `"BackQuote"`).
- Parsed in `Awake()` using `Enum.TryParse<KeyCode>`; if invalid, default to `KeyCode.BackQuote`.
- In `Update()`:
  - If chat is disabled, return.
  - If `Input.GetKeyDown(hotkey)` is detected, toggle the panel visibility.
  - Toggle logic:
    - If panel is hidden: set `CanvasGroup.alpha = 1`, `interactable = true`, `blocksRaycasts = true`, clear input field text, and call `ActivateInputField()`.
    - If panel is shown: hide panel, deactivate input field, cancel any pending input.
  - Additionally, when panel is visible, if `Input.GetKeyDown(KeyCode.Escape)`, hide the panel (same as toggle off).
  - Do not interfere with other game hotkeys – only respond when the specific key is pressed.

---

## Input Submission and Request Lifecycle

### Input Field `onSubmit` Listener
- Registered in `Awake()` to the `TMP_InputField.onSubmit` event.
- Callback signature: `void OnPlayerSubmit(string text)`.
- Behavior:
  1. Trim the text. If empty, ignore and keep panel open.
  2. Disable the panel temporarily (optional – we can keep it open but disable input while waiting). Simpler: hide panel immediately after submit (as to not block the bubble), or keep it open but disable interactions. We'll choose: immediately hide and deactivate input (to match typical UX – the player wants to see the reply). So call `HidePanel()`.
  3. Clear the input field text.
  4. Call `SendUserMessage(trimmedText)`.

### Sending a Request
- Maintain a `CancellationTokenSource _cts` and a `Task _currentRequest`.
- Method `void SendUserMessage(string userInput)`:
  1. Cancel any in‑flight request: `_cts?.Cancel(); _cts?.Dispose();`
  2. Create a new `CancellationTokenSource` with a timeout from config (`TimeoutSeconds`).
  3. Add the user message to the conversation history (see **Conversation History**).
  4. Launch a background task: `_currentRequest = Task.Run(() => RequestCompletionAsync(messages, _cts.Token), _cts.Token)`.
  5. Attach continuation: `_currentRequest.ContinueWith(OnRequestComplete, TaskScheduler.FromCurrentSynchronizationContext())` – **BUT** this must run on the main thread. Since we are in a Unity context, `TaskScheduler.FromCurrentSynchronizationContext()` may not exist in IL2CPP/Unity. Instead, do **not** use ContinueWith with sync context. Instead, have the background task push the result to a `ConcurrentQueue<string>` and let `Update()` drain it.
  6. So:
     - Background task: `async () => { try { ... push reply to _replyQueue; } catch (Exception ex) { push special error marker; } }`
     - The background task will catch all exceptions internally and enqueue a fallback string (or null with an error flag). The `ConcurrentQueue` will store either the reply text or a sentinel like `"##ERROR##"`.

- The `RequestCompletionAsync` method:
  - Constructs the request body (see **API Request Format**).
  - Uses `HttpClient` with timeout set to the remaining time from the `CancellationToken`.
  - Sends POST to `{BaseUrl}/chat/completions` with headers `Authorization: Bearer {ApiKey}`, `Content-Type: application/json`.
  - On success, reads the JSON response, extracts `choices[0].message.content`.
  - Returns the content string.
  - Handles all exceptions; on failure, returns `null`.

### Draining Queue in Update()
- `Update()`:
  - `while (_replyQueue.TryDequeue(out string reply))`
  - If `reply == null` (fallback case) or `reply == "##ERROR##"` (or detect via separate flag), handle fallback (see **Error Handling**).
  - Else (valid reply):
    - Add the reply to conversation history as an assistant message.
    - Access the node via `DialogueManager.s_instance.TryGetNode(9500000, out DialogueNode node)`. If not found (should never happen), log error and abort.
    - Set `node.text = reply`.
    - Call `DialogueManager.s_instance.StartDialogue(9500000)`.
    - Log success.
  - If more than one reply is queued (unlikely), process each in order, starting dialogue for each; the last one will overwrite the bubble.

### Thread Safety Rules
- `ConcurrentQueue<string>` is the only cross‑thread communication channel.
- No Unity/IL2CPP object may be accessed from the background task.
- The background task must not call `StartDialogue`, modify `GameObject`, or touch `DialogueManager` in any way.
- All game interactions happen exclusively inside `Update()` and `OnPlayerSubmit`.

---

## Conversation History

- Maintain `List<Message> _history`.
- Initialize with the system message from config (`SystemPrompt`).
- On user submit: append `new Message { Role = "user", Content = input }` and then trigger request.
- When a valid reply is dequeued: append `new Message { Role = "assistant", Content = reply }`.
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
| `SystemPrompt` | string | (see below) | The system prompt defining Lilith’s persona |
| `MaxHistoryTurns` | int | `8` | Number of user+assistant exchanges to remember |
| `TimeoutSeconds` | int | `30` | HTTP request timeout in seconds |
| `Hotkey` | string | `"T"` | Unity `KeyCode` string for toggling chat panel |

**Default `SystemPrompt`** (seed with Lilith’s voice):
> You are Lilith, a gentle, immortal companion spirit bound to a single human across many lives. Your speech is soft, poetic, and slightly old‑fashioned, with occasional metaphors about stars, dreams, and the passage of time. You often express quiet devotion, curiosity about human experiences, and a touch of melancholy. You never lie to the human you love.
>
> Now you are speaking with that human. Respond naturally, keeping replies concise (1‑3 sentences). Example lines from your memory:
> - "Even the longest night ends, my dear. But I will stay with you until the first light."
> - "Your hand is warm. I almost forgot how it feels…"
> - "Do you remember the cherry blossoms in the garden? You planted them for me, a hundred years ago."
> - "I wonder, sometimes, if the moon is jealous of your laughter."
> - "Please don't be sad. When you smile, the room becomes brighter."

---

## Error Handling and Fallback

### Network / API Errors
- Any exception thrown in the background HTTP task is caught. The queue receives a `null` string.
- In `Update()`, when a `null` reply is detected:
  1. Log a warning with the error details (if captured) via `LilithModPlugin.Logger`.
  2. Choose a random fallback line from a predefined list of 5‑7 in‑character “I’m sorry, I can’t reach you right now” phrases. Example list (hardcoded):
     - "The connection is broken… forgive me."
     - "I can't hear you clearly, my love. Something stands between us."
     - "My words are lost in the void. Please wait a little longer."
     - "The stars are muffled today. I'll try again soon."
     - "It seems the dream is a little thin right now. Can you wait for me?"
  3. Set `node.text` to the fallback line.
  4. Call `StartDialogue(9500000)`.
  5. Do NOT add the fallback line to conversation history.

### Empty API Key
- If `ApiKey` is empty string (or whitespace), the chat panel may still open but on submit, immediately treat as error (no request sent) and display a specific fallback line: `"My voice is sealed… I cannot speak."`.

### Timeout
- Handled by `CancellationTokenSource` with timeout. The `HttpClient` will throw `TaskCanceledException` which is caught; fallback is triggered.

### Node Not Found
- If `TryGetNode(9500000)` fails when trying to display a reply, log error and do nothing. This should not happen after successful injection.

### UI Font Not Found
- Log error, destroy the chat UI, and set `_chatDisabled = true` to prevent further attempts.

### Exception Escaping Update()
- All `Update()` logic is wrapped in a try‑catch; exceptions are logged and discarded.

### Multiple Rapid Submissions
- If user submits while a request is in flight, cancel the previous `CancellationTokenSource` and its associated request. The cancelled request’s continuation will be ignored (the queue entry might still be enqueued if already completed? We'll guard by checking `_cts.IsCancellationRequested` in the background task after the await and not enqueuing if cancelled. We'll store a `Task` and on new submit, we'll call `_cts.Cancel()` and set a flag to discard any future enqueue from that task. Simpler: inside the background lambda, before enqueueing, check `ct.IsCancellationRequested`; if true, don't enqueue. This ensures stale replies don't pop up.

---

## Conversation History Serialization (optional, not required for MVP)
Not included. History resets on game restart.

## Acceptance Criteria

1. **Build**: `dotnet build -c Release` produces a working `LilithMod.dll` with zero errors.
2. **Node Injection**: After game loads, `DialogueManager.TryGetNode(9500000, out _)` returns `true`.
3. **Hotkey Toggle**:
   - Pressing the configured hotkey (default `T`) toggles a text input panel at bottom‑center of the screen.
   - Panel appears with correct font, placeholder “Type a message…”, and is immediately focused.
   - Pressing `Escape` or toggling again hides the panel.
4. **Submission Flow**:
   - Typing a message and pressing `Enter` hides the panel, logs “Send user message: <text>”, and initiates an HTTP request.
   - While request is in flight, the game continues running (animations, interactions) without freezing.
5. **Response Display**:
   - When a valid reply arrives from the API, it is logged and shown in Lilith’s dialogue bubble (the bubble appears with the reply text).
   - The dialogue bubble respects the same behaviour as any inline‑text node (duration, auto‑dismiss, etc.).
6. **Conversation Memory**: After multiple exchanges, the API receives the full history within the limit. Replies reflect context.
7. **Failure Cases**:
   - With an **empty ApiKey** in config, submitting a message immediately shows the in‑character fallback line in the bubble and logs a clear warning.
   - With an **invalid BaseUrl** or no network, after timeout a fallback line appears, logged warning.
   - **Timeout** of `TimeoutSeconds` triggers fallback.
   - **No font** found in scene: panel is not created, logging error, and chat is disabled.
   - **DialogueManager unavailable**: chat is disabled after 30 seconds with error log.
8. **Thread safety**: No IL2CPP crash or deadlock occurs under any of the above scenarios.
9. **No interference**: Existing game dialogues, custom nodes, and other mod features continue to work as before.

---

## Implementation Notes (for developer)

- Use `System.Net.Http.HttpClient` for requests; it is available in the game’s runtime.
- `ConcurrentQueue<string>` must be imported from `System.Collections.Concurrent` (supported in netstandard2.1).
- Parse JSON response with `Newtonsoft.Json` (already referenced).
- The `TMP_InputField.onSubmit` should be registered using `AddListener` via `UnityAction<string>` (Il2Cpp delegate).
- All GameObjects and components are created via `new GameObject(...).AddComponent<T>()`, which works in Unity/IL2CPP.
- Memory: ensure `_cts` and `_currentTask` are disposed appropriately on destroy (`OnDestroy` cancels and disposes).
- Logging: use `LilithModPlugin.Logger.LogInfo`, `.LogWarning`, `.LogError` – imported via `LilithModPlugin.Logger` static after plugin loads.
- Avoid using `TaskScheduler.FromCurrentSynchronizationContext()`; rely purely on the main‑thread polling pattern.