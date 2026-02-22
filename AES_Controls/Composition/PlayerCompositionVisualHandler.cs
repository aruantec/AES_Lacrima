using System;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal record PlayerProgressMessage(double Position, double Duration);
internal record PlayerCircleBackgroundColorMessage(SKColor Color);
internal record PlayerCircleBackgroundBitmapMessage(SKBitmap? Bitmap);
internal record PlayerCircleBackgroundOpacityMessage(float Opacity);
internal record PlayerShowTimeMessage(bool Show);
internal record PlayerShowPlayPauseMessage(bool Show);
internal record PlayerIsPlayingMessage(bool IsPlaying);
internal record PlayerPlayPauseOpacityMessage(float Opacity);
internal record PlayerRotateMessage(bool Rotate);
internal record PlayerShowDiscCenterMessage(bool Show);

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
    private bool _showTime = true;
    private bool _showPlayPause = false;
    private bool _isPlaying = false;
    private float _playPauseOpacity = 1.0f;
    private bool _rotate = false;
    private float _rotationAngle = 0f;
    private long _lastRotationTicks = 0;
    private bool _showDiscCenter = false;

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

    private SKPaint? _discHubPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(255, 255, 255, 30) // Subtle white tint for plastic hub
    };

    private SKPaint? _discHoleRimPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 0.5f,
        Color = new SKColor(0, 0, 0, 40) // Subtle rim for the hole
    };

    private SKPaint? _playPausePaint = new()
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
            case PlayerShowTimeMessage showTime:
                _showTime = showTime.Show;
                Invalidate();
                return;
            case PlayerShowPlayPauseMessage showPP:
                _showPlayPause = showPP.Show;
                Invalidate();
                return;
            case PlayerIsPlayingMessage isPlaying:
                _isPlaying = isPlaying.IsPlaying;
                Invalidate();
                return;
            case PlayerPlayPauseOpacityMessage ppOpacity:
                _playPauseOpacity = ppOpacity.Opacity;
                Invalidate();
                return;
            case PlayerRotateMessage rotate:
                _rotate = rotate.Rotate;
                if (!_rotate) _lastRotationTicks = 0;
                Invalidate();
                return;
            case PlayerShowDiscCenterMessage showDC:
                _showDiscCenter = showDC.Show;
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

        if (_showTime)
        {
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

        if (_showPlayPause)
        {
            DrawPlayPauseIcon(canvas, centerX, centerY, size / 4f);
        }

        if (_showDiscCenter)
        {
            DrawDiscCenter(canvas, centerX, centerY, ringRadius);
        }

        // Handle animation for rotation
        if (_rotate && _isPlaying)
        {
            long currentTicks = DateTime.UtcNow.Ticks;
            if (_lastRotationTicks != 0)
            {
                double deltaSeconds = (currentTicks - _lastRotationTicks) / (double)TimeSpan.TicksPerSecond;
                // Rotate 30 degrees per second
                _rotationAngle = (float)((_rotationAngle + deltaSeconds * 30.0) % 360.0);
            }
            _lastRotationTicks = currentTicks;
            Invalidate(); // Request next frame
        }
        else
        {
            _lastRotationTicks = 0;
        }
    }

    private void DrawPlayPauseIcon(SKCanvas canvas, float centerX, float centerY, float size)
    {
        if (_playPausePaint == null) return;
        _playPausePaint.Color = SKColors.White.WithAlpha((byte)(_playPauseOpacity * 255));

        if (_isPlaying)
        {
            // Draw Pause Icon (two vertical bars)
            var barWidth = size / 4f;
            var barHeight = size;
            var gap = barWidth;

            var leftBar = new SKRect(centerX - barWidth - gap / 2f, centerY - barHeight / 2f, centerX - gap / 2f, centerY + barHeight / 2f);
            var rightBar = new SKRect(centerX + gap / 2f, centerY - barHeight / 2f, centerX + barWidth + gap / 2f, centerY + barHeight / 2f);

            canvas.DrawRoundRect(leftBar, 4f, 4f, _playPausePaint);
            canvas.DrawRoundRect(rightBar, 4f, 4f, _playPausePaint);
        }
        else
        {
            // Draw Play Icon (triangle)
            using var path = new SKPath();
            path.MoveTo(centerX - size / 2.5f, centerY - size / 2f);
            path.LineTo(centerX - size / 2.5f, centerY + size / 2f);
            path.LineTo(centerX + size / 2f, centerY);
            path.Close();
            canvas.DrawPath(path, _playPausePaint);
        }
    }

    private void DrawCircleBackground(SKCanvas canvas, float centerX, float centerY, float radius)
    {
        if (_circleBackgroundPaint == null) return;

        canvas.Save();

        using var clipPath = new SKPath();
        clipPath.AddCircle(centerX, centerY, radius);

        if (_showDiscCenter)
        {
            // Clip out the center hub area from the background
            var hubRadius = radius * 0.35f;
            clipPath.AddCircle(centerX, centerY, hubRadius, SKPathDirection.Clockwise);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

            using var hubClipPath = new SKPath();
            hubClipPath.AddCircle(centerX, centerY, hubRadius);
            canvas.ClipPath(hubClipPath, SKClipOperation.Difference, true);
        }
        else
        {
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
        }

        if (_rotate && _rotationAngle != 0)
        {
            canvas.RotateDegrees(_rotationAngle, centerX, centerY);
        }

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

    private void DrawDiscCenter(SKCanvas canvas, float centerX, float centerY, float radius)
    {
        if (_discHubPaint == null || _discHoleRimPaint == null) return;

        var hubRadius = radius * 0.35f;
        var holeRadius = radius * 0.12f;

        // Draw plastic hub area (slightly transparent overlay)
        using var hubPath = new SKPath();
        hubPath.AddCircle(centerX, centerY, hubRadius);
        using var holePath = new SKPath();
        holePath.AddCircle(centerX, centerY, holeRadius);

        canvas.Save();
        canvas.ClipPath(holePath, SKClipOperation.Difference, true);
        canvas.DrawCircle(centerX, centerY, hubRadius, _discHubPaint);
        canvas.Restore();

        // Draw hub inner rim
        canvas.DrawCircle(centerX, centerY, hubRadius, _discHoleRimPaint);

        // Draw center hole plastic rim
        canvas.DrawCircle(centerX, centerY, holeRadius, _discHoleRimPaint);

        // Draw a subtle "shadow" or rim for the hub area to give it depth
        using var hubDetailPaint = new SKPaint { 
            IsAntialias = true, 
            Style = SKPaintStyle.Stroke, 
            StrokeWidth = 1f, 
            Color = new SKColor(255, 255, 255, 80)
        };
        canvas.DrawCircle(centerX, centerY, hubRadius - 2, hubDetailPaint);
        canvas.DrawCircle(centerX, centerY, holeRadius + 2, hubDetailPaint);
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
        _discHubPaint?.Dispose();
        _discHubPaint = null;
        _discHoleRimPaint?.Dispose();
        _discHoleRimPaint = null;
        _playPausePaint?.Dispose();
        _playPausePaint = null;
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
