using AES_Core.DI;
using AES_Emulation.Windows.API;
using AES_Lacrima.Services;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using log4net;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.ComponentModel;
using System.Linq;

namespace AES_Lacrima.Views
{
    public partial class MainWindow : Window
    {
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MainWindow>();
        private bool _ignoreSizeChange = true;
        private double _lastRenderScale = double.NaN;
        private MainWindowViewModel? _mainViewModel;
        private NavigationService? _navigationService;
        private MusicViewModel? _musicViewModel;
        private EmulationViewModel? _emulationViewModel;
        private bool _isFullscreenActive;
        private MainWindowCaptureFullscreenState? _savedFullscreenState;
        private FullscreenCursorAutoHideHelper? _cursorAutoHide;

        public MainWindow()
        {
            InitializeComponent();
            DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MainWindowViewModel));
            DataContextChanged += OnMainDataContextChanged;
            OnMainDataContextChanged(null, EventArgs.Empty);
            Opened += OnOpened;
            Closing += OnClosing;
            Closed += OnClosed;
            SizeChanged += OnSizeChanged;
            LayoutUpdated += OnLayoutUpdated;
            KeyDown += OnKeyDown;
        }

        private void OnMainDataContextChanged(object? sender, EventArgs e)
        {
            if (_isFullscreenActive)
                ExitWindowFullscreen();

            DetachMainViewModelSubscription();
            DetachNavigationSubscription();
            DetachMediaViewModelSubscription();
            DetachEmulationViewModelSubscription();

            _mainViewModel = DataContext as MainWindowViewModel;
            if (_mainViewModel != null)
            {
                _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
                AttachNavigationService(_mainViewModel.NavigationService);
            }

            AttachActiveFullscreenViewModel();
        }

        private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainWindowViewModel.NavigationService))
                return;

            DetachNavigationSubscription();
            AttachNavigationService(_mainViewModel?.NavigationService);
            AttachActiveFullscreenViewModel();
        }

        private void AttachNavigationService(NavigationService? navigationService)
        {
            _navigationService = navigationService;
            if (_navigationService != null)
                _navigationService.PropertyChanged += OnNavigationServicePropertyChanged;
        }

        private void DetachNavigationSubscription()
        {
            if (_navigationService != null)
                _navigationService.PropertyChanged -= OnNavigationServicePropertyChanged;
            _navigationService = null;
        }

        private void OnNavigationServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(NavigationService.View))
                return;

            AttachActiveFullscreenViewModel();
            _mainViewModel?.RefreshHomeBackgroundVisibility();
        }

        private void AttachActiveFullscreenViewModel()
        {
            var previousMusic = _musicViewModel;
            var previousEmulation = _emulationViewModel;
            DetachMediaViewModelSubscription();
            DetachEmulationViewModelSubscription();

            _musicViewModel = ResolveActiveMediaViewModel();
            _emulationViewModel = _navigationService?.View as EmulationViewModel;

            if (_musicViewModel != null)
                _musicViewModel.PropertyChanged += OnMusicViewModelPropertyChanged;

            if (_emulationViewModel != null)
                _emulationViewModel.PropertyChanged += OnEmulationViewModelPropertyChanged;

            if (previousMusic != null && !ReferenceEquals(previousMusic, _musicViewModel) && previousMusic.IsFullscreen)
            {
                previousMusic.IsFullscreen = false;
                previousMusic.IsVideoExpanded = false;
            }

            if (previousEmulation != null && !ReferenceEquals(previousEmulation, _emulationViewModel) && previousEmulation.IsFullscreen)
                previousEmulation.IsFullscreen = false;

            SyncWindowFullscreenFromActiveViewModel();
        }

        private void SyncWindowFullscreenFromActiveViewModel()
        {
            var wantsFullscreen = IsActiveViewModelFullscreen();

            if (_isFullscreenActive && !wantsFullscreen)
                ExitWindowFullscreen();
            else if (!_isFullscreenActive && wantsFullscreen)
            {
                EnterWindowFullscreen();
                if (_emulationViewModel?.IsFullscreen == true)
                {
                    Dispatcher.UIThread.Post(
                        () => FindEmulationView()?.EnterCapturePresentationFullscreen(),
                        DispatcherPriority.Loaded);
                }
            }
        }

        private bool IsActiveViewModelFullscreen() =>
            _emulationViewModel?.IsFullscreen == true || _musicViewModel?.IsFullscreen == true;

        private MusicViewModel? ResolveActiveMediaViewModel()
        {
            if (_navigationService?.View is MusicViewModel activeMedia)
                return activeMedia;

            return _mainViewModel?.MusicViewModel;
        }

        private void DetachMainViewModelSubscription()
        {
            if (_mainViewModel != null)
                _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            _mainViewModel = null;
        }

        private void DetachMediaViewModelSubscription()
        {
            if (_musicViewModel != null)
                _musicViewModel.PropertyChanged -= OnMusicViewModelPropertyChanged;
            _musicViewModel = null;
        }

        private void OnMusicViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MusicViewModel.IsFullscreen))
                return;

            OnActiveViewModelFullscreenChanged();
        }

        private void OnEmulationViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(EmulationViewModel.IsFullscreen))
                return;

            OnActiveViewModelFullscreenChanged();
        }

        private void OnActiveViewModelFullscreenChanged()
        {
            if (DataContext is ViewModels.MainWindowViewModel mainVm)
                mainVm.IsFullScreen = IsActiveViewModelFullscreen();

            if (_emulationViewModel?.IsFullscreen == true)
            {
                if (!_isFullscreenActive)
                    EnterWindowFullscreen();

                Dispatcher.UIThread.Post(
                    () => FindEmulationView()?.EnterCapturePresentationFullscreen(),
                    DispatcherPriority.Loaded);
                return;
            }

            if (_emulationViewModel?.IsFullscreen == false)
            {
                FindEmulationView()?.ExitCapturePresentationFullscreen();
                if (_isFullscreenActive)
                    ExitWindowFullscreen();

                Dispatcher.UIThread.Post(
                    () => FindEmulationView()?.OnWindowFullscreenEnded(),
                    DispatcherPriority.Loaded);
                return;
            }

            if (_musicViewModel == null)
                return;

            if (_musicViewModel.IsFullscreen && !_isFullscreenActive)
                EnterWindowFullscreen();
            else if (!_musicViewModel.IsFullscreen && _isFullscreenActive)
                ExitWindowFullscreen();
        }

        private EmulationView? FindEmulationView() =>
            this.GetVisualDescendants().OfType<EmulationView>().FirstOrDefault();

        private void EnterWindowFullscreen()
        {
            var screenBounds = Screens?.Primary?.Bounds
                ?? new PixelRect(0, 0, (int)ClientSize.Width, (int)ClientSize.Height);

            // Save state and hide chrome, then let OS native fullscreen
            // handle covering the entire screen (including taskbar).
            _savedFullscreenState = EnterCaptureFullscreenMode(screenBounds);
            WindowState = WindowState.FullScreen;
            _isFullscreenActive = true;

            // Let bindings control shader/background visibility during fullscreen.
            ClearCaptureLayerOverrides();
            _mainViewModel?.RefreshHomeBackgroundVisibility();

            _cursorAutoHide = new FullscreenCursorAutoHideHelper(this);
            _cursorAutoHide.Start();
        }

        private void ExitWindowFullscreen()
        {
            _cursorAutoHide?.Dispose();
            _cursorAutoHide = null;

            // Must restore to Normal first so ExitCaptureFullscreenMode can resize.
            WindowState = WindowState.Normal;

            if (_savedFullscreenState != null)
            {
                ExitCaptureFullscreenMode(_savedFullscreenState);
                _savedFullscreenState = null;
            }

            // Let bindings re-take control after exit mode restores stale values.
            ClearCaptureLayerOverrides();
            _mainViewModel?.RefreshHomeBackgroundVisibility();

            if (_musicViewModel != null)
                _musicViewModel.IsVideoExpanded = false;

            _isFullscreenActive = false;
        }

        private void ClearCaptureLayerOverrides()
        {
            var shaderToyLayer = this.FindControl<Control>("ShaderToyLayer");
            if (shaderToyLayer != null)
                shaderToyLayer.ClearValue(IsVisibleProperty);
            var backgroundImageLayer = this.FindControl<Control>("BackgroundImageLayer");
            if (backgroundImageLayer != null)
                backgroundImageLayer.ClearValue(IsVisibleProperty);
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            PreparePersistedWindowSizeForShutdown();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _cursorAutoHide?.Dispose();
            _cursorAutoHide = null;
            DetachMainViewModelSubscription();
            DetachNavigationSubscription();
            DetachMediaViewModelSubscription();
            DetachEmulationViewModelSubscription();
        }

        private void DetachEmulationViewModelSubscription()
        {
            if (_emulationViewModel != null)
                _emulationViewModel.PropertyChanged -= OnEmulationViewModelPropertyChanged;
            _emulationViewModel = null;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e == null)
                return;

            if (e.Key == Key.Escape && _isFullscreenActive)
            {
                if (_emulationViewModel?.IsFullscreen == true)
                    _emulationViewModel.IsFullscreen = false;
                else if (_musicViewModel != null)
                    _musicViewModel.IsFullscreen = false;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Back && DataContext is ViewModels.MainWindowViewModel vmNav)
            {
                if (vmNav.NavigationService?.NavigateBackCommand.CanExecute(null) == true)
                    vmNav.NavigationService.NavigateBackCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (DataContext is not ViewModels.MainWindowViewModel vm)
                return;

            var music = vm.MusicViewModel;
            if (music == null)
                return;

            MediaKeyRouting.TryHandle(
                e,
                new MediaKeyHandlers(
                    () => ExecuteIfCan(music.PlayNextCommand),
                    () => ExecuteIfCan(music.PlayPreviousCommand),
                    () => ExecuteIfCan(music.TogglePlayCommand)));
        }

        private static bool ExecuteIfCan(System.Windows.Input.ICommand? command)
        {
            if (command?.CanExecute(null) != true)
                return false;

            command.Execute(null);
            return true;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            if (!(DataContext is ViewModels.MainWindowViewModel vm && vm.SettingsViewModel != null))
                return;

            if (!vm.IsPrepared)
            {
                vm.Prepare();
                vm.IsPrepared = true;
            }

            var renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _lastRenderScale = renderScale;
            if (!vm.SettingsViewModel.HasPersistedScaleFactor)
            {
                vm.SettingsViewModel.ScaleFactor = Math.Clamp(renderScale, 1.0, 2.0);
            }

            CenterWindow(vm, renderScale);
            UpdateMainBorderClip();

            // Allow size change tracking after initial layout settles.
            _ignoreSizeChange = true;
            Dispatcher.UIThread.Post(() => _ignoreSizeChange = false, DispatcherPriority.Background);
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (_ignoreSizeChange)
                return;

            if (WindowState != WindowState.Normal)
                return;

            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.HasUserResizedWindow = true;
                vm.WindowWidth = Width;
                vm.WindowHeight = Height;
            }
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            UpdateMainBorderClip();

            var currentScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? _lastRenderScale;
            if (double.IsNaN(currentScale) || Math.Abs(currentScale - _lastRenderScale) < 0.01)
                return;

            _lastRenderScale = currentScale;

            if (DataContext is ViewModels.MainWindowViewModel vm && vm.IsPrepared && !vm.HasUserResizedWindow)
            {
                // When render scaling changes (for example when moving the window to another monitor)
                // only apply default sizing/centering if the window is currently on the primary screen
                // or if the current screen could not be determined. This avoids forcing the window
                // back to the primary display when the user moves it between monitors.
                var primary = Screens.Primary;
                var currentScreen = GetCurrentScreenForWindow(currentScale);
                if (currentScreen != null && primary != null && !currentScreen.WorkingArea.Equals(primary.WorkingArea))
                    return;

                _ignoreSizeChange = true;
                ApplyDefaultWindowSizeFromScreen(vm);
                CenterWindow(vm, currentScale);
                Dispatcher.UIThread.Post(() => _ignoreSizeChange = false, DispatcherPriority.Background);
            }
        }

        private void UpdateMainBorderClip()
        {
            if (MainBorder is null || MainBorderContent is null)
                return;

            var size = MainBorderContent.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var radius = Math.Max(0, MainBorder.CornerRadius.TopLeft);
            MainBorderContent.Clip = new RectangleGeometry
            {
                Rect = new Rect(size),
                RadiusX = radius,
                RadiusY = radius
            };
        }

        private void CenterWindow(ViewModels.MainWindowViewModel vm, double? renderScaleOverride = null)
        {
            var primary = Screens.Primary;
            if (primary == null)
                return;

            var renderScale = renderScaleOverride ?? (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
            var width = (!double.IsNaN(vm.WindowWidth) && vm.WindowWidth > 0) ? vm.WindowWidth : Width;
            var height = (!double.IsNaN(vm.WindowHeight) && vm.WindowHeight > 0) ? vm.WindowHeight : Height;
            if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0)
                return;

            var centerX = primary.WorkingArea.X + (int)((primary.WorkingArea.Width - (width * renderScale)) / 2);
            var centerY = primary.WorkingArea.Y + (int)((primary.WorkingArea.Height - (height * renderScale)) / 2);

            Position = new PixelPoint(centerX, centerY);
        }

        private Screen? GetCurrentScreenForWindow(double renderScale)
        {
            try
            {
                var all = Screens.All;
                if (all == null)
                    return null;

                // Compute window center in physical pixels
                var pxWidth = (int)(Width * renderScale);
                var pxHeight = (int)(Height * renderScale);
                var centerX = Position.X + pxWidth / 2;
                var centerY = Position.Y + pxHeight / 2;

                foreach (var s in all)
                {
                    var wa = s.WorkingArea;
                    if (centerX >= wa.X && centerX <= wa.X + wa.Width && centerY >= wa.Y && centerY <= wa.Y + wa.Height)
                        return s;
                }
            }
            catch (Exception ex)
            {
                // Log and return null so callers fall back to primary screen behavior
                Log.Warn("Failed to determine current screen for window", ex);
            }

            return null;
        }

        private void ApplyDefaultWindowSizeFromScreen(ViewModels.MainWindowViewModel vm)
        {
            var primary = Screens.Primary;
            if (primary == null)
                return;

            var scale = Math.Max(1.0, primary.Scaling);
            var baseWidth = (primary.WorkingArea.Width * 0.7) / scale;
            var baseHeight = (primary.WorkingArea.Height * 0.9) / scale;

            if (baseWidth <= 0 || baseHeight <= 0)
                return;

            vm.WindowWidth = baseWidth;
            vm.WindowHeight = baseHeight;
            Width = baseWidth;
            Height = baseHeight;
        }
    }
}
