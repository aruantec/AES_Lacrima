using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Emulation.Linux.API;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
internal static class LinuxX11WindowHelper
{
    private const string libX11 = "libX11.so.6";

    [DllImport(libX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(libX11)]
    private static extern int XQueryTree(
        IntPtr display,
        IntPtr window,
        out IntPtr rootReturn,
        out IntPtr parentReturn,
        out IntPtr childrenReturn,
        out uint nChildrenReturn);

    [DllImport(libX11)]
    private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr windowNameReturn);

    [DllImport(libX11)]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out X11Interop.XWindowAttributes attributes);

    [DllImport(libX11)]
    private static extern uint XKeysymToKeycode(IntPtr display, nuint keysym);

    private const int RevertToParent = 1;

    [DllImport(libX11)]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, nuint time);

    public static IntPtr GetDefaultRootWindow(IntPtr display) => XDefaultRootWindow(display);

    public static bool TrySetInputFocus(IntPtr display, IntPtr window)
    {
        if (display == IntPtr.Zero || window == IntPtr.Zero)
            return false;

        return XSetInputFocus(display, window, RevertToParent, 0) != 0;
    }

    public static uint KeysymToKeycode(IntPtr display, uint keysym) => XKeysymToKeycode(display, keysym);

    public static bool TryGetWindowGeometry(IntPtr display, IntPtr window, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (XGetWindowAttributes(display, window, out var attributes) == 0)
            return false;

        width = attributes.width;
        height = attributes.height;
        return width > 0 && height > 0;
    }

    public static IEnumerable<IntPtr> EnumerateMappedWindows(IntPtr display, IntPtr root)
    {
        var pending = new Queue<IntPtr>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var window = pending.Dequeue();
            if (XQueryTree(display, window, out _, out _, out var children, out var childCount) == 0)
                continue;

            try
            {
                if (children != IntPtr.Zero && childCount > 0)
                {
                    var childArray = new IntPtr[childCount];
                    Marshal.Copy(children, childArray, 0, (int)childCount);
                    foreach (var child in childArray)
                        pending.Enqueue(child);
                }
            }
            finally
            {
                if (children != IntPtr.Zero)
                    X11Interop.XFree(children);
            }

            if (window == root)
                continue;

            if (XGetWindowAttributes(display, window, out var attributes) == 0)
                continue;

            if (attributes.map_state == 2) // IsViewable
                yield return window;
        }
    }
}
