using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;
using System.Globalization;
using System.Numerics;

namespace AES_Controls.Composition;

public class CompositionWaveformProgressBarVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;
    private float[] _waveformSamples = [];
    private double _progress;

    private SKColor _playedColor = SKColor.Parse("#4169E1");
    private SKColor _unplayedColor = SKColor.Parse("#696969");
    private SKColor _indicatorColor = SKColors.White;
    private SKColor _triangleColor = SKColors.White;
    private SKColor _textColor = SKColors.White;
    private SKColor _loadingColor = SKColors.White;
    private SKColor _borderColor = SKColors.Gray;
    private SKColor _backgroundColor = SKColors.Transparent;

    private float _waveformVerticalOffset = 5f;
    private float _barGap;
    private float _blockHeight;
    private float _verticalGap = 1f;
    private int _visualBarCount;
    private float _marginLeft;
    private float _marginRight;
    private float _marginTop;
    private float _marginBottom;

    private bool _isSymmetric;
    private bool _showReflection;
    private bool _isTriangleUpwards;
    private bool _isDigital;
    private float _triangleOffset = 2f;
    private float _triangleWidth = 14f;
    private float _triangleHeight = 12f;
    private float _textSize = 12f;
    private float _loadingIndicatorSize = 30f;
    private float _borderThickness;

    private SKColor[]? _gradientColors;
    private float[]? _gradientOffsets;

    private float? _hoverX;
    private float? _hoverY;
    private double _minimum;
    private double _maximum = 1.0;
    private bool _isLoading;
    private double _loadingAngle;
    private float _topExtension;

    private SKBitmap? _unplayedCache;
    private SKBitmap? _playedCache;
    private int _cacheWidth;
    private int _cacheHeight;
    private float[]? _digitalBarHeights;
    private int _digitalBarCount;
    private readonly Dictionary<uint, SKColor> _gradientColorCache = new();

    private readonly SKPaint _paint = new() { IsAntialias = true };

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case Vector2 size:
                _visualSize = size;
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformDataMessage data:
                _waveformSamples = data.Samples ?? [];
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformProgressMessage progress:
                _progress = progress.Progress;
                Invalidate();
                break;
            case InstantSliderPositionMessage isp:
                _progress = isp.Value;
                Invalidate();
                break;
            case WaveformColorsMessage colors:
                _playedColor = colors.Played;
                _unplayedColor = colors.Unplayed;
                _indicatorColor = colors.Indicator;
                _triangleColor = colors.Triangle;
                _textColor = colors.Text;
                _loadingColor = colors.Loading;
                _borderColor = colors.Border;
                _backgroundColor = colors.Background;
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformLayoutMessage layout:
                _waveformVerticalOffset = layout.WaveformVerticalOffset;
                _barGap = layout.BarGap;
                _blockHeight = layout.BlockHeight;
                _verticalGap = layout.VerticalGap;
                _visualBarCount = layout.VisualBarCount;
                _marginLeft = layout.MarginLeft;
                _marginRight = layout.MarginRight;
                _marginTop = layout.MarginTop;
                _marginBottom = layout.MarginBottom;
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformStyleMessage style:
                _isSymmetric = style.IsSymmetric;
                _showReflection = style.ShowReflection;
                _isTriangleUpwards = style.IsTriangleUpwards;
                _isDigital = style.IsDigital;
                _triangleOffset = style.TriangleOffset;
                _triangleWidth = style.TriangleWidth;
                _triangleHeight = style.TriangleHeight;
                _textSize = style.TextSize;
                _loadingIndicatorSize = style.LoadingIndicatorSize;
                _borderThickness = style.BorderThickness;
                _topExtension = ComputeTopExtension(_isTriangleUpwards, _triangleOffset, _triangleHeight);
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformGradientMessage gradient:
                _gradientColors = gradient.Colors;
                _gradientOffsets = gradient.Offsets;
                _gradientColorCache.Clear();
                InvalidateWaveformCaches();
                Invalidate();
                break;
            case WaveformHoverMessage hover:
                _hoverX = hover.X;
                _hoverY = hover.Y;
                Invalidate();
                break;
            case WaveformRangeMessage range:
                _minimum = range.Minimum;
                _maximum = range.Maximum;
                Invalidate();
                break;
            case WaveformLoadingMessage loading:
                _isLoading = loading.IsLoading;
                _loadingAngle = loading.Angle;
                Invalidate();
                break;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        Draw(lease.SkCanvas);
    }

    private void InvalidateWaveformCaches()
    {
        _unplayedCache?.Dispose();
        _playedCache?.Dispose();
        _unplayedCache = null;
        _playedCache = null;
        _cacheWidth = 0;
        _cacheHeight = 0;
        _digitalBarHeights = null;
        _digitalBarCount = 0;
    }

    private void EnsureWaveformCaches()
    {
        if (_waveformSamples.Length == 0 || _visualSize.X <= 1f || _visualSize.Y <= 1f)
            return;

        int pxW = Math.Max(1, (int)Math.Round(_visualSize.X));
        int pxH = Math.Max(1, (int)Math.Round(_visualSize.Y));

        if (_unplayedCache != null && _cacheWidth == pxW && _cacheHeight == pxH)
            return;

        InvalidateWaveformCaches();

        float width = _visualSize.X - _marginLeft - _marginRight;
        float height = _visualSize.Y - _marginTop - _marginBottom;
        float offset = _waveformVerticalOffset + _marginTop;
        float availableHeight = Math.Max(0f, height - _waveformVerticalOffset);

        if (width <= 0f || availableHeight <= 0f)
            return;

        if (_isDigital)
            RebuildDigitalBarHeights(width, availableHeight);

        _unplayedCache = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(_unplayedCache))
        {
            canvas.Clear(SKColors.Transparent);
            if (_isDigital)
                DrawDigitalSection(canvas, _unplayedColor, 0f, 1f, width, availableHeight, offset, useGradientColors: false);
            else
                DrawWaveformSection(canvas, _unplayedColor, 0f, 1f, width, availableHeight, offset, useGradient: false);
        }

        _playedCache = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(_playedCache))
        {
            canvas.Clear(SKColors.Transparent);
            if (_isDigital)
                DrawDigitalSection(canvas, _playedColor, 0f, 1f, width, availableHeight, offset, useGradientColors: true);
            else
                DrawWaveformSection(canvas, _playedColor, 0f, 1f, width, availableHeight, offset, useGradient: _gradientColors != null);
        }

        _cacheWidth = pxW;
        _cacheHeight = pxH;
    }

    private void Draw(SKCanvas canvas)
    {
        if (_visualSize.X <= 1f || _visualSize.Y <= 1f)
            return;

        float width = _visualSize.X;
        float height = _visualSize.Y;

        canvas.Save();
        canvas.Translate(0, _topExtension);

        if (_backgroundColor.Alpha > 0)
        {
            _paint.Style = SKPaintStyle.Fill;
            _paint.Shader = null;
            _paint.Color = _backgroundColor;
            canvas.DrawRect(0, 0, width, height, _paint);
        }

        if (_borderThickness > 0f && _borderColor.Alpha > 0)
        {
            _paint.Style = SKPaintStyle.Stroke;
            _paint.StrokeWidth = _borderThickness;
            _paint.Shader = null;
            _paint.Color = _borderColor;
            var inset = _borderThickness / 2f;
            canvas.DrawRect(inset, inset, width - _borderThickness, height - _borderThickness, _paint);
        }

        EnsureWaveformCaches();

        if (_unplayedCache != null)
        {
            canvas.DrawBitmap(_unplayedCache, 0, 0);

            if (_playedCache != null)
            {
                float progress = (float)Math.Clamp(_progress, 0.0, 1.0);
                float clipX = _marginLeft + progress * Math.Max(1f, width - _marginLeft - _marginRight);

                canvas.Save();
                canvas.ClipRect(new SKRect(0, 0, clipX, height));
                canvas.DrawBitmap(_playedCache, 0, 0);
                canvas.Restore();
            }
        }

        float indicatorX = (float)Math.Round(_marginLeft + _progress * Math.Max(1.0, width - _marginLeft - _marginRight));
        _paint.Style = SKPaintStyle.Fill;
        _paint.Shader = null;
        _paint.Color = _indicatorColor;
        canvas.DrawRect(indicatorX - 1f, 0, 2f, height, _paint);

        if (_hoverX.HasValue && _hoverY.HasValue)
            DrawTooltip(canvas, width, _hoverX.Value, _hoverY.Value);

        if (_isLoading)
            DrawLoadingIndicator(canvas, width, height);

        canvas.Restore();

        DrawTriangle(canvas, indicatorX, height);
    }

    private static float ComputeTopExtension(bool isTriangleUpwards, float triangleOffset, float triangleHeight)
    {
        if (isTriangleUpwards)
            return 0f;

        float topY = Math.Min(triangleOffset, triangleOffset + triangleHeight);
        return Math.Max(0f, -topY);
    }

    private float ToLocalY(float controlY) => controlY + _topExtension;

    private int GetDigitalBarCount(float width)
    {
        if (_visualBarCount > 0)
            return _visualBarCount;

        return (int)Math.Clamp(Math.Round(width / 2.75), 120, 260);
    }

    private void RebuildDigitalBarHeights(float width, float availableHeight)
    {
        _digitalBarCount = GetDigitalBarCount(width);
        _digitalBarHeights = new float[_digitalBarCount];

        if (_waveformSamples.Length == 0)
            return;

        float maxVal = 0f;
        for (int i = 0; i < _digitalBarCount; i++)
        {
            float sample = GetWaveformValue(i, _digitalBarCount);
            _digitalBarHeights[i] = sample;
            if (sample > maxVal)
                maxVal = sample;
        }

        if (maxVal < 1e-5f)
            maxVal = 1f;

        for (int i = 0; i < _digitalBarCount; i++)
        {
            float norm = _digitalBarHeights[i] / maxVal;
            norm = MathF.Pow(norm, 0.82f);
            float barHeight = norm * availableHeight * 0.96f;
            if (barHeight < 2f)
                barHeight = 2f;
            _digitalBarHeights[i] = barHeight;
        }
    }

    private void DrawDigitalSection(
        SKCanvas canvas,
        SKColor fallbackColor,
        float startRatio,
        float endRatio,
        float width,
        float availableHeight,
        float offset,
        bool useGradientColors)
    {
        if (_digitalBarHeights == null || _digitalBarCount <= 0)
            return;

        float gap = _barGap > 0f ? _barGap : 1.25f;
        float slotWidth = width / _digitalBarCount;
        float canvasHeight = _visualSize.Y;
        float centerY = (canvasHeight - _marginBottom + _marginTop + offset) / 2f;

        int startIndex = (int)(startRatio * _digitalBarCount);
        int endIndex = (int)Math.Ceiling(endRatio * _digitalBarCount);

        _paint.Style = SKPaintStyle.Fill;

        using SKShader? gradientShader = useGradientColors ? CreateHorizontalGradientShader(_marginLeft, _marginLeft + width) : null;

        for (int i = startIndex; i < endIndex; i++)
        {
            float barHeight = _digitalBarHeights[i];
            float drawX = _marginLeft + (i * slotWidth) + (gap * 0.5f);
            float barWidth = Math.Max(1.25f, slotWidth - gap);
            float top = centerY - (barHeight * 0.5f);
            var rect = new SKRect(drawX, top, drawX + barWidth, top + barHeight);
            float radius = Math.Min(barWidth * 0.5f, barHeight * 0.5f);

            if (gradientShader != null)
                DrawDigitalCapsule(canvas, rect, radius, gradientShader);
            else if (useGradientColors)
                DrawDigitalCapsule(canvas, rect, radius, fallbackColor);
            else
            {
                _paint.Shader = null;
                _paint.MaskFilter = null;
                _paint.Color = fallbackColor;
                canvas.DrawRoundRect(rect, radius, radius, _paint);
            }
        }
    }

    private SKShader? CreateHorizontalGradientShader(float left, float right)
    {
        if (_gradientColors == null || _gradientOffsets == null || _gradientColors.Length == 0)
            return null;

        return SKShader.CreateLinearGradient(
            new SKPoint(left, 0),
            new SKPoint(right, 0),
            _gradientColors,
            _gradientOffsets,
            SKShaderTileMode.Clamp);
    }

    private void DrawDigitalCapsule(SKCanvas canvas, SKRect rect, float radius, SKShader gradientShader)
    {
        float glowRadius = Math.Max(0.75f, radius * 0.35f);
        _paint.Shader = gradientShader;
        _paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowRadius);
        _paint.Color = SKColors.White.WithAlpha(70);
        canvas.DrawRoundRect(rect, radius, radius, _paint);

        _paint.MaskFilter = null;
        _paint.Color = SKColors.White;
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.Shader = null;
    }

    private void DrawDigitalCapsule(SKCanvas canvas, SKRect rect, float radius, SKColor color)
    {
        float glowRadius = Math.Max(0.75f, radius * 0.35f);
        _paint.Shader = null;
        _paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowRadius);
        _paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * 0.4f, 20, 100));
        canvas.DrawRoundRect(rect, radius, radius, _paint);
        _paint.MaskFilter = null;
        _paint.Color = color;
        canvas.DrawRoundRect(rect, radius, radius, _paint);
    }

    private void DrawWaveformSection(
        SKCanvas canvas,
        SKColor color,
        float startRatio,
        float endRatio,
        float width,
        float availableHeight,
        float offset,
        bool useGradient)
    {
        if (_waveformSamples.Length == 0)
            return;

        int visualCount = _visualBarCount > 0 ? _visualBarCount : _waveformSamples.Length;
        float barWidthRaw = width / visualCount;
        float canvasHeight = _visualSize.Y;
        float centerY = (canvasHeight - _marginBottom + _marginTop + offset) / 2f;
        float bottom = canvasHeight - _marginBottom;

        int startIndex = (int)(startRatio * visualCount);
        int endIndex = (int)(endRatio * visualCount);

        _paint.Style = SKPaintStyle.Fill;
        _paint.Shader = null;

        for (int i = startIndex; i < endIndex; i++)
        {
            float val = GetWaveformValue(i, visualCount);
            float barHeightTotal = Math.Clamp(val * availableHeight, 0f, availableHeight);
            if (barHeightTotal < 1.5f)
                barHeightTotal = 1.5f;

            var (drawX, w) = CalculateBarX(i, visualCount, barWidthRaw, _barGap, width);
            if (w <= 0f)
                continue;

            if (_blockHeight > 0f)
            {
                if (barHeightTotal < _blockHeight)
                {
                    float lineY = _isSymmetric ? centerY - 0.75f : bottom - 1.5f;
                    _paint.Color = color;
                    canvas.DrawRect(drawX, lineY, w, 1.5f, _paint);
                    continue;
                }

                float startY = _isSymmetric ? centerY + (barHeightTotal / 2f) : bottom;
                float currentY = startY;
                float limitY = _isSymmetric ? centerY - (barHeightTotal / 2f) : offset;

                while (currentY > limitY + 0.1f && (startY - currentY + _blockHeight) <= barHeightTotal)
                {
                    float blockTop = Math.Max(limitY, currentY - _blockHeight);
                    float actualBlockH = currentY - blockTop;

                    if (useGradient)
                        _paint.Color = GetGradientColor(1f - (blockTop - offset) / availableHeight);
                    else
                        _paint.Color = color;

                    canvas.DrawRect(drawX, blockTop, w, actualBlockH, _paint);

                    if (_showReflection && !_isSymmetric)
                    {
                        float reflOpacity = 0.3f * (1f - (bottom - currentY) / availableHeight);
                        _paint.Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * reflOpacity, 0, 255));
                        canvas.DrawRect(drawX, bottom + (bottom - blockTop), w, actualBlockH, _paint);
                    }

                    currentY -= (_blockHeight + _verticalGap);
                }
            }
            else
            {
                float y = _isSymmetric ? centerY - (barHeightTotal / 2f) : bottom - barHeightTotal;

                if (useGradient && _gradientColors != null && _gradientOffsets != null)
                {
                    using var shader = SKShader.CreateLinearGradient(
                        new SKPoint(drawX, y),
                        new SKPoint(drawX, y + barHeightTotal),
                        _gradientColors,
                        _gradientOffsets,
                        SKShaderTileMode.Clamp);
                    _paint.Shader = shader;
                    _paint.Color = SKColors.White;
                }
                else
                {
                    _paint.Shader = null;
                    _paint.Color = color;
                }

                canvas.DrawRect(drawX, y, w, barHeightTotal, _paint);
                _paint.Shader = null;
            }
        }
    }

    private float GetWaveformValue(int i, int visualCount)
    {
        if (_waveformSamples.Length == 0)
            return 0f;

        if (_visualBarCount > 0)
        {
            double dataPerBar = (double)_waveformSamples.Length / visualCount;
            if (dataPerBar >= 1.0)
            {
                float val = 0f;
                int dataStart = (int)(i * dataPerBar);
                int dataEnd = (int)((i + 1) * dataPerBar);
                for (int d = dataStart; d < dataEnd && d < _waveformSamples.Length; d++)
                    if (_waveformSamples[d] > val)
                        val = _waveformSamples[d];
                return val;
            }

            int index = (int)(i * dataPerBar);
            return index < _waveformSamples.Length ? _waveformSamples[index] : 0f;
        }

        return i < _waveformSamples.Length ? _waveformSamples[i] : 0f;
    }

    private (float drawX, float w) CalculateBarX(int i, int visualCount, float barWidthRaw, float hGap, float width)
    {
        float left = _marginLeft;
        float x1 = left + i * barWidthRaw;
        float x2 = i == visualCount - 1 ? left + width : left + (i + 1) * barWidthRaw;

        int p1 = (int)Math.Round(x1, MidpointRounding.AwayFromZero);
        int p2 = (int)Math.Round(x2, MidpointRounding.AwayFromZero);

        int drawX = p1 + (int)Math.Floor(hGap / 2f);
        int drawEnd = p2 - (int)Math.Ceiling(hGap / 2f);

        if (drawEnd <= drawX && p2 > p1)
            drawEnd = drawX + 1;

        return (drawX, Math.Max(0, drawEnd - drawX));
    }

    private SKColor GetGradientColor(double offset)
    {
        if (_gradientColors == null || _gradientOffsets == null || _gradientColors.Length == 0)
            return SKColors.White;

        var key = (uint)(offset * 10000);
        if (_gradientColorCache.TryGetValue(key, out var cached))
            return cached;

        SKColor targetColor;
        if (_gradientColors.Length == 1)
        {
            targetColor = _gradientColors[0];
        }
        else
        {
            int leftIndex = 0;
            for (int i = _gradientOffsets.Length - 1; i >= 0; i--)
            {
                if (_gradientOffsets[i] <= offset)
                {
                    leftIndex = i;
                    break;
                }
            }

            int rightIndex = leftIndex;
            for (int i = 0; i < _gradientOffsets.Length; i++)
            {
                if (_gradientOffsets[i] > offset)
                {
                    rightIndex = i;
                    break;
                }
            }

            if (leftIndex == rightIndex)
            {
                targetColor = _gradientColors[leftIndex];
            }
            else
            {
                float leftOffset = _gradientOffsets[leftIndex];
                float rightOffset = _gradientOffsets[rightIndex];
                float t = rightOffset > leftOffset
                    ? (float)((offset - leftOffset) / (rightOffset - leftOffset))
                    : 0f;
                targetColor = LerpColor(_gradientColors[leftIndex], _gradientColors[rightIndex], t);
            }
        }

        _gradientColorCache[key] = targetColor;
        return targetColor;
    }

    private static SKColor LerpColor(SKColor left, SKColor right, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(left.Red + t * (right.Red - left.Red)),
            (byte)(left.Green + t * (right.Green - left.Green)),
            (byte)(left.Blue + t * (right.Blue - left.Blue)),
            (byte)(left.Alpha + t * (right.Alpha - left.Alpha)));
    }

    private void DrawTriangle(SKCanvas canvas, float x, float height)
    {
        using var path = new SKPath();
        float halfWidth = _triangleWidth / 2f;

        if (_isTriangleUpwards)
        {
            float baseY = ToLocalY(height - _triangleOffset);
            path.MoveTo(x, baseY - _triangleHeight);
            path.LineTo(x - halfWidth, baseY);
            path.LineTo(x + halfWidth, baseY);
        }
        else
        {
            float baseY = ToLocalY(_triangleOffset);
            path.MoveTo(x, baseY + _triangleHeight);
            path.LineTo(x - halfWidth, baseY);
            path.LineTo(x + halfWidth, baseY);
        }

        path.Close();
        _paint.Style = SKPaintStyle.Fill;
        _paint.Shader = null;
        _paint.Color = _triangleColor;
        canvas.DrawPath(path, _paint);
    }

    private void DrawTooltip(SKCanvas canvas, float width, float hoverX, float hoverY)
    {
        float ratio = Math.Clamp((hoverX - _marginLeft) / Math.Max(1f, width - _marginLeft - _marginRight), 0f, 1f);
        double range = Math.Max(0.0, _maximum - _minimum);
        var time = TimeSpan.FromSeconds(_minimum + ratio * range);
        var text = time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        _paint.TextSize = _textSize;
        CompositionSkiaTextHelper.ConfigurePaint(_paint);
        _paint.Color = _textColor;

        float y = Math.Max(0f, hoverY - _textSize - 5f);
        CompositionSkiaTextHelper.DrawText(canvas, text, hoverX, y + _textSize, _paint);
    }

    private void DrawLoadingIndicator(SKCanvas canvas, float width, float height)
    {
        float size = _loadingIndicatorSize;
        float centerX = width / 2f;
        float centerY = height / 2f;
        float radius = size / 2f;
        const int segments = 12;

        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = 3f;
        _paint.StrokeCap = SKStrokeCap.Round;
        _paint.Shader = null;

        for (int i = 0; i < segments; i++)
        {
            double angle = (_loadingAngle + i * (360.0 / segments)) % 360.0;
            double rad = angle * Math.PI / 180.0;
            float inner = radius * 0.5f;
            var p1 = new SKPoint(centerX + (float)Math.Cos(rad) * inner, centerY + (float)Math.Sin(rad) * inner);
            var p2 = new SKPoint(centerX + (float)Math.Cos(rad) * radius, centerY + (float)Math.Sin(rad) * radius);
            _paint.Color = _loadingColor.WithAlpha((byte)((i + 1) * 255 / segments));
            canvas.DrawLine(p1, p2, _paint);
        }
    }
}
