using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Helpers;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using AES_Core.Logging;
using File = System.IO.File;
using Path = System.IO.Path;


namespace AES_Lacrima.Services
{
    public partial class MetadataService : ViewModelBase, IMetadataService 
    {
        private static async Task<List<WebImageSearchResult>> SearchWebImagesAsync(IReadOnlyList<string> queries, bool isRomSearch = false)
        {
            var interimResults = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultsLock = new object();

            // Take fewer queries to significantly speed up searching
            var normalizedQueries = queries
                .Select(NormalizeSearchTitle)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(isRomSearch ? 2 : 3)
                .ToList();

            void AddResult(WebImageSearchResult result)
            {
                lock (resultsLock)
                {
                    if (interimResults.Count < MaxImageSearchResults && seen.Add(result.FullImageUrl))
                    {
                        interimResults.Add(result);
                    }
                }
            }

            // Using a shorter timeout for individual provider searches to skip slow ones
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            if (!isRomSearch)
            {
                try
                {
                    var itunesTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        var songUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=40";
                        await LoadItunesResults(songUri, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);

                        results.Clear();
                        var albumUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=album&limit=40";
                        await LoadItunesResults(albumUri, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(itunesTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("iTunes search timed out."); }
            }

            if (interimResults.Count < 8) // Lower threshold to move faster to fallbacks
            {
                try
                {
                    var ddgTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadDuckDuckGoImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(ddgTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("DuckDuckGo search timed out."); }
            }

            if (interimResults.Count < 12)
            {
                try
                {
                    var bingTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadBingImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(bingTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("Bing search timed out."); }
            }

            if (interimResults.Count < 4)
            {
                try
                {
                    var googleTasks = normalizedQueries.Select(async query =>
                    {
                        var results = new List<WebImageSearchResult>();
                        await LoadGoogleImageResults(query, new HashSet<string>(), results);
                        foreach (var r in results) AddResult(r);
                    });
                    await Task.WhenAll(googleTasks).WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) { SLog.Warn("Google search timed out."); }
            }

            return interimResults;
        }

        private static async Task LoadDuckDuckGoImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var ddgQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    // DuckDuckGo VQD token is required for the image API
                    // First, get the main search page to extract the VQD
                    var mainUrl = $"https://duckduckgo.com/?q={Uri.EscapeDataString(ddgQuery)}&iax=images&ia=images";
                    using var mainRequest = new HttpRequestMessage(HttpMethod.Get, mainUrl);
                    mainRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

                    using var mainResponse = await ImageHttpClient.SendAsync(mainRequest, HttpCompletionOption.ResponseContentRead);
                    if (!mainResponse.IsSuccessStatusCode)
                        continue;

                    var mainHtml = await mainResponse.Content.ReadAsStringAsync();
                    var vqdMatch = Regex.Match(mainHtml, @"vqd=['""](?<vqd>[^'""]+)['""]|vqd=(?<vqd2>[^&'""\s]+)", RegexOptions.IgnoreCase);
                    var vqd = vqdMatch.Groups["vqd"].Value;
                    if (string.IsNullOrEmpty(vqd)) vqd = vqdMatch.Groups["vqd2"].Value;

                    if (string.IsNullOrEmpty(vqd))
                        continue;

                    // Now call the AJAX endpoint for images
                    var apiUrl = $"https://duckduckgo.com/i.js?l=us-en&o=json&q={Uri.EscapeDataString(ddgQuery)}&vqd={vqd}&f=,,,";
                    using var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    apiRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    apiRequest.Headers.Referrer = new Uri(mainUrl);

                    using var apiResponse = await ImageHttpClient.SendAsync(apiRequest, HttpCompletionOption.ResponseContentRead);
                    if (!apiResponse.IsSuccessStatusCode)
                        continue;

                    var json = await apiResponse.Content.ReadAsStringAsync();
                    ExtractDuckDuckGoImageResults(json, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"DuckDuckGo image search failed for query: {query}", ex);
            }
        }

        private static void ExtractDuckDuckGoImageResults(string json, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            var decoded = WebUtility.HtmlDecode(json)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in DdgJsonImageUrlRegex.Matches(decoded))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = match.Groups["url"].Value;
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static async Task LoadBingImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var bingQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    var url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(bingQuery)}&form=HDRSC3&first=1";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                    request.Headers.Referrer = new Uri("https://www.bing.com/");

                    using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await response.Content.ReadAsStringAsync();
                    ExtractBingImageResults(html, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Bing image search failed for query: {query}", ex);
            }
        }

