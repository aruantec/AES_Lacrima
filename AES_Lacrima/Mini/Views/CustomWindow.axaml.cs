using AES_Core.DI;
using Avalonia.Controls;
using Avalonia.Input;

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

        switch (e.Key)
        {
            case Key.MediaNextTrack:
                if (vm.NextCommand.CanExecute(null))
                    vm.NextCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.MediaPreviousTrack:
                if (vm.PreviousCommand.CanExecute(null))
                    vm.PreviousCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.MediaPlayPause:
                if (vm.PlayPauseCommand.CanExecute(null))
                    vm.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}