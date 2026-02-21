using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;
using System.Reflection;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// A behavior that closes/hides a target control when clicking outside of it.
/// Useful for overlays, dropdowns, and modal-like controls.
/// </summary>
public class CloseOnClickOutsideBehavior : Behavior<Window>
{
    /// <summary>
    /// Defines the <see cref="TargetControl"/> property.
    /// </summary>
    public static readonly StyledProperty<Control?> TargetControlProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, Control?>(nameof(TargetControl));

    /// <summary>
    /// Gets or sets the target control that should be closed when clicking outside of it.
    /// </summary>
    public Control? TargetControl
    {
        get => GetValue(TargetControlProperty);
        set => SetValue(TargetControlProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsEnabled"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsEnabledProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, bool>(nameof(IsEnabled), true);

    /// <summary>
    /// Gets or sets a value indicating whether the behavior is enabled.
    /// When bound to a visibility property, this determines when click-outside detection is active.
    /// </summary>
    public bool IsEnabled
    {
        get => GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="CloseCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, System.Windows.Input.ICommand?>(nameof(CloseCommand));

    /// <summary>
    /// Gets or sets the command to execute when clicking outside the target control.
    /// </summary>
    public System.Windows.Input.ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="PropertyOwner"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> PropertyOwnerProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, object?>(nameof(PropertyOwner));

    /// <summary>
    /// Gets or sets the object that owns the property to be set when clicking outside.
    /// </summary>
    public object? PropertyOwner
    {
        get => GetValue(PropertyOwnerProperty);
        set => SetValue(PropertyOwnerProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="PropertyName"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> PropertyNameProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, string?>(nameof(PropertyName));

    /// <summary>
    /// Gets or sets the name of the boolean property to set to false when clicking outside.
    /// For example, "ShowSettingsOverlay" or "IsOpen".
    /// </summary>
    public string? PropertyName
    {
        get => GetValue(PropertyNameProperty);
        set => SetValue(PropertyNameProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed);
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only process if the behavior is enabled
        if (!IsEnabled || TargetControl == null)
            return;

        // Get the pointer position relative to the target control
        var point = e.GetPosition(TargetControl);

        // Check if the click is outside the target control bounds
        if (point.X < 0 || point.Y < 0 ||
            point.X > TargetControl.Bounds.Width ||
            point.Y > TargetControl.Bounds.Height)
        {
            // Execute close command if provided
            if (CloseCommand?.CanExecute(null) == true)
            {
                CloseCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Otherwise, try to set the property to false using reflection
            if (PropertyOwner != null && !string.IsNullOrEmpty(PropertyName))
            {
                var propertyInfo = PropertyOwner.GetType().GetProperty(PropertyName, BindingFlags.Public | BindingFlags.Instance);
                if (propertyInfo != null && propertyInfo.PropertyType == typeof(bool) && propertyInfo.CanWrite)
                {
                    propertyInfo.SetValue(PropertyOwner, false);
                    e.Handled = true;
                }
            }
        }
    }
}
