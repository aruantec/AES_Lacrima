using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace AES_Controls.Behaviors;

/// <summary>
/// Behavior that ensures a <see cref="ComboBox"/>'s popup width matches the
/// scaled width of the control when placed inside a <c>ScalableDecorator</c>.
/// It listens for the dropdown opening and adjusts the popup/child control
/// width to compensate for any render transform scale so items are not clipped.
/// </summary>
public class ComboBoxPopupScaler : Behavior<ComboBox>
{
    private readonly Dictionary<ComboBox, IDisposable> _subscriptions = [];

    /// <summary>
    /// Called when the behavior is attached to a <see cref="ComboBox"/>.
    /// Subscribes to visual tree attachment events and attempts to attach
    /// popup handling immediately if the control is already in the visual tree.
    /// </summary>
    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null) return;
        AssociatedObject.AttachedToVisualTree += OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree += OnDetachedFromVisualTree;
        if (AssociatedObject.IsAttachedToVisualTree())
            TryAttachPopup();
    }

    /// <summary>
    /// Called when the behavior is being detached from the associated control.
    /// Stops listening for events and disposes any subscriptions created for
    /// popup width adjustment.
    /// </summary>
    protected override void OnDetaching()
    {
        base.OnDetaching();
        DetachPopup();
        if (AssociatedObject == null) return;
        AssociatedObject.AttachedToVisualTree -= OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree -= OnDetachedFromVisualTree;
    }

    /// <summary>
    /// Handler for the control being attached to the visual tree. Attempts
    /// to attach popup listeners if the control is inside a scalable scope.
    /// </summary>
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryAttachPopup();
    }

    /// <summary>
    /// Handler for the control being detached from the visual tree. Cleans
    /// up any popup listeners associated with the control.
    /// </summary>
    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachPopup();
    }

    /// <summary>
    /// Checks whether the associated ComboBox is inside a
    /// <see cref="ScalableDecorator"/> and starts listening for drop-down
    /// open events when appropriate.
    /// </summary>
    private void TryAttachPopup()
    {
        if (AssociatedObject == null) return;
        if (AssociatedObject.FindAncestorOfType<ScalableDecorator>() == null) return;
        StartListening(AssociatedObject);
    }

    /// <summary>
    /// Stops listening to drop-down open events for the associated ComboBox
    /// and disposes any subscriptions.
    /// </summary>
    private void DetachPopup()
    {
        if (AssociatedObject == null) return;
        StopListening(AssociatedObject);
    }

    /// <summary>
    /// Starts observing the <see cref="ComboBox.IsDropDownOpenProperty"/>
    /// and schedules an adjustment of the popup width when the dropdown is opened.
    /// </summary>
    /// <param name="combo">The ComboBox to observe.</param>
    private void StartListening(ComboBox combo)
    {
        if (_subscriptions.ContainsKey(combo)) return;

        var subscription = combo.GetObservable(ComboBox.IsDropDownOpenProperty)
            .Subscribe(new SimpleObserver<bool>(isOpen =>
            {
                if (isOpen)
                {
                    Dispatcher.UIThread.Post(() => AdjustPopupWidth(combo), DispatcherPriority.Background);
                }
            }));

        _subscriptions[combo] = subscription;
    }

    /// <summary>
    /// Stops observing the ComboBox and disposes the subscription for the
    /// specified control.
    /// </summary>
    /// <param name="combo">The ComboBox to stop observing.</param>
    private void StopListening(ComboBox combo)
    {
        if (_subscriptions.TryGetValue(combo, out var subscription))
        {
            subscription.Dispose();
            _subscriptions.Remove(combo);
        }
    }

    /// <summary>
    /// Adjusts the width of the popup and its child content to match the
    /// visible (scaled) width of the ComboBox so items are not clipped when
    /// the control is rendered with a scale transform.
    /// </summary>
    /// <param name="combo">The ComboBox whose popup should be adjusted.</param>
    private static void AdjustPopupWidth(ComboBox combo)
    {
        if (combo.Bounds.Width <= 0 && combo.Width <= 0 && combo.DesiredSize.Width <= 0) return;

        var transform = combo.RenderTransform as ScaleTransform;
        double effectiveScale = transform?.ScaleX ?? 1.0;
        var currentWidth = combo.Bounds.Width * effectiveScale;
        if (double.IsNaN(currentWidth) || currentWidth <= 0)
            currentWidth = !double.IsNaN(combo.Width) && combo.Width > 0 ? combo.Width : combo.DesiredSize.Width;
        if (double.IsNaN(currentWidth) || currentWidth <= 0)
            return;

        var popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup == null) return;
        if (popup.Child is { } child)
        {
            child.MinWidth = currentWidth;
            child.Width = currentWidth;
        }
        popup.Width = currentWidth;
    }
}
