using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.DI;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux.API;
using log4net;
using Avalonia.OpenGL;

namespace AES_Emulation.Linux.Platform;

[SupportedOSPlatform("linux")]
[AutoRegister(DependencyLifetime.Singleton)]
public class LinuxScreenCaptureService : AES_Emulation.Platform.IScreenCaptureService
{
    private static readonly ILog SLog = LogHelper.For<LinuxScreenCaptureService>();
    
    private const string libX11 = "libX11.so.6";
    [DllImport(libX11)]
    private static extern int XLowerWindow(IntPtr display, IntPtr w);

    public bool HideUntilCaptured => true;

    public IntPtr FindPreferredWindowHandle(Process process)
    {
        // This relies on the overridden EmulatorHandler logic, or we fallback to our helper
        try
        {
            var windows = LinuxWindowHelper.FindWindowsByPid(process.Id);
            foreach (var w1 in windows)
            {
                if (w1 != IntPtr.Zero)
                    return w1;
            }
        }
        catch (Exception ex)
        {
            SLog.Debug("LinuxScreenCaptureService failed to find windows.", ex);
        }
        
        return IntPtr.Zero;
    }

    public void PrepareProcessForCapture(Process process)
    {
        // Not used directly, we act on hwnd
    }

    public void PrepareWindowForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsLinux()) return;
        
        IntPtr display = X11Interop.XOpenDisplay(null);
        if (display != IntPtr.Zero)
        {
            try
            {
                // Lower window to the bottom of the Z-order so it is hidden behind our UI.
                // We avoid moving it extremely offscreen because some Compositors freeze XComposite updates for offscreen windows.
                XLowerWindow(display, hwnd);
            }
            finally
            {
                X11Interop.XCloseDisplay(display);
            }
        }
    }

    public async Task<IntPtr> ResolveCaptureTargetAsync(Process process, IEmulatorHandler handler, CancellationToken cancellationToken)
    {
        const int maxAttempts = 300;
        const int delayMs = 100;
        const int stableAttemptsBeforeStop = 6;

        IntPtr assignedHwnd = IntPtr.Zero;
        var assignedStableAttempts = 0;
        var hasAssignedHandle = false;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hwnd = handler.FindPreferredWindowHandle(process);
            
            // Try to find via main window handle if our PID traversal didn't work immediately
            if (hwnd == IntPtr.Zero)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    hwnd = process.MainWindowHandle;
            }

            if (hwnd != IntPtr.Zero)
            {
                if (HideUntilCaptured)
                    handler.PrepareWindowForCapture(hwnd);

                if (hwnd == assignedHwnd)
                {
                    assignedStableAttempts++;
                }
                else
                {
                    assignedHwnd = hwnd;
                    assignedStableAttempts = 1;
                    hasAssignedHandle = true;
                }

                if (hasAssignedHandle && assignedStableAttempts >= stableAttemptsBeforeStop)
                    break;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return assignedHwnd;
    }
}
