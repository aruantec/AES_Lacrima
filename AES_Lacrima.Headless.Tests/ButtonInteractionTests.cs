using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using System.Windows.Input;

namespace AES_Lacrima.Headless.Tests;

public sealed class ButtonInteractionTests
{
    [AvaloniaFact]
    public void MouseClick_RaisesButtonClickEvent()
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = "Click me"
        };

        var window = new Window
        {
            Width = 100,
            Height = 100,
            Content = button
        };

        var clickCount = 0;
        button.Click += (_, _) => clickCount++;

        window.Show();

        window.MouseDown(new Point(50, 50), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(new Point(50, 50), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(1, clickCount);
    }

    [AvaloniaFact]
    public void SpaceKey_ExecutesFocusedButtonCommand()
    {
        var command = new TestCommand();
        var button = new Button
        {
            Width = 80,
            Height = 30,
            Command = command,
            Content = "Run"
        };

        var window = new Window
        {
            Width = 120,
            Height = 80,
            Content = button
        };

        window.Show();
        button.Focus();

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.Equal(1, command.ExecuteCalls);
    }

    private sealed class TestCommand : ICommand
    {
        public int ExecuteCalls { get; private set; }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => ExecuteCalls++;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
