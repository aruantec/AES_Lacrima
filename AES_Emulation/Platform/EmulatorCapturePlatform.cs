using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Emulation.Linux.API;

namespace AES_Emulation.Platform;

public static class EmulatorCapturePlatform
{
    public static void RevealWindowForCapture(IntPtr platformWindowHandle)
    {
        if (platformWindowHandle == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindows())
            RevealWindowForCaptureWindows(platformWindowHandle);
        else if (OperatingSystem.IsLinux())
            RevealWindowForCaptureLinux(platformWindowHandle);
    }

    private static void RevealWindowForCaptureLinux(IntPtr platformWindowHandle)
    {
        if (platformWindowHandle == IntPtr.Zero)
            return;

        LinuxCaptureBridge.aes_linux_capture_reveal_window(platformWindowHandle);
    }

    [SupportedOSPlatform("windows")]
    private static void RevealWindowForCaptureWindows(IntPtr hwnd)
        => ShowWindow(hwnd, SwShowNoActivate);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwShowNoActivate = 4;
}
