using System;
using System.Collections.Generic;
using System.Linq;
using AES_Code.Models;

namespace AES_Lacrima.Services;

/// <summary>
/// Ranks wallpaper and arcade bezel search results. Unlike cover search, wallpaper URLs are preferred.
/// </summary>
internal static class WallpaperBezelImageHeuristics
{
    private static readonly string[] PreferredTokens =
    [
        "bezel", "marquee", "cabinet", "side-art", "sideart", "side_art",
        "wide", "arcade art", "arcade-art", "control panel",
        "wallpaper", "background", "artwork", "promo", "flyer"
    ];

    private static readonly string[] BezelTokens =
    [
        "bezel", "marquee", "cabinet", "side-art", "sideart", "side_art",
        "wide", "control panel", "cpanel", "side panel"
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

    public static bool ShouldSkipSearchResultUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        if (AutoCoverImageHeuristics.ShouldSkipSearchResultUrl(url)
            && !ContainsAnyToken(url, "wallpaper", "bezel", "marquee", "cabinet"))
        {
            return true;
        }

        var lower = url.ToLowerInvariant();
        if (lower.Contains("cover-art", StringComparison.Ordinal)
            || lower.Contains("coverart", StringComparison.Ordinal)
            || lower.Contains("box-art", StringComparison.Ordinal)
            || lower.Contains("boxart", StringComparison.Ordinal)
            || lower.Contains("jewel-case", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static TagImageKind InferKind(string? url, int width = 0, int height = 0)
    {
        if (!string.IsNullOrWhiteSpace(url) && ContainsAnyToken(url, BezelTokens))
            return TagImageKind.BackCover;

        if (width > 0 && height > 0)
        {
            var aspect = width / (double)height;
            if (aspect >= 1.55)
                return TagImageKind.BackCover;
            if (aspect <= 0.85)
                return TagImageKind.Wallpaper;
        }

        if (!string.IsNullOrWhiteSpace(url) && url.Contains("wallpaper", StringComparison.OrdinalIgnoreCase))
            return TagImageKind.Wallpaper;

        return TagImageKind.BackCover;
    }

    private static int ScoreCandidate(WebImageSearchResult candidate)
    {
        var url = candidate.FullImageUrl ?? string.Empty;
        var lower = url.ToLowerInvariant();
        var score = 0;

        foreach (var token in PreferredTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                score += 14;
        }

        if (lower.Contains("wide", StringComparison.Ordinal))
            score += 10;

        if (lower.Contains("1920", StringComparison.Ordinal)
            || lower.Contains("2560", StringComparison.Ordinal)
            || lower.Contains("3840", StringComparison.Ordinal))
        {
            score += 8;
        }

        score += AutoCoverImageHeuristics.ScorePreferredImageFormat(url);
        score += AutoCoverImageHeuristics.ScoreDownloadSpeed(url);

        if (lower.Contains("cover-art", StringComparison.Ordinal)
            || lower.Contains("box-art", StringComparison.Ordinal)
            || lower.Contains("thumbnail", StringComparison.Ordinal))
        {
            score -= 30;
        }

        return score;
    }

    private static bool ContainsAnyToken(string url, params string[] tokens)
    {
        var lower = url.ToLowerInvariant();
        return tokens.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }
}
