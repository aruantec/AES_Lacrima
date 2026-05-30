using AES_Controls.Player.Models;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Renders a stacked (non-spread) folder preview matching <see cref="FolderCompositionVisualHandler"/>.
/// </summary>
internal static class FolderStackSnapshotRenderer
{
    private readonly record struct DrawLayer(SKImage? Image, bool UseFolderCover, int ZIndex);

    public static SKBitmap? Render(
        AvaloniaList<MediaItem>? items,
        MediaItem? folderCoverItem,
        Bitmap? defaultCover,
        int maxVisibleCovers,
        bool uniformToFill,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var (layers, defaultSk, ownedImages) = BuildLayers(items, folderCoverItem, defaultCover, maxVisibleCovers);
        if (layers.Count == 0)
        {
            DisposeAll(ownedImages);
            return null;
        }

        try
        {
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bitmap.Erase(SKColors.Transparent);

            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

            float w = width;
            float itemSize = Math.Max(w, height);
            float baseX = w - itemSize;

            foreach (var layer in layers.OrderBy(l => l.ZIndex))
            {
                SKImage? image = layer.UseFolderCover ? defaultSk : layer.Image;
                if (image == null)
                    continue;

                var dest = new SKRect(baseX, 0, baseX + itemSize, itemSize);
                DrawCover(canvas, paint, image, dest, uniformToFill);
            }

            return bitmap;
        }
        finally
        {
            DisposeAll(ownedImages);
        }
    }

    private static (List<DrawLayer> Layers, SKImage? DefaultCover, List<SKImage> OwnedImages) BuildLayers(
        AvaloniaList<MediaItem>? items,
        MediaItem? folderCoverItem,
        Bitmap? defaultCover,
        int maxVisibleCovers)
    {
        var owned = new List<SKImage>();
        SKImage? defaultSk = CompositionBitmapHelper.ToSkImage(defaultCover, CompositionBitmapHelper.FolderCoverMaxEdge);
        if (defaultSk != null)
            owned.Add(defaultSk);

        var layers = new List<DrawLayer>();
        int count = items?.Count ?? 0;

        if (count == 0 && folderCoverItem != null)
        {
            layers.Add(new DrawLayer(null, true, 0));
            return (layers, defaultSk, owned);
        }

        if (items == null)
            return (layers, defaultSk, owned);

        int maxVisible = Math.Max(1, maxVisibleCovers);
        int startIndex = Math.Max(0, count - maxVisible);
        int z = 0;
        for (int i = startIndex; i < count; i++)
        {
            var sk = CompositionBitmapHelper.ToSkImage(items[i].CoverBitmap, CompositionBitmapHelper.FolderCoverMaxEdge);
            if (sk != null)
                owned.Add(sk);
            layers.Add(new DrawLayer(sk, false, z++));
        }

        return (layers, defaultSk, owned);
    }

    private static void DisposeAll(List<SKImage> images)
    {
        foreach (var image in images)
            image.Dispose();
    }

    private static void DrawCover(SKCanvas canvas, SKPaint paint, SKImage cover, SKRect dest, bool uniformToFill)
    {
        if (dest.Width <= 0.5f || dest.Height <= 0.5f)
            return;

        int w = cover.Width;
        int h = cover.Height;
        if (w <= 0 || h <= 0)
            return;

        paint.Color = SKColors.White;

        var srcFull = new SKRect(0, 0, w, h);
        if (uniformToFill)
        {
            var src = UniformToFillSrc(w, h, dest);
            canvas.DrawImage(cover, src, dest, paint);
        }
        else
        {
            var fitDest = UniformFitDest(w, h, dest);
            canvas.DrawImage(cover, srcFull, fitDest, paint);
        }
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
            cropX = (srcW - cropW) / 2f;
        }
        else
        {
            cropH = srcW / destAspect;
            cropY = (srcH - cropH) / 2f;
        }

        return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
    }

    private static SKRect UniformFitDest(float srcW, float srcH, SKRect dest)
    {
        float scale = Math.Min(dest.Width / srcW, dest.Height / srcH);
        float drawW = srcW * scale;
        float drawH = srcH * scale;
        float drawX = dest.MidX - drawW / 2f;
        float drawY = dest.MidY - drawH / 2f;
        return new SKRect(drawX, drawY, drawX + drawW, drawY + drawH);
    }
}
