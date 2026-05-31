using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AES_Controls.Helpers;

namespace AES_Controls;

internal static class ScaleExclusion
{
    private static readonly AttachedProperty<ScaleExclusionState?> StateProperty =
        AvaloniaProperty.RegisterAttached<Visual, ScaleExclusionState?>("ScaleExclusionState", typeof(ScalableDecorator));

    internal static void Attach(Visual visual)
    {
        Detach(visual);
        var state = new ScaleExclusionState(visual);
        visual.SetValue(StateProperty, state);
        visual.AttachedToVisualTree += OnVisualAttachedToVisualTree;
        visual.DetachedFromVisualTree += OnVisualDetachedFromVisualTree;

        if (visual.IsAttachedToVisualTree())
            state.BindAndApply();
    }

    internal static void Detach(Visual visual)
    {
        var state = visual.GetValue(StateProperty);
        if (state == null)
            return;

        visual.AttachedToVisualTree -= OnVisualAttachedToVisualTree;
        visual.DetachedFromVisualTree -= OnVisualDetachedFromVisualTree;
        state.Dispose();
        visual.SetValue(StateProperty, null);
    }

    internal static void RefreshIfAttached(Visual visual) =>
        visual.GetValue(StateProperty)?.ApplyNow();

    private static void OnVisualAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual)
            visual.GetValue(StateProperty)?.BindAndApply();
    }

    private static void OnVisualDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual)
            visual.GetValue(StateProperty)?.Unbind();
    }

    private sealed class ScaleExclusionState : IDisposable
    {
        private readonly Visual _visual;
        private readonly ScaleTransform _compensation = new(1, 1);
        private readonly List<IDisposable> _subscriptions = [];
        private ScalableDecorator? _decorator;
        private TransformGroup? _transformGroup;
        private Transform? _savedTransform;
        private double _appliedScale = 1.0;

        public ScaleExclusionState(Visual visual) => _visual = visual;

        public void BindAndApply()
        {
            Unbind();
            _decorator = _visual.FindAncestorOfType<ScalableDecorator>();
            if (_decorator == null)
            {
                RestoreTransform();
                RefreshRenderTargets();
                return;
            }

            _subscriptions.Add(_decorator.GetObservable(ScalableDecorator.ScaleProperty)
                .Subscribe(new SimpleObserver<double>(_ => Apply())));
            _subscriptions.Add(_decorator.GetObservable(ScalableDecorator.EfficientScalingProperty)
                .Subscribe(new SimpleObserver<bool>(_ => Apply())));
            _subscriptions.Add(_visual.GetObservable(ScalableDecorator.ExcludeFromScaleCompensationProperty)
                .Subscribe(new SimpleObserver<bool>(_ => Apply())));
            _subscriptions.Add(_decorator.GetObservable(Visual.BoundsProperty)
                .Subscribe(new SimpleObserver<Rect>(_ => Apply())));

            var topLevel = TopLevel.GetTopLevel(_decorator);
            if (topLevel != null)
            {
                _subscriptions.Add(topLevel.GetObservable(TopLevel.ClientSizeProperty)
                    .Subscribe(new SimpleObserver<Size>(_ => Apply())));
            }

            Apply();
        }

        public void Unbind()
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            _decorator = null;
        }

        public void ApplyNow() => Apply();

        private void Apply()
        {
            var scale = _decorator?.GetContentRenderScale() ?? 1.0;
            if (scale <= 1.001 || !ScalableDecorator.GetExcludeFromScaleCompensation(_visual))
            {
                RestoreTransform();
                RefreshRenderTargets();
                return;
            }

            if (Math.Abs(_appliedScale - scale) < 0.001 && _transformGroup != null)
            {
                RefreshRenderTargets();
                return;
            }

            _appliedScale = scale;
            _compensation.ScaleX = scale;
            _compensation.ScaleY = scale;

            if (_transformGroup == null)
            {
                _savedTransform = _visual.RenderTransform as Transform;
                _visual.RenderTransformOrigin = RelativePoint.TopLeft;
                _transformGroup = new TransformGroup();
                if (_savedTransform != null)
                    _transformGroup.Children.Add(_savedTransform);
                _transformGroup.Children.Add(_compensation);
                _visual.RenderTransform = _transformGroup;
            }

            RefreshRenderTargets();
        }

        private void RestoreTransform()
        {
            if (_transformGroup == null)
                return;

            _visual.RenderTransform = _savedTransform;
            _transformGroup = null;
            _savedTransform = null;
            _appliedScale = 1.0;
            _compensation.ScaleX = 1.0;
            _compensation.ScaleY = 1.0;
            RefreshRenderTargets();
        }

        private void RefreshRenderTargets()
        {
            RefreshRenderTargetsRecursive(_visual);
        }

        private static void RefreshRenderTargetsRecursive(Visual visual)
        {
            if (visual is IScaleExclusionRenderTarget renderTarget)
                renderTarget.RefreshExclusionRenderSize();

            foreach (var child in visual.GetVisualChildren())
            {
                if (child is Visual childVisual)
                    RefreshRenderTargetsRecursive(childVisual);
            }
        }

        public void Dispose()
        {
            Unbind();
            RestoreTransform();
        }
    }
}
