using AES_Controls.Helpers;
using AES_Core.Logging;
using log4net;
using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows.VirtualDisplay;

/// <summary>
/// Creates and tears down a dedicated virtual monitor for emulator capture on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVirtualDisplaySession : IAsyncDisposable, IDisposable
{
    private static readonly ILog Log = LogHelper.For<WindowsVirtualDisplaySession>();
    private static readonly TimeSpan DisplaySettleDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReloadSpacing = TimeSpan.FromMilliseconds(250);

    private readonly VirtualDisplayDriverManager _driverManager;
    private readonly int _baselineDisplayCount;
    private bool _disposed;

    public WindowsVirtualDisplayMonitor? ActiveMonitor { get; private set; }

    public bool IsActive => ActiveMonitor != null && !_disposed;

    private WindowsVirtualDisplaySession(
        VirtualDisplayDriverManager driverManager,
        int baselineDisplayCount,
        WindowsVirtualDisplayMonitor activeMonitor)
    {
        _driverManager = driverManager;
        _baselineDisplayCount = baselineDisplayCount;
        ActiveMonitor = activeMonitor;
    }

    public static async Task<WindowsVirtualDisplaySession> StartAsync(
        VirtualDisplayDriverManager driverManager,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Virtual display sessions are only supported on Windows.");

        if (!await driverManager.PingAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(VirtualDisplayDriverManager.CaptureRequiredUserMessage);

        var baselineCount = VirtualDisplayDriverManager.TryReadConfiguredDisplayCount();
        var existingMonitors = WindowsVirtualDisplayMonitorHelper.EnumerateVirtualMonitors();
        if (existingMonitors.Count > 0)
        {
            var reusedMonitor = existingMonitors[^1];
            Log.Info(
                $"Reusing existing virtual display '{reusedMonitor.DeviceName}' " +
                $"{reusedMonitor.Width}x{reusedMonitor.Height} at {reusedMonitor.Left},{reusedMonitor.Top}.");
            return new WindowsVirtualDisplaySession(driverManager, baselineCount, reusedMonitor);
        }

        var before = existingMonitors;
        var targetCount = Math.Max(baselineCount, before.Count) + 1;

        if (!await SetDisplayCountWithSpacingAsync(driverManager, targetCount, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to activate a virtual display for capture.");

        await Task.Delay(DisplaySettleDelay, cancellationToken).ConfigureAwait(false);

        var monitorsAfter = WindowsVirtualDisplayMonitorHelper.EnumerateVirtualMonitors();
        var monitor = WindowsVirtualDisplayMonitorHelper.TryGetNewestVirtualMonitor(before);
        if (monitor == null && monitorsAfter.Count > 0)
            monitor = monitorsAfter[^1];

        if (monitor == null)
            throw new InvalidOperationException("Virtual display monitor did not appear after driver reload.");

        var activeMonitor = monitor.Value;

        Log.Info(
            $"Started virtual display session: device='{activeMonitor.DeviceName}', " +
            $"bounds={activeMonitor.Left},{activeMonitor.Top} {activeMonitor.Width}x{activeMonitor.Height}, count={targetCount}.");

        return new WindowsVirtualDisplaySession(driverManager, baselineCount, activeMonitor);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await SetDisplayCountWithSpacingAsync(_driverManager, _baselineDisplayCount, CancellationToken.None)
                .ConfigureAwait(false);
            Log.Info($"Restored virtual display count to {_baselineDisplayCount}.");
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to restore virtual display count during session teardown.", ex);
        }
        finally
        {
            ActiveMonitor = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _ = DisposeAsync();
    }

    private static async Task<bool> SetDisplayCountWithSpacingAsync(
        VirtualDisplayDriverManager driverManager,
        int count,
        CancellationToken cancellationToken)
    {
        if (ReloadSpacing > TimeSpan.Zero)
            await Task.Delay(ReloadSpacing, cancellationToken).ConfigureAwait(false);
        return await driverManager.SetDisplayCountAsync(count, cancellationToken).ConfigureAwait(false);
    }
}
