using AES_Code.Helpers;
using AES_Code.Models;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using log4net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TagLib;
using File = System.IO.File;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// Provides background metadata extraction and cover art retrieval for media items.
    /// Supports local tagging via TagLib#, Apple/iTunes API searches, and online metadata (YouTube).
    /// </summary>
    public sealed class MetadataScrapper : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MetadataScrapper));

        /// <summary>The default limit for embedded image extraction (4MB).</summary>
        internal const int DefaultMaxEmbeddedImageBytes = 4 * 1024 * 1024;
        private readonly AvaloniaList<MediaItem> _playlist;
        private readonly Bitmap _defaultCover;
        private readonly HttpClient _http = new();
        private readonly ConcurrentDictionary<string, Bitmap> _coverCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _loadingCts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _cacheOrder = new();
        private readonly SemaphoreSlim _throttle = new(6);
        private readonly int? _maxThumbnailWidth;
        private readonly int _maxCacheEntries;
        private readonly int _maxEmbeddedImageBytes;
        private readonly AudioPlayer? _player;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetadataScrapper"/> class.
        /// </summary>
        /// <param name="playlist">The collection of media items to track.</param>
        /// <param name="player">The audio player instance (used for pausing playback during tag writes).</param>
        /// <param name="defaultCover">Fallback cover bitmap if no artwork is found.</param>
        /// <param name="agentInfo">User-Agent string for HTTP requests.</param>
        /// <param name="maxThumbnailWidth">Optional maximum width to decode thumbnails to.</param>
        /// <param name="maxCacheEntries">Maximum number of bitmaps to keep in the memory cache.</param>
        /// <param name="maxEmbeddedImageBytes">Maximum byte size for embedded images to be processed.</param>
        public MetadataScrapper(AvaloniaList<MediaItem> playlist,
                                AudioPlayer player,
                                Bitmap? defaultCover,
                                string agentInfo,
                                int? maxThumbnailWidth = null,
                                int maxCacheEntries = 200,
                                int maxEmbeddedImageBytes = DefaultMaxEmbeddedImageBytes)
        {
            //Initializers
            _playlist = playlist;
            _player = player;
            _defaultCover = defaultCover ?? new Bitmap(AssetLoader.Open(new Uri("avares://AES_Lacrima/Assets/cover_placeholder.png")));
            _maxThumbnailWidth = maxThumbnailWidth;
            _maxCacheEntries = Math.Max(1, maxCacheEntries);
            _maxEmbeddedImageBytes = maxEmbeddedImageBytes;
            _http.Timeout = TimeSpan.FromSeconds(10);

            // Set User-Agent for HTTP requests to improve compatibility with providers like Apple
            if (!string.IsNullOrEmpty(agentInfo))
            {
                try { _http.DefaultRequestHeaders.UserAgent.ParseAdd(agentInfo); }
                catch (Exception ex) { Log.Warn($"Failed to parse User-Agent: {agentInfo}", ex); }
            }
            // Enqueue initial load for existing items in the playlist
            _ = Task.Run(async () =>
            {
                var items = _playlist.ToArray();
                foreach (var mi in items) await EnqueueLoadFor(mi);
            });
            // Subscribe to playlist changes to handle new items and removals
            _playlist.CollectionChanged += Playlist_CollectionChanged;
        }

        private void Playlist_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var o in e.NewItems)
                    if (o is MediaItem mi) _ = EnqueueLoadFor(mi);
            }
            if (e.OldItems != null)
            {
                foreach (var o in e.OldItems)
                    if (o is MediaItem mi) CancelLoad(mi.FileName);
            }
        }

        /// <summary>
        /// Enqueues a metadata load operation for a specific media item.
        /// </summary>
        /// <param name="mi">The media item to process.</param>
        /// <param name="ct">Unused cancellation token for legacy compatibility.</param>
        /// <returns>A task representing the load operation.</returns>
        public Task EnqueueLoadForPublic(MediaItem mi, CancellationToken ct = default) => LoadMetadataForItemAsync(mi, ct);

        /// <summary>
        /// Internal logic to decide if metadata needs to be loaded (e.g. if title is missing or cover is default).
        /// </summary>
        private Task EnqueueLoadFor(MediaItem mi)
        {
            if (!string.IsNullOrWhiteSpace(mi.Title) && mi.CoverBitmap != null && mi.CoverBitmap != _defaultCover)
                return Task.CompletedTask;

            return LoadMetadataForItemAsync(mi);
        }

        /// <summary>
        /// Performs the actual extraction of metadata for a media item.
        /// Extracts local tags, embedded images, or fetches online metadata if needed.
        /// </summary>
        private async Task LoadMetadataForItemAsync(MediaItem mi, CancellationToken? externalToken = null)
        {
            if (string.IsNullOrWhiteSpace(mi.FileName) || _disposed) return;

            var key = mi.FileName!;

            if (_coverCache.TryGetValue(key, out var cachedBmp))
            {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    mi.CoverBitmap = cachedBmp;
                    mi.IsLoadingCover = false;
                });
                return;
            }

            // Global throttle for metadata extraction to prevent OOM with large playlists
            await _throttle.WaitAsync(externalToken ?? CancellationToken.None);
            
            try
            {
                var cts = new CancellationTokenSource();
                if (!_loadingCts.TryAdd(key, cts))
                {
                    CancelLoad(key);
                    _loadingCts[key] = cts;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, externalToken ?? CancellationToken.None);
                var token = linked.Token;

                await Dispatcher.UIThread.InvokeAsync(() => mi.IsLoadingCover = true);

                if (!File.Exists(key))
                {
                    await SetupOnlineMetadata(mi).ConfigureAwait(false);
                    return;
                }

                var tagResult = await Task.Run(() =>
                {
                    try
                    {
                        using var file = TagLib.File.Create(key);

                        var t = file.Tag.Title;
                        var a = file.Tag.FirstPerformer;
                        var al = file.Tag.Album ?? string.Empty;
                        var tr = file.Tag.Track;
                        var yr = file.Tag.Year;
                        var ge = string.Join(";", file.Tag.Genres ?? []);
                        var co = file.Tag.Comment;
                        var ly = file.Tag.Lyrics;

                        if (string.IsNullOrWhiteSpace(t))
                        {
                            var fileName = Path.GetFileNameWithoutExtension(key);
                            var match = Regex.Match(fileName, @"^(.*?)\s*[-–—]\s*(.*)$");
                            if (match.Success)
                            {
                                a = string.IsNullOrWhiteSpace(a) ? match.Groups[1].Value.Trim() : a;
                                t = match.Groups[2].Value.Trim();
                            }
                            else
                            {
                                t = fileName.Trim();
                            }
                        }
                        a ??= string.Empty;

                        byte[]? pic = null;
                        byte[]? wall = null;

                        var pictures = file.Tag.Pictures;
                        if (pictures != null && pictures.Length > 0)
                        {
                            MetadataScrapper.SelectEmbeddedImages(
                                pictures,
                                _maxEmbeddedImageBytes,
                                includeCover: true,
                                includeWallpaper: true,
                                out pic,
                                out wall);
                        }
                        var hasFrontCover = pictures != null && pictures.Any(p => p?.Type == PictureType.FrontCover);
                        var hasEmbedded = pictures != null && pictures.Length > 0;
                        return new { t = t ?? "", a = a ?? "", al, tr, yr, ge, co, ly, pic, wall, hasFrontCover, hasEmbedded, Success = true };
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error extracting tags from {key}", ex);
                        return new { t = Path.GetFileNameWithoutExtension(key), a = "", al = "", tr = 0u, yr = 0u, ge = "", co = "", ly = "", pic = (byte[]?)null, wall = (byte[]?)null, hasFrontCover = false, hasEmbedded = false, Success = false };
                    }
                }, token);

                if (token.IsCancellationRequested) return;

                // Log what we found in tags for diagnostics
                try
                {
                    Log.Debug($"MetadataScrapper: {key} - embedded:{tagResult.hasEmbedded} frontCover:{tagResult.hasFrontCover}");
                }
                catch { }

                await Dispatcher.UIThread.InvokeAsync(() => {
                    mi.Title = tagResult.t;
                    mi.Artist = tagResult.a;
                    mi.Album = tagResult.al;
                    mi.Track = tagResult.tr;
                    mi.Year = tagResult.yr;
                    mi.Genre = tagResult.ge;
                    mi.Comment = tagResult.co;
                    mi.Lyrics = tagResult.ly;
                });

                // If the tag contained any embedded pictures, always prioritize those
                // and do not perform online lookups even if decoding fails.
                if (tagResult.hasEmbedded)
                {
                    Log.Debug($"MetadataScrapper: {key} - processing embedded images (will skip online lookups)");
                    await ProcessEmbeddedImagesInternal(mi, tagResult.pic, tagResult.wall, key, token).ConfigureAwait(false);
                    await UpdateLocalMetadataAsync(mi, tagResult.pic, tagResult.wall).ConfigureAwait(false);
                    return;
                }

                // No embedded pictures - fall back to online services
                Log.Debug($"MetadataScrapper: {key} - no embedded images, performing online lookup");
                if (mi.CoverBitmap == null || mi.CoverBitmap == _defaultCover)
                {
                    await FetchAppleMetadataInternal(mi, tagResult.t, tagResult.a, key, token).ConfigureAwait(false);
                }

                await UpdateLocalMetadataAsync(mi, tagResult.pic, tagResult.wall).ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => mi.IsLoadingCover = false);
                _loadingCts.TryRemove(key, out _);
                _throttle.Release();
            }
        }

        /// <summary>
        /// Decodes provided byte arrays into bitmaps and assigns them to the media item.
        /// </summary>
        private async Task ProcessEmbeddedImagesInternal(MediaItem mi, byte[]? pic, byte[]? wall, string key, CancellationToken token)
        {
            try
            {
                if (pic != null)
                {
                    var bmp = await Task.Run(() => {
                        using var ms = new MemoryStream(pic);
                        // Downscale local image if parameter is set
                        return _maxThumbnailWidth.HasValue
                            ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                            : new Bitmap(ms);
                    }, token);

                    AddToCoverCache(key, bmp);

                    await Dispatcher.UIThread.InvokeAsync(() => {
                        mi.CoverBitmap = bmp;
                        mi.CoverFound = true;
                    });
                }
                if (wall != null)
                {
                    var wBmp = await Task.Run(() => {
                        using var ms = new MemoryStream(wall);
                        return new Bitmap(ms);
                    }, token);
                    await Dispatcher.UIThread.InvokeAsync(() => mi.WallpaperBitmap = wBmp);
                }
            }
            catch (Exception ex) { Log.Error($"Error processing embedded images for {key}", ex); }
        }

        private bool IsWithinEmbeddedImageCap(byte[] data)
            => _maxEmbeddedImageBytes <= 0 || data.Length <= _maxEmbeddedImageBytes;

        /// <summary>
        /// Static helper to select a cover and wallpaper from a list of TagLib pictures.
        /// </summary>
        /// <param name="pictures">The list of pictures extracted from file tags.</param>
        /// <param name="maxBytes">The maximum allowed byte size for an image.</param>
        /// <param name="includeCover">Whether to look for a cover image.</param>
        /// <param name="includeWallpaper">Whether to look for a wallpaper image.</param>
        /// <param name="cover">Output byte array for the cover image.</param>
        /// <param name="wallpaper">Output byte array for the wallpaper image.</param>
        internal static void SelectEmbeddedImages(
            IPicture[] pictures,
            int maxBytes,
            bool includeCover,
            bool includeWallpaper,
            out byte[]? cover,
            out byte[]? wallpaper)
        {
            cover = null;
            wallpaper = null;

            if (pictures == null || pictures.Length == 0) return;

            // Prioritize explicit front-cover images
            if (includeCover)
            {
                foreach (var picture in pictures)
                {
                    if (picture == null) continue;
                    var data = picture.Data;
                    if (data == null) continue;
                    // Allow FrontCover to be selected even if it exceeds the configured
                    // embedded image byte cap — prefer honoring explicit front covers.
                    if (maxBytes > 0 && data.Count > maxBytes && picture.Type != PictureType.FrontCover) continue;
                    var bytes = data.Data;
                    if (bytes == null || bytes.Length == 0) continue;

                    if (picture.Type == PictureType.FrontCover)
                    {
                        cover = bytes;
                        break;
                    }
                }
            }

            // If no front cover found, pick the first non-wallpaper, non-backcover picture as cover
            if (includeCover && cover == null)
            {
                foreach (var picture in pictures)
                {
                    if (picture == null) continue;
                    var isWallpaper = picture.Description?.Contains("wallpaper", StringComparison.OrdinalIgnoreCase) == true;
                    if (isWallpaper) continue;
                    if (picture.Type == PictureType.BackCover) continue; // explicitly skip back cover

                    var data = picture.Data;
                    if (data == null) continue;
                    if (maxBytes > 0 && data.Count > maxBytes && picture.Type != PictureType.FrontCover) continue;
                    var bytes = data.Data;
                    if (bytes == null || bytes.Length == 0) continue;

                    cover = bytes;
                    break;
                }
            }

            // Wallpaper selection: prefer explicit illustration or description containing 'wallpaper'
            if (includeWallpaper)
            {
                foreach (var picture in pictures)
                {
                    if (picture == null) continue;
                    var isWallpaper = picture.Description?.Contains("wallpaper", StringComparison.OrdinalIgnoreCase) == true || picture.Type == PictureType.Illustration;
                    if (!isWallpaper) continue;
                    var data = picture.Data;
                    if (data == null) continue;
                    if (maxBytes > 0 && data.Count > maxBytes && picture.Type != PictureType.Illustration && picture.Type != PictureType.FrontCover) continue;
                    var bytes = data.Data;
                    if (bytes == null || bytes.Length == 0) continue;
                    wallpaper = bytes;
                    break;
                }
            }
        }

        /// <summary>
        /// Asynchronously retrieves and populates metadata for a specified media item from Apple Music using the
        /// provided title and artist information.
        /// </summary>
        /// <remarks>If metadata cannot be found using both the artist and title, the method attempts a
        /// secondary search using only the title. Errors encountered during the operation are logged but not propagated
        /// to the caller.</remarks>
        /// <param name="mi">The media item to be updated with the retrieved Apple Music metadata. This object will be modified if
        /// matching metadata is found.</param>
        /// <param name="title">The title of the media item used to search for corresponding metadata on Apple Music. Cannot be null.</param>
        /// <param name="artist">The artist associated with the media item, used to refine the search for metadata. May be null or empty if
        /// not available.</param>
        /// <param name="key">A unique identifier for the media item, used for logging and tracking the metadata retrieval process.</param>
        /// <param name="token">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task does not return a value.</returns>
        private async Task FetchAppleMetadataInternal(MediaItem mi, string title, string artist, string key, CancellationToken token)
        {
            try
            {
                // Try searching with combined artist and title first
                string cleanTitle = CleanSearchQuery(title);
                string cleanArtist = CleanSearchQuery(artist);
                
                bool found = await TryFetchAppleInternal(mi, $"{cleanArtist} {cleanTitle}", key, token);
                
                // If not found and we had an artist, try searching with just the title
                if (!found && !string.IsNullOrWhiteSpace(cleanArtist))
                {
                    await TryFetchAppleInternal(mi, cleanTitle, key, token);
                }
            }
            catch (Exception ex) { Log.Error($"Error fetching Apple metadata for {key}", ex); }
        }

        /// <summary>
        /// Try to fetch metadata from Apple Music using a specific query.
        /// If successful, updates the media item with the retrieved cover art.
        /// </summary>
        /// <param name="mi"></param>
        /// <param name="query"></param>
        /// <param name="key"></param>
        /// <param name="token"></param>
        /// <returns>bool</returns>
        private async Task<bool> TryFetchAppleInternal(MediaItem mi, string query, string key, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;

            try
            {
                string itunesQuery = Uri.EscapeDataString(query.Trim());
                string url = $"https://itunes.apple.com/search?term={itunesQuery}&entity=song&limit=1";

                var response = await _http.GetStringAsync(url, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(response);
                var results = doc.RootElement.GetProperty("results");

                if (results.GetArrayLength() > 0)
                {
                    var first = results[0];
                    var artUrl = first.GetProperty("artworkUrl100").GetString()?.Replace("100x100bb", "600x600bb");
                    var trackName = first.TryGetProperty("trackName", out var tn) ? tn.GetString() : null;
                    var artistName = first.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                    var collectionName = first.TryGetProperty("collectionName", out var cn) ? cn.GetString() : null;
                    var primaryGenreName = first.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() : null;
                    var releaseDate = first.TryGetProperty("releaseDate", out var rd) ? rd.GetString() : null;
                    var trackNumber = first.TryGetProperty("trackNumber", out var tnum) ? tnum.GetUInt32() : 0;

                    if (!string.IsNullOrEmpty(artUrl))
                    {
                        try
                        {
                            var imgData = await _http.GetByteArrayAsync(artUrl, token).ConfigureAwait(false);
                            if (!IsWithinEmbeddedImageCap(imgData)) return false;
                            var bmp = await Task.Run(() => {
                                using var ms = new MemoryStream(imgData);
                                return _maxThumbnailWidth.HasValue
                                    ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                                    : new Bitmap(ms);
                            }, token);

                            AddToCoverCache(key, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => {
                                if (string.IsNullOrWhiteSpace(mi.Title) || mi.Title == mi.FileName) mi.Title = trackName;
                                if (string.IsNullOrWhiteSpace(mi.Artist)) mi.Artist = artistName;
                                if (string.IsNullOrWhiteSpace(mi.Album)) mi.Album = collectionName;
                                if (string.IsNullOrWhiteSpace(mi.Genre)) mi.Genre = primaryGenreName;
                                if (mi.Year == 0 && DateTime.TryParse(releaseDate, out var dt)) mi.Year = (uint)dt.Year;
                                if (mi.Track == 0) mi.Track = trackNumber;

                                mi.CoverBitmap = bmp;
                                mi.CoverFound = true;
                                mi.SaveCoverBitmapAction = item => TrySaveEmbeddedCover(item, imgData);
                            });
                            return true;
                        }
                        catch (Exception ex) { Log.Warn($"Failed to process Apple artwork URL for {key}", ex); }
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Error in iTunes search for {query}", ex); }
            return false;
        }

        /// <summary>
        /// Cleans up a search query by removing common extraneous terms often found in video titles, such as "Official Video", "HD", "Lyrics", etc.
        /// </summary>
        /// <param name="query"></param>
        /// <returns>Cleaned string</returns>
        private string CleanSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";
            // Remove common music video noise like (Official Video), [HD], etc.
            var cleaned = Regex.Replace(query, @"\s*[\(\[][^\]\)]*(?:official|video|audio|lyrics|hd|4k|hq|remix|feat|ft|music video)[^\]\)]*[\)\]]", "", RegexOptions.IgnoreCase);
            // Remove standalone tags
            cleaned = Regex.Replace(cleaned, @"\s*(?:official video|music video|lyric video|official audio|4k video|hd video)\b", "", RegexOptions.IgnoreCase);
            return cleaned.Trim();
        }

        /// <summary>
        /// Attempts to find and apply metadata for a URL (e.g. YouTube).
        /// Checks local cache first before fetching from online providers.
        /// </summary>
        public async Task SetupOnlineMetadata(MediaItem mi)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mi.FileName)) return;
                string url = mi.FileName;

                // Use a safe hash for the cache filename to avoid illegal characters
                string cacheId = BinaryMetadataHelper.GetCacheId(url);

                var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", cacheId + ".meta");
                var cacheDir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                if (File.Exists(cachePath))
                {
                    var meta = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath));

                    // Check if cache contains valid info or just leftovers from a failed previous scrape
                    bool isPlaceholder = meta == null || string.IsNullOrWhiteSpace(meta.Title) || 
                                       url.Contains(meta.Title, StringComparison.OrdinalIgnoreCase);

                    if (!isPlaceholder)
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            if (meta == null) return;

                            //Load basic info from metadata
                            if (!string.IsNullOrWhiteSpace(meta.Title))
                                mi.Title = meta.Title;
                            if (!string.IsNullOrWhiteSpace(meta.Artist))
                                mi.Artist = meta.Artist;
                            if (!string.IsNullOrWhiteSpace(meta.Album))
                                mi.Album = meta.Album;
                            if (meta.Year > 0)
                                mi.Year = meta.Year;
                            if (!string.IsNullOrWhiteSpace(meta.Genre))
                                mi.Genre = meta.Genre;
                            if (meta.Track > 0)
                                mi.Track = meta.Track;
                            if (!string.IsNullOrWhiteSpace(meta.Comment))
                                mi.Comment = meta.Comment;
                            if (!string.IsNullOrWhiteSpace(meta.Lyrics))
                                mi.Lyrics = meta.Lyrics;

                            //Load cover from metadata
                            var cover = meta.Images?.FirstOrDefault(x => x.Kind != TagImageKind.Wallpaper);
                            if (cover != null)
                            {
                                var mi_bmp = await Task.Run(() => {
                                    using var ms = new MemoryStream(cover.Data);
                                    return _maxThumbnailWidth.HasValue
                                        ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                                        : new Bitmap(ms);
                                });

                                AddToCoverCache(mi.FileName!, mi_bmp);
                                mi.CoverBitmap = mi_bmp;
                                mi.CoverFound = true;
                            }
                        });
                        return;
                    }
                }

                var (videoTitle, videoAuthor, thumbUrl, videoGenre, videoYear) = await YouTubeThumbnail.GetOnlineMetadataAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(videoTitle)) return;

                // Immediately set title/artist/genre/year on UI
                await Dispatcher.UIThread.InvokeAsync(() => {
                    // Overwrite title if it is empty, matches the full URL, or contains the title (likely placeholder like video ID)
                    bool isPlaceholder = string.IsNullOrWhiteSpace(mi.Title) || 
                                       mi.Title == mi.FileName || 
                                       (mi.FileName != null && mi.FileName.Contains(mi.Title));

                    if (isPlaceholder)
                        mi.Title = videoTitle;

                    if (string.IsNullOrWhiteSpace(mi.Artist))
                        mi.Artist = videoAuthor;
                    if (mi.Year == 0 && videoYear > 0)
                        mi.Year = videoYear;
                    if (string.IsNullOrWhiteSpace(mi.Genre))
                        mi.Genre = videoGenre;
                });

                byte[]? data = null;
                if (!string.IsNullOrEmpty(thumbUrl))
                {
                    await _throttle.WaitAsync();
                    try
                    {
                        data = await _http.GetByteArrayAsync(thumbUrl).ConfigureAwait(false);
                        if (IsWithinEmbeddedImageCap(data))
                        {
                            // UI Update for cover
                            await Dispatcher.UIThread.InvokeAsync(() => {
                                using var ms = new MemoryStream(data);
                                mi.CoverBitmap = _maxThumbnailWidth.HasValue
                                    ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                                    : new Bitmap(ms);
                                mi.CoverFound = true;
                                mi.SaveCoverBitmapAction = item => TrySaveEmbeddedCover(item, data);
                            });
                        }
                        else
                        {
                            data = null; // Reset if too large
                        }
                    }
                    catch (Exception ex) { Log.Error($"Failed to fetch online thumbnail for {mi.FileName}", ex); }
                    finally { _throttle.Release(); }
                }

                // Persist collected metadata and thumbnail to local sidecar file
                await UpdateLocalMetadataAsync(mi, data, null).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error($"Error setting up online metadata for {mi.FileName}", ex); }
        }

        /// <summary>
        /// Creates or updates a sidecar .meta file in the cache directory containing all possible info.
        /// </summary>
        private async Task UpdateLocalMetadataAsync(MediaItem mi, byte[]? pic, byte[]? wall)
        {
            try
            {
                if (string.IsNullOrEmpty(mi.FileName)) return;

                string cacheId = BinaryMetadataHelper.GetCacheId(mi.FileName);

                var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", cacheId + ".meta");
                var cacheDir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                    Directory.CreateDirectory(cacheDir);

                var metadata = new CustomMetadata
                {
                    Title = mi.Title ?? "",
                    Artist = mi.Artist ?? "",
                    Album = mi.Album ?? "",
                    Track = mi.Track,
                    Year = mi.Year,
                    Genre = mi.Genre ?? "",
                    Comment = mi.Comment ?? "",
                    Lyrics = mi.Lyrics ?? "",
                    Images = []
                };

                if (pic != null)
                {
                    metadata.Images.Add(new ImageData
                    {
                        Data = pic,
                        Kind = TagImageKind.Cover,
                        MimeType = "image/png"
                    });
                }

                if (wall != null)
                {
                    metadata.Images.Add(new ImageData
                    {
                        Data = wall,
                        Kind = TagImageKind.Wallpaper,
                        MimeType = "image/png"
                    });
                }

                await Task.Run(() => BinaryMetadataHelper.SaveMetadata(cachePath, metadata));
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to update local metadata cache for {mi.FileName}", ex);
            }
        }

        /// <summary>
        /// Adds a bitmap image to the cover cache, updating the entry if the specified key already exists.
        /// </summary>
        /// <remarks>If an existing bitmap is replaced, the previous instance is disposed to prevent
        /// memory leaks. The method manages the order of cached items and trims the cache if it exceeds the allowed
        /// limit.</remarks>
        /// <param name="key">The unique identifier for the bitmap image to be added or updated in the cache. This parameter cannot be
        /// null or empty.</param>
        /// <param name="bmp">The bitmap image to add to the cache. This parameter cannot be null.</param>
        private void AddToCoverCache(string key, Bitmap bmp)
        {
            if (string.IsNullOrEmpty(key) || bmp == null) return;

            // Try to add or update the cache without leaking previous Bitmap instances
            if (_coverCache.TryGetValue(key, out var existing))
            {
                // Attempt to replace the existing bitmap atomically
                if (_coverCache.TryUpdate(key, bmp, existing))
                {
                    try { existing?.Dispose(); } catch (Exception ex) { Log.Debug("Error disposing existing cover bitmap on update", ex); }
                    // Do not enqueue on replacement to avoid duplicate ordering entries
                }
                else
                {
                    // Fallback: set and do not enqueue to avoid duplicates
                    _coverCache[key] = bmp;
                }
            }
            else
            {
                if (_coverCache.TryAdd(key, bmp))
                {
                    _cacheOrder.Enqueue(key);
                }
                else
                {
                    // Concurrent add/race: try to dispose the bmp we couldn't store to avoid leaks
                    try { if (!_coverCache.ContainsKey(key)) bmp.Dispose(); } catch (Exception ex) { Log.Debug("Error disposing bmp on race condition", ex); }
                }
            }

            // Trim if needed
            TrimCacheIfNeeded();
        }

        /// <summary>
        /// Removes the oldest entries from the cache to ensure the total number of cached items does not exceed the
        /// maximum allowed.
        /// </summary>
        /// <remarks>This method disposes of removed cache entries to free resources. It handles and logs
        /// any exceptions that occur during the trimming process. Call this method after adding new items to the cache
        /// to maintain the cache size within the specified limit.</remarks>
        private void TrimCacheIfNeeded()
        {
            try
            {
                while (_coverCache.Count > _maxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
                {
                    if (_coverCache.TryRemove(oldest, out var removedBmp))
                    {
                        try { removedBmp?.Dispose(); } catch (Exception ex) { Log.Debug("Error disposing removed cover bitmap during trim", ex); }
                    }
                    // if TryRemove failed it means it was already removed/updated; continue draining
                }
            }
            catch (Exception ex) { Log.Warn("Error during cover cache trim", ex); }
        }

        /// <summary>
        /// Cancels any ongoing metadata load operation for the specified media item path.
        /// This is typically called when a media item is removed from the playlist to prevent unnecessary processing and resource usage.
        /// </summary>
        /// <param name="path"></param>
        private void CancelLoad(string? path)
        {
            if (path != null && _loadingCts.TryRemove(path, out var cts))
            {
                try { cts.Cancel(); } catch (Exception ex) { Log.Debug($"Error canceling load for {path}", ex); }
                cts.Dispose();
            }
        }

        /// <summary>
        /// Action callback to trigger background saving of cover art.
        /// </summary>
        private void TrySaveEmbeddedCover(MediaItem item, byte[] bytes)
        {
            _ = TrySaveEmbeddedCoverAsync(item, bytes);
        }

        /// <summary>
        /// Saves raw image bytes back to the media file or to the sidecar metadata cache.
        /// If the file is currently playing, it suspends and resumes playback to allow file access.
        /// </summary>
        private async Task TrySaveEmbeddedCoverAsync(MediaItem item, byte[] bytes)
        {
            if (string.IsNullOrEmpty(item.FileName)) return;

            try
            {
                // Handle online media by saving to sidecar metadata
                if (!File.Exists(item.FileName) && (item.FileName.Contains("youtu", StringComparison.OrdinalIgnoreCase) || item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                {
                    string cacheId = BinaryMetadataHelper.GetCacheId(item.FileName);
                    var cachePath = Path.Combine(AppContext.BaseDirectory, "Cache", cacheId + ".meta");

                    var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                    metadata.Images ??= new List<ImageData>();
                    metadata.Images.RemoveAll(x => x.Kind == TagImageKind.Cover);
                    metadata.Images.Insert(0, new ImageData
                    {
                        Data = bytes,
                        MimeType = "image/jpeg",
                        Kind = TagImageKind.Cover
                    });

                    BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
                    return;
                }
                // For local files, ensure the file exists before attempting to edit
                if (!File.Exists(item.FileName)) return;

                double position = 0;
                bool wasPlaying = false;

                // For local files, handle suspension if the file is currently playing
                if (_player != null && _player.CurrentMediaItem?.FileName == item.FileName)
                {
                    (position, wasPlaying) = await _player.SuspendForEditingAsync();
                }
                // Save embedded cover using TagLib# on a background thread to avoid UI freezes
                await Task.Run(() =>
                {
                    try
                    {
                        using var f = TagLib.File.Create(item.FileName);
                        var pic = new TagLib.Picture(new ByteVector(bytes))
                        {
                            Type = PictureType.FrontCover,
                            MimeType = "image/jpeg"
                        };
                        f.Tag.Pictures = new[] { pic };
                        f.Save();
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[MetadataScrapper] TagLib save error for {item.FileName}", ex);
                    }
                });
                // Resume playback if we suspended it
                if (_player != null && _player.CurrentMediaItem?.FileName == item.FileName)
                {
                    await _player.ResumeAfterEditingAsync(item.FileName!, position, wasPlaying);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MetadataScrapper] Failed to save embedded cover for {item.FileName}", ex);
            }
        }

        /// <summary>
        /// Disposes all resources used by the scrapper, including the HTTP client and cached bitmaps.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _playlist.CollectionChanged -= Playlist_CollectionChanged;
            foreach (var cts in _loadingCts.Values) { try { cts.Cancel(); } catch (Exception ex) { Log.Debug("Error canceling load during dispose", ex); } cts.Dispose(); }
            _http.Dispose();
            _throttle.Dispose();

            // Dispose and clear cached bitmaps
            try
            {
                foreach (var kv in _coverCache)
                {
                    try { kv.Value?.Dispose(); } catch (Exception ex) { Log.Debug("Error disposing cached bitmap during dispose", ex); }
                }
            }
            catch (Exception ex) { Log.Warn("Error during cover cache disposal", ex); }
            _coverCache.Clear();
            // Clear ordering queue
            while (_cacheOrder.TryDequeue(out _)) { }
        }
    }
}