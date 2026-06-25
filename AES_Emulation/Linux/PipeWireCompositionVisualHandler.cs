using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AES_Emulation.Linux.API;
using AES_Emulation.Services;
using AES_Core.Logging;
using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using log4net;
using SkiaSharp;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
internal sealed class PipeWireCompositionVisualHandler : CompositionCustomVisualHandler
{
    private static readonly ILog Log = LogHelper.For<PipeWireCompositionVisualHandler>();
    private nint _capture;
    private WeakReference<LinuxCompositionCaptureControl>? _ownerRef;
    private bool _useOwnerInvalidation;
    private volatile bool _renderSuspended;
    private Vector2 _visualSize;
    private Stretch _stretch = Stretch.UniformToFill;
    private float _brightness = 1.0f;
    private float _saturation = 1.0f;
    private Color _tint = Colors.White;
    private double _contentAspectRatio;
    private SKRect _destRect;
    private bool _rectDirty = true;
    private int _lastFrameWidth = -1;
    private int _lastFrameHeight = -1;
    private double _lastContentAspectRatio = -1;
    private readonly SKPaint _paint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    private readonly object _renderLock = new();
    private IntPtr _cpuCopyBuffer = IntPtr.Zero;
    private nuint _cpuCopyBufferSize;
    private SKBitmap? _presentedFrame;
    private long _lastPresentTicks;
    private double _smoothedFps;
    private double _smoothedFrameTimeMs;
    private long _lastUiUpdateTicks;
    private int _ownerInvalidateQueued;
    private LinuxCompositionShaderRenderer? _shaderRenderer;
    private string? _shaderPath;
    private GlInterface? _gl;
    private bool _loggedMissingGl;

    internal Action<byte[], int, int>? RecordingFrameHandler;
    private readonly LinuxGameplayRecordingFrameWorker _recordingFrameWorker = new();
    private int _recordingTargetFps = 60;
    private GameplayRecordingResolutionCap _recordingResolutionCap = GameplayRecordingResolutionCap.P1080;
    private long _lastRecordingPublishTicks;

    public void SetRecordingFrameHandler(Action<byte[], int, int>? frameHandler) => RecordingFrameHandler = frameHandler;

    internal void SetRecordingTargetFps(int fps) => _recordingTargetFps = Math.Clamp(fps, 1, 120);

    internal void SetRecordingResolutionCap(GameplayRecordingResolutionCap cap) => _recordingResolutionCap = cap;

    internal void SetRecordingWorkerActive(bool active)
    {
        if (active)
            _recordingFrameWorker.Start();
        else
            _recordingFrameWorker.Stop();
    }

    public void SetOwner(LinuxCompositionCaptureControl owner) => _ownerRef = new WeakReference<LinuxCompositionCaptureControl>(owner);

    public void SuspendRendering()
    {
        _renderSuspended = true;
        lock (_renderLock)
        {
            _capture = nint.Zero;
            ClearPresentedFrameLocked();
        }
    }

    internal void PrepareForNewSession(nint capture)
    {
        lock (_renderLock)
        {
            ClearPresentedFrameLocked();
            _capture = capture;
            _lastFrameWidth = -1;
            _lastFrameHeight = -1;
            _rectDirty = true;
            _lastPresentTicks = 0;
            _smoothedFps = 0;
            _smoothedFrameTimeMs = 0;
        }

        _renderSuspended = false;
        NotifyCompositor();
    }

    private void ClearPresentedFrameLocked()
    {
        _presentedFrame?.Dispose();
        _presentedFrame = null;
    }

    internal void TryWaitForRenderIdle(TimeSpan timeout)
    {
        if (Monitor.TryEnter(_renderLock, timeout))
            Monitor.Exit(_renderLock);
    }

