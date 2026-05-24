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
/// Ensures a <see cref="ComboBox"/> dropdown fits inside the nearest scroll viewport: matches scaled width
/// inside <see cref="ScalableDecorator"/> and sizes the drop-down to use available space while open.
/// </summary>
public class ComboBoxPopupScaler : Behavior<ComboBox>
{
    private readonly Dictionary<ComboBox, IDisposable> _subscriptions = [];

    /// <summary>
    /// Small padding kept between the drop-down and the edge of the placement bounds.
    /// Window resize handles are suppressed while any drop-down is open, so this does not
    /// need to reserve space for borderless resize borders.
    /// </summary>
    public double EdgePadding { get; set; } = 4d;

    /// <summary>
    /// Minimum dropdown height when space is tight.
    /// </summary>
    public double MinDropDownHeight { get; set; } = 280d;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject == null) return;
        AssociatedObject.AttachedToVisualTree += OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree += OnDetachedFromVisualTree;
        if (AssociatedObject.IsAttachedToVisualTree())
            TryAttachPopup();
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        DetachPopup();
        if (AssociatedObject == null) return;
        AssociatedObject.AttachedToVisualTree -= OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree -= OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => TryAttachPopup();

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => DetachPopup();

    private void TryAttachPopup()
    {
        if (AssociatedObject == null) return;
        StartListening(AssociatedObject);
    }

    private void DetachPopup()
    {
        if (AssociatedObject == null) return;
        StopListening(AssociatedObject);
    }

    private void StartListening(ComboBox combo)
    {
        if (_subscriptions.ContainsKey(combo)) return;

        var subscription = combo.GetObservable(ComboBox.IsDropDownOpenProperty)
            .Subscribe(new SimpleObserver<bool>(isOpen =>
            {
                if (isOpen)
                {
                    ComboBoxDropDownOpenTracker.NotifyOpened();
                    Dispatcher.UIThread.Post(() => AdjustPopup(combo), DispatcherPriority.Background);
                    Dispatcher.UIThread.Post(() => AdjustPopup(combo), DispatcherPriority.Loaded);
                    return;
                }

                ComboBoxDropDownOpenTracker.NotifyClosed();
            }));

        _subscriptions[combo] = subscription;
    }

    private void StopListening(ComboBox combo)
    {
        if (_subscriptions.TryGetValue(combo, out var subscription))
        {
            subscription.Dispose();
            _subscriptions.Remove(combo);
        }

        if (combo.IsDropDownOpen)
            ComboBoxDropDownOpenTracker.NotifyClosed();
    }

    private void AdjustPopup(ComboBox combo)
    {
        if (!combo.IsDropDownOpen)
            return;

        if (combo.FindAncestorOfType<ScalableDecorator>() != null)
            AdjustPopupWidth(combo);

        AdjustPopupMaxHeight(combo);
    }

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

        if (popup.Child is Control child)
        {
            if (child.DesiredSize.Width > currentWidth)
                currentWidth = child.DesiredSize.Width;

            child.MinWidth = currentWidth;
            child.Width = currentWidth;
        }

        popup.Width = currentWidth;
    }

    private void AdjustPopupMaxHeight(ComboBox combo)
    {
        var topLevel = TopLevel.GetTopLevel(combo);
        if (topLevel == null)
            return;

        var topLeft = combo.TranslatePoint(new Point(0, 0), topLevel);
        if (topLeft == null)
            return;

        var comboHeight = combo.Bounds.Height;
        if (comboHeight <= 0)
            comboHeight = combo.DesiredSize.Height;
        if (comboHeight <= 0)
            comboHeight = 32d;

        var placementBounds = GetDropDownPlacementBounds(combo, topLevel);
        if (placementBounds.Width <= 0 || placementBounds.Height <= 0)
            return;

        var padding = Math.Max(0d, EdgePadding);
        var comboBottom = topLeft.Value.Y + comboHeight;
        var spaceBelow = placementBounds.Bottom - comboBottom - padding;
        var spaceAbove = topLeft.Value.Y - placementBounds.Top - padding;

        var maxHeight = Math.Max(MinDropDownHeight, spaceBelow);
        if (spaceBelow < spaceAbove)
            maxHeight = Math.Max(MinDropDownHeight, spaceAbove);

        combo.MaxDropDownHeight = maxHeight;

        var popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is Control child)
        {
            child.MaxHeight = maxHeight;
            var scrollViewer = child.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer != null)
                scrollViewer.MaxHeight = maxHeight;
        }
    }

    private static Rect GetDropDownPlacementBounds(ComboBox combo, TopLevel topLevel)
    {
        var topLevelBounds = topLevel.Bounds;
        if (topLevelBounds.Width <= 0 || topLevelBounds.Height <= 0)
            return default;

        var bounds = new Rect(0, 0, topLevelBounds.Width, topLevelBounds.Height);

        var scrollViewer = combo.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer == null)
            return bounds;

        var topLeft = scrollViewer.TranslatePoint(new Point(0, 0), topLevel);
        if (topLeft == null)
            return bounds;

        var viewportWidth = scrollViewer.Viewport.Width > 0 ? scrollViewer.Viewport.Width : scrollViewer.Bounds.Width;
        var viewportHeight = scrollViewer.Viewport.Height > 0 ? scrollViewer.Viewport.Height : scrollViewer.Bounds.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return bounds;

        var bottomRight = scrollViewer.TranslatePoint(new Point(viewportWidth, viewportHeight), topLevel);
        if (bottomRight == null)
            return bounds;

        var scrollBounds = new Rect(topLeft.Value, bottomRight.Value);
        return scrollBounds.Width > 0 && scrollBounds.Height > 40 ? scrollBounds : bounds;
    }
}
