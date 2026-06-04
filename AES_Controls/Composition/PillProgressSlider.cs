using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using SkiaSharp;
using System.Diagnostics;
using System.Numerics;
using System.Windows.Input;

namespace AES_Controls.Composition
{
    public class PillProgressSlider : UserControl
    {
        public PillProgressSlider()
        {
            ClipToBounds = false;
            Background = Brushes.Transparent;
            Focusable = true;
        }

        public static readonly StyledProperty<double> ValueProperty =
            AvaloniaProperty.Register<PillProgressSlider, double>(nameof(Value));

        public static readonly StyledProperty<double> MinimumProperty =
            AvaloniaProperty.Register<PillProgressSlider, double>(nameof(Minimum), 0.0);

        public static readonly StyledProperty<double> MaximumProperty =
            AvaloniaProperty.Register<PillProgressSlider, double>(nameof(Maximum), 1.0);

        public static readonly StyledProperty<double> TrackHeightProperty =
            AvaloniaProperty.Register<PillProgressSlider, double>(nameof(TrackHeight), 20.0);

        public static readonly StyledProperty<IBrush?> TrackBorderBrushProperty =
            AvaloniaProperty.Register<PillProgressSlider, IBrush?>(nameof(TrackBorderBrush), Brushes.White);

        public static readonly StyledProperty<IBrush?> FillBrushProperty =
            AvaloniaProperty.Register<PillProgressSlider, IBrush?>(nameof(FillBrush), Brushes.WhiteSmoke);

        public static readonly StyledProperty<ICommand?> SetValueCommandProperty =
            AvaloniaProperty.Register<PillProgressSlider, ICommand?>(nameof(SetValueCommand));

        public static readonly StyledProperty<bool> ExecuteDuringDragProperty =
            AvaloniaProperty.Register<PillProgressSlider, bool>(nameof(ExecuteDuringDrag), false);

        public static readonly StyledProperty<string?> LabelTextProperty =
            AvaloniaProperty.Register<PillProgressSlider, string?>(nameof(LabelText));

        public static readonly StyledProperty<double> LabelFontSizeProperty =
            AvaloniaProperty.Register<PillProgressSlider, double>(nameof(LabelFontSize), 0.0);

        public static readonly StyledProperty<IBrush?> LabelForegroundBrushProperty =
            AvaloniaProperty.Register<PillProgressSlider, IBrush?>(nameof(LabelForegroundBrush), new SolidColorBrush(Color.Parse("#E6FFFFFF")));

        public static readonly StyledProperty<IBrush?> LabelOverFillForegroundBrushProperty =
            AvaloniaProperty.Register<PillProgressSlider, IBrush?>(nameof(LabelOverFillForegroundBrush), new SolidColorBrush(Color.Parse("#1A1A1A")));

        public static readonly StyledProperty<bool> ShowPositionLabelProperty =
            AvaloniaProperty.Register<PillProgressSlider, bool>(nameof(ShowPositionLabel), false);

        private CompositionCustomVisual? _visual;
        private bool _isPressed;
        private DateTime _suppressExternalUpdatesUntil = DateTime.MinValue;
        private bool _didExecuteSeekOnPress;
        private bool _didExecuteSeekDuringDrag;
        private long _lastDragSeekTicks;
        private static readonly long DragSeekThrottleTicks = Stopwatch.Frequency / 12;

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

        public double TrackHeight
        {
            get => GetValue(TrackHeightProperty);
            set => SetValue(TrackHeightProperty, value);
        }

        public IBrush? TrackBorderBrush
        {
            get => GetValue(TrackBorderBrushProperty);
            set => SetValue(TrackBorderBrushProperty, value);
        }

        public IBrush? FillBrush
        {
            get => GetValue(FillBrushProperty);
            set => SetValue(FillBrushProperty, value);
        }

        public ICommand? SetValueCommand
        {
            get => GetValue(SetValueCommandProperty);
            set => SetValue(SetValueCommandProperty, value);
        }

        public bool ExecuteDuringDrag
        {
            get => GetValue(ExecuteDuringDragProperty);
            set => SetValue(ExecuteDuringDragProperty, value);
        }

        public string? LabelText
        {
            get => GetValue(LabelTextProperty);
            set => SetValue(LabelTextProperty, value);
        }

        public double LabelFontSize
        {
            get => GetValue(LabelFontSizeProperty);
            set => SetValue(LabelFontSizeProperty, value);
        }

        public IBrush? LabelForegroundBrush
        {
            get => GetValue(LabelForegroundBrushProperty);
            set => SetValue(LabelForegroundBrushProperty, value);
        }

        public IBrush? LabelOverFillForegroundBrush
        {
            get => GetValue(LabelOverFillForegroundBrushProperty);
            set => SetValue(LabelOverFillForegroundBrushProperty, value);
        }

        public bool ShowPositionLabel
        {
            get => GetValue(ShowPositionLabelProperty);
            set => SetValue(ShowPositionLabelProperty, value);
        }

        private Rect TrackBounds
        {
            get
            {
                const double horizPadding = 2.0;
                double trackH = Math.Clamp(TrackHeight, 8.0, Math.Max(8.0, Bounds.Height - 4.0));
                double top = Math.Max(0.0, (Bounds.Height - trackH) / 2.0);
                return new Rect(horizPadding, top, Math.Max(0.0, Bounds.Width - horizPadding * 2.0), trackH);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor == null)
                return;

            _visual = compositor.CreateCustomVisual(new PillProgressSliderVisualHandler());
            ElementComposition.SetElementChildVisual(this, _visual);

            var logicalSize = new Vector2((float)Bounds.Width, (float)Bounds.Height);
            _visual.Size = logicalSize;
            SendVisualConfiguration();
            _visual.SendHandlerMessage(new InstantSliderPositionMessage(NormalizeValue(Value)));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _visual = null;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_visual == null)
                return;

