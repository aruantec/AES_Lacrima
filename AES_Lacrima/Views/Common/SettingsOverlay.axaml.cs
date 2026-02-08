using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima.Views.Common;

public partial class SettingsOverlay : UserControl
{
    public SettingsOverlay()
    {
        InitializeComponent();

        // Show black background in the XAML designer only
        if (Design.IsDesignMode)
            Background = Brushes.Black;
        else
            Background = Brushes.Transparent;
    }
}