using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace AES_Emulation.Windows.API
{
    public static class Win32API
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const long WS_BORDER = 0x00800000;
        private const long WS_DLGFRAME = 0x00400000;
        private const long WS_CAPTION = WS_BORDER | WS_DLGFRAME; // 0x00C00000
        private const long WS_SYSMENU = 0x00080000;
        private const long WS_THICKFRAME = 0x00040000;
        private const long WS_MINIMIZEBOX = 0x00020000;
        private const long WS_MAXIMIZEBOX = 0x00010000;

        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;
        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_TOPMOST = 0x00000008;

        private const uint LWA_ALPHA = 0x00000002;

        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;

        // storage for restoring
        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _savedStyles = new();
        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _savedMenus = new();
        private static readonly ConcurrentDictionary<IntPtr, RECT> _savedRects = new();
        private static readonly ConcurrentDictionary<IntPtr, bool> _savedChildVisibility = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        // Get/SetWindowLongPtr compat
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrPlatform(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrPlatform(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtrPlatform(hWnd, nIndex);
            else
                return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtrPlatform(hWnd, nIndex, dwNewLong);
            else
                return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetMenu(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetMenu(IntPtr hWnd, IntPtr hMenu);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawMenuBar(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)]
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            [MarshalAs(UnmanagedType.U4)]
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_CLOAK = 14;

        /// <summary>
        /// Removes decorations (title bar, border, system menu, sizing boxes) and removes menu from the target window.
        /// Saves previous styles/menus so they can be restored with RestoreWindowDecorations.
        /// </summary>
        public static bool RemoveWindowDecorations(IntPtr hwnd, bool hideFromTaskbar = false)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                // Save style
                if (!_savedStyles.ContainsKey(hwnd))
                {
                    _savedStyles[hwnd] = GetWindowLongPtrCompat(hwnd, GWL_STYLE);
                }

                IntPtr oldStyle = _savedStyles[hwnd];
                long style = oldStyle.ToInt64();
                long remove = WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                long newStyle = style & ~remove;
                SetWindowLongPtrCompat(hwnd, GWL_STYLE, new IntPtr(newStyle));

                // Remove menu if present
                try
                {
                    IntPtr menu = GetMenu(hwnd);
                    if (menu != IntPtr.Zero && !_savedMenus.ContainsKey(hwnd))
                    {
                        _savedMenus[hwnd] = menu;
                        SetMenu(hwnd, IntPtr.Zero);
                        DrawMenuBar(hwnd);
                    }
                }
                catch { }

                // Do not change extended styles (do not remove from taskbar) at this time.

                // Save rect
                try
                {
                    if (!_savedRects.ContainsKey(hwnd))
                    {
                        if (GetWindowRect(hwnd, out RECT r)) _savedRects[hwnd] = r;
                    }
                }
                catch { }

                // Apply frame changed
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
                HideDecorativeChildWindows(hwnd);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restores styles, menus and extended styles previously removed by RemoveWindowDecorations.
        /// </summary>
        public static bool RestoreWindowDecorations(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                if (_savedStyles.TryRemove(hwnd, out IntPtr savedStyle))
                {
                    SetWindowLongPtrCompat(hwnd, GWL_STYLE, savedStyle);
                }

                if (_savedMenus.TryRemove(hwnd, out IntPtr savedMenu))
                {
                    try
                    {
                        SetMenu(hwnd, savedMenu);
                        DrawMenuBar(hwnd);
                    }
                    catch { }
                }

                // No extended-style restoration necessary since we did not modify it.

                if (_savedRects.TryRemove(hwnd, out RECT r))
                {
                    try
                    {
                        int width = Math.Max(0, r.Right - r.Left);
                        int height = Math.Max(0, r.Bottom - r.Top);
                        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, width, height, SWP_NOZORDER);
                    }
                    catch { }
                }

                RestoreDecorativeChildWindows(hwnd);

                // Notify frame changed
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Move the target window out of the visible area and optionally attempt to cloak it via DWM.
        /// Saves the previous window rect (if not already saved) so it can be restored later.
        /// </summary>
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        public static bool MoveAway(IntPtr hwnd, bool useCloak = false)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                // Save rect if not already saved
                try
                {
                    if (!_savedRects.ContainsKey(hwnd))
                    {
                        if (GetWindowRect(hwnd, out RECT r)) _savedRects[hwnd] = r;
                    }
                }
                catch { }

                // Try to cloak via DWM if requested (best-effort)
                if (useCloak)
                {
                    try
                    {
                        int cloak = 1;
                        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
                    }
                    catch { }
                }

                int virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                // Keep the window inside the virtual desktop so WGC can still capture it.
                // Place the target near the bottom-right of the virtual desktop with minimal on-screen exposure.
                int destX = virtualLeft + Math.Max(0, virtualWidth - 100);
                int destY = virtualTop + Math.Max(0, virtualHeight - 100);

                SetWindowPos(hwnd, IntPtr.Zero, destX, destY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public static bool SetWindowTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            }
            catch
            {
                return false;
            }
        }

        public static bool SetWindowNotTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            }
            catch
            {
                return false;
            }
        }

        public static bool SetWindowSize(IntPtr hwnd, int width, int height)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsWindowTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var exStyle = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE).ToInt64();
                return (exStyle & WS_EX_TOPMOST) == WS_EX_TOPMOST;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Give focus to the emulator target. If hostHwnd is provided the host will be made briefly topmost
        /// during activation and then restored so the target does not remain in front.
        /// </summary>
        public static void ForceEmulatorFocus(IntPtr targetHwnd, IntPtr hostHwnd = default, int restoreDelayMs = 50)
        {
            if (targetHwnd == IntPtr.Zero) return;

            // We intentionally avoid changing host Z-order here to prevent the emulator briefly appearing behind
            // or in front of the host. Only use thread attachment and focus messages to transfer focus.

            IntPtr foregroundHwnd = Win32Focus.GetForegroundWindow();
            uint foregroundThreadId = Win32Focus.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
            uint targetThreadId = Win32Focus.GetWindowThreadProcessId(targetHwnd, IntPtr.Zero);

            if (foregroundThreadId != targetThreadId)
            {
                // 1. Attach threads so we can share input state
                Win32Focus.AttachThreadInput(foregroundThreadId, targetThreadId, true);

                // 2. Set foreground, focus and activation
                Win32Focus.SetForegroundWindow(targetHwnd);
                Win32Focus.SetFocus(targetHwnd);
                Win32Focus.SendMessage(targetHwnd, Win32Focus.WM_ACTIVATE, Win32Focus.WA_CLICKACTIVE, IntPtr.Zero);

                // 3. Detach
                Win32Focus.AttachThreadInput(foregroundThreadId, targetThreadId, false);
            }
            else
            {
                // Already same thread context but maybe not foreground.
                Win32Focus.SetForegroundWindow(targetHwnd);
                Win32Focus.SetFocus(targetHwnd);
            }

            // Do not alter host Z-order here. Caller can manage z-order separately if needed.
        }

        /// <summary>
        /// Set the window opacity (0..255). Adds WS_EX_LAYERED if necessary.
        /// </summary>
        public static bool SetWindowOpacity(IntPtr hwnd, byte alpha)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                IntPtr ex = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);
                long exVal = ex.ToInt64();
                if ((exVal & WS_EX_LAYERED) == 0)
                {
                    exVal |= WS_EX_LAYERED;
                    SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, new IntPtr(exVal));
                }
                return SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            }
            catch { return false; }
        }

        public static bool MinimizeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return ShowWindow(hwnd, SW_MINIMIZE); }
            catch { return false; }
        }

        public static bool RestoreWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return ShowWindow(hwnd, SW_RESTORE); }
            catch { return false; }
        }

        /// <summary>
        /// Calculates the offsets and size of the client area relative to the window rect.
        /// Useful for WGC cropping to capture only the game content.
        /// </summary>
        public static bool GetClientAreaOffsets(IntPtr hwnd, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            if (hwnd == IntPtr.Zero) return false;

            if (!GetWindowRect(hwnd, out RECT wr)) return false;
            if (!GetClientRect(hwnd, out RECT cr)) return false;

            POINT pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;

            // In DPI-aware processes (like modern .NET 9 apps), Win32 APIs like GetWindowRect, 
            // GetClientRect, and ClientToScreen return physical pixels on the current monitor.
            // WGC also operates in physical pixels, so we can use these values directly without further scaling.
            x = pt.X - wr.Left;
            y = pt.Y - wr.Top;
            width = cr.Right - cr.Left;
            height = cr.Bottom - cr.Top;

            return true;
        }

        /// <summary>
        /// Attempts to retrieve primary GPU information (Renderer and Vendor) via EnumDisplayDevices.
        /// </summary>
        public static void GetPrimaryGpuInfo(out string renderer, out string vendor)
        {
            renderer = "Unknown";
            vendor = "Unknown";

            try
            {
                DISPLAY_DEVICE d = new DISPLAY_DEVICE();
                d.cb = Marshal.SizeOf(d);

                for (uint i = 0; EnumDisplayDevices(null, i, ref d, 0); i++)
                {
                    // Check if this is the primary device (bit 2 of StateFlags)
                    // If multiple GPUs are present, we usually want the one with StateFlags |= 4
                    if ((d.StateFlags & 4) != 0)
                    {
                        renderer = d.DeviceString;
                        
                        // Parse vendor from DeviceID if possible: PCI\VEN_1002...
                        if (!string.IsNullOrEmpty(d.DeviceID) && d.DeviceID.Contains("VEN_"))
                        {
                            var parts = d.DeviceID.Split('&');
                            foreach (var p in parts)
                            {
                                if (p.Contains("VEN_"))
                                {
                                    var venId = p.Substring(p.IndexOf("VEN_") + 4).ToUpper();
                                    vendor = venId switch
                                    {
                                        "1002" => "AMD",
                                        "10DE" => "NVIDIA",
                                        "8086" => "Intel",
                                        _ => vendor
                                    };
                                    break;
                                }
                            }
                        }
                        
                        // If vendor parsing failed but renderer has name, try simple matches
                        if (vendor == "Unknown")
                        {
                            if (renderer.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) vendor = "NVIDIA";
                            else if (renderer.Contains("AMD", StringComparison.OrdinalIgnoreCase) || renderer.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
                            else if (renderer.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
                        }
                        return;
                    }
                }
            }
            catch { }
        }

        private static void HideDecorativeChildWindows(IntPtr parentHwnd)
        {
            try
            {
                if (parentHwnd == IntPtr.Zero || !GetWindowRect(parentHwnd, out RECT parentRect))
                    return;

                int parentWidth = Math.Max(0, parentRect.Right - parentRect.Left);
                int parentHeight = Math.Max(0, parentRect.Bottom - parentRect.Top);
                if (parentWidth <= 0 || parentHeight <= 0)
                    return;

                EnumChildWindows(parentHwnd, (child, _) =>
                {
                    if (child == IntPtr.Zero || !IsWindowVisible(child))
                        return true;

                    try
                    {
                        if (!GetWindowRect(child, out RECT childRect))
                            return true;

                        if (!IsLikelyDecorativeChild(parentRect, parentWidth, parentHeight, child, childRect))
                            return true;

                        _savedChildVisibility.TryAdd(child, true);
                        ShowWindow(child, 0);
                        SetWindowPos(child, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                    catch
                    {
                        // Ignore individual child failures and continue hiding the rest.
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static void RestoreDecorativeChildWindows(IntPtr parentHwnd)
        {
            try
            {
                if (parentHwnd == IntPtr.Zero)
                    return;

                EnumChildWindows(parentHwnd, (child, _) =>
                {
                    if (child != IntPtr.Zero && _savedChildVisibility.TryRemove(child, out bool wasVisible))
                    {
                        try
                        {
                            ShowWindow(child, wasVisible ? 1 : 0);
                        }
                        catch
                        {
                            // Ignore restore issues per child.
                        }
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                // Best-effort only.
            }
        }

        private static bool IsLikelyDecorativeChild(RECT parentRect, int parentWidth, int parentHeight, IntPtr childHwnd, RECT childRect)
        {
            int childWidth = Math.Max(0, childRect.Right - childRect.Left);
            int childHeight = Math.Max(0, childRect.Bottom - childRect.Top);
            if (childWidth <= 0 || childHeight <= 0)
                return false;

            string className = GetWindowClassName(childHwnd);
            string title = GetWindowTitle(childHwnd);

            bool classMatches =
                className.Contains("Qt", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("QWindow", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("QMenu", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("QToolBar", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("QStatusBar", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("ToolbarWindow32", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("msctls_statusbar32", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("ReBarWindow32", StringComparison.OrdinalIgnoreCase);

            bool textMatches =
                title.Contains("FPS", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Video:", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Tools", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Help", StringComparison.OrdinalIgnoreCase);

            int offsetTop = childRect.Top - parentRect.Top;
            int offsetBottom = parentRect.Bottom - childRect.Bottom;
            bool dockedToTopOrBottom = offsetTop <= 140 || offsetBottom <= 140;
            bool spansMostWidth = childWidth >= (int)(parentWidth * 0.65);
            bool shortBar = childHeight <= Math.Min(140, Math.Max(48, parentHeight / 5));

            return shortBar && dockedToTopOrBottom && (spansMostWidth || classMatches || textMatches);
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(256);
                return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(256);
                return GetWindowText(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
