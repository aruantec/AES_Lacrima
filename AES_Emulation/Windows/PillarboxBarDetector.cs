using System;

namespace AES_Emulation.Windows;

/// <summary>
/// Detects uniform near-black letterbox/pillarbox borders in captured BGRA frames.
/// Ignores sparse UI/text on black backgrounds by requiring large content area and pure bar regions.
/// </summary>
internal static class PillarboxBarDetector
{
    private const int BorderLumaMax = 12;
    private const int ContentLumaMin = 22;
    private const double BarLineRatio = 0.94;
    private const int MaxContentLinesInBarColumn = 1;
    private const int MinBarPixels = 8;
    private const double MinContentWidthRatio = 0.68;
    private const double MinContentHeightRatio = 0.68;
    private const double MaxCropWidthRatioPerSide = 0.22;
    private const double MaxCropHeightRatioPerSide = 0.22;

    public static void DetectInsets(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        out int left,
        out int right,
        out int top,
        out int bottom)
    {
        left = right = top = bottom = 0;
        if (width < 80 || height < 80 || bgra.Length < stride * height)
            return;

        Span<int> rows = stackalloc int[17];
        BuildSampleLines(height, rows);
        Span<int> cols = stackalloc int[17];
        BuildSampleLines(width, cols);

        if (!HasSubstantialCentralContent(bgra, width, height, stride, rows, cols))
            return;

        var maxScanX = Math.Max(24, (int)(width * MaxCropWidthRatioPerSide));
        var maxScanY = Math.Max(24, (int)(height * MaxCropHeightRatioPerSide));

        left = ScanUniformBarFromLeft(bgra, width, height, stride, rows, maxScanX);
        right = ScanUniformBarFromRight(bgra, width, height, stride, rows, maxScanX);
        top = ScanUniformBarFromTop(bgra, width, height, stride, cols, maxScanY);
        bottom = ScanUniformBarFromBottom(bgra, width, height, stride, cols, maxScanY);

        ValidateAndClampInsets(bgra, width, height, stride, ref left, ref right, ref top, ref bottom);
    }

    private static void BuildSampleLines(int extent, Span<int> lines)
    {
        var count = lines.Length;
        for (var i = 0; i < count; i++)
        {
            var t = (i + 0.5) / count;
            lines[i] = Math.Clamp((int)Math.Round(t * (extent - 1)), 0, extent - 1);
        }
    }

    private static int ChannelMax(ReadOnlySpan<byte> pixel) =>
        Math.Max(pixel[0], Math.Max(pixel[1], pixel[2]));

    private static bool IsBorderBlack(ReadOnlySpan<byte> pixel) =>
        ChannelMax(pixel) <= BorderLumaMax;

    private static bool IsContentPixel(ReadOnlySpan<byte> pixel) =>
        ChannelMax(pixel) >= ContentLumaMin;

    /// <summary>
    /// Requires a broad central content area — not just a few text pixels on black.
    /// </summary>
    private static bool HasSubstantialCentralContent(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        ReadOnlySpan<int> rows,
        ReadOnlySpan<int> cols)
    {
        var innerLeft = width / 5;
        var innerRight = width * 4 / 5;
        var innerTop = height / 5;
        var innerBottom = height * 4 / 5;

        var contentPixels = 0;
        var sampled = 0;

        foreach (var y in rows)
        {
            if (y < innerTop || y > innerBottom)
                continue;

            foreach (var x in cols)
            {
                if (x < innerLeft || x > innerRight)
                    continue;

                sampled++;
                var offset = y * stride + x * 4;
                if (offset + 3 < bgra.Length && IsContentPixel(bgra.Slice(offset, 4)))
                    contentPixels++;
            }
        }

        if (sampled == 0)
            return false;

        return contentPixels >= Math.Max(12, (int)(sampled * 0.18));
    }

    private static int ScanUniformBarFromLeft(ReadOnlySpan<byte> bgra, int width, int height, int stride, ReadOnlySpan<int> rows, int maxScan)
    {
        for (var x = 0; x < maxScan && x < width; x++)
        {
            if (!IsUniformBarColumn(bgra, height, stride, rows, x))
                return Math.Max(0, x - 1);
        }

        return 0;
    }

    private static int ScanUniformBarFromRight(ReadOnlySpan<byte> bgra, int width, int height, int stride, ReadOnlySpan<int> rows, int maxScan)
    {
        for (var x = width - 1; x >= width - maxScan && x >= 0; x--)
        {
            if (!IsUniformBarColumn(bgra, height, stride, rows, x))
                return Math.Max(0, width - 1 - x - 1);
        }

        return 0;
    }

    private static int ScanUniformBarFromTop(ReadOnlySpan<byte> bgra, int width, int height, int stride, ReadOnlySpan<int> cols, int maxScan)
    {
        for (var y = 0; y < maxScan && y < height; y++)
        {
            if (!IsUniformBarRow(bgra, width, stride, cols, y))
                return Math.Max(0, y - 1);
        }

        return 0;
    }

