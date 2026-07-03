using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.Logging;
using log4net;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Covers the Parsec virtual monitor with a solid black window so Windows desktop wallpaper and icons
/// are not visible during monitor-based capture.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ParsecVirtualDisplayBackdrop
{
    private const string WindowClassName = "AES_ParsecVddBackdrop";
    private const int WmDestroy = 0x0002;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmQuit = 0x0012;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly ILog Log = LogHelper.For(typeof(ParsecVirtualDisplayBackdrop));
    private static readonly WndProc WindowProcDelegate = WindowProc;

    private static readonly object Gate = new();
    private static Thread? _messageThread;
    private static IntPtr _hwnd = IntPtr.Zero;
    private static IntPtr _blackBrush = IntPtr.Zero;
    private static uint _messageThreadId;
    private static volatile bool _shutdownRequested;

    public static void Show(ParsecVirtualDisplayMonitor monitor)
    {
        if (!OperatingSystem.IsWindows() || monitor.Handle == IntPtr.Zero)
            return;

        lock (Gate)
        {
            if (_messageThread == null)
                StartMessageThread();

            PostToMessageThread(() => CreateOrUpdateWindow(monitor));
        }
    }

    public static void Hide()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Gate)
        {
            _shutdownRequested = true;
            if (_messageThreadId != 0)
            {
                PostToMessageThread(() =>
                {
                    if (_hwnd != IntPtr.Zero)
                    {
                        DestroyWindow(_hwnd);
                        _hwnd = IntPtr.Zero;
                    }
                });
                PostThreadMessage(_messageThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }

            if (_messageThread != null)
            {
                try
                {
                    if (!_messageThread.Join(TimeSpan.FromSeconds(2)))
                        Log.Debug("Parsec virtual display backdrop thread did not exit cleanly.");
                }
                catch (Exception ex)
                {
                    Log.Debug("Parsec virtual display backdrop thread join failed.", ex);
                }
            }

            _messageThread = null;
            _messageThreadId = 0;
            _hwnd = IntPtr.Zero;
            _shutdownRequested = false;

            if (_blackBrush != IntPtr.Zero)
            {
                DeleteObject(_blackBrush);
                _blackBrush = IntPtr.Zero;
            }
        }
    }

    private static void StartMessageThread()
    {
        _messageThread = new Thread(MessageThreadMain)
        {
            IsBackground = true,
            Name = "ParsecVddBackdrop"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private static void MessageThreadMain()
    {
        _messageThreadId = GetCurrentThreadId();
        var ready = new ManualResetEventSlim(false);
        PostToMessageThread(() => ready.Set());
        ready.Wait();

        MSG msg;
        while (!_shutdownRequested && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void PostToMessageThread(Action action)
    {
        if (_messageThread == null)
            return;

        var handle = GCHandle.Alloc(action);
        try
        {
            while (_messageThreadId == 0)
                Thread.Sleep(1);

            PostThreadMessage(_messageThreadId, 0x0400, GCHandle.ToIntPtr(handle), IntPtr.Zero);
        }
        catch
        {
            if (handle.IsAllocated)
                handle.Free();
            throw;
        }
    }

    private static void CreateOrUpdateWindow(ParsecVirtualDisplayMonitor monitor)
    {
        EnsureWindowClassRegistered();

        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = CreateWindowEx(
                WsExNoActivate | WsExToolWindow,
                WindowClassName,
                string.Empty,
                WsPopup | WsVisible,
                monitor.Left,
                monitor.Top,
                monitor.Width,
                monitor.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(IntPtr.Zero),
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Log.Warn("Failed to create Parsec virtual display backdrop window.");
                return;
            }
        }

        SetWindowPos(
            _hwnd,
            HwndTopmost,
            monitor.Left,
            monitor.Top,
            monitor.Width,
            monitor.Height,
            SwpNoActivate | SwpShowWindow);
        ShowWindow(_hwnd, 5);
    }

    private static void EnsureWindowClassRegistered()
    {
        if (_blackBrush == IntPtr.Zero)
            _blackBrush = CreateSolidBrush(0x000000);

        var wndClass = new WndClass
        {
            Style = 0,
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            CbClsExtra = 0,
            CbWndExtra = 0,
            HInstance = GetModuleHandle(IntPtr.Zero),
            HIcon = IntPtr.Zero,
            HCursor = LoadCursor(IntPtr.Zero, 32512),
            HbrBackground = _blackBrush,
            LpszMenuName = null,
            LpszClassName = WindowClassName
        };

        RegisterClass(ref wndClass);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0400:
                if (GCHandle.FromIntPtr(wParam).Target is Action action)
                {
                    try { action(); }
                    finally { GCHandle.FromIntPtr(wParam).Free(); }
                }
                return IntPtr.Zero;
            case WmEraseBkgnd:
            {
                if (wParam != IntPtr.Zero && _blackBrush != IntPtr.Zero)
                {
                    GetClientRect(hwnd, out var rect);
                    FillRect(wParam, ref rect, _blackBrush);
                }
                return new IntPtr(1);
            }
            case WmDestroy:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int colorRef);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("gdi32.dll")]
    private static extern int FillRect(IntPtr hDC, ref Rect lprc, IntPtr hbr);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
}
