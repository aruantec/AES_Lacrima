using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
using AES_Lacrima.ViewModels;
using AES_Controls.Helpers;
using AES_Emulation.Controls;
using AES_Emulation.Linux.API;
using AES_Lacrima.Mac.API;
using EmulatorCaptureHostControl = AES_Emulation.Controls.EmulatorCaptureHost;

namespace AES_Lacrima.Views;

public partial class EmulationView : UserControl
{
    private const double PortalLeftOverlapPixels = 0;
    private const double PortalTopOverlapPixels = 2;
    private const double PortalRightOverlapPixels = 0;
    private const double PortalBottomOverlapPixels = 2;
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
    private double _portalCaptureFps;
    private double _portalCaptureFrameTimeMs;
    private string _portalGpuRenderer = "Unknown";
    private string _portalGpuVendor = "Unknown";
    private Geometry? _portalFrametimeGraphGeometry;
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
    private CancellationTokenSource? _portalRevealCts;
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
        DataContextChanged += OnDataContextChanged;
        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;
        PortalStatusText = "DirectComposition idle";
        PortalGpuRenderer = "Unknown";
        PortalGpuVendor = "Unknown";
        IsAlbumListInteractive = true;
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
        set => SetAndRaise(PortalStatusTextProperty, ref _portalStatusText, value);
    }

    public bool IsPortalDirectCompositionActive
    {
        get => _isPortalDirectCompositionActive;
        set => SetAndRaise(IsPortalDirectCompositionActiveProperty, ref _isPortalDirectCompositionActive, value);
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
        set => SetAndRaise(PortalGpuRendererProperty, ref _portalGpuRenderer, value);
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

        if (DataContext is EmulationViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundsSubscription?.Dispose();
        _mainWindowBoundsSubscription?.Dispose();
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
            e.PropertyName == nameof(EmulationViewModel.IsRenderOptionsOpen) ||
            e.PropertyName == nameof(EmulationViewModel.IsEmulatorLaunchInProgress))
        {
            UpdateAlbumListTransitions(vm);
            UpdatePortalVisibility(vm);
            UpdateInlineCaptureHostVisibility(vm);
            if (e.PropertyName == nameof(EmulationViewModel.IsAlbumListCollapsed))
            {
                StartAlbumListPortalMask(vm);
                SyncPortalWindow();
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
            UpdateWindowZOrder();

            UpdatePortalSurfaceVisibility();

            if (!wasSurfaceVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is EmulationViewModel { IsActive: true } && _portalWindow?.IsVisible == true)
                    {
                        SyncPortalWindowCore();
                        UpdatePortalSurfaceVisibility();
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    private void UpdatePortalSurfaceVisibility()
    {
        _portalRevealCts?.Cancel();
        _portalRevealCts?.Dispose();
        _portalRevealCts = null;

        if (UseInlineCaptureHost)
        {
            PortalFallbackOpacity = 0;
            IsPortalSurfaceVisible = false;
            return;
        }

        if (DataContext is not EmulationViewModel vm)
        {
            HidePortal();
            return;
        }

        bool shouldShowPortal = vm.IsCompositionCaptureVisible && vm.IsActive;
        bool isReady = !vm.IsEmulatorLaunchInProgress && !IsPortalCaptureInitializing && IsPortalDirectCompositionActive && PortalCaptureFps > 0;
        bool isWindowVisible = _portalWindow?.IsVisible == true;

        if (shouldShowPortal && isReady && isWindowVisible)
        {
            var delayMs = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? 300 : 0;

            if (delayMs > 0)
            {
                var cts = new CancellationTokenSource();
                _portalRevealCts = cts;

                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(delayMs, cts.Token);
                        
                        // Check again if we're still ready after the delay
                        if (DataContext is EmulationViewModel currentVm && 
                            currentVm.IsCompositionCaptureVisible && 
                            !currentVm.IsEmulatorLaunchInProgress && 
                            !IsPortalCaptureInitializing && 
                            IsPortalDirectCompositionActive && 
                            PortalCaptureFps > 0 &&
                            _portalWindow?.IsVisible == true)
                        {
                            SyncPortalWindow();
                            IsPortalSurfaceVisible = true;
                            PortalFallbackOpacity = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancelled, do nothing
                    }
                }, DispatcherPriority.Render);
            }
            else
            {
                IsPortalSurfaceVisible = true;
                PortalFallbackOpacity = 0;
            }
        }
        else
        {
            IsPortalSurfaceVisible = false;
            PortalFallbackOpacity = 1;
        }
    }

    private void HidePortal()
    {
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

        if (_isPortalWindowFullscreen && DataContext is EmulationViewModel vm)
        {
            vm.IsFullscreen = false;
        }

        _portalWindow?.Hide();
        HidePortalFullscreenOverlay();
        _isPortalWindowFullscreen = false;
        
        _portalFrameSamples.Clear();
        PortalFrametimeGraphGeometry = null;
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

        var widthPixels = Math.Max(0, screenBottomRight.X - screenTopLeft.X) + (int)PortalLeftOverlapPixels + (int)PortalRightOverlapPixels;
        var heightPixels = Math.Max(0, screenBottomRight.Y - screenTopLeft.Y) + (int)PortalTopOverlapPixels + (int)PortalBottomOverlapPixels;
        var portalRenderScaling = Math.Max(0.0001, _portalWindow.RenderScaling);
        var width = widthPixels / portalRenderScaling;
        var height = heightPixels / portalRenderScaling;

        var portalPosition = new PixelPoint(
            screenTopLeft.X - (int)PortalLeftOverlapPixels,
            screenTopLeft.Y - (int)PortalTopOverlapPixels);
        var portalSize = new Size(width, height);
        if (_lastPortalPosition == portalPosition && _lastPortalSize == portalSize)
        {
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
            _portalWindow.MoveResizeUnconstrained(portalPosition, (int)widthPixels, (int)heightPixels);
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

        _portalFrameSamples.Clear();
        PortalFrametimeGraphGeometry = null;

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
        UpdatePortalSurfaceVisibility();
        PortalStatusText = captureControl.StatusText;
        PortalCaptureFps = captureControl.Fps;
        PortalCaptureFrameTimeMs = captureControl.FrameTimeMs;
        PortalGpuRenderer = captureControl.GpuRenderer;
        PortalGpuVendor = captureControl.GpuVendor;

        _captureInitializingSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.IsCaptureInitializingProperty)
            .Subscribe(new SimpleObserver<bool>(value => 
            {
                IsPortalCaptureInitializing = value;
                UpdatePortalSurfaceVisibility();
            }));

        _captureStatusSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.StatusTextProperty)
            .Subscribe(new SimpleObserver<string>(value => PortalStatusText = value));

        _captureActiveSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.IsDirectCompositionActiveProperty)
            .Subscribe(new SimpleObserver<bool>(value => 
            {
                IsPortalDirectCompositionActive = value;
                UpdatePortalSurfaceVisibility();
            }));

        _captureFpsSubscription = captureControl
            .GetObservable(EmulatorCaptureHostControl.FpsProperty)
            .Subscribe(new SimpleObserver<double>(value => 
            {
                var wasZero = PortalCaptureFps <= 0;
                PortalCaptureFps = value;
                if (wasZero && value > 0)
                    UpdatePortalSurfaceVisibility();
            }));

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
        captureHost.Bind(EmulatorCaptureHostControl.TargetWindowTitleHintProperty, new Binding("CurrentEmulatorWindowTitleHint"));
        captureHost.Bind(EmulatorCaptureHostControl.ForceUseTargetClientAreaProperty, new Binding("ForceUseTargetClientAreaCapture"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropLeftInsetProperty, new Binding("ClientAreaCropLeftInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropTopInsetProperty, new Binding("ClientAreaCropTopInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropRightInsetProperty, new Binding("ClientAreaCropRightInset"));
        captureHost.Bind(EmulatorCaptureHostControl.ClientAreaCropBottomInsetProperty, new Binding("ClientAreaCropBottomInset"));
        captureHost.Bind(EmulatorCaptureHostControl.HideTargetWindowAfterCaptureStartsProperty, new Binding("HideTargetWindowAfterCaptureStarts"));
        captureHost.Bind(EmulatorCaptureHostControl.StretchProperty, new Binding("SelectedStretch") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.BrightnessProperty, new Binding("RenderBrightness") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.SaturationProperty, new Binding("RenderSaturation") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.DisableVSyncProperty, new Binding("DisableVSync") { Mode = BindingMode.TwoWay });
        captureHost.Bind(EmulatorCaptureHostControl.ShaderPathProperty, new Binding("SelectedShaderPath"));
        captureHost.Bind(EmulatorCaptureHostControl.ClearShaderWhenPathEmptyProperty, new Binding("ClearShaderWhenPathEmpty"));
        captureHost.Bind(EmulatorCaptureHostControl.RequestStopSessionProperty, new Binding("RequestStopEmulatorCapture") { Mode = BindingMode.TwoWay });

        captureHostBorder.Child = captureHost;
        _inlineCaptureHost = captureHost;

        if (DataContext is EmulationViewModel vm)
            UpdateInlineCaptureHostVisibility(vm);
    }

    private void UpdateInlineCaptureHostVisibility(EmulationViewModel vm)
    {
        if (!UseInlineCaptureHost || _inlineCaptureHost == null)
            return;

        _inlineCaptureHost.IsVisible = vm.IsCompositionCaptureVisible && !vm.IsRenderOptionsOpen;
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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var mainHwnd = mainWindow.TryGetPlatformHandle()?.Handle;
            var portalHwnd = _portalWindow.TryGetPlatformHandle()?.Handle;
            if (mainHwnd != null && portalHwnd != null)
            {
                var display = X11Interop.XOpenDisplay(null);
                if (display != IntPtr.Zero)
                {
                    try
                    {
                        var changes = new X11Interop.XWindowChanges
                        {
                            sibling = mainHwnd.Value,
                            stack_mode = X11Interop.Below
                        };
                        X11Interop.XConfigureWindow(display, portalHwnd.Value, X11Interop.CWSibling | X11Interop.CWStackMode, ref changes);
                        X11Interop.XSync(display, false);
                    }
                    finally
                    {
                        X11Interop.XCloseDisplay(display);
                    }
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
