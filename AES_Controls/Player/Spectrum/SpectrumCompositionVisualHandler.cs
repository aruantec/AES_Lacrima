using System.Numerics;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Player.Spectrum;

internal sealed record SpectrumFrameMessage(
    float[] BarLevels,
    float[] PeakLevels,
    SKColor[] GradientColors,
    float BarWidth,
    float BarSpacing,
    float BlockHeight,
    float PeakThickness);

internal sealed record SpectrumOpacityMessage(float Opacity);

internal sealed class SpectrumCompositionVisualHandler : CompositionCustomVisualHandler
{
    private Vector2 _visualSize;
    private float[] _barLevels = [];
    private float[] _peakLevels = [];
    private SKColor[] _gradientColors =
    [
        new SKColor(0x00, 0xCC, 0xFF),
        new SKColor(0x33, 0x33, 0xFF),
        new SKColor(0xCC, 0x00, 0xCC),
        new SKColor(0xFF, 0x00, 0x4D),
        new SKColor(0xFF, 0xB3, 0x00)
    ];

    private float _barWidth = 4f;
    private float _barSpacing = 2f;
    private float _blockHeight = 8f;
    private float _peakThickness = 2f;
    private float _globalOpacity = 1f;
    private SKPaint? _barPaint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private SKPaint? _peakPaint = new() { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, 242) };

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
            case SpectrumFrameMessage frame:
                _barLevels = frame.BarLevels;
                _peakLevels = frame.PeakLevels;
                _gradientColors = frame.GradientColors.Length == 5 ? frame.GradientColors : _gradientColors;
                _barWidth = Math.Max(0f, frame.BarWidth);
                _barSpacing = Math.Max(0f, frame.BarSpacing);
                _blockHeight = Math.Max(1f, frame.BlockHeight);
                _peakThickness = Math.Max(1f, frame.PeakThickness);
                Invalidate();
                return;
            case SpectrumOpacityMessage opacity:
                _globalOpacity = Math.Clamp(opacity.Opacity, 0f, 1f);
                Invalidate();
                return;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0 || _barPaint == null || _peakPaint == null)
            return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        int count = _barLevels.Length;
        if (count == 0)
            return;

        float width = _visualSize.X;
        float height = _visualSize.Y;
        float step = width / count;

        for (int i = 0; i < count; i++)
        {
            float x0 = (i * step) + (_barSpacing * 0.5f);
            float x1 = Math.Min(width, x0 + _barWidth);
            if (x1 <= x0)
                continue;

            float barNorm = Math.Clamp(_barLevels[i], 0f, 1f);
            float peakNorm = i < _peakLevels.Length ? Math.Clamp(_peakLevels[i], 0f, 1f) : barNorm;
            float barTop = height - (barNorm * height);
            float peakTop = height - (peakNorm * height);
            SKColor color = GetGradientColor(i, count);

            DrawBar(canvas, x0, x1, barTop, height, color, height);

            float peakBottom = Math.Clamp(peakTop + _peakThickness, 0f, height);
            if (peakBottom > peakTop)
            {
                _peakPaint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)(242f * _globalOpacity), 0, 255));
                canvas.DrawRect(new SKRect(x0, Math.Clamp(peakTop, 0f, height), x1, peakBottom), _peakPaint);
            }
        }
    }

    private void DrawBar(SKCanvas canvas, float left, float right, float top, float bottom, SKColor baseColor, float totalHeight)
    {
        if (_barPaint == null || bottom <= top)
            return;

        float blockHeight = Math.Max(1f, _blockHeight);
        float gapHeight = blockHeight * 0.15f;
        float currentBottom = bottom;

        while (currentBottom > top)
        {
            float blockTop = Math.Max(top, currentBottom - blockHeight);
            float visibleBottom = Math.Max(blockTop, currentBottom - gapHeight);
            if (visibleBottom > blockTop)
            {
                float centerY = (blockTop + visibleBottom) * 0.5f;
                float v = Math.Clamp((totalHeight - centerY) / totalHeight, 0f, 1f);
                float alpha = 0.65f + (0.35f * v);
                _barPaint.Color = baseColor.WithAlpha((byte)Math.Clamp((int)(baseColor.Alpha * alpha * _globalOpacity), 0, 255));
                canvas.DrawRect(new SKRect(left, blockTop, right, visibleBottom), _barPaint);
            }

            currentBottom = blockTop;
        }
    }

    private SKColor GetGradientColor(int index, int count)
    {
        float u = count <= 1 ? 0f : index / (float)(count - 1);

        if (u < 0.25f)
            return Lerp(_gradientColors[0], _gradientColors[1], u / 0.25f);
        if (u < 0.5f)
            return Lerp(_gradientColors[1], _gradientColors[2], (u - 0.25f) / 0.25f);
        if (u < 0.75f)
            return Lerp(_gradientColors[2], _gradientColors[3], (u - 0.5f) / 0.25f);

        return Lerp(_gradientColors[3], _gradientColors[4], (u - 0.75f) / 0.25f);
    }

    private static SKColor Lerp(SKColor from, SKColor to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(from.Red + ((to.Red - from.Red) * t)),
            (byte)(from.Green + ((to.Green - from.Green) * t)),
            (byte)(from.Blue + ((to.Blue - from.Blue) * t)),
            (byte)(from.Alpha + ((to.Alpha - from.Alpha) * t)));
    }

    private void Cleanup()
    {
        _barLevels = [];
        _peakLevels = [];
        _barPaint?.Dispose();
        _peakPaint?.Dispose();
        _barPaint = null;
        _peakPaint = null;
    }
}
