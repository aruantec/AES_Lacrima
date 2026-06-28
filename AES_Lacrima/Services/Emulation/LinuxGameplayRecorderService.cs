using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.DI;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.Linux;
using AES_Emulation.Services;
using AES_Lacrima.ViewModels;
using log4net;
using SkiaSharp;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Linux gameplay recorder: video via stdin; optional audio via pw-record → FFmpeg pipe (Pulse fallback), remuxed on stop.
/// </summary>
[SupportedOSPlatform("linux")]
[AutoRegister]
public partial class LinuxGameplayRecorderService : IGameplayRecorder
{
    private static readonly ILog Log = LogHelper.For<LinuxGameplayRecorderService>();

    private readonly object _frameLock = new();
    private readonly object _processLock = new();
    private byte[]? _latestFrame;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private volatile bool _hasLatestFrame;

    private Process? _ffmpegVideoProcess;
    private Process? _ffmpegAudioProcess;
    private LinuxGameplayAudioCapture? _audioCapture;
    private Stream? _videoStdin;
    private CancellationTokenSource? _writerCts;
    private CancellationTokenSource? _audioPumpCts;
    private Task? _videoWriterTask;
    private Task? _audioPumpTask;

    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private int _targetFps = 30;
    private long _recordingStartTicks;
    private long _frameIntervalTicks;
    private long _videoFramesWritten;

    private string? _activeOutputPath;
    private string? _videoCapturePath;
    private string? _audioCapturePath;
    private volatile bool _isRecording;
    private int _emulatorProcessId;
    private int _compositorLaunchPid;
    private bool _includeAudio;
    private byte[]? _encodeScratch;
    private byte[]? _lastWrittenFrame;
    private bool _hasWrittenFrame;
    private readonly StringBuilder _ffmpegStderr = new();
    private readonly StringBuilder _ffmpegAudioStderr = new();
    private int _recordingSessionId;
    private Task? _finalizeTask;
    private int _encoderStartupScheduled;

    private const int FinalizeWriterWaitMs = 5_000;
    private const int FinalizeFfmpegWaitMs = 15_000;
    private const int FinalizeRemuxWaitMs = 30_000;

    public bool IsRecording => _isRecording;
    public string? ActiveOutputPath => _activeOutputPath;

    public event Action<bool>? RecordingStateChanged;
    public event Action<string>? RecordingFailed;

    public void OnFrameFromCapture(byte[] pixels, int width, int height) => OnFrameReceived(pixels, width, height);

