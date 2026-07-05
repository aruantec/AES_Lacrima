using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
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

    private const int MaxBufferedLines = 32;

    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<string> _recentStderrLines = new();
    private readonly Queue<string> _recentStdoutLines = new();
    private readonly object _bufferGate = new();
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _disposed;
    private Process? _process;

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
        _process = process;

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to subscribe to gamescope exit events for output pump.", ex);
        }

        _stdoutTask = PumpStreamAsync(process.StandardOutput, _recentStdoutLines, cancellationToken: _cts.Token);
        _stderrTask = PumpStreamAsync(process.StandardError, _recentStderrLines, cancellationToken: _cts.Token);
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

        try
        {
            var exitCode = _process?.ExitCode;
            var diagnostics = GetRecentDiagnostics();
            if (string.IsNullOrWhiteSpace(diagnostics))
            {
                SLog.Warn($"gamescope compositor exited early (exitCode={exitCode?.ToString() ?? "unknown"}).");
            }
            else
            {
                SLog.Warn(
                    $"gamescope compositor exited early (exitCode={exitCode?.ToString() ?? "unknown"}). " +
                    $"Recent output:{Environment.NewLine}{diagnostics}");
            }
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to log gamescope compositor exit diagnostics.", ex);
        }
    }

    public string? GetRecentDiagnostics()
    {
        lock (_bufferGate)
        {
            if (_recentStderrLines.Count == 0 && _recentStdoutLines.Count == 0)
                return null;

            var builder = new StringBuilder();
            if (_recentStderrLines.Count > 0)
            {
                builder.AppendLine("stderr:");
                foreach (var line in _recentStderrLines)
                    builder.AppendLine(line);
            }

            if (_recentStdoutLines.Count > 0)
            {
                builder.AppendLine("stdout:");
                foreach (var line in _recentStdoutLines)
                    builder.AppendLine(line);
            }

            return builder.ToString().TrimEnd();
        }
    }

    private async Task PumpStreamAsync(
        StreamReader reader,
        Queue<string> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                BufferLine(buffer, line);
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

    private void BufferLine(Queue<string> buffer, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_bufferGate)
        {
            buffer.Enqueue(line);
            while (buffer.Count > MaxBufferedLines)
                buffer.Dequeue();
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
