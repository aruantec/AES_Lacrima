using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AES_Emulation.Windows.API;
using System.Diagnostics;

namespace AES_Emulation.Windows;

public class DirectCompositionCaptureHost : NativeControlHost
{
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

    public static readonly StyledProperty<string?> TargetWindowTitleHintProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, string?>(nameof(TargetWindowTitleHint), null);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, double>(nameof(Brightness), 1.0);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, double>(nameof(Saturation), 1.0);

    public static readonly StyledProperty<Color> ColorTintProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, Color>(nameof(ColorTint), Colors.White);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(DisableVSync), false);

    public static readonly StyledProperty<string?> ShaderPathProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, string?>(nameof(ShaderPath), null);

    public static readonly StyledProperty<bool> ClearShaderWhenPathEmptyProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(ClearShaderWhenPathEmpty), false);

    public static readonly StyledProperty<bool> ForceUseTargetClientAreaProperty =
        AvaloniaProperty.Register<DirectCompositionCaptureHost, bool>(nameof(ForceUseTargetClientArea), false);

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

    private readonly DispatcherTimer _statusTimer;
    private IntPtr _childHwnd;
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
    private int _lastCropX = int.MinValue;
    private int _lastCropY = int.MinValue;
    private int _lastCropWidth = int.MinValue;
    private int _lastCropHeight = int.MinValue;
    private DateTime _lastFpsSampleUtc = DateTime.UtcNow;
    private DateTime _lastPresentSampleUtc = DateTime.UtcNow;
    private string? _lastAppliedShaderPath;

    public DirectCompositionCaptureHost()
    {
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => RefreshStatus());
    }

    public IntPtr TargetHwnd
    {
        get => GetValue(TargetHwndProperty);
        set => SetValue(TargetHwndProperty, value);
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
        var target = _activeTargetHwnd != IntPtr.Zero ? _activeTargetHwnd : TargetHwnd;
        if (OperatingSystem.IsWindows() && target != IntPtr.Zero)
        {
            Win32API.ForceEmulatorFocus(target, _childHwnd, 0);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        EnsureSession();
        _statusTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _statusTimer.Stop();
        StopSession(restoreTargetWindow: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TargetHwndProperty)
        {
            EnsureSession();
        }
        else if (change.Property == StretchProperty ||
                 change.Property == BrightnessProperty ||
                 change.Property == SaturationProperty ||
                 change.Property == ColorTintProperty ||
                 change.Property == DisableVSyncProperty ||
                 change.Property == ShaderPathProperty ||
                 change.Property == ClearShaderWhenPathEmptyProperty)
        {
            UpdateSessionRenderOptions();
        }
        else if (change.Property == ClientAreaCropLeftInsetProperty ||
                 change.Property == ClientAreaCropTopInsetProperty ||
                 change.Property == ClientAreaCropRightInsetProperty ||
                 change.Property == ClientAreaCropBottomInsetProperty)
        {
            UpdateCaptureCropRect();
        }
        else if (change.Property == RequestStopSessionProperty && change.GetNewValue<bool>())
        {
            StopSession(restoreTargetWindow: false);
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

        return new PlatformHandle(_childHwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopSession(restoreTargetWindow: false);

        if (_windowHandler != null)
        {
            _windowHandler.Stop();
            _windowHandler = null;
        }

        if (_childHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateChildWindowBounds(finalSize);
        return arranged;
    }

    private void EnsureSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            StatusText = "DirectComposition test is Windows-only";
            return;
        }

        if (!_isAttached || _childHwnd == IntPtr.Zero)
            return;

        if (TargetHwnd == IntPtr.Zero)
        {
            StopSession(restoreTargetWindow: false);
            StatusText = "Waiting for emulator HWND";
            return;
        }

        if (_session != IntPtr.Zero && _activeTargetHwnd == TargetHwnd)
        {
            RefreshStatus();
            return;
        }

        StopSession(restoreTargetWindow: false);

        try
        {
            _windowHandler = new WindowHandler(10, 4, 4, 4, 4);
            _windowHandler.EnableRoundedCorners(44);
            _windowHandler.SetMoveToHost(false);
            _windowHandler.Start(_childHwnd, TargetHwnd);
        }
        catch
        {
            _windowHandler?.Stop();
            _windowHandler = null;
        }

        IsCaptureInitializing = true;
        IsDirectCompositionActive = false;
        StatusText = "Starting DirectComposition capture...";
        _session = WgcBridgeApi.CreateDirectCompositionCaptureSession(TargetHwnd, _childHwnd);
        _activeTargetHwnd = TargetHwnd;

        if (_session == IntPtr.Zero)
        {
            if (_windowHandler != null)
            {
                _windowHandler.Stop();
                _windowHandler = null;
            }
            IsCaptureInitializing = false;
            StatusText = "DirectComposition session creation failed";
            return;
        }

        try
        {
            if (HideTargetWindowAfterCaptureStarts)
            {
                Win32API.RemoveWindowDecorations(TargetHwnd);
                Win32API.MoveAway(TargetHwnd, false);
                Win32API.SetWindowOpacity(TargetHwnd, 0);
            }
        }
        catch
        {
        }

        var adapterInfo = WgcBridgeApi.GetDirectCompositionAdapterInfo(_session);
        GpuRenderer = string.IsNullOrWhiteSpace(adapterInfo.Renderer) ? "Unknown" : adapterInfo.Renderer;
        GpuVendor = string.IsNullOrWhiteSpace(adapterInfo.Vendor) ? "Unknown" : adapterInfo.Vendor;
        _lastCropX = int.MinValue;
        _lastCropY = int.MinValue;
        _lastCropWidth = int.MinValue;
        _lastCropHeight = int.MinValue;
        UpdateCaptureCropRect();
        _lastFrameCount = 0;
        _lastPresentCount = 0;
        _lastFpsSampleUtc = DateTime.UtcNow;
        _lastPresentSampleUtc = _lastFpsSampleUtc;
        UpdateSessionRenderOptions();
        RefreshStatus();
    }

    private void UpdateChildWindowBounds(Size size)
    {
        if (!OperatingSystem.IsWindows() || _childHwnd == IntPtr.Zero)
            return;

        var width = Math.Max(1, (int)Math.Ceiling(size.Width));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height));
        SetWindowPos(_childHwnd, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate);
    }

    private void StopSession(bool restoreTargetWindow)
    {
        if (_session != IntPtr.Zero)
        {
            WgcBridgeApi.DestroyCaptureSession(_session);
            _session = IntPtr.Zero;
        }

        if (_windowHandler != null)
        {
            var targetToRestore = _activeTargetHwnd;
            _windowHandler.RestoreOriginalPosition();
            _windowHandler.Stop();
            _windowHandler = null;
            if (restoreTargetWindow && targetToRestore != IntPtr.Zero)
            {
                try
                {
                    Win32API.RestoreWindowDecorations(targetToRestore);
                    Win32API.SetWindowOpacity(targetToRestore, 255);
                }
                catch
                {
                }
            }
        }

        _activeTargetHwnd = IntPtr.Zero;
        IsCaptureInitializing = false;
        IsDirectCompositionActive = false;
        Fps = 0;
        FrameTimeMs = 0;
        GpuRenderer = "Unknown";
        GpuVendor = "Unknown";
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
    }

    private void UpdateSessionRenderOptions()
    {
        if (_session == IntPtr.Zero)
            return;

        Debug.WriteLine(
            $"[DCompHost] UpdateSessionRenderOptions session=0x{_session.ToString("X")} " +
            $"shader='{ShaderPath ?? "<null>"}' stretch={Stretch} brightness={Brightness:0.00} saturation={Saturation:0.00} " +
            $"tint=({ColorTint.R},{ColorTint.G},{ColorTint.B},{ColorTint.A}) disableVsync={DisableVSync}");

        WgcBridgeApi.SetDirectCompositionRenderOptions(
            _session,
            MapStretch(Stretch),
            (float)Brightness,
            (float)Saturation,
            ColorTint.R / 255f,
            ColorTint.G / 255f,
            ColorTint.B / 255f,
            ColorTint.A / 255f,
            DisableVSync);

        var requestedShaderPath = string.IsNullOrWhiteSpace(ShaderPath) ? null : ShaderPath;
        if (requestedShaderPath == null && !ClearShaderWhenPathEmpty && !string.IsNullOrWhiteSpace(_lastAppliedShaderPath))
        {
            Debug.WriteLine(
                $"[DCompHost] Skipping transient shader clear for session=0x{_session.ToString("X")} " +
                $"because last applied shader='{_lastAppliedShaderPath}'.");
            return;
        }

        if (string.Equals(_lastAppliedShaderPath, requestedShaderPath, StringComparison.OrdinalIgnoreCase))
            return;

        WgcBridgeApi.SetDirectCompositionShader(_session, requestedShaderPath);
        _lastAppliedShaderPath = requestedShaderPath;
    }

    private void RefreshStatus()
    {
        if (_session == IntPtr.Zero)
        {
            if (TargetHwnd == IntPtr.Zero)
            {
                StatusText = "Waiting for emulator HWND";
            }

            return;
        }

        UpdateCaptureCropRect();

        var state = WgcBridgeApi.GetDirectCompositionState(_session);
        var frames = WgcBridgeApi.GetCaptureStatus(_session);
        var presents = WgcBridgeApi.GetDirectCompositionPresentCount(_session);
        var lastError = WgcBridgeApi.GetDirectCompositionLastError(_session);
        if (state != _lastLoggedState || !string.Equals(lastError, _lastLoggedDirectCompositionError, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[DCompHost] RefreshStatus state={state} frames={frames} presents={presents} lastError='{lastError}'");
            _lastLoggedState = state;
            _lastLoggedDirectCompositionError = lastError;
        }
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _lastFpsSampleUtc).TotalMilliseconds;
        if (elapsedMs >= 120)
        {
            var deltaFrames = Math.Max(0, frames - _lastFrameCount);
            var fps = deltaFrames * 1000.0 / elapsedMs;
            if (fps > 0.01)
            {
                Fps = Fps <= 0.01 ? fps : (Fps * 0.8) + (fps * 0.2);
            }
            else if (frames <= 0 && presents <= 0)
            {
                Fps = 0;
            }

            _lastFrameCount = frames;
            _lastFpsSampleUtc = now;
        }

        var presentElapsedMs = (now - _lastPresentSampleUtc).TotalMilliseconds;
        var deltaPresents = Math.Max(0, presents - _lastPresentCount);
        if (deltaPresents > 0 && presentElapsedMs >= 1)
        {
            var instantFrameTimeMs = presentElapsedMs / deltaPresents;
            FrameTimeMs = FrameTimeMs <= 0.01
                ? instantFrameTimeMs
                : (FrameTimeMs * 0.82) + (instantFrameTimeMs * 0.18);

            var instantFps = 1000.0 / Math.Max(instantFrameTimeMs, 0.001);
            Fps = Fps <= 0.01
                ? instantFps
                : (Fps * 0.8) + (instantFps * 0.2);

            _lastPresentCount = presents;
            _lastPresentSampleUtc = now;
        }
        else if (presents <= 0 && frames <= 0)
        {
            FrameTimeMs = 0;
        }

        IsDirectCompositionActive = state == 2;
        IsCaptureInitializing = state == 1 || (state == 0 && frames <= 0);

        if (!IsDirectCompositionActive && state != -1 && (presents > 0 || frames > 0))
        {
            IsDirectCompositionActive = true;
            IsCaptureInitializing = false;
        }

        StatusText = state switch
        {
            2 => $"DirectComposition active | frames {frames} | presents {presents}",
            1 => $"DirectComposition initializing | frames {frames} | presents {presents}",
            -1 when !string.IsNullOrWhiteSpace(lastError) => $"DirectComposition failed ({lastError}) | frames {frames} | presents {presents}",
            -1 => $"DirectComposition failed | frames {frames} | presents {presents}",
            _ => $"DirectComposition waiting | frames {frames} | presents {presents}"
        };
    }

    private void UpdateCaptureCropRect()
    {
        if (_session == IntPtr.Zero)
            return;

        if (!ForceUseTargetClientArea || TargetHwnd == IntPtr.Zero)
        {
            ApplyCaptureCropRect(0, 0, 0, 0);
            return;
        }

        try
        {
            if (!Win32API.GetClientAreaOffsets(TargetHwnd, out int cropX, out int cropY, out int cropWidth, out int cropHeight))
            {
                return;
            }

            ApplyClientAreaCropInsets(ref cropX, ref cropY, ref cropWidth, ref cropHeight);

            if (cropWidth <= 0 || cropHeight <= 0)
            {
                ApplyCaptureCropRect(0, 0, 0, 0);
                return;
            }

            ApplyCaptureCropRect(cropX, cropY, cropWidth, cropHeight);
        }
        catch
        {
        }
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

    private void ApplyClientAreaCropInsets(ref int x, ref int y, ref int width, ref int height)
    {
        var leftInset = Math.Max(0, ClientAreaCropLeftInset);
        var topInset = Math.Max(0, ClientAreaCropTopInset);
        var rightInset = Math.Max(0, ClientAreaCropRightInset);
        var bottomInset = Math.Max(0, ClientAreaCropBottomInset);

        if (leftInset == 0 && topInset == 0 && rightInset == 0 && bottomInset == 0)
            return;

        x += leftInset;
        y += topInset;
        width -= leftInset + rightInset;
        height -= topInset + bottomInset;
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
