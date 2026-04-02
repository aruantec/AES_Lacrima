using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace AES_Emulation.Windows.API
{
    /// <summary>
    /// Encapsulates cursor hiding and mouse tunneling logic for a given Avalonia input element.
    /// Subscribe/Unsubscribe is handled internally; call <see cref="Dispose"/> when no longer needed.
    /// </summary>
    public sealed class MouseTunnelHelper : IDisposable
    {
        private readonly InputElement _element;

        public IntPtr TargetHwnd { get; set; } = IntPtr.Zero;
        public bool TunnelMouse { get; set; } = true;

        // Routed event handler references (kept so we can remove them)
        private readonly EventHandler<PointerEventArgs> _enteredHandler;
        private readonly EventHandler<PointerEventArgs> _exitedHandler;
        private readonly EventHandler<PointerEventArgs> _movedHandler;
        private readonly EventHandler<PointerPressedEventArgs> _pressedHandler;
        private readonly EventHandler<PointerReleasedEventArgs> _releasedHandler;
        private readonly EventHandler<PointerWheelEventArgs> _wheelHandler;

        // Visual root we attach handlers to
        private TopLevel? _rootTopLevel;
        private bool _handlersAttachedToRoot = false;
        private bool _isCurrentlyVisible = true;

        // track property changed subscription
        private EventHandler<AvaloniaPropertyChangedEventArgs>? _propChangedHandler;

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MBUTTONDOWN = 0x0207;
        private const uint WM_MBUTTONUP = 0x0208;
        private const uint WM_MOUSEWHEEL = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Focus helpers for gamepad / input-requiring emulators
        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        // SendInput definitions for synthetic keyboard input (Alt trick)
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg; public ushort wParamL; public ushort wParamH;
        }

        private const int GCLP_HCURSOR = -12;

        // Store previous target class cursor so we can restore
        private IntPtr _prevTargetClassCursor = IntPtr.Zero;
        private bool _cursorHiddenOnTarget = false;
        // Fallback: hide cursor locally on the Avalonia element instead of global ShowCursor
        private Avalonia.Input.Cursor? _prevElementCursor = null;
        private bool _cursorHiddenLocal = false;

        [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
        private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private IntPtr? _transparentCursor = null;
        private bool _createdTransparentCursor = false;

        private IntPtr GetClassCursor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (IntPtr.Size == 8)
            {
                return GetClassLongPtr64(hwnd, GCLP_HCURSOR);
            }
            else
            {
                uint val = GetClassLong32(hwnd, GCLP_HCURSOR);
                return new IntPtr((int)val);
            }
        }

        private void SetClassCursor(IntPtr hwnd, IntPtr cursor)
        {
            if (hwnd == IntPtr.Zero) return;
            if (IntPtr.Size == 8)
            {
                SetClassLongPtr64(hwnd, GCLP_HCURSOR, cursor);
            }
            else
            {
                SetClassLong32(hwnd, GCLP_HCURSOR, (uint)cursor.ToInt32());
            }
        }

        public MouseTunnelHelper(InputElement element)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));

            _enteredHandler = (s, e) => OnPointerEntered(e);
            _exitedHandler = (s, e) => OnPointerExited(e);
            _movedHandler = (s, e) => OnPointerMoved(e);
            _pressedHandler = (s, e) => OnPointerPressed(e);
            _releasedHandler = (s, e) => OnPointerReleased(e);
            _wheelHandler = (s, e) => OnPointerWheelChanged(e);

            // Subscribe to visibility changes � only tunnel when visible
            try
            {
                _isCurrentlyVisible = element.IsVisible;
                _propChangedHandler = (s, e) => { if (e.Property?.Name == "IsVisible") OnIsVisibleChanged(element.IsVisible); };
                element.PropertyChanged += _propChangedHandler;
            }
            catch { }

            // Try to attach to the visual root (TopLevel). The element may not yet be attached
            // so poll briefly until the root is available, then attach handlers there so we receive
            // pointer events regardless of Z-order.
            if (_isCurrentlyVisible)
                TryStartRootAttachTimer();
        }

        private void ForceEmulatorFocus(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            IntPtr foregroundHwnd = Win32Focus.GetForegroundWindow();
            uint foregroundThreadId = Win32Focus.GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
            uint targetThreadId = Win32Focus.GetWindowThreadProcessId(targetHwnd, IntPtr.Zero);

            if (foregroundThreadId != targetThreadId)
            {
                // 1. Attach threads so we can share input state
                Win32Focus.AttachThreadInput(foregroundThreadId, targetThreadId, true);

                // 2. Fake the focus and activation
                Win32Focus.SetFocus(targetHwnd);
                Win32Focus.SendMessage(targetHwnd, Win32Focus.WM_ACTIVATE, Win32Focus.WA_CLICKACTIVE, IntPtr.Zero);

                // 3. Detach
                Win32Focus.AttachThreadInput(foregroundThreadId, targetThreadId, false);

                // 4. Return focus to our Avalonia control so keyboard/mouse tunneling still works
                _element.Focus();
            }
        }

        private DispatcherTimer? _rootAttachTimer;
        private int _rootAttachAttempts = 0;
        private DispatcherTimer? _restoreFocusTimer;

        private void TryStartRootAttachTimer()
        {
            if (!_isCurrentlyVisible) return;
            // If already attached, attach immediately
            var root = Avalonia.VisualTree.VisualExtensions.GetVisualRoot(_element) as TopLevel;
            if (root != null)
            {
                AttachHandlersToRoot(root);
                return;
            }

            // Try to attach to application main window as a fallback
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var mw = desktop.MainWindow as TopLevel;
                    if (mw != null)
                    {
                        AttachHandlersToRoot(mw);
                        return;
                    }
                }
            }
            catch { }

            // otherwise poll a few times on the UI thread
            _rootAttachTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (s, e) =>
            {
                try
                {
                    _rootAttachAttempts++;
                    var rt = Avalonia.VisualTree.VisualExtensions.GetVisualRoot(_element) as TopLevel;
                    if (rt != null)
                    {
                        AttachHandlersToRoot(rt);
                        _rootAttachTimer?.Stop(); _rootAttachTimer = null;
                        return;
                    }

                    try
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            var mw = desktop.MainWindow as TopLevel;
                            if (mw != null)
                            {
                                AttachHandlersToRoot(mw);
                                _rootAttachTimer?.Stop(); _rootAttachTimer = null;
                                return;
                            }
                        }
                    }
                    catch { }

                    if (_rootAttachAttempts > 10)
                    {
                        // give up after a few attempts
                        _rootAttachTimer?.Stop(); _rootAttachTimer = null;
                    }
                }
                catch { _rootAttachTimer?.Stop(); _rootAttachTimer = null; }
            });
            _rootAttachTimer.Start();
        }

        private void OnIsVisibleChanged(bool visible)
        {
            try
            {
                _isCurrentlyVisible = visible;
                if (visible)
                {
                    TryStartRootAttachTimer();
                }
                else
                {
                    // stop tunneling
                    _rootAttachTimer?.Stop(); _rootAttachTimer = null;
                    DetachHandlersFromRoot();
                }
            }
            catch { }
        }

        private static IntPtr MakeLParam(int x, int y) => new IntPtr((y << 16) | (x & 0xFFFF));

        private void OnPointerEntered(PointerEventArgs _)
        {
            // Only hide when tunneling enabled and pointer is over the capture element
            if (!TunnelMouse) return;
            try
            {
                // Determine pointer position relative to our element
                var pt = _.GetPosition(_element);
                if (pt.X >= 0 && pt.Y >= 0 && pt.X <= _element.Bounds.Width && pt.Y <= _element.Bounds.Height)
                {
                    if (!_cursorHiddenOnTarget && TargetHwnd != IntPtr.Zero)
                    {
                        // Try to hide cursor on target window by setting its class cursor to NULL
                        var prev = GetClassCursor(TargetHwnd);
                        _prevTargetClassCursor = prev;
                        try
                        {
                            // Ensure we have a transparent cursor handle
                            if (_transparentCursor == null)
                                _transparentCursor = CreateTransparentCursor();

                            if (_transparentCursor != null && _transparentCursor != IntPtr.Zero)
                            {
                                SetClassCursor(TargetHwnd, _transparentCursor.Value);
                                _cursorHiddenOnTarget = true;
                            }
                        }
                        catch
                        {
                            // fallback: hide cursor locally on the element (avoid global ShowCursor)
                            try
                            {
                                _prevElementCursor = _element.Cursor;
                                _element.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None);
                                _cursorHiddenLocal = true;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void OnPointerExited(PointerEventArgs _)
        {
            try
            {
                var pt = _.GetPosition(_element);
                // Pointer exited capture area -> restore cursor on target
                if (!(_element.Bounds.Contains(pt)))
                {
                    if (_cursorHiddenOnTarget && TargetHwnd != IntPtr.Zero)
                    {
                        try
                        {
                            SetClassCursor(TargetHwnd, _prevTargetClassCursor);
                        }
                        catch { }
                        _cursorHiddenOnTarget = false;
                    }

                    if (_cursorHiddenLocal)
                    {
                        try { _element.Cursor = _prevElementCursor; } catch { }
                        _cursorHiddenLocal = false;
                        _prevElementCursor = null;
                    }
                }
            }
            catch { }
        }

        private void OnPointerMoved(PointerEventArgs e)
        {
            if (!TunnelMouse || TargetHwnd == IntPtr.Zero) return;

            // Only forward moves when the pointer is actually over our capture element.
            try
            {
                var local = e.GetPosition(_element);
                if (local.X < 0 || local.Y < 0 || local.X > _element.Bounds.Width || local.Y > _element.Bounds.Height)
                    return;
            }
            catch { return; }

            if (GetCursorPos(out POINT p))
            {
                var pt = p;
                if (ScreenToClient(TargetHwnd, ref pt))
                {
                    // Build wParam with current button state flags so target receives correct state
                    int flags = 0;
                    var props = e.GetCurrentPoint(_element).Properties;
                    if (props.IsLeftButtonPressed) flags |= 0x0001; // MK_LBUTTON
                    if (props.IsRightButtonPressed) flags |= 0x0002; // MK_RBUTTON
                    if (props.IsMiddleButtonPressed) flags |= 0x0010; // MK_MBUTTON

                    SendMessage(TargetHwnd, WM_MOUSEMOVE, new IntPtr(flags), MakeLParam(pt.X, pt.Y));
                }
            }
        }

        private void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!TunnelMouse || TargetHwnd == IntPtr.Zero) return;

            // Only forward presses when pointer over our element
            try
            {
                var local = e.GetPosition(_element);
                if (local.X < 0 || local.Y < 0 || local.X > _element.Bounds.Width || local.Y > _element.Bounds.Height)
                    return;
            }
            catch { return; }

            if (GetCursorPos(out POINT p))
            {
                var pt = p;
                if (!ScreenToClient(TargetHwnd, ref pt)) return;

                var props = e.GetCurrentPoint(_element).Properties;
                if (props.IsLeftButtonPressed) SendMessage(TargetHwnd, WM_LBUTTONDOWN, new IntPtr(0x0001), MakeLParam(pt.X, pt.Y));
                if (props.IsRightButtonPressed) SendMessage(TargetHwnd, WM_RBUTTONDOWN, new IntPtr(0x0002), MakeLParam(pt.X, pt.Y));
                if (props.IsMiddleButtonPressed) SendMessage(TargetHwnd, WM_MBUTTONDOWN, new IntPtr(0x0010), MakeLParam(pt.X, pt.Y));
            }
        }

        private void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!TunnelMouse || TargetHwnd == IntPtr.Zero) return;

            // Only forward releases when pointer over our element
            try
            {
                var local = e.GetPosition(_element);
                if (local.X < 0 || local.Y < 0 || local.X > _element.Bounds.Width || local.Y > _element.Bounds.Height)
                    return;
            }
            catch { return; }

            if (GetCursorPos(out POINT p))
            {
                var pt = p;
                if (!ScreenToClient(TargetHwnd, ref pt)) return;

                var button = e.InitialPressMouseButton;
                if (button == MouseButton.Left) SendMessage(TargetHwnd, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(pt.X, pt.Y));
                else if (button == MouseButton.Right) SendMessage(TargetHwnd, WM_RBUTTONUP, IntPtr.Zero, MakeLParam(pt.X, pt.Y));
                else if (button == MouseButton.Middle) SendMessage(TargetHwnd, WM_MBUTTONUP, IntPtr.Zero, MakeLParam(pt.X, pt.Y));
            }
            ForceEmulatorFocus(TargetHwnd);
        }

        private void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (!TunnelMouse || TargetHwnd == IntPtr.Zero) return;

            // Only forward wheel when pointer over our element
            try
            {
                var local = e.GetPosition(_element);
                if (local.X < 0 || local.Y < 0 || local.X > _element.Bounds.Width || local.Y > _element.Bounds.Height)
                    return;
            }
            catch { return; }

            if (GetCursorPos(out POINT p))
            {
                var pt = p;
                if (!ScreenToClient(TargetHwnd, ref pt)) return;
                int delta = (int)(e.Delta.Y * 120.0);
                // low word can contain MK_* flags, use 0 for now
                IntPtr wParam = new IntPtr(((delta & 0xffff) << 16));
                SendMessage(TargetHwnd, WM_MOUSEWHEEL, wParam, MakeLParam(pt.X, pt.Y));
            }
        }

        public void Dispose()
        {
            try
            {
                _rootAttachTimer?.Stop(); _rootAttachTimer = null;
                if (_propChangedHandler != null) { try { _element.PropertyChanged -= _propChangedHandler; } catch { } _propChangedHandler = null; }
                try { _restoreFocusTimer?.Stop(); _restoreFocusTimer = null; } catch { }
                // restore class cursor if we modified it
                try { if (_cursorHiddenOnTarget && TargetHwnd != IntPtr.Zero) SetClassCursor(TargetHwnd, _prevTargetClassCursor); } catch { }
                // restore element-local cursor if we changed it
                try { if (_cursorHiddenLocal) { _element.Cursor = _prevElementCursor; _cursorHiddenLocal = false; _prevElementCursor = null; } } catch { }
                // destroy temporary transparent cursor if created
                try { if (_createdTransparentCursor && _transparentCursor.HasValue) { DestroyIcon(_transparentCursor.Value); _transparentCursor = null; _createdTransparentCursor = false; } } catch { }
                DetachHandlersFromRoot();
            }
            catch { /* ignore removal errors */ }
        }

        private void AttachHandlersToRoot(TopLevel root)
        {
            if (root == null || _handlersAttachedToRoot) return;
            try
            {
                _rootTopLevel = root;
                // Use direct CLR events on TopLevel (+=) � AddHandler on TopLevel may not route as expected
                _rootTopLevel.PointerEntered += _enteredHandler;
                _rootTopLevel.PointerExited += _exitedHandler;
                _rootTopLevel.PointerMoved += _movedHandler;
                _rootTopLevel.PointerPressed += _pressedHandler;
                _rootTopLevel.PointerReleased += _releasedHandler;
                _rootTopLevel.PointerWheelChanged += _wheelHandler;
                _handlersAttachedToRoot = true;
            }
            catch { _handlersAttachedToRoot = false; }
        }

        private void DetachHandlersFromRoot()
        {
            if (!_handlersAttachedToRoot || _rootTopLevel == null) return;
            try
            {
                _rootTopLevel.PointerEntered -= _enteredHandler;
                _rootTopLevel.PointerExited -= _exitedHandler;
                _rootTopLevel.PointerMoved -= _movedHandler;
                _rootTopLevel.PointerPressed -= _pressedHandler;
                _rootTopLevel.PointerReleased -= _releasedHandler;
                _rootTopLevel.PointerWheelChanged -= _wheelHandler;
            }
            catch { }
            finally
            {
                _handlersAttachedToRoot = false;
                _rootTopLevel = null;
            }
        }

        private IntPtr? CreateTransparentCursor()
        {
            try
            {
                const int width = 32;
                const int height = 32;
                // bytes per row for monochrome mask (width / 8)
                int bytesPerRow = width / 8;
                int size = bytesPerRow * height;
                var andMask = new byte[size];
                var xorMask = new byte[size];
                // Fill AND mask with 0xFF to make pixels transparent when XOR is 0
                for (int i = 0; i < size; i++) andMask[i] = 0xFF;
                for (int i = 0; i < size; i++) xorMask[i] = 0x00;

                var h = CreateCursor(IntPtr.Zero, 0, 0, width, height, andMask, xorMask);
                if (h != IntPtr.Zero)
                {
                    _createdTransparentCursor = true;
                    return h;
                }
            }
            catch { }
            return null;
        }
    }
}