using AES_Code.Models;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Renders how a wallpaper or arcade letterbox bezel image will appear in capture.
/// </summary>
public static class CompositionWallpaperBezelPreviewRenderer
{
    public const float PreviewWidth = 640f;
    public const float PreviewHeight = 360f;
    private const float GameWidthFraction = 0.44f;
    private static readonly SKColor Backdrop = SKColor.Parse("#101010");
    private static readonly SKColor GamePlaceholder = SKColor.Parse("#050505");

    public static SKImage? Render(SKImage source, TagImageKind kind)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return null;

        return kind == TagImageKind.Wallpaper
            ? RenderWallpaperPreview(source)
            : RenderLetterboxBezelPreview(source);
    }

    private static SKImage RenderWallpaperPreview(SKImage source)
    {
        using var surface = CreateSurface();
        var canvas = surface.Canvas;
        canvas.Clear(Backdrop);

        var dst = new SKRect(0, 0, PreviewWidth, PreviewHeight);
        var src = UniformToFillSrc(source.Width, source.Height, dst);
        DrawImage(canvas, source, src, dst);

        return surface.Snapshot();
    }

    private static SKImage RenderLetterboxBezelPreview(SKImage source)
    {
        using var surface = CreateSurface();
        var canvas = surface.Canvas;
        canvas.Clear(Backdrop);

        var dst = new SKRect(0, 0, PreviewWidth, PreviewHeight);
        var src = UniformToFillHeightSrc(source.Width, source.Height, dst);
        DrawImage(canvas, source, src, dst);

        var gameWidth = PreviewWidth * GameWidthFraction;
        var gameLeft = (PreviewWidth - gameWidth) * 0.5f;
        var gameRect = new SKRect(gameLeft, 0, gameLeft + gameWidth, PreviewHeight);
        using var gamePaint = new SKPaint { Color = GamePlaceholder, IsAntialias = true };
        canvas.DrawRect(gameRect, gamePaint);

        using var borderPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(36),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawRect(gameRect, borderPaint);

        return surface.Snapshot();
    }

    private static SKSurface CreateSurface()
    {
        return SKSurface.Create(new SKImageInfo(
            (int)MathF.Ceiling(PreviewWidth),
            (int)MathF.Ceiling(PreviewHeight),
            SKColorType.Rgba8888,
            SKAlphaType.Premul))!;
    }

    private static void DrawImage(SKCanvas canvas, SKImage source, SKRect src, SKRect dst)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White
        };
        canvas.DrawImage(source, src, dst, paint);
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

    private static SKRect UniformToFillHeightSrc(float srcW, float srcH, SKRect dest)
    {
        var height = dest.Height;
        var width = height * (srcW / srcH);
        var cropX = Math.Max(0, (width - dest.Width) * 0.5f * (srcW / width));
        var cropW = srcW;
        if (width > dest.Width)
        {
            cropW = srcH * (dest.Width / dest.Height);
            cropX = (srcW - cropW) * 0.5f;
        }

        return new SKRect(cropX, 0, cropX + cropW, srcH);
    }
}
