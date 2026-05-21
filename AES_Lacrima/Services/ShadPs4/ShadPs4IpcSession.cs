using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.ShadPs4;

public sealed class ShadPs4IpcSession : IDisposable
{
    private static readonly ILog Log = LogHelper.For<ShadPs4IpcSession>();

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly object _writeLock = new();
    private readonly object _transcriptLock = new();
    private readonly CancellationTokenSource _readerCts = new();
    private bool _disposed;
    private bool _runSignaled;

    public event Action? CapabilitiesChanged;

    public int ProcessId
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool IsMemoryPatchSupported { get; private set; }

    public bool IpcHandshakeCompleted { get; private set; }

    public bool IsAttached
    {
        get
        {
            if (_disposed)
                return false;

            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    private ShadPs4IpcSession(Process process, StreamWriter stdin)
    {
        _process = process;
        _stdin = stdin;
    }

    public static ShadPs4IpcSession? TryAttach(Process process, string? transcriptPath = null)
    {
        if (process == null)
            return null;

        try
        {
            if (process.HasExited)
                return null;
        }
        catch
        {
            return null;
        }

        try
        {
            if (process.StartInfo is not { RedirectStandardInput: true, RedirectStandardError: true, RedirectStandardOutput: true })
            {
                Log.Debug($"shadPS4 IPC attach skipped for pid={process.Id}: process was not started with redirected streams.");
                return null;
            }

            var stdin = process.StandardInput;
            stdin.AutoFlush = true;
            stdin.NewLine = "\n";

            var session = new ShadPs4IpcSession(process, stdin);
            session.StartStreamReader(process.StandardError, transcriptPath, isStdErr: true);
            session.StartStreamReader(process.StandardOutput, transcriptPath, isStdErr: false);
            session.StartRunFallback(transcriptPath);
            Log.Info($"shadPS4 IPC session attached to pid={process.Id}.");
            return session;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to attach shadPS4 IPC session.", ex);
            return null;
        }
    }

    public void SendMemoryPatch(
        string modName,
        string offset,
        string value,
        bool treatOffsetAsAbsolute,
        bool littleEndian = false)
    {
        if (!IsAttached)
            throw new InvalidOperationException("shadPS4 is not running with IPC enabled.");

        if (!IsMemoryPatchSupported)
            throw new InvalidOperationException("shadPS4 has not advertised memory patch support yet.");

        lock (_writeLock)
        {
            WriteLine("PATCH_MEMORY");
            WriteLine(modName);
            WriteLine(offset);
            WriteLine(value);
            WriteLine(string.Empty);
            WriteLine(string.Empty);
            WriteLine(treatOffsetAsAbsolute ? "1" : "0");
            WriteLine(littleEndian ? "1" : "0");
            WriteLine("0");
            WriteLine("0");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _readerCts.Cancel();

        try
        {
            _stdin.Dispose();
        }
        catch
        {
        }
    }

    private void WriteLine(string text)
    {
        _stdin.WriteLine(text);
        try
        {
            _stdin.Flush();
        }
        catch
        {
        }
    }

    private void StartStreamReader(StreamReader reader, string? transcriptPath, bool isStdErr)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_readerCts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_readerCts.Token).ConfigureAwait(false);
                    if (line == null)
                        break;

                    AppendTranscriptLine(transcriptPath, line);
                    ProcessOutputLine(line, isStdErr);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Debug($"shadPS4 IPC {(isStdErr ? "stderr" : "stdout")} reader stopped.", ex);
            }
        }, _readerCts.Token);
    }

    private void StartRunFallback(string? transcriptPath)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(12), _readerCts.Token).ConfigureAwait(false);
                if (!_readerCts.IsCancellationRequested && !_runSignaled)
                {
                    Log.Warn("shadPS4 IPC handshake timed out; sending RUN/START as fallback.");
                    SignalRunAndStart();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, _readerCts.Token);
    }

    private void AppendTranscriptLine(string? transcriptPath, string line)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath))
            return;

        try
        {
            lock (_transcriptLock)
            {
                File.AppendAllText(transcriptPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private void ProcessOutputLine(string line, bool isStdErr)
    {
        var trimmed = line.Trim();
        if (trimmed.Contains("Failed to acquire run semaphore", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error("shadPS4 IPC run semaphore is already held (close other shadPS4 instances and retry).");
            return;
        }

        if (!isStdErr || !trimmed.StartsWith(';'))
            return;

        var payload = trimmed[1..].Trim();
        switch (payload)
        {
            case "ENABLE_MEMORY_PATCH":
                if (!IsMemoryPatchSupported)
                {
                    IsMemoryPatchSupported = true;
                    Log.Info("shadPS4 IPC: memory patch support enabled.");
                    NotifyCapabilitiesChanged();
                }
                break;
            case "#IPC_END":
                IpcHandshakeCompleted = true;
                SignalRunAndStart();
                break;
        }
    }

    private void SignalRunAndStart()
    {
        lock (_writeLock)
        {
            if (_runSignaled)
                return;

            _runSignaled = true;
            Log.Info("shadPS4 IPC handshake complete; sending RUN and START.");
            WriteLine("RUN");
            WriteLine("START");
            NotifyCapabilitiesChanged();
        }
    }

    private void NotifyCapabilitiesChanged() =>
        CapabilitiesChanged?.Invoke();
}
