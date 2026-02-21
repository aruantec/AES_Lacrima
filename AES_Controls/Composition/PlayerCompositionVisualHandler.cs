using System;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Message containing the progress information (position and duration).
/// </summary>
internal record PlayerProgressMessage(double Position, double Duration);

/// <summary>
/// Message to update the circle background color.
/// </summary>
internal record PlayerCircleBackgroundColorMessage(SKColor Color);

/// <summary>
/// Message to update the circle background bitmap.
/// </summary>
internal record PlayerCircleBackgroundBitmapMessage(SKBitmap? Bitmap);

/// <summary>
/// Message to update the circle background opacity.
/// </summary>
internal record PlayerCircleBackgroundOpacityMessage(float Opacity);

/// <summary>
/// Composition visual handler that renders a circular player widget with progress ring and time display.
/// </summary>
public class PlayerCompositionVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;
    private double _position = 0;
    private double _duration = 0;
    private SKColor _circleBackgroundColor = SKColors.Transparent;
    private SKBitmap? _circleBackgroundBitmap;
    private float _circleBackgroundOpacity = 1.0f;

    // Paints
    private SKPaint? _ringBackgroundPaint = new()
    {
        IsAntialias = true,
        StrokeWidth = 6f,
        StrokeCap = SKStrokeCap.Round,
        Style = SKPaintStyle.Stroke,
        Color = new SKColor(255, 255, 255, 160)
    };

    private SKPaint? _ringPaint = new()
    {
        IsAntialias = true,
        StrokeWidth = 7f,
        StrokeCap = SKStrokeCap.Round,
        Style = SKPaintStyle.Stroke,
        Color = SKColors.White
    };

    private SKPaint? _thumbPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = SKColors.White
    };

    private SKPaint? _timePaint = new()
    {
        IsAntialias = true,
        Color = SKColors.White,
        TextAlign = SKTextAlign.Center,
        TextSize = 40f,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    private SKPaint? _durationPaint = new()
    {
        IsAntialias = true,
        Color = new SKColor(225, 225, 225),
        TextAlign = SKTextAlign.Center,
        TextSize = 18f,
        Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    private SKPaint? _circleBackgroundPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = SKColors.Transparent
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
            case PlayerProgressMessage progress:
                _position = progress.Position;
                _duration = progress.Duration;
                Invalidate();
                return;
            case PlayerCircleBackgroundColorMessage bgColor:
                _circleBackgroundColor = bgColor.Color;
                Invalidate();
                return;
            case PlayerCircleBackgroundBitmapMessage bgBitmap:
                _circleBackgroundBitmap?.Dispose();
                _circleBackgroundBitmap = bgBitmap.Bitmap;
                Invalidate();
                return;
            case PlayerCircleBackgroundOpacityMessage opacity:
                _circleBackgroundOpacity = opacity.Opacity;
                Invalidate();
                return;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0) return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        var centerX = _visualSize.X / 2f;
        var centerY = _visualSize.Y / 2f;
        var size = Math.Min(_visualSize.X, _visualSize.Y);

        // Calculate ring radius - EXACTLY like ClockCompositionControl
        var ringRadius = (size / 2f) - (_ringPaint!.StrokeWidth / 2f) - 10f;

        // Draw circle background (color or bitmap)
        DrawCircleBackground(canvas, centerX, centerY, ringRadius);

        // Draw the progress ring - EXACTLY like clock's seconds ring
        DrawProgressRing(canvas, centerX, centerY, ringRadius);

        // Draw position time - HIGHER UP
        var positionText = FormatTime(_position);
        var timeY = centerY - 20f;

        if (_timePaint != null)
        {
            _timePaint.TextSize = Math.Max(48f, size / 4.5f);
            canvas.DrawText(positionText, centerX, timeY, _timePaint);
        }

        // Draw duration - MASSIVE MARGIN (80px gap from center area!)
        var durationText = FormatTime(_duration);
        var durationY = centerY + 100f; // Increased for more margin

        if (_durationPaint != null)
        {
            _durationPaint.TextSize = Math.Max(28f, size / 9f); // Bigger duration
            _durationPaint.Color = new SKColor(225, 225, 225);
            canvas.DrawText(durationText, centerX, durationY, _durationPaint);
        }
    }

    private void DrawCircleBackground(SKCanvas canvas, float centerX, float centerY, float radius)
    {
        if (_circleBackgroundPaint == null) return;

        canvas.Save();

        using var clipPath = new SKPath();
        clipPath.AddCircle(centerX, centerY, radius);
        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

        if (_circleBackgroundBitmap != null)
        {
            var destRect = new SKRect(
                centerX - radius,
                centerY - radius,
                centerX + radius,
                centerY + radius);

            var destWidth = destRect.Width;
            var destHeight = destRect.Height;
            var srcWidth = _circleBackgroundBitmap.Width;
            var srcHeight = _circleBackgroundBitmap.Height;

            var scaleX = destWidth / srcWidth;
            var scaleY = destHeight / srcHeight;
            var scale = Math.Max(scaleX, scaleY);

            var scaledWidth = srcWidth * scale;
            var scaledHeight = srcHeight * scale;

            var offsetX = centerX - (scaledWidth / 2f);
            var offsetY = centerY - (scaledHeight / 2f);

            var scaledRect = new SKRect(
                offsetX,
                offsetY,
                offsetX + scaledWidth,
                offsetY + scaledHeight);

            _circleBackgroundPaint.Color = SKColors.White.WithAlpha((byte)(_circleBackgroundOpacity * 255));
            canvas.DrawBitmap(_circleBackgroundBitmap, scaledRect, _circleBackgroundPaint);
        }
        else if (_circleBackgroundColor.Alpha > 0)
        {
            _circleBackgroundPaint.Color = _circleBackgroundColor.WithAlpha((byte)(_circleBackgroundOpacity * _circleBackgroundColor.Alpha));
            canvas.DrawCircle(centerX, centerY, radius, _circleBackgroundPaint);
        }

        canvas.Restore();
    }

    private void DrawProgressRing(SKCanvas canvas, float centerX, float centerY, float radius)
    {
        if (_ringPaint == null || _ringBackgroundPaint == null) return;

        var rect = new SKRect(
            centerX - radius,
            centerY - radius,
            centerX + radius,
            centerY + radius);

        // ALWAYS draw background ring (full circle)
        using (var bgPath = new SKPath())
        {
            bgPath.AddArc(rect, -90f, 360f);
            canvas.DrawPath(bgPath, _ringBackgroundPaint);
        }

        // Draw progress ring EXACTLY like clock seconds
        if (_duration > 0)
        {
            // Calculate progress with fractions (like clock's milliseconds)
            var progress = _position / _duration;
            var sweepAngle = (float)(progress * 360.0);

            // Start at 12 o'clock, just like the clock
            var startAngle = -90f;

            if (sweepAngle > 0)
            {
                using var path = new SKPath();
                path.AddArc(rect, startAngle, sweepAngle);
                canvas.DrawPath(path, _ringPaint);

                // Draw thumb (dot) at the end of the progress arc
                if (_thumbPaint != null)
                {
                    var angleRad = (startAngle + sweepAngle) * Math.PI / 180.0;
                    var thumbX = centerX + (float)(radius * Math.Cos(angleRad));
                    var thumbY = centerY + (float)(radius * Math.Sin(angleRad));
                    canvas.DrawCircle(thumbX, thumbY, 8f, _thumbPaint);
                }
            }
        }
    }

    private string FormatTime(double timeSeconds)
    {
        if (!double.IsFinite(timeSeconds))
            return "0:00";

        var time = TimeSpan.FromSeconds(timeSeconds);
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes}:{time.Seconds:D2}";
    }

    private void Cleanup()
    {
        _ringBackgroundPaint?.Dispose();
        _ringBackgroundPaint = null;
        _ringPaint?.Dispose();
        _ringPaint = null;
        _thumbPaint?.Dispose();
        _thumbPaint = null;
        _timePaint?.Dispose();
        _timePaint = null;
        _durationPaint?.Dispose();
        _durationPaint = null;
        _circleBackgroundPaint?.Dispose();
        _circleBackgroundPaint = null;
        _circleBackgroundBitmap?.Dispose();
        _circleBackgroundBitmap = null;
    }
}
