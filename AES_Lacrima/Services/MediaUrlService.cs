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
    public interface IMediaUrlService;

    [AutoRegister]
    internal partial class MediaUrlService : ViewModelBase, IMediaUrlService
    {
        public async Task OpenMediaItemAsync(AudioPlayer audioPlayer, MediaItem item)
        {
            if (item == null || item.FileName == null || !YtDlpManager.IsInstalled) return;
            // Load online urls
            item.OnlineUrls = await HandleStreamFile(item.FileName);
            // Play audio
            await audioPlayer.PlayFile(item);
        }

        private async Task<(string, string)> HandleStreamFile(string url)
        {
            try
            {
                // fetch data
                var info = await YtDlpMetadata.GetMetaDataAsync(url);

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