using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
internal static class LinuxWindowHelper
{
    private const string libX11 = "libX11.so.6";

    [DllImport(libX11)]
    private static extern int XQueryTree(IntPtr display, IntPtr w, out IntPtr root_return, out IntPtr parent_return, out IntPtr children_return, out int nchildren_return);

    [DllImport(libX11)]
    private static extern int XFree(IntPtr data);

    [DllImport(libX11)]
    private static extern IntPtr XInternAtom(IntPtr display, string atom_name, bool only_if_exists);

    [DllImport(libX11)]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr w, IntPtr property, IntPtr long_offset, IntPtr long_length, bool delete, IntPtr req_type, out IntPtr actual_type_return, out int actual_format_return, out IntPtr nitems_return, out IntPtr bytes_after_return, out IntPtr prop_return);

    [DllImport(libX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(libX11)]
    private static extern int XFetchName(IntPtr display, IntPtr w, out IntPtr window_name_return);

    [DllImport(libX11)]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr w, out X11Interop.XWindowAttributes window_attributes_return);

    public static List<IntPtr> FindWindowsByPid(int pid)
    {
        var result = new List<IntPtr>();
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return result;

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            if (root == IntPtr.Zero) return result;

            IntPtr netWmPidAtom = XInternAtom(display, "_NET_WM_PID", true);
            if (netWmPidAtom == IntPtr.Zero) return result;

            SearchTree(display, root, netWmPidAtom, pid, result);
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }

        return result;
    }

    private static void SearchTree(IntPtr display, IntPtr window, IntPtr netWmPidAtom, int pid, List<IntPtr> result)
    {
        if (window == IntPtr.Zero) return;

        if (GetWindowPid(display, window, netWmPidAtom) == pid)
        {
            result.Add(window);
        }

        if (XQueryTree(display, window, out _, out _, out IntPtr children_ptr, out int nchildren) != 0 && nchildren > 0 && children_ptr != IntPtr.Zero)
        {
            var children = new IntPtr[nchildren];
            Marshal.Copy(children_ptr, children, 0, nchildren);
            XFree(children_ptr);

            foreach (var child in children)
            {
                SearchTree(display, child, netWmPidAtom, pid, result);
            }
        }
    }

    private static int GetWindowPid(IntPtr display, IntPtr window, IntPtr netWmPidAtom)
    {
        if (XGetWindowProperty(display, window, netWmPidAtom, IntPtr.Zero, (IntPtr)1, false, IntPtr.Zero, out _, out int format, out IntPtr nitems, out _, out IntPtr propReturn) == 0)
        {
            if (propReturn != IntPtr.Zero)
            {
                int windowPid = 0;
                if ((long)nitems > 0 && format == 32)
                {
                    windowPid = Marshal.ReadInt32(propReturn);
                }
                XFree(propReturn);
                return windowPid;
            }
        }
        return 0;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return string.Empty;

        try
        {
            return GetWindowTitleInternal(display, hwnd);
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
    }

    private static string GetWindowTitleInternal(IntPtr display, IntPtr hwnd)
    {
        try
        {
            IntPtr netWmName = XInternAtom(display, "_NET_WM_NAME", true);
            if (netWmName != IntPtr.Zero)
            {
                if (XGetWindowProperty(display, hwnd, netWmName, IntPtr.Zero, (IntPtr)1024, false, IntPtr.Zero, out _, out int format, out IntPtr nitems, out _, out IntPtr propReturn) == 0)
                {
                    if (propReturn != IntPtr.Zero)
                    {
                        if ((long)nitems > 0)
                        {
                            string titleNet = Marshal.PtrToStringUTF8(propReturn) ?? string.Empty;
                            XFree(propReturn);
                            return titleNet;
                        }
                        XFree(propReturn);
                    }
                }
            }

            if (XFetchName(display, hwnd, out IntPtr namePtr) != 0 && namePtr != IntPtr.Zero)
            {
                string title = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
                XFree(namePtr);
                return title;
            }
        }
        catch { }
        return string.Empty;
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return string.Empty;

        try
        {
            return GetWindowClassNameInternal(display, hwnd);
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
    }

    private static string GetWindowClassNameInternal(IntPtr display, IntPtr hwnd)
    {
        try
        {
            if (X11Interop.XGetClassHint(display, hwnd, out var classHint) != 0)
            {
                string className = Marshal.PtrToStringAnsi(classHint.res_class) ?? string.Empty;
                if (classHint.res_name != IntPtr.Zero) X11Interop.XFree(classHint.res_name);
                if (classHint.res_class != IntPtr.Zero) X11Interop.XFree(classHint.res_class);
                return className;
            }
        }
        catch { }
        return string.Empty;
    }

    public static List<IntPtr> FindWindowsByTitle(string titleSubstring)
    {
        var result = new List<IntPtr>();
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return result;

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            if (root != IntPtr.Zero)
            {
                string lowerHint = titleSubstring.ToLowerInvariant();
                SearchTreeForTitle(display, root, lowerHint, result);
            }
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
        return result;
    }

    private static void SearchTreeForTitle(IntPtr display, IntPtr window, string lowerHint, List<IntPtr> result)
    {
        if (window == IntPtr.Zero) return;

        string title = GetWindowTitleInternal(display, window).ToLowerInvariant();
        if (!string.IsNullOrEmpty(title) && title.Contains(lowerHint))
        {
            result.Add(window);
        }

        if (XQueryTree(display, window, out _, out _, out IntPtr children_ptr, out int nchildren) != 0 && nchildren > 0 && children_ptr != IntPtr.Zero)
        {
            var children = new IntPtr[nchildren];
            Marshal.Copy(children_ptr, children, 0, nchildren);
            XFree(children_ptr);

            foreach (var child in children)
            {
                SearchTreeForTitle(display, child, lowerHint, result);
            }
        }
    }

    public static bool IsWindowVisible(IntPtr hwnd)
    {
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return false;

        try
        {
            if (XGetWindowAttributes(display, hwnd, out var attrs) != 0)
            {
                // map_state: 0 = IsUnmapped, 1 = IsUnviewable, 2 = IsViewable
                return attrs.map_state == 2;
            }
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
        return false;
    }

    public static bool TryGetWindowGeometry(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (hwnd == IntPtr.Zero)
            return false;

        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero)
            return false;

        try
        {
            if (XGetWindowAttributes(display, hwnd, out var attrs) == 0)
                return false;

            width = attrs.width;
            height = attrs.height;
            return width > 0 && height > 0;
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
    }

    public static IntPtr FindBestCaptureWindow(int pid, params string?[] titleHints)
    {
        IntPtr best = IntPtr.Zero;
        long bestScore = long.MinValue;

        foreach (var hint in titleHints)
        {
            if (string.IsNullOrWhiteSpace(hint))
                continue;

            var hinted = LinuxCaptureBridge.aes_linux_capture_find_window_by_pid(pid, hint);
            var score = ScoreCaptureWindow(hinted, hint);
            if (score > bestScore)
            {
                bestScore = score;
                best = hinted;
            }
        }

        foreach (var window in FindWindowsByPid(pid))
        {
            var score = ScoreCaptureWindow(window, null);
            if (score > bestScore)
            {
                bestScore = score;
                best = window;
            }
        }

        foreach (var hint in titleHints)
        {
            if (string.IsNullOrWhiteSpace(hint))
                continue;

            foreach (var window in FindWindowsByTitle(hint))
            {
                var score = ScoreCaptureWindow(window, hint);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = window;
                }
            }
        }

        return best;
    }

    private static long ScoreCaptureWindow(IntPtr hwnd, string? titleHint)
    {
        if (hwnd == IntPtr.Zero)
            return long.MinValue;

        if (!TryGetWindowGeometry(hwnd, out var width, out var height))
            return long.MinValue;

        if (width < 320 || height < 240)
            return long.MinValue;

        if (!IsWindowVisible(hwnd))
            return long.MinValue / 2;

        var score = (long)width * height;
        var title = GetWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(title))
            score += 250_000;

        if (!string.IsNullOrWhiteSpace(titleHint) &&
            title.Contains(titleHint, StringComparison.OrdinalIgnoreCase))
        {
            score += 500_000;
        }

        var lowerTitle = title.ToLowerInvariant();
        if (lowerTitle.Contains("settings", StringComparison.Ordinal) ||
            lowerTitle.Contains("about", StringComparison.Ordinal) ||
            lowerTitle.Contains("input", StringComparison.Ordinal) ||
            lowerTitle.Contains("controller", StringComparison.Ordinal) ||
            lowerTitle.Contains("cheat", StringComparison.Ordinal) ||
            lowerTitle.Contains("shader", StringComparison.Ordinal))
        {
            score -= 750_000;
        }

        return score;
    }
}
