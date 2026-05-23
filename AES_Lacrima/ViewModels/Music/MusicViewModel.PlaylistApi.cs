using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Code.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Settings;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using log4net;
using TagLib;


namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
        #region Public methods
        
        /// <summary>
        /// Asynchronously retrieves a list of video IDs from a online playlist URL by fetching the page's HTML content and extracting video IDs using a regular expression.
        /// </summary>
        /// <param name="playlistUrl">Playlist URL</param>
        /// <returns>Playlist videos</returns>
        public async Task<List<string>> GetPlaylistVideoIds(string playlistUrl)
        {
            using var client = new HttpClient();
            // Setting a User-Agent makes the request look like a browser to avoid blocks
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            
            var html = await client.GetStringAsync(playlistUrl);
            
            // This regex looks for video IDs inside the page source
            var matches = Regex.Matches(html, @"\""videoId\"":\""([^\""]+)\""");
            
            var videoIds = new HashSet<string>();
            foreach (Match match in matches)
            {
                videoIds.Add(match.Groups[1].Value);
            }
            
            return [.. videoIds];
        }

        /// <summary>
        /// Asynchronously retrieves a list of video URLs from a online playlist URL by fetching the page's HTML content, extracting video IDs using a regular expression, and constructing full online URLs for each video ID.
        /// </summary>
        /// <param name="playlistUrl">Playlist URL</param>
        /// <returns>Playlist Urls</returns>
        public async Task<List<string>> GetPlaylistVideoUrls(string playlistUrl)
        {
            using var client = new HttpClient();
            // Headers mimic a browser to prevent being flagged as a bot
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            try 
            {
                var html = await client.GetStringAsync(playlistUrl);
                
                var videoUrls = new List<string>();
                var seenIds = new HashSet<string>();

                // 1. Target specifically the "playlistId" associated with the videoId.
                // This is the most robust way to ensure we only get items that belong to THE playlist.
                // Recommendations and Reels usually do not have a "playlistId" in their watchEndpoint.
                var playlistMatches = Regex.Matches(html, @"\""videoId\""\s*:\s*\""([^\""]+)\""\s*,\s*\""playlistId\""\s*:\s*\""([^\""]+)\""");
                
                string? targetPlaylistId = null;
                if (playlistUrl.Contains("list="))
                {
                    var uri = new Uri(playlistUrl);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    targetPlaylistId = query["list"];
                }

                if (playlistMatches.Count > 0)
                {
                    foreach (Match m in playlistMatches)
                    {
                        string id = m.Groups[1].Value;
                        string pid = m.Groups[2].Value;

                        // Only add if it belongs to the playlist we are interested in (if we know it)
                        // OR if we don't know it, at least it MUST have a playlistId context.
                        if (targetPlaylistId == null || pid == targetPlaylistId)
                        {
                            if (seenIds.Add(id)) videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                        }
                    }
                }

                // 2. Fallback to renderer-based search if playlistId matching found nothing
                if (videoUrls.Count == 0)
                {
                    var rendererMatches = Regex.Matches(html, @"\""(playlistVideoRenderer|playlistPanelVideoRenderer|playlistVideoListRenderer)\""\s*:\s*\{.*?\""videoId\""\s*:\s*\""([^\""]+)\""");
                    foreach (Match m in rendererMatches)
                    {
                        string id = m.Groups[2].Value;
                        if (seenIds.Add(id)) videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                    }
                }

                // 3. Strict exclusion for recommendations if we are still searching
                if (videoUrls.Count == 0)
                {
                    var matches = Regex.Matches(html, @"\""videoId\"":\""([^\""]+)\""");
                    foreach (Match match in matches)
                    {
                        string id = match.Groups[1].Value;
                        if (seenIds.Add(id))
                        {
                            int index = match.Index;
                            string context = html.Substring(Math.Max(0, index - 200), Math.Min(html.Length - index, 400));
                            
                            // EXCLUDE if it's clearly a recommendation or a short
                            if (context.Contains("compactVideoRenderer") || 
                                context.Contains("reelWatchEndpoint") || 
                                context.Contains("shortsLockupViewModel"))
                                continue;

                            // INCLUDE if it has playlist keywords
                            if (context.Contains("playlistVideoRenderer") || 
                                context.Contains("playlistPanelVideoRenderer") ||
                                context.Contains("playlistId"))
                            {
                                videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                            }
                        }
                    }
                }
                
                return videoUrls;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching playlist: {ex.Message}");
                return new List<string>();
            }
        }

        #endregion
    }
}
