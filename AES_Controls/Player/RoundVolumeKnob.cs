using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;

namespace AES_Controls.Player;

/// <summary>
/// A circular knob control for adjusting volume or other numeric values with customizable visual elements.
/// </summary>
public class RoundVolumeKnob : Control
{
    /// <summary>
    /// Defines the <see cref="Minimum"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(Minimum));

    /// <summary>
    /// Defines the <see cref="Maximum"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(Maximum), 100.0);

    /// <summary>
    /// Defines the <see cref="Value"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(
            nameof(Value),
            coerce: (d, v) =>
            {
                var control = (RoundVolumeKnob)d;
                double min = control.Minimum;
                double max = control.Maximum;
                if (v < min) return min;
                if (v > max) return max;
                return v;
            }
        );

    /// <summary>
    /// Defines the <see cref="KnobBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> KnobBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(KnobBrush), Brushes.LightGray);

    /// <summary>
    /// Defines the <see cref="DotBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> DotBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(DotBrush), Brushes.Red);

    /// <summary>
    /// Defines the <see cref="TickBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> TickBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(TickBrush), Brushes.LightGray);

    /// <summary>
    /// Defines the <see cref="ActiveTickBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> ActiveTickBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(ActiveTickBrush), Brushes.Red);

    /// <summary>
    /// Defines the <see cref="MinTextSize"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MinTextSizeProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(MinTextSize), 16.0);

    /// <summary>
    /// Defines the <see cref="MaxTextSize"/> property.
    /// </summary>
    public static readonly StyledProperty<double> MaxTextSizeProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(MaxTextSize), 16.0);

    /// <summary>
    /// Defines the <see cref="KnobMargin"/> property.
    /// </summary>
    public static readonly StyledProperty<double> KnobMarginProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(KnobMargin), 10.0);

    /// <summary>
    /// Defines the <see cref="TickCount"/> property.
    /// </summary>
    public static readonly StyledProperty<int> TickCountProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, int>(nameof(TickCount), 36);

    /// <summary>
    /// Defines the <see cref="TickLength"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TickLengthProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(TickLength), 12.0);

    /// <summary>
    /// Defines the <see cref="TickThickness"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TickThicknessProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(TickThickness), 4.0);

    /// <summary>
    /// Defines the <see cref="TickOffset"/> property.
    /// </summary>
    public static readonly StyledProperty<double> TickOffsetProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, double>(nameof(TickOffset), 10.0);

    /// <summary>
    /// Defines the <see cref="MinTextBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> MinTextBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(MinTextBrush), Brushes.Gray);

    /// <summary>
    /// Defines the <see cref="MaxTextBrush"/> property.
    /// </summary>
    public static readonly StyledProperty<IBrush> MaxTextBrushProperty =
        AvaloniaProperty.Register<RoundVolumeKnob, IBrush>(nameof(MaxTextBrush), Brushes.Gray);

    /// <summary>
    /// Gets or sets the minimum value allowed for the knob.
    /// </summary>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    
    /// <summary>
    /// Gets or sets the maximum value allowed for the knob.
    /// </summary>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    
    /// <summary>
    /// Gets or sets the current numeric value of the knob.
    /// </summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>
    /// Gets or sets the brush used to paint the knob body.
    /// </summary>
    public IBrush KnobBrush { get => GetValue(KnobBrushProperty); set => SetValue(KnobBrushProperty, value); }
    
    /// <summary>
    /// Gets or sets the brush used for the indicator dot/marker on the knob.
    /// </summary>
    public IBrush DotBrush { get => GetValue(DotBrushProperty); set => SetValue(DotBrushProperty, value); }
    
    /// <summary>
    /// Gets or sets the brush used for drawing inactive ticks.
    /// </summary>
    public IBrush TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    
    /// <summary>
    /// Gets or sets the brush used for drawing active ticks (those below the current value).
    /// </summary>
    public IBrush ActiveTickBrush { get => GetValue(ActiveTickBrushProperty); set => SetValue(ActiveTickBrushProperty, value); }

    /// <summary>
    /// Gets or sets the font size for the "MIN" label.
    /// </summary>
    public double MinTextSize { get => GetValue(MinTextSizeProperty); set => SetValue(MinTextSizeProperty, value); }
    
    /// <summary>
    /// Gets or sets the font size for the "MAX" label.
    /// </summary>
    public double MaxTextSize { get => GetValue(MaxTextSizeProperty); set => SetValue(MaxTextSizeProperty, value); }

    /// <summary>
    /// Gets or sets the margin around the knob body.
    /// </summary>
    public double KnobMargin { get => GetValue(KnobMarginProperty); set => SetValue(KnobMarginProperty, value); }

    /// <summary>
    /// Gets or sets the total number of radial ticks to display.
    /// </summary>
    public int TickCount { get => GetValue(TickCountProperty); set => SetValue(TickCountProperty, value); }
    
    /// <summary>
    /// Gets or sets the length of each radial tick line.
    /// </summary>
    public double TickLength { get => GetValue(TickLengthProperty); set => SetValue(TickLengthProperty, value); }
    
    /// <summary>
    /// Gets or sets the thickness of the tick lines.
    /// </summary>
    public double TickThickness { get => GetValue(TickThicknessProperty); set => SetValue(TickThicknessProperty, value); }
    
    /// <summary>
    /// Gets or sets the radial offset of the ticks from the knob body boundary.
    /// </summary>
    public double TickOffset { get => GetValue(TickOffsetProperty); set => SetValue(TickOffsetProperty, value); }

    /// <summary>
    /// Gets or sets the brush for the "MIN" label text.
    /// </summary>
    public IBrush MinTextBrush { get => GetValue(MinTextBrushProperty); set => SetValue(MinTextBrushProperty, value); }
    
    /// <summary>
    /// Gets or sets the brush for the "MAX" label text.
    /// </summary>
    public IBrush MaxTextBrush { get => GetValue(MaxTextBrushProperty); set => SetValue(MaxTextBrushProperty, value); }

    private bool _isDragging;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoundVolumeKnob"/> class.
    /// </summary>
    public RoundVolumeKnob()
    {
        this.GetObservable(ValueProperty)
            .Subscribe(new SimpleObserver<double>(_ => InvalidateVisual()));
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height);
        double centerX = Bounds.Width / 2;
        double centerY = Bounds.Height / 2;

        // compute effective radius honoring KnobMargin so contents don't overlap the knob
        double radius = size / 2 - KnobMargin - 8; // leave small padding

        double startAngle = 135;
        double span = 270;

        // draw ticks (inactive + active)
        int ticks = Math.Max(1, TickCount);
        double outerRadius = radius + TickOffset + TickLength;
        double innerRadius = radius + TickOffset;

        // compute percent for active ticks
        double percent = (Value - Minimum) / (Maximum - Minimum);
        percent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 1);
        int activeTicks = (int)Math.Round(percent * ticks);

        // Draw each tick as a short radial line
        for (int i = 0; i <= ticks; i++)
        {
            double a = (startAngle + (span * i / ticks)) * Math.PI / 180.0;
            var pOuter = new Point(centerX + outerRadius * Math.Cos(a), centerY + outerRadius * Math.Sin(a));
            var pInner = new Point(centerX + innerRadius * Math.Cos(a), centerY + innerRadius * Math.Sin(a));

            // choose brush based on active state
            IBrush tickBrush = i <= activeTicks ? ActiveTickBrush : TickBrush;

            // draw line (Avalonia Pen requires additional args)
            var pen = new Pen(tickBrush, TickThickness, null, PenLineCap.Round);
            context.DrawLine(pen, pInner, pOuter);
        }

        // draw knob background (supports custom brush)
        var knobBrush = KnobBrush;
        context.DrawEllipse(knobBrush, null, new Point(centerX, centerY), radius, radius);

        // marker/dot for current value
        double angle = startAngle + percent * span;
        double rad = angle * Math.PI / 180;
        double markerRadius = Math.Max(6, radius - 15); // keep inside knob
        var marker = new Point(
            centerX + markerRadius * Math.Cos(rad),
            centerY + markerRadius * Math.Sin(rad)
        );
        // DotBrush is configurable
        context.DrawEllipse(DotBrush, null, marker, 8, 8);

        // MIN/MAX Labels using configured sizes and brushes and placed outside ticks
        // Create FormattedText with the configured brushes for each label
        var minText = new FormattedText(
            "MIN",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            MinTextSize,
            MinTextBrush
        );
        var maxText = new FormattedText(
            "MAX",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            MaxTextSize,
            MaxTextBrush
        );

        // place MIN near start angle, MAX near end angle (slightly below the arc)
        double labelRadius = outerRadius + 12;
        double minAngleRad = (startAngle + span * 0.0) * Math.PI / 180.0;
        double maxAngleRad = (startAngle + span * 1.0) * Math.PI / 180.0;

        // Use Bounds of FormattedText for width/height
        var minPos = new Point(centerX + labelRadius * Math.Cos(minAngleRad) - minText.Width / 2,
                               centerY + labelRadius * Math.Sin(minAngleRad) - minText.Height / 2);
        var maxPos = new Point(centerX + labelRadius * Math.Cos(maxAngleRad) - maxText.Width / 2,
                               centerY + labelRadius * Math.Sin(maxAngleRad) - maxText.Height / 2);

        context.DrawText(minText, minPos);
        context.DrawText(maxText, maxPos);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _isDragging = true;
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isDragging)
        {
            var p = e.GetPosition(this);
            double centerX = Bounds.Width / 2;
            double centerY = Bounds.Height / 2;
            double dx = p.X - centerX;
            double dy = p.Y - centerY;

            // Compute raw angle in degrees [0,360)
            double rawAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (rawAngle < 0)
                rawAngle += 360;

            double startAngle = 135;
            double span = 270; // arc length from start to end (end = start + span)

            // Map angle relative to startAngle into [0,360)
            double delta = (rawAngle - startAngle) % 360;
            if (delta < 0)
                delta += 360;

            if (delta <= span)
            {
                // Inside the valid arc: interpolate value linearly
                double percent = delta / span;
                percent = Math.Clamp(percent, 0, 1);
                Value = Minimum + percent * (Maximum - Minimum);
            }
            else
            {
                // Outside the arc: choose the nearest endpoint (min or max)
                double distToStart = Math.Min(delta, 360 - delta);
                double distToEnd = Math.Min(Math.Abs(delta - span), 360 - Math.Abs(delta - span));

                if (distToStart <= distToEnd)
                    Value = Minimum;
                else
                    Value = Maximum;
            }
        }

        base.OnPointerMoved(e);
    }
}