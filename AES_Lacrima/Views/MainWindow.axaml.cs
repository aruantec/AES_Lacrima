using AES_Core.DI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace AES_Lacrima.Views
{
    public partial class MainWindow : Window
    {
        private bool _ignoreSizeChange = true;
        private double _lastRenderScale = double.NaN;

        public MainWindow()
        {
            InitializeComponent();
            DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MainWindowViewModel));
            Opened += OnOpened;
            SizeChanged += OnSizeChanged;
            LayoutUpdated += OnLayoutUpdated;
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

            var renderScale = VisualRoot?.RenderScaling ?? 1.0;
            _lastRenderScale = renderScale;
            if (Math.Abs(vm.SettingsViewModel.ScaleFactor - 1.0) < 0.01)
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
            }
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            UpdateMainBorderClip();

            var currentScale = VisualRoot?.RenderScaling ?? _lastRenderScale;
            if (double.IsNaN(currentScale) || Math.Abs(currentScale - _lastRenderScale) < 0.01)
                return;

            _lastRenderScale = currentScale;

            if (DataContext is ViewModels.MainWindowViewModel vm && vm.IsPrepared && !vm.HasUserResizedWindow)
            {
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

            var renderScale = renderScaleOverride ?? (VisualRoot?.RenderScaling ?? 1.0);
            var width = (!double.IsNaN(vm.WindowWidth) && vm.WindowWidth > 0) ? vm.WindowWidth : Width;
            var height = (!double.IsNaN(vm.WindowHeight) && vm.WindowHeight > 0) ? vm.WindowHeight : Height;
            if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0)
                return;

            var centerX = primary.WorkingArea.X + (int)((primary.WorkingArea.Width - (width * renderScale)) / 2);
            var centerY = primary.WorkingArea.Y + (int)((primary.WorkingArea.Height - (height * renderScale)) / 2);

            Position = new PixelPoint(centerX, centerY);
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
