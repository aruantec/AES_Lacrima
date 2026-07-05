using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AES_Controls;
using AES_Emulation.Linux;
using AES_Emulation.Mac;
using AES_Emulation.Services;
using AES_Emulation.Windows;
using AES_Emulation.Windows.API;

namespace AES_Emulation.Controls;

public enum EmulatorCaptureMode
{
    /// <summary>
    /// Renders captured frames inside Avalonia via a composition custom visual (WGC + GPU upload).
    /// Avoids native child HWND airspace so overlays and particles render correctly on top.
    /// </summary>
    DirectComposition,

    /// <summary>
    /// Legacy path: presents frames in a separate native DirectComposition child window (HWND).
    /// Highest presentation throughput but always draws above Avalonia content on Windows.
    /// </summary>
    NativeWindow,

    /// <summary>
    /// Injected / alternate WGC host (OpenGL control).
    /// </summary>
    Injected
}

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

    public static readonly StyledProperty<int> TargetProcessIdProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(TargetProcessId));

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

    public static readonly StyledProperty<EmulationFrameGenerationMode> FrameGenerationModeProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, EmulationFrameGenerationMode>(nameof(FrameGenerationMode), EmulationFrameGenerationMode.Off);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> EnablePillarboxCropProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(EnablePillarboxCrop), false);

    public static readonly StyledProperty<bool> AggressivePillarboxCropProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(AggressivePillarboxCrop), false);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<bool> RestoreTargetWindowOnStopProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(RestoreTargetWindowOnStop), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly StyledProperty<double> CaptureWindowAspectRatioProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, double>(nameof(CaptureWindowAspectRatio), 0);

    public static readonly StyledProperty<bool> LowLatencyCaptureProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(LowLatencyCapture), true);

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

    public static readonly DirectProperty<EmulatorCaptureHost, bool> IsCapturePresentingFramesProperty =
        AvaloniaProperty.RegisterDirect<EmulatorCaptureHost, bool>(
            nameof(IsCapturePresentingFrames),
            o => o.IsCapturePresentingFrames);

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

    public static readonly StyledProperty<EmulatorCaptureMode> CaptureModeProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, EmulatorCaptureMode>(nameof(CaptureMode), EmulatorCaptureMode.DirectComposition);

    public static readonly StyledProperty<int> CaptureSessionStartDelayMsProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, int>(nameof(CaptureSessionStartDelayMs), 0);

    public static readonly StyledProperty<bool> UseBackCoverLetterboxFillProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, bool>(nameof(UseBackCoverLetterboxFill), false);

    public static readonly StyledProperty<Bitmap?> LetterboxBitmapProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, Bitmap?>(nameof(LetterboxBitmap));

    public static readonly StyledProperty<string?> CaptureRomPathProperty =
        AvaloniaProperty.Register<EmulatorCaptureHost, string?>(nameof(CaptureRomPath));

    private Control _backend;
    private string _statusText = "Capture unavailable";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private bool _isCapturePresentingFrames;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";
    private MouseTunnelHelper? _mouseTunnel;
    private LinuxGamescopeInputTunnel? _linuxInputTunnel;
    private InputElement? _pointerTunnelSurface;

    private Func<Point, bool>? _captureDoubleClickHandler;

    /// <summary>
    /// Invoked on a double-click inside the capture area. Return true when handled (e.g. fullscreen toggle).
    /// </summary>
    public Func<Point, bool>? CaptureDoubleClickHandler
    {
        get => _captureDoubleClickHandler;
        set
        {
            _captureDoubleClickHandler = value;
            if (_mouseTunnel != null)
                _mouseTunnel.TryHandleDoubleClickAtPoint = value;
        }
    }

    public EmulatorCaptureHost()
    {
        if (OperatingSystem.IsLinux())
            ClipToBounds = true;

        _backend = CreateBackend();
        ApplyLinuxScaleExclusionCompensation();
        Content = _backend;
        SyncBackendProperties();
        HookBackendObservables();

        if (OperatingSystem.IsWindows())
        {
            RecreateMouseTunnel();
            Focusable = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            RecreateLinuxInputTunnel();
            Focusable = true;
        }
    }

    /// <summary>
    /// Routes pointer tunneling through <paramref name="surface"/> (e.g. a transparent overlay)
    /// so Avalonia receives right-clicks for context menus. When set, this host is not hit-testable.
    /// </summary>
    public void ConfigurePointerTunnelSurface(InputElement? surface)
    {
        if (ReferenceEquals(_pointerTunnelSurface, surface))
            return;

        _pointerTunnelSurface = surface;
        RecreateMouseTunnel();
    }

    public IntPtr TargetHwnd
    {
        get => GetValue(TargetHwndProperty);
        set => SetValue(TargetHwndProperty, value);
    }

    public int TargetProcessId
    {
        get => GetValue(TargetProcessIdProperty);
        set => SetValue(TargetProcessIdProperty, value);
    }

    public string? TargetWindowTitleHint
    {
        get => GetValue(TargetWindowTitleHintProperty);
        set => SetValue(TargetWindowTitleHintProperty, value);
    }

    public EmulatorCaptureMode CaptureMode
    {
        get => GetValue(CaptureModeProperty);
        set => SetValue(CaptureModeProperty, value);
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

    public EmulationFrameGenerationMode FrameGenerationMode
    {
        get => GetValue(FrameGenerationModeProperty);
        set => SetValue(FrameGenerationModeProperty, value);
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

    public bool HideTargetWindowAfterCaptureStarts
    {
        get => GetValue(HideTargetWindowAfterCaptureStartsProperty);
        set => SetValue(HideTargetWindowAfterCaptureStartsProperty, value);
    }

    public bool RestoreTargetWindowOnStop
    {
        get => GetValue(RestoreTargetWindowOnStopProperty);
        set => SetValue(RestoreTargetWindowOnStopProperty, value);
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

    public bool LowLatencyCapture
    {
        get => GetValue(LowLatencyCaptureProperty);
        set => SetValue(LowLatencyCaptureProperty, value);
    }

    public int CaptureSessionStartDelayMs
    {
        get => GetValue(CaptureSessionStartDelayMsProperty);
        set => SetValue(CaptureSessionStartDelayMsProperty, value);
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
        if (OperatingSystem.IsWindows() && _backend is CompositionWgcCaptureControl compositionBackend)
            return compositionBackend.TryGetPillarboxCrop(out left, out right, out frameWidth);

        if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            return linuxCompositionBackend.TryGetPillarboxCrop(out left, out right, out frameWidth);

        left = right = frameWidth = 0;
        return false;
    }

    public void ApplyArcadeLockedPillarboxCrop(int left, int right, int frameWidth, string? romPath = null)
    {
        var message = new ArcadePillarboxApplyLockMessage
        {
            RomPath = romPath ?? CaptureRomPath,
            Left = left,
            Right = right,
            FrameWidth = frameWidth
        };

        if (OperatingSystem.IsWindows() && _backend is CompositionWgcCaptureControl compositionBackend)
            compositionBackend.ApplyArcadeLockedPillarboxCrop(message);
        else if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.ApplyArcadeLockedPillarboxCrop(message);
    }

    public void ReloadArcadeLockedPillarboxCropFromMetadata(string? romPath = null)
    {
        var message = new ArcadePillarboxApplyLockMessage
        {
            RomPath = romPath ?? CaptureRomPath
        };

        if (OperatingSystem.IsWindows() && _backend is CompositionWgcCaptureControl compositionBackend)
            compositionBackend.ApplyArcadeLockedPillarboxCrop(message);
        else if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.ApplyArcadeLockedPillarboxCrop(message);
    }

    public void UnlockArcadePillarboxCrop(string? romPath = null)
    {
        var message = new ArcadePillarboxUnlockMessage
        {
            RomPath = romPath ?? CaptureRomPath
        };

        if (OperatingSystem.IsWindows() && _backend is CompositionWgcCaptureControl compositionBackend)
            compositionBackend.UnlockArcadePillarboxCrop(message);
        else if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.UnlockArcadePillarboxCrop(message);
    }

    public void ReloadArcadeLockedPillarboxCrop() => ReloadArcadeLockedPillarboxCropFromMetadata();

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

    public void RefreshCapturePresentation()
    {
        if (_backend is IScaleExclusionRenderTarget scaleTarget)
            scaleTarget.RefreshExclusionRenderSize();

        if (_backend is CompositionWgcCaptureControl compositionBackend)
            compositionBackend.RefreshCapturePresentation();
        else if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.RefreshCapturePresentation();
    }

    public void SetUiBlocksShaderHotCompile(bool blocked)
    {
        if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.SetUiBlocksShaderHotCompile(blocked);
    }

    public void ForwardFocusToTarget()
    {
        if (OperatingSystem.IsLinux())
        {
            // gamescope runs on an isolated X11 display; host-display focus calls steal input
            // from the compositor and can pause Steam titles mid-capture.
            if (_linuxInputTunnel != null && TargetProcessId > 0)
            {
                _linuxInputTunnel.ForwardFocusToTarget();
                return;
            }

            if (_backend is LinuxCompositionCaptureControl linuxCompositionBackend)
                linuxCompositionBackend.ForwardFocusToTarget();
            else if (_backend is LinuxCaptureHost linuxBackend)
                linuxBackend.ForwardFocusToTarget();
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

        switch (_backend)
        {
            case CompositionWgcCaptureControl compositionBackend:
                compositionBackend.ForwardFocusToTarget();
                break;
            case WgcCaptureControl wgcBackend:
                wgcBackend.ForwardFocusToTarget();
                break;
        }
    }

    public void ConfigureGameplayRecording(
        Action<byte[], int, int>? frameHandler,
        int targetFps,
        AES_Emulation.Services.GameplayRecordingResolutionCap resolutionCap = AES_Emulation.Services.GameplayRecordingResolutionCap.P1080)
    {
        if (OperatingSystem.IsWindows() && _backend is CompositionWgcCaptureControl compositionBackend)
        {
            compositionBackend.ConfigureGameplayRecording(frameHandler, targetFps, resolutionCap);
            return;
        }

        if (OperatingSystem.IsLinux())
            ConfigureGameplayRecordingLinux(frameHandler, targetFps, resolutionCap);
    }

    [SupportedOSPlatform("linux")]
    private void ConfigureGameplayRecordingLinux(
        Action<byte[], int, int>? frameHandler,
        int targetFps,
        AES_Emulation.Services.GameplayRecordingResolutionCap resolutionCap)
    {
        if (_backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.ConfigureGameplayRecording(frameHandler, targetFps, resolutionCap);
    }

    private (int X, int Y)? MapLocalToTargetClient(Point hostLocal)
    {
        if (_backend is not Visual backendVisual)
            return null;

        Point backendLocal = hostLocal;
        if (this is Visual hostVisual && !ReferenceEquals(_backend, hostVisual))
            backendLocal = hostVisual.TranslatePoint(hostLocal, backendVisual) ?? hostLocal;

        return MapBackendPointToTargetClient(backendLocal);
    }

    private (int X, int Y)? MapBackendPointToTargetClient(Point backendLocal)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return _backend switch
        {
            CompositionWgcCaptureControl compositionBackend => compositionBackend.TryMapCapturePointToTargetClient(backendLocal),
            WgcCaptureControl wgcBackend => wgcBackend.TryMapCapturePointToTargetClient(backendLocal),
            _ => null
        };
    }

    private void UpdateMouseTunnelTarget()
    {
        if (_mouseTunnel != null)
            _mouseTunnel.TargetHwnd = TargetHwnd;
    }

    [SupportedOSPlatform("windows")]
    private void BindToWgcBackend(WgcCaptureControl backend)
    {
        // Observe the backend name to show "Direct Hook (Injected)" or "Windows Graphics Capture (WGC)"
        backend.GetObservable(WgcCaptureControl.BackendNameProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));

        IsDirectCompositionActive = true;
        backend.GetObservable(WgcCaptureControl.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(WgcCaptureControl.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(WgcCaptureControl.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(WgcCaptureControl.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));
    }

    [SupportedOSPlatform("windows")]
    private void SyncWgcProperties(WgcCaptureControl wgcBackend)
    {
        wgcBackend.TargetHwnd = TargetHwnd;
        wgcBackend.TargetProcessId = TargetProcessId;
        wgcBackend.Stretch = Stretch;
        wgcBackend.Brightness = Brightness;
        wgcBackend.Saturation = Saturation;
        wgcBackend.ColorTint = ColorTint;
        wgcBackend.DisableVSync = DisableVSync;
        wgcBackend.RetroarchShaderFile = string.IsNullOrWhiteSpace(ShaderPath) && ClearShaderWhenPathEmpty ? null : ShaderPath;
        wgcBackend.ForceUseTargetClientSize = ForceUseTargetClientArea;
        wgcBackend.RestoreTargetWindowOnStop = RestoreTargetWindowOnStop;
        wgcBackend.LetterBoxBitmap = UseBackCoverLetterboxFill ? LetterboxBitmap : null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty)
            UpdateMouseTunnelTarget();

        if (change.Property == TargetProcessIdProperty)
            UpdateLinuxInputTunnelTarget();

        if (change.Property == BoundsProperty)
            RefreshCapturePresentation();
        
        if (change.Property == CaptureModeProperty)
        {
            RecreateBackend();
            return;
        }

        if (change.Property == CaptureSessionStartDelayMsProperty)
        {
            SyncBackendProperties();
            return;
        }

        if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
        {
            PropagateStopSession();
            SetCurrentValue(RequestStopSessionProperty, false);
            return;
        }

        SyncBackendProperties();
    }

    private void PropagateStopSession()
    {
        if (OperatingSystem.IsLinux())
            PropagateStopSessionLinux();
        else if (OperatingSystem.IsMacOS())
            PropagateStopSessionMac();
        else if (OperatingSystem.IsWindows())
            PropagateStopSessionWindows();
    }

    public Task SuspendNativeCaptureAsync()
    {
        if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            return linuxCompositionBackend.SuspendSessionImmediatelyAsync();

        PropagateStopSession();
        return Task.CompletedTask;
    }

    public Task AbandonCaptureAfterCompositorExitAsync()
    {
        if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            return linuxCompositionBackend.AbandonAfterCompositorExitAsync();

        PropagateStopSession();
        return Task.CompletedTask;
    }

    public void SuspendCaptureSessionPresentation()
    {
        ReleaseLinuxInputImmediately();

        if (OperatingSystem.IsLinux() && _backend is LinuxCompositionCaptureControl linuxCompositionBackend)
            linuxCompositionBackend.SuspendRenderingOnly();
    }

    internal void ReleaseLinuxInputImmediately()
    {
        if (!OperatingSystem.IsLinux())
            return;

        _linuxInputTunnel?.Dispose();
        _linuxInputTunnel = null;
    }

    [SupportedOSPlatform("linux")]
    private void PropagateStopSessionLinux()
    {
        switch (_backend)
        {
            case LinuxCompositionCaptureControl linuxCompositionBackend:
                linuxCompositionBackend.RequestStopSession = true;
                break;
            case LinuxCaptureHost linuxBackend:
                linuxBackend.RequestStopSession = true;
                break;
        }
    }

    [SupportedOSPlatform("macos")]
    private void PropagateStopSessionMac()
    {
        if (_backend is ScreenCaptureKitCaptureHost macBackend)
            macBackend.RequestStopSession = true;
    }

    [SupportedOSPlatform("windows")]
    private void PropagateStopSessionWindows()
    {
        switch (_backend)
        {
            case CompositionWgcCaptureControl compositionBackend:
                compositionBackend.RequestStopSession = true;
                break;
            case WgcCaptureControl wgcBackend:
                wgcBackend.RequestStopSession = true;
                break;
            case DirectCompositionCaptureHost windowsBackend:
                windowsBackend.RequestStopSession = true;
                break;
        }
    }

    private void RecreateBackend()
    {
        _mouseTunnel?.Dispose();
        _mouseTunnel = null;
        if (OperatingSystem.IsLinux())
        {
            _linuxInputTunnel?.Dispose();
            _linuxInputTunnel = null;
        }

        Content = null;
        _backend = CreateBackend();
        ApplyLinuxScaleExclusionCompensation();
        Content = _backend;
        SyncBackendProperties();
        HookBackendObservables();

        if (OperatingSystem.IsWindows())
            RecreateMouseTunnel();
        else if (OperatingSystem.IsLinux())
            RecreateLinuxInputTunnel();
    }

    private void RecreateLinuxInputTunnel()
    {
        if (!OperatingSystem.IsLinux())
        {
            IsHitTestVisible = true;
            return;
        }

        _linuxInputTunnel?.Dispose();
        _linuxInputTunnel = null;

        var tunnelElement = (InputElement?)_pointerTunnelSurface ?? this;
        IsHitTestVisible = _pointerTunnelSurface == null;

        var aspect = CaptureWindowAspectRatio > 0 ? CaptureWindowAspectRatio : (double?)null;
        var baseHeight = aspect is > 0 and < 1.6 ? 1080 : 720;
        var (width, height) = LinuxCompositorLaunchHelper.ResolveOutputSize(baseHeight, aspect);
        _linuxInputTunnel = new LinuxGamescopeInputTunnel(tunnelElement)
        {
            CompositorProcessId = TargetProcessId,
            ViewportWidth = width,
            ViewportHeight = height,
            MapToTargetClient = local => MapTunnelLocalToTargetClient(local, tunnelElement)
        };
    }

    private void UpdateLinuxInputTunnelTarget()
    {
        if (!OperatingSystem.IsLinux() || _linuxInputTunnel == null)
            return;

        _linuxInputTunnel.CompositorProcessId = TargetProcessId;
    }

    private void RecreateMouseTunnel()
    {
        _mouseTunnel?.Dispose();
        _mouseTunnel = null;

        if (!OperatingSystem.IsWindows())
        {
            IsHitTestVisible = true;
            return;
        }

        var tunnelElement = (InputElement?)_pointerTunnelSurface ?? this;
        IsHitTestVisible = _pointerTunnelSurface == null;

        _mouseTunnel = new MouseTunnelHelper(tunnelElement)
        {
            TunnelMouse = true,
            MapToTargetClient = local => MapTunnelLocalToTargetClient(local, tunnelElement),
            TryHandleDoubleClickAtPoint = CaptureDoubleClickHandler
        };
        UpdateMouseTunnelTarget();
    }

    private (int X, int Y)? MapTunnelLocalToTargetClient(Point local, InputElement tunnelElement)
    {
        if (_backend is not Visual backendVisual)
            return null;

        Point backendLocal = local;
        if (tunnelElement is Visual tunnelVisual && !ReferenceEquals(tunnelVisual, backendVisual))
        {
            var translated = tunnelVisual.TranslatePoint(local, backendVisual);
            if (translated == null)
                return null;

            backendLocal = translated.Value;
        }

        return MapBackendPointToTargetClient(backendLocal);
    }

    private Control CreateBackend()
    {
        if (OperatingSystem.IsWindows())
        {
            return CaptureMode switch
            {
                EmulatorCaptureMode.NativeWindow => new DirectCompositionCaptureHost(),
                EmulatorCaptureMode.Injected => new WgcCaptureControl(),
                _ => new CompositionWgcCaptureControl()
            };
        }

        if (OperatingSystem.IsMacOS())
            return new ScreenCaptureKitCaptureHost();

        if (OperatingSystem.IsLinux())
        {
            return CaptureMode == EmulatorCaptureMode.NativeWindow
                ? new LinuxCaptureHost()
                : new LinuxCompositionCaptureControl();
        }

        return new Border
        {
            Background = Brushes.Black
        };
    }

    private void ApplyLinuxScaleExclusionCompensation()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Composition capture draws through a nested control that already fills the host
        // layout bounds; extra compensation on Linux double-scales the presentation.
        ScalableDecorator.SetExcludeFromScaleCompensation(
            this,
            OperatingSystem.IsWindows() && CaptureMode != EmulatorCaptureMode.NativeWindow);
    }

    private void HookBackendObservables()
    {
        if (OperatingSystem.IsWindows())
        {
            switch (_backend)
            {
                case CompositionWgcCaptureControl compositionBackend:
                    BindToCompositionBackend(compositionBackend);
                    return;
                case DirectCompositionCaptureHost windowsBackend:
                    BindToWindowsBackend(windowsBackend);
                    return;
                case WgcCaptureControl wgcBackend:
                    BindToWgcBackend(wgcBackend);
                    return;
            }
        }

        switch (_backend)
        {
            case ScreenCaptureKitCaptureHost macBackend:
                BindToMacBackend(macBackend);
                break;
            case LinuxCaptureHost linuxBackend when OperatingSystem.IsLinux():
                BindToLinuxBackend(linuxBackend);
                break;
            case LinuxCompositionCaptureControl linuxCompositionBackend when OperatingSystem.IsLinux():
                BindToLinuxCompositionBackend(linuxCompositionBackend);
                break;
            default:
                StatusText = "Capture backend unavailable on this platform";
                break;
        }
    }

    [SupportedOSPlatform("linux")]
    private void BindToLinuxBackend(LinuxCaptureHost backend)
    {
        StatusText = "PipeWire portal capture (DMA-BUF)";
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

    [SupportedOSPlatform("linux")]
    private void BindToLinuxCompositionBackend(LinuxCompositionCaptureControl backend)
    {
        StatusText = "Avalonia composition capture (PipeWire)";
        IsDirectCompositionActive = true;

        backend.GetObservable(LinuxCompositionCaptureControl.IsCaptureInitializingProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCaptureInitializing = value));
        backend.GetObservable(LinuxCompositionCaptureControl.IsCapturePresentingFramesProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCapturePresentingFrames = value));
        backend.GetObservable(LinuxCompositionCaptureControl.StatusTextProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));
        backend.GetObservable(LinuxCompositionCaptureControl.IsDirectCompositionActiveProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsDirectCompositionActive = value));
        backend.GetObservable(LinuxCompositionCaptureControl.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(LinuxCompositionCaptureControl.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(LinuxCompositionCaptureControl.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(LinuxCompositionCaptureControl.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));
    }

    [SupportedOSPlatform("windows")]
    private void BindToCompositionBackend(CompositionWgcCaptureControl backend)
    {
        IsDirectCompositionActive = true;

        backend.GetObservable(CompositionWgcCaptureControl.IsCaptureInitializingProperty)
            .Subscribe(new LambdaObserver<bool>(value => IsCaptureInitializing = value));
        backend.GetObservable(CompositionWgcCaptureControl.StatusTextProperty)
            .Subscribe(new LambdaObserver<string>(value => StatusText = value));
        backend.GetObservable(CompositionWgcCaptureControl.FpsProperty)
            .Subscribe(new LambdaObserver<double>(value => Fps = value));
        backend.GetObservable(CompositionWgcCaptureControl.FrameTimeMsProperty)
            .Subscribe(new LambdaObserver<double>(value => FrameTimeMs = value));
        backend.GetObservable(CompositionWgcCaptureControl.GpuRendererProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuRenderer = value));
        backend.GetObservable(CompositionWgcCaptureControl.GpuVendorProperty)
            .Subscribe(new LambdaObserver<string>(value => GpuVendor = value));

        StatusText = backend.StatusText;
        GpuRenderer = backend.GpuRenderer;
        GpuVendor = backend.GpuVendor;
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

    private void SyncBackendProperties()
    {
        if (OperatingSystem.IsWindows())
        {
            switch (_backend)
            {
                case CompositionWgcCaptureControl compositionBackend:
                    compositionBackend.TargetHwnd = TargetHwnd;
                    compositionBackend.Stretch = Stretch;
                    compositionBackend.Brightness = Brightness;
                    compositionBackend.Saturation = Saturation;
                    compositionBackend.ColorTint = ColorTint;
                    compositionBackend.DisableVSync = DisableVSync;
                    compositionBackend.RetroarchShaderFile = string.IsNullOrWhiteSpace(ShaderPath) && ClearShaderWhenPathEmpty ? null : ShaderPath;
                    compositionBackend.ForceUseTargetClientSize = ForceUseTargetClientArea;
                    compositionBackend.EnableAutoCrop = EnablePillarboxCrop;
                    compositionBackend.AggressivePillarboxCrop = AggressivePillarboxCrop;
                    compositionBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                    compositionBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                    compositionBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                    compositionBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                    compositionBackend.CaptureSessionStartDelayMs = CaptureSessionStartDelayMs;
                    compositionBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                    compositionBackend.RestoreTargetWindowOnStop = RestoreTargetWindowOnStop;
                    compositionBackend.CaptureWindowAspectRatio = CaptureWindowAspectRatio;
                    compositionBackend.ShowStatisticsOverlay = false;
                    compositionBackend.ShowFrametimeGraph = false;
                    compositionBackend.ShowDetailedGpuInfo = false;
                    compositionBackend.UseBackCoverLetterboxFill = UseBackCoverLetterboxFill;
                    compositionBackend.LetterboxBitmap = LetterboxBitmap;
                    compositionBackend.CaptureRomPath = CaptureRomPath;
                    return;
                case WgcCaptureControl wgcBackend:
                    SyncWgcProperties(wgcBackend);
                    return;
                case DirectCompositionCaptureHost windowsBackend:
                    windowsBackend.TargetHwnd = TargetHwnd;
                    windowsBackend.TargetProcessId = TargetProcessId;
                    windowsBackend.TargetWindowTitleHint = TargetWindowTitleHint;
                    windowsBackend.Stretch = Stretch;
                    windowsBackend.Brightness = Brightness;
                    windowsBackend.Saturation = Saturation;
                    windowsBackend.ColorTint = ColorTint;
                    windowsBackend.DisableVSync = DisableVSync;
                    windowsBackend.FrameGenerationMode = FrameGenerationMode;
                    windowsBackend.ShaderPath = ShaderPath;
                    windowsBackend.ClearShaderWhenPathEmpty = ClearShaderWhenPathEmpty;
                    windowsBackend.ForceUseTargetClientArea = ForceUseTargetClientArea;
                    windowsBackend.EnablePillarboxCrop = EnablePillarboxCrop;
                    windowsBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                    windowsBackend.RestoreTargetWindowOnStop = RestoreTargetWindowOnStop;
                    windowsBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                    windowsBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                    windowsBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                    windowsBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                    windowsBackend.CaptureWindowAspectRatio = CaptureWindowAspectRatio;
                    windowsBackend.LowLatencyCapture = LowLatencyCapture;
                    return;
            }
        }

        if (OperatingSystem.IsMacOS())
            SyncMacBackendProperties();
        else if (OperatingSystem.IsLinux())
            SyncLinuxBackendProperties();
    }

    [SupportedOSPlatform("macos")]
    private void SyncMacBackendProperties()
    {
        if (_backend is not ScreenCaptureKitCaptureHost macBackend)
            return;

        macBackend.TargetHwnd = TargetHwnd;
        macBackend.TargetWindowTitleHint = TargetWindowTitleHint;
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
    }

    [SupportedOSPlatform("linux")]
    private void SyncLinuxBackendProperties()
    {
        switch (_backend)
        {
            case LinuxCaptureHost linuxBackend:
                linuxBackend.TargetHwnd = TargetHwnd;
                linuxBackend.CompositorProcessId = TargetProcessId;
                linuxBackend.TargetWindowTitleHint = "gamescope";
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
                linuxBackend.PreferPipeWire = true;
                linuxBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                linuxBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                linuxBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                linuxBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                break;
            case LinuxCompositionCaptureControl linuxCompositionBackend:
                linuxCompositionBackend.TargetHwnd = TargetHwnd;
                linuxCompositionBackend.CompositorProcessId = TargetProcessId;
                linuxCompositionBackend.TargetWindowTitleHint = "gamescope";
                linuxCompositionBackend.RequestStopSession = RequestStopSession;
                linuxCompositionBackend.Stretch = Stretch;
                linuxCompositionBackend.Brightness = Brightness;
                linuxCompositionBackend.Saturation = Saturation;
                linuxCompositionBackend.ColorTint = ColorTint;
                linuxCompositionBackend.DisableVSync = DisableVSync;
                linuxCompositionBackend.ShaderPath = ShaderPath;
                linuxCompositionBackend.ClearShaderWhenPathEmpty = ClearShaderWhenPathEmpty;
                linuxCompositionBackend.ForceUseTargetClientArea = ForceUseTargetClientArea;
                linuxCompositionBackend.HideTargetWindowAfterCaptureStarts = HideTargetWindowAfterCaptureStarts;
                linuxCompositionBackend.ClientAreaCropLeftInset = ClientAreaCropLeftInset;
                linuxCompositionBackend.ClientAreaCropTopInset = ClientAreaCropTopInset;
                linuxCompositionBackend.ClientAreaCropRightInset = ClientAreaCropRightInset;
                linuxCompositionBackend.ClientAreaCropBottomInset = ClientAreaCropBottomInset;
                linuxCompositionBackend.CaptureWindowAspectRatio = CaptureWindowAspectRatio;
                linuxCompositionBackend.UseBackCoverLetterboxFill = UseBackCoverLetterboxFill;
                linuxCompositionBackend.LetterboxBitmap = LetterboxBitmap;
                linuxCompositionBackend.EnablePillarboxCrop = EnablePillarboxCrop;
                linuxCompositionBackend.AggressivePillarboxCrop = AggressivePillarboxCrop;
                linuxCompositionBackend.CaptureRomPath = CaptureRomPath;
                break;
        }
    }
}
