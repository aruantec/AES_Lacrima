using System;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AES_Emulation.Controls;
using AES_Emulation.Linux.API;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public class LinuxCaptureHost : NativeControlHost
{
    public static readonly StyledProperty<IntPtr> TargetHwndProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, IntPtr>(nameof(TargetHwnd));

    public static readonly StyledProperty<int> TargetProcessIdProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, int>(nameof(TargetProcessId), 0);

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

    public static readonly StyledProperty<EmulatorCaptureMode> CaptureModeProperty =
        AvaloniaProperty.Register<LinuxCaptureHost, EmulatorCaptureMode>(nameof(CaptureMode), EmulatorCaptureMode.DirectComposition);

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
    private IntPtr _display;
    private IntPtr _hostWindow;
    private IntPtr _embeddedTargetWindow;
    private IntPtr _embeddedTargetPreviousParent;
    private bool _embeddedTargetWasVisible;
    private bool _isAttached;
    private double _lastEmbeddedWidth = -1;
    private double _lastEmbeddedHeight = -1;

    private string _statusText = "Idle";
    private bool _isDirectCompositionActive;
    private bool _isCaptureInitializing;
    private double _fps;
    private double _frameTimeMs;
    private string _gpuRenderer = "Unknown";
    private string _gpuVendor = "Unknown";

    public LinuxCaptureHost()
    {
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => RefreshStatus());
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

    public EmulatorCaptureMode CaptureMode
    {
        get => GetValue(CaptureModeProperty);
        set => SetValue(CaptureModeProperty, value);
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
        if (_display == IntPtr.Zero || _embeddedTargetWindow == IntPtr.Zero)
            return;

        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            X11Interop.XRaiseWindow(_display, _embeddedTargetWindow);
            X11Interop.XSetInputFocus(_display, _embeddedTargetWindow, X11Interop.RevertToParent, X11Interop.CurrentTime);
            X11Interop.XFlush(_display);
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        
        AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] Attached to tree. Host HWND: 0x{_hostWindow.ToInt64():X}, Target HWND: 0x{TargetHwnd.ToInt64():X}");
        
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

        if (change.Property == TargetHwndProperty)
        {
            AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] TargetHwnd changed: 0x{change.GetOldValue<IntPtr>().ToInt64():X} -> 0x{change.GetNewValue<IntPtr>().ToInt64():X}");
            EnsureCaptureSession();
        }
        else if (change.Property == TargetWindowTitleHintProperty)
        {
            EnsureCaptureSession();
        }
        else if (change.Property == BoundsProperty)
        {
            ResizeEmbeddedTarget();
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateEmbeddedTargetMapping();
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
                 change.Property == DisableVSyncProperty ||
                 change.Property == ShaderPathProperty ||
                 change.Property == ClearShaderWhenPathEmptyProperty ||
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

        try
        {
            _display = X11Interop.XOpenDisplay(null);
        }
        catch (Exception ex)
        {
            StatusText = $"Linux X11 display unavailable: {ex.Message}";
            return base.CreateNativeControlCore(parent);
        }

        if (_display == IntPtr.Zero)
        {
            StatusText = "Linux X11 display creation failed";
            return base.CreateNativeControlCore(parent);
        }

        _hostWindow = CreateHostWindow(parent.Handle);
        if (_hostWindow == IntPtr.Zero)
        {
            StatusText = "Linux embedded host creation failed";
            X11Interop.XCloseDisplay(_display);
            _display = IntPtr.Zero;
            return base.CreateNativeControlCore(parent);
        }

        EnsureCaptureSession();
        return new PlatformHandle(_hostWindow, "X11Window");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopSession();

        if (_hostWindow != IntPtr.Zero && _display != IntPtr.Zero)
        {
            X11Interop.RunWithIgnoredXErrors(_display, () =>
            {
                X11Interop.XDestroyWindow(_display, _hostWindow);
                X11Interop.XFlush(_display);
            });

            _hostWindow = IntPtr.Zero;
        }

        if (_display != IntPtr.Zero)
        {
            X11Interop.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }

        base.DestroyNativeControlCore(control);
    }

    private void EnsureCaptureSession()
    {
        if (!OperatingSystem.IsLinux())
        {
            StatusText = "Linux embedded host is Linux-only";
            return;
        }

        AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] EnsureCaptureSession: Attached={_isAttached}, Display={_display != IntPtr.Zero}, HostHwnd={_hostWindow != IntPtr.Zero}, TargetHwnd=0x{TargetHwnd.ToInt64():X}");

        if (!_isAttached || _display == IntPtr.Zero || _hostWindow == IntPtr.Zero)
            return;

        if (TargetHwnd == IntPtr.Zero)
        {
            DetachEmbeddedTarget();
            IsCaptureInitializing = true;
            IsDirectCompositionActive = false;
            StatusText = "Waiting for emulator process";
            return;
        }

        AttachEmbeddedTarget(TargetHwnd);
        RefreshStatus();
    }

    private void StopSession()
    {
        DetachEmbeddedTarget();

        IsCaptureInitializing = false;
        IsDirectCompositionActive = false;
        Fps = 0;
        FrameTimeMs = 0;
        StatusText = "Idle";
    }

    private void ApplyRenderOptions()
    {
        ResizeEmbeddedTarget();
    }

    private IntPtr CreateHostWindow(IntPtr parentHandle)
    {
        if (_display == IntPtr.Zero || parentHandle == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr hostWindow = IntPtr.Zero;
        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            hostWindow = X11Interop.XCreateSimpleWindow(_display, parentHandle, 0, 0, 1, 1, 0, 0, 0);
            if (hostWindow != IntPtr.Zero)
            {
                X11Interop.XMapWindow(_display, hostWindow);
                X11Interop.XFlush(_display);
            }
        });

        return hostWindow;
    }

    private void AttachEmbeddedTarget(IntPtr targetWindow)
    {
        if (_display == IntPtr.Zero || _hostWindow == IntPtr.Zero)
            return;

        if (_embeddedTargetWindow == targetWindow)
        {
            UpdateEmbeddedTargetMapping();
            ResizeEmbeddedTarget();
            return;
        }

        DetachEmbeddedTarget();

        _embeddedTargetWasVisible = LinuxWindowHelper.IsWindowVisible(targetWindow);
        _embeddedTargetPreviousParent = GetWindowParent(targetWindow);

        var title = LinuxWindowHelper.GetWindowTitle(targetWindow);
        var className = LinuxWindowHelper.GetWindowClassName(targetWindow);
        AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] Attaching target 0x{targetWindow.ToInt64():X} (Title: '{title}', Class: '{className}') to host 0x{_hostWindow.ToInt64():X}");

        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] Executing XReparentWindow: child=0x{targetWindow.ToInt64():X}, parent=0x{_hostWindow.ToInt64():X}");
            
            X11Interop.XUnmapWindow(_display, targetWindow);
            int result = X11Interop.XReparentWindow(_display, targetWindow, _hostWindow, 0, 0);
            
            AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] XReparentWindow result: {result}");

            if (IsVisible && _embeddedTargetWasVisible)
            {
                AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Info($"[LinuxCaptureHost] Mapping window 0x{targetWindow.ToInt64():X}");
                X11Interop.XMapWindow(_display, targetWindow);
            }

            X11Interop.XFlush(_display);
        });

        _embeddedTargetWindow = targetWindow;
        IsCaptureInitializing = false;
        IsDirectCompositionActive = true;
        StatusText = BuildStatusText();
        ResizeEmbeddedTarget();
    }

    private void DetachEmbeddedTarget()
    {
        if (_display == IntPtr.Zero || _embeddedTargetWindow == IntPtr.Zero)
        {
            _embeddedTargetWindow = IntPtr.Zero;
            _embeddedTargetPreviousParent = IntPtr.Zero;
            _embeddedTargetWasVisible = false;
            _lastEmbeddedWidth = -1;
            _lastEmbeddedHeight = -1;
            return;
        }

        var targetWindow = _embeddedTargetWindow;
        var previousParent = _embeddedTargetPreviousParent != IntPtr.Zero
            ? _embeddedTargetPreviousParent
            : X11Interop.XDefaultRootWindow(_display);

        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            X11Interop.XUnmapWindow(_display, targetWindow);

            if (previousParent != IntPtr.Zero)
            {
                X11Interop.XReparentWindow(_display, targetWindow, previousParent, 0, 0);

                if (_embeddedTargetWasVisible)
                    X11Interop.XMapWindow(_display, targetWindow);
            }

            X11Interop.XFlush(_display);
        });

        _embeddedTargetWindow = IntPtr.Zero;
        _embeddedTargetPreviousParent = IntPtr.Zero;
        _embeddedTargetWasVisible = false;
        _lastEmbeddedWidth = -1;
        _lastEmbeddedHeight = -1;
    }

    private void UpdateEmbeddedTargetMapping()
    {
        if (_display == IntPtr.Zero || _embeddedTargetWindow == IntPtr.Zero)
            return;

        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            if (IsVisible)
            {
                X11Interop.XMapWindow(_display, _embeddedTargetWindow);
                ResizeEmbeddedTarget();
            }
            else
            {
                X11Interop.XUnmapWindow(_display, _embeddedTargetWindow);
            }

            X11Interop.XFlush(_display);
        });
    }

    private void ResizeEmbeddedTarget()
    {
        if (_display == IntPtr.Zero || _embeddedTargetWindow == IntPtr.Zero || _hostWindow == IntPtr.Zero)
            return;

        var width = 0;
        var height = 0;

        var hasHostSize = X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            if (X11Interop.XGetWindowAttributes(_display, _hostWindow, out var attrs) == 0)
                return false;

            width = attrs.width;
            height = attrs.height;
            return width > 1 && height > 1;
        }, false);

        if (!hasHostSize)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var renderScaling = topLevel?.RenderScaling > 0 ? topLevel.RenderScaling : 1.0;
            width = Math.Max(1, (int)Math.Round(Bounds.Width * renderScaling));
            height = Math.Max(1, (int)Math.Round(Bounds.Height * renderScaling));
        }

        if (Math.Abs(_lastEmbeddedWidth - width) < 0.5 && Math.Abs(_lastEmbeddedHeight - height) < 0.5)
            return;

        _lastEmbeddedWidth = width;
        _lastEmbeddedHeight = height;

        AES_Core.Logging.LogHelper.For<LinuxCaptureHost>().Debug($"[LinuxCaptureHost] Resizing embedded window 0x{_embeddedTargetWindow.ToInt64():X} to {width}x{height}");

        X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            X11Interop.XMoveResizeWindow(_display, _embeddedTargetWindow, 0, 0, (uint)width, (uint)height);
            X11Interop.XRaiseWindow(_display, _embeddedTargetWindow);
            X11Interop.XFlush(_display);
        });
    }

    private IntPtr GetWindowParent(IntPtr window)
    {
        if (_display == IntPtr.Zero || window == IntPtr.Zero)
            return IntPtr.Zero;

        return X11Interop.RunWithIgnoredXErrors(_display, () =>
        {
            if (X11Interop.XQueryTree(_display, window, out _, out var parent, out var children, out _) == 0)
                return X11Interop.XDefaultRootWindow(_display);

            if (children != IntPtr.Zero)
                X11Interop.XFree(children);

            return parent != IntPtr.Zero ? parent : X11Interop.XDefaultRootWindow(_display);
        }, IntPtr.Zero);
    }

    private string BuildStatusText()
    {
        var targetLabel = string.IsNullOrWhiteSpace(TargetWindowTitleHint)
            ? "emulator"
            : TargetWindowTitleHint.Trim();

        return _embeddedTargetWindow != IntPtr.Zero
            ? $"Embedded {targetLabel}"
            : $"Waiting for {targetLabel}";
    }

    private void RefreshStatus()
    {
        if (!_isAttached)
            return;

        if (_embeddedTargetWindow != IntPtr.Zero)
        {
            ResizeEmbeddedTarget();

            var status = BuildStatusText();
            if (status != StatusText)
                StatusText = status;

            IsDirectCompositionActive = true;
            IsCaptureInitializing = false;

            Fps = 0;
            FrameTimeMs = 0;

            if (string.IsNullOrWhiteSpace(GpuRenderer) || GpuRenderer == "Unknown")
                GpuRenderer = "Embedded X11";

            if (string.IsNullOrWhiteSpace(GpuVendor) || GpuVendor == "Unknown")
                GpuVendor = "Linux";

            return;
        }

        if (TargetHwnd != IntPtr.Zero)
        {
            IsCaptureInitializing = true;
            IsDirectCompositionActive = false;

            var status = BuildStatusText();
            if (status != StatusText)
                StatusText = status;
        }
        else
        {
            IsCaptureInitializing = false;
            IsDirectCompositionActive = false;

            if (StatusText != "Waiting for emulator process")
                StatusText = "Waiting for emulator process";
        }

        Fps = 0;
        FrameTimeMs = 0;
    }
}
