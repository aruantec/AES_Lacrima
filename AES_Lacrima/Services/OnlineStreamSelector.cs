using System.Collections.Generic;
using AES_Controls.Helpers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima.Services;

internal static class OnlineStreamSelector
{
    internal const int DefaultTargetHeight = 1080;

    internal const string StandardMuxedStreamFormat = "best[height<=720]/best";

    internal const string AudioOnlyStreamFormat = "bestaudio/best";

    internal const string HighQualityStreamFormat =
        "bestvideo[height<=1080]+bestaudio/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best";

    private static readonly string[] HighQualityStreamFormats =
    [
        "bestvideo[height<=1080][vcodec^=avc1]+bestaudio/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best",
        HighQualityStreamFormat
    ];

    private static readonly YtDlpExtractorProfile[] HighQualityProfiles =
    [
        YtDlpExtractorProfile.HighQualityStreams,
        YtDlpExtractorProfile.Unrestricted
    ];

    internal static async Task<ResolvedMediaSource?> ResolveStandardMuxedStreamAsync(string currentUrl, string originalUrl)
    {
        IReadOnlyList<string> urls;
        try
        {
            urls = await GetStreamUrlsAsync(currentUrl, originalUrl, StandardMuxedStreamFormat, YtDlpExtractorProfile.Default)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (urls.Count == 0 || string.IsNullOrWhiteSpace(urls[0]))
            return null;

        return new ResolvedMediaSource(urls[0], urls[0], null);
    }

    internal static async Task<ResolvedMediaSource?> ResolveAudioOnlyStreamAsync(string currentUrl, string originalUrl)
    {
        IReadOnlyList<string> urls;
        try
        {
            urls = await GetStreamUrlsAsync(currentUrl, originalUrl, AudioOnlyStreamFormat, YtDlpExtractorProfile.Default)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (urls.Count == 0 || string.IsNullOrWhiteSpace(urls[0]))
            return null;

        return new ResolvedMediaSource(string.Empty, urls[0], null);
    }

    internal const int MinimumHighQualityHeight = 720;

    internal static ResolvedMediaSource? SelectSeparateHighQuality(
        MediaInfo info,
        int targetHeight = DefaultTargetHeight)
    {
        var bestVideo = SelectBestVideo(info.VideoFormats, targetHeight);
        var bestAudio = SelectBestAudio(info.AudioFormats);

        if (bestVideo == null
            || bestAudio == null
            || string.IsNullOrWhiteSpace(bestVideo.Url)
            || string.IsNullOrWhiteSpace(bestAudio.Url)
            || string.Equals(bestVideo.Url, bestAudio.Url, StringComparison.Ordinal))
        {
            return null;
        }

        if ((bestVideo.Height ?? 0) > 0 && bestVideo.Height < MinimumHighQualityHeight)
            return null;

        var candidate = new[] { bestVideo.Url, bestAudio.Url };
        if (!OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet(candidate))
            return null;

        double? aspectRatio = bestVideo.Width > 0 && bestVideo.Height > 0
            ? Math.Round(bestVideo.Width.Value / (double)bestVideo.Height.Value, 4)
            : null;

        return new ResolvedMediaSource(bestVideo.Url, bestAudio.Url, aspectRatio);
    }

    internal static async Task<ResolvedMediaSource?> ResolveHighQualityStreamsAsync(string url)
    {
        var currentUrl = YouTubeThumbnail.GetCleanVideoLink(url);
        IReadOnlyList<string>? streamUrls = null;
        YtDlpExtractorProfile? resolvedProfile = null;
        string? resolvedFormat = null;

        foreach (var profile in HighQualityProfiles)
        {
            foreach (var format in HighQualityStreamFormats)
            {
                IReadOnlyList<string> candidate;
                try
                {
                    candidate = await GetStreamUrlsAsync(currentUrl, url, format, profile).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                        $"HQ stream probe failed for '{url}' (profile={profile}, format='{format}').",
                        ex);
                    continue;
                }

                if (!OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet(candidate))
                    continue;

                streamUrls = candidate;
                resolvedProfile = profile;
                resolvedFormat = format;
                break;
            }

            if (streamUrls != null)
                break;
        }

        if (streamUrls == null)
        {
            AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                $"HQ -g resolution could not verify separate 720p+ streams for '{url}'.");
            return null;
        }

        var videoUrl = streamUrls[0];
        var audioUrl = streamUrls[1];
        var videoItag = OnlineStreamUrlHelper.TryGetItag(videoUrl);
        var audioItag = OnlineStreamUrlHelper.TryGetItag(audioUrl);

        MediaInfo? info = null;
        try
        {
            info = await YtDlpMetadata.GetMetaDataAsync(currentUrl, resolvedProfile ?? YtDlpExtractorProfile.HighQualityStreams)
                .ConfigureAwait(false);
        }
        catch
        {
            if (!string.Equals(currentUrl, url, StringComparison.Ordinal))
            {
                try
                {
                    info = await YtDlpMetadata.GetMetaDataAsync(url, resolvedProfile ?? YtDlpExtractorProfile.HighQualityStreams)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Aspect ratio is optional for playback.
                }
            }
        }

        double? aspectRatio = null;
        if (info != null)
        {
            var selected = Select(info, preferVideo: true, useHighQualityStream: true);
            aspectRatio = selected?.AspectRatio;
        }

        AES_Core.Logging.LogHelper.For<MediaUrlService>().Info(
            $"Resolved HQ stream for '{url}': profile={resolvedProfile}, format='{resolvedFormat}', videoItag={videoItag}, audioItag={audioItag}.");

        return new ResolvedMediaSource(videoUrl, audioUrl, aspectRatio);
    }

