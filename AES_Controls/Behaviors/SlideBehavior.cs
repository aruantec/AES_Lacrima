using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace AES_Controls.Behaviors
{
    /// <summary>
    /// Provides an attached behavior for sliding animation of Controls.
    /// </summary>
    public static class SlideBehavior
    {
        /// <summary>
        /// Defines the IsOpen attached property.
        /// </summary>
        public static readonly AttachedProperty<bool> IsOpenProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(SlideBehavior), defaultValue: true);

        /// <summary>
        /// Gets the value of the IsOpen attached property.
        /// </summary>
        /// <param name="element">The control to get the value for.</param>
        /// <returns>The IsOpen value.</returns>
        public static bool GetIsOpen(Control element) => element.GetValue(IsOpenProperty);

        /// <summary>
        /// Sets the value of the IsOpen attached property.
        /// </summary>
        /// <param name="element">The control to set the value for.</param>
        /// <param name="value">The value to set.</param>
        public static void SetIsOpen(Control element, bool value) => element.SetValue(IsOpenProperty, value);

        /// <summary>
        /// Initializes the <see cref="SlideBehavior"/> class.
        /// </summary>
        static SlideBehavior()
        {
            IsOpenProperty.Changed.Subscribe(new SimpleObserver<AvaloniaPropertyChangedEventArgs<bool>>(e =>
            {
                if (e.Sender is Control control)
                {
                    UpdateOffset(control, e.NewValue.Value);
                }
            }));
        }

        /// <summary>
        /// Initializes the animation state for the specified control.
        /// </summary>
        /// <param name="control">The control to initialize.</param>
        private static void InitializeAnimationState(Control control)
        {
            // Initialize the transform safely
            if (control.RenderTransform is not TranslateTransform)
            {
                // We only assign if it's null to avoid stomping on other transforms/styles
                if (control.RenderTransform == null)
                    control.RenderTransform = new TranslateTransform();
            }

            // Pre-register the transition locally
            if (control.Transitions == null)
                control.Transitions = new Transitions();

            if (!control.Transitions.Any(t => t is DoubleTransition dt && dt.Property == TranslateTransform.XProperty))
            {
                control.Transitions.Add(new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(500),
                    Easing = new CubicEaseOut()
                });
            }
        }

        /// <summary>
        /// Updates the offset of the specified control based on its open state.
        /// </summary>
        /// <param name="control">The control to update.</param>
        /// <param name="isOpen">True if the control is open; otherwise, false.</param>
        private static void UpdateOffset(Control control, bool isOpen)
        {
            // Initialize if needed
            if (control.RenderTransform is not TranslateTransform transform)
            {
                InitializeAnimationState(control);
                transform = (control.RenderTransform as TranslateTransform)!;
            }

            if (isOpen)
            {
                transform.X = 0;
            }
            else
            {
                double width = control.Bounds.Width;
                if (width <= 0) width = control.Width;
                if (double.IsNaN(width) || width <= 0) width = 1200; 

                transform.X = width + 500;
            }
        }
    }
}