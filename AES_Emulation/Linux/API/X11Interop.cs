using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
public static class X11Interop
{
    private const string libX11 = "libX11.so.6";
    private const string libXcomposite = "libXcomposite.so.1";

    public const int CompositeRedirectAutomatic = 0;
    public const int CompositeRedirectManual = 1;

    [DllImport(libX11)]
    public static extern IntPtr XOpenDisplay(string? display_name);

    [DllImport(libX11)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(libX11)]
    public static extern int XFreePixmap(IntPtr display, IntPtr pixmap);

    [DllImport(libXcomposite)]
    public static extern void XCompositeRedirectWindow(IntPtr display, IntPtr window, int update);

    [DllImport(libXcomposite)]
    public static extern void XCompositeUnredirectWindow(IntPtr display, IntPtr window, int update);

    [DllImport(libXcomposite)]
    public static extern IntPtr XCompositeNameWindowPixmap(IntPtr display, IntPtr window);

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
