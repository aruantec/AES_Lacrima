using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace AES_Lacrima.Behaviors;

/// <summary>
/// A behavior that closes/hides a target control when clicking outside of it.
/// Useful for overlays, dropdowns, and modal-like controls.
/// </summary>
public class CloseOnClickOutsideBehavior : Behavior<InputElement>
{
    private InputElement? _pointerEventSource;

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
    public new static readonly StyledProperty<bool> IsEnabledProperty =
        AvaloniaProperty.Register<CloseOnClickOutsideBehavior, bool>(nameof(IsEnabled), true);

    /// <summary>
    /// Gets or sets a value indicating whether the behavior is enabled.
    /// When bound to a visibility property, this determines when click-outside detection is active.
    /// </summary>
    public new bool IsEnabled
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

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null) return;

        AssociatedObject.AttachedToVisualTree += OnAssociatedObjectAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree += OnAssociatedObjectDetachedFromVisualTree;
        HookPointerSource();
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.AttachedToVisualTree -= OnAssociatedObjectAttachedToVisualTree;
            AssociatedObject.DetachedFromVisualTree -= OnAssociatedObjectDetachedFromVisualTree;
        }

        UnhookPointerSource();
    }

    private void OnAssociatedObjectAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => HookPointerSource();

    private void OnAssociatedObjectDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => UnhookPointerSource();

    private void HookPointerSource()
    {
        if (_pointerEventSource != null || AssociatedObject == null)
            return;

        // Behaviors can be attached to Window or nested controls.
        // We listen at TopLevel to catch click-outside events for the whole surface.
        var topLevel = TopLevel.GetTopLevel(AssociatedObject);
        if (topLevel is InputElement inputElement)
        {
            _pointerEventSource = inputElement;
            _pointerEventSource.AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        }
    }

    private void UnhookPointerSource()
    {
        if (_pointerEventSource == null)
            return;

        _pointerEventSource.RemoveHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed);
        _pointerEventSource = null;
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
            if (CloseCommand?.CanExecute(null) == true)
            {
                CloseCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
