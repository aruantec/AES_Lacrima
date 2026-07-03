using AES_Controls.Helpers;
using AES_Controls.Helpers.Windows;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Windows.API;
using AES_Core.Logging;
using log4net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows.Parsec;

/// <summary>
/// Prepares emulator launches on a Parsec virtual monitor. Capture uses full-monitor WGC;
/// placement ensures emulator windows live on the VDD before capture attaches.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ParsecVirtualDisplayLaunchHelper
{
    private const uint MonitorDefaultToNull = 0;
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const int SwShow = 5;
    private const uint MonitorDefaultToNearest = 2;
    private const int StablePlacementThreshold = 6;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly ILog Log = LogHelper.For(typeof(ParsecVirtualDisplayLaunchHelper));
    private static readonly ConcurrentDictionary<IntPtr, byte> PreparedWindows = new();
    private static readonly ConcurrentDictionary<IntPtr, byte> TaskbarHiddenWindows = new();
    private static CancellationTokenSource? _placementCts;

    public static void CancelPlacement()
    {
        try { _placementCts?.Cancel(); }
        catch { /* ignored */ }
    }

    public static void PrepareStartInfoForVirtualDisplay(
        IEmulatorHandler handler,
        ProcessStartInfo startInfo,
        ParsecVirtualDisplayMonitor monitor)
    {
        var monitorIndex = ParsecVirtualDisplayMonitorHelper.TryGetDisplayMonitorIndex(monitor.Handle) ?? 0;
        ApplyGenericVirtualDisplayEnvironment(startInfo, monitorIndex, monitor);
        handler.PrepareStartInfoForVirtualDisplay(startInfo, monitorIndex, monitor);
        StripFullscreenLaunchArguments(startInfo);
    }

    public static void ApplyGenericVirtualDisplayEnvironment(
        ProcessStartInfo startInfo,
        int monitorIndex,
        ParsecVirtualDisplayMonitor monitor)
    {
        startInfo.UseShellExecute = false;
        startInfo.Environment["SDL_VIDEO_FULLSCREEN_DISPLAY"] = monitorIndex.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["SDL_VIDEO_WINDOW_POS"] =
            $"{monitor.Left.ToString(CultureInfo.InvariantCulture)},{monitor.Top.ToString(CultureInfo.InvariantCulture)}";
    }

    public static void BeginPlacement(Process process, ParsecVirtualDisplayMonitor monitor, IEmulatorHandler handler)
    {
        if (!OperatingSystem.IsWindows() || process == null)
            return;

        CancelPlacement();
        _placementCts = new CancellationTokenSource();
        var cancellationToken = _placementCts.Token;

        try
        {
            process.Refresh();
            ConcealProcessWindowsOffMonitor((uint)process.Id, monitor.Handle);
        }
        catch (Exception ex)
        {
            Log.Debug("Initial Parsec virtual display conceal failed.", ex);
        }

        _ = RunPlacementLoopAsync(process, monitor, handler, cancellationToken);
        _ = RunOffMonitorConcealerAsync(process, monitor, cancellationToken);
    }

    public static string DescribeCaptureTarget(IntPtr hwnd, ParsecVirtualDisplayMonitor monitor)
    {
        if (hwnd == IntPtr.Zero)
            return "hwnd=0";

        var onVdd = IsWindowOnMonitor(hwnd, monitor.Handle);
        if (!GetWindowRect(hwnd, out var rect))
            return $"hwnd=0x{hwnd.ToInt64():X}, onVdd={onVdd}, rect=unknown, vdd='{monitor.DeviceName}'";

        return
            $"hwnd=0x{hwnd.ToInt64():X}, onVdd={onVdd}, rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}, " +
            $"vdd='{monitor.DeviceName}' ({monitor.Left},{monitor.Top},{monitor.Width}x{monitor.Height})";
    }

    public static IntPtr TryGetWindowOnMonitor(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler)
    {
        if (!OperatingSystem.IsWindows() || process == null || monitor.Handle == IntPtr.Zero)
            return IntPtr.Zero;

        var captureHwnd = FindPreferredWindowInProcessTree(process, handler);
        if (captureHwnd == IntPtr.Zero)
            return IntPtr.Zero;

        var placementHwnd = ResolvePlacementWindow(process, handler, captureHwnd);
        TryPositionWindowOnMonitor(placementHwnd, monitor);
        return IsWindowOnMonitor(placementHwnd, monitor.Handle) ? captureHwnd : IntPtr.Zero;
    }

    public static async Task<IntPtr> TryResolveWindowOnMonitorAsync(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || process == null || monitor.Handle == IntPtr.Zero)
            return IntPtr.Zero;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                process.Refresh();
            }
            catch
            {
                return IntPtr.Zero;
            }

            var captureHwnd = FindPreferredWindowInProcessTree(process, handler);
            if (captureHwnd != IntPtr.Zero)
            {
                var placementHwnd = ResolvePlacementWindow(process, handler, captureHwnd);
                TryPositionWindowOnMonitor(placementHwnd, monitor);
                if (IsWindowOnMonitor(placementHwnd, monitor.Handle))
                    return captureHwnd;
            }

            await Task.Delay(attempt < 40 ? 10 : 50, cancellationToken).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    public static bool TryPositionWindowOnMonitor(IntPtr hwnd, ParsecVirtualDisplayMonitor monitor)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        try
        {
            WindowsStealth.UncloakWindow(hwnd);

            var alreadyPlaced = IsWindowPlacedOnMonitor(hwnd, monitor) && IsWindowOnMonitor(hwnd, monitor.Handle);
            if (alreadyPlaced)
                return true;

            if (PreparedWindows.TryAdd(hwnd, 0))
            {
                Win32API.TryExitFullscreenWindow(hwnd);
                Win32API.RemoveWindowDecorations(hwnd);
                HideWindowFromMainTaskbar(hwnd);
            }

            SetWindowPos(hwnd, HwndTopmost, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
                SwpNoActivate);
            ShowWindow(hwnd, SwShow);
            return IsWindowPlacedOnMonitor(hwnd, monitor);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to position hwnd 0x{hwnd.ToInt64():X} on Parsec virtual display.", ex);
            return false;
        }
    }

    public static bool IsWindowOnMonitor(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (hwnd == IntPtr.Zero || monitorHandle == IntPtr.Zero)
            return false;

        if (MonitorFromWindow(hwnd, MonitorDefaultToNull) == monitorHandle)
            return true;

        // Cloaked or mid-move windows can report the wrong monitor; fall back to geometry.
        return IsWindowRectOnMonitor(hwnd, monitorHandle);
    }

    private static bool IsWindowRectOnMonitor(IntPtr hwnd, IntPtr monitorHandle)
    {
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return false;

        var centerX = rect.Left + rect.Width / 2;
        var centerY = rect.Top + rect.Height / 2;
        var centerHwnd = new POINT { X = centerX, Y = centerY };
        var monitor = MonitorFromPoint(centerHwnd, MonitorDefaultToNearest);
        return monitor == monitorHandle;
    }

    private static void HideWindowFromMainTaskbar(IntPtr hwnd)
    {
        if (!TaskbarHiddenWindows.TryAdd(hwnd, 0))
            return;

        try
        {
            WindowsStealth.RemoveFromTaskbar(hwnd);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to hide emulator window from main taskbar.", ex);
        }
    }

    private static void StripFullscreenLaunchArguments(ProcessStartInfo startInfo)
    {
        for (var i = startInfo.ArgumentList.Count - 1; i >= 0; i--)
        {
            var arg = startInfo.ArgumentList[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (string.Equals(arg, "-fullscreen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-f", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.RemoveAt(i);
                continue;
            }

            if (arg.StartsWith("--fullscreen=", StringComparison.OrdinalIgnoreCase) &&
                !arg.StartsWith("--fullscreen=false", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.RemoveAt(i);
            }
        }
    }

    public static void EnsureCaptureTargetOnMonitor(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        IntPtr captureHwnd)
    {
        if (captureHwnd == IntPtr.Zero)
            return;

        var placementHwnd = ResolvePlacementWindow(process, handler, captureHwnd);
        TryPositionWindowOnMonitor(placementHwnd, monitor);
        WindowsStealth.UncloakWindow(placementHwnd);
        WindowsStealth.UncloakWindow(captureHwnd);
        Win32API.SetWindowOpacity(captureHwnd, 255);
        Win32API.SetWindowOpacity(placementHwnd, 255);
    }

    private static IntPtr ResolvePlacementWindow(Process process, IEmulatorHandler handler, IntPtr captureHwnd)
    {
        try
        {
            var placementHwnd = handler.FindVirtualDisplayPlacementWindowHandle(process);
            if (placementHwnd != IntPtr.Zero)
                return placementHwnd;
        }
        catch
        {
            // ignored
        }

        return GetRootWindow(captureHwnd);
    }

    private static IntPtr GetRootWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;

        var root = GetAncestor(hwnd, GaRoot);
        return root != IntPtr.Zero ? root : hwnd;
    }

    private static IntPtr FindPreferredWindowInProcessTree(Process process, IEmulatorHandler handler)
    {
        foreach (var pid in EnumerateProcessTreeIds(process.Id))
        {
            try
            {
                using var candidate = Process.GetProcessById(pid);
                var hwnd = handler.FindPreferredWindowHandle(candidate);
                if (hwnd != IntPtr.Zero)
                    return hwnd;
            }
            catch
            {
                // Process may have exited between enumeration and lookup.
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<int> EnumerateProcessTreeIds(int rootProcessId)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!visited.Add(pid))
                continue;

            yield return pid;

            foreach (var childPid in ReadChildProcessIds(pid))
                queue.Enqueue(childPid);
        }
    }

    private static IEnumerable<int> ReadChildProcessIds(int parentProcessId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            yield break;

        try
        {
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                yield break;

            do
            {
                if ((int)entry.Th32ParentProcessID == parentProcessId && entry.Th32ProcessID != 0)
                    yield return (int)entry.Th32ProcessID;
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static bool IsWindowPlacedOnMonitor(IntPtr hwnd, ParsecVirtualDisplayMonitor monitor)
    {
        if (!GetWindowRect(hwnd, out var rect))
            return false;

        return rect.Left == monitor.Left &&
               rect.Top == monitor.Top &&
               rect.Width == monitor.Width &&
               rect.Height == monitor.Height;
    }

    private static async Task RunPlacementLoopAsync(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        IEmulatorHandler handler,
        CancellationToken cancellationToken)
    {
        var stableCount = 0;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                process.Refresh();
            }
            catch
            {
                return;
            }

            var captureHwnd = FindPreferredWindowInProcessTree(process, handler);
            if (captureHwnd != IntPtr.Zero)
            {
                var placementHwnd = ResolvePlacementWindow(process, handler, captureHwnd);
                if (TryPositionWindowOnMonitor(placementHwnd, monitor) &&
                    IsWindowOnMonitor(placementHwnd, monitor.Handle))
                {
                    stableCount++;
                    if (stableCount >= StablePlacementThreshold)
                        return;
                }
                else
                {
                    stableCount = 0;
                }
            }
            else
            {
                stableCount = 0;
            }

            await Task.Delay(attempt < 40 ? 25 : 75, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunOffMonitorConcealerAsync(
        Process process,
        ParsecVirtualDisplayMonitor monitor,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                process.Refresh();
                ConcealProcessWindowsOffMonitor((uint)process.Id, monitor.Handle);
            }
            catch
            {
                return;
            }

            await Task.Delay(attempt < 160 ? 5 : 25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ConcealProcessWindowsOffMonitor(uint processId, IntPtr monitorHandle)
    {
        foreach (var pid in EnumerateProcessTreeIds((int)processId))
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var windowPid);
                if (windowPid != pid || !IsWindowVisible(hwnd))
                    return true;

                if (IsWindowOnMonitor(hwnd, monitorHandle))
                {
                    WindowsStealth.UncloakWindow(hwnd);
                    return true;
                }

                ConcealWindowOffVirtualDisplay(hwnd);
                return true;
            }, IntPtr.Zero);
        }
    }

    private static void ConcealWindowOffVirtualDisplay(IntPtr hwnd)
    {
        try
        {
            // Do not DWM-cloak: cloaked windows stay black in WGC capture even after repositioning.
            ShowWindow(hwnd, 0);
        }
        catch
        {
            // ignored
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private const int GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}
