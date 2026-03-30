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
            item.OnlineUrls = await HandleStreamFile(item.FileName, preferVideo).ConfigureAwait(false);
            // Play media (audio by default, video when requested)
            await audioPlayer.PlayFile(item, preferVideo).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the best available video and audio stream URLs for a given source URL using yt-dlp.
        /// </summary>
        /// <param name="url">The source URL to process.</param>
        /// <returns>A tuple containing the video URL and audio URL.</returns>
        private async Task<(string, string)> HandleStreamFile(string url, bool preferVideo)
        {
            try
            {
                const int targetHeight = 1080;

                // Remove query parameters for better compatibility with yt-dlp
                var currentUrl = YouTubeThumbnail.GetCleanVideoLink(url);
                // fetch data
                var info = await YtDlpMetadata.GetMetaDataAsync(currentUrl).ConfigureAwait(false);

                // Score order for video quality target:
                // 1) exact 1080p, 2) nearest >=1080p (prefer higher quality), 3) nearest below 1080p.
                // This avoids selecting low-quality muxed streams when better separate video exists.
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

                // Best separate audio stream.
                var bestAudio = info.AudioFormats
                    .Where(a => !string.IsNullOrWhiteSpace(a.Url))
                    .OrderByDescending(a => a.Bitrate ?? 0)
                    .FirstOrDefault();

                // Prefer separate video stream selection for audio-only playback metadata/fallback.
                // For actual video playback we strongly prefer a single muxed stream because it is
                // much more stable than attaching a second remote audio stream after load.
                string videoUrl = bestVideo?.Url ?? bestMuxed?.Url ?? string.Empty;

                // For separate video streams, use the best audio stream.
                // If no separate audio exists, fall back to muxed URL.
                string audioUrl = bestAudio?.Url ?? bestMuxed?.Url ?? string.Empty;

                if (preferVideo && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
                {
                    // Use the same muxed URL for both slots so the player does not try to attach
                    // a separate external audio stream for video playback.
                    videoUrl = bestMuxed.Url;
                    audioUrl = bestMuxed.Url;
                }
                else if (string.IsNullOrWhiteSpace(bestVideo?.Url) && !string.IsNullOrWhiteSpace(bestMuxed?.Url))
                {
                    // If only muxed stream is available, use it for both entries.
                    audioUrl = bestMuxed.Url;
                }

                return (videoUrl, audioUrl);
            }
            catch (Exception ex)
            {
                AES_Core.Logging.LogHelper.For<MediaUrlService>().Error($"Fetch failed after retries: {ex.Message}", ex);
            }
            return (string.Empty, string.Empty);
        }
    }
}
