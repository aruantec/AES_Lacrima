using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// A behavior that prevents a control from gaining focus when right-clicked.
/// This is useful for controls that should not show focus indicators (like dashed borders)
/// when their context menu is opened via right-click.
/// </summary>
public class PreventFocusOnRightClickBehavior : Behavior<Control>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // If it's a right-click, prevent default behavior (which includes focusing)
        var point = e.GetCurrentPoint(AssociatedObject);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
        }
    }
}
