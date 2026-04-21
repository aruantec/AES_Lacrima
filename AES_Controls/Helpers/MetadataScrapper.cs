using AES_Code.Models;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using Avalonia.Collections;
using AES_Core.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using log4net;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using TagLib;
using File = System.IO.File;

namespace AES_Controls.Helpers
{
    /// <summary>
    /// Provides background metadata extraction and cover art retrieval for media items.
    /// Supports local tagging via TagLib#, Apple/iTunes API searches, and online metadata.
    /// </summary>
    public sealed class MetadataScrapper : IDisposable
    {
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MetadataScrapper>();
        private static readonly string[] PreferredLocalArtworkBaseNames =
        [
            "cover",
            "folder",
            "front",
            "album",
            "albumart",
            "artwork",
            "thumbnail",
            "thumb"
        ];
        private static readonly string[] SupportedLocalArtworkExtensions =
        [
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".bmp",
            ".gif"
        ];
        private static readonly HashSet<string> VideoFileExtensions =
        [
            ".mp4",
            ".m4v",
            ".mkv",
            ".avi",
            ".mov",
            ".webm",
            ".wmv"
        ];

        /// <summary>The default limit for embedded image extraction (32MB).</summary>
        internal const int DefaultMaxEmbeddedImageBytes = 32 * 1024 * 1024;

        // Default timeout and instance-level HttpClient/Throttle
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim SharedThrottle = new(Environment.ProcessorCount * 2);
        private readonly AvaloniaList<MediaItem> _playlist;

        /// <summary>The collection of media items to track.</summary>
        public AvaloniaList<MediaItem> Playlist => _playlist;

        private readonly Bitmap _defaultCover;
        private readonly ConcurrentDictionary<string, Bitmap> _coverCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _loadingCts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _cacheOrder = new();
        private readonly int? _maxThumbnailWidth;
        private readonly int _maxCacheEntries;
        private readonly int _maxEmbeddedImageBytes;
        private readonly AudioPlayer? _player;
        private readonly bool _allowOnlineLookup;
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
        /// <param name="forceUpdate">Whether to bypass local metadata caches and force a fresh scan.</param>
        /// <param name="allowOnlineLookup">Whether remote metadata/thumbnail providers may be used.</param>
        public MetadataScrapper(AvaloniaList<MediaItem> playlist,
                                AudioPlayer player,
                                Bitmap? defaultCover,
                                string agentInfo,
                                int? maxThumbnailWidth = null,
                                int maxCacheEntries = 80,
                                int maxEmbeddedImageBytes = DefaultMaxEmbeddedImageBytes,
                    bool forceUpdate = false,
                    bool allowOnlineLookup = true)
        {
            //Initializers
            _playlist = playlist;
            _player = player;

            _maxThumbnailWidth = maxThumbnailWidth;
            _maxCacheEntries = Math.Max(1, maxCacheEntries);
            _maxEmbeddedImageBytes = maxEmbeddedImageBytes;
            _allowOnlineLookup = allowOnlineLookup;

            _defaultCover = defaultCover ?? PlaceholderGenerator.GenerateMusicPlaceholder(480, 400);

            // Set User-Agent for HTTP requests to improve compatibility with providers like Apple
            if (!string.IsNullOrEmpty(agentInfo) && !SharedHttpClient.DefaultRequestHeaders.UserAgent.Any())
            {
                try { SharedHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(agentInfo); }
                catch (Exception ex) { Log.Warn($"Failed to parse User-Agent: {agentInfo}", ex); }
            }
            // Enqueue initial load for existing items in the playlist using multiple concurrent loads
            // for local files to ensure they appear instantly without waiting for each other.
            _ = Task.Run(async () =>
            {
                var items = _playlist.ToArray();
                var tasks = new List<Task>();

                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    
                    // We execute local metadata loads in parallel to avoid head-of-line blocking.
                    // The internal SharedThrottle still protects against excessive disk/CPU usage.
                    var loadTask = Task.Run(async () =>
                    {
                        bool didNetwork;
                        if (forceUpdate) didNetwork = await LoadMetadataForItemAsync(item, null, true);
                        else didNetwork = await EnqueueLoadFor(item);

                        // If it was a network request, we still want a small delay to respect provider rate limits,
                        // but we handle it within the individual task.
                        if (didNetwork) await Task.Delay(500);
                    });

                    tasks.Add(loadTask);
                    
                    // Small throttle on task creation to avoid overwhelming the thread pool
                    if (i % 10 == 0) await Task.Delay(5);
                }

                await Task.WhenAll(tasks);
            });
            // Subscribe to playlist changes to handle new items and removals
            _playlist.CollectionChanged += Playlist_CollectionChanged;
        }

