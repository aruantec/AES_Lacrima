using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima;

public partial class Rpcs3CustomConfigOverlay : UserControl
{
    public Rpcs3CustomConfigOverlay()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
