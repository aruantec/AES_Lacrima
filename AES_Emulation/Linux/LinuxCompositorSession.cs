using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private const int SigTerm = 15;

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
        _compositorProcess = null;

        try
        {
            if (!process.HasExited)
            {
                TrySendSignal(process.Id, SigTerm);
                if (!process.WaitForExit(5000))
                {
                    SLog.Info($"gamescope pid={process.Id} did not exit after SIGTERM; sending SIGKILL.");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to stop gamescope compositor gracefully.", ex);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception killEx)
            {
                SLog.Debug("Failed to force-kill gamescope compositor.", killEx);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TrySendSignal(int pid, int signal)
    {
        try
        {
            if (kill(pid, signal) != 0)
                SLog.Debug($"kill({pid}, {signal}) failed with errno={Marshal.GetLastWin32Error()}.");
        }
        catch (Exception ex)
        {
            SLog.Debug($"Failed to send signal {signal} to pid={pid}.", ex);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
