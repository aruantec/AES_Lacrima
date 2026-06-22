using Avalonia.Media.Imaging;
using AES_Controls.Player.Models;

namespace AES_Controls.Composition;

internal static class CompositionCoverImageHelper
{
    public static void ReadCoverSources(
        object item,
        string? bitmapProp,
        string? fileProp,
        Func<object, string?, Bitmap?> getBitmap,
        Func<object?, string?, string?> resolveCoverPath,
        Bitmap? sectionPlaceholder,
        out Bitmap? bitmapValue,
        out string? fileName)
    {
        bitmapValue = null;
        fileName = null;
        try
        {
            bitmapValue = getBitmap(item, bitmapProp);
            fileName = resolveCoverPath(item, fileProp);
        }
        catch
        {
            // Callers log when needed.
        }

        NormalizeCoverSources(item as MediaItem, sectionPlaceholder, ref bitmapValue, ref fileName);
    }

    public static void NormalizeCoverSources(
        MediaItem? item,
        Bitmap? sectionPlaceholder,
        ref Bitmap? bitmapValue,
        ref string? fileName)
    {
        if (IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder))
            bitmapValue = null;

        if (string.IsNullOrWhiteSpace(fileName) && item != null)
            fileName = CompositionMetadataCoverHelper.GetCoverCachePath(item.FileName)
                       ?? CompositionMetadataCoverHelper.GetMetadataCachePath(item.FileName);
    }

    public static bool IsSectionPlaceholderBitmap(Bitmap? bitmapValue, Bitmap? sectionPlaceholder) =>
        bitmapValue != null &&
        sectionPlaceholder != null &&
        ReferenceEquals(bitmapValue, sectionPlaceholder);

    public static bool IsLikelyImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    public static bool HasResolvableLocalCoverFile(MediaItem mediaItem)
    {
        if (!string.IsNullOrWhiteSpace(mediaItem.LocalCoverPath) && File.Exists(mediaItem.LocalCoverPath))
            return true;

        if (string.IsNullOrWhiteSpace(mediaItem.FileName))
            return false;

        var sidecar = CompositionMetadataCoverHelper.GetCoverCachePath(mediaItem.FileName);
        return !string.IsNullOrWhiteSpace(sidecar) && File.Exists(sidecar);
    }

    public static bool ShouldPreferFileOverBitmap(
        MediaItem? item,
        Bitmap? bitmapValue,
        string? fileName,
        Bitmap? sectionPlaceholder)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            return false;

        if (bitmapValue != null && !IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder))
            return false;

        if (CompositionMetadataCoverHelper.IsMetadataCachePath(fileName) ||
            CompositionMetadataCoverHelper.IsCoverSidecarPath(fileName))
            return true;

        if (IsLikelyImageFile(fileName))
            return IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder) ||
                   !string.IsNullOrWhiteSpace(item?.LocalCoverPath);

        return bitmapValue == null;
    }

    public static bool ShouldReloadCachedCover(
        MediaItem? item,
        Bitmap? bitmapValue,
        string? fileName,
        Bitmap? sectionPlaceholder) =>
        (bitmapValue != null && !IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder)) ||
        (IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder) &&
         ShouldPreferFileOverBitmap(item, bitmapValue, fileName, sectionPlaceholder));

    public static object? ResolveImageSourceKey(
        MediaItem? item,
        Bitmap? bitmapValue,
        string? fileName,
        Bitmap? sectionPlaceholder)
    {
        if (bitmapValue != null && !IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder))
            return bitmapValue;

        if (ShouldPreferFileOverBitmap(item, bitmapValue, fileName, sectionPlaceholder))
            return fileName;

        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return null;
    }

    public static Bitmap? DetectSectionPlaceholder(
        IReadOnlyList<object?> items,
        string? bitmapProp,
        Func<object, string?, Bitmap?> getBitmap,
        int scanLimit = int.MaxValue)
    {
        var counts = new Dictionary<Bitmap, int>(ReferenceEqualityComparer.Instance);
        int limit = scanLimit <= 0 ? items.Count : Math.Min(items.Count, scanLimit);
        for (int i = 0; i < limit; i++)
        {
            var item = items[i];
            if (item == null)
                continue;

            var bitmap = getBitmap(item, bitmapProp);
            if (bitmap == null)
                continue;

            counts[bitmap] = counts.GetValueOrDefault(bitmap) + 1;
        }

        return counts
            .Where(pair => pair.Value > 1)
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }
}
