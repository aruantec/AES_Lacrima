using System;
using System.Threading;
using SkiaSharp;

namespace AES_Emulation.Linux;

/// <summary>
/// Scales cropped game pixels to fill the recording output (no baked-in viewport letterbox).
/// </summary>
internal static class LinuxGameplayRecordingFrameComposer
{
    public static byte[] ComposeGameContentFrame(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int cropLeft,
        int cropRight,
        int outputWidth,
        int outputHeight,
        SKColorType sourceColorType = SKColorType.Bgra8888)
    {
        var output = new byte[outputWidth * outputHeight * 4];
        var requiredSource = sourceWidth * sourceHeight * 4;
        if (source.Length < requiredSource)
            return output;

        var cropL = Math.Max(0, cropLeft);
        var cropR = Math.Max(0, cropRight);
        if (cropL + cropR >= sourceWidth)
            return output;

        var cropW = sourceWidth - cropL - cropR;
        var cropH = sourceHeight;
        if (cropW <= 0 || cropH <= 0)
            return output;

        var alphaType = sourceColorType == SKColorType.Rgba8888 ? SKAlphaType.Premul : SKAlphaType.Premul;
        var info = new SKImageInfo(sourceWidth, sourceHeight, sourceColorType, alphaType);
        var destInfo = new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(source, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var srcPixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var srcImage = SKImage.FromPixels(srcPixmap);
            if (srcImage == null)
                return output;

            var cropRect = new SKRectI(cropL, 0, cropL + cropW, cropH);
            using var croppedImage = srcImage.Subset(cropRect);
            if (croppedImage == null)
                return output;

            using var croppedBitmap = SKBitmap.FromImage(croppedImage);
            if (croppedBitmap == null)
                return output;

            using var scaledBitmap = croppedBitmap.Resize(destInfo, SKFilterQuality.High);
            if (scaledBitmap == null)
                return output;

            if (!CopyBitmapRowsToBuffer(scaledBitmap, output, outputWidth, outputHeight))
                return output;
        }
        finally
        {
            handle.Free();
        }

        return output;
    }

    private static bool CopyBitmapRowsToBuffer(SKBitmap bitmap, byte[] output, int width, int height)
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
}

internal sealed class LinuxGameplayRecordingFrameWorker : IDisposable
{
    private readonly object _snapshotLock = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private LinuxGameplayRecordingFrameWorker.RecordingSnapshot? _pendingSnapshot;

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "LinuxGameplayRecordingCompositor",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _frameReady.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;

        lock (_snapshotLock)
            _pendingSnapshot = null;
    }

    public bool TryEnqueue(RecordingSnapshot snapshot)
    {
        if (!_running)
            return false;

        lock (_snapshotLock)
            _pendingSnapshot = snapshot;

        _frameReady.Set();
        return true;
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            _frameReady.WaitOne(100);
            if (!_running)
                break;

            RecordingSnapshot? snapshot;
            lock (_snapshotLock)
            {
                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
            }

            if (snapshot == null || snapshot.Handler == null)
                continue;

            try
            {
                var pixels = LinuxGameplayRecordingFrameComposer.ComposeGameContentFrame(
                    snapshot.Source,
                    snapshot.SourceWidth,
                    snapshot.SourceHeight,
                    snapshot.CropLeft,
                    snapshot.CropRight,
                    snapshot.OutputWidth,
                    snapshot.OutputHeight,
                    snapshot.SourceColorType);

                snapshot.Handler(pixels, snapshot.OutputWidth, snapshot.OutputHeight);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _frameReady.Dispose();
    }

    internal sealed class RecordingSnapshot
    {
        public required byte[] Source { get; init; }
        public required int SourceWidth { get; init; }
        public required int SourceHeight { get; init; }
        public required int CropLeft { get; init; }
        public required int CropRight { get; init; }
        public required int OutputWidth { get; init; }
        public required int OutputHeight { get; init; }
        public SKColorType SourceColorType { get; init; } = SKColorType.Bgra8888;
        public required Action<byte[], int, int>? Handler { get; init; }
    }
}
