using AES_Core.DI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using log4net;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Runtime.InteropServices;

namespace AES_Lacrima.Views
{
    public partial class MainWindow : Window
    {
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MainWindow>();
        private bool _ignoreSizeChange = true;
        private double _lastRenderScale = double.NaN;
        private VisualOverlayWindow? _visualOverlayWindow;

        private static readonly IntPtr HWND_BOTTOM = new(1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        public MainWindow()
        {
            InitializeComponent();
            DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MainWindowViewModel));
            Opened += OnOpened;
            SizeChanged += OnSizeChanged;
            LayoutUpdated += OnLayoutUpdated;
            KeyDown += OnKeyDown;
            Closed += OnClosed;
            PositionChanged += OnPositionChanged;
            
            // Re-sync overlay on many events to ensure it stays visible and in correct position
            Avalonia.AvaloniaObjectExtensions.GetObservable(this, Window.WindowStateProperty)
                .Subscribe(new WindowStateObserver(() => SyncVisualOverlayWindow()));
            
            this.GetObservable(Visual.BoundsProperty)
                .Subscribe(new SimpleBoundsObserver(() => SyncVisualOverlayWindow()));
        }

        private sealed class SimpleBoundsObserver : IObserver<Rect>
        {
            private readonly Action _callback;
            public SimpleBoundsObserver(Action callback) => _callback = callback;
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(Rect value) => _callback();
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

            switch (e.Key)
            {
                case Key.MediaNextTrack:
                    if (music.PlayNextCommand.CanExecute(null))
                        music.PlayNextCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.MediaPreviousTrack:
                    if (music.PlayPreviousCommand.CanExecute(null))
                        music.PlayPreviousCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.MediaPlayPause:
                    if (music.TogglePlayCommand.CanExecute(null))
                        music.TogglePlayCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
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
            EnsureVisualOverlayWindow();

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

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_visualOverlayWindow != null)
            {
                _visualOverlayWindow.Close();
                _visualOverlayWindow = null;
            }
        }

        private void OnPositionChanged(object? sender, PixelPointEventArgs e)
        {
            SyncVisualOverlayWindow();
        }

        private void EnsureVisualOverlayWindow()
        {
            if (_visualOverlayWindow != null)
                return;

            _visualOverlayWindow = new VisualOverlayWindow
            {
                Position = Position,
                Width = Bounds.Width,
                Height = Bounds.Height
            };

            _visualOverlayWindow.Show();
            SyncVisualOverlayWindow();
        }

        private void SyncVisualOverlayWindow()
        {
            if (_visualOverlayWindow == null)
                return;

            if (WindowState == WindowState.Minimized)
            {
                _visualOverlayWindow.Hide();
                return;
            }

            _visualOverlayWindow.Position = Position;
            _visualOverlayWindow.Width = Bounds.Width;
            _visualOverlayWindow.Height = Bounds.Height;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var renderScale = Math.Max(1.0, RenderScaling);
                _visualOverlayWindow.MoveResizeUnconstrained(
                    Position,
                    (int)Math.Round(Bounds.Width * renderScale),
                    (int)Math.Round(Bounds.Height * renderScale));
                
                // On Linux, the window might need a nudge to stay on top of the stack if it was hidden
                if (!_visualOverlayWindow.IsVisible)
                {
                    _visualOverlayWindow.Show();
                }
                
                // Lower it to the bottom of the stack (behind the main window)
                _visualOverlayWindow.MoveToBottomOfStack();
            }
            else
            {
                if (!_visualOverlayWindow.IsVisible)
                    _visualOverlayWindow.Show();
            }
        }

        private sealed class WindowStateObserver : IObserver<WindowState>
        {
            private readonly Action _callback;

            public WindowStateObserver(Action callback)
            {
                _callback = callback;
            }

            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(WindowState value) => _callback();
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XLowerWindow(IntPtr display, IntPtr w);

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
