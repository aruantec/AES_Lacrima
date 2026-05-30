using System.Runtime.InteropServices;
using AES_Code.Models;
using AES_Core.IO;
using SkiaSharp;

namespace AES_Controls.Helpers;

/// <summary>
/// Detects and removes uniform letterbox/pillarbox bars (black, near-black, or transparent)
/// from cover art images.
/// </summary>
public static class CoverImageBarCropHelper
{
    private const int MinBarPixels = 6;
    private const float BarRowThreshold = 0.93f;
    private const float MinRemovedFraction = 0.012f;
    private const float MaxEdgeRemovedFraction = 0.48f;
    private const int MinContentEdge = 48;
    private const int SampleStep = 3;

    public static byte[] TryCropBytes(byte[] sourceBytes, string? originalPath = null)
    {
        if (sourceBytes.Length == 0)
            return sourceBytes;

        try
        {
            using var data = SKData.CreateCopy(sourceBytes);
            using var codec = SKCodec.Create(data);
            if (codec == null)
                return sourceBytes;

            using var bmp = new SKBitmap(codec.Info);
            codec.GetPixels(bmp.Info, bmp.GetPixels());
            using var cropped = TryCrop(bmp, out bool didCrop);
            if (!didCrop || cropped == null)
                return sourceBytes;

            return Encode(cropped, originalPath) ?? sourceBytes;
        }
        catch
        {
            return sourceBytes;
        }
    }

