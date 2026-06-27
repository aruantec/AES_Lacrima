using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using AES_Emulation.Linux.API;
using AES_Emulation.Services;
using AES_Emulation.Windows;
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
    private int _lastDestCropLeft = -1;
    private int _lastDestCropRight = -1;
    private readonly SKPaint _paint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    private readonly SKPaint _letterboxPaint = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
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

    private bool _useBackCoverLetterboxFill;
    private bool _enablePillarboxCrop;
    private bool _aggressivePillarboxCrop;
    private string? _captureRomPath;
    private SKImage? _letterboxImage;
    private int _letterboxWidth;
    private int _letterboxHeight;
    private readonly SKPaint _letterboxFallbackPaint = new() { Color = SKColors.Black, Style = SKPaintStyle.Fill };

    private int _cropLeft;
    private int _cropRight;
    private int _pillarboxStableFrames;
    private int _lastDetectedLeft = -1;
    private int _lastDetectedRight = -1;
    private int _targetCropLeft;
    private int _targetCropRight;
    private int _forcePillarboxDetectFrames;
    private int _pillarboxScanCounter;
    private long _pillarboxAnimStartTicks;
    private int _animFromLeft;
    private int _animFromRight;
    private int _animToLeft;
    private int _animToRight;
    private bool _pillarboxAnimActive;
    private bool _pillarboxAnimClosingBars;
    private readonly ArcadePillarboxCropResolver _arcadeCropResolver = new();

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

    internal void SetUiBlocksShaderHotCompile(bool blocked) =>
        _shaderRenderer?.SetUiBlocksHotCompile(blocked);

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
        if (_ownerRef?.TryGetTarget(out var owner) == true &&
            !string.IsNullOrWhiteSpace(owner.CaptureRomPath))
        {
            _captureRomPath = owner.CaptureRomPath;
        }

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
            ReloadArcadeCropResolverState();
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
                var romChanged = !string.Equals(_captureRomPath, settings.CaptureRomPath, StringComparison.OrdinalIgnoreCase);
                var aggressiveChanged = _aggressivePillarboxCrop != settings.AggressivePillarboxCrop;
                var pillarboxEnabledChanged = _enablePillarboxCrop != settings.EnablePillarboxCrop;
                _captureRomPath = settings.CaptureRomPath;
                _enablePillarboxCrop = settings.EnablePillarboxCrop;
                _aggressivePillarboxCrop = settings.AggressivePillarboxCrop;
                if (romChanged || aggressiveChanged || pillarboxEnabledChanged)
                    ReloadArcadeCropResolverState();
                else
                    EnforceLockedArcadeCrop();
                if (!string.Equals(_shaderPath, settings.ShaderPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Only update the path here. Reload happens on the render thread via
                    // LinuxCompositionShaderRenderer.SetShaderPath so we never delete GL
                    // textures while Skia may still be drawing from them.
                    _shaderPath = settings.ShaderPath;
                }
                _rectDirty = true;
                lock (_renderLock)
                {
                    if (_lastFrameWidth > 0 && _lastFrameHeight > 0)
                        EnsureDestRect(_lastFrameWidth, _lastFrameHeight);
                }
                NotifyCompositor();
                break;
            case ArcadePillarboxApplyLockMessage applyLock:
                ApplyArcadePillarboxLockMessage(applyLock);
                break;
            case ArcadePillarboxUnlockMessage unlock:
                UnlockArcadePillarboxCrop(unlock.RomPath);
                break;
            case PipeWireLetterboxMessage letterbox:
                ApplyLetterboxMessage(letterbox);
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

        if (_useBackCoverLetterboxFill)
            DrawLetterboxBackground(canvas, canvasSize);

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
        int frameWidth;
        int frameHeight;
        lock (_renderLock)
        {
            if (_rectDirty && _lastFrameWidth > 0 && _lastFrameHeight > 0)
                EnsureDestRect(_lastFrameWidth, _lastFrameHeight);

            frame = _presentedFrame;
            destRect = MapDestRectToCanvas(_destRect, canvasSize);
            frameWidth = _lastFrameWidth;
            frameHeight = _lastFrameHeight;
        }

        if (frame == null)
            return;

        if (ShouldAutoCropPillarboxes)
        {
            StepPillarboxCropAnimation();
            if (_arcadeCropResolver.IsLocked)
                EnforceLockedArcadeCrop(frameWidth);
            else if (ShouldScanPillarboxThisFrame())
                AutoDetectPillarboxes(frame);
        }
        else
        {
            ClearPillarboxCropIfInactive();
        }

        lock (_renderLock)
        {
            if (_rectDirty && frameWidth > 0 && frameHeight > 0)
                EnsureDestRect(frameWidth, frameHeight);

            destRect = MapDestRectToCanvas(_destRect, canvasSize);
            if (ShouldAutoCropPillarboxes && frameWidth > 0 && frameHeight > 0)
            {
                var contentW = Math.Max(1f, frameWidth - Math.Max(0, _cropLeft) - Math.Max(0, _cropRight));
                destRect = BuildSnappedFullHeightDestRect(
                    (float)canvasSize.Width,
                    (float)canvasSize.Height,
                    contentW,
                    frameHeight);
            }
        }

        var cropLeft = Math.Max(0, _cropLeft);
        var cropRight = Math.Max(0, _cropRight);
        if (cropLeft + cropRight >= frame.Width)
            cropLeft = cropRight = 0;

        if (_gl != null &&
            grContext != null &&
            !string.IsNullOrWhiteSpace(_shaderPath))
        {
            _shaderRenderer ??= new LinuxCompositionShaderRenderer();
            _shaderRenderer.SetShaderPath(_gl, _shaderPath);
            try
            {
                if (_shaderRenderer.TryDraw(canvas, grContext, _gl, frame, destRect, _brightness, _saturation, _tint, cropLeft, cropRight))
                    return;
            }
            catch (Exception ex)
            {
                Log.Warn("Linux composition shader draw failed; falling back to unshaded frame.", ex);
                try { grContext.ResetContext(); } catch { /* ignored */ }
            }
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

        var srcRect = new SKRect(cropLeft, 0, frame.Width - cropRight, frame.Height);
        canvas.DrawImage(image, srcRect, destRect, _paint);
    }

    private bool ShouldAutoCropPillarboxes =>
        _enablePillarboxCrop || (_useBackCoverLetterboxFill && _letterboxImage != null);

    private PillarboxDetectionProfile PillarboxDetectionProfile =>
        _aggressivePillarboxCrop ? PillarboxDetectionProfile.AggressiveArcade : PillarboxDetectionProfile.Standard;

    private void ApplyLetterboxMessage(PipeWireLetterboxMessage letterbox)
    {
        _useBackCoverLetterboxFill = letterbox.Enabled;
        _letterboxImage?.Dispose();
        _letterboxImage = null;
        _letterboxWidth = 0;
        _letterboxHeight = 0;

        if (!letterbox.Enabled)
        {
            ResetPillarboxDetectionState();
            return;
        }

        if (letterbox.Pixels == null ||
            letterbox.Width <= 0 ||
            letterbox.Height <= 0)
        {
            ResetPillarboxDetectionState();
            return;
        }

        var info = new SKImageInfo(letterbox.Width, letterbox.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _letterboxImage = SKImage.FromPixelCopy(info, letterbox.Pixels);
        _letterboxWidth = letterbox.Width;
        _letterboxHeight = letterbox.Height;
        EnforceLockedArcadeCrop();
    }

    internal bool TryGetPillarboxCrop(out int left, out int right, out int frameWidth)
    {
        left = _cropLeft;
        right = _cropRight;
        frameWidth = _lastFrameWidth;
        if (frameWidth <= 0 && _presentedFrame != null)
            frameWidth = _presentedFrame.Width;

        return frameWidth > 0 && (left > 0 || right > 0);
    }

    internal void ReloadArcadeLockedPillarboxCrop() =>
        ApplyArcadePillarboxLockMessage(new ArcadePillarboxApplyLockMessage { RomPath = _captureRomPath });

    private void DrawLetterboxBackground(SKCanvas canvas, Size canvasSize)
    {
        if (!_useBackCoverLetterboxFill)
            return;

        var viewW = (float)canvasSize.Width;
        var viewH = (float)canvasSize.Height;
        if (viewW <= 0 || viewH <= 0)
            return;

        var viewRect = new SKRect(0, 0, viewW, viewH);
        if (_letterboxImage != null && _letterboxWidth > 0 && _letterboxHeight > 0)
        {
            var imageW = _letterboxImage.Width;
            var imageH = _letterboxImage.Height;
            var srcRect = new SKRect(0, 0, imageW, imageH);
            var dest = CalculateUniformToFillRect(viewW, viewH, imageW, imageH);
            canvas.Save();
            canvas.ClipRect(viewRect);
            canvas.DrawImage(_letterboxImage, srcRect, dest, _letterboxPaint);
            canvas.Restore();
            return;
        }

        canvas.DrawRect(viewRect, _letterboxFallbackPaint);
    }

    private static SKRect CalculateUniformToFillRect(float viewW, float viewH, float imageW, float imageH)
    {
        var viewAspect = viewW / viewH;
        var imageAspect = imageW / imageH;

        if (imageAspect > viewAspect)
        {
            var height = viewH;
            var width = viewH * imageAspect;
            var x = (viewW - width) / 2f;
            return new SKRect(x, 0, x + width, height);
        }

        var fillWidth = viewW;
        var fillHeight = viewW / imageAspect;
        var y = (viewH - fillHeight) / 2f;
        return new SKRect(0, y, fillWidth, y + fillHeight);
    }

    private void ClearPillarboxCropIfInactive()
    {
        if (ShouldAutoCropPillarboxes)
            return;

        if (_cropLeft == 0 && _cropRight == 0 && !_pillarboxAnimActive)
            return;

        _cropLeft = 0;
        _cropRight = 0;
        _targetCropLeft = 0;
        _targetCropRight = 0;
        _pillarboxAnimActive = false;
        _rectDirty = true;
    }

    private bool ShouldScanPillarboxThisFrame()
    {
        if (_arcadeCropResolver.IsLocked)
            return false;

        if (_forcePillarboxDetectFrames > 0)
        {
            _forcePillarboxDetectFrames--;
            return true;
        }

        var interval = _useBackCoverLetterboxFill ? 8 : 15;
        return ++_pillarboxScanCounter % interval == 0;
    }

    private void ResetPillarboxDetectionState() => ReloadArcadeCropResolverState();

    private void ReloadArcadeCropResolverState()
    {
        ApplyArcadePillarboxLockMessage(new ArcadePillarboxApplyLockMessage { RomPath = _captureRomPath });
    }

    private void ApplyArcadePillarboxLockMessage(ArcadePillarboxApplyLockMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.RomPath))
            _captureRomPath = message.RomPath;

        if (message.Left > 0 || message.Right > 0)
            _arcadeCropResolver.SetLockedCrop(message.Left, message.Right, message.FrameWidth);
        else
            _arcadeCropResolver.Reset(_captureRomPath);

        if (!_arcadeCropResolver.IsLocked)
        {
            ResetLivePillarboxDetectionState();
            return;
        }

        _forcePillarboxDetectFrames = 0;
        _pillarboxStableFrames = 6;
        _lastDetectedLeft = -1;
        _lastDetectedRight = -1;
        _pillarboxAnimActive = false;
        EnforceLockedArcadeCrop(message.FrameWidth);
        _rectDirty = true;
    }

    private void UnlockArcadePillarboxCrop(string? romPath)
    {
        if (!string.IsNullOrWhiteSpace(romPath))
            _captureRomPath = romPath;

        _arcadeCropResolver.ClearLock();
        ResetLivePillarboxDetectionState();
    }

    private void ResetLivePillarboxDetectionState()
    {
        _cropLeft = 0;
        _cropRight = 0;
        _targetCropLeft = 0;
        _targetCropRight = 0;
        _pillarboxStableFrames = 0;
        _lastDetectedLeft = -1;
        _lastDetectedRight = -1;
        _forcePillarboxDetectFrames = 18;
        _pillarboxAnimActive = false;
        _rectDirty = true;
    }

    private void EnforceLockedArcadeCrop(int frameWidth = 0)
    {
        if (!_arcadeCropResolver.IsLocked || !ShouldAutoCropPillarboxes)
            return;

        frameWidth = frameWidth > 0 ? frameWidth : _lastFrameWidth;
        if (frameWidth <= 0 && _presentedFrame != null)
            frameWidth = _presentedFrame.Width;

        if (frameWidth <= 0)
            return;

        if (!_arcadeCropResolver.TryGetLockedCrop(frameWidth, out var left, out var right))
            return;

        ApplyCropImmediate(left, right);
    }

    private void ApplyCropImmediate(int left, int right)
    {
        _targetCropLeft = left;
        _targetCropRight = right;
        _cropLeft = left;
        _cropRight = right;
        _lastDetectedLeft = left;
        _lastDetectedRight = right;
        _pillarboxStableFrames = 6;
        _pillarboxAnimActive = false;
        _rectDirty = true;
    }

    private unsafe void AutoDetectPillarboxes(SKBitmap frame)
    {
        if (_arcadeCropResolver.IsLocked)
        {
            EnforceLockedArcadeCrop(frame.Width);
            return;
        }

        if (!ShouldAutoCropPillarboxes || frame.Width < 80 || frame.Height < 80)
        {
            ClearPillarboxCropIfInactive();
            return;
        }

        var pixmap = frame.PeekPixels();
        if (pixmap == null)
            return;

        var w = frame.Width;
        var h = frame.Height;
        var stride = pixmap.RowBytes;
        var span = pixmap.GetPixelSpan();
        if (span.Length < stride * h)
            return;

        fixed (byte* basePtr = span)
        {
            var readOnly = new ReadOnlySpan<byte>(basePtr, stride * h);
            PillarboxBarDetector.DetectInsets(readOnly, w, h, stride, out var detectedLeft, out var detectedRight, out _, out _, PillarboxDetectionProfile);
            UpdatePillarboxCropTargets(detectedLeft, detectedRight, w);
        }
    }

    private void UpdatePillarboxCropTargets(int detectedLeft, int detectedRight, int frameWidth)
    {
        if (_arcadeCropResolver.IsLocked)
        {
            EnforceLockedArcadeCrop(frameWidth);
            return;
        }

        var (left, right) = _aggressivePillarboxCrop && frameWidth > 0
            ? _arcadeCropResolver.Resolve(frameWidth, detectedLeft, detectedRight)
            : (detectedLeft, detectedRight);

        if (left == _lastDetectedLeft && right == _lastDetectedRight)
            _pillarboxStableFrames = Math.Min(_pillarboxStableFrames + 1, 60);
        else
        {
            _lastDetectedLeft = left;
            _lastDetectedRight = right;
            _pillarboxStableFrames = 1;
        }

        const int requiredStableFrames = 6;
        if (_pillarboxStableFrames < requiredStableFrames)
            return;

        if (left == _targetCropLeft && right == _targetCropRight)
            return;

        _targetCropLeft = left;
        _targetCropRight = right;
        BeginPillarboxCropAnimation(left, right);
    }

    private void BeginPillarboxCropAnimation(int toLeft, int toRight)
    {
        if (_cropLeft == toLeft && _cropRight == toRight)
        {
            _pillarboxAnimActive = false;
            return;
        }

        _animFromLeft = _cropLeft;
        _animFromRight = _cropRight;
        _animToLeft = toLeft;
        _animToRight = toRight;
        _pillarboxAnimClosingBars = PillarboxCropAnimator.IsClosingBars(_animFromLeft, _animFromRight, toLeft, toRight);
        _pillarboxAnimStartTicks = Stopwatch.GetTimestamp();
        _pillarboxAnimActive = true;
    }

    private void StepPillarboxCropAnimation()
    {
        if (_arcadeCropResolver.IsLocked)
        {
            EnforceLockedArcadeCrop();
            return;
        }

        if (!_pillarboxAnimActive)
        {
            if (_cropLeft != _targetCropLeft || _cropRight != _targetCropRight)
                BeginPillarboxCropAnimation(_targetCropLeft, _targetCropRight);
            return;
        }

        var elapsedSeconds = (Stopwatch.GetTimestamp() - _pillarboxAnimStartTicks) / (double)Stopwatch.Frequency;
        var linearT = Math.Clamp(elapsedSeconds / PillarboxCropAnimator.Duration.TotalSeconds, 0, 1);
        var easedT = _pillarboxAnimClosingBars
            ? PillarboxCropAnimator.CubicEaseIn(linearT)
            : PillarboxCropAnimator.CubicEaseOut(linearT);

        var prevLeft = _cropLeft;
        var prevRight = _cropRight;
        _cropLeft = PillarboxCropAnimator.Lerp(_animFromLeft, _animToLeft, easedT);
        _cropRight = PillarboxCropAnimator.Lerp(_animFromRight, _animToRight, easedT);

        if (linearT >= 1)
        {
            _cropLeft = _animToLeft;
            _cropRight = _animToRight;
            _pillarboxAnimActive = false;
        }

        if (_cropLeft != prevLeft || _cropRight != prevRight)
            _rectDirty = true;
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
                    if (_arcadeCropResolver.IsLocked)
                        EnforceLockedArcadeCrop(_presentedFrame.Width);
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
        var cropLeft = Math.Max(0, _cropLeft);
        var cropRight = Math.Max(0, _cropRight);
        var contentWidth = Math.Max(1, frameWidth - cropLeft - cropRight);

        if (!_rectDirty &&
            _lastFrameWidth == frameWidth &&
            _lastFrameHeight == frameHeight &&
            _lastDestCropLeft == cropLeft &&
            _lastDestCropRight == cropRight &&
            Math.Abs(_lastContentAspectRatio - _contentAspectRatio) < 0.0001)
        {
            return;
        }

        _lastFrameWidth = frameWidth;
        _lastFrameHeight = frameHeight;
        _lastDestCropLeft = cropLeft;
        _lastDestCropRight = cropRight;
        _lastContentAspectRatio = _contentAspectRatio;

        var layoutWidth = (float)contentWidth;
        var layoutHeight = (float)frameHeight;
        // When revealing back-cover pillars, use cropped pixel aspect — not handler window aspect.
        if (!ShouldAutoCropPillarboxes && _contentAspectRatio > 0 && layoutWidth > 0)
            layoutHeight = layoutWidth / (float)_contentAspectRatio;

        _destRect = ShouldAutoCropPillarboxes
            ? BuildSnappedFullHeightDestRect(_visualSize.X, _visualSize.Y, layoutWidth, layoutHeight)
            : CalculateAspectRect(_visualSize.X, _visualSize.Y, layoutWidth, layoutHeight);
        _rectDirty = false;
    }

    private static SKRect BuildFullHeightDestRect(float viewW, float viewH, float frameW, float frameH)
    {
        if (viewW <= 0 || viewH <= 0 || frameW <= 0 || frameH <= 0)
            return new SKRect(0, 0, Math.Max(0, viewW), Math.Max(0, viewH));

        var width = viewH * (frameW / frameH);
        return new SKRect((viewW - width) * 0.5f, 0, (viewW + width) * 0.5f, viewH);
    }

    private static SKRect BuildSnappedFullHeightDestRect(float viewW, float viewH, float frameW, float frameH)
    {
        var rect = BuildFullHeightDestRect(viewW, viewH, frameW, frameH);
        rect.Top = 0;
        rect.Bottom = viewH;
        if (rect.Left < 0)
            rect.Left = 0;
        if (rect.Right > viewW)
            rect.Right = viewW;
        return rect;
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
        _letterboxImage?.Dispose();
        _letterboxImage = null;
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
    double ContentAspectRatio,
    bool EnablePillarboxCrop = false,
    bool AggressivePillarboxCrop = false,
    string? CaptureRomPath = null);

internal sealed class PipeWireLetterboxMessage
{
    public bool Enabled { get; init; }
    public byte[]? Pixels { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

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
