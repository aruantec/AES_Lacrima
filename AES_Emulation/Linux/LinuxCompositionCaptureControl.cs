using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using AES_Controls;
using AES_Emulation.Linux.API;
using AES_Emulation.Services;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public class LinuxCompositionCaptureControl : Control, IScaleExclusionRenderTarget
{
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<int> CompositorProcessIdProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, int>(nameof(CompositorProcessId));

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, string?>(nameof(TargetWindowTitleHint), "gamescope");

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly StyledProperty<double> CaptureWindowAspectRatioProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, double>(nameof(CaptureWindowAspectRatio), 0);

    public static readonly StyledProperty<bool> UseBackCoverLetterboxFillProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(UseBackCoverLetterboxFill), false);

    public static readonly StyledProperty<Bitmap?> LetterboxBitmapProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, Bitmap?>(nameof(LetterboxBitmap));

    public static readonly StyledProperty<string?> CaptureRomPathProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, string?>(nameof(CaptureRomPath));

    public static readonly StyledProperty<bool> EnablePillarboxCropProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(EnablePillarboxCrop), false);

    public static readonly StyledProperty<bool> AggressivePillarboxCropProperty =
        AvaloniaProperty.Register<LinuxCompositionCaptureControl, bool>(nameof(AggressivePillarboxCrop), false);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, string>(
            nameof(StatusText),
            o => o.StatusText);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, bool> IsDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, bool>(
            nameof(IsDirectCompositionActive),
            o => o.IsDirectCompositionActive);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, bool> IsCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, bool>(
            nameof(IsCaptureInitializing),
            o => o.IsCaptureInitializing);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, bool> IsCapturePresentingFramesProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, bool>(
            nameof(IsCapturePresentingFrames),
            o => o.IsCapturePresentingFrames);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, double> FpsProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, double>(
            nameof(Fps),
            o => o.Fps);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, double> FrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, double>(
            nameof(FrameTimeMs),
            o => o.FrameTimeMs);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, string> GpuRendererProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, string>(
            nameof(GpuRenderer),
            o => o.GpuRenderer);

    public static readonly DirectProperty<LinuxCompositionCaptureControl, string> GpuVendorProperty =
        AvaloniaProperty.RegisterDirect<LinuxCompositionCaptureControl, string>(
            nameof(GpuVendor),
            o => o.GpuVendor);

    private readonly DispatcherTimer _statusTimer;
    private CompositionCustomVisual? _visual;
    private PipeWireCompositionVisualHandler? _handler;
    private DispatcherTimer? _fallbackRenderTimer;
    private IntPtr _capture;
    private bool _isAttached;
    private bool _portalSessionRequested;
    private int _lastCompositorProcessId;
    private IntPtr _lastTargetHwnd;
    private int _lastHostWidth = -1;
    private int _lastHostHeight = -1;
    private bool _hasAppliedRenderOptions;
    private double _lastBrightness = -1;
    private double _lastSaturation = -1;
    private Color _lastTint = Colors.Transparent;
    private int _lastStretch = -1;
    private int _lastCropLeft = -1;
    private int _lastCropTop = -1;
    private int _lastCropRight = -1;
    private int _lastCropBottom = -1;
    private bool _lastHideTargetWindowAfterCaptureStarts;
    private bool? _lastDisableVSync;

    private string _statusText = "Idle";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private bool _isCapturePresentingFrames;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";

    public LinuxCompositionCaptureControl()
    {
        ClipToBounds = true;
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => RefreshStatus());
    }

    public IntPtr TargetHwnd
    {
        get => GetValue(TargetHwndProperty);
        set => SetValue(TargetHwndProperty, value);
    }

    public int CompositorProcessId
    {
        get => GetValue(CompositorProcessIdProperty);
        set => SetValue(CompositorProcessIdProperty, value);
    }

    public string? TargetWindowTitleHint
    {
        get => GetValue(TargetWindowTitleHintProperty);
        set => SetValue(TargetWindowTitleHintProperty, value);
    }

    public bool RequestStopSession
    {
        get => GetValue(RequestStopSessionProperty);
        set => SetValue(RequestStopSessionProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public double Brightness
    {
        get => GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public double Saturation
    {
        get => GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    public Color ColorTint
    {
        get => GetValue(ColorTintProperty);
        set => SetValue(ColorTintProperty, value);
    }

    public bool DisableVSync
    {
        get => GetValue(DisableVSyncProperty);
        set => SetValue(DisableVSyncProperty, value);
    }

    public string? ShaderPath
    {
        get => GetValue(ShaderPathProperty);
        set => SetValue(ShaderPathProperty, value);
    }

    public bool ClearShaderWhenPathEmpty
    {
        get => GetValue(ClearShaderWhenPathEmptyProperty);
        set => SetValue(ClearShaderWhenPathEmptyProperty, value);
    }

    public bool ForceUseTargetClientArea
    {
        get => GetValue(ForceUseTargetClientAreaProperty);
        set => SetValue(ForceUseTargetClientAreaProperty, value);
    }

    public bool HideTargetWindowAfterCaptureStarts
    {
        get => GetValue(HideTargetWindowAfterCaptureStartsProperty);
        set => SetValue(HideTargetWindowAfterCaptureStartsProperty, value);
    }

    public int ClientAreaCropLeftInset
    {
        get => GetValue(ClientAreaCropLeftInsetProperty);
        set => SetValue(ClientAreaCropLeftInsetProperty, value);
    }

    public int ClientAreaCropTopInset
    {
        get => GetValue(ClientAreaCropTopInsetProperty);
        set => SetValue(ClientAreaCropTopInsetProperty, value);
    }

    public int ClientAreaCropRightInset
    {
        get => GetValue(ClientAreaCropRightInsetProperty);
        set => SetValue(ClientAreaCropRightInsetProperty, value);
    }

    public int ClientAreaCropBottomInset
    {
        get => GetValue(ClientAreaCropBottomInsetProperty);
        set => SetValue(ClientAreaCropBottomInsetProperty, value);
    }

    public double CaptureWindowAspectRatio
    {
        get => GetValue(CaptureWindowAspectRatioProperty);
        set => SetValue(CaptureWindowAspectRatioProperty, value);
    }

    public bool UseBackCoverLetterboxFill
    {
        get => GetValue(UseBackCoverLetterboxFillProperty);
        set => SetValue(UseBackCoverLetterboxFillProperty, value);
    }

    public Bitmap? LetterboxBitmap
    {
        get => GetValue(LetterboxBitmapProperty);
        set => SetValue(LetterboxBitmapProperty, value);
    }

    public string? CaptureRomPath
    {
        get => GetValue(CaptureRomPathProperty);
        set => SetValue(CaptureRomPathProperty, value);
    }

    public bool TryGetPillarboxCrop(out int left, out int right, out int frameWidth)
    {
        left = right = frameWidth = 0;
        return _handler?.TryGetPillarboxCrop(out left, out right, out frameWidth) == true;
    }

    public void ApplyArcadeLockedPillarboxCrop(ArcadePillarboxApplyLockMessage message)
    {
        SendHandlerMessage(message);
    }

    public void UnlockArcadePillarboxCrop(ArcadePillarboxUnlockMessage message)
    {
        SendHandlerMessage(message);
    }

    public void ReloadArcadeLockedPillarboxCrop()
    {
        SendHandlerMessage(new ArcadePillarboxApplyLockMessage { RomPath = CaptureRomPath });
    }

    public bool EnablePillarboxCrop
    {
        get => GetValue(EnablePillarboxCropProperty);
        set => SetValue(EnablePillarboxCropProperty, value);
    }

    public bool AggressivePillarboxCrop
    {
        get => GetValue(AggressivePillarboxCropProperty);
        set => SetValue(AggressivePillarboxCropProperty, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public bool IsDirectCompositionActive
    {
        get => _isDirectCompositionActive;
        private set => SetAndRaise(IsDirectCompositionActiveProperty, ref _isDirectCompositionActive, value);
    }

    public bool IsCaptureInitializing
    {
        get => _isCaptureInitializing;
        private set => SetAndRaise(IsCaptureInitializingProperty, ref _isCaptureInitializing, value);
    }

    public bool IsCapturePresentingFrames
    {
        get => _isCapturePresentingFrames;
        private set => SetAndRaise(IsCapturePresentingFramesProperty, ref _isCapturePresentingFrames, value);
    }

    public double Fps
    {
        get => _fps;
        internal set => SetAndRaise(FpsProperty, ref _fps, value);
    }

    public double FrameTimeMs
    {
        get => _frameTimeMs;
        internal set => SetAndRaise(FrameTimeMsProperty, ref _frameTimeMs, value);
    }

    public string GpuRenderer
    {
        get => _gpuRenderer;
        private set => SetAndRaise(GpuRendererProperty, ref _gpuRenderer, value);
    }

    public string GpuVendor
    {
        get => _gpuVendor;
        private set => SetAndRaise(GpuVendorProperty, ref _gpuVendor, value);
    }

    public void ForwardFocusToTarget()
    {
        if (_capture != IntPtr.Zero)
            LinuxCaptureBridge.aes_linux_capture_forward_focus(_capture);
    }

    public void ConfigureGameplayRecording(
        Action<byte[], int, int>? frameHandler,
        int targetFps,
        Services.GameplayRecordingResolutionCap resolutionCap = Services.GameplayRecordingResolutionCap.P1080)
    {
        if (_handler == null)
            return;

        _handler.SetRecordingFrameHandler(frameHandler);
        _handler.SetRecordingTargetFps(targetFps);
        _handler.SetRecordingResolutionCap(resolutionCap);
        _handler.SetRecordingWorkerActive(frameHandler != null);
    }

    public void RefreshExclusionRenderSize() => UpdateHandlerSize();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _handler ??= new PipeWireCompositionVisualHandler();
        _handler.SetOwner(this);
        _handler.EnsureRenderer();

        // Avalonia's composition custom visual render path on Linux does not provide
        // ISkiaSharpApiLeaseFeature, so frames never reach Skia. Present through the
        // owner Custom draw path instead (same approach as WGC fallback on Windows).
        _visual = null;
        _handler.ConfigureInvalidation(true);

        EnsureCaptureSession();
        UpdateHandlerSize();
        UpdateHandlerSettings();
        UpdateFallbackRenderLoop();
        _statusTimer.Start();
        IsDirectCompositionActive = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _statusTimer.Stop();
        _fallbackRenderTimer?.Stop();

        _handler?.SuspendRendering();
        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!);
            ElementComposition.SetElementChildVisual(this, null!);
            _visual = null;
        }
        else
        {
            _handler?.OnMessage(null);
        }

        if (!LinuxEmulationLifecycle.IsApplicationExitInProgress)
            _handler?.TryWaitForRenderIdle(TimeSpan.FromMilliseconds(250));

        ResetCaptureNative();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateHostBounds();
        UpdateHandlerSize();
        return arranged;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_handler == null || _capture == IntPtr.Zero)
            return;

        var drawSize = Bounds.Size;
        if (drawSize.Width <= 0 || drawSize.Height <= 0)
            return;

        context.Custom(new PipeWireCompositionDrawOperation(new Rect(drawSize), _handler));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHandlerSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
            UpdateHostBounds();
        else if (change.Property == TargetHwndProperty ||
                 change.Property == CompositorProcessIdProperty ||
                 change.Property == TargetWindowTitleHintProperty)
            EnsureCaptureSession();
        else if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
        {
            SuspendPresentation();
            SetCurrentValue(RequestStopSessionProperty, false);
        }
        else if (change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == DisableVSyncProperty ||
                 change.Property == ShaderPathProperty ||
                 change.Property == ClearShaderWhenPathEmptyProperty ||
                 change.Property == HideTargetWindowAfterCaptureStartsProperty ||
                 change.Property == ForceUseTargetClientAreaProperty ||
                 change.Property == ClientAreaCropLeftInsetProperty ||
                 change.Property == ClientAreaCropTopInsetProperty ||
                 change.Property == ClientAreaCropRightInsetProperty ||
                 change.Property == ClientAreaCropBottomInsetProperty)
            ApplyRenderOptions();
        else if (change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == DisableVSyncProperty ||
                 change.Property == ShaderPathProperty ||
                 change.Property == ClearShaderWhenPathEmptyProperty ||
                 change.Property == CaptureWindowAspectRatioProperty)
            UpdateHandlerSettings();
        else if (change.Property == UseBackCoverLetterboxFillProperty ||
                 change.Property == LetterboxBitmapProperty)
        {
            SendLetterboxUpdate();
            UpdateHandlerSettings();
        }
        else if (change.Property == CaptureRomPathProperty)
            UpdateHandlerSettings();
        else if (change.Property == EnablePillarboxCropProperty ||
                 change.Property == AggressivePillarboxCropProperty)
            UpdateHandlerSettings();
    }

    private void EnsureCaptureSession()
    {
        if (!OperatingSystem.IsLinux())
        {
            StatusText = "Linux composition capture is Linux-only";
            return;
        }

        if (!_isAttached)
            return;

        if (TargetHwnd == IntPtr.Zero && CompositorProcessId <= 0)
        {
            if (_capture == IntPtr.Zero)
                StatusText = "Waiting for gamescope";
            return;
        }

        if (!EnsureHeadlessCaptureCreated())
            return;

        ApplyRenderOptions();

        if (TargetHwnd != IntPtr.Zero)
        {
            if (_lastTargetHwnd == TargetHwnd && _portalSessionRequested)
                return;

            _lastTargetHwnd = TargetHwnd;
            _lastCompositorProcessId = CompositorProcessId;
            _portalSessionRequested = true;
            IsCaptureInitializing = true;
            SendArcadeLockedCropFromMetadata();
            SendHandlerMessage(new PipeWireSessionMessage(_capture));
            LinuxCaptureBridge.aes_linux_capture_set_target_window(_capture, TargetHwnd);
            UpdateFallbackRenderLoop();
        }
        else if (CompositorProcessId > 0)
        {
            if (_portalSessionRequested && _lastCompositorProcessId == CompositorProcessId)
                return;

            _lastCompositorProcessId = CompositorProcessId;
            _lastTargetHwnd = IntPtr.Zero;
            _portalSessionRequested = true;
            IsCaptureInitializing = true;
            SendArcadeLockedCropFromMetadata();
            SendHandlerMessage(new PipeWireSessionMessage(_capture));
            LinuxCaptureBridge.aes_linux_capture_set_use_pipewire(_capture, 1);
            LinuxCaptureBridge.aes_linux_capture_set_target(
                _capture,
                CompositorProcessId,
                TargetWindowTitleHint ?? "gamescope");
            UpdateFallbackRenderLoop();
        }
        else
        {
            StatusText = "Waiting for gamescope";
            return;
        }

        RefreshStatus();
        InvalidateVisual();
    }

    private bool EnsureHeadlessCaptureCreated()
    {
        if (_capture != IntPtr.Zero)
            return true;

        _capture = LinuxCaptureBridge.aes_linux_capture_create_headless();
        if (_capture == IntPtr.Zero)
        {
            StatusText = "Linux composition capture creation failed";
            return false;
        }

        _hasAppliedRenderOptions = false;
        SendArcadeLockedCropFromMetadata();
        SendHandlerMessage(new PipeWireSessionMessage(_capture));
        return true;
    }

    private void SuspendPresentation()
    {
        _fallbackRenderTimer?.Stop();
        _handler?.SuspendRendering();
        if (_capture != IntPtr.Zero)
            LinuxCaptureBridge.aes_linux_capture_stop(_capture);
        _portalSessionRequested = false;
        _lastCompositorProcessId = 0;
        _lastTargetHwnd = IntPtr.Zero;
        IsCaptureInitializing = false;
        IsCapturePresentingFrames = false;
        IsDirectCompositionActive = false;
        Fps = 0;
        FrameTimeMs = 0;
        StatusText = "Capture suspended";
    }

    private void ResetCaptureNative()
    {
        SuspendPresentation();
        _handler?.OnMessage(null);

        if (_capture == IntPtr.Zero)
            return;

        var capture = _capture;
        _capture = IntPtr.Zero;
        _hasAppliedRenderOptions = false;

        if (LinuxEmulationLifecycle.IsApplicationExitInProgress)
            return;

        _ = Task.Run(() => LinuxCaptureBridge.aes_linux_capture_destroy(capture));
    }

    private void ApplyRenderOptions()
    {
        if (_capture == IntPtr.Zero)
            return;

        LinuxCaptureBridge.aes_linux_capture_set_use_pipewire(_capture, 1);

        var stretch = MapStretch(Stretch);

        if (!_hasAppliedRenderOptions || _lastStretch != stretch)
        {
            LinuxCaptureBridge.aes_linux_capture_set_stretch(_capture, stretch);
            _lastStretch = stretch;
        }

        if (!_hasAppliedRenderOptions ||
            Math.Abs(_lastBrightness - Brightness) > 0.0001 ||
            Math.Abs(_lastSaturation - Saturation) > 0.0001 ||
            _lastTint != ColorTint)
        {
            LinuxCaptureBridge.aes_linux_capture_set_render_options(
                _capture,
                (float)Brightness,
                (float)Saturation,
                ColorTint.R / 255f,
                ColorTint.G / 255f,
                ColorTint.B / 255f,
                ColorTint.A / 255f);
            _lastBrightness = Brightness;
            _lastSaturation = Saturation;
            _lastTint = ColorTint;
        }

        if (!_hasAppliedRenderOptions ||
            _lastCropLeft != ClientAreaCropLeftInset ||
            _lastCropTop != ClientAreaCropTopInset ||
            _lastCropRight != ClientAreaCropRightInset ||
            _lastCropBottom != ClientAreaCropBottomInset)
        {
            LinuxCaptureBridge.aes_linux_capture_set_crop_insets(
                _capture,
                ClientAreaCropLeftInset,
                ClientAreaCropTopInset,
                ClientAreaCropRightInset,
                ClientAreaCropBottomInset);
            _lastCropLeft = ClientAreaCropLeftInset;
            _lastCropTop = ClientAreaCropTopInset;
            _lastCropRight = ClientAreaCropRightInset;
            _lastCropBottom = ClientAreaCropBottomInset;
        }

        if (!_hasAppliedRenderOptions || _lastHideTargetWindowAfterCaptureStarts != HideTargetWindowAfterCaptureStarts)
        {
            LinuxCaptureBridge.aes_linux_capture_set_capture_behavior(
                _capture,
                HideTargetWindowAfterCaptureStarts ? 1 : 0);
            _lastHideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
        }

        if (!_hasAppliedRenderOptions || _lastDisableVSync != DisableVSync)
        {
            LinuxCaptureBridge.aes_linux_capture_set_disable_vsync(_capture, DisableVSync ? 1 : 0);
            _lastDisableVSync = DisableVSync;
        }

        _hasAppliedRenderOptions = true;
        UpdateHandlerSettings();
    }

    private void RefreshStatus()
    {
        if (_capture == IntPtr.Zero)
            return;

        StatusText = LinuxCaptureBridge.GetStatusText(_capture);
        GpuRenderer = LinuxCaptureBridge.GetGpuRenderer(_capture);
        GpuVendor = LinuxCaptureBridge.GetGpuVendor(_capture);
        IsCaptureInitializing = LinuxCaptureBridge.IsCaptureInitializing(_capture);

        if (LinuxCaptureBridge.IsCaptureActive(_capture))
        {
            IsDirectCompositionActive = true;
            IsCaptureInitializing = false;
            Fps = LinuxCaptureBridge.aes_linux_capture_get_fps(_capture);
            FrameTimeMs = LinuxCaptureBridge.aes_linux_capture_get_frame_time_ms(_capture);
        }

        if (_handler?.HasPresentedFrame == true)
            NotifyCaptureFramesStarted();
    }

    private void UpdateHostBounds()
    {
        if (_capture == IntPtr.Zero)
            return;

        var renderSize = Bounds.Size;
        var width = Math.Max(1, (int)Math.Round(renderSize.Width));
        var height = Math.Max(1, (int)Math.Round(renderSize.Height));
        if (width == _lastHostWidth && height == _lastHostHeight)
            return;

        _lastHostWidth = width;
        _lastHostHeight = height;
        LinuxCaptureBridge.aes_linux_capture_set_host_size(_capture, width, height);
    }

    private void UpdateHandlerSize()
    {
        // Match WGC owner-fallback: aspect rect uses layout bounds; Custom() uses the same size.
        var renderSize = Bounds.Size;

        var size = new Vector2((float)Math.Max(1, renderSize.Width), (float)Math.Max(1, renderSize.Height));
        if (_visual != null)
            _visual.Size = size;

        SendHandlerMessage(size);
    }

    private void UpdateHandlerSettings()
    {
        var shaderPath = string.IsNullOrWhiteSpace(ShaderPath) && ClearShaderWhenPathEmpty
            ? string.Empty
            : (ShaderPath ?? string.Empty);

        SendHandlerMessage(new PipeWireSettingsMessage(
            Stretch,
            (float)Brightness,
            (float)Saturation,
            ColorTint,
            shaderPath,
            CaptureWindowAspectRatio,
            EnablePillarboxCrop,
            AggressivePillarboxCrop,
            CaptureRomPath));

        SendArcadeLockedCropFromMetadata();
        SendLetterboxUpdate();
    }

    private void SendLetterboxUpdate()
    {
        if (!UseBackCoverLetterboxFill)
        {
            SendHandlerMessage(new PipeWireLetterboxMessage { Enabled = false });
            return;
        }

        var bitmap = LetterboxBitmap;
        if (bitmap == null)
        {
            SendHandlerMessage(new PipeWireLetterboxMessage { Enabled = true });
            return;
        }

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            SendHandlerMessage(new PipeWireLetterboxMessage { Enabled = true });
            return;
        }

        var stride = width * 4;
        var bufferSize = height * stride;
        var pixels = new byte[bufferSize];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bufferSize, stride);
        }
        finally
        {
            handle.Free();
        }

        SendHandlerMessage(new PipeWireLetterboxMessage
        {
            Enabled = true,
            Pixels = pixels,
            Width = width,
            Height = height
        });
    }

    private void SendArcadeLockedCropFromMetadata()
    {
        if (string.IsNullOrWhiteSpace(CaptureRomPath))
            return;

        SendHandlerMessage(new ArcadePillarboxApplyLockMessage { RomPath = CaptureRomPath });
    }

    private void SendHandlerMessage(object? message)
    {
        if (_visual != null)
        {
            if (message == null)
                _visual.SendHandlerMessage(null!);
            else
                _visual.SendHandlerMessage(message);
            return;
        }

        _handler?.OnMessage(message);
    }

    private void UpdateFallbackRenderLoop()
    {
        if (!_isAttached || _capture == IntPtr.Zero || _handler == null)
        {
            _fallbackRenderTimer?.Stop();
            return;
        }

        _fallbackRenderTimer ??= new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _fallbackRenderTimer.Tick -= FallbackRenderTick;
        _fallbackRenderTimer.Tick += FallbackRenderTick;

        if (!_fallbackRenderTimer.IsEnabled)
            _fallbackRenderTimer.Start();

        InvalidateVisual();
    }

    private void FallbackRenderTick(object? sender, EventArgs e)
    {
        if (!_isAttached || _handler == null || _capture == IntPtr.Zero)
            return;

        // Match WGC owner-fallback: advance frames and always repaint. Conditional
        // invalidation stalls the shader path after fullscreen resize because
        // grContext.ResetContext() requires a steady owner invalidate loop.
        _handler.OnOwnerRenderTick();
        InvalidateVisual();

        if (_handler.HasPresentedFrame)
            NotifyCaptureFramesStarted();
    }

    /// <summary>
    /// Re-prime the owner-render loop after layout changes (e.g. capture fullscreen).
    /// </summary>
    public void RefreshCapturePresentation()
    {
        if (!_isAttached || _handler == null || _capture == IntPtr.Zero)
            return;

        UpdateHandlerSize();
        _handler.InvalidateGraphicsState();
        UpdateFallbackRenderLoop();
        InvalidateVisual();
    }

    private void NotifyCaptureFramesStarted()
    {
        if (IsCapturePresentingFrames)
            return;

        IsCapturePresentingFrames = true;
        IsCaptureInitializing = false;
    }

    private static int MapStretch(Stretch stretch)
        => stretch switch
        {
            Stretch.Fill => 0,
            Stretch.Uniform => 2,
            Stretch.UniformToFill => 3,
            _ => 3
        };
}
