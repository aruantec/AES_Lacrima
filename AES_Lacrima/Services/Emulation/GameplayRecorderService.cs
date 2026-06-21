using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.DI;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.Services;
using AES_Emulation.Windows.API;
using AES_Lacrima.ViewModels;
using log4net;
using SkiaSharp;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// OBS-style gameplay recorder: real-time paced mux to FFmpeg, optional GPU encoding, latest-frame capture to limit overhead.
/// </summary>
[AutoRegister]
public partial class GameplayRecorderService : IGameplayRecorder
{
    private static readonly ILog Log = LogHelper.For<GameplayRecorderService>();

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
    private Task? _audioWriterTask;
    private Task? _audioPipeTask;
    private GameplayAudioCapture? _audioCapture;
    private NamedPipeServerStream? _audioPipe;
    private string? _audioPipeName;

    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private int _targetFps = 30;
    private long _recordingStartTicks;
    private long _frameIntervalTicks;
    private long _videoFramesWritten;
    private long _audioBytesWritten;
    private int _audioBytesPerSecond = 48000 * 2 * 2;

    private string? _activeOutputPath;
    private volatile bool _isRecording;
    private int _emulatorProcessId;
    private bool _includeAudio;
    private byte[]? _encodeScratch;
    private byte[]? _lastWrittenFrame;
    private bool _hasWrittenFrame;
    private readonly StringBuilder _ffmpegStderr = new();
    private volatile bool _encoderStartupInProgress;
    private string? _activeCodecName;
    private string? _activeCodecExtra;
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

        if (!OperatingSystem.IsWindows())
        {
            RecordingFailed?.Invoke("Gameplay recording is only supported on Windows.");
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
        _activeOutputPath = outputPath;
        _targetFps = Math.Clamp(fps, 15, 120);
        _frameIntervalTicks = Stopwatch.Frequency / _targetFps;
        _recordingStartTicks = Stopwatch.GetTimestamp();
        _videoFramesWritten = 0;
        _audioBytesWritten = 0;
        _hasLatestFrame = false;
        _latestFrame = null;
        _hasWrittenFrame = false;
        _lastWrittenFrame = null;

        _isRecording = true;
        RecordingStateChanged?.Invoke(true);

        Log.Info($"Gameplay recording armed. Output will be: {outputPath}");
        return true;
    }

    private void OnFrameReceived(byte[] pixels, int width, int height)
    {
        if (!_isRecording || pixels.Length == 0)
            return;

        if (_ffmpegProcess == null)
        {
            if (_encoderStartupInProgress)
                return;

            if (!TryEnsureEncoder(width, height))
                return;
        }

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
    }

    private bool TryEnsureEncoder(int width, int height)
    {
        lock (_processLock)
        {
            if (_ffmpegProcess != null)
                return true;

            var settings = DiLocator.ResolveViewModel<SettingsViewModel>();
            if (settings == null || string.IsNullOrWhiteSpace(_activeOutputPath))
                return false;

            var fps = Math.Clamp(settings.GameplayRecordingFps, 15, 120);
            _targetFps = fps;
            _frameIntervalTicks = Stopwatch.Frequency / fps;
            _recordingStartTicks = Stopwatch.GetTimestamp();

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
                FailRecording(
                    $"FFmpeg does not include {missingEncoder}. Install a full FFmpeg build from Settings → Components, or set Encoder to Auto / Software.");
                return false;
            }

            _includeAudio = TryStartAudioCapture(settings, _emulatorProcessId);
            if (settings.GameplayRecordingAudioSource != GameplayRecordingAudioSource.None && !_includeAudio)
                Log.Warn("Gameplay recording: audio capture could not start; continuing with video only.");

            _audioBytesPerSecond = _audioCapture?.SampleRate * (_audioCapture?.BytesPerSample ?? 4) ?? (48000 * 4);

            string? audioPipePath = null;
            if (_includeAudio)
            {
                _audioPipeName = $"aes_rec_audio_{Guid.NewGuid():N}";
                audioPipePath = $@"\\.\pipe\{_audioPipeName}";
            }

            _encoderStartupInProgress = true;
            try
            {
                var bitrate = Math.Clamp(settings.GameplayRecordingBitrateKbps, 1000, 100_000);
                var probeResult = FfmpegRecordingPreflight.ProbeBestEncoderAsync(
                        ffmpegPath,
                        settings.GameplayRecordingVideoCodec,
                        settings.GameplayRecordingEncoderPreference,
                        settings.GameplayRecordingContainer,
                        _frameWidth,
                        _frameHeight,
                        fps,
                        bitrate,
                        withAudioPipe: false)
                    .GetAwaiter()
                    .GetResult();

                if (probeResult == null)
                {
                    FailRecording(
                        settings.GameplayRecordingVideoCodec == GameplayRecordingVideoCodec.H264
                            ? "H.264 encoding failed on this GPU. Try Container: MKV, Encoder: AMD, or use AV1 + AMD."
                            : "Video encoder failed to start. Try Encoder: Software or a different container.");
                    return false;
                }

                var codecName = probeResult.CodecName;
                var codecExtra = probeResult.CodecExtra;
                _activeCodecName = codecName;
                _activeCodecExtra = codecExtra;

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
                    audioPipePath);

                if (_includeAudio && _audioPipeName != null)
                {
                    _audioPipe = new NamedPipeServerStream(
                        _audioPipeName,
                        PipeDirection.Out,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        256 * 1024,
                        256 * 1024);

                    _audioPipeTask = Task.Run(() => WaitForAudioPipeClientAsync(_audioPipe));
                }

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

                _ffmpegProcess = Process.Start(startInfo);
                if (_ffmpegProcess == null)
                {
                    FailRecording("Failed to start FFmpeg.");
                    return false;
                }

                try
                {
                    _ffmpegProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                }

                _videoStdin = _ffmpegProcess.StandardInput.BaseStream;
                _writerCts = new CancellationTokenSource();
                _videoWriterTask = Task.Run(() => VideoWriterLoop(_writerCts.Token), CancellationToken.None);

                if (_includeAudio)
                    _audioWriterTask = Task.Run(() => AudioWriterLoop(_writerCts.Token), CancellationToken.None);

                _ffmpegStderr.Clear();
                _ffmpegProcess.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    lock (_ffmpegStderr)
                    {
                        _ffmpegStderr.AppendLine(e.Data);
                    }

                    Log.Debug($"ffmpeg: {e.Data}");
                };
                _ffmpegProcess.EnableRaisingEvents = true;
                _ffmpegProcess.Exited += OnFfmpegProcessExited;
                _ffmpegProcess.BeginErrorReadLine();

