using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Windows.API;
using log4net;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows.VirtualDisplay;

/// <summary>
/// Positions emulator windows on a dedicated virtual monitor and resolves capture/input targets there.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsVirtualDisplayLaunchHelper
{
    private const uint MonitorDefaultToNull = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpHideWindow = 0x0080;
    private const int SwShowNa = 8;
    private static readonly ILog Log = LogHelper.For(typeof(WindowsVirtualDisplayLaunchHelper));

    public static void PrepareStartInfoForVirtualDisplay(
        IEmulatorHandler handler,
        ProcessStartInfo startInfo,
        WindowsVirtualDisplayMonitor monitor)
    {
        var monitorIndex = WindowsVirtualDisplayMonitorHelper.TryGetDisplayMonitorIndex(monitor.Handle) ?? 0;
        handler.PrepareStartInfoForVirtualDisplay(startInfo, monitorIndex, monitor);
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    }

    public static void BeginPlacement(Process process, WindowsVirtualDisplayMonitor monitor, IEmulatorHandler handler)
    {
        if (!OperatingSystem.IsWindows() || process == null)
            return;

        try
        {
            process.Refresh();
            ConcealProcessWindowsOffVirtualDisplay((uint)process.Id, monitor);
        }
        catch (Exception ex)
        {
            Log.Debug("Initial virtual display conceal failed.", ex);
        }

        _ = RunPlacementLoopAsync(process, monitor, handler, CancellationToken.None);
    }

    public static async Task<bool> PositionProcessOnVirtualDisplayAsync(
        Process process,
        WindowsVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        CancellationToken cancellationToken = default)
    {
        BeginPlacement(process, monitor, handler);
        return await WaitForWindowOnVirtualDisplayAsync(process, monitor, handler, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IntPtr> TryResolveWindowOnMonitorAsync(
        Process process,
        IntPtr monitorHandle,
        IEmulatorHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || process == null || monitorHandle == IntPtr.Zero)
            return IntPtr.Zero;

        const int maxAttempts = 200;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                process.Refresh();
                if (process.HasExited)
                    return IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }

            var hwnd = handler.FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero && IsWindowOnMonitor(hwnd, monitorHandle))
                return hwnd;

            await Task.Delay(attempt < 40 ? 10 : 50, cancellationToken).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    public static bool TryPositionWindowOnMonitor(IntPtr hwnd, WindowsVirtualDisplayMonitor monitor)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        try
        {
            Win32API.TryExitFullscreenWindow(hwnd);
            Win32API.RemoveWindowDecorations(hwnd);

            // Move while hidden so the window never flashes on the primary desktop.
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                monitor.Left,
                monitor.Top,
                monitor.Width,
                monitor.Height,
                SwpNoZOrder | SwpNoActivate | SwpHideWindow);
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                monitor.Left,
                monitor.Top,
                monitor.Width,
                monitor.Height,
                SwpNoZOrder | SwpNoActivate);
            ShowWindow(hwnd, SwShowNa);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to position hwnd 0x{hwnd.ToInt64():X} on virtual display.", ex);
            return false;
        }
    }

    public static bool IsWindowOnMonitor(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (hwnd == IntPtr.Zero || monitorHandle == IntPtr.Zero)
            return false;

        return MonitorFromWindow(hwnd, MonitorDefaultToNull) == monitorHandle;
    }

    private static async Task RunPlacementLoopAsync(
        Process process,
        WindowsVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 400;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                process.Refresh();
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            ConcealProcessWindowsOffVirtualDisplay((uint)process.Id, monitor);

            var hwnd = handler.FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
            {
                if (!IsWindowOnMonitor(hwnd, monitor.Handle))
                    TryPositionWindowOnMonitor(hwnd, monitor);
                else
                    return;
            }

            await Task.Delay(attempt < 60 ? 10 : 25, cancellationToken).ConfigureAwait(false);
        }

        Log.Warn($"Timed out placing emulator pid={process.Id} on virtual display '{monitor.DeviceName}'.");
    }

    private static async Task<bool> WaitForWindowOnVirtualDisplayAsync(
        Process process,
        WindowsVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        CancellationToken cancellationToken)
    {
        var hwnd = await TryResolveWindowOnMonitorAsync(process, monitor.Handle, handler, cancellationToken)
            .ConfigureAwait(false);
        return hwnd != IntPtr.Zero;
    }

    private static void ConcealProcessWindowsOffVirtualDisplay(uint processId, WindowsVirtualDisplayMonitor targetMonitor)
    {
        EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero)
                    return true;

                if (GetWindowThreadProcessId(hwnd, out var windowPid) == 0 || windowPid != processId)
                    return true;

                if (IsWindowOnMonitor(hwnd, targetMonitor.Handle))
                    return true;

                if (!IsWindowVisible(hwnd))
                {
                    TryPositionWindowOnMonitor(hwnd, targetMonitor);
                    return true;
                }

                try
                {
                    Win32API.HideWindowForOffScreenCapture(hwnd);
                    TryPositionWindowOnMonitor(hwnd, targetMonitor);
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to conceal hwnd 0x{hwnd.ToInt64():X} off the virtual display.", ex);
                }

                return true;
            },
            IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
