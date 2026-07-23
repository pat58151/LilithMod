using System;
using System.Collections.Generic;
using LilithMod; // ensure Logger is accessible

namespace LilithMod
{
    public static class WindowFocus
    {
        // Previous key state for edge detection.
        private static readonly Dictionary<int, bool> s_prevKeyDown = new Dictionary<int, bool>();

        // Original style restored when input closes.
        private static IntPtr? s_originalExStyle = null;
        private static bool s_chatInputActive;
        private static bool s_settingsInputActive;
        internal static bool ModInputActive => s_chatInputActive || s_settingsInputActive;
        // Tracks balanced click-through suspension.
        private static bool s_beganKeyboardInput;

        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const byte VK_MENU = 0x12;          // ALT
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint SW_SHOW = 5;
        private const uint WS_EX_TRANSPARENT = 0x00000020;

        // SetWindowPos constants
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>Returns true on the up-to-down transition.</summary>
        public static bool IsKeyDown(int vKey)
        {
            try
            {
                short state = WindowsNativeAPI.GetAsyncKeyState(vKey);
                bool isDown = (state & 0x8000) != 0;

                if (s_prevKeyDown.TryGetValue(vKey, out bool prev) && prev == isDown)
                    return false;

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

        /// <summary>Returns the physical key state without changing edge history.</summary>
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

        /// <summary>Converts a supported key name to a Win32 virtual-key code.</summary>
        public static int VirtualKeyFromName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return -1;

            try
            {
                string upper = name.ToUpperInvariant().Trim();

                if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
                {
                    return 0x41 + (upper[0] - 'A');
                }

                if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
                {
                    return 0x30 + (upper[0] - '0');
                }

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

        /// <summary>Makes the game window interactive and focusable.</summary>
        public static void EnableTyping()
        {
            s_chatInputActive = true;
            EnterKeyboardMode();
        }

        /// <summary>Enables keyboard input while preserving desktop click-through.</summary>
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
                long newStyle = current & ~((long)WS_EX_NOACTIVATE | (long)WS_EX_TRANSPARENT);
                WindowsNativeAPI.SetWindowLongPtrSafe(hWnd, GWL_EXSTYLE, (IntPtr)newStyle);

                // An ALT tap satisfies Windows foreground activation rules.
                if (!WindowsNativeAPI.SetForegroundWindow(hWnd))
                {
                    WindowsNativeAPI.keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    WindowsNativeAPI.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    WindowsNativeAPI.ShowWindow(hWnd, SW_SHOW);
                    WindowsNativeAPI.SetForegroundWindow(hWnd);
                }

                // Bring window to top of Z-order for click priority.
                SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
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

                // Windows may require recent input before granting foreground focus.
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
                    // Bring window to top of Z-order for click priority.
                    SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
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

        /// <summary>Restores the saved window style.</summary>
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
                // Resume click-through only when this class suspended it.
                if (s_beganKeyboardInput)
                {
                    TransparentWindowNew.EndKeyboardInput();
                    s_beganKeyboardInput = false;
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[WindowFocus] RestoreWindow failed: {ex}");
                // Do not reuse a style after a failed restore.
                s_originalExStyle = null;
            }
        }

        /// <summary>Finds the active or named game window.</summary>
        private static IntPtr GetGameWindowHandle()
        {
            IntPtr hWnd = WindowsNativeAPI.GetActiveWindow();
            if (hWnd != IntPtr.Zero)
                return hWnd;

            return WindowsNativeAPI.FindWindow(null, "Lilith");
        }
    }
}
