using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace AES_Lacrima.Services;

/// <summary>
/// Scores auto-cover search results so retailer mockups and low-quality listing photos are deprioritized.
/// </summary>
internal static class AutoCoverImageHeuristics
{
    private static readonly string[] SellerHostTokens =
    [
        "amazon.", "ebay.", "walmart.", "bestbuy.", "target.", "gamestop.",
        "aliexpress.", "alibaba.", "etsy.", "mercari.", "rakuten.",
        "cdiscount.", "fnac.", "mediamarkt.", "saturn.", "bol.com"
    ];

    private static readonly string[] SellerPathTokens =
    [
        "/product/", "/listing/", "/shop/", "/store/", "/buy/", "/item/",
        "mockup", "case-mockup", "3d-case", "plastic-case", "jewel-case",
        "product-shot", "packaging", "retail", "seller", "marketplace",
        "thumbnail", "thumb/", "/thumbs/", "sprite/icon"
    ];

    private static readonly string[] PreferredHostTokens =
    [
        "igdb.com", "thegamesdb.net", "gametdb.com", "libretro.com",
        "launchbox-app.com", "mobygames.com", "rawg.io", "steamgriddb.com",
        "art.gametdb.com", "switchdb", "nintendo.com", "wikimedia.org",
        "wikipedia.org", "githubusercontent.com"
    ];

    private static readonly string[] PreferredPathTokens =
    [
        "box-art", "boxart", "box_art", "cover-art", "coverart", "key-art",
        "front-cover", "game-cover", "cover", "box", "artwork", "keyart"
    ];

