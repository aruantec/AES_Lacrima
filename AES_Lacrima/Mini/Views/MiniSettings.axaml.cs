using System;
using AES_Lacrima.ViewModels;
using Avalonia.Controls;

namespace AES_Lacrima.Mini.Views;

public partial class MiniSettings : UserControl
{
    public MiniSettings()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel settings)
            settings.ApplyPlaybackBehaviorSettings();
    }
}