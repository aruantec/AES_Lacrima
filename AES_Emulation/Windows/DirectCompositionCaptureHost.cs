using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AES_Emulation.Windows.API;
using System.Diagnostics;
using System.Threading;

using log4net;
using AES_Core.Logging;
namespace AES_Emulation.Windows;

public class DirectCompositionCaptureHost : NativeControlHost
{
    private static readonly ILog Log = LogHelper.For<DirectCompositionCaptureHost>();
    /// <summary>
    /// When true (Windows default), the emulator is parked once at a fixed on-desktop location and
    /// hidden in place for capture. AES does not mirror or continuously reposition the target HWND.
    /// </summary>
    public static bool UseStaticCaptureDock { get; set; } = true;

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint SsBlackRect = 0x0004;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<bool> RequestStopSessionProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(RequestStopSession), false);

    public static readonly StyledProperty<int> TargetProcessIdProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, int>(nameof(TargetProcessId));

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, string?>(nameof(TargetWindowTitleHint), null);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, Stretch>(nameof(Stretch), Stretch.Uniform);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<EmulationFrameGenerationMode> FrameGenerationModeProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, EmulationFrameGenerationMode>(nameof(FrameGenerationMode), EmulationFrameGenerationMode.Off);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

    public static readonly StyledProperty<bool> EnablePillarboxCropProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(EnablePillarboxCrop), false);

    public static readonly StyledProperty<bool> HideTargetWindowAfterCaptureStartsProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(HideTargetWindowAfterCaptureStarts), true);

    public static readonly StyledProperty<int> ClientAreaCropLeftInsetProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, int>(nameof(ClientAreaCropLeftInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropTopInsetProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, int>(nameof(ClientAreaCropTopInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropRightInsetProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, int>(nameof(ClientAreaCropRightInset), 0);

    public static readonly StyledProperty<int> ClientAreaCropBottomInsetProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, int>(nameof(ClientAreaCropBottomInset), 0);

    public static readonly StyledProperty<double> CaptureWindowAspectRatioProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, double>(nameof(CaptureWindowAspectRatio), 0);

    public static readonly StyledProperty<bool> LowLatencyCaptureProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(LowLatencyCapture), true);

    public static readonly DirectProperty<DirectCompositionCaptureHost, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, string>(
            nameof(StatusText),
            o => o.StatusText);

    public static readonly DirectProperty<DirectCompositionCaptureHost, bool> IsDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, bool>(
            nameof(IsDirectCompositionActive),
            o => o.IsDirectCompositionActive);

    public static readonly DirectProperty<DirectCompositionCaptureHost, bool> IsCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, bool>(
            nameof(IsCaptureInitializing),
            o => o.IsCaptureInitializing);

    public static readonly DirectProperty<DirectCompositionCaptureHost, double> FpsProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, double>(
            nameof(Fps),
            o => o.Fps);

    public static readonly DirectProperty<DirectCompositionCaptureHost, double> FrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, double>(
            nameof(FrameTimeMs),
            o => o.FrameTimeMs);

    public static readonly DirectProperty<DirectCompositionCaptureHost, string> GpuRendererProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, string>(
            nameof(GpuRenderer),
            o => o.GpuRenderer);

    public static readonly DirectProperty<DirectCompositionCaptureHost, string> GpuVendorProperty =
        AvaloniaProperty.RegisterDirect<DirectCompositionCaptureHost, string>(
            nameof(GpuVendor),
            o => o.GpuVendor);

    private readonly record struct CaptureSessionSettings(
        IntPtr TargetHwnd,
        int TargetProcessId,
        bool HideTargetWindowAfterCaptureStarts,
        bool ForceUseTargetClientArea,
        int ClientAreaCropLeftInset,
        int ClientAreaCropTopInset,
        int ClientAreaCropRightInset,
        int ClientAreaCropBottomInset,
        Stretch Stretch,
        double Brightness,
        double Saturation,
        Color ColorTint,
        bool DisableVSync,
        EmulationFrameGenerationMode FrameGenerationMode,
        string? ShaderPath,
        bool ClearShaderWhenPathEmpty,
        bool EnablePillarboxCrop,
        double CaptureWindowAspectRatio,
        bool LowLatencyCapture);

    private readonly BlockingCollection<Action> _rendererQueue = new();
    private readonly Thread _rendererThread;
    private int _rendererThreadId;
    private IntPtr _childHwnd;
    private CaptureSessionSettings? _lastAppliedRenderSettings;
    private IntPtr _session;
    private IntPtr _activeTargetHwnd;
    private bool _isAttached;
    private WindowHandler? _windowHandler;
    private string _statusText = "DirectComposition idle";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";
    private int _lastLoggedState = int.MinValue;
    private string? _lastLoggedDirectCompositionError;
    private int _lastFrameCount;
    private int _lastPresentCount;
    private IntPtr _hostHwnd;
    private int _lastCropX = int.MinValue;
    private int _lastCropY = int.MinValue;
    private int _lastCropWidth = int.MinValue;
    private int _lastCropHeight = int.MinValue;
    private DateTime _lastFpsSampleUtc = DateTime.UtcNow;
    private DateTime _lastPresentSampleUtc = DateTime.UtcNow;
    private string? _lastAppliedShaderPath;
    private string _lastStatusText = string.Empty;
    private CaptureSessionSettings _currentSettings;
    private bool _pendingRenderOptionsRefreshAfterActive;
    private bool _targetHiddenAfterCapture;
    private DateTime _sessionStartedUtc = DateTime.MinValue;
    private EmulationFrameGenerationMode _lastAppliedFrameGenerationMode = EmulationFrameGenerationMode.Off;

    public DirectCompositionCaptureHost()
    {
        _rendererThread = new Thread(RendererThreadMain)
        {
            IsBackground = true,
            Name = "DirectComposition worker"
        };
        _rendererThread.Start();
    }

    private void RendererThreadMain()
    {
        _rendererThreadId = Environment.CurrentManagedThreadId;

        try
        {
            while (!_rendererQueue.IsCompleted)
            {
                try
                {
                    if (_rendererQueue.TryTake(out var action, 50))
                    {
                        action();
                    }
                    else
                    {
                        RefreshStatusCore();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DCompHost] Renderer worker error: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DCompHost] Renderer thread terminated: {ex}");
        }
    }

    private void EnqueueRenderer(Action action)
    {
        if (_rendererQueue.IsAddingCompleted)
            return;

        _rendererQueue.Add(action);
    }

    private void InvokeRenderer(Action action)
    {
        if (Environment.CurrentManagedThreadId == _rendererThreadId)
        {
            action();
            return;
        }

        using var completed = new ManualResetEventSlim(false);
        Exception? capturedException = null;

        EnqueueRenderer(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        completed.Wait();

        if (capturedException != null)
        {
            throw new AggregateException(capturedException);
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private CaptureSessionSettings CaptureSettingsSnapshot()
    {
        return new CaptureSessionSettings(
            TargetHwnd,
            TargetProcessId,
            HideTargetWindowAfterCaptureStarts,
            ForceUseTargetClientArea,
            ClientAreaCropLeftInset,
            ClientAreaCropTopInset,
            ClientAreaCropRightInset,
            ClientAreaCropBottomInset,
            Stretch,
            Brightness,
            Saturation,
            ColorTint,
            DisableVSync,
            FrameGenerationMode,
            ShaderPath,
            ClearShaderWhenPathEmpty,
            EnablePillarboxCrop,
            CaptureWindowAspectRatio,
            LowLatencyCapture);
    }

    private void RequestEnsureSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            SetStatusText("DirectComposition test is Windows-only");
            return;
        }

        if (!_isAttached || _childHwnd == IntPtr.Zero)
            return;

        var settings = CaptureSettingsSnapshot();

        if (settings.TargetHwnd == IntPtr.Zero)
        {
            EnqueueRenderer(() => StopSessionCore(restoreTargetWindow: true));
            SetStatusText("Waiting for emulator HWND");
            return;
        }

        EnqueueRenderer(() => EnsureSessionCore(settings));
    }

    private void RequestRenderOptionsUpdate()
    {
        var settings = CaptureSettingsSnapshot();
        EnqueueRenderer(() => ApplyRenderOptionsCore(settings, force: false));
    }

    private void RequestCropRectUpdate()
    {
        var settings = CaptureSettingsSnapshot();
        EnqueueRenderer(() => ApplyCropRectCore(settings));
    }

    private void QueueStopSession(bool restoreTargetWindow)
    {
        InvokeRenderer(() => StopSessionCore(restoreTargetWindow));
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

    public bool RequestStopSession
    {
        get => GetValue(RequestStopSessionProperty);
        set => SetValue(RequestStopSessionProperty, value);
    }

    public string? TargetWindowTitleHint
    {
        get => GetValue(TargetWindowTitleHintProperty);
        set => SetValue(TargetWindowTitleHintProperty, value);
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

    /// <summary>Width/height ratio for the embedded emulator window (0 = use full host size).</summary>
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
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        RequestEnsureSession();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        QueueStopSession(restoreTargetWindow: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty ||
            change.Property == TargetProcessIdProperty ||
            change.Property == LowLatencyCaptureProperty)
        {
            RequestEnsureSession();
        }
        else if (change.Property == EnablePillarboxCropProperty ||
                 change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == DisableVSyncProperty ||
                 change.Property == FrameGenerationModeProperty ||
                 change.Property == ShaderPathProperty ||
                 change.Property == ClearShaderWhenPathEmptyProperty)
        {
            RequestRenderOptionsUpdate();
        }
        else if (change.Property == ClientAreaCropLeftInsetProperty ||
                 change.Property == ClientAreaCropTopInsetProperty ||
                 change.Property == ClientAreaCropRightInsetProperty ||
                 change.Property == ClientAreaCropBottomInsetProperty)
        {
            RequestCropRectUpdate();
        }
        else if (change.Property == CaptureWindowAspectRatioProperty)
        {
            RequestEnsureSession();
        }
        else if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
        {
            QueueStopSession(restoreTargetWindow: true);
            SetCurrentValue(RequestStopSessionProperty, false);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        _childHwnd = CreateWindowEx(
            0,
            "Static",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren | SsBlackRect,
            0,
            0,
            1,
            1,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_childHwnd == IntPtr.Zero)
        {
            StatusText = "DirectComposition host creation failed";
            return base.CreateNativeControlCore(parent);
        }

        _hostHwnd = parent.Handle;

        return new PlatformHandle(_childHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        QueueStopSession(restoreTargetWindow: false);

        if (_childHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        _hostHwnd = IntPtr.Zero;

        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateChildWindowBounds(finalSize);
        if (_activeTargetHwnd != IntPtr.Zero)
            ApplyCaptureTargetVisibilityPolicy(_activeTargetHwnd, _currentSettings.HideTargetWindowAfterCaptureStarts);
        return arranged;
    }

    private void EnsureSessionCore(CaptureSessionSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            RunOnUiThread(() => StatusText = "DirectComposition test is Windows-only");
            return;
        }

        if (!_isAttached || _childHwnd == IntPtr.Zero)
            return;

        _currentSettings = settings;

        if (settings.TargetHwnd == IntPtr.Zero)
        {
            StopSessionCore(restoreTargetWindow: true);
            RunOnUiThread(() => StatusText = "Waiting for emulator HWND");
            return;
        }

        if (_session != IntPtr.Zero && _activeTargetHwnd == settings.TargetHwnd)
        {
            ApplyRenderOptionsCore(settings, force: false);
            ApplyCropRectCore(settings);
            try
            {
                ApplyCaptureTargetVisibilityPolicy(settings.TargetHwnd, settings.HideTargetWindowAfterCaptureStarts);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            RefreshStatusCore();
            return;
        }

        StopSessionCore(restoreTargetWindow: true);
        _targetHiddenAfterCapture = false;

        if (!UseStaticCaptureDock)
        {
            try
            {
                var mirrorHostHwnd = _childHwnd != IntPtr.Zero ? _childHwnd : _hostHwnd;
                _windowHandler = new WindowHandler(10, 4, 4, 4, 4);
                _windowHandler.SetMoveToHost(true);
                _windowHandler.Start(mirrorHostHwnd, settings.TargetHwnd);
            }
            catch
            {
                _windowHandler?.Stop();
                _windowHandler = null;
            }
        }

        RunOnUiThread(() => IsCaptureInitializing = true);
        RunOnUiThread(() => IsDirectCompositionActive = false);
        RunOnUiThread(() => StatusText = "Starting DirectComposition capture...");
        _session = WgcBridgeApi.CreateDirectCompositionCaptureSession(settings.TargetHwnd, _childHwnd, settings.LowLatencyCapture);
        _activeTargetHwnd = settings.TargetHwnd;
        _currentSettings = settings;
        _sessionStartedUtc = DateTime.UtcNow;

        if (_session == IntPtr.Zero)
        {
            if (_windowHandler != null)
            {
                _windowHandler.Stop();
                _windowHandler = null;
            }
            RunOnUiThread(() => IsCaptureInitializing = false);
            RunOnUiThread(() => StatusText = "DirectComposition session creation failed");
            return;
        }

        try
        {
            Win32API.TryExitFullscreenWindow(settings.TargetHwnd);
            if (Win32API.HasWindowCaption(settings.TargetHwnd))
                Win32API.RemoveWindowDecorations(settings.TargetHwnd);

            if (UseStaticCaptureDock)
            {
                var aspect = settings.CaptureWindowAspectRatio > 0.0 ? settings.CaptureWindowAspectRatio : 0.0;
                Win32API.ParkCaptureWindowAtStaticDock(settings.TargetHwnd, aspect);

                if (settings.HideTargetWindowAfterCaptureStarts)
                {
                    _targetHiddenAfterCapture = true;
                    HideCaptureTarget(settings.TargetHwnd);
                }
            }
            else
            {
                ApplyCaptureTargetVisibilityPolicy(settings.TargetHwnd, settings.HideTargetWindowAfterCaptureStarts);
            }
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        var adapterInfo = WgcBridgeApi.GetDirectCompositionAdapterInfo(_session);
        var rendererName = string.IsNullOrWhiteSpace(adapterInfo.Renderer) ? "Unknown" : adapterInfo.Renderer;
        var vendorName = string.IsNullOrWhiteSpace(adapterInfo.Vendor) ? "Unknown" : adapterInfo.Vendor;
        RunOnUiThread(() => GpuRenderer = rendererName);
        RunOnUiThread(() => GpuVendor = vendorName);
        _lastCropX = int.MinValue;
        _lastCropY = int.MinValue;
        _lastCropWidth = int.MinValue;
        _lastCropHeight = int.MinValue;
        ApplyCropRectCore(settings);
        // Cap WGC readback/scaler work if a non-DComp consumer ever requests CPU frames.
        WgcBridgeApi.SetCaptureMaxResolution(_session, 1920, 1080);
        _lastFrameCount = 0;
        _lastPresentCount = 0;
        _lastFpsSampleUtc = DateTime.UtcNow;
        _lastPresentSampleUtc = _lastFpsSampleUtc;
        ApplyRenderOptionsCore(settings, force: false);
        _pendingRenderOptionsRefreshAfterActive = true;
        RefreshStatusCore();
    }

    private void ApplyCaptureTargetVisibilityPolicy(IntPtr targetHwnd, bool hideAfterCaptureStarts)
    {
        if (targetHwnd == IntPtr.Zero)
            return;

        if (hideAfterCaptureStarts)
        {
            if (_targetHiddenAfterCapture)
                HideCaptureTarget(targetHwnd);
            return;
        }

        Win32API.EnsureVisibleForCapture(targetHwnd);
    }

    private void HideCaptureTarget(IntPtr targetHwnd)
    {
        if (UseStaticCaptureDock)
            Win32API.HideWindowForInPlaceCapture(targetHwnd);
        else
            Win32API.HideWindowForOffScreenCapture(targetHwnd);
    }

    private void TryHideTargetAfterCaptureStarted(int frames, int presents, int state, double captureFps)
    {
        if (!_currentSettings.HideTargetWindowAfterCaptureStarts || _targetHiddenAfterCapture)
            return;

        var captureIsLive = state == 2 ||
                              frames > 0 ||
                              presents > 0 ||
                              captureFps > 0.01;

        if (!captureIsLive)
            return;

        _targetHiddenAfterCapture = true;

        try
        {
            if (!UseStaticCaptureDock && _windowHandler != null)
            {
                _windowHandler.Stop();
                _windowHandler = null;
            }

            if (_activeTargetHwnd != IntPtr.Zero)
                HideCaptureTarget(_activeTargetHwnd);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private void MaintainHiddenCaptureTarget()
    {
        if (!_targetHiddenAfterCapture || _activeTargetHwnd == IntPtr.Zero)
            return;

        try
        {
            HideCaptureTarget(_activeTargetHwnd);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private void UpdateChildWindowBounds(Size size)
    {
        if (!OperatingSystem.IsWindows() || _childHwnd == IntPtr.Zero)
            return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * scaling));
        SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate);
    }

    private void StopSessionCore(bool restoreTargetWindow)
    {
        if (_session != IntPtr.Zero)
        {
            WgcBridgeApi.DestroyCaptureSession(_session);
            _session = IntPtr.Zero;
        }

        var targetToRestore = _activeTargetHwnd;

        if (_windowHandler != null)
        {
            _windowHandler.RestoreOriginalPosition();
            _windowHandler.Stop();
            _windowHandler = null;
        }

        if (targetToRestore != IntPtr.Zero)
        {
            try
            {
                if (restoreTargetWindow)
                    Win32API.RestoreWindowDecorations(targetToRestore);
                else
                    Win32API.ClearSavedWindowState(targetToRestore);

                Win32API.SetWindowOpacity(targetToRestore, 255);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }

        _activeTargetHwnd = IntPtr.Zero;
        _targetHiddenAfterCapture = false;
        _sessionStartedUtc = DateTime.MinValue;
        RunOnUiThread(() => IsCaptureInitializing = false);
        RunOnUiThread(() => IsDirectCompositionActive = false);
        RunOnUiThread(() => Fps = 0);
        RunOnUiThread(() => FrameTimeMs = 0);
        RunOnUiThread(() => GpuRenderer = "Unknown");
        RunOnUiThread(() => GpuVendor = "Unknown");
        _lastFrameCount = 0;
        _lastPresentCount = 0;
        _lastLoggedState = int.MinValue;
        _lastLoggedDirectCompositionError = null;
        _lastCropX = int.MinValue;
        _lastCropY = int.MinValue;
        _lastCropWidth = int.MinValue;
        _lastCropHeight = int.MinValue;
        _lastFpsSampleUtc = DateTime.UtcNow;
        _lastPresentSampleUtc = _lastFpsSampleUtc;
        _lastAppliedShaderPath = null;
        _lastAppliedRenderSettings = null;
        _pendingRenderOptionsRefreshAfterActive = false;
    }

    private void ApplyRenderOptionsCore(CaptureSessionSettings settings, bool force)
    {
        _currentSettings = settings;

        if (_session == IntPtr.Zero)
            return;

        var requestedShaderPath = string.IsNullOrWhiteSpace(settings.ShaderPath) ? null : settings.ShaderPath;
        bool renderOptionsChanged = force || !_lastAppliedRenderSettings.HasValue || !_lastAppliedRenderSettings.Value.Equals(settings);
        bool shaderPathChanged = force || !string.Equals(_lastAppliedShaderPath, requestedShaderPath, StringComparison.OrdinalIgnoreCase);

        if (!renderOptionsChanged && !shaderPathChanged)
            return;

        if (renderOptionsChanged)
        {
            Debug.WriteLine(
                $"[DCompHost] UpdateSessionRenderOptions session=0x{_session.ToString("X")} " +
                $"shader='{requestedShaderPath ?? "<null>"}' stretch={settings.Stretch} brightness={settings.Brightness:0.00} saturation={settings.Saturation:0.00} " +
                $"tint=({settings.ColorTint.R},{settings.ColorTint.G},{settings.ColorTint.B},{settings.ColorTint.A}) disableVsync={settings.DisableVSync}");

            WgcBridgeApi.SetDirectCompositionRenderOptions(
                _session,
                MapStretch(settings.Stretch),
                (float)settings.Brightness,
                (float)settings.Saturation,
                settings.ColorTint.R / 255f,
                settings.ColorTint.G / 255f,
                settings.ColorTint.B / 255f,
                settings.ColorTint.A / 255f,
                settings.DisableVSync);

            WgcBridgeApi.SetDirectCompositionPillarboxCropEnabled(_session, false);
            WgcBridgeApi.SetVrrEnabled(_session, settings.DisableVSync);
        }

        if (force || settings.FrameGenerationMode != _lastAppliedFrameGenerationMode || renderOptionsChanged)
            ApplyFrameGenerationCore(settings);

        if (!shaderPathChanged)
        {
            _lastAppliedRenderSettings = settings;
            return;
        }

        WgcBridgeApi.SetDirectCompositionShader(_session, requestedShaderPath);
        _lastAppliedShaderPath = requestedShaderPath;
        _lastAppliedRenderSettings = settings;
    }

    private void ApplyFrameGenerationCore(CaptureSessionSettings settings)
    {
        _lastAppliedFrameGenerationMode = settings.FrameGenerationMode;

        var software = settings.FrameGenerationMode == EmulationFrameGenerationMode.Software120Hz;
        WgcBridgeApi.SetDirectCompositionFrameGeneration(_session, software, 120);

        if (settings.FrameGenerationMode != EmulationFrameGenerationMode.AmdAfmf)
            return;

        var (renderer, vendor) = WgcBridgeApi.GetDirectCompositionAdapterInfo(_session);
        if (!AmdAfmfCaptureService.IsAmdGpu(renderer, vendor))
        {
            Debug.WriteLine("[DCompHost] AFMF selected but capture GPU does not appear to be AMD.");
            return;
        }

        var status = AmdAfmfCaptureService.TryEnableAfmfViaAdlx();
        Debug.WriteLine($"[DCompHost] AFMF: {status}");
    }

    private void RefreshStatusCore()
    {
        if (_session == IntPtr.Zero)
        {
            if (_currentSettings.TargetHwnd == IntPtr.Zero)
            {
                SetStatusText("Waiting for emulator HWND");
            }

            return;
        }

        ApplyCropRectCore(_currentSettings);

        var state = WgcBridgeApi.GetDirectCompositionState(_session);
        var frames = WgcBridgeApi.GetCaptureStatus(_session);
        var presents = WgcBridgeApi.GetDirectCompositionPresentCount(_session);
        var captureFps = WgcBridgeApi.GetDirectCompositionSmoothedFps(_session);
        TryHideTargetAfterCaptureStarted(frames, presents, state, captureFps);
        MaintainHiddenCaptureTarget();
        var lastError = WgcBridgeApi.GetDirectCompositionLastError(_session);
        if (state != _lastLoggedState || !string.Equals(lastError, _lastLoggedDirectCompositionError, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[DCompHost] RefreshStatus state={state} frames={frames} presents={presents} lastError='{lastError}'");
            _lastLoggedState = state;
            _lastLoggedDirectCompositionError = lastError;
        }
        var now = DateTime.UtcNow;
        captureFps = WgcBridgeApi.GetDirectCompositionSmoothedFps(_session);
        var captureFrameTimeMs = WgcBridgeApi.GetDirectCompositionSmoothedFrameTimeMs(_session);
        if (captureFps > 0.01 || captureFrameTimeMs > 0.01)
        {
            var roundedFps = Math.Round(captureFps, 1);
            var roundedFrameTimeMs = Math.Round(captureFrameTimeMs, 2);
            if (Math.Abs(Fps - roundedFps) > 0.05 || Math.Abs(FrameTimeMs - roundedFrameTimeMs) > 0.05)
            {
                RunOnUiThread(() => Fps = roundedFps);
                RunOnUiThread(() => FrameTimeMs = roundedFrameTimeMs);
            }

            _lastFrameCount = frames;
            _lastPresentCount = presents;
            _lastFpsSampleUtc = now;
            _lastPresentSampleUtc = now;
        }
        else
        {
            var elapsedMs = (now - _lastFpsSampleUtc).TotalMilliseconds;
            if (elapsedMs >= 250)
            {
                var deltaPresents = Math.Max(0, presents - _lastPresentCount);
                if (deltaPresents > 0 && elapsedMs > 0)
                {
                    var fallbackFrameTimeMs = elapsedMs / deltaPresents;
                    var fallbackFps = 1000.0 / Math.Max(fallbackFrameTimeMs, 0.001);
                    RunOnUiThread(() => FrameTimeMs = Math.Round(fallbackFrameTimeMs, 2));
                    RunOnUiThread(() => Fps = Math.Round(fallbackFps, 1));
                }
                else if (presents <= 0 && frames <= 0)
                {
                    RunOnUiThread(() => FrameTimeMs = 0);
                    RunOnUiThread(() => Fps = 0);
                }

                _lastFrameCount = frames;
                _lastPresentCount = presents;
                _lastFpsSampleUtc = now;
                _lastPresentSampleUtc = now;
            }
        }

        RunOnUiThread(() => IsDirectCompositionActive = state == 2);
        RunOnUiThread(() => IsCaptureInitializing = state == 1 || (state == 0 && frames <= 0));

        if (_pendingRenderOptionsRefreshAfterActive && state == 2)
        {
            _pendingRenderOptionsRefreshAfterActive = false;
            ApplyRenderOptionsCore(_currentSettings, force: true);
        }

        if (!IsDirectCompositionActive && state != -1 && (presents > 0 || frames > 0))
        {
            RunOnUiThread(() => IsDirectCompositionActive = true);
            RunOnUiThread(() => IsCaptureInitializing = false);
        }

        var syntheticPresents = WgcBridgeApi.GetDirectCompositionSyntheticPresentCount(_session);
        var frameGenSuffix = _lastAppliedFrameGenerationMode switch
        {
            EmulationFrameGenerationMode.Software120Hz when syntheticPresents > 0 =>
                $" | FG ~{syntheticPresents} synth",
            EmulationFrameGenerationMode.Software120Hz =>
                " | FG on (waiting)",
            EmulationFrameGenerationMode.AmdAfmf =>
                " | AFMF (driver)",
            _ => string.Empty,
        };

        var statusText = state switch
        {
            2 when Fps <= 0.01 && (frames > 0 || presents > 0) => $"DirectComposition active (stalled) | frames {frames} | presents {presents}{frameGenSuffix}",
            2 => $"DirectComposition active | frames {frames} | presents {presents}{frameGenSuffix}",
            1 => $"DirectComposition initializing | frames {frames} | presents {presents}",
            -1 when !string.IsNullOrWhiteSpace(lastError) => $"DirectComposition failed ({lastError}) | frames {frames} | presents {presents}",
            -1 => $"DirectComposition failed | frames {frames} | presents {presents}",
            _ => $"DirectComposition waiting | frames {frames} | presents {presents}"
        };

        SetStatusText(statusText);
    }

    private void SetStatusText(string value)
    {
        if (string.Equals(_lastStatusText, value, StringComparison.Ordinal))
            return;

        _lastStatusText = value;
        RunOnUiThread(() => StatusText = value);
    }

    private void ApplyCropRectCore(CaptureSessionSettings settings)
    {
        if (_session == IntPtr.Zero)
            return;

        ApplyCaptureCropRect(0, 0, 0, 0);
    }

    private void ApplyCaptureCropRect(int x, int y, int width, int height)
    {
        if (x == _lastCropX &&
            y == _lastCropY &&
            width == _lastCropWidth &&
            height == _lastCropHeight)
        {
            return;
        }

        WgcBridgeApi.SetCaptureCropRect(_session, x, y, width, height);
        _lastCropX = x;
        _lastCropY = y;
        _lastCropWidth = width;
        _lastCropHeight = height;
    }

    private static int MapStretch(Stretch stretch)
    {
        return stretch switch
        {
            Stretch.Fill => 0,
            Stretch.Uniform => 1,
            Stretch.UniformToFill => 2,
            _ => 2
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
