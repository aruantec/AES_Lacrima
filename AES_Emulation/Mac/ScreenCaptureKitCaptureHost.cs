using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AES_Emulation.Mac.API;

namespace AES_Emulation.Mac;

public class ScreenCaptureKitCaptureHost : NativeControlHost
{
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, string?>(nameof(TargetWindowTitleHint), null);

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<ScreenCaptureKitCaptureHost, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, string>(
            nameof(StatusText),
            o => o.StatusText);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, bool> IsDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, bool>(
            nameof(IsDirectCompositionActive),
            o => o.IsDirectCompositionActive);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, bool> IsCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, bool>(
            nameof(IsCaptureInitializing),
            o => o.IsCaptureInitializing);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, double> FpsProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, double>(
            nameof(Fps),
            o => o.Fps);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, double> FrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, double>(
            nameof(FrameTimeMs),
            o => o.FrameTimeMs);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, string> GpuRendererProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, string>(
            nameof(GpuRenderer),
            o => o.GpuRenderer);

    public static readonly DirectProperty<ScreenCaptureKitCaptureHost, string> GpuVendorProperty =
        AvaloniaProperty.RegisterDirect<ScreenCaptureKitCaptureHost, string>(
            nameof(GpuVendor),
            o => o.GpuVendor);

    private readonly DispatcherTimer _statusTimer;
    private IntPtr _capture;
    private bool _isAttached;
    private string _statusText = "ScreenCaptureKit idle";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "ScreenCaptureKit";
    private string _gpuVendor = "Apple";

    public ScreenCaptureKitCaptureHost()
    {
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => RefreshStatus());
    }

    public IntPtr TargetHwnd
    {
        get => GetValue(TargetHwndProperty);
        set => SetValue(TargetHwndProperty, value);
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

    public double Fps
    {
        get => _fps;
        private set => SetAndRaise(FpsProperty, ref _fps, value);
    }

    public double FrameTimeMs
    {
        get => _frameTimeMs;
        private set => SetAndRaise(FrameTimeMsProperty, ref _frameTimeMs, value);
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
            MacCaptureBridge.aes_mac_capture_forward_focus(_capture);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        EnsureCaptureSession();
        _statusTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _statusTimer.Stop();
        StopSession();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty ||
            change.Property == TargetWindowTitleHintProperty)
        {
            EnsureCaptureSession();
        }
        else if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
        {
            StopSession();
            SetCurrentValue(RequestStopSessionProperty, false);
        }
        else if (change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == HideTargetWindowAfterCaptureStartsProperty ||
                 change.Property == ClientAreaCropLeftInsetProperty ||
                 change.Property == ClientAreaCropTopInsetProperty ||
                 change.Property == ClientAreaCropRightInsetProperty ||
                 change.Property == ClientAreaCropBottomInsetProperty)
        {
            ApplyRenderOptions();
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS())
            return base.CreateNativeControlCore(parent);

        _capture = MacCaptureBridge.aes_mac_capture_create();
        if (_capture == IntPtr.Zero)
        {
            StatusText = "ScreenCaptureKit host creation failed";
            return base.CreateNativeControlCore(parent);
        }

        var view = MacCaptureBridge.aes_mac_capture_get_view(_capture);
        if (view == IntPtr.Zero)
        {
            StatusText = "ScreenCaptureKit NSView creation failed";
            return base.CreateNativeControlCore(parent);
        }

        ApplyRenderOptions();
        return new PlatformHandle(view, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopSession();

        if (_capture != IntPtr.Zero)
        {
            MacCaptureBridge.aes_mac_capture_destroy(_capture);
            _capture = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private void EnsureCaptureSession()
    {
        if (!OperatingSystem.IsMacOS())
        {
            StatusText = "ScreenCaptureKit is macOS-only";
            return;
        }

        if (!_isAttached || _capture == IntPtr.Zero)
            return;

        if (TargetHwnd == IntPtr.Zero)
        {
            StopSession();
            StatusText = "Waiting for emulator process";
            return;
        }

        ApplyRenderOptions();
        MacCaptureBridge.aes_mac_capture_set_target(_capture, unchecked((int)TargetHwnd.ToInt64()), TargetWindowTitleHint);
        RefreshStatus();
    }

    private void StopSession()
    {
        if (_capture != IntPtr.Zero)
            MacCaptureBridge.aes_mac_capture_stop(_capture);

        IsCaptureInitializing = false;
        IsDirectCompositionActive = false;
        Fps = 0;
        FrameTimeMs = 0;
    }

    private void ApplyRenderOptions()
    {
        if (_capture == IntPtr.Zero)
            return;

        MacCaptureBridge.aes_mac_capture_set_stretch(_capture, MapStretch(Stretch));
        MacCaptureBridge.aes_mac_capture_set_render_options(
            _capture,
            (float)Brightness,
            (float)Saturation,
            ColorTint.R / 255f,
            ColorTint.G / 255f,
            ColorTint.B / 255f,
            ColorTint.A / 255f);
        MacCaptureBridge.aes_mac_capture_set_crop_insets(
            _capture,
            ClientAreaCropLeftInset,
            ClientAreaCropTopInset,
            ClientAreaCropRightInset,
            ClientAreaCropBottomInset);
        MacCaptureBridge.aes_mac_capture_set_capture_behavior(
            _capture,
            HideTargetWindowAfterCaptureStarts ? 1 : 0);
    }

    private void RefreshStatus()
    {
        if (_capture == IntPtr.Zero)
            return;

        IsCaptureInitializing = MacCaptureBridge.aes_mac_capture_is_initializing(_capture) != 0;
        IsDirectCompositionActive = MacCaptureBridge.aes_mac_capture_is_active(_capture) != 0;
        Fps = MacCaptureBridge.aes_mac_capture_get_fps(_capture);
        FrameTimeMs = MacCaptureBridge.aes_mac_capture_get_frame_time_ms(_capture);
        StatusText = MacCaptureBridge.GetStatusText(_capture);
        GpuRenderer = MacCaptureBridge.GetGpuRenderer(_capture);
        GpuVendor = MacCaptureBridge.GetGpuVendor(_capture);
    }

    private static int MapStretch(Stretch stretch)
    {
        return stretch switch
        {
            Stretch.Fill => 0,
            Stretch.Uniform => 1,
            Stretch.UniformToFill => 1,
            _ => 1
        };
    }
}
