using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux.API;

[SupportedOSPlatform("linux")]
internal static class XTestInterop
{
    private const string libXtst = "libXtst.so.6";

    public const int IsFake = 0;
    public const int Press = 1;
    public const int Release = 0;

    [DllImport(libXtst)]
    public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool isPress, ulong delay);

    [DllImport(libXtst)]
    public static extern int XTestFakeButtonEvent(IntPtr display, uint button, bool isPress, ulong delay);

    [DllImport(libXtst)]
    public static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    /// <summary>Allow this client to inject input even when another client has a grab.</summary>
    [DllImport(libXtst)]
    public static extern void XTestGrabControl(IntPtr display, bool impervious);

    /// <summary>Release a prior XTestGrabControl on this display connection.</summary>
    [DllImport(libXtst)]
    public static extern void XTestUngrabControl(IntPtr display);
}
