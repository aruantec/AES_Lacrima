using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Linq;

namespace AES_Controls.Behaviors
{
    /// <summary>
    /// Provides an attached behavior for sliding animation of controls.
    /// </summary>
    public static class SlideBehavior
    {
        private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(450);

        /// <summary>
        /// Defines the IsOpen attached property.
        /// </summary>
        public static readonly AttachedProperty<bool?> IsOpenProperty =
            AvaloniaProperty.RegisterAttached<Control, bool?>("IsOpen", typeof(SlideBehavior), defaultValue: null);

        private static readonly AttachedProperty<bool> HasLifecycleHooksProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("SlideBehaviorHasLifecycleHooks", typeof(SlideBehavior));

        /// <summary>
        /// Gets the value of the IsOpen attached property.
        /// </summary>
        public static bool GetIsOpen(Control element) => element.GetValue(IsOpenProperty) ?? false;

        /// <summary>
        /// Sets the value of the IsOpen attached property.
        /// </summary>
        public static void SetIsOpen(Control element, bool value) => element.SetValue(IsOpenProperty, value);

        static SlideBehavior()
        {
            IsOpenProperty.Changed.AddClassHandler<Control>(OnIsOpenChanged);
        }

        private static void OnIsOpenChanged(Control control, AvaloniaPropertyChangedEventArgs e)
        {
            EnsureLifecycleHooks(control);

            if (e.NewValue is not bool isOpen)
            {
                if (e.NewValue is null)
                    ApplyState(control, isOpen: false, animate: false);
                return;
            }

            var animate = e.OldValue is bool oldValue && oldValue != isOpen;
            ApplyState(control, isOpen, animate);
        }

        private static void EnsureLifecycleHooks(Control control)
        {
            if (control.GetValue(HasLifecycleHooksProperty))
                return;

            control.SetValue(HasLifecycleHooksProperty, true);
            control.AttachedToVisualTree += OnControlAttachedToVisualTree;
        }

        private static void OnControlAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is not Control control)
                return;

            ApplyState(control, GetIsOpen(control), animate: false);
        }

        private static TranslateTransform EnsureTransform(Control control)
        {
            if (control.RenderTransform is TranslateTransform existing &&
                existing.Transitions?.Any(t => t is DoubleTransition dt && dt.Property == TranslateTransform.XProperty) == true)
            {
                control.RenderTransformOrigin = new RelativePoint(1, 0.5, RelativeUnit.Relative);
                return existing;
            }

            var transform = new TranslateTransform
            {
                Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = TranslateTransform.XProperty,
                        Duration = SlideDuration,
                        Easing = new CubicEaseOut()
                    }
                }
            };

            control.RenderTransformOrigin = new RelativePoint(1, 0.5, RelativeUnit.Relative);
            control.RenderTransform = transform;
            return transform;
        }

        private static void EnsureOpacityTransition(Control control)
        {
            control.Transitions ??= new Transitions();

            if (control.Transitions.Any(t => t is DoubleTransition dt && dt.Property == Visual.OpacityProperty))
                return;

            control.Transitions.Add(new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = SlideDuration,
                Easing = new CubicEaseOut()
            });
        }

        private static void SetSlideEasing(TranslateTransform transform, bool isOpen)
        {
            if (transform.Transitions?.FirstOrDefault(t =>
                    t is DoubleTransition dt && dt.Property == TranslateTransform.XProperty) is DoubleTransition slideTransition)
            {
                slideTransition.Easing = isOpen ? new CubicEaseOut() : new CubicEaseIn();
            }
        }

        private static void SetOpacityEasing(Control control, bool isOpen)
        {
            if (control.Transitions?.FirstOrDefault(t =>
                    t is DoubleTransition dt && dt.Property == Visual.OpacityProperty) is DoubleTransition opacityTransition)
            {
                opacityTransition.Easing = isOpen ? new CubicEaseOut() : new CubicEaseIn();
            }
        }

        private static void SetOffsetWithoutTransition(TranslateTransform transform, double x)
        {
            var transitions = transform.Transitions;
            transform.Transitions = null;
            transform.X = x;
            transform.Transitions = transitions;
        }

        private static void SetOpacityWithoutTransition(Control control, double opacity)
        {
            var transitions = control.Transitions;
            control.Transitions = null;
            control.Opacity = opacity;
            control.Transitions = transitions;
        }

        private static void ApplyState(Control control, bool isOpen, bool animate)
        {
            var transform = EnsureTransform(control);
            EnsureOpacityTransition(control);

            var closedOffset = GetClosedOffset(control);

            if (!animate)
            {
                SetOffsetWithoutTransition(transform, isOpen ? 0 : closedOffset);
                SetOpacityWithoutTransition(control, isOpen ? 1 : 0);
                return;
            }

            if (isOpen)
            {
                SetSlideEasing(transform, isOpen: true);
                SetOpacityEasing(control, isOpen: true);
                SetOffsetWithoutTransition(transform, closedOffset);
                SetOpacityWithoutTransition(control, 0);

                Dispatcher.UIThread.Post(() =>
                {
                    if (!GetIsOpen(control))
                        return;

                    transform.X = 0;
                    control.Opacity = 1;
                }, DispatcherPriority.Render);

                return;
            }

            SetSlideEasing(transform, isOpen: false);
            SetOpacityEasing(control, isOpen: false);
            transform.X = closedOffset;
            control.Opacity = 0;
        }

        private static double GetClosedOffset(Control control)
        {
            var width = control.Bounds.Width;
            if (width <= 0)
                width = control.Width;
            if (double.IsNaN(width) || width <= 0)
                width = 400;

            return width;
        }
    }
}
