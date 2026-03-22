using AES_Core.DI;
using Avalonia.Controls;

namespace AES_Lacrima.Views.Mobile;

public partial class MobileMainView : UserControl
{
    public MobileMainView()
    {
        InitializeComponent();
        DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MainWindowViewModel));
    }
}
