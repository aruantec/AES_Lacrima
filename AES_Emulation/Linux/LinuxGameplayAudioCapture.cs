using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using AES_Emulation.Services;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Captures PCM audio for gameplay recording via PipeWire/PulseAudio CLI tools.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGameplayAudioCapture : IDisposable
{
    private static readonly ILog Log = LogHelper.For<LinuxGameplayAudioCapture>();

    private Process? _captureProcess;
    private Stream? _stdout;
    private Task? _stderrDrainTask;
    private readonly MemoryStream _readAheadBuffer = new();
    private readonly object _readAheadLock = new();
    private bool _isCapturing;
    private int _compositorLaunchPid;

    public static bool IsSupported => OperatingSystem.IsLinux();

    public static bool CanCaptureAudio()
    {
        if (!IsSupported || !HasUsableAudioSession())
            return false;

        if (LinuxAudioEnvironmentHelper.ResolvePwRecordExecutable() != null
            || LinuxAudioEnvironmentHelper.ResolveParecExecutable() != null)
        {
            return true;
        }

        return CanQueryPulse();
    }

    /// <summary>
    /// Legacy name retained for callers; prefer <see cref="CanCaptureAudio"/>.
    /// </summary>
    public static bool CanCapturePulse() => CanCaptureAudio();

    public static void ApplyAudioEnvironment(ProcessStartInfo startInfo) => LinuxAudioEnvironmentHelper.Apply(startInfo);

    public int SampleRate { get; private set; } = 48_000;
    public int Channels { get; private set; } = 2;
    public int BitsPerSample { get; private set; } = 16;
    public int BytesPerSample => Channels * BitsPerSample / 8;

    public static int ComputePeakAmplitude(ReadOnlySpan<byte> buffer, int length)
    {
        if (length < 2)
            return 0;

        var peak = 0;
        var sampleCount = length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(buffer[(i * 2)..]));
            if (sample > peak)
                peak = sample;
        }

        return peak;
    }

    public bool TryStart(
        GameplayRecordingAudioSource source,
        int processId,
        string? deviceId,
        int compositorPid = 0)
    {
        if (!IsSupported || source == GameplayRecordingAudioSource.None)
            return false;

        Stop();

        _compositorLaunchPid = compositorPid;
        var targets = LinuxGameplayAudioTargetResolver.ResolveTargets(source, processId, compositorPid, deviceId);
        if (targets.Count == 0)
        {
            Log.Warn("Linux gameplay audio capture: no PipeWire targets were resolved.");
            return false;
        }

        if (source == GameplayRecordingAudioSource.OutputDevice && !string.IsNullOrWhiteSpace(deviceId))
        {
            foreach (var target in targets)
            {
                if (TryStartTarget(target, out _, validationMs: 250, requireSignal: false))
                    return true;

                Stop();
            }

            Log.Warn($"Linux gameplay audio capture: could not open selected output device '{deviceId}'.");
            return false;
        }

        LinuxGameplayAudioTargetResolver.RecordTarget? bestTarget = null;
        var bestPeak = -1;

        foreach (var target in targets)
        {
            if (!TryStartTarget(target, out var peak))
                continue;

            if (peak > bestPeak)
            {
                bestPeak = peak;
                bestTarget = target;
            }

            Stop();
        }

        if (bestTarget == null)
        {
            Log.Warn("Linux gameplay audio capture: all targets were silent or unavailable.");
            return false;
        }

        if (!TryStartTarget(bestTarget.Value, out _))
            return false;

        Log.Info($"Linux gameplay audio capture selected '{bestTarget.Value.Target}' (peak={bestPeak}).");
        return true;
    }

    /// <summary>
    /// Starts capture on a single PipeWire target without probing other sources (for the level meter).
    /// </summary>
    public bool TryStartDirectTarget(string target, int compositorPid = 0)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(target))
            return false;

        Stop();
        _compositorLaunchPid = compositorPid;

        var recordTarget = new LinuxGameplayAudioTargetResolver.RecordTarget(target, target, 100);
        return TryStartTarget(recordTarget, out _, validationMs: 100, requireSignal: false);
    }

    public int Read(byte[] buffer)
    {
        if (!_isCapturing || buffer.Length == 0)
            return 0;

        var totalRead = 0;
        lock (_readAheadLock)
        {
            if (_readAheadBuffer.Length > 0)
            {
                _readAheadBuffer.Position = 0;
                totalRead = _readAheadBuffer.Read(buffer, 0, buffer.Length);
                if (totalRead >= buffer.Length)
                {
                    CompactReadAheadBuffer(totalRead);
                    return totalRead;
                }

                CompactReadAheadBuffer(totalRead);
            }
        }

        if (_stdout == null)
            return totalRead;

        try
        {
            var read = _stdout.Read(buffer, totalRead, buffer.Length - totalRead);
            return read > 0 ? totalRead + read : totalRead;
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture read failed.", ex);
            return totalRead;
        }
    }

    public void Stop()
    {
        _isCapturing = false;

        try
        {
            _stdout?.Dispose();
        }
        catch
        {
            // ignored
        }

        _stdout = null;

        if (_captureProcess == null)
            return;

        try
        {
            if (!_captureProcess.HasExited)
                _captureProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture failed to stop capture process.", ex);
        }
        finally
        {
            try { _captureProcess.Dispose(); } catch { /* ignored */ }
            _captureProcess = null;
        }

        try
        {
            _stderrDrainTask?.Wait(500);
        }
        catch
        {
            // ignored
        }

        _stderrDrainTask = null;
        lock (_readAheadLock)
        {
            _readAheadBuffer.SetLength(0);
            _readAheadBuffer.Position = 0;
        }
    }

    public void Dispose() => Stop();

    internal static IReadOnlyList<int> CollectAudioCandidateProcessIds(int primaryPid, int compositorPid)
    {
        var ordered = new List<int>();
        void Add(int pid)
        {
            if (pid > 0 && !ordered.Contains(pid))
                ordered.Add(pid);
        }

        Add(primaryPid);

        if (compositorPid <= 0)
            return ordered;

        var compositorRoot = LinuxCompositorProcessHelper.ResolveCompositorRootPid(compositorPid);
        var tree = new HashSet<int>();
        LinuxCompositorProcessHelper.CollectCompositorTreePids(compositorRoot, tree);

        foreach (var pid in tree.OrderByDescending(static pid => pid))
            Add(pid);

        return ordered;
    }

    private bool TryStartTarget(
        LinuxGameplayAudioTargetResolver.RecordTarget target,
        out int peakAmplitude,
        int validationMs = 750,
        bool requireSignal = true)
    {
        peakAmplitude = 0;
        var isMonitor = target.Target.Contains(".monitor", StringComparison.OrdinalIgnoreCase)
            || target.Target.StartsWith('@');

        if (isMonitor && TryStartParec(target.Target) &&
            ValidateCaptureReceivingData(out peakAmplitude, validationMs, requireSignal))
        {
            Log.Info($"Linux gameplay audio capture started via parec: {target.Description}.");
            return true;
        }

        Stop();

        if (TryStartPwRecord(target.Target) &&
            ValidateCaptureReceivingData(out peakAmplitude, validationMs, requireSignal))
        {
            Log.Debug(
                $"Linux gameplay audio capture via pw-record target='{target.Target}' " +
                $"(peak={peakAmplitude}, {target.Description}).");
            return true;
        }

        Stop();
        return false;
    }

    private bool ValidateCaptureReceivingData(out int peakAmplitude, int validationMs = 750, bool requireSignal = true)
    {
        peakAmplitude = 0;
        if (_stdout == null)
            return false;

        var scratch = new byte[4096];
        var deadline = Environment.TickCount64 + validationMs;
        var totalBytes = 0;
        var minBytes = requireSignal ? 2048 : 256;

        while (Environment.TickCount64 < deadline)
        {
            int read;
            try
            {
                read = _stdout.Read(scratch, 0, scratch.Length);
            }
            catch
            {
                break;
            }

            if (read <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            peakAmplitude = Math.Max(peakAmplitude, ComputePeakAmplitude(scratch, read));

            lock (_readAheadLock)
            {
                _readAheadBuffer.Position = _readAheadBuffer.Length;
                _readAheadBuffer.Write(scratch, 0, read);
            }

            totalBytes += read;
            if (totalBytes >= 4096 && (!requireSignal || peakAmplitude >= 64))
                return true;
        }

        return totalBytes >= minBytes;
    }

    private void CompactReadAheadBuffer(int consumed)
    {
        if (consumed <= 0)
            return;

        var remaining = (int)(_readAheadBuffer.Length - consumed);
        if (remaining <= 0)
        {
            _readAheadBuffer.SetLength(0);
            _readAheadBuffer.Position = 0;
            return;
        }

        var temp = new byte[remaining];
        _readAheadBuffer.Position = consumed;
        _readAheadBuffer.Read(temp, 0, remaining);
        _readAheadBuffer.SetLength(0);
        _readAheadBuffer.Write(temp, 0, remaining);
        _readAheadBuffer.Position = 0;
    }

    private bool TryStartParec(string monitor)
    {
        var parec = LinuxAudioEnvironmentHelper.ResolveParecExecutable();
        if (parec == null || string.IsNullOrWhiteSpace(monitor))
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = parec,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add($"--device={monitor}");
        startInfo.ArgumentList.Add("--format=s16le");
        startInfo.ArgumentList.Add("--rate=48000");
        startInfo.ArgumentList.Add("--channels=2");
        startInfo.ArgumentList.Add("--latency-msec=50");
        LinuxAudioEnvironmentHelper.Apply(startInfo, includeSdlDriver: false, _compositorLaunchPid);
        return TryStartProcess(startInfo);
    }

    private bool TryStartPwRecord(string target)
    {
        var pwRecord = LinuxAudioEnvironmentHelper.ResolvePwRecordExecutable();
        if (pwRecord == null || string.IsNullOrWhiteSpace(target))
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = pwRecord,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (target.Contains(".monitor", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith('@'))
        {
            // pw-record on a sink name already captures loopback; Monitor category can break some nodes.
        }

        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("s16");
        startInfo.ArgumentList.Add("--rate");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("--channels");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("--raw");
        startInfo.ArgumentList.Add("-");
        LinuxAudioEnvironmentHelper.Apply(startInfo, includeSdlDriver: false, _compositorLaunchPid);
        return TryStartProcess(startInfo);
    }

    private bool TryStartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
                return false;

            _stdout = process.StandardOutput.BaseStream;
            _captureProcess = process;
            _isCapturing = true;
            _stderrDrainTask = Task.Run(() => DrainStreamAsync(process.StandardError.BaseStream));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("LinuxGameplayAudioCapture failed to start.", ex);
            Stop();
            return false;
        }
    }

    private static async Task DrainStreamAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                    break;
            }
        }
        catch
        {
            // ignored
        }
    }

    private static bool HasUsableAudioSession()
    {
        var runtimeDir = LinuxAudioEnvironmentHelper.ResolveRuntimeDir()
            ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir);
    }

    private static bool CanQueryPulse()
    {
        var pactl = LinuxAudioEnvironmentHelper.ResolvePactlExecutable();
        if (pactl == null || !HasUsableAudioSession())
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pactl,
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit(1500);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture pulse availability check failed.", ex);
            return false;
        }
    }
}
