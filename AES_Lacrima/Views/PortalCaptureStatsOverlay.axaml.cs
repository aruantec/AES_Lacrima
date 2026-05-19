using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima.Views;

public partial class PortalCaptureStatsOverlay : UserControl
{
    public static readonly StyledProperty<EmulationView?> CaptureHostProperty =
        AvaloniaProperty.Register<PortalCaptureStatsOverlay, EmulationView?>(nameof(CaptureHost));

    public static readonly StyledProperty<EmulationViewModel?> SettingsProperty =
        AvaloniaProperty.Register<PortalCaptureStatsOverlay, EmulationViewModel?>(nameof(Settings));

    public PortalCaptureStatsOverlay()
    {
        InitializeComponent();
    }

    public EmulationView? CaptureHost
    {
        get => GetValue(CaptureHostProperty);
        set => SetValue(CaptureHostProperty, value);
    }

    public EmulationViewModel? Settings
    {
        get => GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
