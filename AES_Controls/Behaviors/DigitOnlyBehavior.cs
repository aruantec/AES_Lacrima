using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Xaml.Interactivity;

namespace AES_Controls.Behaviors
{
    /// <summary>
    /// Behavior that restricts a TextBox so only digit characters may be entered.
    /// Useful for settings or numeric entry fields where non-numeric input should
    /// be ignored.  The behavior intercepts the TextInput event and marks
    /// invalid characters as handled.
    /// </summary>
    public class DigitOnlyBehavior : Behavior<TextBox>
    {
        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                AssociatedObject.TextInput += OnTextInput;
                AssociatedObject.TextChanged += OnTextChanged;
            }
        }

        protected override void OnDetaching()
        {
            if (AssociatedObject != null)
            {
                AssociatedObject.TextInput -= OnTextInput;
                AssociatedObject.TextChanged -= OnTextChanged;
            }
            base.OnDetaching();
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text))
                return;

            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    break;
                }
            }
        }

        private void OnTextChanged(object? sender, global::System.EventArgs e)
        {
            if (sender is TextBox tb)
            {
                var text = tb.Text;
                if (string.IsNullOrEmpty(text))
                    return;

                var filtered = new string(text.Where(char.IsDigit).ToArray());
                if (filtered != text)
                {
                    var sel = tb.SelectionStart;
                    tb.Text = filtered;
                    tb.SelectionStart = Math.Min(filtered.Length, sel);
                }
            }
        }
    }
}