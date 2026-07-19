Write `LilithMod/PointerFocus.cs` for the LilithMod BepInEx IL2CPP plugin.
Output only the complete C# file contents, no prose and no markdown fences.

## Why this exists
The game is a desktop pet whose window carries WS_EX_NOACTIVATE|WS_EX_TRANSPARENT, so
Windows delivers it no keyboard input and Unity's own input is permanently silent - this
was verified empirically. The mod therefore cannot use Unity mouse input or uGUI pointer
events to notice a click on its chat box. It must ask Windows directly.

Goal: detect that the user left-clicked inside a given RectTransform, so the caller can
re-focus the window and re-activate the text field.

The game exposes the needed Win32 calls as PUBLIC STATIC members of `WindowsNativeAPI`
(no namespace, in Assembly-CSharp). Use them; do NOT write your own DllImport.

    public static short  GetAsyncKeyState(int vKey)
    public static bool   GetCursorPos(ref WindowsNativeAPI.POINT lpPoint)
    public static bool   GetWindowRect(IntPtr hWnd, ref WindowsNativeAPI.RECT lpRect)
    public static IntPtr GetActiveWindow()
    public static IntPtr FindWindow(string lpClassName, string lpWindowName)

    public struct POINT { public int X; public int Y; }
    public struct RECT  { public int Left; public int Top; public int Right; public int Bottom; }

Note these take `ref` (byref) parameters, not `out`.

## What to write
`public static class PointerFocus` in namespace `LilithMod`. A plain static helper, NOT a
MonoBehaviour, so no IntPtr constructor. Log through `LilithModPlugin.Logger`. No exception
may escape any method - catch, log at most once, and return false.

Required members:

1. `public static bool LeftClickPressed()` - edge-triggered, true only on the frame the
   left mouse button transitions from up to down. Virtual key VK_LBUTTON = 0x01, high bit
   0x8000 means currently down. Keep the previous state in a static field.

2. `public static bool TryGetUnityScreenPoint(out Vector2 point)` - reads the desktop cursor
   position and converts it to Unity screen coordinates for the game window. Steps:
     - `GetCursorPos` for the absolute desktop position (origin top-left, Y grows downward).
     - Obtain the window handle: prefer `GetActiveWindow()`, and if it is IntPtr.Zero fall
       back to `FindWindow(null, "Lilith")`.
     - `GetWindowRect` for the window's desktop rectangle.
     - Convert: Unity screen space has its origin at the BOTTOM-left with Y growing upward,
       and is relative to the window, so
           x = cursor.X - rect.Left
           y = rect.Bottom - cursor.Y
     - Return false (and set point to Vector2.zero) if any call fails or the handle is zero.

3. `public static bool ClickedInside(RectTransform target)` - returns true when
   `LeftClickPressed()` is true AND `TryGetUnityScreenPoint` succeeds AND the point lies
   within `target`. Use
   `RectTransformUtility.RectangleContainsScreenPoint(target, point, null)` - the null
   camera is correct for a ScreenSpaceOverlay canvas. Return false if `target` is null.
   IMPORTANT: call `LeftClickPressed()` exactly once per invocation and store the result,
   because it is edge-triggered and calling it twice in one frame consumes the edge.

Add brief comments explaining the coordinate-system flip, since it is the non-obvious part.
