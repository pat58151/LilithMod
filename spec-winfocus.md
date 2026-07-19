Write `LilithMod/WindowFocus.cs` for the LilithMod BepInEx IL2CPP plugin.
Output only the complete C# file contents, no prose and no markdown fences.

## Why this exists
The game is a desktop pet whose window carries
`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, so Windows never
delivers keyboard messages to it. Unity therefore reports NO key input at all - this was
verified empirically: `Keyboard.current.anyKey.wasPressedThisFrame` and legacy
`Input.anyKeyDown` are both permanently false while `Application.isFocused` misleadingly
reports true. Any hotkey or TMP_InputField relying on Unity input silently does nothing.

The game already exposes the Win32 calls we need as PUBLIC STATIC members of
`WindowsNativeAPI` (namespace-less, in Assembly-CSharp). Use them - do NOT write your own
DllImport, because a P/Invoke declared in an IL2CPP mod assembly is fragile.

Available on `WindowsNativeAPI` (exact signatures):
    public static short  GetAsyncKeyState(int vKey)
    public static IntPtr GetActiveWindow()
    public static IntPtr FindWindow(string lpClassName, string lpWindowName)
    public static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
    public static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    public static bool   SetForegroundWindow(IntPtr hWnd)

## What to write
A `public static class WindowFocus` in namespace `LilithMod`. It is a plain static helper -
NOT a MonoBehaviour, so it needs no IntPtr constructor. Log through
`LilithModPlugin.Logger`. No exception may escape any method; catch, log once, and degrade.

Required members:

1. `public static bool IsKeyDown(int vKey)` - edge-triggered. Returns true only on the
   frame the key transitions from up to down. `GetAsyncKeyState` returns a short whose high
   bit (0x8000) means currently down. Keep a small internal dictionary of previous states
   keyed by vKey so repeated calls per key work independently.

2. `public static int VirtualKeyFromName(string name)` - maps a config string to a Win32
   virtual-key code. Support at minimum single letters A-Z (0x41 + offset), digits 0-9
   (0x30 + offset), and F1-F12 (0x70 + offset). Case-insensitive. Return -1 if unknown.

3. `public static void EnableTyping()` - makes the window able to receive keystrokes:
   get the handle (prefer `GetActiveWindow()`; if it returns IntPtr.Zero, fall back to
   `FindWindow(null, "Lilith")`), read the current ex-style with
   `GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE)` where `GWL_EXSTYLE = -20`, SAVE that original
   value in a static field, then write back the style with `WS_EX_NOACTIVATE` (0x08000000)
   and `WS_EX_TRANSPARENT` (0x00000020) bits CLEARED, and finally call
   `SetForegroundWindow(hWnd)`. Idempotent: calling it twice must not overwrite the saved
   original with an already-modified value.

4. `public static void RestoreWindow()` - writes the saved original ex-style back and
   clears the saved state. Safe to call when nothing was saved (no-op). This MUST be
   reliable: if it does not run, the pet permanently stops being click-through.

Use `System.IntPtr` arithmetic carefully - convert to `long` for the bit operations and
back to `IntPtr`. Include brief comments explaining the ex-style bits, since the magic
numbers are otherwise opaque.
