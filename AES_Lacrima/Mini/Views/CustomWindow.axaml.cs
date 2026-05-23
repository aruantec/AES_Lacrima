using AES_Core.DI;
using AES_Lacrima.Services;
using Avalonia.Controls;
using Avalonia.Input;
using System.Windows.Input;

namespace AES_Lacrima.Mini.Views;

public partial class CustomWindow : Window
{
    public CustomWindow()
    {
        InitializeComponent();
        DataContext ??= DiLocator.TryResolve(typeof(ViewModels.MinViewModel));
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e == null)
            return;

        if (DataContext is not ViewModels.MinViewModel vm)
            return;

        MediaKeyRouting.TryHandle(
            e,
            new MediaKeyHandlers(
                () => ExecuteIfCan(vm.NextCommand),
                () => ExecuteIfCan(vm.PreviousCommand),
                () => ExecuteIfCan(vm.PlayPauseCommand)));
    }

    private static bool ExecuteIfCan(ICommand? command)
    {
        if (command?.CanExecute(null) != true)
            return false;

        command.Execute(null);
        return true;
    }
}
