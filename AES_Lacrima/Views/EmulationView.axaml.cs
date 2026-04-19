using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Threading;
using System.Threading.Tasks;
using AES_Lacrima.ViewModels;
using AES_Controls.Helpers;
using AES_Emulation.Controls;
using AES_Lacrima.Mac.API;
using EmulatorCaptureHostControl = AES_Emulation.Controls.EmulatorCaptureHost;

namespace AES_Lacrima.Views;

public partial class EmulationView : UserControl
{
    private const double PortalLeftOverlapPixels = 0;
    private const double PortalTopOverlapPixels = 0;
    private const double PortalRightOverlapPixels = 0;
    private const double PortalBottomOverlapPixels = 0;
    private const double PortalGraphWidth = 520;
    private const double PortalGraphHeight = 40;
    private static readonly TimeSpan AlbumListPortalMaskDuration = TimeSpan.FromMilliseconds(260);

    public static readonly DirectProperty<EmulationView, bool> IsPortalCaptureInitializingProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsPortalCaptureInitializing),
            o => o.IsPortalCaptureInitializing);

    public static readonly DirectProperty<EmulationView, Color> PortalCaptureTintProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, Color>(
            nameof(PortalCaptureTint),
            o => o.PortalCaptureTint,
            (o, v) => o.PortalCaptureTint = v);

    public static readonly DirectProperty<EmulationView, double> PortalFallbackOpacityProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, double>(
            nameof(PortalFallbackOpacity),
            o => o.PortalFallbackOpacity,
            (o, v) => o.PortalFallbackOpacity = v);

    public static readonly DirectProperty<EmulationView, bool> IsPortalSurfaceVisibleProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsPortalSurfaceVisible),
            o => o.IsPortalSurfaceVisible,
            (o, v) => o.IsPortalSurfaceVisible = v);

    public static readonly DirectProperty<EmulationView, string> PortalStatusTextProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, string>(
            nameof(PortalStatusText),
            o => o.PortalStatusText,
            (o, v) => o.PortalStatusText = v);

    public static readonly DirectProperty<EmulationView, bool> IsPortalDirectCompositionActiveProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsPortalDirectCompositionActive),
            o => o.IsPortalDirectCompositionActive,
            (o, v) => o.IsPortalDirectCompositionActive = v);

    public static readonly DirectProperty<EmulationView, bool> IsPortalFallbackLimitedProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsPortalFallbackLimited),
            o => o.IsPortalFallbackLimited,
            (o, v) => o.IsPortalFallbackLimited = v);

    public static readonly DirectProperty<EmulationView, double> PortalCaptureFpsProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, double>(
            nameof(PortalCaptureFps),
            o => o.PortalCaptureFps,
            (o, v) => o.PortalCaptureFps = v);

    public static readonly DirectProperty<EmulationView, double> PortalCaptureFrameTimeMsProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, double>(
            nameof(PortalCaptureFrameTimeMs),
            o => o.PortalCaptureFrameTimeMs,
            (o, v) => o.PortalCaptureFrameTimeMs = v);

    public static readonly DirectProperty<EmulationView, string> PortalGpuRendererProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, string>(
            nameof(PortalGpuRenderer),
            o => o.PortalGpuRenderer,
            (o, v) => o.PortalGpuRenderer = v);

    public static readonly DirectProperty<EmulationView, string> PortalGpuVendorProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, string>(
            nameof(PortalGpuVendor),
            o => o.PortalGpuVendor,
            (o, v) => o.PortalGpuVendor = v);

    public static readonly DirectProperty<EmulationView, Geometry?> PortalFrametimeGraphGeometryProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, Geometry?>(
            nameof(PortalFrametimeGraphGeometry),
            o => o.PortalFrametimeGraphGeometry,
            (o, v) => o.PortalFrametimeGraphGeometry = v);

    public static readonly DirectProperty<EmulationView, bool> IsAlbumListInteractiveProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsAlbumListInteractive),
            o => o.IsAlbumListInteractive,
            (o, v) => o.IsAlbumListInteractive = v);

    private PortalWindow? _portalWindow;
    private EmulatorCaptureHostControl? _inlineCaptureHost;
    private IDisposable? _boundsSubscription;
    private IDisposable? _mainWindowBoundsSubscription;
    private IDisposable? _mainWindowStateSubscription;
    private IDisposable? _captureInitializingSubscription;
    private IDisposable? _captureStatusSubscription;
    private IDisposable? _captureActiveSubscription;
    private IDisposable? _captureFpsSubscription;
    private IDisposable? _captureFrameTimeSubscription;
    private IDisposable? _captureGpuRendererSubscription;
    private IDisposable? _captureGpuVendorSubscription;
    private bool _isPortalCaptureInitializing;
    private Color _portalCaptureTint = Colors.White;
    private double _portalFallbackOpacity;
    private bool _isPortalSurfaceVisible;
    private string _portalStatusText = "DirectComposition idle";
    private bool _isPortalDirectCompositionActive;
    private bool _isPortalFallbackLimited;
    private double _portalCaptureFps;
    private double _portalCaptureFrameTimeMs;
    private string _portalGpuRenderer = "Unknown";
    private string _portalGpuVendor = "Unknown";
    private Geometry? _portalFrametimeGraphGeometry;
    private CancellationTokenSource? _portalBrightnessFadeCancellation;
    private bool _isAlbumListInteractive = true;
    private readonly Queue<double> _portalFrameSamples = new();
    private const int PortalFrameSampleCount = 180;
    private readonly Transitions _albumListTransitions =
    [
        new DoubleTransition
        {
            Property = MaxHeightProperty,
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = new CubicEaseOut()
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut()
        }
    ];
    private bool _portalSyncPending;
    private DateTime _lastLayoutSyncUtc = DateTime.MinValue;
    private int _portalMaskVersion;
    private PixelPoint _lastPortalPosition = new(int.MinValue, int.MinValue);
    private Size _lastPortalSize = new(double.NaN, double.NaN);
    private bool _isPortalWindowFullscreen;
    private PixelPoint _portalWindowFullscreenPosition;
    private Size _portalWindowFullscreenSize;
    private PortalFullscreenOverlayWindow? _portalFullscreenOverlayWindow;

    private static bool UseInlineCaptureHost => false;

    private EmulatorCaptureHostControl? ActiveCaptureHost
        => UseInlineCaptureHost ? _inlineCaptureHost : _portalWindow?.CaptureHostControl;

    public EmulationView()
    {
        InitializeComponent();
        var captureHost = this.FindControl<Border>("EmulatorCaptureHost");
        captureHost?.AddHandler(InputElement.PointerPressedEvent, OnCaptureHostPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        var portalSurface = this.FindControl<Control>("PortalPortal");
        portalSurface?.AddHandler(InputElement.PointerPressedEvent, OnPortalSurfacePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, OnEmulationViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        LayoutUpdated += OnViewLayoutUpdated;
        DataContextChanged += OnDataContextChanged;
        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;
        PortalStatusText = "DirectComposition idle";
        PortalGpuRenderer = "Unknown";
        PortalGpuVendor = "Unknown";
        IsAlbumListInteractive = true;
    }

    private void OnViewLayoutUpdated(object? sender, EventArgs e)
    {
        if (UseInlineCaptureHost || _portalWindow == null || _isPortalWindowFullscreen)
            return;

        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastLayoutSyncUtc).TotalMilliseconds < 120)
            return;

        _lastLayoutSyncUtc = now;
        SyncPortalWindow();
    }

    private void OnCaptureHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (DataContext is EmulationViewModel vm)
            {
                vm.IsFullscreen = !vm.IsFullscreen;
            }
            e.Handled = true;
            return;
        }

        if (_isPortalWindowFullscreen)
            return;

        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        ActiveCaptureHost?.ForwardFocusToTarget();
    }

    private void OnPortalSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        ActiveCaptureHost?.ForwardFocusToTarget();
    }

    private void OnEmulationViewKeyDown(object? sender, KeyEventArgs e)
    {
        // Prevent Avalonia focus traversal while native capture is active.
        // This avoids layout-feedback loops and keeps input ownership stable.
        if (e.Key != Key.Tab)
            return;

        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        ActiveCaptureHost?.ForwardFocusToTarget();
        e.Handled = true;
    }

    public bool IsPortalCaptureInitializing
    {
        get => _isPortalCaptureInitializing;
        private set => SetAndRaise(IsPortalCaptureInitializingProperty, ref _isPortalCaptureInitializing, value);
    }

    public Color PortalCaptureTint
    {
        get => _portalCaptureTint;
        set
        {
            if (SetAndRaise(PortalCaptureTintProperty, ref _portalCaptureTint, value))
            {
                var captureControl = ActiveCaptureHost;
                if (captureControl != null && captureControl.ColorTint != value)
                {
                    captureControl.ColorTint = value;
                }
            }
        }
    }

    public double PortalFallbackOpacity
    {
        get => _portalFallbackOpacity;
        set => SetAndRaise(PortalFallbackOpacityProperty, ref _portalFallbackOpacity, value);
    }

    public bool IsPortalSurfaceVisible
    {
        get => _isPortalSurfaceVisible;
        set => SetAndRaise(IsPortalSurfaceVisibleProperty, ref _isPortalSurfaceVisible, value);
    }

    public string PortalStatusText
    {
        get => _portalStatusText;
        set
        {
            if (SetAndRaise(PortalStatusTextProperty, ref _portalStatusText, value))
                UpdatePortalLinuxFallbackState();
        }
    }

    public bool IsPortalDirectCompositionActive
    {
        get => _isPortalDirectCompositionActive;
        set => SetAndRaise(IsPortalDirectCompositionActiveProperty, ref _isPortalDirectCompositionActive, value);
    }

    public bool IsPortalFallbackLimited
    {
        get => _isPortalFallbackLimited;
        set => SetAndRaise(IsPortalFallbackLimitedProperty, ref _isPortalFallbackLimited, value);
    }

    public double PortalCaptureFps
    {
        get => _portalCaptureFps;
        set => SetAndRaise(PortalCaptureFpsProperty, ref _portalCaptureFps, value);
    }

    public double PortalCaptureFrameTimeMs
    {
        get => _portalCaptureFrameTimeMs;
        set
        {
            if (SetAndRaise(PortalCaptureFrameTimeMsProperty, ref _portalCaptureFrameTimeMs, value))
            {
                UpdatePortalFrametimeGraph(value);
            }
        }
    }

    public string PortalGpuRenderer
    {
        get => _portalGpuRenderer;
        set
        {
            if (SetAndRaise(PortalGpuRendererProperty, ref _portalGpuRenderer, value))
                UpdatePortalLinuxFallbackState();
        }
    }

    public string PortalGpuVendor
    {
        get => _portalGpuVendor;
        set => SetAndRaise(PortalGpuVendorProperty, ref _portalGpuVendor, value);
    }

    public Geometry? PortalFrametimeGraphGeometry
    {
        get => _portalFrametimeGraphGeometry;
        set => SetAndRaise(PortalFrametimeGraphGeometryProperty, ref _portalFrametimeGraphGeometry, value);
    }

    public bool IsAlbumListInteractive
    {
        get => _isAlbumListInteractive;
        set => SetAndRaise(IsAlbumListInteractiveProperty, ref _isAlbumListInteractive, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is Window mainWindow)
        {
            if (UseInlineCaptureHost)
            {
                EnsureInlineCaptureHost();
                AttachPortalCaptureBindings();
            }
            else if (_portalWindow == null)
            {
                _portalWindow = new PortalWindow();
                _portalWindow.DataContext = DataContext;

                _portalWindow.Show();
                _portalWindow.Hide();
                AttachPortalCaptureBindings();

                var portal = this.FindControl<Control>("PortalPortal");
                if (portal != null)
                {
                    _boundsSubscription = portal.GetObservable(Visual.BoundsProperty).Subscribe(new SimpleObserver<Rect>(_ => SyncPortalWindow()));
                }

                _mainWindowBoundsSubscription = mainWindow.GetObservable(Visual.BoundsProperty)
                    .Subscribe(new SimpleObserver<Rect>(_ => SyncPortalWindow()));

                _mainWindowStateSubscription = mainWindow.GetObservable(Window.WindowStateProperty)
                    .Subscribe(new SimpleObserver<WindowState>(OnMainWindowStateChanged));
                mainWindow.PositionChanged += OnMainWindowPositionChanged;
            }

            mainWindow.Activated += OnMainWindowActivated;
            mainWindow.Deactivated += OnMainWindowDeactivated;
        }

        if (DataContext is EmulationViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdatePortalVisibility(vm);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        var captureHost = this.FindControl<Border>("EmulatorCaptureHost");
        captureHost?.RemoveHandler(InputElement.PointerPressedEvent, OnCaptureHostPointerPressed);
        var portalSurface = this.FindControl<Control>("PortalPortal");
        portalSurface?.RemoveHandler(InputElement.PointerPressedEvent, OnPortalSurfacePointerPressed);
        RemoveHandler(InputElement.KeyDownEvent, OnEmulationViewKeyDown);
        LayoutUpdated -= OnViewLayoutUpdated;

        if (DataContext is EmulationViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundsSubscription?.Dispose();
        _mainWindowBoundsSubscription?.Dispose();
        _mainWindowStateSubscription?.Dispose();
        _captureInitializingSubscription?.Dispose();
        _captureStatusSubscription?.Dispose();
        _captureActiveSubscription?.Dispose();
        _captureFpsSubscription?.Dispose();
        _captureFrameTimeSubscription?.Dispose();
        _captureGpuRendererSubscription?.Dispose();
        _captureGpuVendorSubscription?.Dispose();

        if (TopLevel.GetTopLevel(this) is Window mainWindow)
        {
            if (!UseInlineCaptureHost)
                mainWindow.PositionChanged -= OnMainWindowPositionChanged;
            mainWindow.Activated -= OnMainWindowActivated;
            mainWindow.Deactivated -= OnMainWindowDeactivated;
        }

        if (_portalWindow != null)
        {
            _portalWindow.Close();
            _portalWindow = null;
        }

        if (_inlineCaptureHost != null)
        {
            if (this.FindControl<Border>("EmulatorCaptureHost") is { } captureHostBorder &&
                ReferenceEquals(captureHostBorder.Child, _inlineCaptureHost))
            {
                captureHostBorder.Child = null;
            }

            _inlineCaptureHost = null;
        }

        if (_portalFullscreenOverlayWindow != null)
        {
            _portalFullscreenOverlayWindow.Close();
            _portalFullscreenOverlayWindow = null;
        }

        IsPortalCaptureInitializing = false;
        IsPortalDirectCompositionActive = false;
        PortalStatusText = "DirectComposition idle";
        PortalCaptureFps = 0;
        PortalCaptureFrameTimeMs = 0;
        PortalGpuRenderer = "Unknown";
        PortalGpuVendor = "Unknown";
        IsPortalFallbackLimited = false;
        _portalFrameSamples.Clear();
        PortalFrametimeGraphGeometry = null;
        IsAlbumListInteractive = true;
        _portalSyncPending = false;
        _lastPortalPosition = new PixelPoint(int.MinValue, int.MinValue);
        _lastPortalSize = new Size(double.NaN, double.NaN);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_portalWindow != null)
        {
            _portalWindow.DataContext = DataContext;
        }

        EnsureInlineCaptureHost();
        AttachPortalCaptureBindings();

        if (DataContext is EmulationViewModel vm)
        {
            UpdatePortalVisibility(vm);
        }
        else
        {
            HidePortal();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EmulationViewModel vm)
            return;

        if (e.PropertyName == nameof(EmulationViewModel.IsActive) ||
            e.PropertyName == nameof(EmulationViewModel.IsCompositionCaptureVisible) ||
            e.PropertyName == nameof(EmulationViewModel.IsEmulatorViewportVisible) ||
            e.PropertyName == nameof(EmulationViewModel.IsAlbumListCollapsed) ||
            e.PropertyName == nameof(EmulationViewModel.IsRenderOptionsOpen))
        {
            UpdateAlbumListTransitions(vm);
            UpdatePortalVisibility(vm);
            UpdateInlineCaptureHostVisibility(vm);
            if (e.PropertyName == nameof(EmulationViewModel.IsAlbumListCollapsed))
            {
                StartAlbumListPortalMask(vm);
                SyncPortalWindow();
            }
            else if (e.PropertyName == nameof(EmulationViewModel.IsRenderOptionsOpen))
            {
                SyncPortalWindow();
                if (!vm.IsRenderOptionsOpen)
                {
                    IsAlbumListInteractive = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
                            ActiveCaptureHost?.ForwardFocusToTarget();
                    }, DispatcherPriority.Input);
                }
            }
        }
        else if (e.PropertyName == nameof(EmulationViewModel.IsFullscreen))
        {
            var mainWindow = TopLevel.GetTopLevel(this) as Window;
            if (mainWindow == null)
                return;

            if (vm.IsFullscreen && !_isPortalWindowFullscreen)
            {
                EnterPortalFullscreen(mainWindow);
            }
            else if (!vm.IsFullscreen && _isPortalWindowFullscreen)
            {
                ExitPortalFullscreen();
            }
        }
    }

    private void UpdatePortalVisibility(EmulationViewModel vm)
    {
        UpdateAlbumListTransitions(vm);

        if (vm.IsCompositionCaptureVisible)
        {
            ShowPortal();
        }
        else
        {
            HidePortal();
        }

        UpdateInlineCaptureHostVisibility(vm);
    }

    private void OnMainWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (UseInlineCaptureHost)
            return;

        SyncPortalWindow();
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
        {
            ShowPortal();
            if (!UseInlineCaptureHost)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
                    {
                        SyncPortalWindow();
                        UpdateWindowZOrder();
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    private void OnMainWindowDeactivated(object? sender, EventArgs e)
    {
        if (!UseInlineCaptureHost)
            UpdateWindowZOrder();
    }

    private void OnMainWindowStateChanged(WindowState state)
    {
        if (UseInlineCaptureHost)
            return;

        if (_portalWindow == null)
            return;

        if (state == WindowState.Minimized)
        {
            _portalWindow.Hide();
        }
        else if (DataContext is EmulationViewModel vm && vm.IsCompositionCaptureVisible)
        {
            ShowPortal();
        }
    }

    private void ShowPortal()
    {
        if (UseInlineCaptureHost)
        {
            EnsureInlineCaptureHost();
            if (_inlineCaptureHost == null)
                return;

            if (DataContext is EmulationViewModel vm)
                UpdateInlineCaptureHostVisibility(vm);

            PortalFallbackOpacity = 0;
            IsPortalSurfaceVisible = false;
            return;
        }

        if (_portalWindow != null)
        {
            var wasSurfaceVisible = IsPortalSurfaceVisible;
            if (!wasSurfaceVisible)
            {
                PortalFallbackOpacity = 1;
                IsPortalSurfaceVisible = false;
            }

            _portalWindow.Show();
            SyncPortalWindowCore();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                LinuxWindowPlacement.TryConfigureClickThrough(_portalWindow);
            UpdateWindowZOrder();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && TopLevel.GetTopLevel(this) is Window mainWindow)
            {
                mainWindow.Activate();
            }

            if (!wasSurfaceVisible)
            {
                ResetPortalCaptureBrightness();
                IsPortalSurfaceVisible = true;
                PortalFallbackOpacity = 0;
                StartPortalBrightnessFade();
            }

            if (!wasSurfaceVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is EmulationViewModel { IsActive: true } && _portalWindow?.IsVisible == true)
                    {
                        SyncPortalWindowCore();
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && TopLevel.GetTopLevel(this) is Window mainWindow)
                        {
                            mainWindow.Activate();
                        }
                        IsPortalSurfaceVisible = true;
                        PortalFallbackOpacity = 0;
                        StartPortalBrightnessFade();
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    private void HidePortal()
    {
        _portalBrightnessFadeCancellation?.Cancel();

        if (UseInlineCaptureHost)
        {
            PortalFallbackOpacity = 1;
            IsPortalSurfaceVisible = false;

            if (_inlineCaptureHost != null)
                _inlineCaptureHost.IsVisible = false;

            return;
        }

        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;

        if (DataContext is EmulationViewModel vm)
            vm.PortalCaptureBrightness = 0;

        if (_isPortalWindowFullscreen && DataContext is EmulationViewModel vmFullscreen)
        {
            vmFullscreen.IsFullscreen = false;
        }

        _portalWindow?.Hide();
        HidePortalFullscreenOverlay();
        _isPortalWindowFullscreen = false;
    }

    private void ResetPortalCaptureBrightness()
    {
        if (DataContext is EmulationViewModel vm)
            vm.PortalCaptureBrightness = 0;
    }

    private void StartPortalBrightnessFade()
    {
        if (DataContext is not EmulationViewModel vm)
            return;

        _portalBrightnessFadeCancellation?.Cancel();
        _portalBrightnessFadeCancellation = new CancellationTokenSource();
        var token = _portalBrightnessFadeCancellation.Token;

        _ = FadePortalCaptureBrightnessAsync(vm, token);
    }

    private async Task FadePortalCaptureBrightnessAsync(EmulationViewModel vm, CancellationToken token)
    {
        const int steps = 16;
        var duration = TimeSpan.FromMilliseconds(320);
        var delay = TimeSpan.FromMilliseconds(duration.TotalMilliseconds / steps);

        for (var step = 1; step <= steps; step++)
        {
            if (token.IsCancellationRequested)
                return;

            var progress = step / (double)steps;
            var targetBrightness = vm.RenderBrightness;
            var nextBrightness = targetBrightness * progress;

            Dispatcher.UIThread.Post(() => vm.PortalCaptureBrightness = nextBrightness);

            try
            {
                await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (!token.IsCancellationRequested)
            Dispatcher.UIThread.Post(() => vm.PortalCaptureBrightness = vm.RenderBrightness);
    }

    private void TogglePortalFullscreen()
    {
        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow)
            return;

        if (_isPortalWindowFullscreen)
        {
            ExitPortalFullscreen();
        }
        else
        {
            EnterPortalFullscreen(mainWindow);
        }
    }

    private void EnterPortalFullscreen(Window mainWindow)
    {
        if (_portalWindow == null)
            return;

        _portalWindowFullscreenPosition = _portalWindow.Position;
        _portalWindowFullscreenSize = new Size(_portalWindow.Width, _portalWindow.Height);
        var screen = mainWindow.Screens?.ScreenFromWindow(mainWindow) ?? mainWindow.Screens?.Primary;
        var bounds = screen?.Bounds ?? new PixelRect(0, 0, 0, 0);

        _portalWindow.Position = bounds.Position;
        _portalWindow.Width = bounds.Width;
        _portalWindow.Height = bounds.Height;
        _portalWindow.Topmost = false;

        if (_portalFullscreenOverlayWindow == null)
        {
            _portalFullscreenOverlayWindow = new PortalFullscreenOverlayWindow();
            _portalFullscreenOverlayWindow.DoubleClicked += OnPortalFullscreenOverlayDoubleClicked;
        }

        _portalFullscreenOverlayWindow.Position = bounds.Position;
        _portalFullscreenOverlayWindow.Width = bounds.Width;
        _portalFullscreenOverlayWindow.Height = bounds.Height;
        _portalFullscreenOverlayWindow.Topmost = true;
        _portalFullscreenOverlayWindow.Show();
        _portalFullscreenOverlayWindow.Activate();
        _portalFullscreenOverlayWindow.Focus();

        _isPortalWindowFullscreen = true;
    }

    private void ExitPortalFullscreen()
    {
        if (_portalWindow == null)
            return;

        _portalFullscreenOverlayWindow?.Hide();

        _portalWindow.Position = _portalWindowFullscreenPosition;
        _portalWindow.Width = _portalWindowFullscreenSize.Width;
        _portalWindow.Height = _portalWindowFullscreenSize.Height;
        _portalWindow.Topmost = false;

        _isPortalWindowFullscreen = false;
        SyncPortalWindow();
    }

    private void OnPortalFullscreenOverlayDoubleClicked(object? sender, EventArgs e)
    {
        if (DataContext is EmulationViewModel vm)
        {
            vm.IsFullscreen = false;
        }
    }

    private void HidePortalFullscreenOverlay()
    {
        if (_portalFullscreenOverlayWindow == null)
            return;

        _portalFullscreenOverlayWindow.Hide();
    }

    private void SyncPortalWindow()
    {
        if (UseInlineCaptureHost)
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            SyncPortalWindowCore();
            return;
        }

        if (_portalSyncPending)
            return;

        _portalSyncPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _portalSyncPending = false;
            SyncPortalWindowCore();
        }, DispatcherPriority.Render);
    }

    private void SyncPortalWindowCore()
    {
        if (UseInlineCaptureHost)
            return;

        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow || _isPortalWindowFullscreen) return;

        var portal = this.FindControl<Control>("PortalPortal");
        if (portal == null) return;

        var topLeft = portal.TranslatePoint(new Point(0, 0), mainWindow);
        var bottomRight = portal.TranslatePoint(new Point(portal.Bounds.Width, portal.Bounds.Height), mainWindow);
        if (topLeft == null || bottomRight == null) return;

        PixelPoint screenTopLeft;
        PixelPoint screenBottomRight;
        var mainWindowHandle = mainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            mainWindowHandle != IntPtr.Zero &&
            MacSystemDialogs.TryConvertContentPointToScreen(mainWindowHandle, topLeft.Value.X, topLeft.Value.Y, out var topLeftScreenX, out var topLeftScreenY) &&
            MacSystemDialogs.TryConvertContentPointToScreen(mainWindowHandle, bottomRight.Value.X, bottomRight.Value.Y, out var bottomRightScreenX, out var bottomRightScreenY))
        {
            screenTopLeft = new PixelPoint((int)Math.Round(topLeftScreenX), (int)Math.Round(topLeftScreenY));
            screenBottomRight = new PixelPoint((int)Math.Round(bottomRightScreenX), (int)Math.Round(bottomRightScreenY));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var renderScale = Math.Max(1.0, mainWindow.RenderScaling);
            screenTopLeft = new PixelPoint(
                mainWindow.Position.X + (int)Math.Round(topLeft.Value.X * renderScale),
                mainWindow.Position.Y + (int)Math.Round(topLeft.Value.Y * renderScale));
            screenBottomRight = new PixelPoint(
                mainWindow.Position.X + (int)Math.Round(bottomRight.Value.X * renderScale),
                mainWindow.Position.Y + (int)Math.Round(bottomRight.Value.Y * renderScale));
        }
        else
        {
            screenTopLeft = mainWindow.PointToScreen(topLeft.Value);
            screenBottomRight = mainWindow.PointToScreen(bottomRight.Value);
        }

        const int portalExpansionPixels = 1;
        var widthPixels = Math.Max(0, screenBottomRight.X - screenTopLeft.X) + (int)PortalLeftOverlapPixels + (int)PortalRightOverlapPixels + portalExpansionPixels * 2;
        var heightPixels = Math.Max(0, screenBottomRight.Y - screenTopLeft.Y) + (int)PortalTopOverlapPixels + (int)PortalBottomOverlapPixels + portalExpansionPixels * 2;
        var portalRenderScaling = Math.Max(0.0001, _portalWindow.RenderScaling > 0 ? _portalWindow.RenderScaling : mainWindow.RenderScaling);
        var width = Math.Ceiling(widthPixels / portalRenderScaling);
        var height = Math.Ceiling(heightPixels / portalRenderScaling);

        var portalPosition = new PixelPoint(
            screenTopLeft.X - (int)PortalLeftOverlapPixels - portalExpansionPixels,
            screenTopLeft.Y - (int)PortalTopOverlapPixels - portalExpansionPixels);
        var portalSize = new Size(width, height);
        if (_lastPortalPosition == portalPosition && _lastPortalSize == portalSize)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _portalWindow.MoveResizeUnconstrained(portalPosition, widthPixels, heightPixels);
                LinuxWindowPlacement.TryConfigureClickThrough(_portalWindow);
            }
            UpdateWindowZOrder();
            return;
        }

        _lastPortalPosition = portalPosition;
        _lastPortalSize = portalSize;
        _portalWindow.Position = portalPosition;
        _portalWindow.Width = width;
        _portalWindow.Height = height;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _portalWindow.MoveResizeUnconstrained(portalPosition, widthPixels, heightPixels);
            LinuxWindowPlacement.TryConfigureClickThrough(_portalWindow);
        }
        UpdateWindowZOrder();
    }

    private void AttachPortalCaptureBindings()
    {
        _captureInitializingSubscription?.Dispose();
        _captureInitializingSubscription = null;
        _captureStatusSubscription?.Dispose();
        _captureStatusSubscription = null;
        _captureActiveSubscription?.Dispose();
        _captureActiveSubscription = null;
        _captureFpsSubscription?.Dispose();
        _captureFpsSubscription = null;
        _captureFrameTimeSubscription?.Dispose();
        _captureFrameTimeSubscription = null;
        _captureGpuRendererSubscription?.Dispose();
        _captureGpuRendererSubscription = null;
        _captureGpuVendorSubscription?.Dispose();
        _captureGpuVendorSubscription = null;

        var captureControl = ActiveCaptureHost;
        if (captureControl == null)
        {
            IsPortalCaptureInitializing = false;
            IsPortalDirectCompositionActive = false;
            PortalStatusText = "DirectComposition unavailable";
            PortalCaptureFps = 0;
            PortalCaptureFrameTimeMs = 0;
            PortalGpuRenderer = "Unknown";
            PortalGpuVendor = "Unknown";
            return;
        }

        captureControl.ColorTint = PortalCaptureTint;
        IsPortalCaptureInitializing = captureControl.IsCaptureInitializing;
        IsPortalDirectCompositionActive = captureControl.IsDirectCompositionActive;
        PortalStatusText = captureControl.StatusText;
        PortalCaptureFps = captureControl.Fps;
        PortalCaptureFrameTimeMs = captureControl.FrameTimeMs;
        PortalGpuRenderer = captureControl.GpuRenderer;
        PortalGpuVendor = captureControl.GpuVendor;

        _captureInitializingSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.IsCaptureInitializingProperty)
            .Subscribe(new SimpleObserver<bool>(value => IsPortalCaptureInitializing = value));

        _captureStatusSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.StatusTextProperty)
            .Subscribe(new SimpleObserver<string>(value => PortalStatusText = value));

        _captureActiveSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.IsDirectCompositionActiveProperty)
            .Subscribe(new SimpleObserver<bool>(value => IsPortalDirectCompositionActive = value));

        _captureFpsSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.FpsProperty)
            .Subscribe(new SimpleObserver<double>(value => PortalCaptureFps = value));

        _captureFrameTimeSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.FrameTimeMsProperty)
            .Subscribe(new SimpleObserver<double>(value => PortalCaptureFrameTimeMs = value));

        _captureGpuRendererSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.GpuRendererProperty)
            .Subscribe(new SimpleObserver<string>(value => PortalGpuRenderer = value));

        _captureGpuVendorSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.GpuVendorProperty)
            .Subscribe(new SimpleObserver<string>(value => PortalGpuVendor = value));
    }

    private void EnsureInlineCaptureHost()
    {
        if (!UseInlineCaptureHost || _inlineCaptureHost != null)
            return;

        var captureHostBorder = this.FindControl<Border>("EmulatorCaptureHost");
        if (captureHostBorder == null)
            return;

        var captureHost = new EmulatorCaptureHostControl
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsVisible = false
        };

        captureHost.Bind(EmulatorCaptureHostControl.TargetHwndProperty, new Binding("EmulatorTargetHwnd"));
        captureHost.Bind(EmulatorCaptureHostControl.TargetProcessIdProperty, new Binding("EmulatorTargetProcessId"));
        captureHost.Bind(EmulatorCaptureHostControl.TargetWindowTitleHintProperty, new Binding("CurrentEmulatorWindowTitleHint"));
        captureHost.Bind(EmulatorCaptureHostControl.ForceUseTargetClientAreaProperty, new Binding("ForceUseTargetClientAreaCapture"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropLeftInsetProperty, new Binding("ClientAreaCropLeftInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropTopInsetProperty, new Binding("ClientAreaCropTopInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropRightInsetProperty, new Binding("ClientAreaCropRightInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropBottomInsetProperty, new Binding("ClientAreaCropBottomInset"));
        captureHost.Bind(EmulatorCaptureHostControl.HideTargetWindowAfterCaptureStartsProperty, new Binding("HideTargetWindowAfterCaptureStarts"));
        captureHost.Bind(EmulatorCaptureHostControl.StretchProperty, new Binding("SelectedStretch") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.BrightnessProperty, new Binding("PortalCaptureBrightness") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.SaturationProperty, new Binding("RenderSaturation") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.DisableVSyncProperty, new Binding("DisableVSync") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.ShaderPathProperty, new Binding("SelectedShaderPath"));
        captureHost.Bind(EmulatorCaptureHostControl.ClearShaderWhenPathEmptyProperty, new Binding("ClearShaderWhenPathEmpty"));
        captureHost.Bind(EmulatorCaptureHostControl.RequestStopSessionProperty, new Binding("RequestStopEmulatorCapture") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.CaptureModeProperty, new Binding("SelectedCaptureMode"));

        captureHostBorder.Child = captureHost;
        _inlineCaptureHost = captureHost;

        if (DataContext is EmulationViewModel vm)
            UpdateInlineCaptureHostVisibility(vm);
    }

    private void UpdateInlineCaptureHostVisibility(EmulationViewModel vm)
    {
        if (!UseInlineCaptureHost || _inlineCaptureHost == null)
            return;

        _inlineCaptureHost.IsVisible = vm.IsCompositionCaptureVisible;
    }

    private void UpdatePortalFrametimeGraph(double latestMs)
    {
        if (_portalFrameSamples.Count >= PortalFrameSampleCount)
        {
            _portalFrameSamples.Dequeue();
        }

        _portalFrameSamples.Enqueue(latestMs);
        if (_portalFrameSamples.Count < 2)
        {
            PortalFrametimeGraphGeometry = null;
            return;
        }

        const double maxMs = 50;
        var samples = _portalFrameSamples.ToArray();
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var x = i * (PortalGraphWidth / (samples.Length - 1));
                var clamped = Math.Clamp(samples[i], 0, maxMs);
                var y = PortalGraphHeight - (clamped / maxMs * PortalGraphHeight);
                var point = new Point(x, y);
                if (i == 0)
                {
                    ctx.BeginFigure(point, false);
                }
                else
                {
                    ctx.LineTo(point);
                }
            }
        }

        PortalFrametimeGraphGeometry = geometry;
    }

    private void UpdatePortalLinuxFallbackState()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            IsPortalFallbackLimited = false;
            return;
        }

        var statusFallback = !string.IsNullOrWhiteSpace(PortalStatusText) &&
            PortalStatusText.Contains("fallback", StringComparison.OrdinalIgnoreCase);
        var rendererFallback = !string.IsNullOrWhiteSpace(PortalGpuRenderer) &&
            PortalGpuRenderer.Contains("fallback", StringComparison.OrdinalIgnoreCase);

        IsPortalFallbackLimited = statusFallback || rendererFallback;
    }

    private void UpdateAlbumListTransitions(EmulationViewModel vm)
    {
        var albumList = this.FindControl<Control>("AlbumListView");
        if (albumList == null)
            return;

        albumList.Transitions = _albumListTransitions;
    }

    private void StartAlbumListPortalMask(EmulationViewModel vm)
    {
        var maskVersion = ++_portalMaskVersion;
        IsAlbumListInteractive = false;

        var captureVisible = UseInlineCaptureHost
            ? _inlineCaptureHost?.IsVisible == true
            : _portalWindow?.IsVisible == true;

        if (vm.IsCompositionCaptureVisible && captureVisible)
        {
            PortalFallbackOpacity = 1;
            IsPortalSurfaceVisible = false;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(AlbumListPortalMaskDuration).ConfigureAwait(true);
            if (maskVersion != _portalMaskVersion)
                return;

            IsAlbumListInteractive = true;

            captureVisible = UseInlineCaptureHost
                ? _inlineCaptureHost?.IsVisible == true
                : _portalWindow?.IsVisible == true;

            if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true, IsActive: true } &&
                captureVisible)
            {
                SyncPortalWindow();
                Dispatcher.UIThread.Post(() =>
                {
                    if (maskVersion != _portalMaskVersion)
                        return;

                    SyncPortalWindow();
                    IsPortalSurfaceVisible = !UseInlineCaptureHost;
                    PortalFallbackOpacity = 0;
                }, DispatcherPriority.Render);
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateWindowZOrder()
    {
        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow || _isPortalWindowFullscreen) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var mainHwnd = mainWindow.TryGetPlatformHandle()?.Handle;
            var portalHwnd = _portalWindow.TryGetPlatformHandle()?.Handle;

            if (mainHwnd != null && portalHwnd != null)
            {
                // Set portal window behind main window
                SetWindowPos(portalHwnd.Value, mainHwnd.Value, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var mainHandle = mainWindow.TryGetPlatformHandle()?.Handle;
            var portalHandle = _portalWindow.TryGetPlatformHandle()?.Handle;
            if (mainHandle != null && portalHandle != null)
            {
                MacSystemDialogs.AttachPortalWindow(portalHandle.Value, mainHandle.Value);
                MacSystemDialogs.OrderWindowBelow(portalHandle.Value, mainHandle.Value);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
