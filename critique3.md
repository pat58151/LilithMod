# Rewrite request: plan-step3.md FAILED review. Fix these defects.

Produce a corrected, complete plan in the same format and scope. Output only the fixed
plan; do not restate this critique. Everything not listed here is accepted as-is.

## FATAL 1 - legacy UnityEngine.Input may throw; the hotkey would never work
Section "Hotkey Handling" polls `Input.GetKeyDown(hotkey)`. This game ships
`Unity.InputSystem.dll` AND `Unity.InputSystem.ForUI.dll` in its interop assemblies,
meaning the NEW Input System is in use (InputSystemUIInputModule drives the UI). If the
project's active input handling is set to "Input System Package (New)" only, every call to
legacy `UnityEngine.Input` throws InvalidOperationException. We cannot determine the
setting statically, so the plan must be robust either way.

Required: read the hotkey through the new Input System as the PRIMARY path -
`UnityEngine.InputSystem.Keyboard.current` and `.wasPressedThisFrame` (add
`<game>\BepInEx\interop\Unity.InputSystem.dll` to the csproj references). Keep legacy
`Input.GetKeyDown` only as a guarded fallback wrapped in try/catch, and if BOTH paths fail,
log one clear error naming the problem and disable chat rather than throwing every frame.
Map the configured KeyCode name to the InputSystem `Key` enum.

## FATAL 2 - TMP_InputField is not wired up, only its children are created
The UI section creates "Text Area", "Placeholder" and "Text" GameObjects but never assigns
them to the component. A TMP_InputField built this way throws or renders nothing, because
it resolves its own children through explicit properties. The plan MUST state that after
creating the hierarchy you assign:
- `inputField.textViewport` = the Text Area RectTransform
- `inputField.textComponent` = the "Text" TextMeshProUGUI
- `inputField.placeholder`   = the "Placeholder" TextMeshProUGUI
and only then set `text`, `characterLimit`, and line type. Also state that the
TextMeshProUGUI components must exist and have their font assigned BEFORE being attached to
these properties, and that the InputField component should be added after its children
exist.

## FATAL 3 - CancellationTokenSource is disposed while a request still uses it
"Sending a Request" step 1 does `_cts?.Cancel(); _cts?.Dispose();`. The previous background
task may still be running and holding that token; touching a disposed CTS throws
ObjectDisposedException on the background thread. Required: cancel but do NOT dispose
eagerly. Capture `var token = _cts.Token;` into a local BEFORE `Task.Run` and pass the
local into the lambda - never dereference the field from inside the background task, since
the field may be reassigned by a newer submit. Dispose the old CTS only from within the
task that owned it (finally block), or simply drop the reference and let GC handle it.

## DEFECT 4 - the error sentinel can collide with real model output
The queue is `ConcurrentQueue<string>` carrying either a reply, `null`, or the literal
`"##ERROR##"`. A model can legitimately emit that string, and using both null AND a
sentinel gives two mechanisms for one condition. Required: define one small plain result
type, e.g. `private sealed class ChatResult { public bool Ok; public string Text; public
string Error; }`, and use `ConcurrentQueue<ChatResult>`. It must be a plain managed class
holding only managed types - never an Il2Cpp type, since it crosses threads.

## DEFECT 5 - the threading section contradicts itself
Step 5 of "Sending a Request" proposes `ContinueWith(..., TaskScheduler.
FromCurrentSynchronizationContext())`, then immediately says not to do that and to use the
queue instead. Delete the ContinueWith branch entirely. State only the final design: the
background task catches everything, enqueues exactly one ChatResult, and `Update()` on the
main thread drains the queue and performs all game interaction. Leaving both in invites the
implementer to write the wrong one.
