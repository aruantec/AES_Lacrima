using AES_Emulation.Windows;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace AES_Lacrima.Views;

public partial class EmulationView : UserControl
{
    public EmulationView()
    {
        InitializeComponent();
        AttachEmulatorCaptureControl();
    }

    private void AttachEmulatorCaptureControl()
    {
        var host = this.FindControl<Border>("EmulatorCaptureHost");
        if (host == null || host.Child != null)
            return;

        var captureControl = new WgcCaptureControl
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        captureControl.Bind(WgcCaptureControl.TargetHwndProperty, new Binding("EmulatorTargetHwnd"));
        host.Child = captureControl;
    }
}
