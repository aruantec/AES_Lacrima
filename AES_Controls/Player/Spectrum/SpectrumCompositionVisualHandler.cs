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

/// <summary>
/// Renders spectrum bars into a fixed-size offscreen surface, then presents a single scaled blit.
/// Physics run on the UI thread in <see cref="GlSpectrumControl"/>; this handler is display-only.
/// </summary>
internal sealed class SpectrumCompositionVisualHandler : CompositionCustomVisualHandler
{
    private const int FixedRenderWidth = 960;
    private const int FixedRenderHeight = 192;

    private Vector2 _visualSize;
    private float _barWidth = 4f;
    private float _barSpacing = 2f;
    private float _blockHeight = 8f;
    private float _peakThickness = 2f;
    private float _globalOpacity = 1f;
    private SKColor[] _gradientColors =
    [
        new SKColor(0x00, 0xCC, 0xFF),
        new SKColor(0x33, 0x33, 0xFF),
        new SKColor(0xCC, 0x00, 0xCC),
        new SKColor(0xFF, 0x00, 0x4D),
        new SKColor(0xFF, 0xB3, 0x00)
    ];

    private SKSurface? _offscreenSurface;
    private SKImage? _frameImage;
    private SKPaint? _barPaint;
    private SKPaint? _peakPaint;
    private SKPaint? _blitPaint;

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case null:
                Cleanup();
                return;
            case Vector2 size:
                _visualSize = size;
                EnsureOffscreenSurface();
                Invalidate();
                return;
            case SpectrumFrameMessage frame:
                _barWidth = Math.Max(0f, frame.BarWidth);
                _barSpacing = Math.Max(0f, frame.BarSpacing);
                _blockHeight = Math.Max(1f, frame.BlockHeight);
                _peakThickness = Math.Max(1f, frame.PeakThickness);
                if (frame.GradientColors.Length == 5)
                    _gradientColors = frame.GradientColors;

                EnsureOffscreenSurface();
                RenderFrameToOffscreen(frame);
                Invalidate();
                return;
            case SpectrumOpacityMessage opacity:
                _globalOpacity = Math.Clamp(opacity.Opacity, 0f, 1f);
                if (_blitPaint != null)
                    _blitPaint.Color = _blitPaint.Color.WithAlpha((byte)Math.Clamp((int)(255f * _globalOpacity), 0, 255));
                Invalidate();
                return;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_visualSize.X <= 0 || _visualSize.Y <= 0 || _frameImage == null || _blitPaint == null)
            return;

        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null)
            return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        var dest = SKRect.Create(0, 0, _visualSize.X, _visualSize.Y);
        var source = SKRect.Create(0, 0, FixedRenderWidth, FixedRenderHeight);
        canvas.DrawImage(
            _frameImage,
            source,
            dest,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
            _blitPaint);
    }

    private void EnsureOffscreenSurface()
    {
        if (_offscreenSurface != null)
            return;

        var info = new SKImageInfo(FixedRenderWidth, FixedRenderHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        _offscreenSurface = SKSurface.Create(info);
        _barPaint ??= new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        _peakPaint ??= new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, 242) };
        _blitPaint ??= new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low };
    }

    private void RenderFrameToOffscreen(SpectrumFrameMessage frame)
    {
        if (_offscreenSurface == null)
            return;

        int count = frame.BarLevels.Length;
        if (count == 0)
        {
            _frameImage?.Dispose();
            _frameImage = null;
            return;
        }

        var canvas = _offscreenSurface.Canvas;
        canvas.Clear(SKColors.Transparent);

        float width = FixedRenderWidth;
        float height = FixedRenderHeight;
        float pitch = Math.Max(1f, _barWidth + _barSpacing);
        float drawBarWidth = Math.Max(1f, _barWidth);
        float totalWidth = (count * drawBarWidth) + (Math.Max(0, count - 1) * _barSpacing);
        float offset = Math.Max(0f, (width - totalWidth) * 0.5f);

        for (int i = 0; i < count; i++)
        {
            float x0 = offset + (i * pitch);
            float x1 = Math.Min(width, x0 + drawBarWidth);
            if (x1 <= x0 || x0 >= width)
                continue;

            float barNorm = Math.Clamp(frame.BarLevels[i], 0f, 1f);
            float peakNorm = i < frame.PeakLevels.Length
                ? Math.Clamp(frame.PeakLevels[i], 0f, 1f)
                : barNorm;
            float barTop = height - (barNorm * height);
            float peakTop = height - (peakNorm * height);
            SKColor color = GetGradientColor(i, count);

            DrawBar(canvas, x0, x1, barTop, height, color, height);

            float peakBottom = Math.Clamp(peakTop + _peakThickness, 0f, height);
            if (peakBottom > peakTop && _peakPaint != null)
            {
                _peakPaint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)(242f * _globalOpacity), 0, 255));
                canvas.DrawRect(new SKRect(x0, Math.Clamp(peakTop, 0f, height), x1, peakBottom), _peakPaint);
            }
        }

        _frameImage?.Dispose();
        _frameImage = _offscreenSurface.Snapshot();
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
        _frameImage?.Dispose();
        _frameImage = null;
        _offscreenSurface?.Dispose();
        _offscreenSurface = null;

        _barPaint?.Dispose();
        _peakPaint?.Dispose();
        _blitPaint?.Dispose();
        _barPaint = null;
        _peakPaint = null;
        _blitPaint = null;
    }
}