            var logicalSize = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            _visual.Size = logicalSize;
            _visual.SendHandlerMessage(logicalSize);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (_visual == null)
                return;

            if (change.Property == ValueProperty)
            {
                if (_isPressed || DateTime.UtcNow <= _suppressExternalUpdatesUntil)
                    return;

                _visual.SendHandlerMessage(NormalizeValue(change.GetNewValue<double>()));
            }
            else if (change.Property == TrackHeightProperty
                     || change.Property == TrackBorderBrushProperty
                     || change.Property == FillBrushProperty
                     || change.Property == LabelTextProperty
                     || change.Property == LabelFontSizeProperty
                     || change.Property == LabelForegroundBrushProperty
                     || change.Property == LabelOverFillForegroundBrushProperty
                     || change.Property == ShowPositionLabelProperty)
            {
                SendVisualConfiguration();
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var pos = e.GetPosition(this);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var bounds = TrackBounds;
            var verticalPad = Math.Max(8.0, bounds.Height);
            var hit = new Rect(bounds.X, bounds.Y - verticalPad, bounds.Width, bounds.Height + verticalPad * 2);
            if (!hit.Contains(pos))
                return;

            Focus();
            _isPressed = true;
            e.Pointer.Capture(this);
            UpdateSliderPosition(pos.X);
            _didExecuteSeekDuringDrag = false;
            _lastDragSeekTicks = 0;
            _didExecuteSeekOnPress = false;

            if (SetValueCommand?.CanExecute(Value) == true)
            {
                SetValueCommand.Execute(Value);
                _didExecuteSeekOnPress = true;
                _suppressExternalUpdatesUntil = DateTime.UtcNow.AddMilliseconds(800);
            }

            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_isPressed)
                return;

            UpdateSliderPosition(e.GetPosition(this).X);

            if (ExecuteDuringDrag && SetValueCommand != null)
            {
                var now = Stopwatch.GetTimestamp();
                if (now - _lastDragSeekTicks >= DragSeekThrottleTicks && SetValueCommand.CanExecute(Value))
                {
                    SetValueCommand.Execute(Value);
                    _didExecuteSeekDuringDrag = true;
                    _lastDragSeekTicks = now;
                }
            }

            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isPressed)
                return;

            _isPressed = false;
            e.Pointer.Capture(null);

            if (SetValueCommand != null
                && (!_didExecuteSeekOnPress || _didExecuteSeekDuringDrag)
                && SetValueCommand.CanExecute(Value))
            {
                SetValueCommand.Execute(Value);
            }

            _visual?.SendHandlerMessage(new InstantSliderPositionMessage(NormalizeValue(Value)));
            _suppressExternalUpdatesUntil = DateTime.UtcNow.AddMilliseconds(800);
            e.Handled = true;
        }

        private void SendVisualConfiguration()
        {
            if (_visual == null)
                return;

            _visual.SendHandlerMessage(new Vector2((float)Bounds.Width, (float)Bounds.Height));
            _visual.SendHandlerMessage(new PillTrackHeightMessage((float)TrackHeight));
            _visual.SendHandlerMessage(new PillBorderColorMessage(ConvertBrushToSkColor(TrackBorderBrush, SKColors.White)));
            _visual.SendHandlerMessage(new PillFillColorMessage(ConvertBrushToSkColor(FillBrush, SKColor.Parse("#F5F5F5"))));
            _visual.SendHandlerMessage(new PillLabelTextMessage(LabelText ?? string.Empty));
            _visual.SendHandlerMessage(new PillLabelFontSizeMessage((float)LabelFontSize));
            _visual.SendHandlerMessage(new PillLabelColorMessage(ConvertBrushToSkColor(LabelForegroundBrush, SKColor.Parse("#E6FFFFFF"))));
            _visual.SendHandlerMessage(new PillLabelOverFillColorMessage(ConvertBrushToSkColor(LabelOverFillForegroundBrush, SKColor.Parse("#1A1A1A"))));
            _visual.SendHandlerMessage(new PillShowPositionLabelMessage(ShowPositionLabel));
        }

        private void UpdateSliderPosition(double x)
        {
            var bounds = TrackBounds;
            double pct = Math.Clamp((x - bounds.Left) / Math.Max(1.0, bounds.Width), 0.0, 1.0);
            _visual?.SendHandlerMessage(new InstantSliderPositionMessage(pct));

            double min = Minimum;
            double max = Maximum;
            Value = min + pct * Math.Max(0, max - min);
        }

        private double NormalizeValue(double val)
        {
            double min = Minimum;
            double max = Maximum;
            if (max <= min)
                return 0.0;

            return Math.Clamp((val - min) / (max - min), 0.0, 1.0);
        }

        private static SKColor ConvertBrushToSkColor(IBrush? brush, SKColor fallback)
        {
            if (brush is not ISolidColorBrush solidColorBrush)
                return fallback;

            try
            {
                var c = solidColorBrush.Color;
                return new SKColor(c.R, c.G, c.B, c.A);
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
    }
}
