using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.Services
{
    public interface IMediaUrlService;

    internal sealed record ResolvedMediaSource(string VideoUrl, string AudioUrl, double? AspectRatio, string? MuxedFallbackUrl = null)
    {
        public (string, string) OnlineUrls => (VideoUrl, AudioUrl);

        public bool UsesSeparateStreams =>
            !string.IsNullOrWhiteSpace(VideoUrl)
            && !string.IsNullOrWhiteSpace(AudioUrl)
            && !string.Equals(VideoUrl, AudioUrl, StringComparison.Ordinal);
    }

    [AutoRegister]
    internal partial class MediaUrlService : ViewModelBase, IMediaUrlService
    {
        public async Task<bool> OpenMediaItemAsync(
            AudioPlayer audioPlayer,
            MediaItem item,
            bool preferVideo = false,
            bool useHighQualityStream = false)
        {
            if (item.FileName == null)
                return false;

            audioPlayer.IsLoadingMedia = true;

            var resolvedSource = await ResolveMediaSourceAsync(item.FileName, preferVideo, useHighQualityStream)
                .ConfigureAwait(false);
            if (resolvedSource == null)
            {
                audioPlayer.IsLoadingMedia = false;
                return false;
            }

            item.OnlineUrls = resolvedSource.OnlineUrls;
            item.MuxedStreamFallbackUrl = resolvedSource.MuxedFallbackUrl;
            if (resolvedSource.UsesSeparateStreams
                && string.IsNullOrWhiteSpace(item.MuxedStreamFallbackUrl)
                && item.FileName != null)
            {
                var currentUrl = YouTubeThumbnail.GetCleanVideoLink(item.FileName);
                var muxed = await OnlineStreamSelector.ResolveStandardMuxedStreamAsync(currentUrl, item.FileName)
                    .ConfigureAwait(false);
                item.MuxedStreamFallbackUrl = muxed?.VideoUrl;
            }

            var videoItag = OnlineStreamUrlHelper.TryGetItag(resolvedSource.VideoUrl);
            var audioItag = OnlineStreamUrlHelper.TryGetItag(resolvedSource.AudioUrl);
            AES_Core.Logging.LogHelper.For<MediaUrlService>().Info(
                $"Resolved online stream for '{item.FileName}': hq={useHighQualityStream}, " +
                $"separate={resolvedSource.UsesSeparateStreams}, videoItag={videoItag}, audioItag={audioItag}.");

            try
            {
                await audioPlayer.PlayFile(item, preferVideo).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                    $"Failed to play resolved online media for '{item.FileName}'",
                    ex);
                audioPlayer.IsLoadingMedia = false;
                return false;
            }
        }

        internal async Task<ResolvedMediaSource?> ResolveMediaSourceAsync(
            string url,
            bool preferVideo,
            bool useHighQualityStream = false)
        {
            try
            {
                var currentUrl = YouTubeThumbnail.GetCleanVideoLink(url);

                if (preferVideo && useHighQualityStream)
                {
                    var hq = await OnlineStreamSelector.ResolveHighQualityStreamsAsync(url).ConfigureAwait(false);
                    if (hq != null)
                        return hq;

                    MediaInfo hqInfo;
                    try
                    {
                        hqInfo = await YtDlpMetadata.GetMetaDataAsync(currentUrl, YtDlpExtractorProfile.HighQualityStreams)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cleanedUrlError) when (!string.Equals(currentUrl, url, StringComparison.Ordinal))
                    {
                        AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                            $"HQ metadata failed for normalized URL. Retrying with original URL. Normalized='{currentUrl}', Original='{url}'",
                            cleanedUrlError);
                        hqInfo = await YtDlpMetadata.GetMetaDataAsync(url, YtDlpExtractorProfile.HighQualityStreams)
                            .ConfigureAwait(false);
                    }

                    var separate = OnlineStreamSelector.SelectSeparateHighQuality(hqInfo);
                    if (separate != null)
                        return separate;

                    AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                        $"HQ separate streams unavailable for '{url}'; falling back to standard muxed stream.");
                }

                var profile = YtDlpExtractorProfile.Default;
                var useHighQualityStreamForSelect = false;

                MediaInfo info;
                try
                {
                    info = await YtDlpMetadata.GetMetaDataAsync(currentUrl, profile).ConfigureAwait(false);
                }
                catch (Exception cleanedUrlError) when (!string.Equals(currentUrl, url, StringComparison.Ordinal))
                {
                    AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                        $"yt-dlp metadata failed for normalized URL. Retrying with original URL. Normalized='{currentUrl}', Original='{url}'",
                        cleanedUrlError);
                    info = await YtDlpMetadata.GetMetaDataAsync(url, profile).ConfigureAwait(false);
                }

                var selected = OnlineStreamSelector.Select(info, preferVideo, useHighQualityStreamForSelect);
                if (selected == null)
                {
                    AES_Core.Logging.LogHelper.For<MediaUrlService>().Warn(
                        $"yt-dlp returned no usable media formats for URL '{url}'. " +
                        $"VideoFormats={info.VideoFormats.Count}, AudioFormats={info.AudioFormats.Count}, MuxedFormats={info.MuxedFormats.Count}");
                }

                return selected;
            }
            catch (Exception ex)
            {
                AES_Core.Logging.LogHelper.For<MediaUrlService>().Error($"Fetch failed after retries: {ex.Message}", ex);
                return null;
            }
        }
    }
}
