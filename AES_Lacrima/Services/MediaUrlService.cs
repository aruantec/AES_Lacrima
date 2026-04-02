using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima.Services
{
    /// <summary>
    /// Service for handling media URLs, specifically for online streaming content.
    /// </summary>
    public interface IMediaUrlService;

    internal sealed record ResolvedMediaSource(string VideoUrl, string AudioUrl, double? AspectRatio)
    {
        public (string, string) OnlineUrls => (VideoUrl, AudioUrl);
    }

    /// <summary>
    /// Implementation of <see cref="IMediaUrlService"/> that uses yt-dlp to resolve streaming URLs.
    /// </summary>
    [AutoRegister]
    internal partial class MediaUrlService : ViewModelBase, IMediaUrlService
    {
        /// <summary>
        /// Opens a media item, resolving its online stream URLs if necessary, and starts playback.
        /// </summary>
        /// <param name="audioPlayer">The audio player instance to use for playback.</param>
        /// <param name="item">The media item to open and play.</param>
        /// <param name="preferVideo">When true, uses the resolved video stream for playback.</param>
        public async Task OpenMediaItemAsync(AudioPlayer audioPlayer, MediaItem item, bool preferVideo = false)
        {
            if (item.FileName == null) return;
            // Notify the UI instantly that media loading has started
            audioPlayer.IsLoadingMedia = true;

            // Load online urls
            var resolvedSource = await ResolveMediaSourceAsync(item.FileName, preferVideo).ConfigureAwait(false);
            item.OnlineUrls = resolvedSource?.OnlineUrls;
            // Play media (audio by default, video when requested)
            await audioPlayer.PlayFile(item, preferVideo).ConfigureAwait(false);
        }

        internal async Task<ResolvedMediaSource?> ResolveMediaSourceAsync(string url, bool preferVideo)
        {
            try
            {
                const int targetHeight = 1080;

                var currentUrl = YouTubeThumbnail.GetCleanVideoLink(url);
                var info = await YtDlpMetadata.GetMetaDataAsync(currentUrl).ConfigureAwait(false);

                var bestMuxed = info.MuxedFormats
                    .Where(m => !string.IsNullOrWhiteSpace(m.Url) && (m.Height ?? 0) > 0)
                    .OrderBy(m => m.Height == targetHeight ? 0 : 1)
                    .ThenBy(m => (m.Height ?? 0) < targetHeight ? 1 : 0)
                    .ThenBy(m => Math.Abs((m.Height ?? targetHeight) - targetHeight))
                    .ThenByDescending(m => m.Height ?? 0)
                    .ThenByDescending(m => m.Fps ?? 0)
                    .FirstOrDefault();

                var bestVideo = info.VideoFormats
                    .Where(v => !string.IsNullOrWhiteSpace(v.Url) && (v.Height ?? 0) > 0)
                    .OrderBy(v => v.Height == targetHeight ? 0 : 1)
                    .ThenBy(v => (v.Height ?? 0) < targetHeight ? 1 : 0)
                    .ThenBy(v => Math.Abs((v.Height ?? targetHeight) - targetHeight))
                    .ThenByDescending(v => v.Height ?? 0)
                    .ThenByDescending(v => v.Fps ?? 0)
                    .FirstOrDefault();

                var bestAudio = info.AudioFormats
                    .Where(a => !string.IsNullOrWhiteSpace(a.Url))
                    .OrderByDescending(a => a.Bitrate ?? 0)
                    .FirstOrDefault();

                string videoUrl = bestVideo?.Url ?? bestMuxed?.Url ?? string.Empty;
                string audioUrl = bestAudio?.Url ?? bestMuxed?.Url ?? string.Empty;

                int? width = bestVideo?.Width ?? bestMuxed?.Width;
                int? height = bestVideo?.Height ?? bestMuxed?.Height;

                if (preferVideo && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
                {
                    videoUrl = bestMuxed.Url;
                    audioUrl = bestMuxed.Url;
                    width = bestMuxed.Width;
                    height = bestMuxed.Height;
                }
                else if (string.IsNullOrWhiteSpace(bestVideo?.Url) && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
                {
                    audioUrl = bestMuxed.Url;
                    width = bestMuxed.Width;
                    height = bestMuxed.Height;
                }

                if (string.IsNullOrWhiteSpace(videoUrl) && string.IsNullOrWhiteSpace(audioUrl))
                    return null;

                double? aspectRatio = width > 0 && height > 0
                    ? Math.Round(width.Value / (double)height.Value, 4)
                    : null;

                return new ResolvedMediaSource(videoUrl, audioUrl, aspectRatio);
            }
            catch (Exception ex)
            {
                AES_Core.Logging.LogHelper.For<MediaUrlService>().Error($"Fetch failed after retries: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Resolves the best available video and audio stream URLs for a given source URL using yt-dlp.
        /// </summary>
        /// <param name="url">The source URL to process.</param>
        /// <returns>A tuple containing the video URL and audio URL.</returns>
        private async Task<(string, string)> HandleStreamFile(string url, bool preferVideo)
        {
            var resolved = await ResolveMediaSourceAsync(url, preferVideo).ConfigureAwait(false);
            return resolved?.OnlineUrls ?? (string.Empty, string.Empty);
        }
    }
}
