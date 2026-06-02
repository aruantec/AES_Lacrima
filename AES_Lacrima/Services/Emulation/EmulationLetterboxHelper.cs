using System;
using System.IO;
using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Core.IO;
using Avalonia.Media.Imaging;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Loads back-cover artwork from per-ROM metadata cache for capture letterbox fill.
/// </summary>
public static class EmulationLetterboxHelper
{
    public static string GetMetadataCachePath(string? filePath)
    {
        var normalized = string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim();
        var cacheId = BinaryMetadataHelper.GetCacheId(normalized);
        return ApplicationPaths.GetCacheFile(cacheId + ".meta");
    }

    public static Bitmap? TryLoadBackCoverBitmap(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        try
        {
            var cachePath = GetMetadataCachePath(romPath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
            if (metadata == null)
                return null;

            foreach (var entry in BinaryMetadataHelper.ReadMetadataImages(metadata))
            {
                if (entry.Kind != TagImageKind.BackCover || entry.Data.Length == 0)
                    continue;

                using var stream = new MemoryStream(entry.Data, writable: false);
                return Bitmap.DecodeToWidth(stream, 1920);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
