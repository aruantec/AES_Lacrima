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
        public async Task OpenMediaItemAsync(AudioPlayer audioPlayer, MediaItem item)
        {
            if (item.FileName == null) return;
            // Load online urls
            item.OnlineUrls = await HandleStreamFile(item.FileName).ConfigureAwait(false);
            // Play audio
            await audioPlayer.PlayFile(item).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the best available video and audio stream URLs for a given source URL using yt-dlp.
        /// </summary>
        /// <param name="url">The source URL to process.</param>
        /// <returns>A tuple containing the video URL and audio URL.</returns>
        private async Task<(string, string)> HandleStreamFile(string url)
        {
            try
            {
                // Remove query parameters for better compatibility with yt-dlp
                var currentUrl = YouTubeThumbnail.GetCleanVideoLink(url);
                // fetch data
                var info = await YtDlpMetadata.GetMetaDataAsync(currentUrl).ConfigureAwait(false);

                // best 1080p video
                var bestVideo = info.VideoFormats
                    .Where(v => v.Height <= 1080)
                    .OrderByDescending(v => v.Height)
                    .ThenByDescending(v => v.Fps)
                    .FirstOrDefault();

                // best audio
                var bestAudio = info.AudioFormats
                    .OrderByDescending(a => a.Bitrate)
                    .FirstOrDefault();

                string videoUrl = bestVideo?.Url ?? string.Empty;
                string audioUrl = bestAudio?.Url ?? string.Empty;

                return (videoUrl, audioUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fetch failed after retries: {ex.Message}");
            }
            return (string.Empty, string.Empty);
        }
    }
}