using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using SkiaSharp;
using System.Numerics;
using System.Windows.Input;

namespace AES_Controls.Composition
{
    public class CompositionSlider : UserControl
    {
        public CompositionSlider()
        {
            ClipToBounds = false;
            Background = Brushes.Transparent;
            Focusable = true;
        }

        private SKColor ConvertBrushToSkColor(IBrush? brush)
        {
            try
            {
                if (brush is SolidColorBrush scb)
                {
                    var c = scb.Color;
                    return new SKColor(c.R, c.G, c.B, c.A);
                }
                // Some brush implementations (immutable variants) may not be SolidColorBrush
                // but still expose a Color property. Try to read it via reflection as a fallback.
                if (brush != null)
                {
                    var t = brush.GetType();
                    var prop = t.GetProperty("Color");
                    if (prop != null && prop.PropertyType == typeof(Avalonia.Media.Color))
                    {
                        var val = (Avalonia.Media.Color)prop.GetValue(brush)!;
                        return new SKColor(val.R, val.G, val.B, val.A);
                    }
                }
            }
            catch { }
            return SKColors.Transparent;
        }

        

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<CompositionSlider, double>(nameof(Value));

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<CompositionSlider, double>(nameof(Minimum), 0.0);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<CompositionSlider, double>(nameof(Maximum), 1.0);

        public static readonly StyledProperty<double> SliderVerticalOffsetProperty =
            AvaloniaProperty.Register<CompositionSlider, double>(nameof(SliderVerticalOffset), 60.0);

        public static readonly StyledProperty<double> SliderTrackHeightProperty =
            AvaloniaProperty.Register<CompositionSlider, double>(nameof(SliderTrackHeight), 4.0);

        // When true, the visual should render a smaller thumb (half the standard width)
        public static readonly StyledProperty<bool> SmallThumbProperty =
            AvaloniaProperty.Register<CompositionSlider, bool>(nameof(SmallThumb), false);

        // Brush used to color the played portion of the track (0..Value). Default is transparent.
        public static readonly StyledProperty<IBrush?> PlayedAreaBrushProperty =
            AvaloniaProperty.Register<CompositionSlider, IBrush?>(nameof(PlayedAreaBrush), Brushes.Transparent);

        public static readonly StyledProperty<ICommand?> SetValueCommandProperty =
            AvaloniaProperty.Register<CompositionSlider, ICommand?>(nameof(SetValueCommand));

        public static readonly StyledProperty<bool> ExecuteDuringDragProperty =
            AvaloniaProperty.Register<CompositionSlider, bool>(nameof(ExecuteDuringDrag), false);

        private CompositionCustomVisual? _visual;
        private bool _isSliderPressed;
        // Guard to distinguish programmatic updates from user dragging updates
        private bool _isUpdatingFromSlider;
        // Suppress external updates until this time (UTC) to avoid jump-back after a seek
        private DateTime _suppressExternalUpdatesUntil = DateTime.MinValue;
        // Track whether we already executed the seek on press to avoid double-executing on release
        private bool _didExecuteSeekOnPress;
        // Track whether we executed seek during drag (first move) or continuously
        private bool _didExecuteSeekDuringDrag;

        public double Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double SliderVerticalOffset
        {
            get => GetValue(SliderVerticalOffsetProperty);
            set => SetValue(SliderVerticalOffsetProperty, value);
        }

        public double SliderTrackHeight
        {
            get => GetValue(SliderTrackHeightProperty);
            set => SetValue(SliderTrackHeightProperty, value);
        }

        public ICommand? SetValueCommand
        {
            get => GetValue(SetValueCommandProperty);
            set => SetValue(SetValueCommandProperty, value);
        }

        public IBrush? PlayedAreaBrush
        {
            get => GetValue(PlayedAreaBrushProperty);
            set => SetValue(PlayedAreaBrushProperty, value);
        }

        public bool SmallThumb
        {
            get => GetValue(SmallThumbProperty);
            set => SetValue(SmallThumbProperty, value);
        }

        public bool ExecuteDuringDrag
        {
            get => GetValue(ExecuteDuringDragProperty);
            set => SetValue(ExecuteDuringDragProperty, value);
        }