    public override void OnMessage(object? message)
    {
        if (message == null)
        {
            Cleanup();
            return;
        }

        switch (message)
        {
            case PipeWireSessionMessage session:
                PrepareForNewSession(session.Capture);
                break;
            case Vector2 size:
                if (_visualSize != size)
                {
                    _visualSize = size;
                    _rectDirty = true;
                    InvalidateGraphicsState();
                    NotifyCompositor();
                }
                break;
            case PipeWireSettingsMessage settings:
                _stretch = settings.Stretch;
                _brightness = settings.Brightness;
                _saturation = settings.Saturation;
                _tint = settings.Tint;
                _contentAspectRatio = settings.ContentAspectRatio;
                if (!string.Equals(_shaderPath, settings.ShaderPath, StringComparison.OrdinalIgnoreCase))
                {
                    _shaderPath = settings.ShaderPath;
                    _shaderRenderer?.Dispose();
                    _shaderRenderer = null;
                }
                _rectDirty = true;
                lock (_renderLock)
                {
                    if (_lastFrameWidth > 0 && _lastFrameHeight > 0)
                        EnsureDestRect(_lastFrameWidth, _lastFrameHeight);
                }
                NotifyCompositor();
                break;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        if (_renderSuspended)
            return;

        nint capture;
        lock (_renderLock)
        {
            capture = _capture;
        }

        if (capture == nint.Zero)
            return;

        try
        {
            if (TryAdvanceFrame())
                NotifyCompositor();
            UpdateUiStatsIfNeeded();
        }
        finally
        {
            if (!_renderSuspended && capture != nint.Zero && !_useOwnerInvalidation)
                RegisterForNextAnimationFrameUpdate();
        }
    }

    private void PublishUiStats()
    {
        if (_ownerRef == null || !_ownerRef.TryGetTarget(out var owner))
            return;

        var fps = Math.Round(_smoothedFps, 1);
        var ft = Math.Round(_smoothedFrameTimeMs, 2);
        Dispatcher.UIThread.Post(() =>
        {
            owner.Fps = fps;
            owner.FrameTimeMs = ft;
        }, DispatcherPriority.Background);
    }

    private void UpdateUiStatsIfNeeded()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if ((double)(nowTicks - _lastUiUpdateTicks) / Stopwatch.Frequency < 0.1)
            return;

        if (_ownerRef == null || !_ownerRef.TryGetTarget(out var owner))
            return;

        _lastUiUpdateTicks = nowTicks;
        var fps = Math.Round(_smoothedFps, 1);
        var ft = Math.Round(_smoothedFrameTimeMs, 2);
        Dispatcher.UIThread.Post(() =>
        {
            owner.Fps = fps;
            owner.FrameTimeMs = ft;
        }, DispatcherPriority.Background);
    }

    private void RecordPresentMetrics()
    {
        var nowTicks = Stopwatch.GetTimestamp();
        if (_lastPresentTicks != 0)
        {
            var dt = (double)(nowTicks - _lastPresentTicks) / Stopwatch.Frequency;
            if (dt >= 1.0 / 240.0)
            {
                var instantFps = Math.Clamp(1.0 / dt, 1.0, 240.0);
                var frameMs = dt * 1000.0;
                _smoothedFps = _smoothedFps <= 0 ? instantFps : (_smoothedFps * 0.85) + (instantFps * 0.15);
                _smoothedFrameTimeMs = _smoothedFrameTimeMs <= 0 ? frameMs : (_smoothedFrameTimeMs * 0.85) + (frameMs * 0.15);
            }
        }

        _lastPresentTicks = nowTicks;
    }

    private void NotifyCompositor()
    {
        if (_useOwnerInvalidation)
        {
            RequestOwnerInvalidate();
            return;
        }

        if (!_renderSuspended)
        {
            Invalidate();
            RegisterForNextAnimationFrameUpdate();
        }
    }

