using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Windows.VirtualDisplay;

[SupportedOSPlatform("windows")]
public readonly record struct WindowsVirtualDisplayMonitor(
    IntPtr Handle,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>
/// Enumerates Windows displays and identifies monitors provided by Virtual Display Driver.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsVirtualDisplayMonitorHelper
{
    private const int MonitorInfoF = 0x00000010;

    public static bool IsVirtualDisplayDeviceName(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        deviceName.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<WindowsVirtualDisplayMonitor> EnumerateVirtualMonitors()
    {
        var monitors = new List<WindowsVirtualDisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);
        return monitors;

        bool MonitorCallback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info))
                return true;

            if (!IsVirtualDisplayDeviceName(info.DeviceName))
                return true;

            monitors.Add(new WindowsVirtualDisplayMonitor(
                monitor,
                info.DeviceName,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                (info.Flags & 1) != 0));

            return true;
        }
    }

    public static WindowsVirtualDisplayMonitor? TryGetNewestVirtualMonitor(IReadOnlyList<WindowsVirtualDisplayMonitor>? before)
    {
        var current = EnumerateVirtualMonitors();
        if (current.Count == 0)
            return null;

        if (before == null || before.Count == 0)
            return current[^1];

        foreach (var monitor in current)
        {
            if (before.All(existing => existing.Handle != monitor.Handle))
                return monitor;
        }

        return current[^1];
    }

    public static int? TryGetDisplayMonitorIndex(IntPtr monitorHandle)
    {
        if (monitorHandle == IntPtr.Zero)
            return null;

        var search = new MonitorIndexSearch(monitorHandle);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, search.Callback, IntPtr.Zero);
        return search.FoundIndex;
    }

    private sealed class MonitorIndexSearch(IntPtr target)
    {
        private int _index;
        public int? FoundIndex { get; private set; }

        public bool Callback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            if (monitor == target)
                FoundIndex = _index;
            _index++;
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);


    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
