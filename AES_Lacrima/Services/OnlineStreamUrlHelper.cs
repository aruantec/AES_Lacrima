using System;
using System.Collections.Generic;

namespace AES_Lacrima.Services;

internal static class OnlineStreamUrlHelper
{
    private static readonly HashSet<int> LowQualityItags = [5, 6, 17, 18, 34, 35, 36, 43];

    // Separate DASH video streams below 720p (and 480p video-only).
    private static readonly HashSet<int> LowQualityVideoOnlyItags = [133, 134, 135, 242, 243, 278];

    internal static int? TryGetItag(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("itag=", StringComparison.Ordinal))
                continue;

            return int.TryParse(part.AsSpan(5), out var itag) ? itag : null;
        }

        return null;
    }

    internal static bool IsHighQualityVideoItag(int? itag) =>
        itag is int value
        && !LowQualityItags.Contains(value)
        && !LowQualityVideoOnlyItags.Contains(value);

    internal static bool IsVerifiedHighQualityStreamSet(IReadOnlyList<string> urls)
    {
        if (urls.Count < 2)
            return false;

        var videoUrl = urls[0];
        var audioUrl = urls[1];
        if (string.IsNullOrWhiteSpace(videoUrl) || string.IsNullOrWhiteSpace(audioUrl))
            return false;

        if (string.Equals(videoUrl, audioUrl, StringComparison.Ordinal))
            return false;

        var videoItag = TryGetItag(videoUrl);
        if (videoItag is int itag && (LowQualityItags.Contains(itag) || LowQualityVideoOnlyItags.Contains(itag)))
            return false;

        var hasVideoMime = videoUrl.Contains("mime=video", StringComparison.OrdinalIgnoreCase);
        var hasAudioMime = audioUrl.Contains("mime=audio", StringComparison.OrdinalIgnoreCase);
        if (hasVideoMime && hasAudioMime)
            return true;

        // Some clients omit mime= in -g output; accept verified separate googlevideo streams.
        return videoItag != null
               && videoUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase)
               && audioUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase);
    }
}
