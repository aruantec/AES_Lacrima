using System.Globalization;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Message containing the current date and time to display.
/// </summary>
internal record ClockUpdateMessage(DateTime DateTime);

/// <summary>
/// Message to update the ring color.
/// </summary>
internal record ClockRingColorMessage(SKColor Color);

/// <summary>
/// Message to update the text color.
/// </summary>
internal record ClockTextColorMessage(SKColor Color);

/// <summary>
/// Message to update the ring thickness.
/// </summary>
internal record ClockRingThicknessMessage(float Thickness);

/// <summary>
/// Message to update the ring gap angle in degrees.
/// </summary>
internal record ClockRingGapAngleMessage(float GapAngle);

/// <summary>
/// Composition visual handler that renders a circular clock with time, date, and weekday.
/// </summary>
public class ClockCompositionVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;
    private DateTime _currentDateTime = DateTime.Now;
    private SKColor _ringColor = SKColors.White;
    private SKColor _textColor = SKColors.White;
    private float _ringThickness = 4f;
    private float _ringGapAngle = 60f; // Not used anymore - kept for compatibility

    // Paints
    private SKPaint? _ringPaint = new()
    {
        IsAntialias = true,
        StrokeWidth = 4f,
        StrokeCap = SKStrokeCap.Round,
        Style = SKPaintStyle.Stroke,
        Color = SKColors.White
    };

    private SKPaint? _timePaint = new()
    {
        IsAntialias = true,
        Color = SKColors.White,
        TextAlign = SKTextAlign.Center,
        TextSize = 64f,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    private SKPaint? _datePaint = new()
    {
        IsAntialias = true,
        Color = new SKColor(200, 200, 200),
        TextAlign = SKTextAlign.Center,
        TextSize = 24f,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    private SKPaint? _separatorPaint = new()
    {
        IsAntialias = true,
        Color = SKColors.White,
        StrokeWidth = 2f,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round
    };

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                return;
            case ClockUpdateMessage update:
                _currentDateTime = update.DateTime;
                Invalidate();
                return;
            case ClockRingColorMessage ringColor:
                _ringColor = ringColor.Color;
                if (_ringPaint != null)
                    _ringPaint.Color = _ringColor;
                Invalidate();
                return;
            case ClockTextColorMessage textColor:
                _textColor = textColor.Color;
                if (_timePaint != null)
                    _timePaint.Color = _textColor;
                Invalidate();
                return;
            case ClockRingThicknessMessage thickness:
                _ringThickness = thickness.Thickness;
                if (_ringPaint != null)
                    _ringPaint.StrokeWidth = _ringThickness;
                Invalidate();
                return;
            case ClockRingGapAngleMessage gapAngle:
                _ringGapAngle = gapAngle.GapAngle;
                Invalidate();
                return;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        // Update to get smooth second progression
        Invalidate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0) return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        // Use DateTime.Now for smooth real-time updates including milliseconds
        var now = DateTime.Now;

        var centerX = _visualSize.X / 2f;
        var centerY = _visualSize.Y / 2f;
        var size = Math.Min(_visualSize.X, _visualSize.Y);

        // Calculate ring radius
        var ringRadius = (size / 2f) - (_ringThickness / 2f) - 10f;

        // Draw the circular ring with smooth second progression
        DrawRing(canvas, centerX, centerY, ringRadius, now);

        // Draw time (HH : MM)
        var timeText = now.ToString("HH : mm");
        var timeY = centerY - 20f;

        if (_timePaint != null)
        {
            // Adjust font size based on control size (made slightly bigger)
            _timePaint.TextSize = Math.Max(36f, size / 5.5f);
            canvas.DrawText(timeText, centerX, timeY, _timePaint);
        }

        // Draw separator line
        var separatorY = centerY + 5f;
        
        // Draw date with month and weekday (DD MMM | DAY)
        var day = now.Day.ToString("00");
        var month = now.ToString("MMM", CultureInfo.InvariantCulture).ToUpper();
        var weekday = now.ToString("ddd", CultureInfo.InvariantCulture).ToUpper();
        var dateText = $"{day} {month}  |  {weekday}";
        var dateY = centerY + 40f;

        if (_datePaint != null)
        {
            _datePaint.TextSize = Math.Max(18f, size / 12f);
            _datePaint.Color = new SKColor(
                (byte)(_textColor.Red * 0.8f),
                (byte)(_textColor.Green * 0.8f),
                (byte)(_textColor.Blue * 0.8f),
                _textColor.Alpha);
            
            // Measure the text width to size the separator line accordingly
            var textWidth = _datePaint.MeasureText(dateText);
            
            // Draw separator line slightly wider than the text (10% wider)
            var separatorWidth = textWidth * 1.1f;
            if (_separatorPaint != null)
            {
                _separatorPaint.Color = _textColor;
                canvas.DrawLine(
                    centerX - separatorWidth / 2f, separatorY,
                    centerX + separatorWidth / 2f, separatorY,
                    _separatorPaint);
            }
            
            canvas.DrawText(dateText, centerX, dateY, _datePaint);
        }

        RegisterForNextAnimationFrameUpdate();
    }

    private void DrawRing(SKCanvas canvas, float centerX, float centerY, float radius, DateTime currentTime)
    {
        if (_ringPaint == null) return;

        // Calculate sweep angle based on current seconds with milliseconds for smooth growth
        var seconds = currentTime.Second + (currentTime.Millisecond / 1000f);
        var sweepAngle = (seconds / 60f) * 360f;
        
        // Start at 12 o'clock position (top) - in Skia, -90 degrees is top
        var startAngle = -90f;

        var rect = new SKRect(
            centerX - radius,
            centerY - radius,
            centerX + radius,
            centerY + radius);

        // Only draw if there's something to draw (sweep > 0)
        if (sweepAngle > 0)
        {
            using var path = new SKPath();
            path.AddArc(rect, startAngle, sweepAngle);
            canvas.DrawPath(path, _ringPaint);
        }
    }

    private void Cleanup()
    {
        _ringPaint?.Dispose();
        _ringPaint = null;
        _timePaint?.Dispose();
        _timePaint = null;
        _datePaint?.Dispose();
        _datePaint = null;
        _separatorPaint?.Dispose();
        _separatorPaint = null;
    }
}
