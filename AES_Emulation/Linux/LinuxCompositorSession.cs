using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Tracks a running gamescope compositor session for the active emulator launch.
/// </summary>
public sealed class LinuxCompositorSession : IDisposable
{
    private static readonly ILog SLog = LogHelper.For<LinuxCompositorSession>();

    private Process? _compositorProcess;
    private bool _disposed;

    public Process CompositorProcess => _compositorProcess
        ?? throw new InvalidOperationException("The gamescope compositor session is not active.");

    public string? WaylandSocketName { get; private set; }

    public bool IsActive
    {
        get
        {
            if (_compositorProcess == null)
                return false;

            try
            {
                return !_compositorProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<LinuxCompositorSession> StartAsync(
        ProcessStartInfo emulatorStartInfo,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("gamescope sessions are only supported on Linux.");

        var process = await LinuxCompositorLaunchHelper.LaunchInCompositorAsync(
            emulatorStartInfo,
            width,
            height,
            cancellationToken).ConfigureAwait(false);

        return new LinuxCompositorSession { _compositorProcess = process };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopGracefully();
    }

    public void StopGracefully()
    {
        if (_compositorProcess == null)
            return;

        var process = _compositorProcess;
        var pid = 0;
        try
        {
            pid = process.Id;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to read gamescope pid before kill.", ex);
        }

        _compositorProcess = null;

        try
        {
            if (pid > 0)
                LinuxCompositorKillHelper.ForceKillProcessTree(pid);
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to force-kill gamescope compositor.", ex);
        }
        finally
        {
            try { process.Dispose(); } catch { /* already disposed */ }
        }
    }
}