    private void RequestOwnerInvalidate()
    {
        if (_ownerRef == null || !_ownerRef.TryGetTarget(out var owner))
            return;

        if (Interlocked.Exchange(ref _ownerInvalidateQueued, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                owner.InvalidateVisual();
            }
            finally
            {
                Volatile.Write(ref _ownerInvalidateQueued, 0);
            }
        }, DispatcherPriority.Render);
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        OnRender(context, new Size(_visualSize.X, _visualSize.Y));
    }

    internal void OnRender(ImmediateDrawingContext context, Size canvasSize)
    {
        if (_visualSize.X < 1 || _visualSize.Y < 1 || _renderSuspended)
            return;

        TryAdvanceFrame();

        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        var grContext = lease.GrContext;
        canvas.Clear(SKColors.Black);

        GlInterface? platformGl = null;
        try
        {
            using var platformLease = lease.TryLeasePlatformGraphicsApi();
            platformGl = platformLease?.Context.TryGetFeature<IGlContext>()?.GlInterface;
        }
        catch
        {
            // Shader path falls back to Skia color matrix when GL is unavailable.
        }

        var useShader = !string.IsNullOrWhiteSpace(_shaderPath);
        if (useShader && platformGl != null)
        {
            // ResetContext in the shader path invalidates Skia's cached GL bindings.
            // Re-bind the active platform lease each frame (especially after fullscreen resize).
            _gl = platformGl;
        }

        EnsureGl(context, grContext, platformGl);

        SKBitmap? frame;
        SKRect destRect;
        lock (_renderLock)
        {
            if (_rectDirty && _lastFrameWidth > 0 && _lastFrameHeight > 0)
                EnsureDestRect(_lastFrameWidth, _lastFrameHeight);

            frame = _presentedFrame;
            destRect = MapDestRectToCanvas(_destRect, canvasSize);
        }

        if (frame == null)
            return;

        if (_gl != null &&
            grContext != null &&
            !string.IsNullOrWhiteSpace(_shaderPath))
        {
            _shaderRenderer ??= new LinuxCompositionShaderRenderer();
            _shaderRenderer.SetShaderPath(_gl, _shaderPath);
            if (_shaderRenderer.TryDraw(canvas, grContext, _gl, frame, destRect, _brightness, _saturation, _tint))
                return;
        }
        else if (!string.IsNullOrWhiteSpace(_shaderPath) && _gl == null && !_loggedMissingGl)
        {
            _loggedMissingGl = true;
            Log.Warn("Linux composition capture could not acquire an OpenGL interface for pixel shaders; falling back to unshaded frames.");
        }

        ApplyColorAdjustments();
        using var image = SKImage.FromBitmap(frame);
        if (image == null)
            return;

        var srcRect = new SKRect(0, 0, frame.Width, frame.Height);
        canvas.DrawImage(image, srcRect, destRect, _paint);
    }

    internal bool HasPresentedFrame => _presentedFrame != null;

    internal void OnOwnerRenderTick()
    {
        if (_renderSuspended)
            return;

        TryAdvanceFrame();
        UpdateUiStatsIfNeeded();
    }

    internal void InvalidateGraphicsState()
    {
        _gl = null;
        _loggedMissingGl = false;
    }

    internal bool TryAdvanceFrame()
    {
        if (_renderSuspended || _visualSize.X < 1 || _visualSize.Y < 1)
            return false;

        lock (_renderLock)
        {
            if (_renderSuspended || _capture == nint.Zero)
                return false;

            if (LinuxCaptureBridge.aes_linux_capture_try_acquire_frame(_capture, out var frame) == 0)
                return false;

            var updated = false;
            try
            {
                updated = TryCopyFrameToCache(in frame);
                if (updated && _presentedFrame != null)
                {
                    _rectDirty = true;
                    EnsureDestRect(_presentedFrame.Width, _presentedFrame.Height);
                    TryPublishRecordingFrame(_presentedFrame.Width, _presentedFrame.Height, frame.Stride, frame.DrmFourcc);
                }
            }
            finally
            {
                LinuxCaptureBridge.aes_linux_capture_release_frame(_capture, frame.FrameId);
            }

            if (updated)
            {
                RecordPresentMetrics();
                PublishUiStats();
            }

            return updated;
        }
    }

    private void EnsureGl(ImmediateDrawingContext context, GRContext? grContext, GlInterface? platformLeaseGl)
    {
        if (_gl != null)
            return;

        if (platformLeaseGl != null)
            _gl = platformLeaseGl;

        if (_gl == null)
        {
            _gl = context.TryGetFeature<IPlatformGraphicsContext>()?.TryGetFeature<IGlContext>()?.GlInterface;
        }

        if (_gl == null)
        {
            var glContext = context.TryGetFeature<IGlContext>();
            if (glContext != null)
                _gl = glContext.GlInterface;
        }

        if (_gl == null && grContext != null)
            _gl = LinuxGlBootstrap.TryCreateFromCurrentContext();
    }

    internal void ConfigureInvalidation(bool useOwnerInvalidation) => _useOwnerInvalidation = useOwnerInvalidation;

    internal void EnsureRenderer()
    {
    }

    private bool TryCopyFrameToCache(in LinuxExportFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return false;

        if (frame.CpuPixels != IntPtr.Zero)
        {
            var cpuByteCount = (nuint)(frame.Stride * frame.Height);
            if (cpuByteCount == 0)
                return false;

            return UpdatePresentedFrame(frame.CpuPixels, frame.Width, frame.Height, frame.Stride, frame.DrmFourcc);
        }

        var dmabufByteCount = (nuint)(frame.Stride * frame.Height);
        if (dmabufByteCount == 0)
            return false;

        EnsureCpuCopyBuffer(dmabufByteCount);
        var copied = LinuxCaptureBridge.aes_linux_capture_copy_held_frame(
            _capture,
            frame.FrameId,
            _cpuCopyBuffer,
            dmabufByteCount);
        if (copied <= 0)
            return false;

        var presentStride = frame.Stride > 0 ? frame.Stride : frame.Width * 4;
        return UpdatePresentedFrame(_cpuCopyBuffer, frame.Width, frame.Height, presentStride, frame.DrmFourcc);
    }

    private bool UpdatePresentedFrame(IntPtr pixels, int width, int height, int stride, uint drmFourcc)
    {
        var colorType = drmFourcc == 0x34324152u ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
        var alphaType = drmFourcc == 0x34324758u ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var info = new SKImageInfo(width, height, colorType, alphaType);

        using var source = SKImage.FromPixelCopy(info, pixels, stride);
        if (source == null)
            return false;

        if (_presentedFrame == null || _presentedFrame.Width != width || _presentedFrame.Height != height)
        {
            _presentedFrame?.Dispose();
            _presentedFrame = SKBitmap.FromImage(source);
            return _presentedFrame != null;
        }

        return source.ReadPixels(_presentedFrame.Info, _presentedFrame.GetPixels(), _presentedFrame.RowBytes, 0, 0);
    }

    private void TryPublishRecordingFrame(int width, int height, int stride, uint drmFourcc)
    {
        _ = stride;
        _ = drmFourcc;

        var handler = RecordingFrameHandler;
        if (handler == null || _presentedFrame == null || width < 16 || height < 16)
            return;

        var fps = Math.Clamp(_recordingTargetFps, 1, 120);
        var intervalTicks = Stopwatch.Frequency / fps;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastRecordingPublishTicks < intervalTicks)
            return;

        _lastRecordingPublishTicks = now;

        var pixmap = _presentedFrame.PeekPixels();
        if (pixmap == null)
            return;

        var rowBytes = width * 4;
        var required = rowBytes * height;
        var sourceCopy = GC.AllocateUninitializedArray<byte>(required);
        var src = pixmap.GetPixelSpan();
        if (src.Length < pixmap.RowBytes * height)
            return;

        if (pixmap.RowBytes == rowBytes)
        {
            src.Slice(0, required).CopyTo(sourceCopy);
        }
        else
        {
            for (var y = 0; y < height; y++)
                src.Slice(y * pixmap.RowBytes, rowBytes).CopyTo(sourceCopy.AsSpan(y * rowBytes, rowBytes));
        }

        var (outputW, outputH) = GameplayRecordingResolution.FitEvenDimensions(width, height, _recordingResolutionCap);
        if (outputW < 16 || outputH < 16)
            return;

        _recordingFrameWorker.TryEnqueue(new LinuxGameplayRecordingFrameWorker.RecordingSnapshot
        {
            Source = sourceCopy,
            SourceWidth = width,
            SourceHeight = height,
            CropLeft = 0,
            CropRight = 0,
            OutputWidth = outputW,
            OutputHeight = outputH,
            SourceColorType = _presentedFrame.ColorType,
            Handler = handler,
        });
    }

    private SKRect MapDestRectToCanvas(SKRect destRect, Size canvasSize)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0 || canvasSize.Width <= 0 || canvasSize.Height <= 0)
            return destRect;

        var scaleX = (float)(canvasSize.Width / _visualSize.X);
        var scaleY = (float)(canvasSize.Height / _visualSize.Y);
        if (Math.Abs(scaleX - 1f) < 0.001f && Math.Abs(scaleY - 1f) < 0.001f)
            return SnapDestRectToCanvas(destRect, canvasSize);

        return SnapDestRectToCanvas(new SKRect(
            destRect.Left * scaleX,
            destRect.Top * scaleY,
            destRect.Right * scaleX,
            destRect.Bottom * scaleY), canvasSize);
    }

    private static SKRect SnapDestRectToCanvas(SKRect destRect, Size canvasSize)
    {
        const float epsilon = 1.5f;
        var canvasW = (float)canvasSize.Width;
        var canvasH = (float)canvasSize.Height;

        if (Math.Abs(destRect.Top) < epsilon)
            destRect.Top = 0;

        if (Math.Abs(destRect.Left) < epsilon)
            destRect.Left = 0;

        if (Math.Abs(canvasW - destRect.Right) < epsilon)
            destRect.Right = canvasW;

        if (Math.Abs(canvasH - destRect.Bottom) < epsilon)
            destRect.Bottom = canvasH;

        return destRect;
    }

    private void EnsureDestRect(int frameWidth, int frameHeight)
    {
        if (!_rectDirty &&
            _lastFrameWidth == frameWidth &&
            _lastFrameHeight == frameHeight &&
            Math.Abs(_lastContentAspectRatio - _contentAspectRatio) < 0.0001)
        {
            return;
        }

        _lastFrameWidth = frameWidth;
        _lastFrameHeight = frameHeight;
        _lastContentAspectRatio = _contentAspectRatio;

        var layoutWidth = (float)frameWidth;
        var layoutHeight = (float)frameHeight;
        if (_contentAspectRatio > 0 && layoutWidth > 0)
            layoutHeight = layoutWidth / (float)_contentAspectRatio;

        _destRect = CalculateAspectRect(_visualSize.X, _visualSize.Y, layoutWidth, layoutHeight);
        _rectDirty = false;
    }

    private SKRect CalculateAspectRect(float viewW, float viewH, float frameW, float frameH)
    {
        if (_stretch == Stretch.Fill)
            return new SKRect(0, 0, viewW, viewH);

        var viewAspect = viewW / viewH;
        var frameAspect = frameW / frameH;

        if (_stretch == Stretch.Uniform)
        {
            if (frameAspect > viewAspect)
            {
                var h = viewW / frameAspect;
                return new SKRect(0, (viewH - h) / 2, viewW, (viewH + h) / 2);
            }

            var w = viewH * frameAspect;
            return new SKRect((viewW - w) / 2, 0, (viewW + w) / 2, viewH);
        }

        if (_stretch == Stretch.UniformToFill)
        {
            if (frameAspect > viewAspect)
            {
                var w = viewH * frameAspect;
                return new SKRect((viewW - w) / 2, 0, (viewW + w) / 2, viewH);
            }

            var h = viewW / frameAspect;
            return new SKRect(0, (viewH - h) / 2, viewW, (viewH + h) / 2);
        }

        return new SKRect((viewW - frameW) / 2, (viewH - frameH) / 2, (viewW + frameW) / 2, (viewH + frameH) / 2);
    }

    private void ApplyColorAdjustments()
    {
        _paint.ColorFilter?.Dispose();

        if (Math.Abs(_brightness - 1f) < 0.001f &&
            Math.Abs(_saturation - 1f) < 0.001f &&
            _tint == Colors.White)
        {
            _paint.ColorFilter = null;
            return;
        }

        const float rWeight = 0.299f;
        const float gWeight = 0.587f;
        const float bWeight = 0.114f;
        var oneMinusSat = 1.0f - _saturation;
        var rAlpha = oneMinusSat * rWeight;
        var gAlpha = oneMinusSat * gWeight;
        var bAlpha = oneMinusSat * bWeight;

        var matrix = new float[]
        {
            (rAlpha + _saturation) * _brightness * (_tint.R / 255f), gAlpha * _brightness * (_tint.G / 255f), bAlpha * _brightness * (_tint.B / 255f), 0, 0,
            rAlpha * _brightness * (_tint.R / 255f), (gAlpha + _saturation) * _brightness * (_tint.G / 255f), bAlpha * _brightness * (_tint.B / 255f), 0, 0,
            rAlpha * _brightness * (_tint.R / 255f), gAlpha * _brightness * (_tint.G / 255f), (bAlpha + _saturation) * _brightness * (_tint.B / 255f), 0, 0,
            0, 0, 0, _tint.A / 255f, 0
        };
        _paint.ColorFilter = SKColorFilter.CreateColorMatrix(matrix);
    }

    private void EnsureCpuCopyBuffer(nuint size)
    {
        if (_cpuCopyBuffer != IntPtr.Zero && _cpuCopyBufferSize >= size)
            return;

        if (_cpuCopyBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_cpuCopyBuffer);

        _cpuCopyBuffer = Marshal.AllocHGlobal((int)size);
        _cpuCopyBufferSize = size;
    }

    private void Cleanup()
    {
        _renderSuspended = true;
        lock (_renderLock)
        {
            _capture = nint.Zero;
            _presentedFrame?.Dispose();
            _presentedFrame = null;
        }

        _shaderRenderer?.Dispose();
        _shaderRenderer = null;
        _gl = null;
        _recordingFrameWorker.Stop();
        RecordingFrameHandler = null;

        if (_cpuCopyBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_cpuCopyBuffer);
            _cpuCopyBuffer = IntPtr.Zero;
            _cpuCopyBufferSize = 0;
        }
    }
}

internal readonly record struct PipeWireSessionMessage(nint Capture);

internal readonly record struct PipeWireSettingsMessage(
    Stretch Stretch,
    float Brightness,
    float Saturation,
    Color Tint,
    string? ShaderPath,
    double ContentAspectRatio);

[SupportedOSPlatform("linux")]
internal sealed class PipeWireCompositionDrawOperation(Rect bounds, PipeWireCompositionVisualHandler handler) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context) =>
        handler.OnRender(context, Bounds.Size);

    public bool Equals(ICustomDrawOperation? other) => false;
}