    public static IReadOnlyList<WebImageSearchResult> RankCandidates(IReadOnlyList<WebImageSearchResult> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        return candidates
            .Select(candidate => (candidate, Score: ScoreCandidate(candidate)))
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.candidate.FullImageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.candidate)
            .ToList();
    }

    public static bool ShouldRejectDownloadedImage(byte[] bytes, int width, int height)
    {
        if (bytes.Length < 8 * 1024)
            return true;

        if (width < 200 || height < 200)
            return true;

        var aspect = width / (double)height;
        if (aspect < 0.45 || aspect > 2.2)
            return true;

        return false;
    }

    public static int ScoreCandidate(WebImageSearchResult candidate)
    {
        var url = candidate.FullImageUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return int.MinValue;

        var score = 0;
        var lower = url.ToLowerInvariant();

        foreach (var token in SellerHostTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                score -= 80;
        }

        foreach (var token in SellerPathTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                score -= 45;
        }

        foreach (var token in PreferredHostTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                score += 35;
        }

        foreach (var token in PreferredPathTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                score += 25;
        }

        if (lower.Contains("icon", StringComparison.Ordinal) || lower.Contains("logo", StringComparison.Ordinal))
            score -= 30;

        if (lower.Contains("wallpaper", StringComparison.Ordinal) || lower.Contains("screenshot", StringComparison.Ordinal))
            score -= 20;

        if (lower.Contains("4k", StringComparison.Ordinal) || lower.Contains("uhd", StringComparison.Ordinal) ||
            lower.Contains("3840", StringComparison.Ordinal) || lower.Contains("2160", StringComparison.Ordinal) ||
            lower.Contains("raw", StringComparison.Ordinal) || lower.Contains("original/full", StringComparison.Ordinal))
            score -= 35;

        if (lower.EndsWith(".png", StringComparison.Ordinal) || lower.EndsWith(".webp", StringComparison.Ordinal))
            score += 8;

        if (lower.EndsWith(".jpg", StringComparison.Ordinal) || lower.EndsWith(".jpeg", StringComparison.Ordinal))
            score += 12;

        if (!string.IsNullOrWhiteSpace(candidate.ThumbnailUrl))
        {
            var thumb = candidate.ThumbnailUrl.ToLowerInvariant();
            if (thumb.Contains("=s128", StringComparison.Ordinal) || thumb.Contains("=s256", StringComparison.Ordinal) ||
                thumb.Contains("w=200", StringComparison.Ordinal) || thumb.Contains("w=300", StringComparison.Ordinal))
                score += 18;
        }

        score += AutoCoverImageHeuristics.ScoreDownloadSpeed(url);

        if (lower.Contains("=s", StringComparison.Ordinal) || lower.Contains("w=", StringComparison.Ordinal) || lower.Contains("width=", StringComparison.Ordinal))
        {
            if (lower.Contains("w=75", StringComparison.Ordinal) || lower.Contains("w=100", StringComparison.Ordinal) ||
                lower.Contains("=s64", StringComparison.Ordinal) || lower.Contains("=s128", StringComparison.Ordinal))
            {
                score -= 40;
            }
            else if (lower.Contains("w=300", StringComparison.Ordinal) || lower.Contains("w=400", StringComparison.Ordinal) ||
                     lower.Contains("=s512", StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }

    /// <summary>
    /// Prefers webp/jpeg over png while preserving original search order as a tie-breaker.
    /// </summary>
    public static IReadOnlyList<WebImageSearchResult> OrderByPreferredFormat(IReadOnlyList<WebImageSearchResult> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        return candidates
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(pair => ScorePreferredImageFormat(pair.candidate.FullImageUrl))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.candidate)
            .ToList();
    }

    public static int ScorePreferredImageFormat(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return 0;

        var lower = url.ToLowerInvariant();
        if (lower.Contains(".webp", StringComparison.Ordinal) ||
            lower.Contains("format=webp", StringComparison.Ordinal) ||
            lower.Contains("type=webp", StringComparison.Ordinal))
            return 30;

        if (lower.Contains(".jpg", StringComparison.Ordinal) ||
            lower.Contains(".jpeg", StringComparison.Ordinal) ||
            lower.Contains("format=jpg", StringComparison.Ordinal) ||
            lower.Contains("format=jpeg", StringComparison.Ordinal))
            return 28;

        if (lower.Contains(".png", StringComparison.Ordinal) ||
            lower.Contains("format=png", StringComparison.Ordinal))
            return 8;

        return 18;
    }

    public static bool ShouldSkipSlowDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        var lower = url.ToLowerInvariant();
        if (lower.Contains("4k", StringComparison.Ordinal) || lower.Contains("uhd", StringComparison.Ordinal) ||
            lower.Contains("3840", StringComparison.Ordinal) || lower.Contains("2160", StringComparison.Ordinal) ||
            lower.Contains("wallpaper", StringComparison.Ordinal) || lower.Contains("/raw/", StringComparison.Ordinal) ||
            lower.Contains("original/full", StringComparison.Ordinal))
            return true;

        if (lower.Contains("width=3840", StringComparison.Ordinal) || lower.Contains("width=2560", StringComparison.Ordinal) ||
            lower.Contains("w=3840", StringComparison.Ordinal) || lower.Contains("w=2560", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Skips obvious marketplace listing URLs before attempting a download.
    /// </summary>
    public static bool ShouldSkipSearchResultUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (ShouldSkipSlowDownloadUrl(url))
            return true;

        var lower = url.ToLowerInvariant();
        foreach (var token in SellerHostTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }

        foreach (var token in SellerPathTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Rejects seller product photos where the subject sits on a large uniform border instead of filling the frame.
    /// CPU-only Skia path — must not touch Avalonia compositor/GPU during background lookup.
    /// </summary>
    public static bool LooksLikeMarketplacePhoto(byte[] imageBytes)
    {
        if (imageBytes.Length == 0)
            return false;

        try
        {
            using var source = SKBitmap.Decode(imageBytes);
            return source != null && LooksLikeMarketplacePhoto(source);
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksLikeMarketplacePhoto(SKBitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return false;

        try
        {
            const int sampleWidth = 72;
            const int sampleHeight = 96;
            using var sample = source.Resize(
                new SKImageInfo(sampleWidth, sampleHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                SKSamplingOptions.Default);
            if (sample == null)
                return false;

            int bgR = 0, bgG = 0, bgB = 0;
            ReadCornerColor(sample, 0, 0, ref bgR, ref bgG, ref bgB);
            ReadCornerColor(sample, sampleWidth - 3, 0, ref bgR, ref bgG, ref bgB);
            ReadCornerColor(sample, 0, sampleHeight - 3, ref bgR, ref bgG, ref bgB);
            ReadCornerColor(sample, sampleWidth - 3, sampleHeight - 3, ref bgR, ref bgG, ref bgB);
            bgR /= 4;
            bgG /= 4;
            bgB /= 4;

            int minX = sampleWidth;
            int minY = sampleHeight;
            int maxX = -1;
            int maxY = -1;
            int borderMatches = 0;
            int borderSamples = 0;

            for (int y = 0; y < sampleHeight; y++)
            {
                for (int x = 0; x < sampleWidth; x++)
                {
                    var color = sample.GetPixel(x, y);
                    byte r = color.Red;
                    byte g = color.Green;
                    byte b = color.Blue;
                    byte a = color.Alpha;
                    if (a < 16)
                        continue;

                    bool isBackground = IsNearColor(r, g, b, bgR, bgG, bgB, 42);
                    bool onBorder = x < 8 || y < 8 || x >= sampleWidth - 8 || y >= sampleHeight - 8;
                    if (onBorder)
                    {
                        borderSamples++;
                        if (isBackground)
                            borderMatches++;
                    }

                    if (isBackground)
                        continue;

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
                return false;

            double contentWidthRatio = (maxX - minX + 1) / (double)sampleWidth;
            double contentHeightRatio = (maxY - minY + 1) / (double)sampleHeight;
            double contentAreaRatio = contentWidthRatio * contentHeightRatio;
            double borderMatchRatio = borderSamples == 0 ? 0 : borderMatches / (double)borderSamples;

            if (contentAreaRatio < 0.42)
                return true;

            if (contentWidthRatio < 0.58 || contentHeightRatio < 0.58)
            {
                if (borderMatchRatio > 0.72)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadCornerColor(SKBitmap bitmap, int x, int y, ref int r, ref int g, ref int b)
    {
        var color = bitmap.GetPixel(x, y);
        r += color.Red;
        g += color.Green;
        b += color.Blue;
    }

    private static bool IsNearColor(byte r, byte g, byte b, int bgR, int bgG, int bgB, int tolerance)
    {
        return Math.Abs(r - bgR) <= tolerance
            && Math.Abs(g - bgG) <= tolerance
            && Math.Abs(b - bgB) <= tolerance;
    }

    public static int ScoreDownloadSpeed(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return 0;

        var lower = url.ToLowerInvariant();
        var score = 0;

        if (lower.Contains("thumb", StringComparison.Ordinal) ||
            lower.Contains("thumbnail", StringComparison.Ordinal) ||
            lower.Contains("/small/", StringComparison.Ordinal) ||
            lower.Contains("/medium/", StringComparison.Ordinal))
            score += 20;

        if (lower.Contains("w=200", StringComparison.Ordinal) || lower.Contains("w=300", StringComparison.Ordinal) ||
            lower.Contains("w=400", StringComparison.Ordinal) || lower.Contains("=s256", StringComparison.Ordinal) ||
            lower.Contains("=s512", StringComparison.Ordinal))
            score += 15;

        if (lower.Contains("cdn.", StringComparison.Ordinal) || lower.Contains("static.", StringComparison.Ordinal))
            score += 8;

        if (lower.Contains("4k", StringComparison.Ordinal) || lower.Contains("3840", StringComparison.Ordinal) ||
            lower.Contains("original", StringComparison.Ordinal) || lower.Contains("/raw/", StringComparison.Ordinal))
            score -= 25;

        return score;
    }
}
