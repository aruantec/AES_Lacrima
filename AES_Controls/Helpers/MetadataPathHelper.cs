using AES_Core.IO;

namespace AES_Controls.Helpers;

/// <summary>
/// Normalizes local file paths and online stream URLs for metadata cache keys.
/// </summary>
public static class MetadataPathHelper
{
    public static bool IsOnlineMediaPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (MediaCoverPaths.IsOnlineOrMissingMediaFile(path))
            return true;

        return TryExtractStreamUrl(path) != null;
    }

    /// <summary>
    /// Recovers an http(s) URL that may have been prefixed with a filesystem path.
    /// </summary>
    public static string? TryExtractStreamUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var httpIndex = trimmed.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (httpIndex < 0)
            httpIndex = trimmed.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (httpIndex < 0)
            httpIndex = trimmed.IndexOf("https:/", StringComparison.OrdinalIgnoreCase);
        if (httpIndex < 0)
            httpIndex = trimmed.IndexOf("http:/", StringComparison.OrdinalIgnoreCase);

        if (httpIndex < 0)
            return null;

        var candidate = trimmed[httpIndex..];
        if (candidate.StartsWith("https:/", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate["https:/".Length..];
        }
        else if (candidate.StartsWith("http:/", StringComparison.OrdinalIgnoreCase) &&
                 !candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "http://" + candidate["http:/".Length..];
        }

        return candidate;
    }

    public static string NormalizeMetadataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var streamUrl = TryExtractStreamUrl(path);
        if (!string.IsNullOrWhiteSpace(streamUrl))
            return YouTubeThumbnail.GetCleanVideoLink(streamUrl);

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    public static string GetMetadataCachePath(string? path)
    {
        var normalized = NormalizeMetadataPath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return ApplicationPaths.GetCacheFile(BinaryMetadataHelper.GetCacheId(normalized) + ".meta");
    }

    public static CustomMetadata? TryLoadMetadata(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var candidates = new List<string>();
        var normalized = NormalizeMetadataPath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
            candidates.Add(normalized);

        var trimmed = path.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !candidates.Contains(trimmed, StringComparer.Ordinal))
        {
            candidates.Add(trimmed);
        }

        var extracted = TryExtractStreamUrl(path);
        if (!string.IsNullOrWhiteSpace(extracted))
        {
            var cleaned = YouTubeThumbnail.GetCleanVideoLink(extracted);
            if (!candidates.Contains(cleaned, StringComparer.Ordinal))
                candidates.Add(cleaned);
        }

        foreach (var candidate in candidates)
        {
            var cachePath = ApplicationPaths.GetCacheFile(BinaryMetadataHelper.GetCacheId(candidate) + ".meta");
            if (!File.Exists(cachePath))
                continue;

            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
            if (metadata != null)
                return metadata;
        }

        return null;
    }
}
