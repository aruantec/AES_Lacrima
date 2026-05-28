using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;

namespace AES_Controls.Player;

/// <summary>
/// A vertical volume slider: pill-shaped track with a bright fill that
/// rises from the bottom and a speaker icon at the base.
/// At 100% the fill covers the entire track.
/// </summary>
public class VerticalVolumeSliderControl : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<VerticalVolumeSliderControl, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<VerticalVolumeSliderControl, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VerticalVolumeSliderControl, double>(
            nameof(Value),
            defaultValue: 50.0,
            coerce: (o, v) =>
            {
                var c = (VerticalVolumeSliderControl)o;
                return Math.Clamp(v, c.Minimum, c.Maximum);
            });

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private const double TrackWidth = 46.0;
    private const double TrackRadius = 23.0;
    private const double IconZoneHeight = 36.0;

    private static readonly IBrush TrackOutlineBrush = new SolidColorBrush(Color.FromArgb(255, 70, 72, 80));
    private static readonly IBrush TrackEmptyBrush = new SolidColorBrush(Color.FromArgb(255, 115, 117, 125));
    private static readonly IBrush FillBrush = new SolidColorBrush(Colors.White);
    private static readonly IBrush IconBrush = new SolidColorBrush(Color.FromArgb(200, 80, 82, 90));

    private bool _isDragging;
    private double _trackTop;
    private double _trackBottom;
    private double _trackCenterX;

    public VerticalVolumeSliderControl()
    {
        this.GetObservable(ValueProperty)
            .Subscribe(new SimpleObserver<double>(_ => InvalidateVisual()));
    }

    private double Percent =>
        Maximum > Minimum
            ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1)
            : 0;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;

        _trackCenterX = w / 2;
        _trackTop = 0;
        _trackBottom = h;
        double trackH = h;

        double pct = Percent;
        double fillTop = _trackBottom - trackH * pct;

        double trackLeft = _trackCenterX - TrackWidth / 2;

        // 1. Outer pill (dark outline)
        var outerRect = new RoundedRect(
            new Rect(trackLeft, _trackTop, TrackWidth, trackH),
            TrackRadius);
        ctx.DrawRectangle(null, new Pen(TrackOutlineBrush, 3.0), outerRect);

        // 2. Inner pill clipped area
        double innerPad = 3.0;
        var innerRect = new RoundedRect(
            new Rect(trackLeft + innerPad, _trackTop + innerPad, TrackWidth - innerPad * 2, trackH - innerPad * 2),
            TrackRadius - innerPad);

        // 3. Empty portion (gray, from top down to fill boundary)
        double emptyBottom = fillTop;
        double emptyHeight = emptyBottom - (_trackTop + innerPad);
        if (emptyHeight > 0)
        {
            using (ctx.PushClip(innerRect))
            {
                ctx.DrawRectangle(TrackEmptyBrush, null, new Rect(trackLeft + innerPad, _trackTop + innerPad, TrackWidth - innerPad * 2, emptyHeight));
            }
        }

        // 4. Filled portion (white, from fill boundary down to bottom)
        double fillHeight = (_trackBottom - innerPad) - fillTop;
        if (fillHeight > 0)
        {
            using (ctx.PushClip(innerRect))
            {
                ctx.DrawRectangle(FillBrush, null, new Rect(trackLeft + innerPad, fillTop, TrackWidth - innerPad * 2, fillHeight));
            }
        }

        // 5. Speaker icon at bottom
        double iconCy = _trackBottom - IconZoneHeight / 2;
        DrawSpeakerIcon(ctx, _trackCenterX, iconCy);
    }

    private static void DrawSpeakerIcon(DrawingContext ctx, double cx, double cy)
    {
        double bodyW = 6.0;
        double bodyH = 8.0;
        double coneW = 4.5;

        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(new Point(cx - bodyW / 2 - coneW, cy - bodyH * 0.3), true);
            sgc.LineTo(new Point(cx - bodyW / 2, cy - bodyH / 2));
            sgc.LineTo(new Point(cx + bodyW / 2, cy - bodyH / 2));
            sgc.LineTo(new Point(cx + bodyW / 2, cy + bodyH / 2));
            sgc.LineTo(new Point(cx - bodyW / 2, cy + bodyH / 2));
            sgc.LineTo(new Point(cx - bodyW / 2 - coneW, cy + bodyH * 0.3));
            sgc.EndFigure(true);
        }
        ctx.DrawGeometry(IconBrush, null, geo);

        DrawArc(ctx, cx + bodyW / 2, cy, bodyH * 0.75, -50, 50);
        DrawArc(ctx, cx + bodyW / 2, cy, bodyH * 1.25, -60, 60);
    }

    private static void DrawArc(DrawingContext ctx, double cx, double cy,
                                 double radius, double startDeg, double endDeg)
    {
        double s = startDeg * Math.PI / 180;
        double e = endDeg * Math.PI / 180;
        var start = new Point(cx + radius * Math.Cos(s), cy + radius * Math.Sin(s));
        var end = new Point(cx + radius * Math.Cos(e), cy + radius * Math.Sin(e));
        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(start, false);
            sgc.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(IconBrush, 1.6, null, PenLineCap.Round), geo);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _isDragging = true;
        e.Pointer.Capture(this);
        UpdateValueFromPointer(e.GetPosition(this));
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isDragging)
            UpdateValueFromPointer(e.GetPosition(this));
        base.OnPointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        double step = (Maximum - Minimum) / 100.0 * 5;
        Value = Math.Clamp(Value + e.Delta.Y * step, Minimum, Maximum);
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private void UpdateValueFromPointer(Point p)
    {
        double trackH = _trackBottom - _trackTop;
        if (trackH <= 0) return;
        double pct = Math.Clamp((_trackBottom - p.Y) / trackH, 0, 1);
        Value = Minimum + pct * (Maximum - Minimum);
    }
}
