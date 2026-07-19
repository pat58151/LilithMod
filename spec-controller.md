Write `LilithMod/LlmChatController.cs` - the single remaining file for step 3 of the
LilithMod BepInEx plugin. Implement it EXACTLY per the attached plan-step3-approved.md.
Output only the complete C# file contents, no prose and no markdown fences.

Already written, do not redefine them: `Message` (Role/Content) and `ChatResult`
(Ok/Text/Error) - see attached Message.cs and ChatResult.cs. Use them as-is.

The class is `public class LlmChatController : MonoBehaviour` in namespace `LilithMod`.

Non-negotiable requirements (violating any of these breaks the game at runtime):
- It is an Il2Cpp-injected MonoBehaviour, so it MUST declare
  `public LlmChatController(System.IntPtr ptr) : base(ptr) { }`.
- It has no `Log` property. Log through `LilithModPlugin.Logger`.
- Hotkey via the NEW Input System: `UnityEngine.InputSystem.Keyboard.current`, using
  `.wasPressedThisFrame`, null-safe. Guarded legacy `UnityEngine.Input` fallback in
  try/catch. If both fail, log ONE error and disable chat permanently - never throw
  every frame.
- The background HTTP task MUST NOT touch any Unity or Il2Cpp object. It produces a
  `ChatResult` and enqueues it on a `ConcurrentQueue<ChatResult>`. `Update()` on the main
  thread drains that queue and does ALL game interaction.
- Capture `var token = _cts.Token;` into a local BEFORE `Task.Run` and pass the local in.
  Never dereference the `_cts` field from inside the background task. Do NOT dispose the
  old CTS eagerly when a new request starts.
- Catch every exception on every path, including inside the background task. Nothing may
  escape into the game.
- TMP_InputField wiring: create the child GameObjects, add TextMeshProUGUI components,
  assign their fonts, THEN add TMP_InputField and set `textViewport`, `textComponent`
  and `placeholder`. Setting only the children without these properties makes it throw.
- Font: obtain a `TMP_FontAsset` from a live game `TextMeshProUGUI` at runtime. The TMP
  default is Latin-only and would render Chinese as blank boxes.
- Reuse the EXISTING EventSystem via `Object.FindObjectOfType`; never create a second one.
- Reply output: set the text of the reserved node id 9500000 and call
  `DialogueManager.s_instance.StartDialogue(9500000)`.
- Config is read from BepInEx `ConfigFile` entries exposed as statics on
  `LilithModPlugin` (BaseUrl, ApiKey, Model, SystemPrompt, MaxHistoryTurns,
  TimeoutSeconds, Hotkey). Assume they exist as `LilithModPlugin.CfgBaseUrl` etc. of type
  `BepInEx.Configuration.ConfigEntry<T>`; read `.Value`.
- The HTTP call is an OpenAI-compatible POST to `{BaseUrl}/chat/completions`, header
  `Authorization: Bearer {ApiKey}`, JSON body with `model` and `messages`. Parse
  `choices[0].message.content`. Newtonsoft.Json is available.
- If ApiKey is empty, do not make a request: enqueue a ChatResult with Ok=false and a
  clear error, so the fallback line shows.