        private Rect SliderBounds
        {
            get
            {
                double w = Bounds.Width;
                double h = Bounds.Height;
                // match visual handler: small horizontal padding (2% clamped) and natural slider height
                double horizPadding = Math.Clamp(w * 0.02, 2.0, 16.0);
                double sliderW = Math.Max(0.0, w - horizPadding * 2.0);
                // Fill available vertical space as much as possible while keeping a minimum
                double sliderH = Math.Max(12.0, h - 2.0);
                double top = Math.Max(0.0, (h - sliderH) / 2.0);
                return new Rect(horizPadding, top, sliderW, sliderH);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor != null)
            {
                // Use a dedicated slider visual handler so the slider fills its own bounds
                _visual = compositor.CreateCustomVisual(new CompositionSliderVisualHandler());
                ElementComposition.SetElementChildVisual(this, _visual);
                var logicalSize = new Vector2((float)Bounds.Width, (float)Bounds.Height);
                _visual.Size = logicalSize;
                _visual.SendHandlerMessage(logicalSize);

                // Send a small placeholder images list so the carousel handler draws the slider.
                var placeholders = new List<SKImage> { CreatePlaceholder(), CreatePlaceholder() };
                _visual.SendHandlerMessage(placeholders);

                // Forward configuration
                _visual.SendHandlerMessage(new SliderVerticalOffsetMessage(SliderVerticalOffset));
                _visual.SendHandlerMessage(new SliderTrackHeightMessage(SliderTrackHeight));
                _visual.SendHandlerMessage(new SliderSmallThumbMessage(SmallThumb));
                // send played area brush color to visual
                var skColor = ConvertBrushToSkColor(PlayedAreaBrush);
                _visual.SendHandlerMessage(new PlayedAreaBrushMessage(skColor));
                // send normalized value (0..1) as an instant position so the thumb
                // doesn't animate from 0 to current value when the control is attached
                // (for example when a flyout opens).
                _visual.SendHandlerMessage(new InstantSliderPositionMessage(NormalizeValue(Value)));
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _visual = null;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_visual != null)
            {
                var logicalSize = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                _visual.Size = logicalSize;
                _visual.SendHandlerMessage(logicalSize);
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (_visual == null) return;

            if (change.Property == ValueProperty)
            {
                // While the user is actively dragging, ignore external updates.
                // Also ignore external updates for a short suppression window after a seek.
                if (_isSliderPressed || DateTime.UtcNow <= _suppressExternalUpdatesUntil)
                    return;

                _visual.SendHandlerMessage(NormalizeValue(change.GetNewValue<double>()));
            }
            else if (change.Property == SliderVerticalOffsetProperty)
            {
                _visual.SendHandlerMessage(new SliderVerticalOffsetMessage(change.GetNewValue<double>()));
            }
            else if (change.Property == SliderTrackHeightProperty)
            {
                _visual.SendHandlerMessage(new SliderTrackHeightMessage(change.GetNewValue<double>()));
            }
            else if (change.Property == SmallThumbProperty)
            {
                _visual.SendHandlerMessage(new SliderSmallThumbMessage(change.GetNewValue<bool>()));
            }
            else if (change.Property == PlayedAreaBrushProperty)
            {
                var skColor = ConvertBrushToSkColor(change.GetNewValue<IBrush?>());
                _visual.SendHandlerMessage(new PlayedAreaBrushMessage(skColor));
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pos = e.GetPosition(this);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // Inflate the slider hit area similar to the carousel control for easier interaction
                var bounds = SliderBounds;
                // compute thumb size to match visual handler proportions
                var sliderH = bounds.Height;
                var thumbH = Math.Max(6.0, sliderH * 0.6);
                var thumbW = Math.Max(12.0, thumbH * 2.0 * 1.3);
                if (SmallThumb) thumbW *= 0.5;
                var inflated = new Rect(bounds.Left - thumbW, bounds.Top - thumbH, bounds.Width + thumbW * 2, bounds.Height + thumbH * 2);
                if (inflated.Contains(pos))
                {
                    // take focus so keyboard events etc. route correctly
                    Focus();
                    _isSliderPressed = true;
                    e.Pointer.Capture(this);
                    _visual?.SendHandlerMessage(new SliderPressedMessage(true));
                    UpdateSliderPosition(pos.X);
                    // Reset drag-executed flag for new interaction
                    _didExecuteSeekDuringDrag = false;
                    // If this is a simple click (no drag yet) execute the seek immediately so a single
                    // click updates the player. Mark that we executed so release doesn't double-run.
                    _didExecuteSeekOnPress = false;
                    if (e.ClickCount == 1 && SetValueCommand != null)
                    {
                        var v = Value;
                        if (SetValueCommand.CanExecute(v))
                        {
                            SetValueCommand.Execute(v);
                            _didExecuteSeekOnPress = true;
                            // suppress external updates briefly to avoid jump-back
                            _suppressExternalUpdatesUntil = DateTime.UtcNow.AddMilliseconds(800);
                        }
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_isSliderPressed) return;
            var pos = e.GetPosition(this);
            UpdateSliderPosition(pos.X);
            // Execute a seek on first drag movement so dragging immediately updates the player
            if (!_didExecuteSeekDuringDrag && SetValueCommand != null)
            {
                var v = Value;
                if (SetValueCommand.CanExecute(v))
                {
                    SetValueCommand.Execute(v);
                    _didExecuteSeekDuringDrag = true;
                    _suppressExternalUpdatesUntil = DateTime.UtcNow.AddMilliseconds(800);
                }
            }
            else if (ExecuteDuringDrag && SetValueCommand != null)
            {
                // If continuous seeks during drag are enabled, execute on every move.
                var v = Value;
                if (SetValueCommand.CanExecute(v)) SetValueCommand.Execute(v);
            }
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isSliderPressed)
            {
                _isSliderPressed = false;
                e.Pointer.Capture(null);
                _visual?.SendHandlerMessage(new SliderPressedMessage(false));
                // Execute final command with current value when drag ends
                if (SetValueCommand != null)
                {
                    var v = Value;
                    if (!_didExecuteSeekOnPress && !_didExecuteSeekDuringDrag && SetValueCommand.CanExecute(v)) SetValueCommand.Execute(v);
                }
                // Ensure the visual reflects the final position instantly
                _visual?.SendHandlerMessage(new InstantSliderPositionMessage(NormalizeValue(Value)));

                // Suppress external updates for a short time to avoid jump-back while the
                // audio player processes the seek and reports its new position.
                _suppressExternalUpdatesUntil = DateTime.UtcNow.AddMilliseconds(800);
                e.Handled = true;
            }
        }

        private void UpdateSliderPosition(double x)
        {
            var bounds = SliderBounds;
            // compute thumb size to match visual handler proportions
            double sliderH = bounds.Height;
            double thumbH = Math.Max(6.0, sliderH * 0.6);
            // match visual handler: make thumb ~30% wider
            double thumbW = Math.Max(12.0, thumbH * 2.0 * 1.3);
            if (SmallThumb) thumbW *= 0.5;
            double clickableWidth = Math.Max(1.0, bounds.Width - thumbW);
            double pct = Math.Clamp((x - bounds.Left - thumbW / 2.0) / clickableWidth, 0.0, 1.0);
            // Map pct to Value range
            double min = Minimum;
            double max = Maximum;
            double newVal = min + pct * Math.Max(0, max - min);
            // Send instant update to visual so thumb moves immediately without smoothing
            _visual?.SendHandlerMessage(new InstantSliderPositionMessage(pct));
            // Assign Value (will notify visual). Mark as originating from slider so
            // OnPropertyChanged can distinguish external updates during drag.
            _isUpdatingFromSlider = true;
            try
            {
                Value = newVal;
            }
            finally
            {
                _isUpdatingFromSlider = false;
            }
        }

        private double NormalizeValue(double val)
        {
            double min = Minimum;
            double max = Maximum;
            if (max <= min) return 0.0;
            return Math.Clamp((val - min) / (max - min), 0.0, 1.0);
        }

        private SKImage CreatePlaceholder()
        {
            using var surface = SKSurface.Create(new SKImageInfo(2, 2));
            surface.Canvas.Clear(SKColors.Transparent);
            return surface.Snapshot();
        }
    }
}
