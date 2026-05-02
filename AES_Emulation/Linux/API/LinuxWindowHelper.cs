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

            X11Interop.RunWithIgnoredXErrors(display, () => SearchTree(display, root, netWmPidAtom, pid, result));
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

    public static string GetWindowClassName(IntPtr hwnd)
    {
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return string.Empty;

        try
        {
            return X11Interop.RunWithIgnoredXErrors(display, () => GetWindowClassNameInternal(display, hwnd), string.Empty);
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
            IntPtr wmClass = XInternAtom(display, "WM_CLASS", true);
            if (wmClass != IntPtr.Zero)
            {
                if (XGetWindowProperty(display, hwnd, wmClass, IntPtr.Zero, (IntPtr)1024, false, IntPtr.Zero, out _, out int format, out IntPtr nitems, out _, out IntPtr propReturn) == 0)
                {
                    if (propReturn != IntPtr.Zero)
                    {
                        if ((long)nitems > 0)
                        {
                            // WM_CLASS is "res_name\0res_class\0"
                            // We return the class which is usually the second string if present
                            string classes = Marshal.PtrToStringAnsi(propReturn) ?? string.Empty;
                            int firstNull = classes.IndexOf('\0');
                            string resClass = Marshal.PtrToStringAnsi(propReturn + (firstNull + 1)) ?? string.Empty;

                            XFree(propReturn);
                            return string.IsNullOrEmpty(resClass) ? classes : resClass;
                        }
                        XFree(propReturn);
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return string.Empty;

        try
        {
            return X11Interop.RunWithIgnoredXErrors(display, () => GetWindowTitleInternal(display, hwnd), string.Empty);
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

    public static List<IntPtr> FindWindowsByClass(string classSubstring)
    {
        var result = new List<IntPtr>();
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display == IntPtr.Zero) return result;

        try
        {
            IntPtr root = XDefaultRootWindow(display);
            if (root != IntPtr.Zero)
            {
                string lowerHint = classSubstring.ToLowerInvariant();
                X11Interop.RunWithIgnoredXErrors(display, () => SearchTreeForClass(display, root, lowerHint, result));
            }
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
        return result;
    }

    private static void SearchTreeForClass(IntPtr display, IntPtr window, string lowerHint, List<IntPtr> result)
    {
        if (window == IntPtr.Zero) return;

        string className = GetWindowClassNameInternal(display, window).ToLowerInvariant();
        if (!string.IsNullOrEmpty(className) && className.Contains(lowerHint))
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
                SearchTreeForClass(display, child, lowerHint, result);
            }
        }
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
                X11Interop.RunWithIgnoredXErrors(display, () => SearchTreeForTitle(display, root, lowerHint, result));
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
            return X11Interop.RunWithIgnoredXErrors(display, () =>
            {
                if (XGetWindowAttributes(display, hwnd, out var attrs) != 0)
                {
                    // map_state: 0 = IsUnmapped, 1 = IsUnviewable, 2 = IsViewable
                    return attrs.map_state == 2;
                }

                return false;
            }, false);
        }
        finally
        {
            X11Interop.XCloseDisplay(display);
        }
    }

    private static void SearchTreeByClass(IntPtr display, IntPtr window, string lowerHint, List<IntPtr> result)
    {
        if (window == IntPtr.Zero) return;

        string currentClass = GetWindowClassNameInternal(display, window).ToLowerInvariant();
        if (!string.IsNullOrEmpty(currentClass) && currentClass.Contains(lowerHint))
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
                SearchTreeByClass(display, child, lowerHint, result);
            }
        }
    }
}
