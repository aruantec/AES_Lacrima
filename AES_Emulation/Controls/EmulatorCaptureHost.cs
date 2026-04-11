using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AES_Emulation.Mac;
using AES_Emulation.Windows;
using AES_Emulation.Linux;

namespace AES_Emulation.Controls;

public class EmulatorCaptureHost : ContentControl
{
    private sealed class LambdaObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }

    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, string?>(nameof(TargetWindowTitleHint));

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly DirectProperty<EmulatorCaptureHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, string>(
            nameof(StatusText),
            o => o.StatusText);

    public static readonly DirectProperty<EmulatorCaptureHost, bool> IsDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, bool>(
            nameof(IsDirectCompositionActive),
            o => o.IsDirectCompositionActive);

    public static readonly DirectProperty<EmulatorCaptureHost, bool> IsCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, bool>(
            nameof(IsCaptureInitializing),
            o => o.IsCaptureInitializing);

    public static readonly DirectProperty<EmulatorCaptureHost, double> FpsProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, double>(
            nameof(Fps),
            o => o.Fps);

    public static readonly DirectProperty<EmulatorCaptureHost, double> FrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, double>(
            nameof(FrameTimeMs),
            o => o.FrameTimeMs);

    public static readonly DirectProperty<EmulatorCaptureHost, string> GpuRendererProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, string>(
            nameof(GpuRenderer),
            o => o.GpuRenderer);

    public static readonly DirectProperty<EmulatorCaptureHost, string> GpuVendorProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, string>(
            nameof(GpuVendor),
            o => o.GpuVendor);

    private readonly Control _backend;
    private string _statusText = "Capture unavailable";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";

    public EmulatorCaptureHost()
    {
        _backend = CreateBackend();
        Content = _backend;
        SyncBackendProperties();
        HookBackendObservables();
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
        switch (_backend)
        {
            case DirectCompositionCaptureHost windowsBackend:
                windowsBackend.ForwardFocusToTarget();
                break;
            case ScreenCaptureKitCaptureHost macBackend:
                macBackend.ForwardFocusToTarget();
                break;
            case LinuxCaptureHost linuxBackend:
                linuxBackend.ForwardFocusToTarget();
                break;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        SyncBackendProperties();

        if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
            SetCurrentValue(RequestStopSessionProperty, false);
    }

    private static Control CreateBackend()
    {
        if (OperatingSystem.IsWindows())
            return new DirectCompositionCaptureHost();

        if (OperatingSystem.IsMacOS())
            return new ScreenCaptureKitCaptureHost();

        if (OperatingSystem.IsLinux())
            return new LinuxCaptureHost();

        return new Border
        {
            Background = Brushes.Black
        };
    }

    private void HookBackendObservables()
    {
        switch (_backend)
        {
            case DirectCompositionCaptureHost windowsBackend:
                BindToWindowsBackend(windowsBackend);
                break;
            case ScreenCaptureKitCaptureHost macBackend:
                BindToMacBackend(macBackend);
                break;
            case LinuxCaptureHost linuxBackend:
                BindToLinuxBackend(linuxBackend);
                break;
            default:
                StatusText = "Capture backend unavailable on this platform";
                break;
        }
    }

    private void BindToWindowsBackend(DirectCompositionCaptureHost backend)
    {
        backend.GetObservable(DirectCompositionCaptureHost.IsCaptureInitializingProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCaptureInitializing = value));
        backend.GetObservable(DirectCompositionCaptureHost.StatusTextProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));
        backend.GetObservable(DirectCompositionCaptureHost.IsDirectCompositionActiveProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsDirectCompositionActive = value));
        backend.GetObservable(DirectCompositionCaptureHost.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(DirectCompositionCaptureHost.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(DirectCompositionCaptureHost.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(DirectCompositionCaptureHost.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));
    }

    private void BindToMacBackend(ScreenCaptureKitCaptureHost backend)
    {
        backend.GetObservable(ScreenCaptureKitCaptureHost.IsCaptureInitializingProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCaptureInitializing = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.StatusTextProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.IsDirectCompositionActiveProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsDirectCompositionActive = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(ScreenCaptureKitCaptureHost.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));
    }

    private void BindToLinuxBackend(LinuxCaptureHost backend)
    {
        backend.GetObservable(LinuxCaptureHost.IsCaptureInitializingProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCaptureInitializing = value));
        backend.GetObservable(LinuxCaptureHost.StatusTextProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));
        backend.GetObservable(LinuxCaptureHost.IsDirectCompositionActiveProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsDirectCompositionActive = value));
        backend.GetObservable(LinuxCaptureHost.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(LinuxCaptureHost.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(LinuxCaptureHost.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(LinuxCaptureHost.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));
    }

    private void SyncBackendProperties()
    {
        switch (_backend)
        {
            case DirectCompositionCaptureHost windowsBackend:
                windowsBackend.TargetHwnd = TargetHwnd;
                windowsBackend.TargetWindowTitleHint = TargetWindowTitleHint;
                windowsBackend.RequestStopSession = RequestStopSession;
                windowsBackend.Stretch = Stretch;
                windowsBackend.Brightness = Brightness;
                windowsBackend.Saturation = Saturation;
                windowsBackend.ColorTint = ColorTint;
                windowsBackend.DisableVSync = DisableVSync;
                windowsBackend.ShaderPath = ShaderPath;
                windowsBackend.ClearShaderWhenPathEmpty = ClearShaderWhenPathEmpty;
                windowsBackend.ForceUseTargetClientArea = ForceUseTargetClientArea;
                windowsBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                windowsBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                windowsBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                windowsBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                windowsBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                break;
            case ScreenCaptureKitCaptureHost macBackend:
                macBackend.TargetHwnd = TargetHwnd;
                macBackend.TargetWindowTitleHint = TargetWindowTitleHint;
                macBackend.RequestStopSession = RequestStopSession;
                macBackend.Stretch = Stretch;
                macBackend.Brightness = Brightness;
                macBackend.Saturation = Saturation;
                macBackend.ColorTint = ColorTint;
                macBackend.DisableVSync = DisableVSync;
                macBackend.ShaderPath = ShaderPath;
                macBackend.ClearShaderWhenPathEmpty = ClearShaderWhenPathEmpty;
                macBackend.ForceUseTargetClientArea = ForceUseTargetClientArea;
                macBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                macBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                macBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                macBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                macBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                break;
            case LinuxCaptureHost linuxBackend:
                linuxBackend.TargetHwnd = TargetHwnd;
                linuxBackend.TargetWindowTitleHint = TargetWindowTitleHint;
                linuxBackend.RequestStopSession = RequestStopSession;
                linuxBackend.Stretch = Stretch;
                linuxBackend.Brightness = Brightness;
                linuxBackend.Saturation = Saturation;
                linuxBackend.ColorTint = ColorTint;
                linuxBackend.DisableVSync = DisableVSync;
                linuxBackend.ShaderPath = ShaderPath;
                linuxBackend.ClearShaderWhenPathEmpty = ClearShaderWhenPathEmpty;
                linuxBackend.ForceUseTargetClientArea = ForceUseTargetClientArea;
                linuxBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                linuxBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                linuxBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                linuxBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                linuxBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                break;
        }
    }
}
