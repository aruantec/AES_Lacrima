using Avalonia;
using Avalonia.Controls;
using System;

namespace AES_Lacrima.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Opened += OnOpened;
        }

        private void OnOpened(object? sender, System.EventArgs e)
        {
            if (!(DataContext is ViewModels.MainWindowViewModel vm && vm.SettingsViewModel != null))
                return;

            var renderScale = VisualRoot?.RenderScaling ?? 1.0;
            if (Math.Abs(vm.SettingsViewModel.ScaleFactor - 1.0) < 0.01)
            {
                vm.SettingsViewModel.ScaleFactor = Math.Clamp(renderScale, 1.0, 2.0);
            }

            var primary = Screens.Primary;
            if (primary == null)
                return;

            var maxW = primary.WorkingArea.Width;
            var maxH = primary.WorkingArea.Height;
            
            // Previous size: 92.4% height, 1.69x width aspect ratio.
            var forceHeight = (maxH * 0.924) / renderScale;
            // Reduce width by 15%: 1.69 * 0.85 = ~1.436 aspect ratio.
            var forceWidth = forceHeight * 1.436;

            // Ensure width doesn't exceed screen limits
            forceWidth = Math.Min(forceWidth, (maxW * 0.98) / renderScale);

            vm.WindowWidth = forceWidth;
            vm.WindowHeight = forceHeight;
            Width = forceWidth;
            Height = forceHeight;

            var centerX = primary.WorkingArea.X + (int)((primary.WorkingArea.Width - (forceWidth * renderScale)) / 2);
            var centerY = primary.WorkingArea.Y + (int)((primary.WorkingArea.Height - (forceHeight * renderScale)) / 2);
            
            Position = new PixelPoint(centerX, centerY);
        }
    }
}
