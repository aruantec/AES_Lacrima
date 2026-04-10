using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AES_Lacrima.Mac.API;

internal static class MacSystemDialogs
{
    private const string LibraryName = "libAesMacCaptureBridge";

    [DllImport(LibraryName)]
    private static extern int aes_mac_pick_emulator_application(StringBuilder buffer, int bufferChars);

    [DllImport(LibraryName, CharSet = CharSet.Ansi)]
    private static extern int aes_mac_pick_folder(string? title, StringBuilder buffer, int bufferChars);

    [DllImport(LibraryName)]
    private static extern void aes_mac_configure_portal_window(IntPtr windowHandle);

    [DllImport(LibraryName)]
    private static extern void aes_mac_order_window_below(IntPtr windowHandle, IntPtr siblingWindowHandle);

    [DllImport(LibraryName)]
    private static extern void aes_mac_attach_portal_window(IntPtr portalWindowHandle, IntPtr parentWindowHandle);

    [DllImport(LibraryName)]
    private static extern int aes_mac_window_content_to_screen(
        IntPtr windowHandle,
        double x,
        double y,
        out double screenX,
        out double screenY);

    public static string? PickEmulatorApplication()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var buffer = new StringBuilder(4096);
        var length = aes_mac_pick_emulator_application(buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : null;
    }

    public static string? PickFolder(string? title)
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var buffer = new StringBuilder(4096);
        var length = aes_mac_pick_folder(title, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : null;
    }

    public static void ConfigurePortalWindow(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsMacOS() || windowHandle == IntPtr.Zero)
            return;

        aes_mac_configure_portal_window(windowHandle);
    }

    public static void OrderWindowBelow(IntPtr windowHandle, IntPtr siblingWindowHandle)
    {
        if (!OperatingSystem.IsMacOS() || windowHandle == IntPtr.Zero || siblingWindowHandle == IntPtr.Zero)
            return;

        aes_mac_order_window_below(windowHandle, siblingWindowHandle);
    }

    public static void AttachPortalWindow(IntPtr portalWindowHandle, IntPtr parentWindowHandle)
    {
        if (!OperatingSystem.IsMacOS() || portalWindowHandle == IntPtr.Zero || parentWindowHandle == IntPtr.Zero)
            return;

        aes_mac_attach_portal_window(portalWindowHandle, parentWindowHandle);
    }

    public static bool TryConvertContentPointToScreen(IntPtr windowHandle, double x, double y, out double screenX, out double screenY)
    {
        screenX = 0;
        screenY = 0;
        if (!OperatingSystem.IsMacOS() || windowHandle == IntPtr.Zero)
            return false;

        return aes_mac_window_content_to_screen(windowHandle, x, y, out screenX, out screenY) != 0;
    }
}
