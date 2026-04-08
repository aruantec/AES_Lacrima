using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AES_Lacrima.ViewModels;
using AES_Controls.Helpers;
using AES_Emulation.Windows;

namespace AES_Lacrima.Views;

public partial class EmulationView : UserControl
{
    private const double PortalTopOverlapPixels = 2;
    private const double PortalBottomOverlapPixels = 2;

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

    private PortalWindow? _portalWindow;
    private IDisposable? _boundsSubscription;
    private IDisposable? _mainWindowBoundsSubscription;
    private IDisposable? _captureInitializingSubscription;
    private IDisposable? _captureTintSubscription;
    private bool _isPortalCaptureInitializing;
    private Color _portalCaptureTint = Colors.White;
    private double _portalFallbackOpacity;
    private bool _isPortalSurfaceVisible;

    public EmulationView()
    {
        InitializeComponent();
        var captureHost = this.FindControl<Border>("EmulatorCaptureHost");
        if (captureHost != null)
        {
            captureHost.PointerPressed += OnCaptureHostPointerPressed;
        }
        DataContextChanged += OnDataContextChanged;
        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;
    }

    private void OnCaptureHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        _portalWindow?.CaptureHostControl?.ForwardFocusToTarget();
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
                var captureControl = _portalWindow?.CaptureHostControl;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_portalWindow == null && TopLevel.GetTopLevel(this) is Window mainWindow)
        {
            _portalWindow = new PortalWindow();
            _portalWindow.DataContext = DataContext;
            
            // Ensure the window is created but not necessarily shown yet
            _portalWindow.Show();
            _portalWindow.Hide(); 
            AttachPortalCaptureBindings();

            // Sync with TransparentComposition
            var portal = this.FindControl<Control>("PortalPortal");
            if (portal != null)
            {
                _boundsSubscription = portal.GetObservable(Visual.BoundsProperty).Subscribe(new SimpleObserver<Rect>(_ => SyncPortalWindow()));
            }

            _mainWindowBoundsSubscription = mainWindow.GetObservable(Visual.BoundsProperty)
                .Subscribe(new SimpleObserver<Rect>(_ => SyncPortalWindow()));

            mainWindow.PositionChanged += OnMainWindowPositionChanged;
            mainWindow.Activated += OnMainWindowActivated;
            mainWindow.Deactivated += OnMainWindowDeactivated;
            LayoutUpdated += OnLayoutUpdated;
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
        
        if (DataContext is EmulationViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundsSubscription?.Dispose();
        _mainWindowBoundsSubscription?.Dispose();
        _captureInitializingSubscription?.Dispose();
        _captureTintSubscription?.Dispose();
        
        if (TopLevel.GetTopLevel(this) is Window mainWindow)
        {
            mainWindow.PositionChanged -= OnMainWindowPositionChanged;
            mainWindow.Activated -= OnMainWindowActivated;
            mainWindow.Deactivated -= OnMainWindowDeactivated;
            LayoutUpdated -= OnLayoutUpdated;
        }

        if (_portalWindow != null)
        {
            _portalWindow.Close();
            _portalWindow = null;
        }

        IsPortalCaptureInitializing = false;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_portalWindow != null)
        {
            _portalWindow.DataContext = DataContext;
            AttachPortalCaptureBindings();
        }

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
        if (e.PropertyName == nameof(EmulationViewModel.IsActive) ||
            e.PropertyName == nameof(EmulationViewModel.IsCompositionCaptureVisible) ||
            e.PropertyName == nameof(EmulationViewModel.IsEmulatorViewportVisible))
        {
            if (sender is EmulationViewModel vm)
            {
                UpdatePortalVisibility(vm);
            }
        }
    }

    private void UpdatePortalVisibility(EmulationViewModel vm)
    {
        if (vm.IsCompositionCaptureVisible)
        {
            ShowPortal();
        }
        else
        {
            HidePortal();
        }
    }

    private void OnMainWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        SyncPortalWindow();
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is EmulationViewModel { IsCompositionCaptureVisible: true })
        {
            ShowPortal();
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

    private void OnMainWindowDeactivated(object? sender, EventArgs e)
    {
        // Reorder z-order when main window loses focus to ensure portal stays visible
        UpdateWindowZOrder();
    }

    private void ShowPortal()
    {
        if (_portalWindow != null)
        {
            var wasSurfaceVisible = IsPortalSurfaceVisible;
            if (!wasSurfaceVisible)
            {
                PortalFallbackOpacity = 1;
                IsPortalSurfaceVisible = false;
            }

            _portalWindow.Show();
            SyncPortalWindow();
            UpdateWindowZOrder();

            if (!wasSurfaceVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is EmulationViewModel { IsActive: true } && _portalWindow?.IsVisible == true)
                    {
                        IsPortalSurfaceVisible = true;
                        PortalFallbackOpacity = 0;
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    private void HidePortal()
    {
        PortalFallbackOpacity = 1;
        IsPortalSurfaceVisible = false;
        _portalWindow?.Hide();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_portalWindow == null || DataContext is not EmulationViewModel { IsCompositionCaptureVisible: true })
            return;

        if (_portalWindow.IsVisible)
        {
            SyncPortalWindow();
        }
    }

    private void SyncPortalWindow()
    {
        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow) return;

        var portal = this.FindControl<Control>("PortalPortal");
        if (portal == null) return;

        var topLeft = portal.TranslatePoint(new Point(0, 0), mainWindow);
        var bottomRight = portal.TranslatePoint(new Point(portal.Bounds.Width, portal.Bounds.Height), mainWindow);
        if (topLeft == null || bottomRight == null) return;

        var screenTopLeft = mainWindow.PointToScreen(topLeft.Value);
        var width = Math.Max(0, bottomRight.Value.X - topLeft.Value.X);
        var height = Math.Max(0, bottomRight.Value.Y - topLeft.Value.Y) + PortalTopOverlapPixels + PortalBottomOverlapPixels;

        _portalWindow.Position = new PixelPoint(screenTopLeft.X, screenTopLeft.Y - (int)PortalTopOverlapPixels);
        _portalWindow.Width = width;
        _portalWindow.Height = height;
        UpdateWindowZOrder();
    }

    private void AttachPortalCaptureBindings()
    {
        _captureInitializingSubscription?.Dispose();
        _captureInitializingSubscription = null;
        _captureTintSubscription?.Dispose();
        _captureTintSubscription = null;

        var captureControl = _portalWindow?.CaptureHostControl;
        if (captureControl == null)
        {
            IsPortalCaptureInitializing = false;
            return;
        }

        if (captureControl.ColorTint != PortalCaptureTint)
        {
            captureControl.ColorTint = PortalCaptureTint;
        }

        IsPortalCaptureInitializing = false;

        _captureTintSubscription = captureControl
            .GetObservable(WgcCaptureControl.ColorTintProperty)
            .Subscribe(new SimpleObserver<Color>(value =>
            {
                if (_portalCaptureTint != value)
                {
                    SetAndRaise(PortalCaptureTintProperty, ref _portalCaptureTint, value);
                }
            }));
    }

    private void UpdateWindowZOrder()
    {
        if (_portalWindow == null || TopLevel.GetTopLevel(this) is not Window mainWindow) return;

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
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
