using System;
using System.Collections.Generic;
using LilithMod; // ensure Logger is accessible

namespace LilithMod
{
    public static class WindowFocus
    {
        // Edge-triggered state: maps virtual-key to whether the key was down on the previous call.
        private static readonly Dictionary<int, bool> s_prevKeyDown = new Dictionary<int, bool>();

        // Saved original extended window style, so EnableTyping() is idempotent.
        private static IntPtr? s_originalExStyle = null;
        private static bool s_chatInputActive;
        private static bool s_settingsInputActive;
        // Whether click-through is currently suspended, so Begin/End stay balanced.
        private static bool s_beganKeyboardInput;

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const byte VK_MENU = 0x12;          // ALT
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint SW_SHOW = 5;
        private const uint WS_EX_TRANSPARENT = 0x00000020;

        /// <summary>
        /// Returns true only on the frame the key transitions from up to down.
        /// Uses Windows <c>GetAsyncKeyState</c> directly.
        /// </summary>
        public static bool IsKeyDown(int vKey)
        {
            try
            {
                short state = WindowsNativeAPI.GetAsyncKeyState(vKey);
                bool isDown = (state & 0x8000) != 0;

                if (s_prevKeyDown.TryGetValue(vKey, out bool prev) && prev == isDown)
                    return false;

                // Rising edge: not down last time, down now
                bool pressed = !prev && isDown;

                s_prevKeyDown[vKey] = isDown;
                return pressed;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] IsKeyDown({vKey}) failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Returns true for as long as the key is physically down, with no edge
        /// detection. Push-to-talk needs the held state, not the transition, and it
        /// must not disturb the edge history <see cref="IsKeyDown"/> keeps per key.
        /// </summary>
        public static bool IsKeyHeld(int vKey)
        {
            try
            {
                return (WindowsNativeAPI.GetAsyncKeyState(vKey) & 0x8000) != 0;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] IsKeyHeld({vKey}) failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Converts a config string to a Win32 virtual-key code.
        /// Supports A-Z, 0-9, and F1-F12 (case-insensitive). Returns -1 if unknown.
        /// </summary>
        public static int VirtualKeyFromName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return -1;

            try
            {
                string upper = name.ToUpperInvariant().Trim();

                // Single letter A-Z
                if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
                {
                    return 0x41 + (upper[0] - 'A');
                }

                // Single digit 0-9
                if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
                {
                    return 0x30 + (upper[0] - '0');
                }

                // F1-F12
                if (upper.StartsWith("F") && upper.Length >= 2 && upper.Length <= 3)
                {
                    if (int.TryParse(upper.Substring(1), out int num) && num >= 1 && num <= 12)
                    {
                        return 0x70 + (num - 1);
                    }
                }

                return -1;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] VirtualKeyFromName('{name}') failed: {ex}");
                return -1;
            }
        }

        /// <summary>
        /// Removes <c>WS_EX_NOACTIVATE</c> and <c>WS_EX_TRANSPARENT</c> from the game window
        /// so it can receive keystrokes. Idempotent: calling it multiple times does not
        /// overwrite the saved original style.
        /// </summary>
        public static void EnableTyping()
        {
            s_chatInputActive = true;
            EnterKeyboardMode();
        }

        /// <summary>
        /// Makes the window able to receive keystrokes WITHOUT taking the mouse.
        ///
        /// Only WS_EX_NOACTIVATE is cleared, because that is all keyboard delivery
        /// needs - hit-testing is a separate concern. The per-pixel click-through the
        /// game runs is deliberately left alone, so the desktop stays usable while the
        /// chat bar is open, and clicks still land on the bar itself because it is
        /// opaque.
        ///
        /// The settings path still uses EnterInteractiveMode, which additionally
        /// clears WS_EX_TRANSPARENT and suspends click-through. Doing that for chat is
        /// what made opening the bar capture the entire screen.
        /// </summary>
        private static void EnterKeyboardMode()
        {
            try
            {
                IntPtr hWnd = GetGameWindowHandle();
                if (hWnd == IntPtr.Zero)
                {
                    LilithModPlugin.Logger.LogWarning("[WindowFocus] EnableTyping: could not obtain window handle.");
                    return;
                }

                if (s_originalExStyle == null)
                    s_originalExStyle = WindowsNativeAPI.GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE);

                long current = (long)WindowsNativeAPI.GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE);
                long newStyle = current & ~(long)WS_EX_NOACTIVATE;
                WindowsNativeAPI.SetWindowLongPtrSafe(hWnd, GWL_EXSTYLE, (IntPtr)newStyle);

