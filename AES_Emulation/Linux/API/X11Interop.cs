using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
public static class X11Interop
{
    private const string libX11 = "libX11.so.6";
    private const string libXcomposite = "libXcomposite.so.1";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandlerDelegate(IntPtr display, IntPtr errorEvent);

    [DllImport(libX11)]
    private static extern XErrorHandlerDelegate? XSetErrorHandler(XErrorHandlerDelegate? handler);

    private static readonly object XErrorHandlerLock = new();
    private static readonly XErrorHandlerDelegate IgnoreBadWindowHandler = (_, _) => 0;

    static X11Interop()
    {
        try
        {
            XInitThreads();
        }
        catch
        {
        }
    }

    public const int CompositeRedirectAutomatic = 0;
    public const int CompositeRedirectManual = 1;

    public const int RevertToParent = 2;
    public const ulong CurrentTime = 0;

    [DllImport(libX11)]
    private static extern int XInitThreads();

    [DllImport(libX11)]
    public static extern IntPtr XOpenDisplay(string? display_name);

    [DllImport(libX11)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(libX11)]
    public static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(libX11)]
    public static extern IntPtr XCreateSimpleWindow(
        IntPtr display,
        IntPtr parent,
        int x,
        int y,
        uint width,
        uint height,
        uint border_width,
        ulong border,
        ulong background);

    [DllImport(libX11)]
    public static extern int XMapWindow(IntPtr display, IntPtr window);

    [DllImport(libX11)]
    public static extern int XUnmapWindow(IntPtr display, IntPtr window);

    [DllImport(libX11)]
    public static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport(libX11)]
    public static extern int XReparentWindow(IntPtr display, IntPtr window, IntPtr parent, int x, int y);

    [DllImport(libX11)]
    public static extern int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);

    [DllImport(libX11)]
    public static extern int XRaiseWindow(IntPtr display, IntPtr window);

    [DllImport(libX11)]
    public static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revert_to, ulong time);

    [DllImport(libX11)]
    public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport(libX11)]
    public static extern int XQueryTree(IntPtr display, IntPtr w, out IntPtr root_return, out IntPtr parent_return, out IntPtr children_return, out int nchildren_return);

    [DllImport(libX11)]
    public static extern int XFree(IntPtr data);

    [DllImport(libX11)]
    public static extern int XFreePixmap(IntPtr display, IntPtr pixmap);

    [DllImport(libXcomposite)]
    public static extern void XCompositeRedirectWindow(IntPtr display, IntPtr window, int update);

    [DllImport(libXcomposite)]
    public static extern void XCompositeUnredirectWindow(IntPtr display, IntPtr window, int update);

    [DllImport(libXcomposite)]
    public static extern IntPtr XCompositeNameWindowPixmap(IntPtr display, IntPtr window);

    public static void RunWithIgnoredXErrors(IntPtr display, Action action)
    {
        if (action == null)
            return;

        lock (XErrorHandlerLock)
        {
            var previousHandler = XSetErrorHandler(IgnoreBadWindowHandler);
            try
            {
                action();
                if (display != IntPtr.Zero)
                    XSync(display, false);
            }
            finally
            {
                XSetErrorHandler(previousHandler);
            }
        }
    }

    public static T RunWithIgnoredXErrors<T>(IntPtr display, Func<T> action, T fallbackValue)
    {
        if (action == null)
            return fallbackValue;

        lock (XErrorHandlerLock)
        {
            var previousHandler = XSetErrorHandler(IgnoreBadWindowHandler);
            try
            {
                var result = action();
                if (display != IntPtr.Zero)
                    XSync(display, false);
                return result;
            }
            catch
            {
                return fallbackValue;
            }
            finally
            {
                XSetErrorHandler(previousHandler);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public int depth;
        public IntPtr visual;
        public IntPtr root;
        public int class_type;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public ulong backing_planes;
        public ulong backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed;
        public int map_state;
        public ulong all_event_masks;
        public ulong your_event_mask;
        public ulong do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    [DllImport(libX11)]
    public static extern int XSync(IntPtr display, bool discard);

    [DllImport(libX11)]
    public static extern int XFlush(IntPtr display);

    [DllImport(libX11)]
    public static extern int XLowerWindow(IntPtr display, IntPtr window);

    [DllImport(libX11)]
    public static extern int XConfigureWindow(IntPtr display, IntPtr window, uint valueMask, ref XWindowChanges changes);

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowChanges
    {
        public int x, y;
        public int width, height;
        public int border_width;
        public IntPtr sibling;
        public int stack_mode;
    }

    public const uint CWX = 1 << 0;
    public const uint CWY = 1 << 1;
    public const uint CWWidth = 1 << 2;
    public const uint CWHeight = 1 << 3;
    public const uint CWBorderWidth = 1 << 4;
    public const uint CWSibling = 1 << 5;
    public const uint CWStackMode = 1 << 6;

    public const int Below = 1;
    public const int TopIf = 2;
    public const int BottomIf = 3;
    public const int Opposite = 4;
}