    private static async Task<IReadOnlyList<string>> GetStreamUrlsAsync(
        string currentUrl,
        string originalUrl,
        string format,
        YtDlpExtractorProfile profile)
    {
        try
        {
            return await YtDlpMetadata.GetStreamUrlsAsync(currentUrl, format, profile).ConfigureAwait(false);
        }
        catch when (!string.Equals(currentUrl, originalUrl, StringComparison.Ordinal))
        {
            return await YtDlpMetadata.GetStreamUrlsAsync(originalUrl, format, profile).ConfigureAwait(false);
        }
    }

    internal static ResolvedMediaSource? Select(
        MediaInfo info,
        bool preferVideo,
        bool useHighQualityStream,
        int targetHeight = DefaultTargetHeight)
    {
        if (preferVideo && useHighQualityStream)
            return SelectSeparateHighQuality(info, targetHeight);

        var bestMuxed = SelectBestMuxed(info.MuxedFormats, targetHeight);
        var bestVideo = SelectBestVideo(info.VideoFormats, targetHeight);
        var bestAudio = SelectBestAudio(info.AudioFormats);

        string videoUrl;
        string audioUrl;
        int? width;
        int? height;

        if (preferVideo && !useHighQualityStream && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
        {
            videoUrl = bestMuxed.Url;
            audioUrl = bestMuxed.Url;
            width = bestMuxed.Width;
            height = bestMuxed.Height;
        }
        else
        {
            videoUrl = bestVideo?.Url ?? bestMuxed?.Url ?? string.Empty;
            audioUrl = bestAudio?.Url ?? string.Empty;

            if (preferVideo && string.IsNullOrWhiteSpace(videoUrl) && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
            {
                videoUrl = bestMuxed.Url;
                width = bestMuxed.Width;
                height = bestMuxed.Height;
            }
            else
            {
                width = bestVideo?.Width ?? bestMuxed?.Width;
                height = bestVideo?.Height ?? bestMuxed?.Height;
            }

            if (string.IsNullOrWhiteSpace(audioUrl))
                audioUrl = bestMuxed?.Url ?? videoUrl;
        }

        if (string.IsNullOrWhiteSpace(videoUrl) && string.IsNullOrWhiteSpace(audioUrl))
            return null;

        double? aspectRatio = width > 0 && height > 0
            ? Math.Round(width.Value / (double)height.Value, 4)
            : null;

        return new ResolvedMediaSource(videoUrl, audioUrl, aspectRatio);
    }

    internal static VideoFormat? SelectBestVideo(IReadOnlyList<VideoFormat> formats, int targetHeight)
    {
        var eligible = formats
            .Where(v => !string.IsNullOrWhiteSpace(v.Url) && (v.Height ?? 0) > 0)
            .ToList();

        if (eligible.Count == 0)
            return null;

        var capped = eligible.Where(v => (v.Height ?? 0) <= targetHeight).ToList();
        var candidates = capped.Count > 0 ? capped : eligible;

        return candidates
            .OrderBy(v => v.Height == targetHeight ? 0 : 1)
            .ThenBy(v => (v.Height ?? 0) < targetHeight ? 1 : 0)
            .ThenBy(v => Math.Abs((v.Height ?? targetHeight) - targetHeight))
            .ThenByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.Fps ?? 0)
            .FirstOrDefault();
    }

    internal static AudioFormat? SelectBestAudio(IReadOnlyList<AudioFormat> formats) =>
        formats
            .Where(a => !string.IsNullOrWhiteSpace(a.Url))
            .OrderByDescending(a => a.Bitrate ?? 0)
            .FirstOrDefault();

    internal static MuxedFormat? SelectBestMuxed(IReadOnlyList<MuxedFormat> formats, int targetHeight) =>
        formats
            .Where(m => !string.IsNullOrWhiteSpace(m.Url) && (m.Height ?? 0) > 0)
            .OrderBy(m => m.Height == targetHeight ? 0 : 1)
            .ThenBy(m => (m.Height ?? 0) < targetHeight ? 1 : 0)
            .ThenBy(m => Math.Abs((m.Height ?? targetHeight) - targetHeight))
            .ThenByDescending(m => m.Height ?? 0)
            .ThenByDescending(m => m.Fps ?? 0)
            .FirstOrDefault();
}
