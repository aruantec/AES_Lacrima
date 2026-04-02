using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System;

namespace AES_Emulation.Windows.API
{
    public static class Win32Focus
    {
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_ACTIVATE = 0x0006;
        public static readonly IntPtr WA_CLICKACTIVE = new IntPtr(2);
    }

    public static class Win32TaskbarHider
    {
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
        }

        [ComImport]
        [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarList { }

        public static void HideFromTaskbar(IntPtr hwnd)
        {
            try
            {
                ITaskbarList taskbarList = (ITaskbarList)new TaskbarList();
                taskbarList.HrInit();
                taskbarList.DeleteTab(hwnd); // Removes the icon without merging
            }
            catch { /* Fallback for non-windows */ }
        }

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        public static void MakeInvisibleGhost(IntPtr hwnd)
        {
            // We do NOT use WS_EX_TRANSPARENT here because that breaks Focus Tunneling.
            // We only use WS_EX_LAYERED for Alpha 0.
            int style = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, style | 0x80000); // WS_EX_LAYERED
            SetLayeredWindowAttributes(hwnd, 0, 0, 0x2); // LWA_ALPHA
        }
    }

    public static class Win32Stealth
    {
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20; // Click-through
        const int LWA_ALPHA = 0x2;

        public static void ApplyInvisibleStealth(IntPtr hwnd)
        {
            // 1. Make the window Layered so we can control opacity
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            // WS_EX_TRANSPARENT makes it click-through so it doesn't block your app
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_LAYERED | WS_EX_TRANSPARENT);

            // 2. Set Opacity to 0 (Invisible but still "Rendering" for WGC)
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        }

        [DllImport("user32.dll")]
        static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        private static IntPtr _dummyOwner = IntPtr.Zero;

        public static void RemoveFromTaskbarSafe(IntPtr targetHwnd)
        {
            if (_dummyOwner == IntPtr.Zero)
            {
                // Create a hidden "Owner" window. 
                // Any window owned by a hidden window disappears from the taskbar.
                _dummyOwner = CreateWindowEx(0, "Static", "Owner", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }

            // Set the emulator's OWNER to our hidden dummy window
            // Note: SetWindowLong with GWL_HWNDPARENT sets the OWNER, not the PARENT.
            // This is safe for WGC.
            SetWindowLong(targetHwnd, -8, (int)_dummyOwner);
        }
    }

    public static class Win32InvisibleCapture
    {
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int LWA_ALPHA = 0x2;

        public static void ApplyGhostStealth(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            // 1. Hide from Taskbar and make Click-Through
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT);

            // 2. Set Alpha to 1 (NOT 0). 
            // 1/255 opacity is invisible to the eye but "Active" to the DWM.
            SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);

            // 3. Keep it ON-SCREEN but tiny. 
            // If it's off-screen, WGC will freeze. If it's 1x1 at (0,0), it stays alive.
            SetWindowPos(hwnd, new IntPtr(1), 0, 0, 1, 1, 0x0010 | 0x0040);
        }
    }

    public static class WindowsStealth
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int DWMWA_CLOAK = 14; // Cloaks the window
        public const int DWMWA_CLOAKED = 13; // Checks if cloaked
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public static void HideWindowFromUser(IntPtr hwnd)
        {
            int cloak = 1; // 1 = Cloak, 0 = Uncloak
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            //SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static void RemoveFromTaskbar(IntPtr hwnd)
        {
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            style |= WS_EX_TOOLWINDOW;   // Add tool window (hides from taskbar)
            style &= ~WS_EX_APPWINDOW;   // Remove app window (forces hide from taskbar)
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }

        public static void MoveAway(IntPtr hwnd)
        {
            SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    public static class WindowDecorationHelper
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        private const uint WS_BORDER = 0x00800000;
        private const uint WS_DLGFRAME = 0x00400000;
        private const uint WS_CAPTION = WS_BORDER | WS_DLGFRAME; // 0x00C00000
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_THICKFRAME = 0x00040000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_MAXIMIZEBOX = 0x00010000;

        // Extended styles for taskbar/toolwindow handling
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const long WS_EX_APPWINDOW = 0x00040000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _savedStyles = new();
        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _savedMenus = new();

        // New: save extended styles and window rects so we can restore them later
        private static readonly ConcurrentDictionary<IntPtr, IntPtr> _savedExStyles = new();
        private static readonly ConcurrentDictionary<IntPtr, RECT> _savedRects = new();
        private static readonly ConcurrentDictionary<IntPtr, bool> _savedVisibility = new();

        // RECT struct for Win32 interop
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        // Compatibility helpers for Get/SetWindowLongPtr across x86/x64
        private static IntPtr GetWindowLongPtrCompat(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtrPlatform(hWnd, nIndex);
            }
            else
            {
                return new IntPtr(GetWindowLong32(hWnd, nIndex));
            }
        }

        private static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtrPlatform(hWnd, nIndex, dwNewLong);
            }
            else
            {
                return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
            }
        }

        // Proper EntryPoint names — use Get/SetWindowLongPtr on x64 and Get/SetWindowLong on x86.
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrPlatform(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrPlatform(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // Menu manipulation APIs
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetMenu(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetMenu(IntPtr hWnd, IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawMenuBar(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // GetWindowRect for saving/restoring position and size
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        // Additional Win32 helpers for top-level enumeration and window text/class
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        private const uint GW_OWNER = 4;

        // Common style flags
        private const long WS_POPUP = unchecked((long)0x80000000);

        // Attempt to hide Qt popup windows which are owner-owned by the target window
        private static void HideQtMenus(IntPtr targetHwnd)
        {
            try
            {
                if (targetHwnd == IntPtr.Zero) return;
                if (GetWindowThreadProcessId(targetHwnd, out uint targetPid) == 0) return;

                EnumWindows(new EnumWindowsProc((hwnd, lparam) =>
                {
                    if (hwnd == IntPtr.Zero || hwnd == targetHwnd) return true;

                    if (GetWindowThreadProcessId(hwnd, out uint pid) == 0) return true;
                    if (pid != targetPid) return true;

                    // Walk owner chain to see if this window is owned by the target window
                    IntPtr owner = GetWindow(hwnd, GW_OWNER);
                    bool ownedByTarget = false;
                    while (owner != IntPtr.Zero)
                    {
                        if (owner == targetHwnd) { ownedByTarget = true; break; }
                        owner = GetWindow(owner, GW_OWNER);
                    }

                    if (!ownedByTarget) return true;

                    // Found an owned window; hide/cloak/move it
                    try
                    {
                        var clsSb = new StringBuilder(256);
                        GetClassName(hwnd, clsSb, clsSb.Capacity);
                        string cls = clsSb.ToString() ?? string.Empty;
                        var txtSb = new StringBuilder(256);
                        GetWindowText(hwnd, txtSb, txtSb.Capacity);
                        string title = txtSb.ToString() ?? string.Empty;
                        Debug.WriteLine($"[WGC] Hiding owned popup hwnd={hwnd} class={cls} title='{title}'");

                        int cloak = 1;
                        try { WindowsStealth.DwmSetWindowAttribute(hwnd, WindowsStealth.DWMWA_CLOAK, ref cloak, sizeof(int)); } catch { }
                        try { SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); } catch { }
                        try { IntPtr oldEx = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE); long ex = oldEx.ToInt64(); SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW)); } catch { }
                    }
                    catch { }

                    return true;
                }), IntPtr.Zero);
            }
            catch { }
        }

        // Hide descendant child windows with Qt-like class names (menus implemented as WS_CHILD)
        private static void HideQtChildMenus(IntPtr parentHwnd)
        {
            try
            {
                if (parentHwnd == IntPtr.Zero) return;

                EnumChildWindows(parentHwnd, new EnumChildProc((child, l) =>
                {
                    if (child == IntPtr.Zero) return true;
                    try
                    {
                        var clsSb = new StringBuilder(256);
                        GetClassName(child, clsSb, clsSb.Capacity);
                        string cls = clsSb.ToString() ?? string.Empty;

                        // Match common Qt window class patterns
                        if (!string.IsNullOrEmpty(cls) && (cls.IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("QWindow", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("QMenu", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            try
                            {
                                // Save current visibility state
                                if (!_savedVisibility.ContainsKey(child))
                                {
                                    bool vis = IsWindowVisible(child);
                                    _savedVisibility[child] = vis;
                                }

                                // Hide the child window
                                ShowWindow(child, 0); // SW_HIDE
                                // Also move off-screen as extra measure
                                SetWindowPos(child, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    return true;
                }), IntPtr.Zero);
            }
            catch { }
        }

        // Helper that applies headless changes to a single HWND (used for recursive application)
        private static bool ApplyHeadlessToWindow(IntPtr hwnd)
        {
            try
            {
                IntPtr oldStyle = GetWindowLongPtrCompat(hwnd, GWL_STYLE);
                long style = oldStyle.ToInt64();
                long remove = (long)(WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
                long newStyle = style & ~remove;

                IntPtr result = SetWindowLongPtrCompat(hwnd, GWL_STYLE, new IntPtr(newStyle));
                if (result == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0) return false;
                }

                // If a traditional Win32 menu exists, remove it and save it for restoration.
                try
                {
                    IntPtr menu = GetMenu(hwnd);
                    if (menu != IntPtr.Zero && !_savedMenus.ContainsKey(hwnd))
                    {
                        _savedMenus[hwnd] = menu;
                        var setRes = SetMenu(hwnd, IntPtr.Zero);
                        DrawMenuBar(hwnd);
                    }
                }
                catch { }

                // Notify the window to update its non-client area
                bool ok = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
                return ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Removes window decorations (title bar, border, system menu, sizing boxes) and removes the Win32 menu if present.
        /// Saves previous style and menu so they can be restored with RestoreWindowStyles.
        /// Returns true on success.
        /// </summary>
        public static bool MakeWindowHeadless(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                // Apply to the top-level window first
                IntPtr oldStyle = GetWindowLongPtrCompat(hwnd, GWL_STYLE);
                if (!_savedStyles.ContainsKey(hwnd))
                    _savedStyles[hwnd] = oldStyle;

                bool topOk = ApplyHeadlessToWindow(hwnd);

                // Also attempt to apply to child windows (Qt often hosts rendering in a child HWND)
                try
                {
                    EnumChildWindows(hwnd, new EnumChildProc((child, l) =>
                    {
                        // Skip if same as parent
                        if (child == hwnd) return true;
                        // Save style for restoration if not already saved
                        if (!_savedStyles.ContainsKey(child))
                        {
                            try { _savedStyles[child] = GetWindowLongPtrCompat(child, GWL_STYLE); } catch { }
                        }
                        ApplyHeadlessToWindow(child);
                        return true; // continue enumeration
                    }), IntPtr.Zero);
                }
                catch { }

                // Additionally hide Qt child popup windows (many Qt menus are WS_CHILD with Qt class names)
                try { HideQtChildMenus(hwnd); } catch { }

                return topOk;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Make the window hidden from the taskbar and treated as a tool window.
        /// Also calls MakeWindowHeadless to remove decorations if not already done.
        /// Saves previous extended style so it can be restored.
        /// </summary>
        public static bool MakeWindowStealth(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                // Best-effort to remove decorations first
                try { MakeWindowHeadless(hwnd); } catch { }

                IntPtr oldEx = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);
                if (!_savedExStyles.ContainsKey(hwnd)) _savedExStyles[hwnd] = oldEx;

                long ex = oldEx.ToInt64();
                long newEx = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;

                IntPtr result = SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, new IntPtr(newEx));
                if (result == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0) return false;
                }

                // Refresh the window frame so the taskbar updates
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
                // Ensure window stays visible (not minimized/hidden) so capture keeps working
                try { ShowWindow(hwnd, 1); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restore previously saved window styles and menu (if any).
        /// Also attempts to restore extended styles and saved size/position if they exist.
        /// </summary>
        public static bool RestoreWindowStyles(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            if (!_savedStyles.TryRemove(hwnd, out IntPtr saved)) return false;

            try
            {
                IntPtr result = SetWindowLongPtrCompat(hwnd, GWL_STYLE, saved);
                if (result == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0) return false;
                }

                // Restore menu if we removed one earlier
                if (_savedMenus.TryRemove(hwnd, out IntPtr savedMenu))
                {
                    try
                    {
                        SetMenu(hwnd, savedMenu);
                        DrawMenuBar(hwnd);
                    }
                    catch { /* ignore restore errors */ }
                }

                // Restore extended style if we saved it
                if (_savedExStyles.TryRemove(hwnd, out IntPtr savedEx))
                {
                    try
                    {
                        IntPtr resEx = SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, savedEx);
                        // ignore resEx check — best-effort
                    }
                    catch { }
                }

                // Restore size/position if we saved it
                if (_savedRects.TryRemove(hwnd, out RECT r))
                {
                    try
                    {
                        int width = Math.Max(0, r.Right - r.Left);
                        int height = Math.Max(0, r.Bottom - r.Top);
                        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, width, height,
                            SWP_NOZORDER);
                    }
                    catch { }
                }

                // Restore any saved child visibility states for descendant windows
                try
                {
                    EnumChildWindows(hwnd, new EnumChildProc((child, l) =>
                    {
                        if (child == IntPtr.Zero) return true;
                        if (_savedVisibility.TryRemove(child, out bool wasVisible))
                        {
                            try { if (wasVisible) ShowWindow(child, 1); else ShowWindow(child, 0); } catch { }
                        }
                        return true;
                    }), IntPtr.Zero);
                }
                catch { }

                bool ok = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
                return ok;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reduce the visible size of the window to the specified width/height (default 1x1) to reduce perceived resource usage.
        /// Saves the previous window rect so it can be restored with RestoreWindowStyles.
        /// Note: resizing the target window may affect its rendering and the capture results. Use with care.
        /// </summary>
        public static bool ReduceWindowSize(IntPtr hwnd, int width = 1, int height = 1)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                // Save current rect if not already saved
                if (!_savedRects.ContainsKey(hwnd))
                {
                    if (GetWindowRect(hwnd, out RECT rect))
                    {
                        _savedRects[hwnd] = rect;
                    }
                }

                // Determine current position to keep it in place
                if (GetWindowRect(hwnd, out RECT cur))
                {
                    int x = cur.Left;
                    int y = cur.Top;
                    // Set new size
                    bool ok = SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER);
                    return ok;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}