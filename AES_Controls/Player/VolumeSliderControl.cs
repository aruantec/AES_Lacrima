using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;

namespace AES_Controls.Player;

/// <summary>
/// A horizontal volume slider styled after the macOS/iOS aesthetic:
/// sunken pill track, circular thumb, speaker icons on each end,
/// and a dark floating tooltip above the thumb showing the percentage.
/// </summary>
public class VolumeSliderControl : Control
{
    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<VolumeSliderControl, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<VolumeSliderControl, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<VolumeSliderControl, double>(
            nameof(Value),
            defaultValue: 100.0,
            coerce: (o, v) =>
            {
                var c = (VolumeSliderControl)o;
                return Math.Clamp(v, c.Minimum, c.Maximum);
            });

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value   { get => GetValue(ValueProperty);   set => SetValue(ValueProperty,   value); }

    // ── Visual constants ──────────────────────────────────────────────────

    private const double TrackHeight      = 10.0;
    private const double TrackInnerHeight = 7.0;
    private const double ThumbRadius      = 9.5;
    private const double IconZoneWidth    = 28.0;

    private const double TooltipWidth  = 46.0;
    private const double TooltipHeight = 22.0;
    private const double TooltipRadius = 5.0;
    private const double TooltipArrow  = 5.0;
    private const double TooltipGap    = 4.0;

    // ── Colours ───────────────────────────────────────────────────────────

    private static readonly Color TrackBg1      = Color.FromArgb(255, 85,  87, 100);
    private static readonly Color TrackBg2      = Color.FromArgb(255, 68,  70,  82);
    private static readonly Color InnerGroove1  = Color.FromArgb(255, 52,  54,  65);
    private static readonly Color InnerGroove2  = Color.FromArgb(255, 60,  62,  74);
    private static readonly Color FillColor     = Color.FromArgb(255, 100, 102, 118);
    private static readonly Color ThumbEdge     = Color.FromArgb(255, 50,  52,  63);
    private static readonly Color ThumbBase1    = Color.FromArgb(255, 122, 124, 140);
    private static readonly Color ThumbBase2    = Color.FromArgb(255, 88,  90, 106);
    private static readonly Color ThumbHilight  = Color.FromArgb(150, 255, 255, 255);

    private static readonly IBrush IconBrush    = new SolidColorBrush(Color.FromArgb(200, 200, 202, 215));
    private static readonly IBrush TooltipBg    = new SolidColorBrush(Color.FromArgb(230, 32,  33,  42));
    private static readonly IBrush TooltipFg    = new SolidColorBrush(Colors.White);

    // ── State ─────────────────────────────────────────────────────────────

    private bool   _isDragging;
    private double _trackLeft;
    private double _trackRight;

    // ── Constructor ───────────────────────────────────────────────────────

    public VolumeSliderControl()
    {
        this.GetObservable(ValueProperty)
            .Subscribe(new SimpleObserver<double>(_ => InvalidateVisual()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private double Percent =>
        Maximum > Minimum
            ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1)
            : 0;

    // ── Rendering ─────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        double w  = Bounds.Width;
        double h  = Bounds.Height;
        double cy = h / 2;

        _trackLeft  = IconZoneWidth;
        _trackRight = w - IconZoneWidth;
        double trackW = _trackRight - _trackLeft;

        double pct    = Percent;
        double thumbX = _trackLeft + trackW * pct;

        // 1. Outer track pill
        double trackTop = cy - TrackHeight / 2;
        var trackRect = new RoundedRect(
            new Rect(_trackLeft, trackTop, trackW, TrackHeight),
            TrackHeight / 2);

        var trackBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(TrackBg1, 0),
                new GradientStop(TrackBg2, 1)
            }
        };
        ctx.DrawRectangle(trackBrush, null, trackRect);

        // 2. Inner recessed groove
        double ig     = (TrackHeight - TrackInnerHeight) / 2;
        double igTop  = cy - TrackInnerHeight / 2;
        double igLeft = _trackLeft + ig;
        double igW    = trackW - ig * 2;
        var igRect = new RoundedRect(new Rect(igLeft, igTop, igW, TrackInnerHeight), TrackInnerHeight / 2);

