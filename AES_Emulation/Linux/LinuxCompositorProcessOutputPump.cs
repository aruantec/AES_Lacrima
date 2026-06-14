using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Drains gamescope stdout/stderr so redirected pipe buffers cannot block the compositor or emulator.
/// Also captures the PipeWire node id gamescope prints when its headless stream comes up.
/// </summary>
public sealed partial class LinuxCompositorProcessOutputPump : IDisposable
{
    private static readonly ILog SLog = LogHelper.For<LinuxCompositorProcessOutputPump>();

    [GeneratedRegex(@"node\s+ID:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PipeWireNodeIdRegex();

    private readonly CancellationTokenSource _cts = new();
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _disposed;

    public int PipeWireNodeId { get; private set; }

    public static LinuxCompositorProcessOutputPump Start(Process process)
    {
        if (process == null)
            throw new ArgumentNullException(nameof(process));

        var pump = new LinuxCompositorProcessOutputPump();
        pump.Attach(process);
        return pump;
    }

    private void Attach(Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to subscribe to gamescope exit events for output pump.", ex);
        }

        _stdoutTask = PumpStreamAsync(process.StandardOutput, _cts.Token);
        _stderrTask = PumpStreamAsync(process.StandardError, _cts.Token);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }
    }

    private async Task PumpStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                TryCapturePipeWireNodeId(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the compositor exits.
        }
        catch (Exception ex)
        {
            SLog.Debug("gamescope output pump stopped due to read failure.", ex);
        }
    }

    private void TryCapturePipeWireNodeId(string line)
    {
        var match = PipeWireNodeIdRegex().Match(line);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var nodeId) || nodeId <= 0)
            return;

        if (nodeId > PipeWireNodeId)
        {
            PipeWireNodeId = nodeId;
            SLog.Info($"gamescope PipeWire node id observed from compositor log: {nodeId}.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            _cts.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
