using System;
using System.Collections.Generic;
using System.Linq;

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
}
