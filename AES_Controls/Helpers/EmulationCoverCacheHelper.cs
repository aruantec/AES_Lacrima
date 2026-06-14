using System.Collections.Concurrent;
using AES_Code.Models;
using AES_Core.IO;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System.Linq;

namespace AES_Controls.Helpers;

/// <summary>
/// Sidecar <c>{cacheId}.cover</c> files for emulation box art, separate from <c>.meta</c> JSON.
/// Covers are stored as WebP at display resolution for fast decode.
/// </summary>
public static class EmulationCoverCacheHelper
{
    public const string CoverExtension = ".cover";
    public const int MaxCoverDimension = 384;
    public const int WebpQuality = 82;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static string GetCacheId(string? filePath) => BinaryMetadataHelper.GetCacheId(NormalizeRomPath(filePath));

    public static string GetCoverCachePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        return ApplicationPaths.GetCacheFile(GetCacheId(filePath) + CoverExtension);
    }

    public static string GetMetadataCachePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        return ApplicationPaths.GetCacheFile(GetCacheId(filePath) + ".meta");
    }

    public static bool HasCover(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && File.Exists(GetCoverCachePath(filePath));

    public static bool IsCoverCachePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(CoverExtension, StringComparison.OrdinalIgnoreCase);

    public static byte[]? TryReadCoverBytes(string? filePath)
    {
        if (!HasCover(filePath))
            return null;

        try
        {
            return File.ReadAllBytes(GetCoverCachePath(filePath));
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? TryReadCoverBytesFromPath(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath))
            return null;

        try
        {
            return File.ReadAllBytes(coverPath);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryDeleteCoverSidecar(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var coverPath = GetCoverCachePath(filePath);
        if (!File.Exists(coverPath))
            return false;

        try
        {
            File.Delete(coverPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes sidecar bytes (WebP/PNG/etc.) into an Avalonia bitmap for <see cref="MediaItem.CoverBitmap"/>.
    /// </summary>
    public static Bitmap? DecodeCoverBytesToBitmap(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return null;

        using var decoded = SKBitmap.Decode(bytes);
        if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0)
            return null;

        SKBitmap? resized = null;
        var working = decoded;
        try
        {
            if (Math.Max(decoded.Width, decoded.Height) > MaxCoverDimension)
            {
                float scale = MaxCoverDimension / (float)Math.Max(decoded.Width, decoded.Height);
                int tw = Math.Max(1, (int)(decoded.Width * scale));
                int th = Math.Max(1, (int)(decoded.Height * scale));
                resized = decoded.Resize(new SKImageInfo(tw, th), SKFilterQuality.Medium);
                if (resized != null)
                    working = resized;
            }

            using var image = SKImage.FromBitmap(working);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
                return null;

            return new Bitmap(data.AsStream());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (resized != null && !ReferenceEquals(resized, decoded))
                resized.Dispose();
        }
    }

    /// <summary>
    /// Encodes source artwork to WebP, applies bar-crop when applicable, and writes the sidecar file.
    /// </summary>
    public static bool WriteCoverFromBytes(string? filePath, ReadOnlySpan<byte> sourceBytes)
    {
        if (string.IsNullOrWhiteSpace(filePath) || sourceBytes.Length == 0)
            return false;

        var cropped = CoverImageBarCropHelper.TryCropBytes(sourceBytes.ToArray(), filePath);
        var encoded = EncodeDisplayWebp(cropped);
        if (encoded == null || encoded.Length == 0)
            return false;

        return WriteEncodedCover(filePath, encoded);
    }

    /// <summary>
    /// Moves a legacy cover embedded in <c>.meta</c> into a sidecar <c>.cover</c> file.
    /// </summary>
    public static bool TryMigrateCoverFromMetadata(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || HasCover(filePath))
            return false;

        var metaPath = GetMetadataCachePath(filePath);
        if (!File.Exists(metaPath))
            return false;

        var metadata = BinaryMetadataHelper.LoadMetadata(metaPath);
        var cover = metadata?.Images?.FirstOrDefault(image =>
            image.Kind == TagImageKind.Cover && image.Data is { Length: > 0 });
        if (cover == null)
            return false;

        if (!WriteCoverFromBytes(filePath, cover.Data))
            return false;

        metadata!.Images = metadata.Images
            .Where(image => image.Kind != TagImageKind.Cover)
            .ToList();
        metadata.CoverScanned = true;
        metadata.CoverLookupExhausted = false;
        BinaryMetadataHelper.SaveMetadata(metaPath, metadata);
        return true;
    }

    public static bool TryEnsureCoverSidecar(string? filePath)
    {
        if (HasCover(filePath))
            return true;

        return TryMigrateCoverFromMetadata(filePath);
    }

    private static byte[]? EncodeDisplayWebp(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;

        using var source = SKBitmap.Decode(bytes);
        if (source == null || source.Width <= 0 || source.Height <= 0)
            return null;

        SKBitmap? cropped = null;
        var working = source;
        try
        {
            if (working.Width != working.Height)
            {
                int size = Math.Min(working.Width, working.Height);
                int x = (working.Width - size) / 2;
                int y = (working.Height - size) / 2;
                cropped = new SKBitmap(size, size);
                if (!working.ExtractSubset(cropped, new SKRectI(x, y, x + size, y + size)))
                {
                    cropped.Dispose();
                    cropped = null;
                }
                else
                {
                    working = cropped;
                }
            }

            if (Math.Max(working.Width, working.Height) > MaxCoverDimension)
            {
                float scale = MaxCoverDimension / (float)Math.Max(working.Width, working.Height);
                int tw = Math.Max(1, (int)(working.Width * scale));
                int th = Math.Max(1, (int)(working.Height * scale));
                using var resized = working.Resize(new SKImageInfo(tw, th), SKFilterQuality.Medium);
                if (resized == null)
                    return null;

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
                return data?.ToArray();
            }

            using var fullImage = SKImage.FromBitmap(working);
            using var fullData = fullImage.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            return fullData?.ToArray();
        }
        finally
        {
            if (cropped != null && !ReferenceEquals(cropped, source))
                cropped.Dispose();
        }
    }

    private static bool WriteEncodedCover(string filePath, byte[] encoded)
    {
        var coverPath = GetCoverCachePath(filePath);
        if (string.IsNullOrWhiteSpace(coverPath))
            return false;

        var fileLock = FileLocks.GetOrAdd(coverPath, _ => new SemaphoreSlim(1, 1));
        fileLock.Wait();
        try
        {
            var directory = Path.GetDirectoryName(coverPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = coverPath + ".tmp";
            File.WriteAllBytes(tempPath, encoded);
            if (File.Exists(coverPath))
                File.Replace(tempPath, coverPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, coverPath);

            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static string NormalizeRomPath(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return string.Empty;

        try
        {
            return Path.GetFullPath(romPath.Trim());
        }
        catch
        {
            return romPath.Trim();
        }
    }
}