        private void Playlist_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var o in e.NewItems)
                    if (o is MediaItem mi)
                    {
                        // Use Task.Run to ensure we don't block the UI thread during EnqueueLoadFor
                        _ = Task.Run(async () => await EnqueueLoadFor(mi));
                    }
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
        public Task EnqueueLoadForPublic(MediaItem mi, CancellationToken ct = default, bool force = true) => LoadMetadataForItemAsync(mi, ct, force);

        /// <summary>
        /// Internal logic to decide if metadata needs to be loaded (e.g. if title is missing or cover is default).
        /// Returns true if a network request was initiated/performed, false if it was skipped or handled locally.
        /// </summary>
        private async Task<bool> EnqueueLoadFor(MediaItem mi)
        {
            if (mi.CoverBitmap == null) mi.CoverBitmap = _defaultCover;

            bool isOnlineMedia = !string.IsNullOrWhiteSpace(mi.FileName) && !File.Exists(mi.FileName);

            // Keep existing fast-skip behavior, but only skip when duration is already known as well.
            // For online media we must still check sidecar metadata, because a fast thumbnail fetch
            // may temporarily set a non-default cover before local cached metadata is applied.
            if (!string.IsNullOrWhiteSpace(mi.Title)
                && mi.CoverBitmap != null
                && mi.CoverBitmap != _defaultCover
                && mi.Duration > 0
                && !isOnlineMedia)
                return false;

            return await LoadMetadataForItemAsync(mi);
        }

        /// <summary>
        /// Performs the actual extraction of metadata for a media item.
        /// Returns true if a network operation was involved.
        /// </summary>
        private async Task<bool> LoadMetadataForItemAsync(MediaItem mi, CancellationToken? externalToken = null, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(mi.FileName) || _disposed) return false;

            var key = mi.FileName!;

            // Bypass memory cache if force is true
            if (!force && _coverCache.TryGetValue(key, out var cachedBmp))
            {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    mi.CoverBitmap = cachedBmp;
                });

                // Keep the fast path only when duration is already known.
                // If duration is missing, continue loading metadata so we can fill it.
                if (mi.Duration > 0) return false;
            }

            // Global throttle for metadata extraction to prevent OOM with large playlists
            await SharedThrottle.WaitAsync(externalToken ?? CancellationToken.None);

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

                bool isLocalFile = File.Exists(key);
                bool isStream = !isLocalFile && (key.StartsWith("http", StringComparison.OrdinalIgnoreCase));

                if (!isLocalFile && !isStream)
                {
                    Log.Warn($"MetadataScrapper: File not found and not a valid stream URL: {key}");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (mi.CoverBitmap == null || mi.CoverBitmap == _defaultCover)
                        {
                            mi.CoverBitmap = _defaultCover;
                        }
                        mi.CoverFound = true; // Mark as "found" (processed) even if failed to stop re-loading
                    });
                    return false;
                }

                string cacheId = BinaryMetadataHelper.GetCacheId(key);
                string cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");

                // Online items should prefer cached sidecar metadata before hitting the network.
                // Local files must prefer embedded metadata first and only use other fallbacks later.
                if (!force && !isLocalFile && File.Exists(cachePath))
                {
                    var meta = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), token);
                    if (meta != null)
                    {
                        await ApplyMetadataToItem(mi, meta, key).ConfigureAwait(false);

                        var hasUsableSidecarCover = HasUsableCoverImage(meta.Images);
                        var hasCustomCoverAssigned = mi.CoverBitmap != null && mi.CoverBitmap != _defaultCover;
                        if (hasUsableSidecarCover && hasCustomCoverAssigned)
                            return false;
                    }
                }

                if (!isLocalFile)
                {
                    if (!_allowOnlineLookup)
                        return false;

                    await SetupOnlineMetadata(mi, force).ConfigureAwait(false);
                    return true;
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
                            var match = Regex.Match(fileName, @"^(.*?)\s*[\-\u2013\u2014]\s*(.*)$");
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
                        var hasUsableEmbedded = pic != null || wall != null;
                        // Read duration from file properties (in seconds)
                        double duration = 0.0;
                        try { duration = file.Properties?.Duration.TotalSeconds ?? 0.0; } catch { }

                        return new { t = t ?? "", a = a ?? "", al, tr, yr, ge, co, ly, pic, wall, hasFrontCover, hasEmbedded, hasUsableEmbedded, Success = true, duration };
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error extracting tags from {key}", ex);
                        return new { t = Path.GetFileNameWithoutExtension(key), a = "", al = "", tr = 0u, yr = 0u, ge = "", co = "", ly = "", pic = (byte[]?)null, wall = (byte[]?)null, hasFrontCover = false, hasEmbedded = false, hasUsableEmbedded = false, Success = false, duration = 0.0 };
                    }
                }, token);

                if (token.IsCancellationRequested) return false;

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
                    try { if (tagResult.duration > 0 || (mi.Duration <= 0 && tagResult.duration >= 0)) mi.Duration = tagResult.duration; } catch { }
                }, DispatcherPriority.Background);

                // Small yield to UI thread
                await Task.Delay(1, token);

                // Local embedded metadata is authoritative for local files.
                // If usable embedded pictures exist, stop here and do not consult other sources.
                if (tagResult.hasUsableEmbedded)
                {
                    Log.Debug($"MetadataScrapper: {key} - processing usable embedded images (will skip online lookups)");
                    await ProcessEmbeddedImagesInternal(mi, tagResult.pic, tagResult.wall, key, token).ConfigureAwait(false);
                    await UpdateLocalMetadataAsync(mi, tagResult.pic, tagResult.wall).ConfigureAwait(false);
                    return false;
                }

                var localArtworkResult = await TryProcessLocalArtworkAsync(mi, key, token).ConfigureAwait(false);
                if (localArtworkResult.HasArtwork)
                {
                    Log.Debug($"MetadataScrapper: {key} - using local folder artwork at {localArtworkResult.ArtworkPath}; skipping online lookups");
                    await UpdateLocalMetadataAsync(mi, localArtworkResult.CacheableCoverBytes, null).ConfigureAwait(false);
                    return false;
                }

                if (await TryProcessLocalVideoThumbnailAsync(mi, key, token).ConfigureAwait(false) is { } videoThumbBytes)
                {
                    Log.Debug($"MetadataScrapper: {key} - using local video thumbnail; skipping online lookups");
                    await UpdateLocalMetadataAsync(mi, videoThumbBytes, null).ConfigureAwait(false);
                    return false;
                }

                if (!_allowOnlineLookup)
                {
                    await UpdateLocalMetadataAsync(mi, tagResult.pic, tagResult.wall).ConfigureAwait(false);
                    return false;
                }

                // No usable embedded pictures - fall back to online services
                Log.Debug($"MetadataScrapper: {key} - no usable embedded images, performing online lookup");
                bool didNetwork = false;
                if (mi.CoverBitmap == null || mi.CoverBitmap == _defaultCover)
                {
                    await FetchAppleMetadataInternal(mi, tagResult.t, tagResult.a, key, token).ConfigureAwait(false);
                    didNetwork = true;
                }

                await UpdateLocalMetadataAsync(mi, tagResult.pic, tagResult.wall).ConfigureAwait(false);
                return didNetwork;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => 
                {
                    mi.IsLoadingCover = false;
                    mi.MetadataProcessed = true;
                });
                _loadingCts.TryRemove(key, out _);
                SharedThrottle.Release();
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

        private async Task<(bool HasArtwork, string? ArtworkPath, byte[]? CacheableCoverBytes)> TryProcessLocalArtworkAsync(MediaItem mi, string key, CancellationToken token)
        {
            try
            {
                var artworkPath = FindLocalArtworkPath(key);
                if (string.IsNullOrWhiteSpace(artworkPath))
                    return (false, null, null);

                var bmp = await Task.Run(() =>
                {
                    using var fs = File.OpenRead(artworkPath);
                    return _maxThumbnailWidth.HasValue
                        ? Bitmap.DecodeToWidth(fs, _maxThumbnailWidth.Value)
                        : new Bitmap(fs);
                }, token).ConfigureAwait(false);

                AddToCoverCache(key, bmp);

                byte[]? cacheableBytes = null;
                try
                {
                    var info = new FileInfo(artworkPath);
                    if (_maxEmbeddedImageBytes <= 0 || info.Length <= _maxEmbeddedImageBytes)
                        cacheableBytes = await Task.Run(() => File.ReadAllBytes(artworkPath), token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"MetadataScrapper: failed to cache local artwork bytes for {artworkPath}", ex);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    mi.CoverBitmap = bmp;
                    mi.LocalCoverPath = artworkPath;
                    mi.CoverFound = true;
                });

                return (true, artworkPath, cacheableBytes);
            }
            catch (Exception ex)
            {
                Log.Warn($"MetadataScrapper: failed to process local artwork for {key}", ex);
                return (false, null, null);
            }
        }

        private async Task<byte[]?> TryProcessLocalVideoThumbnailAsync(MediaItem mi, string key, CancellationToken token)
        {
            try
            {
                if (!IsLocalVideoFile(key))
                    return null;

                var ffmpegPath = FFmpegLocator.FindFFmpegPath();
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                    return null;

                var outputFile = await Task.Run(() =>
                {
                    static bool RunFfmpeg(string ffmpegExe, string args)
                    {
                        var psi = new ProcessStartInfo(ffmpegExe, args)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var process = Process.Start(psi);
                        process?.WaitForExit();
                        return process != null && process.ExitCode == 0;
                    }

                    // 1) Representative thumbnail over the stream (usually avoids blank intro frames).
                    var candidates = new List<string>();
                    candidates.Add($"-hide_banner -loglevel error -y -i \"{key}\" -vf \"thumbnail=180,scale=-2:720\" -frames:v 1 \"{{0}}\"");

                    // 2) Fallback explicit seek points.
                    string[] seekPoints = ["00:00:03", "00:00:08", "00:00:15", "00:00:30"];
                    foreach (var seek in seekPoints)
                        candidates.Add($"-hide_banner -loglevel error -y -ss {seek} -i \"{key}\" -frames:v 1 -q:v 2 \"{{0}}\"");

                    foreach (var template in candidates)
                    {
                        var tempImagePath = Path.GetTempFileName() + ".jpg";
                        var args = string.Format(template, tempImagePath);

                        if (!RunFfmpeg(ffmpegPath, args) || !File.Exists(tempImagePath))
                        {
                            try { if (File.Exists(tempImagePath)) File.Delete(tempImagePath); } catch { }
                            continue;
                        }

                        try
                        {
                            using var validationBitmap = new Bitmap(tempImagePath);
                            if (IsLikelyBlackFrame(validationBitmap))
                            {
                                try { File.Delete(tempImagePath); } catch { }
                                continue;
                            }
                        }
                        catch
                        {
                            try { File.Delete(tempImagePath); } catch { }
                            continue;
                        }

                        return tempImagePath;
                    }

                    return null;
                }, token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(outputFile) || !File.Exists(outputFile))
                    return null;

                byte[]? bytes = null;
                Bitmap? bmp = null;
                try
                {
                    bytes = await Task.Run(() => File.ReadAllBytes(outputFile), token).ConfigureAwait(false);
                    if (bytes.Length == 0)
                        return null;

                    bmp = await Task.Run(() =>
                    {
                        using var ms = new MemoryStream(bytes);
                        return _maxThumbnailWidth.HasValue
                            ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                            : new Bitmap(ms);
                    }, token).ConfigureAwait(false);

                    AddToCoverCache(key, bmp);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        mi.CoverBitmap = bmp;
                    });

                    return bytes;
                }
                finally
                {
                    try { File.Delete(outputFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"MetadataScrapper: failed to extract local video thumbnail for {key}", ex);
                return null;
            }
        }

        private static bool IsLikelyBlackFrame(Bitmap bitmap)
        {
            try
            {
                var sourceSize = bitmap.Size;
                if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
                    return true;

                var sampleSize = new PixelSize(96, 54);
                using var small = new RenderTargetBitmap(sampleSize);
                using (var ctx = small.CreateDrawingContext())
                {
                    ctx.DrawImage(bitmap,
                        new Rect(0, 0, sourceSize.Width, sourceSize.Height),
                        new Rect(0, 0, sampleSize.Width, sampleSize.Height));
                }

                var stride = sampleSize.Width * 4;
                var pixelBytes = new byte[sampleSize.Height * stride];
                var handle = GCHandle.Alloc(pixelBytes, GCHandleType.Pinned);
                try
                {
                    small.CopyPixels(new PixelRect(0, 0, sampleSize.Width, sampleSize.Height), handle.AddrOfPinnedObject(), pixelBytes.Length, stride);
                }
                finally
                {
                    handle.Free();
                }

                int total = sampleSize.Width * sampleSize.Height;
                int darkPixels = 0;
                long lumaSum = 0;

                for (int i = 0; i < pixelBytes.Length; i += 4)
                {
                    byte b = pixelBytes[i];
                    byte g = pixelBytes[i + 1];
                    byte r = pixelBytes[i + 2];
                    byte a = pixelBytes[i + 3];

                    if (a < 16)
                    {
                        darkPixels++;
                        continue;
                    }

                    int luma = (r * 2126 + g * 7152 + b * 722) / 10000;
                    lumaSum += luma;
                    if (luma < 22)
                        darkPixels++;
                }

                var avgLuma = (double)lumaSum / Math.Max(1, total);
                var darkRatio = (double)darkPixels / Math.Max(1, total);
                return darkRatio > 0.94 || avgLuma < 24.0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLocalVideoFile(string path)
        {
            var ext = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(ext) && VideoFileExtensions.Contains(ext);
        }

        private async Task ApplyMetadataToItem(MediaItem mi, CustomMetadata meta, string key)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (meta == null) return;

                mi.Title = meta.Title;
                mi.Artist = meta.Artist;
                mi.Album = meta.Album;
                mi.Year = meta.Year;
                mi.Genre = meta.Genre;
                mi.Track = meta.Track;
                mi.Comment = meta.Comment;
                mi.Lyrics = meta.Lyrics;
                mi.ReplayGainTrackGain = meta.ReplayGainTrackGain;
                mi.ReplayGainAlbumGain = meta.ReplayGainAlbumGain;
                if (meta.Duration > 0) mi.Duration = meta.Duration;

                var cover = SelectPreferredCover(meta.Images);
                if (cover != null)
                {
                    var bmp = await Task.Run(() =>
                    {
                        using var ms = new MemoryStream(cover.Data);
                        return _maxThumbnailWidth.HasValue
                            ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                            : new Bitmap(ms);
                    });
                    AddToCoverCache(key, bmp);
                    mi.CoverBitmap = bmp;
                }

                var wall = meta.Images?.FirstOrDefault(x => x.Kind == TagImageKind.Wallpaper);
                if (wall != null)
                {
                    var wBmp = await Task.Run(() =>
                    {
                        using var ms = new MemoryStream(wall.Data);
                        return new Bitmap(ms);
                    });
                    mi.WallpaperBitmap = wBmp;
                }
            });
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

            // Prefer explicit or strongly implied front-cover images.
            if (includeCover)
            {
                foreach (var picture in pictures)
                {
                    if (picture == null) continue;
                    var data = picture.Data;
                    if (data == null) continue;
                    var bytes = data.Data;
                    if (bytes == null || bytes.Length == 0) continue;

                    var isLikelyCover = picture.Type == PictureType.FrontCover || IsLikelyCoverPicture(picture);
                    if (!isLikelyCover) continue;

                    // For explicit/likely covers, allow extraction even when exceeding maxBytes.
                    cover = bytes;
                    break;
                }
            }

            // If no explicit/likely cover found, pick first non-wallpaper/non-backcover candidate.
            if (includeCover && cover == null)
            {
                byte[]? oversizedFallback = null;

                foreach (var picture in pictures)
                {
                    if (picture == null) continue;
                    if (picture.Type == PictureType.BackCover) continue;
                    if (picture.Type == PictureType.Illustration) continue;

                    var isWallpaper = picture.Description?.Contains("wallpaper", StringComparison.OrdinalIgnoreCase) == true;
                    if (isWallpaper) continue;

                    var data = picture.Data;
                    if (data == null) continue;
                    var bytes = data.Data;
                    if (bytes == null || bytes.Length == 0) continue;

                    if (maxBytes > 0 && data.Count > maxBytes)
                    {
                        oversizedFallback ??= bytes;
                        continue;
                    }

                    cover = bytes;
                    break;
                }

                cover ??= oversizedFallback;
            }

            // Wallpaper selection: prefer explicit illustration or description containing 'wallpaper'.
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

        private static ImageData? SelectPreferredCover(IList<ImageData>? images)
        {
            if (images == null || images.Count == 0)
                return null;

            return images.FirstOrDefault(x => x.Kind == TagImageKind.Cover)
                ?? images.FirstOrDefault(x => x.Kind == TagImageKind.Artist)
                ?? images.FirstOrDefault(x => x.Kind == TagImageKind.Other)
                ?? images.FirstOrDefault(x => x.Kind == TagImageKind.BackCover)
                ?? images.FirstOrDefault(x => x.Kind != TagImageKind.Wallpaper && x.Kind != TagImageKind.LiveWallpaper);
        }

        private static bool HasUsableCoverImage(IList<ImageData>? images)
        {
            return SelectPreferredCover(images) is { Data.Length: > 0 };
        }

        internal static string? FindLocalArtworkPath(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            var directory = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            try
            {
                var imageFiles = Directory.EnumerateFiles(directory)
                    .Where(path => SupportedLocalArtworkExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (imageFiles.Count == 0)
                    return null;

                foreach (var baseName in PreferredLocalArtworkBaseNames)
                {
                    var exactMatch = imageFiles.FirstOrDefault(path =>
                        Path.GetFileNameWithoutExtension(path).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(exactMatch))
                        return exactMatch;
                }

                foreach (var baseName in PreferredLocalArtworkBaseNames)
                {
                    var prefixMatch = imageFiles.FirstOrDefault(path =>
                        Path.GetFileNameWithoutExtension(path).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(prefixMatch))
                        return prefixMatch;
                }

                return imageFiles
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Warn($"MetadataScrapper: failed to scan local artwork for {mediaPath}", ex);
                return null;
            }
        }

        private static bool IsLikelyCoverPicture(IPicture picture)
        {
            var desc = picture.Description;
            if (string.IsNullOrWhiteSpace(desc))
                return false;

            var normalized = desc.Trim();
            return normalized.Contains("[AES_KIND:Cover]", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("front cover", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("cover (front)", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("cover", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("cover", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("album art", StringComparison.OrdinalIgnoreCase);
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
                    found = await TryFetchAppleInternal(mi, cleanTitle, key, token);
                }

                if (!found)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => mi.CoverFound = true); // Mark as processed
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

                var responseMessage = await SharedHttpClient.GetAsync(url, token).ConfigureAwait(false);
                if (!responseMessage.IsSuccessStatusCode) return false;

                var response = await responseMessage.Content.ReadAsStringAsync(token).ConfigureAwait(false);
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
                    double trackTimeMillis = 0.0;
                    if (first.TryGetProperty("trackTimeMillis", out var ttm) && ttm.ValueKind == JsonValueKind.Number)
                        ttm.TryGetDouble(out trackTimeMillis);

                    if (!string.IsNullOrEmpty(artUrl))
                    {
                        try
                        {
                            var imgData = await SharedHttpClient.GetByteArrayAsync(artUrl, token).ConfigureAwait(false);
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
                                if (mi.Duration <= 0 && trackTimeMillis > 0) mi.Duration = trackTimeMillis / 1000.0;

                                mi.CoverBitmap = bmp;
                                mi.CoverFound = !string.IsNullOrEmpty(mi.FileName) && File.Exists(mi.FileName);
                                var saveBytes = imgData.ToArray();
                                mi.SaveCoverBitmapAction = item => TrySaveEmbeddedCover(item, saveBytes);
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
        /// Attempts to find and apply metadata for a URL.
        /// Checks local cache first before fetching from online providers.
        /// </summary>
        public async Task SetupOnlineMetadata(MediaItem mi, bool force = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mi.FileName)) return;
                string url = mi.FileName;

                // Use a safe hash for the cache filename to avoid illegal characters
                string cacheId = BinaryMetadataHelper.GetCacheId(url);

                var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                var cacheDir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                if (!force && File.Exists(cachePath))
                {
                    var meta = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath));
                    if (meta != null)
                    {
                        await ApplyMetadataToItem(mi, meta, url).ConfigureAwait(false);

                        var hasUsableSidecarCover = HasUsableCoverImage(meta.Images);
                        var hasCustomCoverAssigned = mi.CoverBitmap != null && mi.CoverBitmap != _defaultCover;
                        if (mi.Duration > 0 && hasUsableSidecarCover && hasCustomCoverAssigned)
                            return;
                    }
                }

                var (videoTitle, videoAuthor, thumbUrl, videoGenre, videoYear) = await YouTubeThumbnail.GetOnlineMetadataAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(videoTitle)) return;

                // Immediately set title/artist/genre/year on UI
                await Dispatcher.UIThread.InvokeAsync(() => {
                    // Overwrite title regardless if force is true, otherwise only if it's a placeholder
                    bool isPlaceholder = string.IsNullOrWhiteSpace(mi.Title) || 
                                       mi.Title == mi.FileName || 
                                       (mi.FileName != null && mi.FileName.Contains(mi.Title));

                    if (force || isPlaceholder)
                        mi.Title = videoTitle;

                    if (force || string.IsNullOrWhiteSpace(mi.Artist))
                        mi.Artist = videoAuthor;
                    if (force || (mi.Year == 0 && videoYear > 0))
                        mi.Year = videoYear;
                    if (force || string.IsNullOrWhiteSpace(mi.Genre))
                        mi.Genre = videoGenre;
                });

                byte[]? data = null;
                if (!string.IsNullOrEmpty(thumbUrl))
                {
                    try
                    {
                        data = await SharedHttpClient.GetByteArrayAsync(thumbUrl).ConfigureAwait(false);
                        if (IsWithinEmbeddedImageCap(data))
                        {
                            // Decode image off the UI thread to avoid blocking animations or UI responsiveness.
                            Bitmap? decoded = null;
                            try
                            {
                                decoded = await Task.Run(() => {
                                    using var ms = new MemoryStream(data);
                                    return _maxThumbnailWidth.HasValue
                                        ? Bitmap.DecodeToWidth(ms, _maxThumbnailWidth.Value)
                                        : new Bitmap(ms);
                                });

                                if (decoded != null && !string.IsNullOrEmpty(mi.FileName))
                                    AddToCoverCache(mi.FileName, decoded);
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"Failed to decode online thumbnail for {mi.FileName}", ex);
                                try { decoded?.Dispose(); } catch { }
                                decoded = null;
                            }

                            // Assign decoded bitmap on UI thread
                            await Dispatcher.UIThread.InvokeAsync(() => {
                                if (decoded != null)
                                {
                                    mi.CoverBitmap = decoded;
                                    mi.CoverFound = !string.IsNullOrEmpty(mi.FileName) && File.Exists(mi.FileName);
                                    var saveBytes = data.ToArray();
                                    mi.SaveCoverBitmapAction = item => TrySaveEmbeddedCover(item, saveBytes);
                                }
                            });
                        }
                        else
                        {
                            data = null; // Reset if too large
                        }
                    }
                    catch (Exception ex) { Log.Error($"Failed to fetch online thumbnail for {mi.FileName}", ex); }
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

                var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
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
                    ReplayGainTrackGain = mi.ReplayGainTrackGain,
                    ReplayGainAlbumGain = mi.ReplayGainAlbumGain,
                    Duration = mi.Duration,
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
        /// <remarks>
        /// Cached bitmaps can be shared with live UI bindings. Cache replacement/eviction intentionally avoids
        /// disposing bitmap instances here to prevent disposing images that Avalonia is still rendering.
        /// </remarks>
        /// <param name="key">The unique identifier for the bitmap image to be added or updated in the cache. This parameter cannot be
        /// null or empty.</param>
        /// <param name="bmp">The bitmap image to add to the cache. This parameter cannot be null.</param>
        private void AddToCoverCache(string key, Bitmap bmp)
        {
            if (string.IsNullOrEmpty(key) || bmp == null) return;

            // Try to add or update the cache. Do not dispose replaced instances here because they may still
            // be referenced by Image controls on the UI thread.
            if (_coverCache.TryGetValue(key, out var existing))
            {
                // Attempt to replace the existing bitmap atomically
                if (_coverCache.TryUpdate(key, bmp, existing))
                {
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
                    // Concurrent add/race: keep the instance alive to avoid disposing a bitmap that may
                    // already be in use by UI bindings from a parallel path.
                }
            }

            // Trim if needed
            TrimCacheIfNeeded();
        }

        /// <summary>
        /// Removes the oldest entries from the cache to ensure the total number of cached items does not exceed the
        /// maximum allowed.
        /// </summary>
        /// <remarks>
        /// Evicted entries are removed from dictionary ownership only. They are not disposed here because
        /// references can remain active in Avalonia visual tree bindings.
        /// </remarks>
        private void TrimCacheIfNeeded()
        {
            try
            {
                while (_coverCache.Count > _maxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
                {
                    _coverCache.TryRemove(oldest, out _);
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

        private static string DetectMimeType(byte[] bytes)
        {
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "image/png";

            if (bytes.Length >= 12
                && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                return "image/jpeg";

            return "image/jpeg";
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
                    var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");

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
                            MimeType = DetectMimeType(bytes),
                            Description = "[AES_KIND:Cover] saved"
                        };

                        var existing = (f.Tag.Pictures ?? Array.Empty<IPicture>())
                            .Where(p => p != null && p.Type != PictureType.FrontCover)
                            .ToList();
                        existing.Insert(0, pic);
                        f.Tag.Pictures = existing.ToArray();
                        f.Save();

                        using var verify = TagLib.File.Create(item.FileName);
                        var verifyHasCover = (verify.Tag.Pictures ?? Array.Empty<IPicture>())
                            .Any(p => p?.Type == PictureType.FrontCover && p.Data != null && p.Data.Count > 0);
                        if (!verifyHasCover)
                            throw new InvalidOperationException($"Cover save verification failed for {item.FileName}");
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

            // Do not dispose cached bitmaps during scrapper disposal.
            // Some controls may still reference these instances briefly during teardown/recomposition.
            _coverCache.Clear();
            // Clear ordering queue
            while (_cacheOrder.TryDequeue(out _)) { }
        }
    }
}
