using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;

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
}
