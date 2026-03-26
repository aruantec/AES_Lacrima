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
    private float _armAngleDegrees = (float)PlayerCompositionArmMetrics.RestAngleDegrees;
    private long _lastAnimationTicks = 0;

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

    private SKPaint? _armShadowPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        Color = new SKColor(0, 0, 0, 90)
    };

    private SKPaint? _armMetalPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        Color = new SKColor(208, 214, 221)
    };

    private SKPaint? _armAccentPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        Color = new SKColor(118, 126, 138)
    };

    private SKPaint? _armHeadPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(46, 48, 56)
    };

    private SKPaint? _armPivotPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(54, 57, 65)
    };

    private SKPaint? _armPivotInnerPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(210, 214, 220)
    };

    private SKPaint? _armStylusPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = new SKColor(225, 94, 94)
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
        var discLayout = PlayerCompositionArmMetrics.GetDiscLayout(new Size(_visualSize.X, _visualSize.Y));
        var centerX = (float)discLayout.Center.X;
        var centerY = (float)discLayout.Center.Y;
        var size = (float)discLayout.DiscDiameter;
        var ringRadius = (float)discLayout.RingRadius;
        var nowTicks = DateTime.UtcNow.Ticks;
        var shouldContinueAnimating = UpdateAnimations(nowTicks);

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

        DrawArm(canvas);

        if (shouldContinueAnimating)
            Invalidate();
    }

    private bool UpdateAnimations(long nowTicks)
    {
        var deltaSeconds = _lastAnimationTicks == 0
            ? (1.0 / 60.0)
            : (nowTicks - _lastAnimationTicks) / (double)TimeSpan.TicksPerSecond;

        _lastAnimationTicks = nowTicks;

        var needsAnotherFrame = false;

        if (_rotate && _isPlaying)
        {
            if (_lastRotationTicks != 0)
            {
                var rotationDeltaSeconds = (nowTicks - _lastRotationTicks) / (double)TimeSpan.TicksPerSecond;
                _rotationAngle = (float)((_rotationAngle + rotationDeltaSeconds * 30.0) % 360.0);
            }

            _lastRotationTicks = nowTicks;
            needsAnotherFrame = true;
        }
        else
        {
            _lastRotationTicks = 0;
        }

        var targetArmAngle = _isPlaying
            ? (float)PlayerCompositionArmMetrics.PlayAngleDegrees
            : (float)PlayerCompositionArmMetrics.RestAngleDegrees;

        var angleDelta = targetArmAngle - _armAngleDegrees;
        if (Math.Abs(angleDelta) > 0.15f)
        {
            var maxStep = (float)Math.Max(1.0, deltaSeconds * 220.0);
            if (Math.Abs(angleDelta) <= maxStep)
            {
                _armAngleDegrees = targetArmAngle;
            }
            else
            {
                _armAngleDegrees += MathF.Sign(angleDelta) * maxStep;
                needsAnotherFrame = true;
            }
        }
        else
        {
            _armAngleDegrees = targetArmAngle;
        }

        return needsAnotherFrame;
    }

    private void DrawArm(SKCanvas canvas)
    {
        if (_armMetalPaint == null
            || _armHeadPaint == null
            || _armPivotPaint == null
            || _armPivotInnerPaint == null)
        {
            return;
        }

        var layout = PlayerCompositionArmMetrics.GetLayout(new Size(_visualSize.X, _visualSize.Y), _armAngleDegrees);
        var pivot = new SKPoint((float)layout.Pivot.X, (float)layout.Pivot.Y);
        var elbow = new SKPoint((float)layout.Elbow.X, (float)layout.Elbow.Y);
        var headshellStart = new SKPoint((float)layout.HeadshellStart.X, (float)layout.HeadshellStart.Y);
        var headshellEnd = new SKPoint((float)layout.HeadshellEnd.X, (float)layout.HeadshellEnd.Y);
        var counterweightEnd = new SKPoint((float)layout.CounterweightEnd.X, (float)layout.CounterweightEnd.Y);

        var borderColor = _ringPaint?.Color ?? SKColors.White;
        var armColor = borderColor.Alpha > 0 ? borderColor.WithAlpha(255) : SKColors.White;

        _armMetalPaint.Style = SKPaintStyle.Stroke;
        _armMetalPaint.StrokeCap = SKStrokeCap.Round;
        _armMetalPaint.StrokeJoin = SKStrokeJoin.Round;
        _armMetalPaint.StrokeWidth = (float)layout.TubeThickness;
        _armMetalPaint.Color = armColor;

        _armHeadPaint.Style = SKPaintStyle.Fill;
        _armHeadPaint.Color = armColor;

        _armPivotPaint.Style = SKPaintStyle.Stroke;
        _armPivotPaint.StrokeWidth = Math.Max(3f, (float)layout.PivotRadius * 0.16f);
        _armPivotPaint.Color = armColor;

        _armPivotInnerPaint.Style = SKPaintStyle.Fill;
        _armPivotInnerPaint.Color = armColor;

        canvas.DrawLine(pivot, elbow, _armMetalPaint);

        using var headshellConnectorPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = (float)layout.TubeThickness,
            Color = armColor
        };
        var connectorTarget = new SKPoint(
            headshellStart.X + (headshellEnd.X - headshellStart.X) * 0.78f,
            headshellStart.Y + (headshellEnd.Y - headshellStart.Y) * 0.7f);
        canvas.DrawLine(elbow, connectorTarget, headshellConnectorPaint);

        using var elbowBlendPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = armColor
        };
        canvas.DrawCircle(elbow, Math.Max(1.6f, (float)layout.TubeThickness * 0.42f), elbowBlendPaint);

        var hsDir = new SKPoint(headshellEnd.X - headshellStart.X, headshellEnd.Y - headshellStart.Y);
        var hsLen = MathF.Max(1f, MathF.Sqrt(hsDir.X * hsDir.X + hsDir.Y * hsDir.Y));
        var hsUx = hsDir.X / hsLen;
        var hsUy = hsDir.Y / hsLen;
        var hsPx = -hsUy;
        var hsPy = hsUx;

        var headHalfLen = Math.Max(8f, (float)layout.HeadshellThickness * 1.9f);
        var headHalfWid = Math.Max(4.6f, (float)layout.HeadshellThickness * 1.22f);
        var headCenter = new SKPoint(
            headshellStart.X + hsUx * MathF.Max(headHalfLen * 1.55f, hsLen * 1.05f),
            headshellStart.Y + hsUy * MathF.Max(headHalfLen * 1.55f, hsLen * 1.05f));

        using var headshellPath = new SKPath();
        headshellPath.MoveTo(headCenter.X - hsUx * headHalfLen - hsPx * headHalfWid, headCenter.Y - hsUy * headHalfLen - hsPy * headHalfWid);
        headshellPath.LineTo(headCenter.X + hsUx * headHalfLen - hsPx * headHalfWid, headCenter.Y + hsUy * headHalfLen - hsPy * headHalfWid);
        headshellPath.LineTo(headCenter.X + hsUx * headHalfLen + hsPx * headHalfWid, headCenter.Y + hsUy * headHalfLen + hsPy * headHalfWid);
        headshellPath.LineTo(headCenter.X - hsUx * headHalfLen + hsPx * headHalfWid, headCenter.Y - hsUy * headHalfLen + hsPy * headHalfWid);
        headshellPath.Close();
        canvas.DrawPath(headshellPath, _armHeadPaint);

        var counterweightMid = new SKPoint(
            (pivot.X + counterweightEnd.X) * 0.5f,
            (pivot.Y + counterweightEnd.Y) * 0.5f);
        var counterweightDir = new SKPoint(counterweightEnd.X - pivot.X, counterweightEnd.Y - pivot.Y);
        var counterweightLen = MathF.Max(1f, MathF.Sqrt(counterweightDir.X * counterweightDir.X + counterweightDir.Y * counterweightDir.Y));
        var ux = counterweightDir.X / counterweightLen;
        var uy = counterweightDir.Y / counterweightLen;
        var px = -uy;
        var py = ux;

        var halfLen = (float)Math.Max(3.0, layout.CounterweightLength * 0.45);
        var halfWid = (float)(layout.CounterweightWidth * 0.5);
        using var counterweightPath = new SKPath();
        counterweightPath.MoveTo(counterweightMid.X - ux * halfLen - px * halfWid, counterweightMid.Y - uy * halfLen - py * halfWid);
        counterweightPath.LineTo(counterweightMid.X + ux * halfLen - px * halfWid, counterweightMid.Y + uy * halfLen - py * halfWid);
        counterweightPath.LineTo(counterweightMid.X + ux * halfLen + px * halfWid, counterweightMid.Y + uy * halfLen + py * halfWid);
        counterweightPath.LineTo(counterweightMid.X - ux * halfLen + px * halfWid, counterweightMid.Y - uy * halfLen + py * halfWid);
        counterweightPath.Close();

        using var counterweightPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = armColor
        };
        canvas.DrawPath(counterweightPath, counterweightPaint);

        canvas.DrawCircle(pivot, (float)layout.PivotRadius, _armPivotPaint);
        canvas.DrawCircle(pivot, (float)layout.PivotRadius * 0.48f, _armPivotInnerPaint);
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
        _armShadowPaint?.Dispose();
        _armShadowPaint = null;
        _armMetalPaint?.Dispose();
        _armMetalPaint = null;
        _armAccentPaint?.Dispose();
        _armAccentPaint = null;
        _armHeadPaint?.Dispose();
        _armHeadPaint = null;
        _armPivotPaint?.Dispose();
        _armPivotPaint = null;
        _armPivotInnerPaint?.Dispose();
        _armPivotInnerPaint = null;
        _armStylusPaint?.Dispose();
        _armStylusPaint = null;
        _circleBackgroundBitmap?.Dispose();
        _circleBackgroundBitmap = null;
    }
}







