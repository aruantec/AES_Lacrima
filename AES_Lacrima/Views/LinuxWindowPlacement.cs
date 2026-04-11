using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AES_Lacrima.Views;

internal static class LinuxWindowPlacement
{
    private const int PropModeReplace = 0;
    private const int XA_ATOM = 4;

    public static bool TryConfigureAsNormalWindow(Window window)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return false;

        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            var windowType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            var normalType = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NORMAL", false);
            if (windowType == IntPtr.Zero || normalType == IntPtr.Zero)
                return false;

            var atoms = new[] { normalType };
            XChangeProperty(
                display,
                handle,
                windowType,
                new IntPtr(XA_ATOM),
                32,
                PropModeReplace,
                atoms,
                atoms.Length);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    public static bool TryMoveResize(Window window, int x, int y, int width, int height)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return false;

        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            if (ShouldStageThroughRightEdge(window, x, y, width, height, out var stagingX))
            {
                XMoveResizeWindow(display, handle, stagingX, y, width, height);
                XSync(display, false);
                var changes = new XWindowChanges { x = x };
                XConfigureWindow(display, handle, CWX, ref changes);
                XFlush(display);
                return true;
            }

            XMoveResizeWindow(
                display,
                handle,
                x,
                y,
                width,
                height);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    public static bool TryMove(Window window, int x, int y)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return false;

        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            XMoveWindow(display, handle, x, y);
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static bool ShouldStageThroughRightEdge(Window window, int x, int y, int width, int height, out int stagingX)
    {
        stagingX = x + width + 4096;

        var screens = window.Screens;
        var allScreens = screens?.All;
        if (allScreens == null)
            return false;

        var centerX = x + width / 2;
        var centerY = y + height / 2;
        foreach (var screen in allScreens)
        {
            var bounds = screen.Bounds;
            var isOnScreen =
                centerX >= bounds.X &&
                centerX <= bounds.X + bounds.Width &&
                centerY >= bounds.Y &&
                centerY <= bounds.Y + bounds.Height;
            if (!isOnScreen)
                continue;

            stagingX = bounds.X + bounds.Width + 64;

            var workArea = screen.WorkingArea;
            return x < workArea.X ||
                   y < workArea.Y ||
                   x + width > workArea.X + workArea.Width ||
                   y + height > workArea.Y + workArea.Height;
        }

        return false;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(
        IntPtr display,
        IntPtr w,
        IntPtr property,
        IntPtr type,
        int format,
        int mode,
        IntPtr[] data,
        int nelements);

    [DllImport("libX11.so.6")]
    private static extern int XMoveResizeWindow(IntPtr display, IntPtr w, int x, int y, int width, int height);

    [DllImport("libX11.so.6")]
    private static extern int XMoveWindow(IntPtr display, IntPtr w, int x, int y);

    [DllImport("libX11.so.6")]
    private static extern int XConfigureWindow(IntPtr display, IntPtr w, uint valueMask, ref XWindowChanges changes);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, bool discard);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowChanges
    {
        public int x;
        public int y;
        public int width;
        public int height;
        public int border_width;
        public IntPtr sibling;
        public int stack_mode;
    }

    private const uint CWX = 1 << 0;
}
