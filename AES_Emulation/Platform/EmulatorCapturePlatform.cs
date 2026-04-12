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

    public static void HideWindowForCapture(IntPtr platformWindowHandle)
    {
        if (platformWindowHandle == IntPtr.Zero)
            return;

        if (OperatingSystem.IsLinux())
            HideWindowForCaptureLinux(platformWindowHandle);
    }

    private static void HideWindowForCaptureLinux(IntPtr platformWindowHandle)
    {
        if (platformWindowHandle == IntPtr.Zero)
            return;

        LinuxCaptureBridge.aes_linux_capture_hide_window(platformWindowHandle);
    }

    public static IntPtr FindWindowByPid(int pid, string? titleHint = null)
    {
        if (OperatingSystem.IsLinux())
            return LinuxCaptureBridge.aes_linux_capture_find_window_by_pid(pid, titleHint);

        return IntPtr.Zero;
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
