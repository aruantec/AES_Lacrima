using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Renders how a source cover will appear in carousel or grid item layouts.
/// </summary>
public static class CompositionCoverPreviewRenderer
{
    public const float CarouselCardWidth = 240f;
    public const float CarouselCardHeight = 200f;
    private const float CardCornerRadius = 12f;
    private static readonly SKColor CarouselBackdrop = SKColor.Parse("#1E1E1E");

    public static SKImage? Render(SKImage source, CoverLayoutMode layoutMode, string? title)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return null;

        return layoutMode == CoverLayoutMode.Carousel
            ? RenderCarouselPreview(source)
            : CompositionCardDisplayBaker.Bake(source, title);
    }

    private static SKImage RenderCarouselPreview(SKImage source)
    {
        float cardW = CarouselCardWidth;
        float cardH = CarouselCardHeight;

        using var surface = SKSurface.Create(new SKImageInfo(
            (int)MathF.Ceiling(cardW),
            (int)MathF.Ceiling(cardH),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(CarouselBackdrop);

        using var clipPath = new SKPath();
        clipPath.AddRoundRect(new SKRect(0, 0, cardW, cardH), CardCornerRadius, CardCornerRadius);
        canvas.Save();
        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

        var dst = new SKRect(0, 0, cardW, cardH);
        var src = UniformToFillSrc(source.Width, source.Height, dst);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White
        };
        canvas.DrawImage(source, src, dst, paint);
        canvas.Restore();

        return surface.Snapshot();
    }

    private static SKRect UniformToFillSrc(float srcW, float srcH, SKRect dest)
    {
        float srcAspect = srcW / srcH;
        float destAspect = dest.Width / dest.Height;
        float cropW = srcW;
        float cropH = srcH;
        float cropX = 0;
        float cropY = 0;

        if (srcAspect > destAspect)
        {
            cropW = srcH * destAspect;
            cropX = (srcW - cropW) * 0.5f;
        }
        else
        {
            cropH = srcW / destAspect;
            cropY = (srcH - cropH) * 0.5f;
        }

        return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
    }
}
