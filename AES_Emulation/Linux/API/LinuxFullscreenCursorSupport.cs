using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace AES_Emulation.Linux.API;

/// <summary>
/// Hides the X11 cursor during fullscreen capture and polls pointer movement over native embeds.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxFullscreenCursorSupport : IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXfixes = "libXfixes.so.3";

    private readonly IntPtr _display;
    private readonly IntPtr _hideWindow;
    private readonly bool _supportsXfixes;
    private bool _isHidden;

    private LinuxFullscreenCursorSupport(IntPtr display, IntPtr hideWindow, bool supportsXfixes)
    {
        _display = display;
        _hideWindow = hideWindow;
        _supportsXfixes = supportsXfixes;
    }

    public static LinuxFullscreenCursorSupport? TryCreate(TopLevel? topLevel)
    {
        _ = topLevel;
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
            return null;

        var supportsXfixes = XFixesQueryExtension(display, out _, out _);
        var hideWindow = XDefaultRootWindow(display);
        if (hideWindow == IntPtr.Zero)
        {
            XCloseDisplay(display);
            return null;
        }

        return new LinuxFullscreenCursorSupport(display, hideWindow, supportsXfixes);
    }

    public bool TryGetRootPointerPosition(out int x, out int y)
    {
        x = 0;
        y = 0;

        if (_display == IntPtr.Zero)
            return false;

        var root = XDefaultRootWindow(_display);
        return XQueryPointer(
            _display,
            root,
            out _,
            out _,
            out x,
            out y,
            out _,
            out _,
            out _);
    }

    public void HideCursor()
    {
        if (_isHidden || _display == IntPtr.Zero || _hideWindow == IntPtr.Zero)
            return;

        if (_supportsXfixes)
            XFixesHideCursor(_display, _hideWindow);

        _isHidden = true;
        XSync(_display, false);
    }

    public void ShowCursor()
    {
        if (!_isHidden || _display == IntPtr.Zero || _hideWindow == IntPtr.Zero)
            return;

        if (_supportsXfixes)
            XFixesShowCursor(_display, _hideWindow);

        _isHidden = false;
        XSync(_display, false);
    }

    public void Dispose()
    {
        try
        {
            ShowCursor();
        }
        catch
        {
        }

        if (_display != IntPtr.Zero)
            XCloseDisplay(_display);
    }

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XSync(IntPtr display, bool discard);

    [DllImport(LibX11)]
    private static extern bool XQueryPointer(
        IntPtr display,
        IntPtr w,
        out IntPtr rootReturn,
        out IntPtr childReturn,
        out int rootXReturn,
        out int rootYReturn,
        out int winXReturn,
        out int winYReturn,
        out uint maskReturn);

    [DllImport(LibXfixes)]
    private static extern bool XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(LibXfixes)]
    private static extern void XFixesHideCursor(IntPtr display, IntPtr window);

    [DllImport(LibXfixes)]
    private static extern void XFixesShowCursor(IntPtr display, IntPtr window);
}
