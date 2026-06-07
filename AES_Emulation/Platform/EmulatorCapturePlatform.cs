using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Platform;

public static class EmulatorCapturePlatform
{
    /// <summary>
    /// Whether the host UI should embed emulator output via composition/portal capture.
    /// Linux capture was removed pending a rewrite; emulators launch in their own window there.
    /// </summary>
    public static bool SupportsCompositionCapture =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static void RevealWindowForCapture(IntPtr platformWindowHandle)
    {
        if (platformWindowHandle == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindows())
            RevealWindowForCaptureWindows(platformWindowHandle);
    }

    [SupportedOSPlatform("windows")]
    private static void RevealWindowForCaptureWindows(IntPtr hwnd)
        => ShowWindow(hwnd, SwShowNoActivate);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwShowNoActivate = 4;
}
