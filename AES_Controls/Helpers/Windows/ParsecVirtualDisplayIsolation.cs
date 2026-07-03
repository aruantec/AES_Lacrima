using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.Logging;
using log4net;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Keeps the Parsec virtual monitor isolated from the main desktop (gamescope-like).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ParsecVirtualDisplayIsolation
{
    private const uint MonitorDefaultToNearest = 2;
    private const int SwHide = 0;
    private static readonly ILog Log = LogHelper.For(typeof(ParsecVirtualDisplayIsolation));
    private static readonly string[] TaskbarWindowClasses =
    [
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    ];

    private static readonly HashSet<string> IgnoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm",
        "csrss",
        "winlogon",
        "explorer",
        "aes_lacrima",
        "aes-lacrima"
    };

    public static void ApplyMonitorIsolation(ParsecVirtualDisplayMonitor monitor)
    {
        if (!OperatingSystem.IsWindows() || monitor.Handle == IntPtr.Zero)
            return;

        HideTaskbarsOnMonitor(monitor.Handle);
    }

    public static void PrepareForGameLaunch(ParsecVirtualDisplayMonitor monitor, uint? protectedProcessId = null)
    {
        if (!OperatingSystem.IsWindows() || monitor.Handle == IntPtr.Zero)
            return;

        ApplyMonitorIsolation(monitor);
        TerminateForeignProcessesOnMonitor(monitor, protectedProcessId);
    }

    public static bool HasForeignWindowsOnMonitor(ParsecVirtualDisplayMonitor monitor, uint? protectedProcessId = null)
    {
        if (!OperatingSystem.IsWindows() || monitor.Handle == IntPtr.Zero)
            return false;

        var found = false;
        var aesPid = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidateForeignWindow(hwnd, monitor.Handle, aesPid, protectedProcessId))
                return true;

            found = true;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    public static void TerminateForeignProcessesOnMonitor(ParsecVirtualDisplayMonitor monitor, uint? protectedProcessId = null)
    {
        if (!OperatingSystem.IsWindows() || monitor.Handle == IntPtr.Zero)
            return;

        var aesPid = (uint)Process.GetCurrentProcess().Id;
        var processIds = new HashSet<uint>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidateForeignWindow(hwnd, monitor.Handle, aesPid, protectedProcessId))
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid > 0)
                processIds.Add(pid);
            return true;
        }, IntPtr.Zero);

        foreach (var pid in processIds)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                Log.Info($"Terminating orphaned emulator process pid={pid} ({process.ProcessName}) on Parsec virtual display.");
                process.Kill(true);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to terminate process pid={pid} on Parsec virtual display.", ex);
            }
        }
    }

    private static bool IsCandidateForeignWindow(IntPtr hwnd, IntPtr monitorHandle, uint aesPid, uint? protectedProcessId)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
            return false;

        if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) != monitorHandle)
            return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == aesPid || pid == protectedProcessId)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (IgnoredProcessNames.Contains(process.ProcessName))
                return false;
        }
        catch
        {
            return false;
        }

        if (IsTaskbarWindow(hwnd))
            return false;

        return GetWindowRect(hwnd, out var rect) && rect.Width > 64 && rect.Height > 64;
    }

    private static void HideTaskbarsOnMonitor(IntPtr monitorHandle)
    {
        EnumWindows((hwnd, _) =>
        {
            if (!IsTaskbarWindow(hwnd))
                return true;

            if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) == monitorHandle)
                ShowWindow(hwnd, SwHide);

            return true;
        }, IntPtr.Zero);
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        var className = GetClassName(hwnd);
        return TaskbarWindowClasses.Any(c => string.Equals(className, c, StringComparison.Ordinal));
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        _ = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer).TrimEnd('\0');
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

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}