                // Same foreground rule as EnterInteractiveMode: Windows refuses
                // SetForegroundWindow unless we own the foreground or sent the last
                // input event, so synthesise an ALT tap to qualify.
                if (!WindowsNativeAPI.SetForegroundWindow(hWnd))
                {
                    WindowsNativeAPI.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    WindowsNativeAPI.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    WindowsNativeAPI.ShowWindow(hWnd, SW_SHOW);
                    WindowsNativeAPI.SetForegroundWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] EnableTyping failed: {ex}");
            }
        }

        public static void SetSettingsInteractive(bool enabled)
        {
            if (s_settingsInputActive == enabled)
                return;

            s_settingsInputActive = enabled;
            if (enabled)
                EnterInteractiveMode(false);
            else
                LeaveInteractiveMode();
        }

        private static void EnterInteractiveMode(bool takeFocus)
        {
            try
            {
                IntPtr hWnd = GetGameWindowHandle();
                if (hWnd == IntPtr.Zero)
                {
                    LilithModPlugin.Logger.LogWarning("[WindowFocus] EnableTyping: could not obtain window handle.");
                    return;
                }

                // Save before the native component clears click-through flags.
                if (s_originalExStyle == null)
                    s_originalExStyle = WindowsNativeAPI.GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE);
                if (!s_beganKeyboardInput)
                {
                    TransparentWindowNew.BeginKeyboardInput();
                    s_beganKeyboardInput = true;
                }

                // Clear the NOACTIVATE and TRANSPARENT bits while leaving everything else intact.
                long current = (long)WindowsNativeAPI.GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE);
                long newStyle = current & ~((long)WS_EX_NOACTIVATE | (long)WS_EX_TRANSPARENT);

                WindowsNativeAPI.SetWindowLongPtrSafe(hWnd, GWL_EXSTYLE, (IntPtr)newStyle);

                // Windows only grants SetForegroundWindow to a process that already owns
                // the foreground or received the last input event. Once the pet loses
                // foreground, plain SetForegroundWindow is refused (returns false) and the
                // window can never take keyboard focus again - which is exactly why typing
                // worked on the first open and never after. Synthesising an ALT keypress
                // makes our process the last input source, lifting that restriction. This
                // is the long-standing documented workaround for the rule.
                bool fg = true;
                if (takeFocus)
                {
                    fg = WindowsNativeAPI.SetForegroundWindow(hWnd);
                    if (!fg)
                    {
                        WindowsNativeAPI.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                        WindowsNativeAPI.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        WindowsNativeAPI.ShowWindow(hWnd, SW_SHOW);
                        fg = WindowsNativeAPI.SetForegroundWindow(hWnd);
                    }
                }

                if (LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value)
                {
                    long applied = (long)WindowsNativeAPI.GetWindowLongPtrSafe(hWnd, GWL_EXSTYLE);
                    long fgWnd = (long)WindowsNativeAPI.GetForegroundWindow();
                    LilithModPlugin.Logger.LogInfo(
                        $"[WindowFocus] hWnd=0x{(long)hWnd:X} old=0x{current:X} want=0x{newStyle:X} "
                        + $"applied=0x{applied:X} setFg={fg} fgWnd=0x{fgWnd:X} "
                        + $"noactivate={(applied & (long)WS_EX_NOACTIVATE) != 0} "
                        + $"transparent={(applied & (long)WS_EX_TRANSPARENT) != 0}");
                }
                else if (!fg)
                {
                    LilithModPlugin.Logger.LogWarning(
                        "[WindowFocus] Could not bring the window to the foreground; typing may not work.");
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] EnableTyping failed: {ex}");
            }
        }

        /// <summary>
        /// Restores the original extended window style that was saved by <c>EnableTyping</c>.
        /// Safe to call when nothing was saved (no-op).
        /// </summary>
        public static void RestoreWindow()
        {
            s_chatInputActive = false;
            LeaveInteractiveMode();
        }

        private static void LeaveInteractiveMode()
        {
            try
            {
                if (s_chatInputActive || s_settingsInputActive)
                    return;

                if (s_originalExStyle == null)
                    return;

                IntPtr hWnd = GetGameWindowHandle();
                if (hWnd == IntPtr.Zero)
                {
                    LilithModPlugin.Logger.LogWarning("[WindowFocus] RestoreWindow: could not obtain window handle. Saved style will be discarded.");
                    s_originalExStyle = null;
                    return;
                }

                WindowsNativeAPI.SetWindowLongPtrSafe(hWnd, GWL_EXSTYLE, s_originalExStyle.Value);
                s_originalExStyle = null;
                // Resume character click-through only after the last interactive panel
                // closes, and only if it was ever suspended. The chat bar's keyboard
                // mode never suspends it, so ending it here would be unbalanced.
                if (s_beganKeyboardInput)
                {
                    TransparentWindowNew.EndKeyboardInput();
                    s_beganKeyboardInput = false;
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] RestoreWindow failed: {ex}");
                // To avoid leaving the mod in an inconsistent state, discard the saved style on error.
                s_originalExStyle = null;
            }
        }

        /// <summary>
        /// Tries to get the game window handle, preferring <c>GetActiveWindow</c> with a fallback
        /// to <c>FindWindow(null, "Lilith")</c>.
        /// </summary>
        private static IntPtr GetGameWindowHandle()
        {
            IntPtr hWnd = WindowsNativeAPI.GetActiveWindow();
            if (hWnd != IntPtr.Zero)
                return hWnd;

            return WindowsNativeAPI.FindWindow(null, "Lilith");
        }
    }
}