    public static SKBitmap? TryCrop(SKBitmap source, out bool cropped)
    {
        cropped = false;
        if (source == null || source.Width < MinContentEdge + MinBarPixels * 2 || source.Height < MinContentEdge + MinBarPixels * 2)
            return null;

        if (source.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
        {
            using var converted = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (!source.CopyTo(converted))
                return null;
            return TryCropCore(converted, out cropped);
        }

        return TryCropCore(source, out cropped);
    }

    public static byte[]? Encode(SKBitmap bitmap, string? originalPath)
    {
        var ext = Path.GetExtension(originalPath ?? string.Empty).ToLowerInvariant();
        using var data = ext switch
        {
            ".jpg" or ".jpeg" => bitmap.Encode(SKEncodedImageFormat.Jpeg, 92),
            ".webp" => bitmap.Encode(SKEncodedImageFormat.Webp, 90),
            _ => bitmap.Encode(SKEncodedImageFormat.Png, 100)
        };

        return data?.ToArray();
    }

    public static string GuessMimeType(string? path)
    {
        return Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
    }

    public static bool TryWriteImageFile(string? path, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsLikelyImageFile(path))
            return false;

        try
        {
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryWriteMetadataCover(string fileName, byte[] bytes, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        try
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(fileName);
            var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
            var preserved = BinaryMetadataHelper.ReadMetadataImages(metadata)
                .Where(entry => entry.Kind is not TagImageKind.Cover and not TagImageKind.BackCover)
                .ToList();
            preserved.Insert(0, new MetadataImageEntry(TagImageKind.Cover, bytes.ToArray(), mimeType));
            metadata.CoverScanned = true;
            BinaryMetadataHelper.WriteMetadataImages(metadata, preserved);
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryPersistCroppedCover(SKBitmap cropped, string? imagePath, string? ownerFileName)
    {
        var bytes = Encode(cropped, imagePath ?? ownerFileName);
        if (bytes == null || bytes.Length == 0)
            return false;

        if (TryWriteImageFile(imagePath, bytes))
            return true;

        if (!string.IsNullOrWhiteSpace(ownerFileName))
            return TryWriteMetadataCover(ownerFileName, bytes, GuessMimeType(imagePath ?? ownerFileName));

        return false;
    }

    private static SKBitmap? TryCropCore(SKBitmap source, out bool cropped)
    {
        cropped = false;
        int width = source.Width;
        int height = source.Height;
        var pixels = source.GetPixels();
        if (pixels == IntPtr.Zero)
            return null;

        int stride = source.RowBytes;
        int top = FindContentTop(pixels, stride, width, height);
        int bottom = FindContentBottom(pixels, stride, width, height);
        int left = FindContentLeft(pixels, stride, width, height, top, bottom);
        int right = FindContentRight(pixels, stride, width, height, top, bottom);

        int cropWidth = right - left + 1;
        int cropHeight = bottom - top + 1;
        if (cropWidth < MinContentEdge || cropHeight < MinContentEdge)
            return null;

        int removedPixels = width * height - cropWidth * cropHeight;
        if (removedPixels <= 0)
            return null;

        float removedFraction = removedPixels / (float)(width * height);
        if (removedFraction < MinRemovedFraction)
            return null;

        if (top > height * MaxEdgeRemovedFraction ||
            height - 1 - bottom > height * MaxEdgeRemovedFraction ||
            left > width * MaxEdgeRemovedFraction ||
            width - 1 - right > width * MaxEdgeRemovedFraction)
            return null;

        if (top == 0 && bottom == height - 1 && left == 0 && right == width - 1)
            return null;

        var dest = new SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType);
        if (!source.ExtractSubset(dest, new SKRectI(left, top, right + 1, bottom + 1)))
        {
            dest.Dispose();
            return null;
        }

        cropped = true;
        return dest;
    }

    private static int FindContentTop(IntPtr pixels, int stride, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            if (!IsBarRow(pixels, stride, width, y))
                return y;
        }

        return 0;
    }

    private static int FindContentBottom(IntPtr pixels, int stride, int width, int height)
    {
        for (int y = height - 1; y >= 0; y--)
        {
            if (!IsBarRow(pixels, stride, width, y))
                return y;
        }

        return height - 1;
    }

    private static int FindContentLeft(IntPtr pixels, int stride, int width, int height, int top, int bottom)
    {
        for (int x = 0; x < width; x++)
        {
            if (!IsBarColumn(pixels, stride, width, height, x, top, bottom))
                return x;
        }

        return 0;
    }

    private static int FindContentRight(IntPtr pixels, int stride, int width, int height, int top, int bottom)
    {
        for (int x = width - 1; x >= 0; x--)
        {
            if (!IsBarColumn(pixels, stride, width, height, x, top, bottom))
                return x;
        }

        return width - 1;
    }

    private static bool IsBarRow(IntPtr pixels, int stride, int width, int y)
    {
        int barCount = 0;
        int total = 0;
        int rowOffset = y * stride;

        for (int x = 0; x < width; x += SampleStep)
        {
            total++;
            if (IsBarLikePixel(pixels, rowOffset + x * 4))
                barCount++;
        }

        return total > 0 && barCount >= total * BarRowThreshold;
    }

    private static bool IsBarColumn(IntPtr pixels, int stride, int width, int height, int x, int top, int bottom)
    {
        int barCount = 0;
        int total = 0;
        int colOffset = x * 4;

        for (int y = top; y <= bottom; y += SampleStep)
        {
            total++;
            if (IsBarLikePixel(pixels, y * stride + colOffset))
                barCount++;
        }

        return total > 0 && barCount >= total * BarRowThreshold;
    }

    private static bool IsBarLikePixel(IntPtr pixels, int offset)
    {
        byte b = Marshal.ReadByte(pixels, offset + 0);
        byte g = Marshal.ReadByte(pixels, offset + 1);
        byte r = Marshal.ReadByte(pixels, offset + 2);
        byte a = Marshal.ReadByte(pixels, offset + 3);

        if (a < 20)
            return true;

        int maxRgb = Math.Max(r, Math.Max(g, b));
        if (a > 200 && maxRgb < 45)
            return true;

        if (maxRgb < 30 && a < 180)
            return true;

        return false;
    }

    private static bool IsLikelyImageFile(string path) =>
        Path.GetExtension(path) switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" => true,
            _ => false
        };
}
