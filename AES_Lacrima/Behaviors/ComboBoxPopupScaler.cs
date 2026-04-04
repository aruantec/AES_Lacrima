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

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryAttachPopup();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachPopup();
    }

    private void TryAttachPopup()
    {
        if (AssociatedObject == null) return;
        if (AssociatedObject.FindAncestorOfType<ScalableDecorator>() == null) return;
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
                if (isOpen)
                {
                    Dispatcher.UIThread.Post(() => AdjustPopupWidth(combo), DispatcherPriority.Background);
                }
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

    private sealed class PopupOpenObserver : IObserver<bool>
    {
        private readonly Action<bool> _onNext;

        public PopupOpenObserver(Action<bool> onNext) => _onNext = onNext;

        public void OnNext(bool value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
