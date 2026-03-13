using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

namespace AES_Lacrima.Headless.Tests;

public sealed class TextBoxInputTests
{
    [AvaloniaFact]
    public void KeyTextInput_UpdatesFocusedTextBox()
    {
        var textBox = new TextBox();
        var window = new Window
        {
            Width = 200,
            Height = 100,
            Content = textBox
        };

        window.Show();
        textBox.Focus();

        window.KeyTextInput("Hello World");

        Assert.Equal("Hello World", textBox.Text);
    }
}