    private static int ScanUniformBarFromBottom(ReadOnlySpan<byte> bgra, int width, int height, int stride, ReadOnlySpan<int> cols, int maxScan)
    {
        for (var y = height - 1; y >= height - maxScan && y >= 0; y--)
        {
            if (!IsUniformBarRow(bgra, width, stride, cols, y))
                return Math.Max(0, height - 1 - y - 1);
        }

        return 0;
    }

    /// <summary>
    /// True letterbox columns are uniformly black with at most a single stray bright sample line.
    /// </summary>
    private static bool IsUniformBarColumn(ReadOnlySpan<byte> bgra, int height, int stride, ReadOnlySpan<int> rows, int x)
    {
        var blackLines = 0;
        var contentLines = 0;
        var lumaSum = 0;
        var samples = 0;

        foreach (var y in rows)
        {
            if (y < 0 || y >= height)
                continue;

            var offset = y * stride + x * 4;
            if (offset + 3 >= bgra.Length)
                continue;

            samples++;
            var pixel = bgra.Slice(offset, 4);
            var luma = ChannelMax(pixel);
            lumaSum += luma;

            if (IsBorderBlack(pixel))
                blackLines++;
            else if (IsContentPixel(pixel))
                contentLines++;
        }

        if (samples == 0)
            return true;

        if (contentLines > MaxContentLinesInBarColumn)
            return false;

        if (lumaSum / samples > 8)
            return false;

        return blackLines >= (int)Math.Ceiling(samples * BarLineRatio);
    }

    private static bool IsUniformBarRow(ReadOnlySpan<byte> bgra, int width, int stride, ReadOnlySpan<int> cols, int y)
    {
        var blackLines = 0;
        var contentLines = 0;
        var lumaSum = 0;
        var samples = 0;

        foreach (var x in cols)
        {
            if (x < 0 || x >= width)
                continue;

            var offset = y * stride + x * 4;
            if (offset + 3 >= bgra.Length)
                continue;

            samples++;
            var pixel = bgra.Slice(offset, 4);
            var luma = ChannelMax(pixel);
            lumaSum += luma;

            if (IsBorderBlack(pixel))
                blackLines++;
            else if (IsContentPixel(pixel))
                contentLines++;
        }

        if (samples == 0)
            return true;

        if (contentLines > MaxContentLinesInBarColumn)
            return false;

        if (lumaSum / samples > 8)
            return false;

        return blackLines >= (int)Math.Ceiling(samples * BarLineRatio);
    }

    private static void ValidateAndClampInsets(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        ref int left,
        ref int right,
        ref int top,
        ref int bottom)
    {
        if (left < MinBarPixels) left = 0;
        if (right < MinBarPixels) right = 0;
        if (top < MinBarPixels) top = 0;
        if (bottom < MinBarPixels) bottom = 0;

        var maxCropX = Math.Max(MinBarPixels, (int)(width * MaxCropWidthRatioPerSide));
        var maxCropY = Math.Max(MinBarPixels, (int)(height * MaxCropHeightRatioPerSide));
        left = Math.Min(left, maxCropX);
        right = Math.Min(right, maxCropX);
        top = Math.Min(top, maxCropY);
        bottom = Math.Min(bottom, maxCropY);

        var contentW = width - left - right;
        var contentH = height - top - bottom;

        if (contentW < width * MinContentWidthRatio || contentH < height * MinContentHeightRatio)
        {
            left = right = top = bottom = 0;
            return;
        }

        if (left > 0 && !IsUniformBlackRegion(bgra, width, height, stride, 0, left, 0, height))
            left = 0;
        if (right > 0 && !IsUniformBlackRegion(bgra, width, height, stride, width - right, width, 0, height))
            right = 0;
        if (top > 0 && !IsUniformBlackRegion(bgra, width, height, stride, 0, width, 0, top))
            top = 0;
        if (bottom > 0 && !IsUniformBlackRegion(bgra, width, height, stride, 0, width, height - bottom, height))
            bottom = 0;

        contentW = width - left - right;
        contentH = height - top - bottom;
        if (contentW < width * MinContentWidthRatio || contentH < height * MinContentHeightRatio)
        {
            left = right = top = bottom = 0;
        }
    }

    private static bool IsUniformBlackRegion(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        int x0,
        int x1,
        int y0,
        int y1)
    {
        if (x1 <= x0 || y1 <= y0)
            return false;

        var stepX = Math.Max(1, (x1 - x0) / 8);
        var stepY = Math.Max(1, (y1 - y0) / 8);
        var samples = 0;
        var blackSamples = 0;

        for (var y = y0; y < y1; y += stepY)
        {
            for (var x = x0; x < x1; x += stepX)
            {
                var offset = y * stride + x * 4;
                if (offset + 3 >= bgra.Length)
                    continue;

                samples++;
                if (IsBorderBlack(bgra.Slice(offset, 4)))
                    blackSamples++;
            }
        }

        return samples > 0 && blackSamples >= (int)Math.Ceiling(samples * 0.97);
    }
}