    public bool TryStart(
        string outputDirectory,
        GameplayRecordingContainer container,
        GameplayRecordingVideoCodec codec,
        int fps,
        int videoBitrateKbps,
        int emulatorProcessId,
        int compositorLaunchPid = 0)
    {
        if (_isRecording)
            return false;

        WaitForPendingFinalize();

        _recordingSessionId++;

        if (!OperatingSystem.IsLinux())
        {
            RecordingFailed?.Invoke("Linux gameplay recording is only available on Linux.");
            return false;
        }

        if (FFmpegLocator.FindFFmpegPath() == null)
        {
            RecordingFailed?.Invoke("FFmpeg was not found. Install it from Settings → Components.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            RecordingFailed?.Invoke($"Could not create output folder: {ex.Message}");
            return false;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = $"AES_Recording_{timestamp}{GameplayRecordingFormat.GetFileExtension(container)}";
        var outputPath = Path.Combine(outputDirectory, fileName);

        _emulatorProcessId = emulatorProcessId;
        _compositorLaunchPid = compositorLaunchPid;
        _includeAudio = false;
        _videoCapturePath = null;
        _audioCapturePath = null;
        _activeOutputPath = outputPath;
        _targetFps = Math.Clamp(fps, 15, 120);
        _frameIntervalTicks = Stopwatch.Frequency / _targetFps;
        _recordingStartTicks = Stopwatch.GetTimestamp();
        _videoFramesWritten = 0;
        _hasLatestFrame = false;
        _latestFrame = null;
        _hasWrittenFrame = false;
        _lastWrittenFrame = null;
        Interlocked.Exchange(ref _encoderStartupScheduled, 0);

        _isRecording = true;
        RecordingStateChanged?.Invoke(true);

        Log.Info($"Linux gameplay recording armed. Output will be: {outputPath}");
        return true;
    }

    private void OnFrameReceived(byte[] pixels, int width, int height)
    {
        if (!_isRecording || pixels.Length == 0)
            return;

        lock (_frameLock)
        {
            var required = width * height * 4;
            if (_latestFrame == null || _latestFrame.Length != required)
                _latestFrame = new byte[required];

            if (pixels.Length >= required)
            {
                Buffer.BlockCopy(pixels, 0, _latestFrame, 0, required);
                _latestFrameWidth = width;
                _latestFrameHeight = height;
                _hasLatestFrame = true;
            }
        }

        if (_ffmpegVideoProcess != null)
            return;

        if (Interlocked.CompareExchange(ref _encoderStartupScheduled, 1, 0) != 0)
            return;

        var frameWidth = width;
        var frameHeight = height;
        Task.Run(() =>
        {
            try
            {
                TryEnsureEncoder(frameWidth, frameHeight);
            }
            finally
            {
                Interlocked.Exchange(ref _encoderStartupScheduled, 0);
            }
        });
    }

    private bool TryEnsureEncoder(int width, int height)
    {
        if (!_isRecording)
            return false;

        lock (_processLock)
        {
            if (!_isRecording || _ffmpegVideoProcess != null)
                return true;

            var settings = DiLocator.ResolveViewModel<SettingsViewModel>();
            if (settings == null || string.IsNullOrWhiteSpace(_activeOutputPath))
                return false;

            var fps = Math.Clamp(settings.GameplayRecordingFps, 15, 120);
            _targetFps = fps;
            _frameIntervalTicks = Stopwatch.Frequency / fps;

            _frameWidth = Math.Max(64, width & ~1);
            _frameHeight = Math.Max(64, height & ~1);
            _frameStride = _frameWidth * 4;
            _encodeScratch = new byte[_frameStride * _frameHeight];

            var ffmpegPath = FFmpegLocator.FindFFmpegPath();
            if (ffmpegPath == null)
            {
                FailRecording("FFmpeg was not found.");
                return false;
            }

            var bitrate = Math.Clamp(settings.GameplayRecordingBitrateKbps, 1000, 100_000);
            var probeResult = LinuxFfmpegRecordingPreflight.ResolveRecordingEncoder(
                ffmpegPath,
                settings.GameplayRecordingVideoCodec,
                settings.GameplayRecordingEncoderPreference,
                settings.GameplayRecordingContainer);

            if (!string.IsNullOrWhiteSpace(probeResult.Error))
            {
                FailRecording(probeResult.Error);
                return false;
            }

            var finalOutputPath = _activeOutputPath!;
            if (probeResult.Container != settings.GameplayRecordingContainer)
            {
                var newExt = GameplayRecordingFormat.GetFileExtension(probeResult.Container);
                finalOutputPath = Path.ChangeExtension(finalOutputPath, newExt);
                _activeOutputPath = finalOutputPath;
                Log.Info($"Recording container adjusted to {probeResult.Container} for {probeResult.CodecName}.");
            }

            _includeAudio = settings.GameplayRecordingAudioSource != GameplayRecordingAudioSource.None;
            if (_includeAudio)
            {
                var basePath = Path.Combine(
                    Path.GetDirectoryName(finalOutputPath) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(finalOutputPath));
                _videoCapturePath = basePath + ".video.mkv";
                _audioCapturePath = basePath + ".audio.wav";
            }
            else
            {
                _videoCapturePath = finalOutputPath;
                _audioCapturePath = null;
            }

            if (!StartVideoEncoder(ffmpegPath, settings, fps, bitrate, probeResult))
                return false;

            if (_includeAudio)
            {
                settings.RefreshGameplayRecordingSessionContext?.Invoke();
                var session = settings.GetGameplayRecordingSessionPids();
                if (session.CompositorPid > 0)
                    _compositorLaunchPid = session.CompositorPid;
                if (session.EmulatorPid > 0)
                    _emulatorProcessId = session.EmulatorPid;

                StartAudioEncoder(ffmpegPath, settings);
            }

            return true;
        }
    }

    private bool StartVideoEncoder(
        string ffmpegPath,
        SettingsViewModel settings,
        int fps,
        int bitrate,
        FfmpegRecordingPreflight.PreflightResult probeResult)
    {
        var videoArgs = BuildVideoFfmpegArguments(
            _videoCapturePath!,
            _frameWidth,
            _frameHeight,
            fps,
            _includeAudio ? GameplayRecordingContainer.Mkv : probeResult.Container,
            probeResult.CodecName,
            probeResult.CodecExtra,
            bitrate);

        _ffmpegVideoProcess = StartFfmpegProcess(ffmpegPath, videoArgs, redirectStdin: true, redirectStderr: true);
        if (_ffmpegVideoProcess == null)
        {
            FailRecording("Failed to start FFmpeg video encoder.");
            return false;
        }

        _recordingStartTicks = Stopwatch.GetTimestamp();
        _videoFramesWritten = 0;
        _hasWrittenFrame = false;

        _videoStdin = _ffmpegVideoProcess.StandardInput.BaseStream;
        _writerCts = new CancellationTokenSource();
        _videoWriterTask = Task.Run(() => VideoWriterLoop(_writerCts.Token), CancellationToken.None);

        _ffmpegStderr.Clear();
        _ffmpegVideoProcess.ErrorDataReceived += OnFfmpegVideoErrorData;
        _ffmpegVideoProcess.EnableRaisingEvents = true;
        _ffmpegVideoProcess.Exited += OnFfmpegVideoProcessExited;
        _ffmpegVideoProcess.BeginErrorReadLine();

        Log.Info($"Linux gameplay recording started ({_frameWidth}x{_frameHeight} @ {fps}fps, {probeResult.CodecName}, audio={_includeAudio}): {_activeOutputPath}");
        Log.Info($"FFmpeg video arguments: {videoArgs}");
        return true;
    }

    private void StartAudioEncoder(string ffmpegPath, SettingsViewModel settings)
    {
        // Prefer Pulse — same path as the live level meter when it is moving.
        if (TryStartPulseAudioEncoder(ffmpegPath, settings))
            return;

        if (TryStartPipewireAudioEncoder(ffmpegPath, settings))
            return;

        Log.Warn("Linux gameplay recording: all audio capture paths failed.");
        _includeAudio = false;
        _audioCapturePath = null;
    }

    private bool TryStartPipewireAudioEncoder(string ffmpegPath, SettingsViewModel settings)
    {
        var processId = settings.GameplayRecordingAudioSource switch
        {
            GameplayRecordingAudioSource.Application => settings.GameplayRecordingAudioProcessId,
            GameplayRecordingAudioSource.EmulatorProcess => _emulatorProcessId,
            _ => 0
        };

        var deviceId = settings.GameplayRecordingAudioSource == GameplayRecordingAudioSource.OutputDevice
            ? settings.GameplayRecordingAudioDeviceId
            : null;

        _audioCapture = new LinuxGameplayAudioCapture();
        if (!_audioCapture.TryStart(
                settings.GameplayRecordingAudioSource,
                processId,
                deviceId,
                _compositorLaunchPid))
        {
            _audioCapture.Dispose();
            _audioCapture = null;
            return false;
        }

        var audioArgs = BuildPcmPipeFfmpegAudioArguments(_audioCapturePath!);
        _ffmpegAudioProcess = StartFfmpegProcess(ffmpegPath, audioArgs, redirectStdin: true, redirectStderr: true);
        if (_ffmpegAudioProcess == null)
        {
            FailAudioCapture("Failed to start FFmpeg audio encoder.");
            return false;
        }

        _ffmpegAudioProcess.EnableRaisingEvents = true;
        _ffmpegAudioProcess.Exited += OnFfmpegAudioProcessExited;
        _ffmpegAudioProcess.ErrorDataReceived += OnFfmpegAudioErrorData;
        _ffmpegAudioProcess.BeginErrorReadLine();

        var audioStdin = _ffmpegAudioProcess.StandardInput.BaseStream;
        _audioPumpCts = new CancellationTokenSource();
        _audioPumpTask = Task.Run(() => PumpAudioFromCaptureAsync(_audioCapture, audioStdin, _audioPumpCts.Token));

        if (!WaitForAudioSidecarGrowth(_ffmpegAudioProcess, _audioCapturePath, timeoutMs: 1500))
        {
            Log.Warn("Linux gameplay recording PipeWire pipe did not produce audio data.");
            FailAudioCapture("PipeWire audio capture produced no data.");
            return false;
        }

        Log.Info($"Linux gameplay recording audio via PipeWire → FFmpeg: {audioArgs}");
        return true;
    }

    private void FailAudioCapture(string message)
    {
        Log.Warn(message);
        _audioCapture?.Dispose();
        _audioCapture = null;
        try { _ffmpegAudioProcess?.Kill(entireProcessTree: true); } catch { }
        _ffmpegAudioProcess = null;
        _includeAudio = false;
        _audioCapturePath = null;
    }

    private bool TryStartPulseAudioEncoder(string ffmpegPath, SettingsViewModel settings)
    {
        var processId = settings.GameplayRecordingAudioSource switch
        {
            GameplayRecordingAudioSource.Application => settings.GameplayRecordingAudioProcessId,
            GameplayRecordingAudioSource.EmulatorProcess => _emulatorProcessId,
            _ => 0
        };

        var deviceId = settings.GameplayRecordingAudioSource == GameplayRecordingAudioSource.OutputDevice
            ? settings.GameplayRecordingAudioDeviceId
            : null;

        var pulseMonitor = LinuxFfmpegPulseAudio.ResolvePulseMonitor(
            settings.GameplayRecordingAudioSource,
            processId,
            _compositorLaunchPid,
            deviceId);

        var audioArgs = BuildPulseFfmpegAudioArguments(_audioCapturePath!, pulseMonitor);
        _ffmpegAudioStderr.Clear();
        _ffmpegAudioProcess = StartFfmpegProcess(ffmpegPath, audioArgs, redirectStdin: false, redirectStderr: true);
        if (_ffmpegAudioProcess == null)
            return false;

        if (!WaitForAudioSidecarGrowth(_ffmpegAudioProcess, _audioCapturePath))
        {
            Log.Warn(
                $"Linux gameplay recording Pulse capture did not produce audio data for '{pulseMonitor}'. " +
                ExtractFfmpegAudioErrorMessage());
            try
            {
                if (!_ffmpegAudioProcess.HasExited)
                    _ffmpegAudioProcess.Kill(entireProcessTree: true);
                _ffmpegAudioProcess.WaitForExit(2000);
            }
            catch
            {
            }

            _ffmpegAudioProcess.Dispose();
            _ffmpegAudioProcess = null;
            return false;
        }

        _ffmpegAudioProcess.EnableRaisingEvents = true;
        _ffmpegAudioProcess.Exited += OnFfmpegAudioProcessExited;
        _ffmpegAudioProcess.ErrorDataReceived += OnFfmpegAudioErrorData;
        _ffmpegAudioProcess.BeginErrorReadLine();
        Log.Info($"Linux gameplay recording audio via Pulse: {audioArgs}");
        return true;
    }

    private bool WaitForAudioSidecarGrowth(Process process, string? sidecarPath, int timeoutMs = 1200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (process.HasExited)
                return false;

            if (GetCaptureFileSize(sidecarPath) >= 4096)
                return true;

            Thread.Sleep(50);
        }

        return !process.HasExited && GetCaptureFileSize(sidecarPath) >= 1024;
    }

    private void OnFfmpegAudioErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        lock (_ffmpegAudioStderr)
            _ffmpegAudioStderr.AppendLine(e.Data);
    }

