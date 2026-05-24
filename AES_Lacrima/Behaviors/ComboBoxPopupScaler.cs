using AES_Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Lacrima.Behaviors;

public class ComboBoxPopupScaler : Behavior<ComboBox>
{
    private readonly Dictionary<ComboBox, IDisposable> _subscriptions = new();

    public double ResizeBorderMargin { get; set; } = 16d;

    public double MinDropDownHeight { get; set; } = 120d;

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
            .Subscribe(new PopupOpenObserver(isOpen =>
            {
                if (!isOpen) return;

                Dispatcher.UIThread.Post(() => AdjustPopup(combo), DispatcherPriority.Background);
                Dispatcher.UIThread.Post(() => AdjustPopup(combo), DispatcherPriority.Loaded);
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
    }

    private void AdjustPopup(ComboBox combo)
    {
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

        var windowHeight = topLevel.Bounds.Height;
        if (windowHeight <= 0)
            return;

        var margin = Math.Max(1d, ResizeBorderMargin);
        var spaceBelow = windowHeight - topLeft.Value.Y - comboHeight - margin;
        var spaceAbove = topLeft.Value.Y - margin;

        var maxHeight = Math.Max(MinDropDownHeight, spaceBelow);
        if (spaceBelow < MinDropDownHeight && spaceAbove > spaceBelow)
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

    private sealed class PopupOpenObserver : IObserver<bool>
    {
        private readonly Action<bool> _onNext;

        public PopupOpenObserver(Action<bool> onNext) => _onNext = onNext;

        public void OnNext(bool value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