        private static async Task LoadGoogleImageResults(string query, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            try
            {
                foreach (var googleQuery in BuildGoogleQueries(query))
                {
                    if (sink.Count >= MaxImageSearchResults)
                        break;

                    var url = $"https://www.google.com/search?tbm=isch&udm=2&hl=en&q={Uri.EscapeDataString(googleQuery)}";

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    request.Headers.Add("Cookie", GoogleConsentCookie);
                    request.Headers.Referrer = new Uri("https://www.google.com/");

                    using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var html = await response.Content.ReadAsStringAsync();
                    ExtractGoogleImageResults(html, seen, sink);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Google image search failed for query: {query}", ex);
            }
        }

        private static IEnumerable<string> BuildGoogleQueries(string query)
        {
            var googleQueries = new List<string>();
            var normalized = NormalizeSearchTitle(query);
            AddDistinctQuery(googleQueries, normalized);

            foreach (var aliasQuery in ExpandSearchQueryAliases(normalized))
                AddDistinctQuery(googleQueries, aliasQuery);

            AddDistinctQuery(googleQueries, $"{normalized} album cover");
            AddDistinctQuery(googleQueries, $"{normalized} cover art");

            var stripped = StripCoverSearchTokens(normalized);
            if (!string.IsNullOrWhiteSpace(stripped) && !string.Equals(stripped, normalized, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctQuery(googleQueries, stripped);
                foreach (var aliasQuery in ExpandSearchQueryAliases(stripped))
                    AddDistinctQuery(googleQueries, aliasQuery);
            }

            return googleQueries;
        }

        private static IEnumerable<string> ExpandSearchQueryAliases(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                yield break;

            foreach (var pair in EmulationConsoleCatalog.SearchAliases)
            {
                if (!query.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var alias in pair.Value)
                    yield return Regex.Replace(query, $@"\b{Regex.Escape(pair.Key)}\b", alias, RegexOptions.IgnoreCase);
            }
        }

        private static string StripCoverSearchTokens(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            var stripped = CoverSearchTokenRegex.Replace(query, " ");
            return MultiSpaceRegex.Replace(stripped, " ").Trim();
        }

        private static void ExtractGoogleImageResults(string html, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            foreach (Match match in GoogleImgTagRegex.Matches(html))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            var decodedHtml = WebUtility.HtmlDecode(html)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in GoogleImgResRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in DirectImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in GoogleJsonImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in GoogleQuotedHttpUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static void ExtractBingImageResults(string html, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            var decodedHtml = WebUtility.HtmlDecode(html)
                .Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            foreach (Match match in BingJsonImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in BingHtmlEncodedImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Groups["url"].Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }

            foreach (Match match in DirectImageUrlRegex.Matches(decodedHtml))
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var imageUrl = DecodeGoogleUrl(match.Value);
                TryAddGoogleImageResult(imageUrl, seen, sink);
            }
        }

        private static string DecodeGoogleUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return string.Empty;

            var decoded = WebUtility.HtmlDecode(rawUrl.Trim());
            decoded = decoded.Replace("\\u003d", "=")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/");

            try
            {
                decoded = Uri.UnescapeDataString(decoded);
            }
            catch (Exception logEx) { SLog.Warn("Keep the best-effort decoded value.", logEx); }

            return decoded;
        }

        private static void TryAddGoogleImageResult(string imageUrl, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            if (sink.Count >= MaxImageSearchResults)
                return;

            if (!IsUsableImageUrl(imageUrl) || !seen.Add(imageUrl))
                return;

            sink.Add(new WebImageSearchResult
            {
                ThumbnailUrl = imageUrl,
                FullImageUrl = imageUrl,
                Title = "Google Image",
                Artist = "Web"
            });
        }

        private static bool IsUsableImageUrl(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return false;

            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                return false;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            if (imageUrl.Contains("googlelogo", StringComparison.OrdinalIgnoreCase)
                || imageUrl.Contains("/images/branding/", StringComparison.OrdinalIgnoreCase)
                || imageUrl.Contains("/gen_204", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var host = uri.Host;
            var isGoogleThumbnailHost =
                host.StartsWith("encrypted-tbn", StringComparison.OrdinalIgnoreCase)
                || host.Contains("gstatic.com", StringComparison.OrdinalIgnoreCase)
                || host.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase);

            if (host.Contains("google.", StringComparison.OrdinalIgnoreCase) && !isGoogleThumbnailHost)
                return false;

            if (uri.AbsolutePath.Contains("/search", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("/imgres", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("/url", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Reject marketplaces and social media that often host "lazy" photos or listings
            // like the Dreamcast jewel case on a teal background.
            var trashHosts = new[]
            {
                "ebayimg.com", "ebay.com", "mercari.com", "poshmark.com",
                "fbcdn.net", "fb.com", "instagram.com", "twimg.com",
                "pinterest.com", "etsystatic.com", "etsy.com", "carousell.com",
                "offerup.com", "depop.com", "gumtree.com"
            };

            if (trashHosts.Any(h => host.Contains(h, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }

        private static async Task LoadItunesResults(string uri, HashSet<string> seen, List<WebImageSearchResult> sink)
        {
            using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("results", out var jsonResults) || jsonResults.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in jsonResults.EnumerateArray())
            {
                var thumb = item.TryGetProperty("artworkUrl100", out var artworkNode)
                    ? artworkNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(thumb))
                    continue;

                var full = UpgradeArtworkSize(thumb);
                if (!seen.Add(full))
                    continue;

                var trackName = item.TryGetProperty("trackName", out var trackNode) ? trackNode.GetString() : string.Empty;
                var collectionName = item.TryGetProperty("collectionName", out var collectionNode) ? collectionNode.GetString() : string.Empty;
                var artistName = item.TryGetProperty("artistName", out var artistNode) ? artistNode.GetString() : string.Empty;

                sink.Add(new WebImageSearchResult
                {
                    ThumbnailUrl = thumb,
                    FullImageUrl = full,
                    Title = trackName ?? collectionName ?? string.Empty,
                    Artist = artistName ?? string.Empty
                });

                if (sink.Count >= MaxImageSearchResults)
                    return;
            }
        }

        private static string UpgradeArtworkSize(string artworkUrl)
        {
            if (string.IsNullOrWhiteSpace(artworkUrl))
                return artworkUrl;

            return Regex.Replace(artworkUrl, @"\d+x\d+bb", "1200x1200bb", RegexOptions.IgnoreCase);
        }

        private async Task<bool> TryAddImageFromUrlAsync(string url, TagImageKind kind)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ImageSearchStatus = "Invalid image URL.";
                return false;
            }

            try
            {
                using var response = await ImageHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    ImageSearchStatus = "Could not download image.";
                    return false;
                }

                var mimeType = response.Content.Headers.ContentType?.MediaType;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    ImageSearchStatus = "Downloaded image is empty.";
                    return false;
                }

                mimeType ??= GuessMimeTypeFromUrl(uri.AbsolutePath);
                if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    ImageSearchStatus = "URL is not an image.";
                    return false;
                }

                AddImageToCollection(bytes, mimeType, kind, uri.AbsoluteUri);
                return true;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to add image from URL: {url}", ex);
                ImageSearchStatus = "Failed to add image from URL.";
                return false;
            }
        }

        private void AddImageToCollection(byte[] data, string mimeType, TagImageKind kind, string description)
        {
            var model = new TagImageModel(kind, data, mimeType, description)
            {
                OnDeleteImage = OnDeleteImage
            };

            Images.Add(model);
        }

        private static string GuessMimeTypeFromUrl(string url)
        {
            var lower = url.ToLowerInvariant();
            return lower switch
            {
                var s when s.EndsWith(".png") => "image/png",
                var s when s.EndsWith(".webp") => "image/webp",
                var s when s.EndsWith(".avif") => "image/avif",
                var s when s.EndsWith(".gif") => "image/gif",
                _ => "image/jpeg"
            };
        }

        private static string BuildPictureDescription(TagImageModel model)
        {
            var baseDescription = StripImageKindMarker(model.Description);
            if (model.Kind == TagImageKind.Wallpaper)
            {
                baseDescription = string.IsNullOrWhiteSpace(baseDescription)
                    ? "wallpaper"
                    : baseDescription;
            }

            return $"[AES_KIND:{model.Kind}] {baseDescription}".Trim();
        }

        private static PictureType MapKindToPictureType(TagImageModel model) => model.Kind switch
        {
            TagImageKind.Cover => PictureType.FrontCover,
            TagImageKind.BackCover => PictureType.BackCover,
            TagImageKind.Artist => PictureType.Artist,
            TagImageKind.Wallpaper => PictureType.Illustration,
            _ => PictureType.Other,
        };

        private static TagImageKind MapPictureToKind(IPicture? pic)
        {
            if (pic == null)
                return TagImageKind.Other;

            if (TryGetKindFromDescription(pic.Description, out var descriptionKind))
                return descriptionKind;

            return pic.Type switch
            {
                PictureType.FrontCover => TagImageKind.Cover,
                PictureType.BackCover => TagImageKind.BackCover,
                PictureType.Artist => TagImageKind.Artist,
                PictureType.Illustration => TagImageKind.Wallpaper, // Map Illustration back to Wallpaper
                _ => (pic.Description?.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0
                    ? TagImageKind.Wallpaper
                    : TagImageKind.Other)
            };
        }

        private static bool TryGetKindFromDescription(string? description, out TagImageKind kind)
        {
            kind = TagImageKind.Other;
            if (string.IsNullOrWhiteSpace(description))
                return false;

            var match = ImageKindDescriptionRegex.Match(description);
            if (!match.Success)
                return false;

            return Enum.TryParse(match.Groups["kind"].Value, ignoreCase: true, out kind);
        }

        private static string StripImageKindMarker(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            return ImageKindDescriptionRegex.Replace(description, string.Empty).Trim();
        }
    }
}