    private string ExtractFfmpegAudioErrorMessage()
    {
        lock (_ffmpegAudioStderr)
        {
            var text = _ffmpegAudioStderr.ToString();
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        }
    }

    private static async Task PumpAudioFromCaptureAsync(
        LinuxGameplayAudioCapture capture,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await Task.Run(() => capture.Read(buffer), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            try { destination.Flush(); } catch { }
            try { destination.Close(); } catch { }
        }
    }

    private void OnFfmpegVideoErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        lock (_ffmpegStderr)
            _ffmpegStderr.AppendLine(e.Data);

        Log.Debug($"ffmpeg: {e.Data}");
    }

    private Process? StartFfmpegProcess(string ffmpegPath, string args, bool redirectStdin, bool redirectStderr)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            RedirectStandardError = redirectStderr,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };
        LinuxAudioEnvironmentHelper.Apply(startInfo, includeSdlDriver: false, _compositorLaunchPid);
        return Process.Start(startInfo);
    }

    internal static string BuildVideoFfmpegArguments(
        string outputPath,
        int width,
        int height,
        int fps,
        GameplayRecordingContainer container,
        string codecName,
        string codecExtra,
        int bitrateKbps)
    {
        return BuildFfmpegArguments(
            outputPath,
            width,
            height,
            fps,
            container,
            codecName,
            codecExtra,
            bitrateKbps,
            pulseInput: null);
    }

    internal static string BuildPcmPipeFfmpegAudioArguments(string outputPath)
    {
        var quotedOut = $"\"{outputPath}\"";
        return string.Join(' ',
            "-hide_banner -loglevel warning -y",
            "-f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i pipe:0",
            "-c:a pcm_s16le",
            "-f wav",
            quotedOut).Trim();
    }

    internal static string BuildPulseFfmpegAudioArguments(string outputPath, string pulseInput)
    {
        var quotedOut = $"\"{outputPath}\"";
        var quotedAudio = pulseInput.Contains(' ') ? $"\"{pulseInput}\"" : pulseInput;
        return string.Join(' ',
            "-hide_banner -loglevel warning -y",
            $"-f pulse -i {quotedAudio}",
            "-c:a pcm_s16le -ar 48000 -ac 2",
            "-flush_packets", "1",
            "-f wav",
            quotedOut).Trim();
    }

    internal static string BuildFfmpegArguments(
        string outputPath,
        int width,
        int height,
        int fps,
        GameplayRecordingContainer container,
        string codecName,
        string codecExtra,
        int bitrateKbps,
        string? pulseInput)
    {
        if (!string.IsNullOrWhiteSpace(pulseInput))
            throw new InvalidOperationException("Use separate video/audio FFmpeg processes on Linux.");

        var quotedOut = $"\"{outputPath}\"";
        var movFlags = GetLinuxMp4MovFlags(container, codecName, forFinalMux: false);
        var vaapiDevice = GetVaapiDeviceArgument(codecName);
        var videoFilter = GetLinuxVideoFilter(codecName);
        var fpsMode = FfmpegHardwareEncoderProbe.UseCfrFpsMode(codecName) ? "-fps_mode cfr" : string.Empty;

        return string.Join(' ',
            "-hide_banner -loglevel warning -y",
            vaapiDevice,
            $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps}",
            "-i pipe:0",
            "-an",
            videoFilter,
            $"-c:v {codecName} {codecExtra}",
            FfmpegHardwareEncoderProbe.GetVideoBitrateArguments(codecName, bitrateKbps),
            fpsMode,
            movFlags,
            quotedOut).Trim();
    }

    internal static string BuildRemuxTempOutputPath(string outputPath)
    {
        var ext = Path.GetExtension(outputPath);
        if (string.IsNullOrEmpty(ext))
            ext = ".mkv";

        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
            directory = Path.GetTempPath();

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".muxing" + ext);
    }

    internal static string BuildRemuxArguments(
        string videoPath,
        string audioPath,
        string outputPath,
        GameplayRecordingContainer container,
        string codecName)
        => string.Join(' ', BuildRemuxArgumentList(videoPath, audioPath, outputPath, container, codecName));

    internal static List<string> BuildRemuxArgumentList(
        string videoPath,
        string audioPath,
        string outputPath,
        GameplayRecordingContainer container,
        string codecName)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-y",
            "-i",
            videoPath,
            "-i",
            audioPath,
            "-map",
            "0:v:0",
            "-map",
            "1:a:0",
            "-c:v",
            "copy",
            "-shortest",
        };

        if (audioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("192k");
            args.Add("-ar");
            args.Add("48000");
        }
        else
        {
            args.Add("-c:a");
            args.Add("copy");
        }

        if (container == GameplayRecordingContainer.Mp4)
        {
            args.Add("-f");
            args.Add("mp4");
        }
        else if (container == GameplayRecordingContainer.Mkv)
        {
            args.Add("-f");
            args.Add("matroska");
        }

        var movFlags = GetLinuxMp4MovFlags(container, codecName, forFinalMux: true);
        if (!string.IsNullOrWhiteSpace(movFlags))
        {
            foreach (var token in movFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                args.Add(token);
        }

        args.Add(outputPath);
        return args;
    }

    private static string GetLinuxMp4MovFlags(GameplayRecordingContainer container, string codecName, bool forFinalMux)
    {
        if (container != GameplayRecordingContainer.Mp4 || FfmpegHardwareEncoderProbe.IsAmdAmfEncoder(codecName))
            return string.Empty;

        return forFinalMux ? "-movflags +faststart" : string.Empty;
    }

    private static string GetLinuxVideoFilter(string codecName)
    {
        if (codecName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase))
            return "-vf format=nv12,hwupload";

        return FfmpegHardwareEncoderProbe.GetInputVideoFilter(codecName);
    }

    private static string GetVaapiDeviceArgument(string codecName)
    {
        if (!codecName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        foreach (var device in new[] { "/dev/dri/renderD128", "/dev/dri/renderD129", "/dev/dri/renderD130" })
        {
            if (File.Exists(device))
                return $"-vaapi_device {device}";
        }

        return string.Empty;
    }

    private void OnFfmpegVideoProcessExited(object? sender, EventArgs e)
    {
        if (!_isRecording)
            return;

        var exitCode = _ffmpegVideoProcess?.ExitCode ?? -1;
        if (exitCode == 0)
            return;

        if (_videoFramesWritten > 0)
        {
            Log.Warn($"Linux gameplay recording: FFmpeg video exited with code {exitCode} after {_videoFramesWritten} frames.");
            return;
        }

        var message = ExtractFfmpegErrorMessage();
        if (string.IsNullOrWhiteSpace(message))
            message = $"FFmpeg video encoder exited with code {exitCode}.";

        FailRecording(message);
    }

    private void OnFfmpegAudioProcessExited(object? sender, EventArgs e)
    {
        if (!_isRecording)
            return;

        var exitCode = _ffmpegAudioProcess?.ExitCode ?? -1;
        if (exitCode == 0)
            return;

        Log.Warn($"Linux gameplay recording audio encoder exited with code {exitCode}; keeping video.");
    }

    private bool TryShutdownEncoderGracefully(bool allowKill)
    {
        try
        {
            _writerCts?.Cancel();
        }
        catch
        {
        }

        WaitBackgroundTask(_videoWriterTask, FinalizeWriterWaitMs);

        ShutdownAudioCaptureBeforeVideoFinalize();

        try
        {
            _videoStdin?.Flush();
            _videoStdin?.Close();
        }
        catch
        {
        }

        _videoStdin = null;

        var videoOk = WaitForFfmpegProcess(_ffmpegVideoProcess, OnFfmpegVideoProcessExited, allowKill);
        var audioOk = !_includeAudio || WaitForFfmpegProcess(_ffmpegAudioProcess, OnFfmpegAudioProcessExited, allowKill);
        return videoOk && audioOk;
    }

    private void ShutdownAudioCaptureBeforeVideoFinalize()
    {
        if (!_includeAudio)
            return;

        try
        {
            _audioPumpCts?.Cancel();
        }
        catch
        {
        }

        _audioCapture?.Stop();
        WaitBackgroundTask(_audioPumpTask, FinalizeWriterWaitMs);

        if (_audioCapture != null)
        {
            try
            {
                _ffmpegAudioProcess?.StandardInput.BaseStream.Flush();
                _ffmpegAudioProcess?.StandardInput.BaseStream.Close();
            }
            catch
            {
            }
        }
        else
        {
            TrySignalFfmpegGracefulStop(_ffmpegAudioProcess);
        }

        _audioPumpCts?.Dispose();
        _audioPumpCts = null;
        _audioCapture?.Dispose();
        _audioCapture = null;
    }

    private static void TrySignalFfmpegGracefulStop(Process? process)
    {
        if (process is null or { HasExited: true })
            return;

        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-TERM {process.Id}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(2000);
        }
        catch
        {
        }

        try
        {
            if (process.WaitForExit(5000))
                return;
        }
        catch
        {
        }

        TryGracefulFfmpegQuit(process);
    }

    private static void TryGracefulFfmpegQuit(Process? process)
    {
        if (process is null or { HasExited: true })
            return;

        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                var stdin = process.StandardInput;
                stdin.Write('q');
                stdin.WriteLine();
                stdin.Flush();
                if (process.WaitForExit(4_000))
                    return;
            }
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch
        {
        }
    }

    private bool WaitForFfmpegProcess(Process? process, EventHandler? exitedHandler, bool allowKill)
    {
        if (process == null)
            return true;

        try
        {
            if (exitedHandler != null)
                process.Exited -= exitedHandler;
        }
        catch
        {
        }

        try
        {
            if (process.WaitForExit(FinalizeFfmpegWaitMs))
                return process.ExitCode == 0;
        }
        catch
        {
        }

        if (!allowKill)
            return false;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch
        {
        }

        return false;
    }

    private static void WaitBackgroundTask(Task? task, int timeoutMs)
    {
        if (task == null)
            return;

        try
        {
            task.Wait(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch
        {
        }
    }

    private void ResetEncoderStateAfterShutdown()
    {
        if (_ffmpegVideoProcess != null)
        {
            try { _ffmpegVideoProcess.ErrorDataReceived -= OnFfmpegVideoErrorData; } catch { }
        }

        if (_ffmpegAudioProcess != null)
        {
            try { _ffmpegAudioProcess.ErrorDataReceived -= OnFfmpegAudioErrorData; } catch { }
        }

        DisposeProcess(ref _ffmpegVideoProcess);
        DisposeProcess(ref _ffmpegAudioProcess);

        _audioCapture?.Dispose();
        _audioCapture = null;

        _writerCts?.Dispose();
        _writerCts = null;
        _audioPumpCts?.Dispose();
        _audioPumpCts = null;
        _videoWriterTask = null;
        _audioPumpTask = null;
        _ffmpegStderr.Clear();
        _hasWrittenFrame = false;
        _videoFramesWritten = 0;
    }

    private static void DisposeProcess(ref Process? process)
    {
        if (process == null)
            return;

        try { process.Dispose(); } catch { }
        process = null;
    }

    private string ExtractFfmpegErrorMessage()
    {
        lock (_ffmpegStderr)
        {
            var text = _ffmpegStderr.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var lines = new List<string>();
            foreach (var line in text.Split('\n', '\r'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("empty, nothing was encoded", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Cannot", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(line.Trim());
                }
            }

            if (lines.Count > 0)
                return lines[^1];

            var tail = text.Trim();
            return tail.Length > 240 ? tail[^240..] : tail;
        }
    }

    private async Task VideoWriterLoop(CancellationToken cancellationToken)
    {
        try
        {
            var frameSize = _frameStride * _frameHeight;

            while (!cancellationToken.IsCancellationRequested && _isRecording)
            {
                var nextDue = _recordingStartTicks + (_videoFramesWritten + 1) * _frameIntervalTicks;
                var now = Stopwatch.GetTimestamp();
                if (now < nextDue)
                {
                    var waitMs = (int)((nextDue - now) * 1000 / Stopwatch.Frequency);
                    if (waitMs > 0)
                        await Task.Delay(Math.Min(waitMs, 250), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_videoStdin == null || _ffmpegVideoProcess is { HasExited: true })
                    break;

                byte[] frame;
                if (TryGetEncodedFrame(out var fresh, frameSize))
                {
                    frame = fresh;
                    if (_lastWrittenFrame == null || _lastWrittenFrame.Length != frameSize)
                        _lastWrittenFrame = new byte[frameSize];
                    Buffer.BlockCopy(frame, 0, _lastWrittenFrame, 0, frameSize);
                    _hasWrittenFrame = true;
                }
                else if (_hasWrittenFrame && _lastWrittenFrame != null)
                {
                    frame = _lastWrittenFrame;
                }
                else
                {
                    continue;
                }

                await _videoStdin.WriteAsync(frame.AsMemory(0, frameSize), cancellationToken).ConfigureAwait(false);
                _videoFramesWritten++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Linux gameplay recording video writer failed.", ex);
            if (_isRecording && _ffmpegVideoProcess is { HasExited: true } && _videoFramesWritten == 0)
            {
                var ffmpegMessage = ExtractFfmpegErrorMessage();
                FailRecording(string.IsNullOrWhiteSpace(ffmpegMessage)
                    ? $"Recording stopped: {ex.Message}"
                    : ffmpegMessage);
            }
        }
    }

    private bool TryGetEncodedFrame(out byte[] frame, int frameSize)
    {
        frame = _encodeScratch ?? [];
        if (frame.Length < frameSize)
            return false;

        lock (_frameLock)
        {
            if (!_hasLatestFrame || _latestFrame == null)
                return false;

            if (_latestFrameWidth == _frameWidth && _latestFrameHeight == _frameHeight)
            {
                Buffer.BlockCopy(_latestFrame, 0, frame, 0, frameSize);
                return true;
            }

            var scaled = ScaleBgraFrame(_latestFrame, _latestFrameWidth, _latestFrameHeight, _frameWidth, _frameHeight);
            if (scaled.Length < frameSize)
                return false;

            Buffer.BlockCopy(scaled, 0, frame, 0, frameSize);
            return true;
        }
    }

    private static byte[] ScaleBgraFrame(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var expectedSrc = srcWidth * srcHeight * 4;
        if (source.Length < expectedSrc)
            return [];

        var dest = new byte[dstWidth * dstHeight * 4];
        var srcInfo = new SKImageInfo(srcWidth, srcHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dstInfo = new SKImageInfo(dstWidth, dstHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            using var srcPixmap = new SKPixmap(srcInfo, handle.AddrOfPinnedObject(), srcInfo.RowBytes);
            using var srcImage = SKImage.FromPixels(srcPixmap);
            if (srcImage == null)
                return [];

            using var srcBitmap = SKBitmap.FromImage(srcImage);
            if (srcBitmap == null)
                return [];

            using var dstBitmap = srcBitmap.Resize(dstInfo, SKFilterQuality.High);
            if (dstBitmap == null)
                return [];

            if (!CopyBgraRows(dstBitmap, dest, dstWidth, dstHeight))
                return [];
        }
        finally
        {
            handle.Free();
        }

        return dest;
    }

    private static bool CopyBgraRows(SKBitmap bitmap, byte[] output, int width, int height)
    {
        var pixmap = bitmap.PeekPixels();
        if (pixmap == null)
            return false;

        var rowBytes = width * 4;
        var required = rowBytes * height;
        if (output.Length < required)
            return false;

        var src = pixmap.GetPixelSpan();
        if (src.Length < pixmap.RowBytes * height)
            return false;

        if (pixmap.RowBytes == rowBytes)
        {
            src.Slice(0, required).CopyTo(output);
            return true;
        }

        for (var y = 0; y < height; y++)
            src.Slice(y * pixmap.RowBytes, rowBytes).CopyTo(output.AsSpan(y * rowBytes, rowBytes));

        return true;
    }

    public void Stop()
    {
        if (!_isRecording)
            return;

        _isRecording = false;
        var sessionId = _recordingSessionId;
        var outputPath = _activeOutputPath;
        var videoCapturePath = _videoCapturePath;
        var audioCapturePath = _audioCapturePath;
        var includeAudio = !string.IsNullOrWhiteSpace(_audioCapturePath);

        RecordingStateChanged?.Invoke(false);

        _finalizeTask = Task.Run(() => FinalizeStopAsync(
            sessionId,
            outputPath,
            videoCapturePath,
            audioCapturePath,
            includeAudio));
    }

    public void WaitForPendingFinalize()
    {
        var task = _finalizeTask;
        if (task == null || task.IsCompleted)
            return;

        try
        {
            task.Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Log.Warn("Timed out waiting for the previous Linux recording to finalize.", ex);
        }
    }

    private void FinalizeStopAsync(
        int sessionId,
        string? outputPath,
        string? videoCapturePath,
        string? audioCapturePath,
        bool includeAudio)
    {
        try
        {
            if (sessionId != _recordingSessionId)
                return;

            var finalizedCleanly = TryShutdownEncoderGracefully(allowKill: true);
            var audioCaptureSize = WaitForCaptureFileSizeStable(audioCapturePath, includeAudio);

            lock (_processLock)
            {
                if (sessionId != _recordingSessionId)
                    return;

                ResetEncoderStateAfterShutdown();
            }

            if (sessionId != _recordingSessionId)
                return;

            var outputReady = false;
            var remuxIncludedAudio = false;
            if (!string.IsNullOrWhiteSpace(outputPath) &&
                !string.IsNullOrWhiteSpace(videoCapturePath) &&
                includeAudio &&
                !string.Equals(videoCapturePath, outputPath, StringComparison.Ordinal))
            {
                outputReady = TryFinalizeMuxedOutput(
                    outputPath,
                    videoCapturePath,
                    audioCapturePath,
                    audioCaptureSize,
                    finalizedCleanly,
                    out remuxIncludedAudio);
            }
            else if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
            {
                outputReady = new FileInfo(outputPath).Length >= 1024;
            }

            if (outputReady)
                DeleteSidecarsAfterSuccessfulOutput(videoCapturePath, audioCapturePath, outputPath, remuxIncludedAudio);
            else if (!finalizedCleanly)
                Log.Warn($"Linux gameplay recording may be incomplete. Video: {videoCapturePath}, audio: {audioCapturePath}");

            if (!remuxIncludedAudio &&
                !string.IsNullOrWhiteSpace(outputPath) &&
                !string.IsNullOrWhiteSpace(audioCapturePath) &&
                GetCaptureFileSize(audioCapturePath) > 1024 &&
                File.Exists(outputPath))
            {
                if (TryRemuxOutputWithAudioSidecar(outputPath, audioCapturePath, out _))
                {
                    remuxIncludedAudio = true;
                    DeleteSidecarsAfterSuccessfulOutput(videoCapturePath, audioCapturePath, outputPath, includedAudio: true);
                }
            }

            lock (_frameLock)
            {
                _latestFrame = null;
                _hasLatestFrame = false;
            }

            _encodeScratch = null;
            _lastWrittenFrame = null;
            _hasWrittenFrame = false;
            _ffmpegStderr.Clear();
            _ffmpegAudioStderr.Clear();
            _activeOutputPath = null;
            _videoCapturePath = null;
            _audioCapturePath = null;
            _emulatorProcessId = 0;
            _compositorLaunchPid = 0;
            _includeAudio = false;

            Log.Info($"Linux gameplay recording stopped: {outputPath}");
        }
        catch (Exception ex)
        {
            Log.Warn("Error finalizing Linux gameplay recording.", ex);
        }
    }

    private static long GetCaptureFileSize(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long WaitForCaptureFileSize(string? path, bool includeAudio)
    {
        if (!includeAudio || string.IsNullOrWhiteSpace(path))
            return 0;

        long size = 0;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            size = GetCaptureFileSize(path);
            if (size > 1024)
                return size;

            Thread.Sleep(100);
        }

        return size;
    }

    private static long WaitForCaptureFileSizeStable(string? path, bool includeAudio, int minBytes = 4096)
    {
        if (!includeAudio || string.IsNullOrWhiteSpace(path))
            return 0;

        long lastSize = -1;
        var stableSince = Environment.TickCount64;
        var deadline = Environment.TickCount64 + 6000;

        while (Environment.TickCount64 < deadline)
        {
            var size = GetCaptureFileSize(path);
            if (size >= minBytes && size == lastSize && Environment.TickCount64 - stableSince >= 300)
                return size;

            if (size != lastSize)
                stableSince = Environment.TickCount64;

            lastSize = size;
            Thread.Sleep(50);
        }

        return GetCaptureFileSize(path);
    }

    private bool TryRemuxOutputWithAudioSidecar(string outputPath, string audioCapturePath, out string? error)
    {
        error = null;
        var ffmpegPath = FFmpegLocator.FindFFmpegPath();
        if (ffmpegPath == null)
            return false;

        var container = Path.GetExtension(outputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? GameplayRecordingContainer.Mkv
            : GameplayRecordingContainer.Mp4;

        var tempOutput = BuildRemuxTempOutputPath(outputPath);
        TryDeleteFile(tempOutput);

        if (!TryRunRemux(ffmpegPath, outputPath, audioCapturePath, tempOutput, container, out error))
            return false;

        try
        {
            TryDeleteFile(outputPath);
            File.Move(tempOutput, outputPath);
            Log.Info($"Linux gameplay recording remuxed existing output with audio sidecar: {outputPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDeleteFile(tempOutput);
            return false;
        }
    }

    private bool TryRunRemux(
        string ffmpegPath,
        string videoPath,
        string audioPath,
        string tempOutput,
        GameplayRecordingContainer container,
        out string? error)
    {
        error = null;
        var args = BuildRemuxArgumentList(videoPath, audioPath, tempOutput, container, "libx264");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "Failed to start FFmpeg remux process.";
                return false;
            }

            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(FinalizeRemuxWaitMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "Remux timed out.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = ExtractRemuxError(stderr);
                return false;
            }

            if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length < 1024)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? "Remux produced an empty output file." : ExtractRemuxError(stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryFinalizeMuxedOutput(
        string outputPath,
        string videoCapturePath,
        string? audioCapturePath,
        long audioCaptureSize,
        bool videoFinalizedCleanly,
        out bool includedAudio)
    {
        includedAudio = false;
        var videoSize = GetCaptureFileSize(videoCapturePath);
        if (videoSize < 1024)
        {
            Log.Warn($"Linux gameplay recording video capture is too small to remux ({videoSize} bytes): {videoCapturePath}");
            return false;
        }

        var ffmpegPath = FFmpegLocator.FindFFmpegPath();
        if (ffmpegPath == null)
            return TryMoveVideoOnlyFallback(outputPath, videoCapturePath);

        var hasAudio = !string.IsNullOrWhiteSpace(audioCapturePath)
            && File.Exists(audioCapturePath)
            && audioCaptureSize > 1024;

        if (!hasAudio)
        {
            Log.Warn(
                $"Linux gameplay recording audio sidecar missing or too small ({audioCaptureSize} bytes): {audioCapturePath}");
            if (audioCaptureSize > 0 && !string.IsNullOrWhiteSpace(audioCapturePath))
                Log.Warn($"Linux gameplay recording audio sidecar kept for inspection: {audioCapturePath}");
            var moved = TryMoveVideoOnlyFallback(outputPath, videoCapturePath);
            if (!videoFinalizedCleanly)
                Log.Warn($"Linux gameplay recording saved without audio: {outputPath}");
            return moved;
        }

        var container = Path.GetExtension(outputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? GameplayRecordingContainer.Mkv
            : GameplayRecordingContainer.Mp4;

        var tempOutput = BuildRemuxTempOutputPath(outputPath);
        TryDeleteFile(tempOutput);

        if (!TryRunRemux(ffmpegPath, videoCapturePath, audioCapturePath!, tempOutput, container, out var remuxError))
        {
            Log.Warn($"Linux gameplay recording remux failed: {remuxError ?? "unknown remux error"}");
            TryDeleteFile(tempOutput);
            return TryMoveVideoOnlyFallback(outputPath, videoCapturePath);
        }

        try
        {
            TryDeleteFile(outputPath);
            File.Move(tempOutput, outputPath);
            includedAudio = true;
            Log.Info($"Linux gameplay recording remuxed with audio: {outputPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Linux gameplay recording remux failed while replacing output.", ex);
            TryDeleteFile(tempOutput);
            return TryMoveVideoOnlyFallback(outputPath, videoCapturePath);
        }
    }

    private static bool TryMoveVideoOnlyFallback(string outputPath, string videoCapturePath)
    {
        try
        {
            if (!File.Exists(videoCapturePath))
                return false;

            var targetExt = Path.GetExtension(outputPath);
            var captureExt = Path.GetExtension(videoCapturePath);
            TryDeleteFile(outputPath);

            if (string.Equals(targetExt, captureExt, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(videoCapturePath, outputPath);
                return new FileInfo(outputPath).Length >= 1024;
            }

            File.Copy(videoCapturePath, outputPath, overwrite: true);
            return new FileInfo(outputPath).Length >= 1024;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move video-only recording to {outputPath}.", ex);
            return false;
        }
    }

    private static string ExtractRemuxError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "unknown remux error";

        var lines = stderr.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : stderr.Trim();
    }

    private static void DeleteSidecarsAfterSuccessfulOutput(
        string? videoCapturePath,
        string? audioCapturePath,
        string? outputPath,
        bool includedAudio)
    {
        if (!string.IsNullOrWhiteSpace(videoCapturePath) &&
            !string.Equals(videoCapturePath, outputPath, StringComparison.Ordinal))
        {
            TryDeleteFile(videoCapturePath);
        }

        if (includedAudio)
            TryDeleteFile(audioCapturePath);
    }

    private void FailRecording(string message)
    {
        if (Monitor.IsEntered(_processLock))
        {
            Task.Run(() => FailRecording(message));
            return;
        }

        Log.Warn($"Linux gameplay recording failed: {message}");
        Stop();
        RecordingFailed?.Invoke(message);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}
