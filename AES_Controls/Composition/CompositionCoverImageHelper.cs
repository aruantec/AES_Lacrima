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
            fileName = CompositionMetadataCoverHelper.GetMetadataCachePath(item.FileName);
    }

    public static bool IsSectionPlaceholderBitmap(Bitmap? bitmapValue, Bitmap? sectionPlaceholder) =>
        bitmapValue != null &&
        sectionPlaceholder != null &&
        ReferenceEquals(bitmapValue, sectionPlaceholder);

    public static bool IsLikelyImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    public static bool ShouldPreferFileOverBitmap(
        MediaItem? item,
        Bitmap? bitmapValue,
        string? fileName,
        Bitmap? sectionPlaceholder)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
            return false;

        if (CompositionMetadataCoverHelper.IsMetadataCachePath(fileName))
            return CompositionMetadataCoverHelper.MetadataCacheHasCoverImage(fileName);

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
        IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder) &&
        ShouldPreferFileOverBitmap(item, bitmapValue, fileName, sectionPlaceholder);

    public static object? ResolveImageSourceKey(
        MediaItem? item,
        Bitmap? bitmapValue,
        string? fileName,
        Bitmap? sectionPlaceholder)
    {
        if (ShouldPreferFileOverBitmap(item, bitmapValue, fileName, sectionPlaceholder))
            return fileName;

        if (bitmapValue != null && !IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder))
            return bitmapValue;

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            if (CompositionMetadataCoverHelper.IsMetadataCachePath(fileName) &&
                !CompositionMetadataCoverHelper.MetadataCacheHasCoverImage(fileName))
            {
                return null;
            }

            return fileName;
        }

        return null;
    }

    public static Bitmap? DetectSectionPlaceholder(IReadOnlyList<object?> items, string? bitmapProp, Func<object, string?, Bitmap?> getBitmap)
    {
        var counts = new Dictionary<Bitmap, int>(ReferenceEqualityComparer.Instance);
        foreach (var item in items)
        {
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
