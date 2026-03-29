using System.Diagnostics;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal sealed record EdgeBorderConfigMessage(
    float BorderThickness,
    float CornerRadius,
    float GlowSize,
    float Speed,
    float HighlightLength,
    float OutsideOffset,
    float BaseOpacity,
    SKColor BaseStartColor,
    SKColor BaseEndColor,
    SKColor HighlightLeadColor,
    SKColor HighlightTailColor);

public sealed class EdgeBorderCompositionVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;
    private float _borderThickness = 1.5f;
    private float _cornerRadius = 10f;
    private float _glowSize = 12f;
    private float _speed = 0.085f;
    private float _highlightLength = 0.18f;
    private float _outsideOffset = 3f;
    private float _baseOpacity = 0.42f;
    private SKColor _baseStartColor = new(0x14, 0xD4, 0xFF);
    private SKColor _baseEndColor = new(0x6A, 0x38, 0xFF);
    private SKColor _highlightLeadColor = new(0x19, 0xF0, 0xFF);
    private SKColor _highlightTailColor = new(0xA2, 0x4B, 0xFF);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private SKPath? _borderPath;
    private SKRect _pathRect;
    private float _pathLength;
    private bool _pathDirty = true;

    private readonly SKPaint _basePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _glowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _segmentPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case Vector2 size:
                _visualSize = size;
                _pathDirty = true;
                Invalidate();
                return;
            case EdgeBorderConfigMessage config:
                _borderThickness = Math.Max(0.5f, config.BorderThickness);
                _cornerRadius = Math.Max(0f, config.CornerRadius);
                _glowSize = Math.Max(0f, config.GlowSize);
                _speed = Math.Max(0f, config.Speed);
                _highlightLength = Math.Clamp(config.HighlightLength, 0.04f, 0.65f);
                _outsideOffset = Math.Max(0f, config.OutsideOffset);
                _baseOpacity = Math.Clamp(config.BaseOpacity, 0f, 1f);
                _baseStartColor = config.BaseStartColor;
                _baseEndColor = config.BaseEndColor;
                _highlightLeadColor = config.HighlightLeadColor;
                _highlightTailColor = config.HighlightTailColor;
                _pathDirty = true;
                Invalidate();
                return;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        Invalidate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
        {
            RegisterForNextAnimationFrameUpdate();
            return;
        }

        EnsurePath();
        if (_borderPath == null || _pathLength <= 1f)
        {
            RegisterForNextAnimationFrameUpdate();
            return;
        }

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
        {
            RegisterForNextAnimationFrameUpdate();
            return;
        }

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        ConfigureBasePaint();
        canvas.DrawPath(_borderPath, _basePaint);

        DrawAnimatedSegment(canvas);
        RegisterForNextAnimationFrameUpdate();
    }

    private void DrawAnimatedSegment(SKCanvas canvas)
    {
        if (_pathLength <= 1f)
            return;

        float normalizedHead = (float)((_stopwatch.Elapsed.TotalSeconds * _speed) % 1.0);
        float headDistance = normalizedHead * _pathLength;
        float segmentLength = Math.Max(18f, _pathLength * _highlightLength);
        float colorPhase = 0.5f + (0.5f * MathF.Sin((float)(_stopwatch.Elapsed.TotalSeconds * 2.35)));
        float pulse = 0.5f + (0.5f * MathF.Sin((float)(_stopwatch.Elapsed.TotalSeconds * 6.0)));
        var segmentColor = Lerp(_highlightLeadColor, _highlightTailColor, colorPhase).WithAlpha(245);
        var bodyColor = Lerp(_highlightLeadColor, _highlightTailColor, 0.25f + (0.15f * pulse)).WithAlpha(220);
        var headColor = Lerp(segmentColor, SKColors.White, 0.42f + (0.18f * pulse)).WithAlpha(255);
        var outerColor = Lerp(_highlightLeadColor, _highlightTailColor, 0.8f).WithAlpha(170);
        float tailDistance = headDistance - segmentLength;

        using var segment = ExtractSegment(tailDistance, headDistance);
        if (segment == null || segment.IsEmpty)
            return;

        if (TryGetSegmentEndpoints(tailDistance, headDistance, out var tailPoint, out var headPoint))
        {
            var glowColors = new[]
            {
                bodyColor.WithAlpha(0),
                bodyColor.WithAlpha(90),
                outerColor.WithAlpha(185),
                headColor.WithAlpha(255)
            };
            var coreColors = new[]
            {
                bodyColor.WithAlpha(0),
                bodyColor.WithAlpha(70),
                segmentColor.WithAlpha(235),
                headColor.WithAlpha(255)
            };
            var fadeStops = new[] { 0f, 0.55f, 0.86f, 1f };

            _glowPaint.StrokeWidth = _borderThickness + (_glowSize * 0.26f);
            _glowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1.1f, _glowSize * 0.18f));
            _glowPaint.Shader = SKShader.CreateLinearGradient(tailPoint, headPoint, glowColors, fadeStops, SKShaderTileMode.Clamp);
            canvas.DrawPath(segment, _glowPaint);

            _segmentPaint.StrokeWidth = Math.Max(0.72f, _borderThickness * 0.86f);
            _segmentPaint.MaskFilter = null;
            _segmentPaint.Shader = SKShader.CreateLinearGradient(
                tailPoint,
                headPoint,
                coreColors,
                fadeStops,
                SKShaderTileMode.Clamp);
            canvas.DrawPath(segment, _segmentPaint);

            using var hotHead = ExtractSegment(headDistance - Math.Max(10f, segmentLength * 0.08f), headDistance);
            if (hotHead != null && !hotHead.IsEmpty)
            {
                _segmentPaint.StrokeWidth = Math.Max(0.7f, _borderThickness * 0.72f);
                _segmentPaint.Shader = null;
                _segmentPaint.Color = headColor;
                canvas.DrawPath(hotHead, _segmentPaint);
            }

            _glowPaint.Shader = null;
            _segmentPaint.Shader = null;
        }
    }

    private void ConfigureBasePaint()
    {
        _basePaint.StrokeWidth = _borderThickness;
        _basePaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, Math.Max(0.65f, _glowSize * 0.09f));
        _basePaint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(_pathRect.Left, _pathRect.Top),
            new SKPoint(_pathRect.Right, _pathRect.Bottom),
            [
                _baseStartColor.WithAlpha((byte)(_baseStartColor.Alpha * _baseOpacity)),
                _baseEndColor.WithAlpha((byte)(_baseEndColor.Alpha * _baseOpacity)),
                _baseStartColor.WithAlpha((byte)(_baseStartColor.Alpha * (_baseOpacity * 0.8f)))
            ],
            [0f, 0.58f, 1f],
            SKShaderTileMode.Clamp);
    }

    private void EnsurePath()
    {
        if (!_pathDirty && _borderPath != null)
            return;

        _borderPath?.Dispose();
        _borderPath = null;
        _pathLength = 0f;

        float left;
        float top;
        float right;
        float bottom;

        if (_outsideOffset <= 0.001f)
        {
            float inset = Math.Max(1f, _borderThickness * 0.5f + 1f);
            left = inset;
            top = inset;
            right = _visualSize.X - inset;
            bottom = _visualSize.Y - inset;
        }
        else
        {
            float outside = _outsideOffset + (_borderThickness * 0.5f);
            left = -outside;
            top = -outside;
            right = _visualSize.X + outside;
            bottom = _visualSize.Y + outside;
        }

        float width = Math.Max(0f, right - left);
        float height = Math.Max(0f, bottom - top);
        if (width <= 1f || height <= 1f)
            return;

        _pathRect = new SKRect(left, top, right, bottom);
        float radius = _outsideOffset <= 0.001f
            ? MathF.Min(_cornerRadius, MathF.Min(width, height) * 0.5f)
            : MathF.Min(_cornerRadius + _outsideOffset + (_borderThickness * 0.5f), MathF.Min(width, height) * 0.5f);
        _borderPath = new SKPath();
        _borderPath.AddRoundRect(_pathRect, radius, radius);

        using var measure = new SKPathMeasure(_borderPath, true);
        _pathLength = measure.Length;
        _pathDirty = false;
    }

    private SKPath? ExtractSegment(float startDistance, float endDistance)
    {
        if (_borderPath == null || _pathLength <= 1f)
            return null;

        float start = NormalizeDistance(startDistance);
        float end = NormalizeDistance(endDistance);
        var result = new SKPath();

        using var measure = new SKPathMeasure(_borderPath, true);
        if (end >= start)
        {
            measure.GetSegment(start, end, result, true);
        }
        else
        {
            measure.GetSegment(start, _pathLength, result, true);
            measure.GetSegment(0, end, result, true);
        }

        return result;
    }

    private bool TryGetSegmentEndpoints(float tailDistance, float headDistance, out SKPoint tailPoint, out SKPoint headPoint)
    {
        tailPoint = default;
        headPoint = default;

        if (_borderPath == null || _pathLength <= 1f)
            return false;

        using var measure = new SKPathMeasure(_borderPath, true);
        return measure.GetPosition(NormalizeDistance(tailDistance), out tailPoint)
            && measure.GetPosition(NormalizeDistance(headDistance), out headPoint);
    }

    private float NormalizeDistance(float distance)
    {
        if (_pathLength <= 0f)
            return 0f;

        float normalized = distance % _pathLength;
        if (normalized < 0f)
            normalized += _pathLength;
        return normalized;
    }

    private static SKColor Lerp(SKColor left, SKColor right, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(left.Red + ((right.Red - left.Red) * t)),
            (byte)(left.Green + ((right.Green - left.Green) * t)),
            (byte)(left.Blue + ((right.Blue - left.Blue) * t)),
            (byte)(left.Alpha + ((right.Alpha - left.Alpha) * t)));
    }

    private void Cleanup()
    {
        _borderPath?.Dispose();
        _borderPath = null;
        _basePaint.Dispose();
        _glowPaint.Dispose();
        _segmentPaint.Dispose();
    }
}
