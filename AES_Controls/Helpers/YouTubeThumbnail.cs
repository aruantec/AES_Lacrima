using System.Text.RegularExpressions;
using System.Text.Json;

namespace AES_Controls.Helpers;

/// <summary>
/// Represents a collection of image URLs, including thumbnails, video frames, and channel images.
/// </summary>
public class ThumbnailUrls
{
    /// <summary>
    /// Gets or sets the 11-character video ID.
    /// </summary>
    public required string VideoId { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the display name of the channel.
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// Gets or sets a dictionary of thumbnail labels and their corresponding URLs.
    /// </summary>
    public Dictionary<string, string> Thumbnails { get; set; } = [];

    /// <summary>
    /// Gets or sets a dictionary of auto-generated video frames.
    /// </summary>
    public Dictionary<string, string> VideoFrames { get; set; } = [];

    /// <summary>
    /// Gets or sets the URL for the player background image.
    /// </summary>
    public string PlayerBackground { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a dictionary of channel-related images (profile, banner).
    /// </summary>
    public Dictionary<string, string> ChannelImages { get; set; } = [];
}

/// <summary>
/// Provides utility methods for extracting metadata and downloading thumbnails.
/// </summary>
public abstract class YouTubeThumbnail
{
    private static readonly HttpClient Client = new();

    static YouTubeThumbnail()
    {
        // Set a user agent to avoid being blocked
        Client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    /// <summary>
    /// Extracts a video ID from a URL using regular expressions.
    /// </summary>
    /// <param name="url">The URL or potential video ID.</param>
    /// <returns>The extracted 11-character video ID, or null if not found.</returns>
    public static string? ExtractVideoIdWithRegex(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Pattern that matches all common URL formats
        var patterns = new[]
        {
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/|youtube\.com\/embed\/|youtube\.com\/v\/|youtube\.com\/shorts\/)([a-zA-Z0-9_-]{11})",
            @"[?&]v=([a-zA-Z0-9_-]{11})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(url, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // If no pattern matches and input is exactly 11 characters, assume it's already a video ID
        if (url.Length == 11 && Regex.IsMatch(url, @"^[a-zA-Z0-9_-]{11}$"))
        {
            return url;
        }

        return null;
    }

    /// <summary>
    /// Cleans up a URL or video ID to its canonical watch format.
    /// </summary>
    /// <param name="url">The URL or video ID to clean.</param>
    /// <returns>The canonical YouTube URL, or the original input if no video ID could be extracted.</returns>
    public static string GetCleanVideoLink(string url)
    {
        var id = ExtractVideoIdWithRegex(url);
        return id != null ? $"https://www.youtube.com/watch?v={id}" : url;
    }

    /// <summary>
    /// Generates a collection of possible thumbnail and image URLs for a given video and channel.
    /// </summary>
    /// <param name="videoId">The 11-character video ID.</param>
    /// <param name="channelId">Optional channel ID.</param>
    /// <returns>A <see cref="ThumbnailUrls"/> object containing the generated URLs.</returns>
    public static ThumbnailUrls GetAllThumbnailUrls(string videoId, string? channelId = null)
    {
        var urls = new ThumbnailUrls { VideoId = videoId, ChannelId = channelId };

        // Standard quality thumbnails (custom set by uploader)
        urls.Thumbnails.Add("Default (120x90)", $"https://img.youtube.com/vi/{videoId}/default.jpg");
        urls.Thumbnails.Add("Medium Quality (320x180)", $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg");
        urls.Thumbnails.Add("High Quality (480x360)", $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg");
        urls.Thumbnails.Add("Standard Definition (640x480)", $"https://img.youtube.com/vi/{videoId}/sddefault.jpg");
        urls.Thumbnails.Add("Max Resolution (1280x720)", $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg");
        urls.Thumbnails.Add("Max Resolution 1080p (1920x1080)", $"https://img.youtube.com/vi/{videoId}/maxres1.jpg");
        urls.Thumbnails.Add("Max Resolution 2 (1920x1080)", $"https://img.youtube.com/vi/{videoId}/maxres2.jpg");
        urls.Thumbnails.Add("Max Resolution 3 (1920x1080)", $"https://img.youtube.com/vi/{videoId}/maxres3.jpg");

        // Auto-generated video frames
        urls.VideoFrames.Add("Frame 0 (Start)", $"https://img.youtube.com/vi/{videoId}/0.jpg");
        urls.VideoFrames.Add("Frame 1 (Early)", $"https://img.youtube.com/vi/{videoId}/1.jpg");
        urls.VideoFrames.Add("Frame 2 (Middle)", $"https://img.youtube.com/vi/{videoId}/2.jpg");
        urls.VideoFrames.Add("Frame 3 (End)", $"https://img.youtube.com/vi/{videoId}/3.jpg");

        // Player background (used in embedded players)
        urls.PlayerBackground = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

        // Channel images (if channel ID is available)
        if (!string.IsNullOrEmpty(channelId))
        {
            urls.ChannelImages.Add("Profile Picture (Default)", $"https://yt3.googleusercontent.com/ytc/{channelId}");
            urls.ChannelImages.Add("Profile Picture (High Quality)", $"https://yt3.googleusercontent.com/ytc/{channelId}=s800-c-k-c0x00ffffff-no-rj");
            urls.ChannelImages.Add("Profile Picture (Medium)", $"https://yt3.googleusercontent.com/ytc/{channelId}=s240-c-k-c0x00ffffff-no-rj");
            urls.ChannelImages.Add("Profile Picture (88x88)", $"https://yt3.googleusercontent.com/ytc/{channelId}=s88-c-k-c0x00ffffff-no-rj");
            
            // Banner URLs (these might not always be available)
            urls.ChannelImages.Add("Banner (TV)", $"https://yt3.googleusercontent.com/{channelId}");
            urls.ChannelImages.Add("Banner (Desktop)", $"https://yt3.googleusercontent.com/{channelId}=w1060-fcrop64=1,00005a57ffffa5a8-k-c0xffffffff-no-nd-rj");
            urls.ChannelImages.Add("Banner (Tablet)", $"https://yt3.googleusercontent.com/{channelId}=w1138-fcrop64=1,00005a57ffffa5a8-k-c0xffffffff-no-nd-rj");
            urls.ChannelImages.Add("Banner (Mobile)", $"https://yt3.googleusercontent.com/{channelId}=w640-fcrop64=1,00005a57ffffa5a8-k-c0xffffffff-no-nd-rj");
        }

        return urls;
    }

    /// <summary>
    /// Attempts to extract the channel ID and author name from a video page.
    /// </summary>
    /// <param name="videoId">The 11-character video ID.</param>
    /// <returns>A tuple containing the channel ID and channel name.</returns>
    public static async Task<(string? channelId, string? channelName)> ExtractChannelInfoFromVideo(string videoId)
    {
        try
        {
            string url = $"https://www.youtube.com/watch?v={videoId}";
            string html = await Client.GetStringAsync(url);

            // Extract channel ID from various possible locations in the HTML
            string? channelId = null;
            string? channelName = null;

            // Method 1: From channelId property in JSON
            var channelIdMatch = Regex.Match(html, @"""channelId"":""([^""]+)""");
            if (channelIdMatch.Success)
            {
                channelId = channelIdMatch.Groups[1].Value;
            }

            // Method 2: From externalChannelId
            if (string.IsNullOrEmpty(channelId))
            {
                channelIdMatch = Regex.Match(html, @"""externalChannelId"":""([^""]+)""");
                if (channelIdMatch.Success)
                {
                    channelId = channelIdMatch.Groups[1].Value;
                }
            }

            // Method 3: From browse endpoint
            if (string.IsNullOrEmpty(channelId))
            {
                channelIdMatch = Regex.Match(html, @"""browseEndpoint"":\{""browseId"":""([^""]+)""");
                if (channelIdMatch.Success)
                {
                    channelId = channelIdMatch.Groups[1].Value;
                }
            }

            // Extract channel name
            var channelNameMatch = Regex.Match(html, @"""author"":""([^""]+)""");
            if (channelNameMatch.Success)
            {
                channelName = channelNameMatch.Groups[1].Value;
            }

            // Alternative channel name extraction
            if (string.IsNullOrEmpty(channelName))
            {
                channelNameMatch = Regex.Match(html, @"""channelName"":""([^""]+)""");
                if (channelNameMatch.Success)
                {
                    channelName = channelNameMatch.Groups[1].Value;
                }
            }

            return (channelId, channelName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting channel info: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Retrieves online metadata for a given URL, prioritizing if detected.
    /// </summary>
    /// <param name="url">The URL to fetch metadata for.</param>
    /// <returns>A tuple containing Title, Author, ThumbnailUrl, Genre, and Year.</returns>
    public static async Task<(string Title, string Author, string ThumbnailUrl, string Genre, uint Year)> GetOnlineMetadataAsync(string url)
    {
        try
        {
            string videoId = ExtractVideoId(url);
            bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");

            if (isYouTube && videoId != url && videoId.Length == 11)
            {
                var ytMeta = await GetVideoMetadataAsync(videoId);
                // If specific scraping worked, return it.
                if (!string.IsNullOrWhiteSpace(ytMeta.Title)) return ytMeta;
            }

            // General online metadata (OpenGraph tags) as fallback
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Use a consistent modern browser User-Agent
            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            request.Headers.Add("User-Agent", userAgent);
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            var res = await Client.SendAsync(request);
            if (!res.IsSuccessStatusCode) return ("", "", "", "", 0);

            string html = await res.Content.ReadAsStringAsync();

            string title = Regex.Match(html, @"<meta property=""og:title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(title)) title = Regex.Match(html, @"<meta name=""twitter:title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(title)) title = Regex.Match(html, @"<meta name=""title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(title)) title = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;

            // Trim potential suffixes from title fallbacks
            if (!string.IsNullOrEmpty(title))
            {
                title = Regex.Replace(title, @"\s*-\s*YouTube$", "", RegexOptions.IgnoreCase);
            }

            string author = Regex.Match(html, @"<meta property=""og:site_name"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(author)) author = Regex.Match(html, @"<meta name=""author"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(author)) author = Regex.Match(html, @"<meta name=""twitter:creator"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;

            string thumbUrl = Regex.Match(html, @"<meta property=""og:image"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(thumbUrl)) thumbUrl = Regex.Match(html, @"<meta name=""twitter:image"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;

            return (System.Net.WebUtility.HtmlDecode(title).Trim(), System.Net.WebUtility.HtmlDecode(author).Trim(), thumbUrl, "", 0);
        }
        catch
        {
            return ("", "", "", "", 0);
        }
    }

    /// <summary>
    /// Specifically fetches video metadata by parsing the video's watch page.
    /// </summary>
    /// <param name="videoId">The 11-character video ID.</param>
    /// <returns>A tuple containing Title, Author, ThumbnailUrl, Genre, and Year.</returns>
    public static async Task<(string Title, string Author, string ThumbnailUrl, string Genre, uint Year)> GetVideoMetadataAsync(string videoId)
    {
        try
        {
            // Use a more realistic User-Agent to avoid consent/bot-detection pages
            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            string url = $"https://www.youtube.com/watch?v={videoId}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", userAgent);
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Add("Cookie", "CONSENT=YES+cb.20210328-17-p0.en+FX+417"); // Attempt to bypass consent page

            var res = await Client.SendAsync(request);
            string html = await res.Content.ReadAsStringAsync();

            // Try to find ytInitialPlayerResponse. It can be var, window, or inside a JSON object.
            // Using a broader match and then balancing.
            var playerResponseMatch = Regex.Match(html, @"ytInitialPlayerResponse\s*=\s*(\{.+?\});?", RegexOptions.Singleline);
            if (!playerResponseMatch.Success)
            {
                // Fallback: search for it as a property in a JSON or just standalone
                playerResponseMatch = Regex.Match(html, @"""ytInitialPlayerResponse"":\s*(\{.+?\})", RegexOptions.Singleline);
            }

            if (playerResponseMatch.Success)
            {
                try
                {
                    string json = playerResponseMatch.Groups[1].Value.Trim();
                    // Ensure we only take the balanced JSON object if regex overshot
                    json = ExtractBalancedJson(json);

                    if (!string.IsNullOrEmpty(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("videoDetails", out var videoDetails))
                        {
                            string title = videoDetails.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                            string author = videoDetails.TryGetProperty("author", out var aProp) ? aProp.GetString() ?? "" : "";

                            string thumb = "";
                            if (videoDetails.TryGetProperty("thumbnail", out var thumbnail))
                            {
                                var thumbnails = thumbnail.GetProperty("thumbnails").EnumerateArray();
                                thumb = thumbnails.LastOrDefault().GetProperty("url").GetString() ?? "";
                            }

                            string genre = "";
                            uint year = 0;

                            if (root.TryGetProperty("microformat", out var microformat) && 
                                root.TryGetProperty("microformat", out var micro) && // backup check
                                micro.TryGetProperty("playerMicroformatRenderer", out var renderer))
                            {
                                genre = renderer.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
                                if (renderer.TryGetProperty("publishDate", out var pubDate))
                                {
                                    var dateStr = pubDate.GetString();
                                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 4 && uint.TryParse(dateStr.AsSpan(0, 4), out var y))
                                        year = y;
                                }
                            }

                            if (!string.IsNullOrEmpty(title)) 
                                return (System.Net.WebUtility.HtmlDecode(title).Trim(), System.Net.WebUtility.HtmlDecode(author).Trim(), thumb, genre, year);
                        }
                    }
                }
                catch { /* fallback to meta tags */ }
            }

            // Fallback to meta tags with broader patterns
            string metaTitle = Regex.Match(html, @"<meta property=""og:title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaTitle)) metaTitle = Regex.Match(html, @"<meta name=""title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaTitle)) metaTitle = Regex.Match(html, @"<meta name=""twitter:title"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaTitle))
            {
                var titleMatch = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (titleMatch.Success)
                {
                    metaTitle = titleMatch.Groups[1].Value;
                    metaTitle = Regex.Replace(metaTitle, @"\s*-\s*YouTube$", "", RegexOptions.IgnoreCase).Trim();
                }
            }

            // Sanitization: If title is generic, treat as empty to trigger further fallbacks or re-scrape
            if (!string.IsNullOrEmpty(metaTitle))
            {
                if (metaTitle.Equals("YouTube", StringComparison.OrdinalIgnoreCase) || 
                    metaTitle.Equals("Google", StringComparison.OrdinalIgnoreCase))
                    metaTitle = "";
            }

            string metaAuthor = Regex.Match(html, @"<link itemprop=""name"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaAuthor)) metaAuthor = Regex.Match(html, @"""author"":""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaAuthor)) metaAuthor = Regex.Match(html, @"<meta name=""author"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaAuthor)) metaAuthor = Regex.Match(html, @"<meta property=""og:site_name"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(metaAuthor)) metaAuthor = Regex.Match(html, @"<meta name=""twitter:creator"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;

            string thumbUrl = Regex.Match(html, @"<meta property=""og:image"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(thumbUrl)) thumbUrl = $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg";

            string metaGenre = Regex.Match(html, @"<meta itemprop=""genre"" content=""([^""]*)""", RegexOptions.IgnoreCase).Groups[1].Value;
            uint metaYear = 0;
            var dateMatch = Regex.Match(html, @"<meta itemprop=""datePublished"" content=""(\d{4})", RegexOptions.IgnoreCase);
            if (dateMatch.Success) uint.TryParse(dateMatch.Groups[1].Value, out metaYear);

            return (System.Net.WebUtility.HtmlDecode(metaTitle).Trim(), System.Net.WebUtility.HtmlDecode(metaAuthor).Trim(), thumbUrl, metaGenre, metaYear);
        }
        catch
        {
            return ("", "", $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg", "", 0);
        }
    }

    /// <summary>
    /// Extracts a balanced JSON object string from a larger text starting with '{'.
    /// </summary>
    private static string ExtractBalancedJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int start = text.IndexOf('{');
        if (start == -1) return "";

        int balance = 0;
        bool inQuotes = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes)
            {
                if (c == '{') balance++;
                else if (c == '}') balance--;

                if (balance == 0) return text.Substring(start, i - start + 1);
            }
        }
        return "";
    }

    /// <summary>
    /// Checks if a given URL exists by sending a HEAD request.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <returns>True if the response is successful, otherwise false.</returns>
    public static async Task<bool> CheckUrlExists(string url)
    {
        try
        {
            var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads an image from the specified URL as a byte array.
    /// </summary>
    /// <param name="url">The URL of the image to download.</param>
    /// <returns>The image data as a byte array, or null if the download fails.</returns>
    public static async Task<byte[]?> DownloadImage(string url)
    {
        try
        {
            return await Client.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download {url}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Discovers and downloads all available thumbnails, video frames, and channel images for a video.
    /// </summary>
    /// <param name="videoId">The 11-character video ID.</param>
    /// <param name="outputDirectory">The directory where images will be saved.</param>
    public static async Task DownloadAllAvailableThumbnails(string videoId, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        Console.WriteLine($"Downloading content for video: {videoId}\n");

        // First, extract channel information
        Console.WriteLine("Extracting channel information...");
        var (channelId, channelName) = await ExtractChannelInfoFromVideo(videoId);

        if (!string.IsNullOrEmpty(channelId))
        {
            Console.WriteLine($"✓ Channel ID: {channelId}");
            Console.WriteLine($"✓ Channel Name: {channelName ?? "Unknown"}\n");
        }
        else
        {
            Console.WriteLine("✗ Could not extract channel information\n");
        }

        var urls = GetAllThumbnailUrls(videoId, channelId);

        // Download standard thumbnails
        Console.WriteLine("Checking standard thumbnails...");
        foreach (var thumb in urls.Thumbnails)
        {
            var exists = await CheckUrlExists(thumb.Value);
            if (exists)
            {
                Console.WriteLine($"✓ {thumb.Key}: Available");
                var data = await DownloadImage(thumb.Value);
                if (data != null)
                {
                    var filename = Path.Combine(outputDirectory, $"{videoId}_{SanitizeFilename(thumb.Key)}.jpg");
                    await File.WriteAllBytesAsync(filename, data);
                    Console.WriteLine($"  Saved to: {filename}");
                }
            }
            else
            {
                Console.WriteLine($"✗ {thumb.Key}: Not available");
            }
        }

        // Download video frames
        Console.WriteLine("\nChecking video frames...");
        foreach (var frame in urls.VideoFrames)
        {
            var exists = await CheckUrlExists(frame.Value);
            if (exists)
            {
                Console.WriteLine($"✓ {frame.Key}: Available");
                var data = await DownloadImage(frame.Value);
                if (data != null)
                {
                    var filename = Path.Combine(outputDirectory, $"{videoId}_{SanitizeFilename(frame.Key)}.jpg");
                    await File.WriteAllBytesAsync(filename, data);
                    Console.WriteLine($"  Saved to: {filename}");
                }
            }
            else
            {
                Console.WriteLine($"✗ {frame.Key}: Not available");
            }
        }

        // Download player background
        Console.WriteLine("\nChecking player background...");
        var bgExists = await CheckUrlExists(urls.PlayerBackground);
        if (bgExists)
        {
            Console.WriteLine($"✓ Player Background: Available");
            var data = await DownloadImage(urls.PlayerBackground);
            if (data != null)
            {
                var filename = Path.Combine(outputDirectory, $"{videoId}_PlayerBackground.jpg");
                await File.WriteAllBytesAsync(filename, data);
                Console.WriteLine($"  Saved to: {filename}");
            }
        }

        // Download channel images
        if (!string.IsNullOrEmpty(channelId))
        {
            Console.WriteLine("\nChecking channel images...");
            foreach (var img in urls.ChannelImages)
            {
                var exists = await CheckUrlExists(img.Value);
                if (exists)
                {
                    Console.WriteLine($"✓ {img.Key}: Available");
                    var data = await DownloadImage(img.Value);
                    if (data != null)
                    {
                        var extension = ".jpg";
                        var filename = Path.Combine(outputDirectory, $"Channel_{SanitizeFilename(img.Key)}{extension}");
                        await File.WriteAllBytesAsync(filename, data);
                        Console.WriteLine($"  Saved to: {filename}");
                    }
                }
                else
                {
                    Console.WriteLine($"✗ {img.Key}: Not available");
                }
            }
        }

        Console.WriteLine("\nDownload complete!");
    }

    /// <summary>
    /// Sanitizes a string for use as a filename by replacing or removing invalid characters.
    /// </summary>
    /// <param name="filename">The filename to sanitize.</param>
    /// <returns>A sanitized filename string.</returns>
    private static string SanitizeFilename(string filename)
    {
        return filename.Replace(" ", "_")
                      .Replace("(", "")
                      .Replace(")", "")
                      .Replace("/", "_")
                      .Replace("\\", "_");
    }

    /// <summary>
    /// Extracts a 11-character video ID from various URL formats.
    /// </summary>
    /// <param name="input">The URL or string containing the video ID.</param>
    /// <returns>The extracted video ID or the original input if no match is found.</returns>
    public static string ExtractVideoId(string input)
    {
        // Handle different URL formats
        if (input.Contains("youtube.com/watch?v="))
        {
            var match = Regex.Match(input, @"[?&]v=([^&]+)");
            return match.Success ? match.Groups[1].Value : input;
        }
        else if (input.Contains("youtu.be/"))
        {
            var match = Regex.Match(input, @"youtu\.be/([^?]+)");
            return match.Success ? match.Groups[1].Value : input;
        }
        else if (input.Contains("youtube.com/embed/"))
        {
            var match = Regex.Match(input, @"embed/([^?]+)");
            return match.Success ? match.Groups[1].Value : input;
        }
        return input;
    }
}