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
/// Linux gameplay recorder: PipeWire composition frames to FFmpeg with optional PulseAudio input.
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

    private Process? _ffmpegProcess;
    private Stream? _videoStdin;
    private CancellationTokenSource? _writerCts;
    private Task? _videoWriterTask;

    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private int _targetFps = 30;
    private long _recordingStartTicks;
    private long _frameIntervalTicks;
    private long _videoFramesWritten;

    private string? _activeOutputPath;
    private volatile bool _isRecording;
    private int _emulatorProcessId;
    private bool _includeAudio;
    private string? _pulseInput;
    private byte[]? _encodeScratch;
    private byte[]? _lastWrittenFrame;
    private bool _hasWrittenFrame;
    private readonly StringBuilder _ffmpegStderr = new();
    private int _recordingSessionId;
    private Task? _finalizeTask;
    private int _encoderStartupScheduled;

    private const int FinalizeWriterWaitMs = 5_000;
    private const int FinalizeFfmpegWaitMs = 8_000;

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
        int emulatorProcessId)
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
        _includeAudio = false;
        _pulseInput = null;
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

        if (_ffmpegProcess != null)
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
            if (!_isRecording)
                return false;

            if (_ffmpegProcess != null)
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
                return false;

            if (FfmpegHardwareEncoderProbe.IsVendorEncoderMissing(
                    ffmpegPath,
                    settings.GameplayRecordingVideoCodec,
                    settings.GameplayRecordingEncoderPreference,
                    out var missingEncoder))
            {
                Log.Warn($"Linux gameplay recording: {missingEncoder} is not in this FFmpeg build; falling back during probe.");
            }

            var audioSource = settings.GameplayRecordingAudioSource;
            var audioProcessId = audioSource switch
            {
                GameplayRecordingAudioSource.Application => settings.GameplayRecordingAudioProcessId,
                GameplayRecordingAudioSource.EmulatorProcess => _emulatorProcessId,
                _ => 0
            };
            var audioDeviceId = audioSource == GameplayRecordingAudioSource.OutputDevice
                ? settings.GameplayRecordingAudioDeviceId
                : null;

            _pulseInput = LinuxGameplayAudioCapture.CanCapturePulse()
                ? LinuxGameplayAudioCapture.ResolvePulseInputForRecording(
                    audioSource,
                    audioProcessId,
                    audioDeviceId)
                : null;
            _includeAudio = !string.IsNullOrWhiteSpace(_pulseInput);

            if (audioSource != GameplayRecordingAudioSource.None && !_includeAudio)
                Log.Warn("Linux gameplay recording: PulseAudio is unavailable; continuing with video only.");

            try
            {
                var bitrate = Math.Clamp(settings.GameplayRecordingBitrateKbps, 1000, 100_000);
                var probeResult = LinuxFfmpegRecordingPreflight.ResolveRecordingEncoder(
                    ffmpegPath,
                    settings.GameplayRecordingVideoCodec,
                    settings.GameplayRecordingEncoderPreference,
                    settings.GameplayRecordingContainer);

                return StartEncoderProcess(ffmpegPath, settings, fps, bitrate, probeResult, _includeAudio);
            }
            catch (Exception ex)
            {
                FailRecording($"Failed to start encoder: {ex.Message}");
                return false;
            }
        }
    }

    private bool StartEncoderProcess(
        string ffmpegPath,
        SettingsViewModel settings,
        int fps,
        int bitrate,
        FfmpegRecordingPreflight.PreflightResult probeResult,
        bool includeAudio)
    {
        var codecName = probeResult.CodecName;
        var codecExtra = probeResult.CodecExtra;

        var outputPath = _activeOutputPath!;
        if (probeResult.Container != settings.GameplayRecordingContainer)
        {
            var newExt = GameplayRecordingFormat.GetFileExtension(probeResult.Container);
            outputPath = Path.ChangeExtension(outputPath, newExt);
            _activeOutputPath = outputPath;
            Log.Info($"Recording container adjusted to {probeResult.Container} for {codecName}.");
        }

        var args = BuildFfmpegArguments(
            outputPath,
            _frameWidth,
            _frameHeight,
            fps,
            probeResult.Container,
            codecName,
            codecExtra,
            bitrate,
            includeAudio ? _pulseInput : null);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };
        LinuxGameplayAudioCapture.ApplyAudioEnvironment(startInfo);

        _ffmpegProcess = Process.Start(startInfo);
        if (_ffmpegProcess == null)
        {
            FailRecording("Failed to start FFmpeg.");
            return false;
        }

        _recordingStartTicks = Stopwatch.GetTimestamp();
        _videoFramesWritten = 0;

        _videoStdin = _ffmpegProcess.StandardInput.BaseStream;
        _writerCts = new CancellationTokenSource();
        _videoWriterTask = Task.Run(() => VideoWriterLoop(_writerCts.Token), CancellationToken.None);

        _ffmpegStderr.Clear();
        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            lock (_ffmpegStderr)
                _ffmpegStderr.AppendLine(e.Data);

            Log.Debug($"ffmpeg: {e.Data}");
        };
        _ffmpegProcess.EnableRaisingEvents = true;
        _ffmpegProcess.Exited += OnFfmpegProcessExited;
        _ffmpegProcess.BeginErrorReadLine();

        Log.Info($"Linux gameplay recording started ({_frameWidth}x{_frameHeight} @ {fps}fps, {codecName}, audio={includeAudio}): {_activeOutputPath}");
        Log.Info($"FFmpeg arguments: {args}");
        return true;
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
        var quotedOut = $"\"{outputPath}\"";
        var movFlags = container == GameplayRecordingContainer.Mp4 && !FfmpegHardwareEncoderProbe.IsAmdAmfEncoder(codecName)
            ? "-movflags +faststart"
            : string.Empty;
        var videoFilter = FfmpegHardwareEncoderProbe.GetInputVideoFilter(codecName);
        var fpsMode = FfmpegHardwareEncoderProbe.UseCfrFpsMode(codecName) ? "-fps_mode cfr" : string.Empty;

        if (!string.IsNullOrWhiteSpace(pulseInput))
        {
            var quotedPulse = pulseInput.Contains(' ') ? $"\"{pulseInput}\"" : pulseInput;
            return string.Join(' ',
                "-hide_banner -loglevel warning -y",
                $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i pipe:0",
                "-f pulse -ar 48000 -ac 2 -thread_queue_size 1024 -i", quotedPulse,
                "-map 0:v:0 -map 1:a:0?",
                videoFilter,
                $"-c:v {codecName} {codecExtra}",
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k",
                fpsMode,
                "-c:a aac -b:a 192k -ar 48000",
                "-shortest",
                "-max_interleave_delta 0",
                movFlags,
                quotedOut).Trim();
        }

        return string.Join(' ',
            "-hide_banner -loglevel warning -y",
            $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps}",
            "-i pipe:0",
            "-an",
            videoFilter,
            $"-c:v {codecName} {codecExtra}",
            $"-b:v {bitrateKbps}k",
            fpsMode,
            movFlags,
            quotedOut).Trim();
    }

    private void OnFfmpegProcessExited(object? sender, EventArgs e)
    {
        if (!_isRecording)
            return;

        var exitCode = _ffmpegProcess?.ExitCode ?? -1;
        if (exitCode == 0)
            return;

        if (_includeAudio)
        {
            Log.Warn("Linux gameplay recording: FFmpeg exited while capturing audio; retrying video only.");
            _includeAudio = false;
            _pulseInput = null;
            if (TryRestartEncoderWithoutAudio())
                return;
        }

        var message = ExtractFfmpegErrorMessage();
        if (string.IsNullOrWhiteSpace(message))
            message = $"FFmpeg exited with code {exitCode}.";

        FailRecording(message);
    }

    private bool TryRestartEncoderWithoutAudio()
    {
        lock (_processLock)
        {
            CleanupEncoderProcess();

            if (!_isRecording || string.IsNullOrWhiteSpace(_activeOutputPath))
                return false;

            var settings = DiLocator.ResolveViewModel<SettingsViewModel>();
            if (settings == null)
                return false;

            var fps = Math.Clamp(settings.GameplayRecordingFps, 15, 120);
            var ffmpegPath = FFmpegLocator.FindFFmpegPath();
            if (ffmpegPath == null)
                return false;

            try
            {
                var bitrate = Math.Clamp(settings.GameplayRecordingBitrateKbps, 1000, 100_000);
                var probeResult = LinuxFfmpegRecordingPreflight.ResolveRecordingEncoder(
                    ffmpegPath,
                    settings.GameplayRecordingVideoCodec,
                    settings.GameplayRecordingEncoderPreference,
                    settings.GameplayRecordingContainer);

                return StartEncoderProcess(ffmpegPath, settings, fps, bitrate, probeResult, includeAudio: false);
            }
            catch (Exception ex)
            {
                Log.Warn("Linux gameplay recording video-only restart failed.", ex);
                return false;
            }
        }
    }

    private void CleanupEncoderProcess()
    {
        try
        {
            _writerCts?.Cancel();
            _videoWriterTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        try
        {
            _videoStdin?.Close();
        }
        catch
        {
        }

        if (_ffmpegProcess != null)
        {
            try
            {
                _ffmpegProcess.Exited -= OnFfmpegProcessExited;
            }
            catch
            {
            }

            try
            {
                if (!_ffmpegProcess.HasExited)
                    _ffmpegProcess.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try { _ffmpegProcess.Dispose(); } catch { }
            _ffmpegProcess = null;
        }

        _videoStdin = null;
        _writerCts?.Dispose();
        _writerCts = null;
        _videoWriterTask = null;
        _ffmpegStderr.Clear();
        _hasWrittenFrame = false;
        _videoFramesWritten = 0;
        _recordingStartTicks = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _encoderStartupScheduled, 0);
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

                if (_videoStdin == null || _ffmpegProcess is { HasExited: true })
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
            if (_ffmpegProcess is { HasExited: true })
            {
                var ffmpegMessage = ExtractFfmpegErrorMessage();
                FailRecording(string.IsNullOrWhiteSpace(ffmpegMessage)
                    ? $"Recording stopped: {ex.Message}"
                    : ffmpegMessage);
            }
            else
            {
                FailRecording(ex.Message);
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

        RecordingStateChanged?.Invoke(false);

        _finalizeTask = Task.Run(() => FinalizeStopAsync(sessionId, outputPath));
    }

    private void WaitForPendingFinalize()
    {
        var task = _finalizeTask;
        if (task == null || task.IsCompleted)
            return;

        try
        {
            task.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Warn("Timed out waiting for the previous Linux recording to finalize.", ex);
        }
    }

    private void FinalizeStopAsync(int sessionId, string? outputPath)
    {
        try
        {
            try
            {
                _writerCts?.Cancel();
                _videoWriterTask?.Wait(TimeSpan.FromMilliseconds(FinalizeWriterWaitMs));
            }
            catch (Exception ex)
            {
                Log.Warn("Error stopping Linux recording writers.", ex);
            }

            lock (_processLock)
            {
                if (sessionId != _recordingSessionId)
                    return;

                try
                {
                    _videoStdin?.Flush();
                    _videoStdin?.Close();
                }
                catch (Exception logEx)
                {
                    Log.Warn("Error closing ffmpeg video stdin.", logEx);
                }

                if (_ffmpegProcess != null)
                {
                    try
                    {
                        _ffmpegProcess.Exited -= OnFfmpegProcessExited;
                    }
                    catch
                    {
                    }

                    try
                    {
                        if (!_ffmpegProcess.WaitForExit(FinalizeFfmpegWaitMs))
                            _ffmpegProcess.Kill(entireProcessTree: true);
                    }
                    catch (Exception logEx)
                    {
                        Log.Warn("Error waiting for ffmpeg exit.", logEx);
                    }

                    try { _ffmpegProcess.Dispose(); } catch { }
                    _ffmpegProcess = null;
                }

                _videoStdin = null;
                _writerCts?.Dispose();
                _writerCts = null;
                _videoWriterTask = null;
            }

            if (sessionId != _recordingSessionId)
                return;

            lock (_frameLock)
            {
                _latestFrame = null;
                _hasLatestFrame = false;
            }

            _encodeScratch = null;
            _lastWrittenFrame = null;
            _hasWrittenFrame = false;
            _ffmpegStderr.Clear();
            _activeOutputPath = null;
            _emulatorProcessId = 0;
            _includeAudio = false;
            _pulseInput = null;

            Log.Info($"Linux gameplay recording stopped: {outputPath}");
        }
        catch (Exception ex)
        {
            Log.Warn("Error finalizing Linux gameplay recording.", ex);
        }
    }

    private void FailRecording(string message)
    {
        Log.Warn($"Linux gameplay recording failed: {message}");
        var failedPath = _activeOutputPath;
        Stop();
        TryDeleteEmptyRecording(failedPath);
        RecordingFailed?.Invoke(message);
    }

    private static void TryDeleteEmptyRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not delete empty recording file: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