                Log.Info($"Gameplay recording started ({_frameWidth}x{_frameHeight} @ {fps}fps, {codecName}, audio={_includeAudio}): {_activeOutputPath}");
                Log.Info($"FFmpeg arguments: {args}");
                return true;
            }
            catch (Exception ex)
            {
                FailRecording($"Failed to start encoder: {ex.Message}");
                return false;
            }
            finally
            {
                _encoderStartupInProgress = false;
            }
        }
    }

    private bool TryStartAudioCapture(SettingsViewModel settings, int emulatorProcessId)
    {
        var source = settings.GameplayRecordingAudioSource;
        if (source == GameplayRecordingAudioSource.None || !GameplayAudioCapture.IsSupported)
            return false;

        var processId = source switch
        {
            GameplayRecordingAudioSource.Application => settings.GameplayRecordingAudioProcessId,
            GameplayRecordingAudioSource.EmulatorProcess => emulatorProcessId,
            _ => 0
        };

        var deviceId = source == GameplayRecordingAudioSource.OutputDevice
            ? settings.GameplayRecordingAudioDeviceId
            : null;

        _audioCapture?.Dispose();
        _audioCapture = new GameplayAudioCapture();
        if (_audioCapture.TryStart(source, processId, deviceId))
            return true;

        _audioCapture.Dispose();
        _audioCapture = null;
        return false;
    }

    private async Task WaitForAudioPipeClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            Log.Info("Gameplay recording: FFmpeg connected to audio pipe.");
        }
        catch (Exception ex)
        {
            Log.Warn("Gameplay recording audio pipe wait failed.", ex);
        }
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
        string? audioPipePath)
    {
        var quotedOut = $"\"{outputPath}\"";
        // AMF + fragmented/faststart MP4 often breaks H.264 on AMD; plain mux is safest.
        var movFlags = container == GameplayRecordingContainer.Mp4 && !FfmpegHardwareEncoderProbe.IsAmdAmfEncoder(codecName)
            ? "-movflags +faststart"
            : string.Empty;
        var videoFilter = FfmpegHardwareEncoderProbe.GetInputVideoFilter(codecName);
        var fpsMode = FfmpegHardwareEncoderProbe.UseCfrFpsMode(codecName) ? "-fps_mode cfr" : string.Empty;

        if (!string.IsNullOrWhiteSpace(audioPipePath))
        {
            var quotedAudio = $"\"{audioPipePath}\"";
            return string.Join(' ',
                "-hide_banner -loglevel warning -y",
                $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i pipe:0",
                "-f s16le -ar 48000 -ac 2 -thread_queue_size 1024 -i", quotedAudio,
                "-map 0:v:0 -map 1:a:0?",
                videoFilter,
                $"-c:v {codecName} {codecExtra}",
                FfmpegHardwareEncoderProbe.GetVideoBitrateArguments(codecName, bitrateKbps),
                fpsMode,
                "-c:a aac -b:a 192k -ar 48000",
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
            FfmpegHardwareEncoderProbe.GetVideoBitrateArguments(codecName, bitrateKbps),
            fpsMode,
            movFlags,
            quotedOut).Trim();
    }

    private void OnFfmpegProcessExited(object? sender, EventArgs e)
    {
        if (!_isRecording)
            return;

        var exitCode = _ffmpegProcess?.ExitCode ?? -1;
        if (exitCode == 0 && _videoFramesWritten > 0)
            return;

        var message = ExtractFfmpegErrorMessage();
        if (string.IsNullOrWhiteSpace(message) && exitCode != 0)
            message = $"FFmpeg exited with code {exitCode}.";

        FailRecording(string.IsNullOrWhiteSpace(message)
            ? "FFmpeg stopped unexpectedly. For H.264 on AMD try MKV container, or use AV1 + AMD."
            : message);
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

    /// <summary>
    /// Writes exactly one video frame per wall-clock interval (OBS-style CFR pacing).
    /// </summary>
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
            Log.Warn("Gameplay recording video writer failed.", ex);
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

    private async Task AudioWriterLoop(CancellationToken cancellationToken)
    {
        try
        {
            if (_audioPipe == null || _audioCapture == null)
                return;

            while (!_audioPipe.IsConnected && !cancellationToken.IsCancellationRequested)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            var chunkSize = Math.Max(4096, _audioCapture.SampleRate * _audioCapture.BytesPerSample / 20);
            var buffer = new byte[chunkSize];

            while (!cancellationToken.IsCancellationRequested && _isRecording)
            {
                var read = _audioCapture.Read(buffer);
                if (read <= 0)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_audioPipe.IsConnected)
                    await _audioPipe.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                _audioBytesWritten += read;
                await PaceAudioAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Gameplay recording audio writer failed.", ex);
        }
    }

    private async Task PaceAudioAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.GetTimestamp() - _recordingStartTicks;
        var expectedBytes = (long)(_audioBytesPerSecond * (elapsed / (double)Stopwatch.Frequency));
        if (_audioBytesWritten <= expectedBytes + _audioBytesPerSecond / 20)
            return;

        var excess = _audioBytesWritten - expectedBytes;
        var ms = (int)(excess * 1000.0 / _audioBytesPerSecond);
        if (ms > 0)
            await Task.Delay(Math.Min(ms, 100), cancellationToken).ConfigureAwait(false);
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

        try
        {
            _writerCts?.Cancel();
            _videoWriterTask?.Wait(TimeSpan.FromSeconds(8));
            _audioWriterTask?.Wait(TimeSpan.FromSeconds(3));
            _audioPipeTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            Log.Warn("Error stopping recording writers.", ex);
        }

        lock (_processLock)
        {
            try
            {
                _videoStdin?.Flush();
                _videoStdin?.Close();
            }
            catch (Exception logEx)
            {
                Log.Warn("Error closing ffmpeg video stdin.", logEx);
            }

            try
            {
                _audioPipe?.Dispose();
            }
            catch (Exception logEx)
            {
                Log.Warn("Error closing audio pipe.", logEx);
            }

            _audioPipe = null;
            _audioPipeName = null;

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
                    if (!_ffmpegProcess.WaitForExit(60_000))
                        _ffmpegProcess.Kill(entireProcessTree: true);
                }
                catch (Exception logEx)
                {
                    Log.Warn("Error waiting for ffmpeg exit.", logEx);
                }

                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }

            _videoStdin = null;
            _writerCts?.Dispose();
            _writerCts = null;
            _videoWriterTask = null;
            _audioWriterTask = null;
            _audioPipeTask = null;
        }

        _audioCapture?.Dispose();
        _audioCapture = null;

        lock (_frameLock)
        {
            _latestFrame = null;
            _hasLatestFrame = false;
        }

        _encodeScratch = null;
        _lastWrittenFrame = null;
        _hasWrittenFrame = false;
        _ffmpegStderr.Clear();

        Log.Info($"Gameplay recording stopped: {_activeOutputPath}");
        _activeOutputPath = null;
        _emulatorProcessId = 0;
        _includeAudio = false;
        RecordingStateChanged?.Invoke(false);
    }

    private void FailRecording(string message)
    {
        Log.Warn($"Gameplay recording failed: {message}");
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

    public static string GetDefaultOutputDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(documents))
            return Path.Combine(documents, ApplicationName);

        return Path.Combine(ApplicationPaths.DataRootDirectory, "Recordings");
    }

    private const string ApplicationName = "AES_Lacrima";
}
