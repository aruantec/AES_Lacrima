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

    public static bool IsCoverSidecarPath(string? path) =>
        EmulationCoverCacheHelper.IsCoverCachePath(path);

    public static string? GetMetadataCachePath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;

        var cachePath = EmulationCoverCacheHelper.GetMetadataCachePath(mediaPath);
        return File.Exists(cachePath) ? cachePath : null;
    }

    public static string? GetCoverCachePath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || MediaCoverPaths.IsAudioMediaFile(mediaPath))
            return null;

        if (EmulationCoverCacheHelper.TryEnsureCoverSidecar(mediaPath))
            return EmulationCoverCacheHelper.GetCoverCachePath(mediaPath);

        return EmulationCoverCacheHelper.HasCover(mediaPath)
            ? EmulationCoverCacheHelper.GetCoverCachePath(mediaPath)
            : null;
    }

    public static bool MetadataCacheHasCoverImage(string metaPath)
    {
        try
        {
            if (IsCoverSidecarPath(metaPath))
                return File.Exists(metaPath);

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
            if (IsCoverSidecarPath(path))
                return EmulationCoverCacheHelper.TryReadCoverBytesFromPath(path);

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
