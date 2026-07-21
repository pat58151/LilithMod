using System;
using UnityEngine;
using LilithMod;

namespace LilithMod
{
    /// <summary>Detects UI clicks through Win32 when Unity input is unavailable.</summary>
    public static class PointerFocus
    {
        // Previous state for edge detection.
        private static bool? s_prevLeftDown = null;

        /// <summary>Returns true once per physical left-click.</summary>
        public static bool LeftClickPressed()
        {
            try
            {
                short state = WindowsNativeAPI.GetAsyncKeyState(0x01); // VK_LBUTTON
                bool isDown = (state & 0x8000) != 0;

                if (s_prevLeftDown.HasValue && s_prevLeftDown.Value == isDown)
                    return false;

                bool pressed = (!s_prevLeftDown.HasValue || !s_prevLeftDown.Value) && isDown;
                s_prevLeftDown = isDown;
                return pressed;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[PointerFocus] LeftClickPressed failed: {ex}");
                return false;
            }
        }

        /// <summary>Converts the desktop cursor to game-window coordinates.</summary>
        /// <param name="point">Receives the screen point (origin bottom‑left, Y up).</param>
        /// <returns><c>true</c> if the conversion succeeded; otherwise <c>false</c> and <paramref name="point"/> is set to <c>Vector2.zero</c>.</returns>
        public static bool TryGetUnityScreenPoint(out Vector2 point)
        {
            point = Vector2.zero;
            try
            {
                WindowsNativeAPI.POINT desktopPoint;
                if (!WindowsNativeAPI.GetCursorPos(out desktopPoint))
                {
                    LilithModPlugin.Logger.LogWarning("[PointerFocus] GetCursorPos failed.");
                    return false;
                }

                IntPtr hWnd = WindowsNativeAPI.GetActiveWindow();
                if (hWnd == IntPtr.Zero)
                    hWnd = WindowsNativeAPI.FindWindow(null, "Lilith");

                if (hWnd == IntPtr.Zero)
                {
                    LilithModPlugin.Logger.LogWarning("[PointerFocus] Could not obtain game window handle.");
                    return false;
                }

                WindowsNativeAPI.RECT rect;
                if (!WindowsNativeAPI.GetWindowRect(hWnd, out rect))
                {
                    LilithModPlugin.Logger.LogWarning("[PointerFocus] GetWindowRect failed.");
                    return false;
                }

                // Convert top-left desktop coordinates to bottom-left Unity coordinates.
                float x = desktopPoint.X - rect.Left;
                float y = rect.Bottom - desktopPoint.Y;
                point = new Vector2(x, y);
                return true;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[PointerFocus] TryGetUnityScreenPoint failed: {ex}");
                point = Vector2.zero;
                return false;
            }
        }

        /// <summary>Returns true when a click lands inside an overlay rectangle.</summary>
        /// <param name="target">The UI rectangle to test against. If <c>null</c>, immediately returns <c>false</c>.</param>
        public static bool ClickedInside(RectTransform target)
        {
            try
            {
                if (target == null)
                    return false;

                // Consume the click edge once.
                if (!LeftClickPressed())
                    return false;

                if (!TryGetUnityScreenPoint(out Vector2 point))
                    return false;

                return RectTransformUtility.RectangleContainsScreenPoint(target, point, null);
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[PointerFocus] ClickedInside failed: {ex}");
                return false;
            }
        }
    }
}
