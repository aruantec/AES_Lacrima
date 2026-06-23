using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Emulation;

internal static class LibRetroThumbnailCoverService
{
    private const string BaseUrl = "https://thumbnails.libretro.com/";
    internal static readonly string[] CoverArtExtensions = [".webp", ".jpg", ".png"];

    private static readonly string[] RegionSuffixes =
    [
        "(USA)",
        "(Europe)",
        "(World)",
        "(Japan)",
        "(Australia)",
        "(Canada)",
        "(France)",
        "(Germany)",
        "(Spain)",
        "(Italy)",
        "(Korea)",
        "(Brazil)",
        "(Netherlands)",
        "(Sweden)",
        "(Asia)"
    ];

    public static IEnumerable<string> BuildCoverUrls(string platformFolder, IEnumerable<string> titleCandidates)
    {
        if (string.IsNullOrWhiteSpace(platformFolder))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in titleCandidates)
        {
            foreach (var title in ExpandTitleVariants(candidate))
            {
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var encodedPlatform = Uri.EscapeDataString(platformFolder);
                var encodedTitle = Uri.EscapeDataString(title);
                foreach (var extension in CoverArtExtensions)
                {
                    var url = $"{BaseUrl}{encodedPlatform}/Named_Boxarts/{encodedTitle}{extension}";
                    if (seen.Add(url))
                        yield return url;
                }
            }
        }
    }

    public static async Task<LibRetroCoverDownloadResult?> TryDownloadCoverAsync(
        string? albumName,
        string? hasheousPlatformName,
        IEnumerable<string> titleCandidates,
        CancellationToken cancellationToken)
    {
        if (!LibRetroPlatformCatalog.TryResolveFolder(albumName, hasheousPlatformName, out var platformFolder))
            return null;

        foreach (var url in BuildCoverUrls(platformFolder, titleCandidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await EmulationCoverImageDownload.TryDownloadValidatedCoverAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (bytes == null)
                continue;

            var matchedTitle = ExtractTitleFromBoxArtUrl(url);
            if (string.IsNullOrWhiteSpace(matchedTitle))
                matchedTitle = titleCandidates.FirstOrDefault() ?? string.Empty;

            return new LibRetroCoverDownloadResult
            {
                Bytes = bytes,
                MatchedTitle = matchedTitle
            };
        }

        return null;
    }

    internal static string? ExtractTitleFromBoxArtUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        const string marker = "/Named_Boxarts/";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var encodedTitle = url[(index + marker.Length)..];
        foreach (var extension in CoverArtExtensions)
        {
            if (encodedTitle.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                encodedTitle = encodedTitle[..^extension.Length];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(encodedTitle))
            return null;

        return Uri.UnescapeDataString(encodedTitle).Trim();
    }

    internal static IEnumerable<string> ExpandTitleVariants(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            yield break;

        var trimmed = title.Trim();
        if (trimmed.Length == 0)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseTitle in new[] { trimmed, ToLibRetroDisplayTitle(trimmed) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(baseTitle))
                continue;

            if (seen.Add(baseTitle))
                yield return baseTitle;

            var withoutRegion = StripRegionSuffix(baseTitle);
            if (!string.Equals(withoutRegion, baseTitle, StringComparison.Ordinal) && seen.Add(withoutRegion))
                yield return withoutRegion;

            if (!ContainsRegionSuffix(baseTitle))
            {
                foreach (var suffix in RegionSuffixes)
                {
                    var withRegion = $"{withoutRegion} {suffix}".Trim();
                    if (seen.Add(withRegion))
                        yield return withRegion;
                }
            }
        }
    }

    private static string ToLibRetroDisplayTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var regionSuffix = string.Empty;
        foreach (var suffix in RegionSuffixes)
        {
            if (!title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            regionSuffix = suffix;
            title = title[..^suffix.Length].TrimEnd();
            break;
        }

        if (title != title.ToUpperInvariant())
            return string.IsNullOrEmpty(regionSuffix) ? title : $"{title} {regionSuffix}".Trim();

        var titleCased = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant());
        return string.IsNullOrEmpty(regionSuffix) ? titleCased : $"{titleCased} {regionSuffix}".Trim();
    }

    private static bool ContainsRegionSuffix(string title)
    {
        foreach (var suffix in RegionSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string StripRegionSuffix(string title)
    {
        foreach (var suffix in RegionSuffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return title[..^suffix.Length].TrimEnd();
            }
        }

        return title;
    }
}
