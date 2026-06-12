using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Core.IO;
using SkiaSharp;

namespace AES_Controls.Composition;

internal static class CompositionMetadataCoverHelper
{
    public static bool IsMetadataCachePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

    public static string? GetMetadataCachePath(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        var normalized = NormalizeRomPath(romPath);
        var cachePath = ApplicationPaths.GetCacheFile(BinaryMetadataHelper.GetCacheId(normalized) + ".meta");
        return File.Exists(cachePath) ? cachePath : null;
    }

    private static string NormalizeRomPath(string romPath)
    {
        try
        {
            return Path.GetFullPath(romPath.Trim());
        }
        catch
        {
            return romPath.Trim();
        }
    }

    public static bool MetadataCacheHasCoverImage(string metaPath)
    {
        try
        {
            var metadata = BinaryMetadataHelper.LoadMetadata(metaPath);
            if (metadata == null)
                return false;

            return BinaryMetadataHelper.ReadMetadataImages(metadata)
                .Any(entry => entry.Kind == TagImageKind.Cover && entry.Data.Length > 0);
        }
        catch
        {
            return false;
        }
    }

    public static byte[]? TryReadCoverBytes(string path)
    {
        try
        {
            if (IsMetadataCachePath(path))
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(path);
                if (metadata == null)
                    return null;

                foreach (var entry in BinaryMetadataHelper.ReadMetadataImages(metadata))
                {
                    if (entry.Kind == TagImageKind.Cover && entry.Data.Length > 0)
                        return entry.Data;
                }

                return null;
            }

            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static SKImage? LoadCoverFromBytes(byte[] bytes, int maxSize, Func<SKBitmap, SKImage?> finalize)
    {
        if (bytes.Length == 0)
            return null;

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return null;

        return finalize(bitmap);
    }
}
