using AES_Core.DI;
using Avalonia.Controls;

namespace AES_Lacrima.Mini.Views;

public partial class CustomWindow : Window
{
    public CustomWindow()
    {
        InitializeComponent();
        DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MinViewModel));
    }
}