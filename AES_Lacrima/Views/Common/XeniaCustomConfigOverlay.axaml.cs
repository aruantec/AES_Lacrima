using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima;

public partial class XeniaCustomConfigOverlay : UserControl
{
    public XeniaCustomConfigOverlay()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
