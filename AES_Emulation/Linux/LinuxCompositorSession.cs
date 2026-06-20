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
    private LinuxCompositorProcessOutputPump? _outputPump;
    private bool _disposed;

    public Process CompositorProcess => _compositorProcess
        ?? throw new InvalidOperationException("The gamescope compositor session is not active.");

    public int PipeWireNodeId => _outputPump?.PipeWireNodeId ?? 0;

    public string? RecentCompositorOutput => _outputPump?.GetRecentDiagnostics();

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

        var (process, outputPump) = await LinuxCompositorLaunchHelper.LaunchInCompositorAsync(
            emulatorStartInfo,
            width,
            height,
            cancellationToken).ConfigureAwait(false);

        return new LinuxCompositorSession
        {
            _compositorProcess = process,
            _outputPump = outputPump,
        };
    }

    public async Task<Process> LaunchEmulatorAsync(
        ProcessStartInfo emulatorStartInfo,
        CancellationToken cancellationToken = default)
    {
        if (_compositorProcess == null)
            throw new InvalidOperationException("The gamescope compositor session is not active.");

        var process = await LinuxCompositorLaunchHelper.LaunchEmulatorInExistingCompositorAsync(
            _compositorProcess,
            emulatorStartInfo,
            cancellationToken).ConfigureAwait(false);

        _compositorProcess = process;
        return process;
    }

    public void Release()
    {
        if (_compositorProcess == null)
            return;

        var process = _compositorProcess;
        _compositorProcess = null;

        try { _outputPump?.Dispose(); } catch { /* ignored */ }
        _outputPump = null;

        try { process.Dispose(); } catch { /* already disposed */ }
    }

    public void Dispose() => Dispose(waitForProcessExit: false, scheduleKill: false);

    public void Dispose(bool waitForProcessExit, bool scheduleKill = false)
    {
        if (_disposed)
            return;

        _disposed = true;
        StopGracefully(waitForProcessExit, scheduleKill);
    }

    public void StopGracefully(bool waitForProcessExit = false, bool scheduleKill = false)
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

        try { _outputPump?.Dispose(); } catch { /* ignored */ }
        _outputPump = null;

        var alreadyExited = false;
        try
        {
            alreadyExited = process.HasExited;
        }
        catch
        {
            alreadyExited = true;
        }

        try { process.Dispose(); } catch { /* already disposed */ }

        if (pid <= 0 || alreadyExited)
            return;

        if (scheduleKill)
            LinuxCompositorKillHelper.ScheduleKillProcessTree(pid);
        else
            LinuxCompositorKillHelper.ForceKillProcessTree(pid, waitForProcessExit);
    }
}
