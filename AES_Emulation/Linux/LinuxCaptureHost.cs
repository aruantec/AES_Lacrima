using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AES_Emulation.Linux.API;

namespace AES_Emulation.Linux;

public class LinuxCaptureHost : NativeControlHost
{
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, string?>(nameof(TargetWindowTitleHint), null);

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> PreferPipeWireProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(PreferPipeWire), true);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly DirectProperty<LinuxCaptureHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, string>(
            nameof(StatusText),
            o => o.StatusText);

    public static readonly DirectProperty<LinuxCaptureHost, bool> IsDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, bool>(
            nameof(IsDirectCompositionActive),
            o => o.IsDirectCompositionActive);

    public static readonly DirectProperty<LinuxCaptureHost, bool> IsCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, bool>(
            nameof(IsCaptureInitializing),
            o => o.IsCaptureInitializing);

    public static readonly DirectProperty<LinuxCaptureHost, double> FpsProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, double>(
            nameof(Fps),
            o => o.Fps);

    public static readonly DirectProperty<LinuxCaptureHost, double> FrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, double>(
            nameof(FrameTimeMs),
            o => o.FrameTimeMs);

    public static readonly DirectProperty<LinuxCaptureHost, string> GpuRendererProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, string>(
            nameof(GpuRenderer),
            o => o.GpuRenderer);

    public static readonly DirectProperty<LinuxCaptureHost, string> GpuVendorProperty =
        AvaloniaProperty.RegisterDirect<LinuxCaptureHost, string>(
            nameof(GpuVendor),
            o => o.GpuVendor);

    private readonly DispatcherTimer _statusTimer;
    private IntPtr _capture;
    private bool _isAttached;
    private string _statusText = "Linux Capture idle";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";

    public LinuxCaptureHost()
    {
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => RefreshStatus());
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

    public bool PreferPipeWire
    {
        get => GetValue(PreferPipeWireProperty);
        set => SetValue(PreferPipeWireProperty, value);
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
            LinuxCaptureBridge.aes_linux_capture_forward_focus(_capture);
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
                 change.Property == PreferPipeWireProperty ||
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
        if (!OperatingSystem.IsLinux())
            return base.CreateNativeControlCore(parent);

        _capture = LinuxCaptureBridge.aes_linux_capture_create(parent.Handle);
        if (_capture == IntPtr.Zero)
        {
            StatusText = "Linux capture host creation failed";
            return base.CreateNativeControlCore(parent);
        }

        var view = LinuxCaptureBridge.aes_linux_capture_get_view(_capture);
        if (view == IntPtr.Zero)
        {
            StatusText = "Linux capture Window creation failed";
            return base.CreateNativeControlCore(parent);
        }

        ApplyRenderOptions();
        return new PlatformHandle(view, "X11Window");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopSession();

        if (_capture != IntPtr.Zero)
        {
            LinuxCaptureBridge.aes_linux_capture_destroy(_capture);
            _capture = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private void EnsureCaptureSession()
    {
        if (!OperatingSystem.IsLinux())
        {
            StatusText = "Linux capture is Linux-only";
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
        LinuxCaptureBridge.aes_linux_capture_set_target(_capture, unchecked((int)TargetHwnd.ToInt64()), TargetWindowTitleHint);
        RefreshStatus();
    }

    private void StopSession()
    {
        if (_capture != IntPtr.Zero)
            LinuxCaptureBridge.aes_linux_capture_stop(_capture);

        IsCaptureInitializing = false;
        IsDirectCompositionActive = false;
        Fps = 0;
        FrameTimeMs = 0;
    }

    private void ApplyRenderOptions()
    {
        if (_capture == IntPtr.Zero)
            return;

        LinuxCaptureBridge.aes_linux_capture_set_stretch(_capture, MapStretch(Stretch));
        LinuxCaptureBridge.aes_linux_capture_set_render_options(
            _capture,
            (float)Brightness,
            (float)Saturation,
            ColorTint.R / 255f,
            ColorTint.G / 255f,
            ColorTint.B / 255f,
            ColorTint.A / 255f);
        LinuxCaptureBridge.aes_linux_capture_set_crop_insets(
            _capture,
            ClientAreaCropLeftInset,
            ClientAreaCropTopInset,
            ClientAreaCropRightInset,
            ClientAreaCropBottomInset);
        LinuxCaptureBridge.aes_linux_capture_set_capture_behavior(
            _capture,
            HideTargetWindowAfterCaptureStarts ? 1 : 0);
        LinuxCaptureBridge.aes_linux_capture_set_use_pipewire(
            _capture,
            PreferPipeWire ? 1 : 0);
    }

    private void RefreshStatus()
    {
        if (_capture == IntPtr.Zero)
            return;

        IsCaptureInitializing = LinuxCaptureBridge.aes_linux_capture_is_initializing(_capture) != 0;
        IsDirectCompositionActive = LinuxCaptureBridge.aes_linux_capture_is_active(_capture) != 0;
        Fps = LinuxCaptureBridge.aes_linux_capture_get_fps(_capture);
        FrameTimeMs = LinuxCaptureBridge.aes_linux_capture_get_frame_time_ms(_capture);
        StatusText = LinuxCaptureBridge.GetStatusText(_capture);
        GpuRenderer = LinuxCaptureBridge.GetGpuRenderer(_capture);
        GpuVendor = LinuxCaptureBridge.GetGpuVendor(_capture);
    }

    private static int MapStretch(Stretch stretch)
    {
        return stretch switch
        {
            Stretch.None => 0,
            Stretch.Fill => 1,
            Stretch.Uniform => 2,
            Stretch.UniformToFill => 3,
            _ => 3
        };
    }
}
