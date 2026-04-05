using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AES_Emulation.Windows.API
{
    /// <summary>
    /// Keeps a target window positioned behind a host window and mirrors its size/position.
    /// Simple poll-based implementation: call Start(hostHwnd, targetHwnd) to begin mirroring.
    /// Call Stop() or Dispose() to stop and optionally restore the target window's original position.
    /// </summary>
    public class WindowHandler : IDisposable, INotifyPropertyChanged
    {
        private IntPtr _host = IntPtr.Zero;
        private IntPtr _target = IntPtr.Zero;

        private RECT _lastHostRect;
        private RECT _lastTargetRect;
        private int _lastOpacity = -1;
        private bool _lastMoveToHost = true;
        private RECT _savedTargetRect;
        private bool _haveSavedTargetRect = false;

        private System.Timers.Timer? _timer;
        private System.Timers.ElapsedEventHandler? _elapsedHandler;
        // WinEvent hooks for move/size and foreground tracking
        private IntPtr _hookMove = IntPtr.Zero;
        private IntPtr _hookForeground = IntPtr.Zero;
        private WinEventDelegate? _winEventDelegate;
        private bool _applyRoundedCorners = false;
        private int _cornerRadius = 0;
        private readonly int _pollMs;
        private readonly uint _marginLeft;
        private readonly uint _marginTop;
        private readonly uint _marginRight;
        private readonly uint _marginBottom;
        private bool _running = false;

        // Configurable fixed capture size and aspect ratio (defaults: 1920x1080, 16:9)
        private int _fixedCaptureWidth = 1920;
        private int _fixedCaptureHeight = 1080;
        private double _fixedAspectRatio = 16.0 / 9.0;

        public int FixedCaptureWidth
        {
            get => _fixedCaptureWidth;
            set
            {
                var v = Math.Max(1, value);
                if (v == _fixedCaptureWidth) return;
                _fixedCaptureWidth = v;
                OnPropertyChanged(nameof(FixedCaptureWidth));
            }
        }

        public int FixedCaptureHeight
        {
            get => _fixedCaptureHeight;
            set
            {
                var v = Math.Max(1, value);
                if (v == _fixedCaptureHeight) return;
                _fixedCaptureHeight = v;
                OnPropertyChanged(nameof(FixedCaptureHeight));
            }
        }

        /// <summary>
        /// Aspect ratio expressed as width/height (e.g. 16.0/9.0). Changing this does not automatically resize
        /// the target window; it is provided for callers that prefer to compute the fixed resolution from an aspect.
        /// </summary>
        public double FixedAspectRatio
        {
            get => _fixedAspectRatio;
            set
            {
                if (value > 0.0 && Math.Abs(value - _fixedAspectRatio) > double.Epsilon)
                {
                    _fixedAspectRatio = value;
                    OnPropertyChanged(nameof(FixedAspectRatio));
                }
            }
        }

        public void SetFixedCaptureResolution(int width, int height)
        {
            FixedCaptureWidth = width;
            FixedCaptureHeight = height;
            if (FixedCaptureWidth > 0 && FixedCaptureHeight > 0)
                FixedAspectRatio = (double)FixedCaptureWidth / FixedCaptureHeight;
        }

        // Backwards-compatible constructor that takes a single uniform margin (int)
        public WindowHandler(int pollMilliseconds = 50, int margin = 0)
            : this(pollMilliseconds, (uint)Math.Max(0, margin), (uint)Math.Max(0, margin), (uint)Math.Max(0, margin), (uint)Math.Max(0, margin))
        {
        }

        // New constructor that accepts individual margins (like XAML Thickness)
        public WindowHandler(int pollMilliseconds, uint marginLeft, uint marginTop, uint marginRight, uint marginBottom)
        {
            _pollMs = Math.Max(16, pollMilliseconds);
            _marginLeft = marginLeft;
            _marginTop = marginTop;
            _marginRight = marginRight;
            _marginBottom = marginBottom;
        }

        /// <summary>
        /// Start mirroring target window position/size to match host and keep it directly behind host in Z-order.
        /// </summary>
        public void Start(IntPtr hostHwnd, IntPtr targetHwnd)
        {
            if (hostHwnd == IntPtr.Zero || targetHwnd == IntPtr.Zero) throw new ArgumentException("HWNDs must be non-zero");
            Stop();

            _host = hostHwnd;
            _target = targetHwnd;

            // Save target rect so it can be restored later
            try
            {
                if (GetWindowRect(_target, out RECT tr))
                {
                    _savedTargetRect = tr;
                    _haveSavedTargetRect = true;
                }
            }
            catch { }

            // Initialize last host rect using full window rect
            GetWindowRectSafe(_host, out _lastHostRect);

            _running = true;
            // Use System.Timers.Timer to ensure the timer is rooted by this instance and fires reliably.
            _timer = new System.Timers.Timer(_pollMs) { AutoReset = true };
            _elapsedHandler = new System.Timers.ElapsedEventHandler((s, ev) => Tick(null));
            _timer.Elapsed += _elapsedHandler;
            _timer.Start();

            // Install WinEvent hooks to react immediately to host moves/sizes and foreground changes
            _winEventDelegate = new WinEventDelegate(WinEventProc);
            const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
            const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
            const uint WINEVENT_OUTOFCONTEXT = 0x0000;
            const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
            try
            {
                // Filter hooks to host/target processes to reduce noise and ensure prompt callbacks.
                uint hostPid = 0; uint targetPid = 0;
                try { GetWindowThreadProcessId(_host, out hostPid); } catch { hostPid = 0; }
                try { GetWindowThreadProcessId(_target, out targetPid); } catch { targetPid = 0; }

                if (hostPid != 0)
                {
                    _hookMove = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventDelegate, hostPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }
                else
                {
                    _hookMove = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }

                if (targetPid != 0)
                {
                    _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, targetPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }
                else
                {
                    _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }
            }
            catch { }
        }

        /// <summary>
        /// Stop mirroring. Does not automatically restore the target window position; call RestoreOriginalPosition() if desired.
        /// </summary>
        public void Stop()
        {
            _running = false;
            try
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    if (_elapsedHandler != null) _timer.Elapsed -= _elapsedHandler;
                    _timer.Dispose();
                }
            }
            catch { }
            _timer = null;
            _elapsedHandler = null;
            // Unhook WinEvent hooks
            try { if (_hookMove != IntPtr.Zero) UnhookWinEvent(_hookMove); } catch { }
            try { if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground); } catch { }
            _hookMove = IntPtr.Zero; _hookForeground = IntPtr.Zero; _winEventDelegate = null;
            _host = IntPtr.Zero;
            _target = IntPtr.Zero;
        }

        /// <summary>
        /// Restore the target window to its saved original position (if Start saved it).
        /// </summary>
        public void RestoreOriginalPosition()
        {
            if (!_haveSavedTargetRect || _savedTargetRect.Left == 0 && _savedTargetRect.Top == 0 && _savedTargetRect.Right == 0 && _savedTargetRect.Bottom == 0) return;
            if (_target == IntPtr.Zero) return;
            try
            {
                int w = Math.Max(0, _savedTargetRect.Right - _savedTargetRect.Left);
                int h = Math.Max(0, _savedTargetRect.Bottom - _savedTargetRect.Top);
                SetWindowPos(_target, IntPtr.Zero, _savedTargetRect.Left, _savedTargetRect.Top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// Enable rounded corners on the target window with the given radius (pixels).
        /// </summary>
        public void EnableRoundedCorners(int radius)
        {
            _cornerRadius = Math.Max(0, radius);
            _applyRoundedCorners = _cornerRadius > 0;
            if (_applyRoundedCorners) ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            if (_target == IntPtr.Zero || !_applyRoundedCorners) return;
            try
            {
                if (GetWindowRect(_target, out RECT r))
                {
                    int w = Math.Max(0, r.Right - r.Left);
                    int h = Math.Max(0, r.Bottom - r.Top);
                    if (w > 0 && h > 0)
                    {
                        IntPtr hrgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, _cornerRadius, _cornerRadius);
                        if (hrgn != IntPtr.Zero)
                        {
                            SetWindowRgn(_target, hrgn, true);
                            // Do not delete region after SetWindowRgn; system owns it.
                        }
                    }
                }
            }
            catch { }
        }

        private void Tick(object? state)
        {
            if (!_running) return;
            if (_host == IntPtr.Zero || _target == IntPtr.Zero) return;

            // Use full window rect for sizing so capture matches visible window including decorations
            if (!GetWindowRectSafe(_host, out RECT hostRect)) return;

            // Always maintain the target size to match the host, unless the flag says to move away.
            try
            {
                int hostWidth = Math.Max(0, hostRect.Right - hostRect.Left);
                int hostHeight = Math.Max(0, hostRect.Bottom - hostRect.Top);

                if (MoveToHost)
                {
                    bool stateChanged = !RectsEqual(hostRect, _lastHostRect) || _lastMoveToHost != MoveToHost;
                    if (stateChanged)
                    {
                        // Place target right AFTER the host in Z-order so the target stays behind the host.
                        try { SetWindowPos(_target, _host, hostRect.Left, hostRect.Top, hostWidth, hostHeight, SWP_NOACTIVATE); } catch { }
                        if (_applyRoundedCorners) ApplyRoundedRegion();
                        _lastHostRect = hostRect;
                    }

                    if (_lastOpacity != 255)
                    {
                        // restore opacity in case it was dimmed
                        try { Win32API.SetWindowOpacity(_target, 255); } catch { }
                        _lastOpacity = 255;
                    }
                }
                else
                {
                    bool sizeChanged = _lastTargetRect.Right != FixedCaptureWidth || _lastTargetRect.Bottom != FixedCaptureHeight;
                    if (sizeChanged || _lastMoveToHost != MoveToHost)
                    {
                        // Ensure the hidden/moved-away target uses the fixed capture resolution.
                        try { SetWindowPos(_target, IntPtr.Zero, 0, 0, FixedCaptureWidth, FixedCaptureHeight, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); } catch { }
                        
                        // Move away the target but keep its size synchronized for capture.
                        try { Win32API.MoveAway(_target, false); } catch { }
                        
                        _lastTargetRect.Right = FixedCaptureWidth;
                        _lastTargetRect.Bottom = FixedCaptureHeight;
                    }

                    if (_lastOpacity != 255)
                    {
                        // Keep the hidden window opaque off-screen so WGC can capture it reliably.
                        try { Win32API.SetWindowOpacity(_target, 255); } catch { }
                        _lastOpacity = 255;
                    }
                }
                _lastMoveToHost = MoveToHost;
            }
            catch { }
            //if (!RectsEqual(hostRect, _lastHostRect))
            //{
            //    _lastHostRect = hostRect;
            //    try
            //    {
            //        int width = Math.Max(0, hostRect.Right - hostRect.Left);
            //        int height = Math.Max(0, hostRect.Bottom - hostRect.Top);

            //        // Place target right AFTER the host in Z-order so the target stays behind the host.
            //        // Using host HWND as hWndInsertAfter places the target just below the host.
            //        SetWindowPos(_target, _host, hostRect.Left, hostRect.Top, width, height, SWP_NOACTIVATE);
            //        if (_applyRoundedCorners) ApplyRoundedRegion();
            //    }
            //    catch { }
            //}
        }

        private static bool RectsEqual(RECT a, RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        private static bool GetWindowRectSafe(IntPtr hwnd, out RECT rect)
        {
            rect = new RECT();
            try
            {
                return GetWindowRect(hwnd, out rect);
            }
            catch { return false; }
        }

        // Get the host client rect in screen coordinates (left/top/right/bottom in screen space)
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private static bool GetClientRectScreen(IntPtr hwnd, out RECT rect)
        {
            rect = new RECT();
            try
            {
                if (!GetClientRect(hwnd, out RECT cr)) return false;
                POINT p1 = new POINT { x = cr.Left, y = cr.Top };
                if (!ClientToScreen(hwnd, ref p1)) return false;
                POINT p2 = new POINT { x = cr.Right, y = cr.Bottom };
                if (!ClientToScreen(hwnd, ref p2)) return false;
                rect.Left = p1.x; rect.Top = p1.y; rect.Right = p2.x; rect.Bottom = p2.y;
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            Stop();
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                // If host moved/resized, mirror immediately
                if (hwnd == _host)
                {
                    if (GetWindowRectSafe(_host, out RECT hostRect))
                    {
                        // Always update last host rect
                        _lastHostRect = hostRect;
                        // Only move/resize target when MoveToHost is true
                        if (MoveToHost)
                        {
                            int width = FixedCaptureWidth;
                            int height = FixedCaptureHeight;
                            // When positioning behind host, ensure position matches window origin; keep fixed capture size
                            SetWindowPos(_target, _host, hostRect.Left, hostRect.Top, width, height, SWP_NOACTIVATE);
                            if (_applyRoundedCorners) ApplyRoundedRegion();
                        }
                        else
                        {
                            // Even when not moved behind host, ensure the hidden target uses fixed capture size.
                            try { SetWindowPos(_target, IntPtr.Zero, 0, 0, FixedCaptureWidth, FixedCaptureHeight, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); } catch { }
                        }
                    }
                }
                // If target became foreground, restore host above it
                if (hwnd == _target && eventType == 0x0003) // EVENT_SYSTEM_FOREGROUND
                {
                    try
                    {
                        if (MoveToHost)
                        {
                            // Bring host to top, then re-place target behind
                            SetWindowPos(_host, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                            SetWindowPos(_host, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                            if (GetWindowRectSafe(_host, out RECT hostRect))
                            {
                                SetWindowPos(_target, _host, hostRect.Left, hostRect.Top, FixedCaptureWidth, FixedCaptureHeight, SWP_NOACTIVATE);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        #region Win32
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_SHOW = 5;
        private const int SW_HIDE = 0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // Focus & input APIs used by SendFocusToTarget
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint WM_ACTIVATE = 0x0006;
        private static readonly IntPtr WA_CLICKACTIVE = new IntPtr(2);

        /// <summary>
        /// Attempt to forward input focus to the target window. If returnFocusToHost is true, try to restore focus to the host window afterwards.
        /// Returns true if the operation was attempted.
        /// </summary>
        public bool SendFocusToTarget(bool returnFocusToHost = true)
        {
            if (_target == IntPtr.Zero) return false;
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, out _);
                uint targetThread = GetWindowThreadProcessId(_target, out _);

                if (fgThread != targetThread)
                {
                    // Attach threads to allow setting focus across thread boundaries
                    AttachThreadInput(fgThread, targetThread, true);
                    // Set focus and send activation
                    SetFocus(_target);
                    SendMessage(_target, WM_ACTIVATE, WA_CLICKACTIVE, IntPtr.Zero);
                    AttachThreadInput(fgThread, targetThread, false);

                    if (returnFocusToHost && _host != IntPtr.Zero)
                    {
                        try { SetForegroundWindow(_host); } catch { }
                    }
                }

                return true;
            }
            catch { return false; }
        }
        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        #endregion

        // When true, the target window will be placed behind the host and shown.
        // When false, the target window will be moved away from the desktop and hidden
        // (still kept registered so native capture code can attempt to capture it).
        public bool MoveToHost { get; private set; } = true;

        /// <summary>
        /// Set whether the target window should be moved to/kept behind the host (true)
        /// or moved away and hidden (false). This applies immediately when called.
        /// </summary>
        public void SetMoveToHost(bool moveToHost)
        {
            MoveToHost = moveToHost;
            if (_target == IntPtr.Zero) return;

            try
            {
                if (moveToHost)
                {
                    // Ensure the target is placed behind the host and made visible
                    if (GetWindowRectSafe(_host, out RECT hostRect))
                    {
                        int width = FixedCaptureWidth;
                        int height = FixedCaptureHeight;
                        // When positioning behind host, ensure position matches window origin; keep fixed capture size
                        SetWindowPos(_target, _host, hostRect.Left, hostRect.Top, width, height, SWP_NOACTIVATE);
                        // Restore opacity so the emulator is visible to the user
                        try { Win32API.SetWindowOpacity(_target, 255); } catch { }
                        if (_applyRoundedCorners) ApplyRoundedRegion();
                    }
                }
                else
                {
                    // Ensure target size matches host client before moving away so capture sees correct size.
                    try
                    {
                        if (GetWindowRectSafe(_host, out RECT hostRect))
                        {
                            try { SetWindowPos(_target, IntPtr.Zero, 0, 0, FixedCaptureWidth, FixedCaptureHeight, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE); } catch { }
                        }
                    }
                    catch { }

                    // Move away off-screen and keep the window opaque so WGC can capture it reliably.
                    try { Win32API.MoveAway(_target, false); } catch { }
                    try { Win32API.SetWindowOpacity(_target, 255); } catch { }
                }
            }
            catch { }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