        var innerBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(InnerGroove1, 0),
                new GradientStop(InnerGroove2, 1)
            }
        };
        ctx.DrawRectangle(innerBrush, null, igRect);

        // 3. Filled left portion
        double fillRight = thumbX;
        double fillLeft2 = igLeft;
        if (fillRight > fillLeft2)
        {
            double r2 = TrackInnerHeight / 2;
            using (ctx.PushClip(igRect.Rect))
            {
                var fillRect = new RoundedRect(
                    new Rect(fillLeft2, igTop, Math.Max(fillRight - fillLeft2, r2 * 2), TrackInnerHeight),
                    r2);
                ctx.DrawRectangle(new SolidColorBrush(FillColor), null, fillRect);
            }
        }

        // 4. Left speaker icon (small / muted)
        DrawSpeakerIcon(ctx, IconZoneWidth / 2, cy, small: true);

        // 5. Right speaker icon (full volume)
        DrawSpeakerIcon(ctx, w - IconZoneWidth / 2, cy, small: false);

        // 6. Thumb
        double tr = ThumbRadius;
        var thumbCenter = new Point(thumbX, cy);

        // Outer dark ring
        ctx.DrawEllipse(Brushes.Transparent, new Pen(new SolidColorBrush(ThumbEdge), 1.2), thumbCenter, tr, tr);

        // Body gradient
        var thumbBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.3, 0.0, RelativeUnit.Relative),
            EndPoint   = new RelativePoint(0.7, 1.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ThumbBase1, 0),
                new GradientStop(ThumbBase2, 1)
            }
        };
        ctx.DrawEllipse(thumbBrush, null, thumbCenter, tr - 0.6, tr - 0.6);

        // Specular highlight
        double hr = (tr - 0.6) * 0.52;
        ctx.DrawEllipse(
            new SolidColorBrush(ThumbHilight), null,
            new Point(thumbX - tr * 0.18, cy - tr * 0.20), hr, hr * 0.55);

        // 7. Tooltip
        DrawTooltip(ctx, thumbX, trackTop, pct);
    }

    // ── Tooltip ───────────────────────────────────────────────────────────

    private static void DrawTooltip(DrawingContext ctx, double thumbX, double trackTop, double pct)
    {
        int percent = (int)Math.Round(pct * 100);

        double tooltipBottom = trackTop - ThumbRadius - TooltipGap;
        double tooltipTop    = tooltipBottom - TooltipHeight - TooltipArrow;
        double tl            = thumbX - TooltipWidth / 2;
        double tw            = TooltipWidth;
        double th            = TooltipHeight;
        double r             = TooltipRadius;
        double ab            = tooltipTop + th;

        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(new Point(tl + r, tooltipTop), true);
            sgc.LineTo(new Point(tl + tw - r, tooltipTop));
            sgc.ArcTo(new Point(tl + tw, tooltipTop + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(tl + tw, ab));
            sgc.ArcTo(new Point(tl + tw - r, ab), new Size(r, r), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(thumbX + TooltipArrow, ab));
            sgc.LineTo(new Point(thumbX, ab + TooltipArrow));
            sgc.LineTo(new Point(thumbX - TooltipArrow, ab));
            sgc.LineTo(new Point(tl + r, ab));
            sgc.ArcTo(new Point(tl, ab - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            sgc.LineTo(new Point(tl, tooltipTop + r));
            sgc.ArcTo(new Point(tl + r, tooltipTop), new Size(r, r), 0, false, SweepDirection.Clockwise);
            sgc.EndFigure(true);
        }

        ctx.DrawGeometry(TooltipBg, null, geo);

        var ft = new FormattedText(
            $"{percent}%",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            11.5,
            TooltipFg);

        ctx.DrawText(ft,
            new Point(tl + (tw - ft.Width) / 2,
                      tooltipTop + (th - ft.Height) / 2));
    }

    // ── Speaker icon ──────────────────────────────────────────────────────

    private static void DrawSpeakerIcon(DrawingContext ctx, double cx, double cy, bool small)
    {
        double bodyW = 5.5;
        double bodyH = small ? 7.0 : 8.0;
        double coneW = small ? 3.5 : 4.0;

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

        if (!small)
        {
            DrawArc(ctx, cx + bodyW / 2, cy, bodyH * 0.75, -50, 50);
            DrawArc(ctx, cx + bodyW / 2, cy, bodyH * 1.25, -60, 60);
        }
    }

    private static void DrawArc(DrawingContext ctx, double cx, double cy,
                                 double radius, double startDeg, double endDeg)
    {
        double s     = startDeg * Math.PI / 180;
        double e     = endDeg   * Math.PI / 180;
        var start    = new Point(cx + radius * Math.Cos(s), cy + radius * Math.Sin(s));
        var end      = new Point(cx + radius * Math.Cos(e), cy + radius * Math.Sin(e));
        var geo      = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(start, false);
            sgc.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            sgc.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(IconBrush, 1.6, null, PenLineCap.Round), geo);
    }

    // ── Input ─────────────────────────────────────────────────────────────

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
        double trackW = _trackRight - _trackLeft;
        if (trackW <= 0) return;
        double pct = Math.Clamp((p.X - _trackLeft) / trackW, 0, 1);
        Value = Minimum + pct * (Maximum - Minimum);
    }
}
