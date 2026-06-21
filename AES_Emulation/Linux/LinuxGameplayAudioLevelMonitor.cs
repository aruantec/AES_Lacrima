using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Emulation.Services;

namespace AES_Emulation.Linux;

/// <summary>
/// Lightweight live level meter — FFmpeg Pulse first, then single-target pw-record.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGameplayAudioLevelMonitor : IDisposable
{
    private static readonly Regex PeakRegex = new(
        @"lavfi\.astats\.Overall\.Peak_level=(-?inf|-?\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private LinuxGameplayAudioCapture? _capture;
    private Process? _ffmpegProcess;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private double _level;
    private volatile bool _hasAudibleSignal;
    private int _stopGeneration;

    public double Level => _level;

    public bool HasAudibleSignal => _hasAudibleSignal;

    public event Action<double>? LevelChanged;

    public event Action<bool>? AudibleSignalChanged;

    public bool TryStart(
        GameplayRecordingAudioSource source,
        int processId,
        string? deviceId,
        int compositorLaunchPid = 0)
    {
        Stop();

        if (!OperatingSystem.IsLinux() || source == GameplayRecordingAudioSource.None)
            return false;

        var pulseInput = LinuxFfmpegPulseAudio.ResolvePulseMonitor(source, processId, compositorLaunchPid, deviceId);
        if (TryStartFfmpegPulseMeter(pulseInput, compositorLaunchPid))
            return true;

        var pwTarget = LinuxFfmpegPulseAudio.ResolvePwRecordTarget(
            source,
            processId,
            compositorLaunchPid,
            deviceId);

        _capture = new LinuxGameplayAudioCapture();
        if (_capture.TryStartDirectTarget(pwTarget, compositorLaunchPid))
        {
            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => CaptureMonitorLoop(_cts.Token));
            return true;
        }

        _capture.Dispose();
        _capture = null;
        return false;
    }

    public bool TryStartOutputDeviceMonitor(string? deviceId, int compositorLaunchPid = 0, int emulatorProcessId = 0)
        => TryStart(
            GameplayRecordingAudioSource.OutputDevice,
            emulatorProcessId,
            deviceId,
            compositorLaunchPid);

    private bool TryStartFfmpegPulseMeter(string pulseInput, int compositorLaunchPid)
    {
        var ffmpeg = FFmpegLocator.FindFFmpegPath();
        if (ffmpeg == null || string.IsNullOrWhiteSpace(pulseInput))
            return false;

        var quoted = pulseInput.Contains(' ') ? $"\"{pulseInput}\"" : pulseInput;
        var args = string.Join(' ',
            "-hide_banner -nostats -loglevel info",
            $"-f pulse -i {quoted}",
            "-af astats=metadata=1:reset=1:length=0.05,ametadata=print:key=lavfi.astats.Overall.Peak_level",
            "-f null -");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };
        LinuxAudioEnvironmentHelper.Apply(startInfo, includeSdlDriver: false, compositorLaunchPid);

        try
        {
            _ffmpegProcess = Process.Start(startInfo);
            if (_ffmpegProcess == null)
                return false;

            _cts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => FfmpegMeterLoop(_ffmpegProcess, _cts.Token));
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    private void FfmpegMeterLoop(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = process.StandardError.ReadLine();
                if (line == null)
                    break;

                var match = PeakRegex.Match(line);
                if (!match.Success)
                    continue;

                if (!TryParseDecibels(match.Groups[1].Value, out var db))
                    continue;

                UpdateLevelFromDecibels(db);
            }
        }
        catch
        {
        }
    }

    private void CaptureMonitorLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = 0;
            try
            {
                read = _capture?.Read(buffer) ?? 0;
            }
            catch
            {
                break;
            }

            if (read < 2)
            {
                ApplyMeterDecay();
                Thread.Sleep(16);
                continue;
            }

            var peak = LinuxGameplayAudioCapture.ComputePeakAmplitude(buffer, read);
            UpdateLevelFromPeak(peak);
        }
    }

    private static bool TryParseDecibels(string raw, out double db)
    {
        if (raw.Equals("inf", StringComparison.OrdinalIgnoreCase))
        {
            db = 0;
            return true;
        }

        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase))
        {
            db = double.NegativeInfinity;
            return true;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out db);
    }

    private static double MapPeakToMeter(int peak)
    {
        if (peak <= 16)
            return 0;

        var normalized = peak / 32768.0;
        var db = 20.0 * Math.Log10(Math.Max(normalized, 1e-6));
        return Math.Clamp((db + 55.0) / 55.0, 0.0, 1.0);
    }

    private static double MapDecibelsToMeter(double db)
    {
        if (double.IsNegativeInfinity(db) || db <= -65.0)
            return 0;

        return Math.Clamp((db + 55.0) / 55.0, 0.0, 1.0);
    }

    private void UpdateLevelFromPeak(int peak)
    {
        SetMeterLevel(MapPeakToMeter(peak));

        var audible = peak >= 128;
        if (audible != _hasAudibleSignal)
        {
            _hasAudibleSignal = audible;
            AudibleSignalChanged?.Invoke(audible);
        }

        LevelChanged?.Invoke(_level);
    }

    private void UpdateLevelFromDecibels(double db)
    {
        SetMeterLevel(MapDecibelsToMeter(db));

        var audible = !double.IsNegativeInfinity(db) && db > -48.0;
        if (audible != _hasAudibleSignal)
        {
            _hasAudibleSignal = audible;
            AudibleSignalChanged?.Invoke(audible);
        }

        LevelChanged?.Invoke(_level);
    }

    private void ApplyMeterDecay()
    {
        if (_level <= 0.001)
            return;

        _level *= 0.86;
        LevelChanged?.Invoke(_level);
    }

    private void SetMeterLevel(double target)
    {
        target = Math.Clamp(target, 0.0, 1.0);
        _level = target >= _level
            ? target
            : Math.Max(target, _level * 0.8);
    }

    public void Stop() => StopAndWait(asyncCleanup: true);

    public void StopAndWait(bool asyncCleanup = false)
    {
        var generation = Interlocked.Increment(ref _stopGeneration);

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        var process = Interlocked.Exchange(ref _ffmpegProcess, null);
        var capture = Interlocked.Exchange(ref _capture, null);
        var monitorTask = Interlocked.Exchange(ref _monitorTask, null);
        var cts = Interlocked.Exchange(ref _cts, null);

        _level = 0;
        _hasAudibleSignal = false;

        if (asyncCleanup)
            _ = Task.Run(() => CleanupAsync(process, capture, monitorTask, cts, generation));
        else
            CleanupAsync(process, capture, monitorTask, cts, generation);
    }

    private void CleanupAsync(
        Process? process,
        LinuxGameplayAudioCapture? capture,
        Task? monitorTask,
        CancellationTokenSource? cts,
        int generation)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            monitorTask?.Wait(300);
        }
        catch
        {
        }

        try { process?.Dispose(); } catch { }
        try { capture?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }

        if (generation != _stopGeneration)
            return;
    }

    public void Dispose() => StopAndWait(asyncCleanup: false);
}
