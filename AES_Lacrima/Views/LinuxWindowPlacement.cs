using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AES_Lacrima.Views;

internal static class LinuxWindowPlacement
{
    private const int PropModeReplace = 0;
    private const int XA_ATOM = 4;
    private static readonly Dictionary<IntPtr, bool> BottomOverflowUnlocked = new();
    private static readonly Dictionary<IntPtr, long> PlacementVersions = new();
    private static long _nextPlacementVersion;

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

    public static bool TryConfigureClickThrough(Window window)
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
            // Empty input region => pointer events pass through this window.
            var region = XFixesCreateRegion(display, IntPtr.Zero, 0);
            if (region == IntPtr.Zero)
                return false;

            try
            {
                XFixesSetWindowShapeRegion(display, handle, ShapeInput, 0, 0, region);
                XFlush(display);
                return true;
            }
            finally
            {
                XFixesDestroyRegion(display, region);
            }
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

            if (ShouldStageThroughRightEdge(window, handle, x, y, width, height, out var stagingX, out var postFinalMove))
            {
                XMoveResizeWindow(display, handle, stagingX, y, width, height);
                XFlush(display);
                if (postFinalMove)
                {
                    ScheduleFinalMoveResize(handle, x, y, width, height);
                }
                else
                {
                    XSync(display, false);
                    var changes = new XWindowChanges { x = x };
                    XConfigureWindow(display, handle, CWX, ref changes);
                    XFlush(display);
                }
                return true;
            }

            MarkPlacementVersion(handle);
            XMoveResizeWindow(
                display,
                handle,
                x,
                y,
                width,
                height);
            XFlush(display);
            UpdateBottomOverflowState(window, handle, x, y, width, height);
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

    private static bool ShouldStageThroughRightEdge(
        Window window,
        IntPtr handle,
        int x,
        int y,
        int width,
        int height,
        out int stagingX,
        out bool postFinalMove)
    {
        stagingX = x + width + 4096;
        postFinalMove = false;

        if (!TryGetScreenAreas(window, x, y, width, height, out _, out var workArea))
            return false;

        stagingX = workArea.X + workArea.Width + 64;

        var overflowLeft = x < workArea.X;
        var overflowTop = y < workArea.Y;
        var overflowRight = x + width > workArea.X + workArea.Width;
        var overflowBottom = y + height > workArea.Y + workArea.Height;
        var isBottomUnlocked = BottomOverflowUnlocked.TryGetValue(handle, out var unlocked) && unlocked;

        if (overflowBottom)
        {
            if (!isBottomUnlocked)
            {
                BottomOverflowUnlocked[handle] = true;
                postFinalMove = true;
                return true;
            }

            return overflowLeft || overflowTop || overflowRight;
        }

        BottomOverflowUnlocked[handle] = false;
        return overflowLeft || overflowTop || overflowRight;
    }

    private static void ScheduleFinalMoveResize(IntPtr handle, int x, int y, int width, int height)
    {
        var version = MarkPlacementVersion(handle);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!PlacementVersions.TryGetValue(handle, out var latestVersion) || latestVersion != version)
                    return;

                TryMoveResizeHandle(handle, x, y, width, height);
            },
            DispatcherPriority.Background);
    }

    private static long MarkPlacementVersion(IntPtr handle)
    {
        var version = ++_nextPlacementVersion;
        PlacementVersions[handle] = version;
        return version;
    }

    private static bool TryMoveResizeHandle(IntPtr handle, int x, int y, int width, int height)
    {
        if (handle == IntPtr.Zero)
            return false;

        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            XMoveResizeWindow(display, handle, x, y, Math.Max(1, width), Math.Max(1, height));
            XFlush(display);
            return true;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static void UpdateBottomOverflowState(Window window, IntPtr handle, int x, int y, int width, int height)
    {
        if (!TryGetScreenAreas(window, x, y, width, height, out _, out var workArea))
            return;

        if (y + height <= workArea.Y + workArea.Height)
            BottomOverflowUnlocked[handle] = false;
    }

    private static bool TryGetScreenAreas(Window window, int x, int y, int width, int height, out PixelRect bounds, out PixelRect workArea)
    {
        bounds = default;
        workArea = default;

        var screens = window.Screens;
        var allScreens = screens?.All;
        if (allScreens == null)
            return false;

        var centerX = x + width / 2;
        var centerY = y + height / 2;
        foreach (var screen in allScreens)
        {
            bounds = screen.Bounds;
            var isOnScreen =
                centerX >= bounds.X &&
                centerX <= bounds.X + bounds.Width &&
                centerY >= bounds.Y &&
                centerY <= bounds.Y + bounds.Height;
            if (!isOnScreen)
                continue;

            workArea = screen.WorkingArea;
            return true;
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

    [DllImport("libXfixes.so.3")]
    private static extern IntPtr XFixesCreateRegion(IntPtr display, IntPtr rectangles, int nrectangles);

    [DllImport("libXfixes.so.3")]
    private static extern void XFixesSetWindowShapeRegion(IntPtr display, IntPtr window, int shapeKind, int xOffset, int yOffset, IntPtr region);

    [DllImport("libXfixes.so.3")]
    private static extern void XFixesDestroyRegion(IntPtr display, IntPtr region);

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
    private const int ShapeInput = 2;
}
