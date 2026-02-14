using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// A behavior that focuses the associated control when it becomes visible.
/// Especially useful for focusing a TextBox when entering edit mode.
/// </summary>
public class FocusOnVisibleBehavior : Behavior<Control>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject?.PropertyChanged += AssociatedObject_PropertyChanged;
        // Check initial state if it's already visible when attached
        if (AssociatedObject != null && AssociatedObject.IsVisible)
        {
            FocusAndSelect();
        }
    }

    private void AssociatedObject_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (AssociatedObject != null && e.Property == Control.IsVisibleProperty && AssociatedObject.IsVisible)
        {
            FocusAndSelect();
        }
    }

    private void FocusAndSelect()
    {
        // Use Dispatcher to ensure focus is set after layout/visibility change
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AssociatedObject?.Focus();
            if (AssociatedObject is TextBox textBox)
            {
                textBox.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }
}
