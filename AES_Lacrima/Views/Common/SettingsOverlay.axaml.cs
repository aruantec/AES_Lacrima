using System;
using AES_Lacrima.ViewModels;
using Avalonia.Controls;
using Avalonia.Media;

namespace AES_Lacrima.Views.Common;

public partial class SettingsOverlay : UserControl
{
    public SettingsOverlay()
    {
        InitializeComponent();
        Background = Design.IsDesignMode ? Brushes.Black : new SolidColorBrush(Color.Parse("#E6000000"));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel settings)
            settings.ApplyPlaybackBehaviorSettings();
    }
}