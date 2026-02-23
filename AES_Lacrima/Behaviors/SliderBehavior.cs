using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Xaml.Interactivity;
using System;
using System.Windows.Input;

namespace AES_Lacrima.Behaviors
{
    /// <summary>
    /// Behavior to execute an ICommand when the user changes a Slider value.
    /// By default the command is executed when the pointer is released (end of drag).
    /// Set <see cref="ExecuteDuringDrag"/> to true to invoke the command on every value change.
    /// If <see cref="CommandParameter"/> is not set, the Slider's current Value is passed to the command.
    /// </summary>
    public class SliderBehavior : Behavior<Slider>
    {
        public static readonly StyledProperty<ICommand?> CommandProperty =
            AvaloniaProperty.Register<SliderBehavior, ICommand?>(nameof(Command));

        public static readonly StyledProperty<object?> CommandParameterProperty =
            AvaloniaProperty.Register<SliderBehavior, object?>(nameof(CommandParameter));

        // BoundPosition is the value coming from the player (e.g. AudioPlayer.Position).
        // Bind this to the player's position property. The behavior will monitor changes
        // to this property and update the Slider.Value when the user is not dragging.
        public static readonly StyledProperty<double> BoundPositionProperty =
            AvaloniaProperty.Register<SliderBehavior, double>(nameof(BoundPosition));

        public static readonly StyledProperty<bool> ExecuteDuringDragProperty =
            AvaloniaProperty.Register<SliderBehavior, bool>(nameof(ExecuteDuringDrag), false);

        public ICommand? Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public object? CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        /// <summary>
        /// Current player position (in seconds). Bind this to the player's Position property.
        /// </summary>
        public double BoundPosition
        {
            get => GetValue(BoundPositionProperty);
            set => SetValue(BoundPositionProperty, value);
        }

        /// <summary>
        /// When true, the command will be executed for every value change. Otherwise it runs on PointerReleased / Enter key.
        /// </summary>
        public bool ExecuteDuringDrag
        {
            get => GetValue(ExecuteDuringDragProperty);
            set => SetValue(ExecuteDuringDragProperty, value);
        }

        private IDisposable? _valueSubscription;
        private IDisposable? _boundPositionSubscription;
        private bool _isDragging;
        // Guard to avoid reacting to programmatic updates coming from BoundPosition.
        private bool _isUpdatingFromBound;

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject == null) return;

            // Subscribe to pointer released (end of drag)
            AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
            // Pointer pressed starts dragging
            AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
            // KeyUp to allow keyboard seeking (Enter)
            AssociatedObject.AddHandler(InputElement.KeyUpEvent, OnKeyUp, handledEventsToo: true);

            // Subscribe to value changes (use SimpleObserver helper to support IObserver<T>)
            _valueSubscription = AssociatedObject.GetObservable(Slider.ValueProperty).Subscribe(new AES_Controls.Helpers.SimpleObserver<double>(OnSliderValueChanged));

            // Monitor the bound player position. When not dragging, update the slider value to follow the player.
            _boundPositionSubscription = this.GetObservable(BoundPositionProperty).Subscribe(new AES_Controls.Helpers.SimpleObserver<double>(OnBoundPositionChanged));
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject != null)
            {
                AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
                AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                AssociatedObject.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            }
            _valueSubscription?.Dispose();
            _valueSubscription = null;
            _boundPositionSubscription?.Dispose();
            _boundPositionSubscription = null;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // End of drag: always execute with current slider value so the player seeks to final position.
            if (_isDragging)
            {
                _isDragging = false;
                ExecuteWithSliderValue();
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Start drag: stop updating slider.Value from the bound position until released.
            _isDragging = true;
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteWithSliderValue();
            }
        }

        private void OnSliderValueChanged(double value)
        {
            // Ignore changes we made when updating from the bound position
            if (_isUpdatingFromBound)
                return;

            // If the user is dragging and ExecuteDuringDrag is enabled, execute continuously.
            if (_isDragging && ExecuteDuringDrag)
            {
                ExecuteWithSliderValue();
            }
        }

        private void ExecuteWithSliderValue()
        {
            if (AssociatedObject == null) return;
            var parameter = CommandParameter ?? AssociatedObject.Value;
            if (Command != null && Command.CanExecute(parameter))
            {
                Command.Execute(parameter);
            }
        }

        private void OnBoundPositionChanged(double pos)
        {
            if (AssociatedObject == null) return;

            // Don't update the slider while the user is dragging.
            if (_isDragging) return;

            try
            {
                _isUpdatingFromBound = true;
                // Clamp to slider range to avoid exceptions
                if (pos < AssociatedObject.Minimum) pos = AssociatedObject.Minimum;
                if (pos > AssociatedObject.Maximum) pos = AssociatedObject.Maximum;
                AssociatedObject.Value = pos;
            }
            finally
            {
                _isUpdatingFromBound = false;
            }
        }
    }
}
