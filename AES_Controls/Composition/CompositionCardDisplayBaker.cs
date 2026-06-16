using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Pre-renders the full grid card face (cover crop, blurred title backdrop, gradient, and title text)
/// into a single display-only bitmap.
/// </summary>
internal static class CompositionCardDisplayBaker
{
    public const int BakedCardWidth = 256;
    public const float TitleAreaRatio = 0.24f;
    private const float TitleTextSizeRatio = 0.09f;
    private const float TitleTextSizeMin = 17f;
    private const float TitleTextSizeMax = 22f;
    private const int BlurBackdropWidth = 128;
    private const int BlurBackdropHeight = 172;

    private static readonly SKColor PlaceholderColor = SKColor.Parse("#1E1E1E");

    public static SKImage Bake(SKImage source, string? title = null)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return source;

        float cardW = BakedCardWidth;
        float cardH = cardW * CompositionCardGridVisualHandler.BaseCardHeight / CompositionCardGridVisualHandler.BaseCardWidth;
        float titleH = cardH * TitleAreaRatio;
        float coverH = cardH - titleH;

        using var surface = SKSurface.Create(new SKImageInfo(
            (int)MathF.Ceiling(cardW),
            (int)MathF.Ceiling(cardH),
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(PlaceholderColor);

        var coverDst = new SKRect(0, 0, cardW, coverH);
        DrawUniformCover(canvas, source, coverDst);

        var titleRect = new SKRect(0, coverH, cardW, cardH);
        DrawTitleBarBackground(canvas, source, coverDst, titleRect, coverH);
        DrawTitleText(canvas, titleRect, cardW, titleH, title);

        return surface.Snapshot();
    }

    private static void DrawUniformCover(SKCanvas canvas, SKImage source, SKRect dst)
    {
        var src = UniformToFillSrc(source.Width, source.Height, dst);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = SKColors.White
        };
        canvas.DrawImage(source, src, dst, paint);
    }

    private static void DrawTitleBarBackground(SKCanvas canvas, SKImage source, SKRect coverDst, SKRect titleRect, float titleTop)
    {
        using var backdrop = CreateBlurBackdrop(source);
        if (backdrop != null)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.Low,
                Color = SKColors.White
            };

            canvas.Save();
            canvas.Translate(0, titleTop);
            canvas.Scale(1, -1);
            canvas.Translate(0, -titleTop);
            canvas.DrawImage(backdrop, coverDst);
            canvas.Restore();
        }

        using var gradientPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(titleRect.Left, titleTop),
                new SKPoint(titleRect.Left, titleRect.Bottom),
                new[] { SKColors.Black.WithAlpha(10), SKColors.Black.WithAlpha(175) },
                null,
                SKShaderTileMode.Clamp),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(titleRect, gradientPaint);
    }

    private static void DrawTitleText(SKCanvas canvas, SKRect titleRect, float cardW, float titleH, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;

        using var titlePaint = CreateTitlePaint(SKColors.White.WithAlpha(245));
        using var shadowPaint = CreateTitlePaint(SKColors.Black.WithAlpha(140));

        float textSize = Math.Clamp(cardW * TitleTextSizeRatio, TitleTextSizeMin, TitleTextSizeMax);
        titlePaint.TextSize = textSize;
        shadowPaint.TextSize = textSize;

        float textX = titleRect.Left + 12f;
        float maxTextWidth = cardW - 24f;
        float lineHeight = textSize * 1.18f;
        int maxLines = Math.Clamp((int)Math.Floor(titleH / lineHeight), 1, 2);
        var lines = CompositionSkiaTextHelper.WrapTextLines(title, maxTextWidth, titlePaint, maxLines);
        if (lines.Count == 0)
            return;

        float totalHeight = lines.Count * lineHeight;
        float firstBaselineY = titleRect.MidY - totalHeight * 0.5f + textSize * 0.82f;
        var textClip = new SKRect(textX, titleRect.Top, titleRect.Right - 12f, titleRect.Bottom);

        canvas.Save();
        canvas.ClipRect(textClip);
        CompositionSkiaTextHelper.DrawTextLines(canvas, lines, textX, firstBaselineY + 1f, lineHeight, shadowPaint);
        CompositionSkiaTextHelper.DrawTextLines(canvas, lines, textX, firstBaselineY, lineHeight, titlePaint);
        canvas.Restore();
    }

    private static SKPaint CreateTitlePaint(SKColor color)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            TextSize = 18,
            IsLinearText = true,
            SubpixelText = true
        };
        CompositionSkiaTextHelper.ConfigurePaint(paint);
        return paint;
    }

    private static SKImage? CreateBlurBackdrop(SKImage source)
    {
        var cacheDst = new SKRect(0, 0, BlurBackdropWidth, BlurBackdropHeight);
        var fillSrc = UniformToFillSrc(source.Width, source.Height, cacheDst);

        using var surface = SKSurface.Create(new SKImageInfo(BlurBackdropWidth, BlurBackdropHeight));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var blurFilter = SKImageFilter.CreateBlur(8f, 8f);
        using var blurPaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Low,
            ImageFilter = blurFilter
        };

        canvas.SaveLayer(blurPaint);
        using var imagePaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Low,
            Color = SKColors.White
        };
        canvas.DrawImage(source, fillSrc, cacheDst, imagePaint);
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
