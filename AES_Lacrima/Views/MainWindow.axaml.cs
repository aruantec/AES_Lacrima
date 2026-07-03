using AES_Core.DI;
using AES_Lacrima.Services;
using AES_Controls.Navigation;
using AES_Controls.GL;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using log4net;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.Views
{
    public partial class MainWindow : Window
    {
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MainWindow>();
        private bool _ignoreSizeChange = true;
        private double _lastRenderScale = double.NaN;

        public MainWindow()
        {
            InitializeComponent();
            DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MainWindowViewModel));
            Opened += OnOpened;
            SizeChanged += OnSizeChanged;
            LayoutUpdated += OnLayoutUpdated;
            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e == null)
                return;

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

            ScheduleMediaViewWarmup();
        }

        private void ScheduleMediaViewWarmup()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is not ViewModels.MainWindowViewModel vm || ContentPresenter == null)
                    return;

                if (App.SkipNextMediaViewWarmup)
                {
                    App.SkipNextMediaViewWarmup = false;
                    return;
                }

                var views = vm.WarmedViewModels;
                if (views == null || views.Count == 0)
                    return;

                // Pay the Emulation XAML cost immediately so navigation cannot beat warmup.
                var emulation = views.Find(v => v is ViewModels.EmulationViewModel);
                if (emulation != null)
                    ContentPresenter.PrewarmBuild(emulation);

                _ = FinishMediaViewWarmupAsync(views);
            }, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Restores AES mode UI state after returning from mini mode.
        /// </summary>
        public void ResumeFromMiniMode()
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
                vm.IsShaderToyRenderingPaused = false;

            DiLocator.ResolveViewModel<NavigationService>()?.ResetToHomeView();
            DiLocator.ResolveViewModel<ViewModels.MusicViewModel>()?.InitializeTaskbarButtons();
            UpdateMainBorderClip();
        }

        private async Task FinishMediaViewWarmupAsync(System.Collections.Generic.List<AES_Core.Interfaces.IViewModelBase> views)
        {
            if (ContentPresenter == null)
                return;

            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (ContentPresenter.Bounds.Width > 0 && ContentPresenter.Bounds.Height > 0)
                    break;

                await Task.Delay(16).ConfigureAwait(true);
            }

            await ContentPresenter.WarmupAsync(views).ConfigureAwait(true);
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (OperatingSystem.IsWindows())
            {
                if (IsCapturePresentationFullscreen)
                {
                    MaintainCapturePresentationBounds();
                    return;
                }

                if (_desktopDisplayIsolation != null)
                {
                    if (DataContext is ViewModels.MainWindowViewModel isolationVm)
                    {
                        isolationVm.HasUserResizedWindow = true;
                        isolationVm.WindowWidth = Width;
                        isolationVm.WindowHeight = Height;
                    }

                    MaintainDesktopDisplayIsolation();
                    return;
                }
            }

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
            UpdateShaderDiscOccluder();

            if (IsCapturePresentationFullscreen)
                return;

            var currentScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? _lastRenderScale;
            if (_desktopDisplayIsolation != null)
            {
                if (!double.IsNaN(currentScale) && Math.Abs(currentScale - _lastRenderScale) >= 0.01)
                    RecoverFromDisplayModeChange();
                return;
            }

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

        private void UpdateShaderDiscOccluder()
        {
            var shader = this.FindControl<GlShaderToyControl>("ShaderToyLayer");
            if (shader == null)
                return;

            var player = ShaderDiscOccluderSync.FindPlayerWidget(this);
            ShaderDiscOccluderSync.Apply(shader, player);
        }

        internal void SyncShaderDiscOccluder() => UpdateShaderDiscOccluder();

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
