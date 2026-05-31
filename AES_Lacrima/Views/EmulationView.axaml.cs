using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
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
using AES_Emulation.Windows.API;
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
    private static readonly TimeSpan AlbumListPortalMaskDuration = TimeSpan.FromMilliseconds(550);

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

    public static readonly DirectProperty<EmulationView, double> CarouselOpacityProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, double>(
            nameof(CarouselOpacity),
            o => o.CarouselOpacity,
            (o, v) => o.CarouselOpacity = v);

    public static readonly DirectProperty<EmulationView, bool> IsCaptureChromeVisibleProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsCaptureChromeVisible),
            o => o.IsCaptureChromeVisible,
            (o, v) => o.IsCaptureChromeVisible = v);

    public static readonly DirectProperty<EmulationView, double> CapturePresentationOpacityProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, double>(
            nameof(CapturePresentationOpacity),
            o => o.CapturePresentationOpacity,
            (o, v) => o.CapturePresentationOpacity = v);

    public static readonly DirectProperty<EmulationView, bool> IsPortalChromeVisibleProperty =
        AvaloniaProperty.RegisterDirect<EmulationView, bool>(
            nameof(IsPortalChromeVisible),
            o => o.IsPortalChromeVisible);

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
    private CancellationTokenSource? _portalHideTransitionCancellation;
    private CancellationTokenSource? _portalShowBlendCancellation;
    private CancellationTokenSource? _viewportTransitionCancellation;
    private bool _wasViewActive;
    private bool _captureHostPresentationVisible;
    private bool _isCaptureChromeVisible;
    private double _capturePresentationOpacity;
    private const int CarouselTransitionMs = 300;
    private const int CaptureTransitionMs = 300;
    private bool _isAlbumListInteractive = true;
    private double _carouselOpacity = 1;
    private readonly Queue<double> _portalFrameSamples = new();
    private const int PortalFrameSampleCount = 180;
    private readonly Transitions _albumListTransitions =
    [
        new DoubleTransition
        {
            Property = MaxHeightProperty,
            Duration = TimeSpan.FromMilliseconds(550),
            Easing = new CubicEaseInOut()
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(450),
            Easing = new CubicEaseInOut()
        }
    ];
    private bool _portalSyncPending;
    private DateTime _lastLayoutSyncUtc = DateTime.MinValue;
    private int _portalMaskVersion;
    private PixelPoint _lastPortalPosition = new(int.MinValue, int.MinValue);
    private Size _lastPortalSize = new(double.NaN, double.NaN);
    private bool _isCaptureFullscreen;
    private PixelPoint _portalWindowFullscreenPosition;
    private Size _portalWindowFullscreenSize;
    private PortalFullscreenOverlayWindow? _portalFullscreenOverlayWindow;
    private MainWindowCaptureFullscreenState? _savedMainWindowCaptureFullscreenState;
    private int _savedCaptureOverlayZIndex = 5;
    private int _savedCaptureOverlayRowSpan = 1;
    private DateTime _lastPortalGraphUpdateUtc = DateTime.MinValue;
    private bool _compositionCaptureWasVisible;
    private bool _inlinePortalPresentationActive;
    private FullscreenCursorAutoHideHelper? _fullscreenCursorAutoHide;

    /// <summary>
    /// When <see langword="true"/>, composition capture renders in <c>InlineCaptureControl</c> on this view.
    /// When <see langword="false"/>, capture uses <see cref="PortalWindow"/> instead.
    /// Windows uses inline capture with Avalonia 12 NativeControlHost/DComp for lowest overhead.
    /// </summary>
    private static bool UseInlineCaptureHost => OperatingSystem.IsWindows();

    /// <summary>
    /// Portal chrome (transparent hole + fallback overlay) is only used with the external portal window.
    /// </summary>
    public bool IsPortalChromeVisible => !UseInlineCaptureHost;

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
        AddHandler(InputElement.PointerMovedEvent, OnFullscreenCursorPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, OnFullscreenCursorPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        LayoutUpdated += OnViewLayoutUpdated;
        DataContextChanged += OnDataContextChanged;
        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;
        PortalStatusText = "DirectComposition idle";
        PortalGpuRenderer = "Unknown";
        PortalGpuVendor = "Unknown";
        IsAlbumListInteractive = true;
        CarouselOpacity = 1;
        CapturePresentationOpacity = 0;
        SetCaptureChromeVisible(false);
    }

    private void OnViewLayoutUpdated(object? sender, EventArgs e)
    {
        if (UseInlineCaptureHost || _portalWindow == null || _isCaptureFullscreen)
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
        if (TryHandleCaptureDoubleClick(e))
            return;

        if (_isCaptureFullscreen &&
            DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
        {
            ActiveCaptureHost?.ForwardFocusToTarget();
        }
    }

    private void OnPortalSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryHandleCaptureDoubleClick(e))
            return;

        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        ActiveCaptureHost?.ForwardFocusToTarget();
    }

    private bool TryHandleCaptureDoubleClick(PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2)
            return false;

        e.Handled = true;

        if (DataContext is not EmulationViewModel vm)
            return true;

        if (!vm.IsCompositionCaptureVisible || !vm.IsEmulatorRunning)
            return true;

        vm.ToggleFullscreenCommand.Execute(null);
        return true;
    }

    private void OnEmulationViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is EmulationViewModel { IsFullscreen: true } vm)
        {
            vm.IsFullscreen = false;
            e.Handled = true;
            return;
        }

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

    public double CarouselOpacity
    {
        get => _carouselOpacity;
        set => SetAndRaise(CarouselOpacityProperty, ref _carouselOpacity, value);
    }

    public double CapturePresentationOpacity
    {
        get => _capturePresentationOpacity;
        set
        {
            if (SetAndRaise(CapturePresentationOpacityProperty, ref _capturePresentationOpacity, value))
                UpdateCaptureChromeVisibilityFromOpacity();
        }
    }

    public bool IsCaptureChromeVisible
    {
        get => _isCaptureChromeVisible;
        private set => SetAndRaise(IsCaptureChromeVisibleProperty, ref _isCaptureChromeVisible, value);
    }

    private void SetCaptureChromeVisible(bool visible)
    {
        IsCaptureChromeVisible = visible;
    }

    private void UpdateCaptureChromeVisibilityFromOpacity()
    {
        if (DataContext is EmulationViewModel vm)
        {
            IsCaptureChromeVisible =
                _capturePresentationOpacity > 0.001 ||
                vm.IsCompositionCaptureVisible ||
                vm.IsEmulatorLaunchInProgress ||
                vm.IsRenderOptionsOpen ||
                IsPortalCaptureInitializing;
        }
        else
        {
            IsCaptureChromeVisible = _capturePresentationOpacity > 0.001;
        }
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

        if (_isCaptureFullscreen)
            ExitCaptureFullscreen();

        if (_inlineCaptureHost != null)
        {
            _inlineCaptureHost.IsVisible = false;
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
            CancelPresentationTransitions();
            _inlinePortalPresentationActive = false;
            _captureHostPresentationVisible = false;
            _compositionCaptureWasVisible = false;
            _wasViewActive = false;
            CarouselOpacity = 0;
            CapturePresentationOpacity = 0;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EmulationViewModel vm)
            return;

        if (e.PropertyName == nameof(EmulationViewModel.IsActive) ||
            e.PropertyName == nameof(EmulationViewModel.IsCompositionCaptureVisible) ||
            e.PropertyName == nameof(EmulationViewModel.IsEmulatorViewportVisible) ||
            e.PropertyName == nameof(EmulationViewModel.IsEmulatorRunning))
        {
            if (e.PropertyName == nameof(EmulationViewModel.IsEmulatorRunning) &&
                !vm.IsEmulatorRunning &&
                vm.IsFullscreen)
            {
                vm.IsFullscreen = false;
            }

            if (e.PropertyName == nameof(EmulationViewModel.IsCompositionCaptureVisible) &&
                !vm.IsCompositionCaptureVisible &&
                vm.IsFullscreen)
            {
                vm.IsFullscreen = false;
            }

            UpdatePortalVisibility(vm);
        }
        else if (e.PropertyName == nameof(EmulationViewModel.IsAlbumListCollapsed) ||
                 e.PropertyName == nameof(EmulationViewModel.IsRenderOptionsOpen))
        {
            UpdateAlbumListTransitions(vm);
            UpdateInlineCaptureHostVisibility(vm);
            UpdateCaptureChromeVisibilityFromOpacity();
            if (e.PropertyName == nameof(EmulationViewModel.IsAlbumListCollapsed))
            {
                StartAlbumListPortalMask(vm);
            }

            SyncPortalWindow();

            if (e.PropertyName == nameof(EmulationViewModel.IsRenderOptionsOpen) && !vm.IsRenderOptionsOpen)
            {
                IsAlbumListInteractive = true;
                if (_isCaptureFullscreen)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
                            ActiveCaptureHost?.ForwardFocusToTarget();
                    }, DispatcherPriority.Input);
                }
            }
        }
        else if (e.PropertyName == nameof(EmulationViewModel.IsEmulatorLaunchInProgress))
        {
            UpdateCaptureChromeVisibilityFromOpacity();
        }
        else if (e.PropertyName == nameof(EmulationViewModel.LoadedAlbum) ||
                 e.PropertyName == nameof(EmulationViewModel.SelectedAlbum))
        {
            EnsureCarouselVisibleWhenIdle(vm, vm.IsCompositionCaptureVisible);
            UpdateCaptureChromeVisibilityFromOpacity();
        }
        else if (e.PropertyName == nameof(EmulationViewModel.IsFullscreen))
        {
            if (TopLevel.GetTopLevel(this) is not Window mainWindow)
                return;

            if (vm.IsFullscreen && !_isCaptureFullscreen)
                EnterCaptureFullscreen(mainWindow);
            else if (!vm.IsFullscreen && _isCaptureFullscreen)
                ExitCaptureFullscreen();
        }
    }

    private void UpdatePortalVisibility(EmulationViewModel vm)
    {
        UpdateAlbumListTransitions(vm);

        var captureVisible = vm.IsCompositionCaptureVisible;
        var viewActive = vm.IsActive;
        var viewActivated = viewActive && !_wasViewActive;
        var viewDeactivated = !viewActive && _wasViewActive;

        if (viewDeactivated)
        {
            CancelPresentationTransitions();
            var cancellation = new CancellationTokenSource();
            _viewportTransitionCancellation = cancellation;
            _ = FadeOutPresentationForNavigationAsync(cancellation.Token);
            _compositionCaptureWasVisible = false;
            _wasViewActive = false;
            return;
        }

        if (viewActivated)
        {
            CancelPresentationTransitions();
            var cancellation = new CancellationTokenSource();
            _viewportTransitionCancellation = cancellation;
            if (captureVisible)
                _ = EnterWithCaptureAsync(cancellation.Token);
            else
                _ = EnterWithCarouselAsync(cancellation.Token);

            _compositionCaptureWasVisible = captureVisible;
            _wasViewActive = true;
            UpdateCaptureChromeVisibilityFromOpacity();
            return;
        }

        if (!viewActive)
        {
            _wasViewActive = false;
            return;
        }

        if (captureVisible != _compositionCaptureWasVisible)
        {
            CancelPresentationTransitions();
            var cancellation = new CancellationTokenSource();
            _viewportTransitionCancellation = cancellation;
            if (captureVisible)
                _ = TransitionToCaptureAsync(cancellation.Token);
            else
                _ = TransitionToCarouselAsync(cancellation.Token, restoreCarousel: true);

            _compositionCaptureWasVisible = captureVisible;
        }

        EnsureCarouselVisibleWhenIdle(vm, captureVisible);

        _wasViewActive = true;
        UpdateInlineCaptureHostVisibility(vm);
        UpdateCaptureChromeVisibilityFromOpacity();
    }

    private void EnsureCarouselVisibleWhenIdle(EmulationViewModel vm, bool captureVisible)
    {
        if (!vm.IsActive || captureVisible || vm.IsEmulatorLaunchInProgress)
            return;

        if (CarouselOpacity < 0.05)
            CarouselOpacity = 1;

        if (CapturePresentationOpacity > 0.001)
            CapturePresentationOpacity = 0;

        _inlinePortalPresentationActive = false;
        _captureHostPresentationVisible = false;
    }

    private void CancelPresentationTransitions()
    {
        _portalHideTransitionCancellation?.Cancel();
        _portalShowBlendCancellation?.Cancel();
        _viewportTransitionCancellation?.Cancel();
    }

    private async Task FadeOutPresentationForNavigationAsync(CancellationToken cancellationToken)
    {
        _inlinePortalPresentationActive = false;
        _captureHostPresentationVisible = true;
        SetCaptureChromeVisible(true);

        if (DataContext is EmulationViewModel vm)
            UpdateInlineCaptureHostVisibility(vm);

        CapturePresentationOpacity = 0;
        CarouselOpacity = 0;

        try
        {
            await Task.Delay(Math.Max(CarouselTransitionMs, CaptureTransitionMs), cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        _captureHostPresentationVisible = false;
        if (DataContext is EmulationViewModel vmAfter)
        {
            UpdateInlineCaptureHostVisibility(vmAfter);
            SetCaptureChromeVisible(vmAfter.IsEmulatorLaunchInProgress || IsPortalCaptureInitializing);
        }

        if (!UseInlineCaptureHost)
            await CompletePortalHideAfterFadeAsync(cancellationToken);
    }

    private async Task EnterWithCarouselAsync(CancellationToken cancellationToken)
    {
        _inlinePortalPresentationActive = false;
        _captureHostPresentationVisible = false;
        CapturePresentationOpacity = 0;

        if (DataContext is EmulationViewModel vm)
            UpdateInlineCaptureHostVisibility(vm);

        CarouselOpacity = 0;
        try
        {
            await Task.Delay(16, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        CarouselOpacity = 1;
    }

    private async Task EnterWithCaptureAsync(CancellationToken cancellationToken)
    {
        CarouselOpacity = 0;
        CapturePresentationOpacity = 0;
        await TransitionToCaptureAsync(cancellationToken);
    }

    private async Task TransitionToCaptureAsync(CancellationToken cancellationToken)
    {
        if (UseInlineCaptureHost)
        {
            if (_inlinePortalPresentationActive && CapturePresentationOpacity > 0.95)
                return;

            EnsureInlineCaptureHost();
            if (_inlineCaptureHost == null)
                return;

            _inlinePortalPresentationActive = true;
            _captureHostPresentationVisible = true;
            SetCaptureChromeVisible(true);
            if (DataContext is EmulationViewModel vm)
                UpdateInlineCaptureHostVisibility(vm);

            CarouselOpacity = 0;
            try
            {
                await Task.Delay(CarouselTransitionMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            if (DataContext is not EmulationViewModel { IsActive: true, IsCompositionCaptureVisible: true })
                return;

            CapturePresentationOpacity = 1;
            SetCaptureChromeVisible(true);
            ResetPortalCaptureBrightness();
            StartPortalBrightnessFade();
            return;
        }

        await ShowPortalWindowAsync(cancellationToken);
    }

    private async Task TransitionToCarouselAsync(CancellationToken cancellationToken, bool restoreCarousel)
    {
        if (UseInlineCaptureHost)
        {
            _inlinePortalPresentationActive = false;
            _captureHostPresentationVisible = true;
            SetCaptureChromeVisible(true);
            if (DataContext is EmulationViewModel vm)
                UpdateInlineCaptureHostVisibility(vm);

            CapturePresentationOpacity = 0;
            try
            {
                await Task.Delay(CaptureTransitionMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            _captureHostPresentationVisible = false;
            if (DataContext is EmulationViewModel vmAfterFade)
            {
                UpdateInlineCaptureHostVisibility(vmAfterFade);
                SetCaptureChromeVisible(vmAfterFade.IsEmulatorLaunchInProgress || IsPortalCaptureInitializing);
            }

            if (restoreCarousel && DataContext is EmulationViewModel { IsActive: true })
                CarouselOpacity = 1;

            return;
        }

        await HidePortalWindowAsync(cancellationToken, restoreCarousel);
    }

    private async Task ShowPortalWindowAsync(CancellationToken cancellationToken)
    {
        _portalHideTransitionCancellation?.Cancel();
        _portalShowBlendCancellation?.Cancel();

        CarouselOpacity = 0;

        if (_portalWindow != null)
        {
            var wasSurfaceVisible = IsPortalSurfaceVisible;
            if (!wasSurfaceVisible)
            {
                PortalFallbackOpacity = 1;
                IsPortalSurfaceVisible = false;

                var showCancellation = new CancellationTokenSource();
                _portalShowBlendCancellation = showCancellation;
                _ = FadeInPortalAfterCarouselFadeOutAsync(showCancellation.Token);
            }

            _portalWindow.Show();
            SyncPortalWindowCore();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                LinuxWindowPlacement.TryConfigureClickThrough(_portalWindow);
            UpdateWindowZOrder();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && TopLevel.GetTopLevel(this) is Window mainWindow)
                mainWindow.Activate();

            if (wasSurfaceVisible)
                PortalFallbackOpacity = 0;
        }

        try
        {
            await Task.Delay(CarouselTransitionMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        CapturePresentationOpacity = 1;
    }

    private async Task HidePortalWindowAsync(CancellationToken cancellationToken, bool restoreCarousel)
    {
        IsPortalSurfaceVisible = false;
        PortalFallbackOpacity = 0;

        if (_isCaptureFullscreen && DataContext is EmulationViewModel vmFullscreen)
            vmFullscreen.IsFullscreen = false;

        CapturePresentationOpacity = 0;

        var cancellation = new CancellationTokenSource();
        _portalHideTransitionCancellation = cancellation;
        _ = CompletePortalHideAsync(cancellation.Token);

        try
        {
            await Task.Delay(CaptureTransitionMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        if (restoreCarousel && DataContext is EmulationViewModel { IsActive: true })
            CarouselOpacity = 1;
    }

    private async Task CompletePortalHideAfterFadeAsync(CancellationToken cancellationToken)
    {
        if (_isCaptureFullscreen && DataContext is EmulationViewModel vmFullscreen)
            vmFullscreen.IsFullscreen = false;

        var cancellation = new CancellationTokenSource();
        _portalHideTransitionCancellation = cancellation;
        await CompletePortalHideAsync(cancellation.Token);
    }

    private void OnMainWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (UseInlineCaptureHost)
            return;

        SyncPortalWindow();
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is EmulationViewModel vm && vm.IsActive && vm.IsCompositionCaptureVisible)
        {
            if (UseInlineCaptureHost && CapturePresentationOpacity < 0.05)
            {
                CancelPresentationTransitions();
                var cancellation = new CancellationTokenSource();
                _viewportTransitionCancellation = cancellation;
                _ = TransitionToCaptureAsync(cancellation.Token);
            }
            else if (!UseInlineCaptureHost)
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
            if (DataContext is EmulationViewModel activeVm)
                UpdatePortalVisibility(activeVm);
        }
    }

    private async Task FadeInPortalAfterCarouselFadeOutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(170, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        if (DataContext is not EmulationViewModel { IsActive: true })
            return;

        if (UseInlineCaptureHost)
        {
            if (_inlineCaptureHost == null)
                return;

            ResetPortalCaptureBrightness();
            StartPortalBrightnessFade();
            return;
        }

        if (_portalWindow?.IsVisible != true)
            return;

        SyncPortalWindowCore();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && TopLevel.GetTopLevel(this) is Window mainWindow)
        {
            mainWindow.Activate();
        }

        ResetPortalCaptureBrightness();
        IsPortalSurfaceVisible = true;
        PortalFallbackOpacity = 0;
        StartPortalBrightnessFade();
    }

    private async Task CompletePortalHideAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(320, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        if (DataContext is EmulationViewModel vm)
            vm.PortalCaptureBrightness = 0;

        _portalWindow?.Hide();
        HidePortalFullscreenOverlay();
        StopFullscreenCursorAutoHide();
        _isCaptureFullscreen = false;
        PortalFallbackOpacity = 1;
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

    private void EnterCaptureFullscreen(Window mainWindow)
    {
        if (UseInlineCaptureHost)
            EnterInlineCaptureFullscreen(mainWindow);
        else
            EnterPortalCaptureFullscreen(mainWindow);
    }

    private void ExitCaptureFullscreen()
    {
        if (UseInlineCaptureHost)
            ExitInlineCaptureFullscreen();
        else
            ExitPortalCaptureFullscreen();
    }

    private void EnterInlineCaptureFullscreen(Window mainWindow)
    {
        EnsureInlineCaptureHost();
        if (_inlineCaptureHost == null)
            return;

        if (DataContext is EmulationViewModel vm)
        {
            if (vm.IsRenderOptionsOpen)
                vm.IsRenderOptionsOpen = false;
        }

        var bounds = GetScreenBounds(mainWindow);
        if (mainWindow is MainWindow chromeWindow)
            _savedMainWindowCaptureFullscreenState = chromeWindow.EnterCaptureFullscreenMode(bounds);

        ApplyInlineCaptureOverlayFullscreenLayout(fullscreen: true);

        ShowFullscreenStatsOverlay(mainWindow, bounds);
        _portalFullscreenOverlayWindow!.Topmost = true;
        _portalFullscreenOverlayWindow.Show();
        _portalFullscreenOverlayWindow.Activate();

        CarouselOpacity = 0;
        CapturePresentationOpacity = 1;
        SetCaptureChromeVisible(true);
        _isCaptureFullscreen = true;

        StartFullscreenCursorAutoHide();

        Dispatcher.UIThread.Post(() =>
        {
            _inlineCaptureHost?.InvalidateArrange();
            _inlineCaptureHost?.InvalidateVisual();
            ActiveCaptureHost?.ForwardFocusToTarget();
        }, DispatcherPriority.Background);
    }

    private void ExitInlineCaptureFullscreen()
    {
        StopFullscreenCursorAutoHide();
        HideFullscreenStatsOverlay();

        if (TopLevel.GetTopLevel(this) is MainWindow chromeWindow &&
            _savedMainWindowCaptureFullscreenState != null)
        {
            chromeWindow.ExitCaptureFullscreenMode(_savedMainWindowCaptureFullscreenState);
            _savedMainWindowCaptureFullscreenState = null;
        }

        ApplyInlineCaptureOverlayFullscreenLayout(fullscreen: false);
        _isCaptureFullscreen = false;

        if (DataContext is EmulationViewModel vm)
        {
            UpdateInlineCaptureHostVisibility(vm);
            UpdateCaptureChromeVisibilityFromOpacity();
        }
    }

    private void StartFullscreenCursorAutoHide()
    {
        if (_fullscreenCursorAutoHide != null)
            return;

        _fullscreenCursorAutoHide = new FullscreenCursorAutoHideHelper(this);
        _fullscreenCursorAutoHide.Start();
    }

    private void StopFullscreenCursorAutoHide()
    {
        _fullscreenCursorAutoHide?.Stop();
        _fullscreenCursorAutoHide = null;

        if (TopLevel.GetTopLevel(this) is Window mainWindow)
            mainWindow.Cursor = Cursor.Default;

        Cursor = Cursor.Default;
    }

    private void OnFullscreenCursorPointerActivity(object? sender, PointerEventArgs e)
    {
        if (!_isCaptureFullscreen)
            return;

        _fullscreenCursorAutoHide?.NotifyPointerActivity();
    }

    private void ApplyInlineCaptureOverlayFullscreenLayout(bool fullscreen)
    {
        var overlay = this.FindControl<Grid>("EmulatorCaptureOverlay");
        if (overlay == null)
            return;

        if (fullscreen)
        {
            _savedCaptureOverlayZIndex = overlay.ZIndex;
            _savedCaptureOverlayRowSpan = Grid.GetRowSpan(overlay);
            Grid.SetRowSpan(overlay, 3);
            overlay.ZIndex = 5000;
            return;
        }

        Grid.SetRowSpan(overlay, _savedCaptureOverlayRowSpan);
        overlay.ZIndex = _savedCaptureOverlayZIndex;
    }

    private void EnterPortalCaptureFullscreen(Window mainWindow)
    {
        if (_portalWindow == null)
            return;

        _portalWindowFullscreenPosition = _portalWindow.Position;
        _portalWindowFullscreenSize = new Size(_portalWindow.Width, _portalWindow.Height);
        var bounds = GetScreenBounds(mainWindow);

        _portalWindow.Topmost = false;
        ApplyPortalWindowBounds(bounds.Position, bounds.Width, bounds.Height, mainWindow);
        ShowFullscreenStatsOverlay(mainWindow, bounds);

        _portalFullscreenOverlayWindow!.Topmost = true;
        _portalFullscreenOverlayWindow.Show();
        _portalFullscreenOverlayWindow.Activate();
        _portalFullscreenOverlayWindow.Focus();

        _isCaptureFullscreen = true;

        StartFullscreenCursorAutoHide();

        Dispatcher.UIThread.Post(() =>
        {
            _portalWindow?.CaptureHostControl?.InvalidateArrange();
            _portalWindow?.CaptureHostControl?.InvalidateVisual();
        }, DispatcherPriority.Background);
    }

    private void ExitPortalCaptureFullscreen()
    {
        if (_portalWindow == null)
            return;

        StopFullscreenCursorAutoHide();
        HideFullscreenStatsOverlay();

        _portalWindow.Position = _portalWindowFullscreenPosition;
        _portalWindow.Width = _portalWindowFullscreenSize.Width;
        _portalWindow.Height = _portalWindowFullscreenSize.Height;
        _portalWindow.Topmost = false;

        _isCaptureFullscreen = false;
        SyncPortalWindow();
    }

    private static PixelRect GetScreenBounds(Window mainWindow)
    {
        var screen = mainWindow.Screens?.ScreenFromWindow(mainWindow) ?? mainWindow.Screens?.Primary;
        return screen?.Bounds ?? new PixelRect(0, 0, 0, 0);
    }

    private void ShowFullscreenStatsOverlay(Window mainWindow, PixelRect screenBounds)
    {
        if (_portalFullscreenOverlayWindow == null)
        {
            _portalFullscreenOverlayWindow = new PortalFullscreenOverlayWindow();
            _portalFullscreenOverlayWindow.DoubleClicked += OnCaptureFullscreenExitRequested;
        }

        ApplyFullscreenOverlayBounds(screenBounds, mainWindow);
        if (_portalFullscreenOverlayWindow.FindControl<PortalCaptureStatsOverlay>("StatsOverlay") is { } statsOverlay &&
            DataContext is EmulationViewModel vm)
        {
            statsOverlay.CaptureHost = this;
            statsOverlay.Settings = vm;
        }
    }

    private void HideFullscreenStatsOverlay()
    {
        _portalFullscreenOverlayWindow?.Hide();
        if (_portalFullscreenOverlayWindow?.FindControl<PortalCaptureStatsOverlay>("StatsOverlay") is { } statsOverlay)
        {
            statsOverlay.CaptureHost = null;
            statsOverlay.Settings = null;
        }
    }

    private void OnCaptureFullscreenExitRequested(object? sender, EventArgs e)
    {
        if (DataContext is EmulationViewModel vm)
            vm.IsFullscreen = false;
    }

    private void HidePortalFullscreenOverlay()
    {
        HideFullscreenStatsOverlay();
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

        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow || _isCaptureFullscreen) return;

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

        var portalPosition = new PixelPoint(
            screenTopLeft.X - (int)PortalLeftOverlapPixels - portalExpansionPixels,
            screenTopLeft.Y - (int)PortalTopOverlapPixels - portalExpansionPixels);
        var portalRenderScaling = GetPortalRenderScaling(mainWindow);
        var portalSize = new Size(
            Math.Ceiling(widthPixels / portalRenderScaling),
            Math.Ceiling(heightPixels / portalRenderScaling));
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

        ApplyPortalWindowBounds(portalPosition, widthPixels, heightPixels, mainWindow);
        UpdateWindowZOrder();
    }

    private double GetPortalRenderScaling(Window? mainWindow)
    {
        if (_portalWindow == null)
            return Math.Max(0.0001, mainWindow?.RenderScaling ?? 1.0);

        return Math.Max(0.0001, _portalWindow.RenderScaling > 0
            ? _portalWindow.RenderScaling
            : mainWindow?.RenderScaling ?? 1.0);
    }

    private void ApplyPortalWindowBounds(PixelPoint position, int widthPixels, int heightPixels, Window? mainWindow)
    {
        if (_portalWindow == null || widthPixels <= 0 || heightPixels <= 0)
            return;

        var renderScaling = GetPortalRenderScaling(mainWindow);
        var width = Math.Ceiling(widthPixels / renderScaling);
        var height = Math.Ceiling(heightPixels / renderScaling);

        _lastPortalPosition = position;
        _lastPortalSize = new Size(width, height);
        _portalWindow.Position = position;
        _portalWindow.Width = width;
        _portalWindow.Height = height;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _portalWindow.MoveResizeUnconstrained(position, widthPixels, heightPixels);
            LinuxWindowPlacement.TryConfigureClickThrough(_portalWindow);
        }
    }

    private void ApplyFullscreenOverlayBounds(PixelRect screenBounds, Window mainWindow)
    {
        if (_portalFullscreenOverlayWindow == null || screenBounds.Width <= 0 || screenBounds.Height <= 0)
            return;

        var renderScaling = Math.Max(0.0001, _portalFullscreenOverlayWindow.RenderScaling > 0
            ? _portalFullscreenOverlayWindow.RenderScaling
            : mainWindow.RenderScaling);
        _portalFullscreenOverlayWindow.Position = screenBounds.Position;
        _portalFullscreenOverlayWindow.Width = Math.Ceiling(screenBounds.Width / renderScaling);
        _portalFullscreenOverlayWindow.Height = Math.Ceiling(screenBounds.Height / renderScaling);
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
            .Subscribe(new SimpleObserver<bool>(value =>
            {
                IsPortalDirectCompositionActive = value;
                if (value && DataContext is EmulationViewModel vm)
                    vm.ApplyEmulatorVolumeAfterCaptureActive();
            }));

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

        _inlineCaptureHost = this.FindControl<EmulatorCaptureHostControl>("InlineCaptureControl");
        if (_inlineCaptureHost == null)
            return;

        if (DataContext is EmulationViewModel vm)
            UpdateInlineCaptureHostVisibility(vm);
    }

    private void UpdateInlineCaptureHostVisibility(EmulationViewModel vm)
    {
        if (!UseInlineCaptureHost || _inlineCaptureHost == null)
            return;

        _inlineCaptureHost.IsVisible =
            vm.IsActive &&
            (vm.IsCompositionCaptureVisible || _captureHostPresentationVisible);
    }

    private void UpdatePortalFrametimeGraph(double latestMs)
    {
        var now = DateTime.UtcNow;
        var minIntervalMs = _isCaptureFullscreen ? 200 : 50;
        if ((now - _lastPortalGraphUpdateUtc).TotalMilliseconds < minIntervalMs)
            return;

        _lastPortalGraphUpdateUtc = now;

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
            if (!UseInlineCaptureHost)
            {
                PortalFallbackOpacity = 1;
                IsPortalSurfaceVisible = false;
            }
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
        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow || _isCaptureFullscreen) return;

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
