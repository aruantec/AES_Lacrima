using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using File = System.IO.File;
using Path = System.IO.Path;

namespace AES_Lacrima.Services
{
    public sealed class WebImageSearchResult
    {
        public required string ThumbnailUrl { get; init; }
        public required string FullImageUrl { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
    }

    public interface IMetadataService;

    [AutoRegister]
    public partial class MetadataService : ViewModelBase, IMetadataService
    {
        private static readonly ILog SLog = AES_Core.Logging.LogHelper.For<MetadataService>();
        private const int MaxImageSearchResults = 24;
        private const int MaxAutoCoverQueries = 8;
        private const int MaxAutoCoverCandidatesPerQuery = 8;
        private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly Regex BracketCleanupRegex = new(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex ImageKindDescriptionRegex = new(@"^\[AES_KIND:(?<kind>[A-Za-z]+)\]\s*", RegexOptions.Compiled);
        private static readonly Regex GoogleImgTagRegex = new(@"<img[^>]+(?:src|data-src|data-iurl)=""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleImgResRegex = new(@"[?&]imgurl=(?<url>[^&""'\s<>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleJsonImageUrlRegex = new(@"(?:""(?:(?:ou)|(?:iurl)|(?:imageUrl)|(?:thumbnailUrl))""\s*:\s*"")(?<url>https?:\\?/\\?/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex GoogleQuotedHttpUrlRegex = new(@"""(?<url>https?:\\?/\\?/[^""'\s<>]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BingJsonImageUrlRegex = new(@"""(?:murl|imgurl|turl|thumb|thumbnailUrl)""\s*:\s*""(?<url>https?:\\?/\\?/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BingHtmlEncodedImageUrlRegex = new(@"(?:murl|imgurl|turl|thumb|thumbnailUrl)&quot;:&quot;(?<url>https?:[^""'<>]+?)&quot;", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DirectImageUrlRegex = new(@"https?://[^""'\s<>\\]+?\.(?:jpg|jpeg|png|webp|gif|bmp|avif)(?:\?[^""'\s<>\\]*)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomDumpTokenRegex = new(@"\b(?:rev\s*\d+|beta|proto|prototype|demo|sample|unl|hack|translated?|translation|usa|europe|japan|world)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomReleaseTokenRegex = new(@"\b(?:complete|fixed|fix|patched?|update(?:d)?|release|final)\b|\bv(?:ersion)?\s*\d+(?:[._-]\d+)*(?:\s+\d+)*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CoverSearchTokenRegex = new(@"\b(?:cover(?:\s+art)?|album\s+cover|box\s*art)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly SemaphoreSlim AutoCoverLookupThrottle = new(2, 2);
        private const string GoogleConsentCookie = "CONSENT=YES+cb.20210328-17-p0.en+FX+471";
        private static readonly string[] NoiseTokens =
        [
            "lyrics", "lyric", "official video", "official audio", "official",
            "music video", "video", "audio", "hd", "4k", "remastered",
            "feat", "ft", "featuring", "live", "karaoke", "visualizer"
        ];

        private MediaItem? _currentSelectedMedia;

        [ObservableProperty] private bool _isOnlineMedia;
        [ObservableProperty] private string? _filePath;
        [ObservableProperty] private string? _title;
        [ObservableProperty] private string? _artists;
        [ObservableProperty] private string? _album;
        [ObservableProperty] private uint _track;
        [ObservableProperty] private uint _year;
        [ObservableProperty] private string? _genres;
        [ObservableProperty] private string? _comment;
        [ObservableProperty] private string? _lyrics;
        [ObservableProperty] private double _replayGainTrackGain;
        [ObservableProperty] private double _replayGainAlbumGain;
        [ObservableProperty] private TagImageKind _selectedImageKind;

        [ObservableProperty]
        private bool _isMetadataLoaded;

        [ObservableProperty]
        private AvaloniaList<TagImageModel> _images = [];

        [ObservableProperty] private string? _addImageUrl;
        [ObservableProperty] private string _imageSearchQuery = string.Empty;
        [ObservableProperty] private bool _isImageSearchOverlayOpen;
        [ObservableProperty] private bool _isImageSearchLoading;
        [ObservableProperty] private string _imageSearchStatus = string.Empty;
        [ObservableProperty] private AvaloniaList<WebImageSearchResult> _imageSearchResults = [];

        [AutoResolve]
        private MusicViewModel? _musicViewModel;

        public IEnumerable<TagImageKind> ImageKinds { get; } = Enum.GetValues<TagImageKind>();

        public async Task LoadMetadataAsync(MediaItem item)
        {
            _currentSelectedMedia = item;
            var resolvedPath = item.FileName;
            FilePath = resolvedPath;
            IsOnlineMedia = false;

            try
            {
                if (string.IsNullOrWhiteSpace(resolvedPath))
                    throw new ArgumentException("file missing", nameof(item));

                if (!File.Exists(resolvedPath))
                {
                    // Pre-populate with current media item info while loading from cache.
                    Title = item.Title;
                    Artists = item.Artist;
                    Album = item.Album;
                    Track = item.Track;
                    Year = item.Year;
                    Genres = item.Genre;
                    Comment = item.Comment;
                    Lyrics = item.Lyrics;
                    IsOnlineMedia = true;

                    var metadata = await Task.Run(() =>
                    {
                        var cacheId = BinaryMetadataHelper.GetCacheId(resolvedPath);
                        var metaData = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                        return BinaryMetadataHelper.LoadMetadata(metaData);
                    });

                    var newImages = (metadata?.Images ?? [])
                        .Select(img => new TagImageModel(img.Kind, img.Data, img.MimeType ?? "image/png") { OnDeleteImage = OnDeleteImage })
                        .ToList();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (metadata != null)
                        {
                            Title = metadata.Title;
                            Album = metadata.Album;
                            Artists = metadata.Artist;
                            Track = metadata.Track;
                            Year = metadata.Year;
                            Lyrics = metadata.Lyrics;
                            Genres = metadata.Genre;
                            Comment = metadata.Comment;
                            ReplayGainTrackGain = metadata.ReplayGainTrackGain;
                            ReplayGainAlbumGain = metadata.ReplayGainAlbumGain;
                            if (_currentSelectedMedia != null && metadata.Duration > 0)
                                _currentSelectedMedia.Duration = metadata.Duration;
                        }

                        foreach (var old in Images)
                            old.Dispose();

                        Images.Clear();
                        foreach (var image in newImages)
                        {
                            Images.Add(image);
                            if (image.Kind == TagImageKind.LiveWallpaper)
                                _ = LoadImageAsync(image);
                        }

                        IsMetadataLoaded = true;
                    });

                    return;
                }

                var snapshot = await Task.Run(() =>
                {
                    using var tlFile = TagLib.File.Create(resolvedPath);
                    var tag = tlFile.Tag;
                    var pics = tag.Pictures ?? [];
                    var imagesToAdd = new List<TagImageModel>(pics.Length);
                    foreach (var p in pics)
                    {
                        var kind = MapPictureToKind(p);
                        var data = p.Data.Data;
                        var mime = p.MimeType;
                        var desc = StripImageKindMarker(p.Description);
                        imagesToAdd.Add(new TagImageModel(kind, data, mime, desc) { OnDeleteImage = OnDeleteImage });
                    }

                    return new
                    {
                        tag.Title,
                        Artists = tag.JoinedPerformers,
                        tag.Album,
                        tag.Track,
                        tag.Year,
                        tag.Lyrics,
                        Genres = string.Join(";", tag.Genres ?? []),
                        tag.Comment,
                        Images = imagesToAdd
                    };
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Title = snapshot.Title;
                    Artists = snapshot.Artists;
                    Album = snapshot.Album;
                    Track = snapshot.Track;
                    Year = snapshot.Year;
                    Lyrics = snapshot.Lyrics;
                    Genres = snapshot.Genres;
                    Comment = snapshot.Comment;

                    foreach (var old in Images)
                        old.Dispose();

                    Images.Clear();
                    foreach (var img in snapshot.Images)
                        Images.Add(img);

                    IsMetadataLoaded = true;
                });
            }
            catch (Exception ex)
            {
                SLog.Error("Failed to load metadata", ex);
                await Dispatcher.UIThread.InvokeAsync(() => IsMetadataLoaded = false);
            }
        }

        [RelayCommand]
        private async Task SaveMetadataAsync(string? path = null)
        {
            try
            {
                if (!File.Exists(FilePath) && FilePath != null && FilePath.Contains("youtu", StringComparison.OrdinalIgnoreCase))
                {
                    // Get unique cache id
                    var cacheId = BinaryMetadataHelper.GetCacheId(FilePath);
                    // Construct metadata path
                    var metaData = ApplicationPaths.GetCacheFile(cacheId + ".meta");

                    // Ensure Cache directory exists
                    var metaDir = Path.GetDirectoryName(metaData);
                    if (!string.IsNullOrEmpty(metaDir) && !Directory.Exists(metaDir))
                        Directory.CreateDirectory(metaDir);

                    // Save metadata
                    try
                    {
                        var customMetadata = new CustomMetadata
                        {
                            Title = Title!,
                            Artist = Artists!,
                            Album = Album!,
                            Track = Track,
                            Year = Year,
                            Lyrics = Lyrics!,
                            Genre = Genres!,
                            Comment = Comment!,
                            ReplayGainTrackGain = ReplayGainTrackGain,
                            ReplayGainAlbumGain = ReplayGainAlbumGain,
                            Duration = _currentSelectedMedia?.Duration ?? 0.0,
                            Images = [.. Images.Select(img => new ImageData
                            {
                                Data = img.Data,
                                MimeType = img.MimeType,
                                Kind = img.Kind
                            })],
                            Videos = [.. Images.Where(img => img.Kind == TagImageKind.LiveWallpaper)
                                .Select(img => new VideoData
                                {
                                    MimeType = img.MimeType,
                                    Data = img.Data,
                                    Kind = img.Kind
                                })]
                        };

                        BinaryMetadataHelper.SaveMetadata(metaData, customMetadata);
                    }
                    catch (Exception e)
                    {
                        SLog.Error("Failed to save metadata cache", e);
                    }

                    // Set cover bitmap in current media item
                    if (_currentSelectedMedia != null
                        && Images.FirstOrDefault(cover => cover.Kind == TagImageKind.Cover) is { } localCoverImage)
                    {
                        using var ms = new MemoryStream(localCoverImage.Data);
                        _currentSelectedMedia.CoverBitmap = new Bitmap(ms);
                    }

                    // Set wallpaper bitmap in current media item
                    if (_currentSelectedMedia != null
                        && Images.FirstOrDefault(cover => cover.Kind == TagImageKind.Wallpaper) is { } localWallpaperImage)
                    {
                        using var ms = new MemoryStream(localWallpaperImage.Data);
                        _currentSelectedMedia.WallpaperBitmap = new Bitmap(ms);
                    }
                    // Update current media item
                    UpdateInfo();

                    return;
                }

                if (string.IsNullOrWhiteSpace(path))
                    path = FilePath;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new ArgumentException("file missing", nameof(path));

                using var tlFile = TagLib.File.Create(path);
                var tag = tlFile.Tag;
                Debug.WriteLine($"Tag type: {tag.GetType().FullName}");

                tag.Title = Title;
                tag.Performers = string.IsNullOrEmpty(Artists) ? [] : [Artists];
                tag.Album = Album;
                tag.Track = Track;
                tag.Year = Year;
                tag.Lyrics = Lyrics;
                tag.Genres = string.IsNullOrEmpty(Genres) ? [] : Genres.Split(';');
                tag.Comment = Comment;

                var picList = new List<IPicture>();
                TagImageModel? wallpaperImage = null;
                TagImageModel? coverImage = null;
                foreach (var img in Images)
                {
                    // Add wallpaper description
                    if (img.Kind == TagImageKind.Wallpaper)
                    {
                        wallpaperImage = img;
                    }
                    else if (img.Kind != TagImageKind.LiveWallpaper)
                    {
                        coverImage ??= img;
                        if (img.Kind == TagImageKind.Cover || img.Kind == TagImageKind.Other)
                            coverImage = img;
                    }

                    // Create picture
                    var pic = new Picture([.. img.Data])
                    {
                        Type = MapKindToPictureType(img),
                        MimeType = img.MimeType,
                        Description = BuildPictureDescription(img)
                    };

                    picList.Add(pic);
                }

                // Assign pictures
                tag.Pictures = [.. picList];
                if (_musicViewModel != null
                    && _musicViewModel?.SelectedMediaItem?.FileName == _currentSelectedMedia?.FileName
                    && _musicViewModel != null
                    && _musicViewModel.AudioPlayer != null)
                {
                    // Pause music playback
                    var (position, wasPlaying) = await _musicViewModel.AudioPlayer.SuspendForEditingAsync();
                    // Save tag
                    tlFile.Save();
                    // Resume music playback
                    await _musicViewModel.AudioPlayer.ResumeAfterEditingAsync(_currentSelectedMedia!.FileName!, position, wasPlaying);
                }
                else
                {
                    tlFile.Save();
                }

                // Update current media item
                UpdateInfo();

                // Set cover bitmap in current media item
                if (coverImage != null && _currentSelectedMedia != null)
                {
                    using var ms = new MemoryStream(coverImage.Data);
                    _currentSelectedMedia.CoverBitmap = new Bitmap(ms);
                }
                else if (_currentSelectedMedia != null)
                {
                    _currentSelectedMedia.CoverBitmap = null;
                }

                // Set wallpaper bitmap in current media item
                if (wallpaperImage != null && _currentSelectedMedia != null)
                {
                    using var ms = new MemoryStream(wallpaperImage.Data);
                    _currentSelectedMedia.WallpaperBitmap = new Bitmap(ms);
                }
            }
            catch (Exception ex)
            {
                SLog.Error("Failed to save metadata to file", ex);
            }
            finally
            {
                Close();
            }
        }

        private void UpdateInfo()
        {
            // Update current media item
            _currentSelectedMedia!.Title = Title;
            _currentSelectedMedia!.Artist = Artists;
            _currentSelectedMedia!.Album = Album;
            _currentSelectedMedia!.Track = Track;
            _currentSelectedMedia!.Year = Year;
            _currentSelectedMedia!.Lyrics = Lyrics;
            _currentSelectedMedia!.Genre = Genres;
            _currentSelectedMedia!.Comment = Comment;
            _currentSelectedMedia!.ReplayGainTrackGain = ReplayGainTrackGain;
            _currentSelectedMedia!.ReplayGainAlbumGain = ReplayGainAlbumGain;
        }

        [RelayCommand]
        private async Task OpenAddImageDialog()
        {
            var mainWindow = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow == null) return;

            var storageProvider = mainWindow.StorageProvider;

            var fileOptions = new FilePickerOpenOptions
            {
                Title = "Select Images or Videos",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("JPEG"),
                new FilePickerFileType("PNG"),
                new FilePickerFileType("BMP"),
                new FilePickerFileType("GIF"),
                new FilePickerFileType("WebP"),
                new FilePickerFileType("AVIF"),
                new FilePickerFileType("MP4"),
                new FilePickerFileType("AVI"),
                new FilePickerFileType("MKV"),
                new FilePickerFileType("MOV"),
            ]
            };
            var results = await storageProvider.OpenFilePickerAsync(fileOptions);
            foreach (var result in results)
            {
                await using var stream = await result.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var data = ms.ToArray();
                var lowerName = result.Name.ToLower();
                var mimeType = lowerName switch
                {
                    { } s when s.EndsWith(".png") => "image/png",
                    { } s when s.EndsWith(".webp") => "image/webp",
                    { } s when s.EndsWith(".avif") => "image/avif",
                    { } s when s.EndsWith(".mp4") => "video/mp4",
                    { } s when s.EndsWith(".avi") => "video/avi",
                    { } s when s.EndsWith(".mkv") => "video/x-matroska",
                    { } s when s.EndsWith(".mov") => "video/quicktime",
                    _ => "image/jpeg"
                };
                var isVideo = mimeType.StartsWith("video/");
                var kind = isVideo ? TagImageKind.LiveWallpaper : SelectedImageKind;
                var newImage = new TagImageModel(kind, data, mimeType, isVideo ? result.Path.AbsolutePath : result.Name)
                {
                    OnDeleteImage = OnDeleteImage
                };
                if (newImage.Kind == TagImageKind.LiveWallpaper)
                {
                    await LoadImageAsync(newImage);
                    newImage.RaisePropertyChanged(nameof(Image));
                }
                Images.Add(newImage);
            }
        }

        [RelayCommand]
        private async Task AddImageFromUrlAsync(string? imageUrl = null)
        {
            var url = string.IsNullOrWhiteSpace(imageUrl) ? AddImageUrl : imageUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (await TryAddImageFromUrlAsCoverAsync(url))
            {
                AddImageUrl = string.Empty;
                ImageSearchStatus = "Image added.";
            }
        }

        [RelayCommand]
        private async Task PasteImageFromClipboardAsync()
        {
            var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (mainWindow?.Clipboard == null)
                return;

            try
            {
                using var clipboardData = await mainWindow.Clipboard.TryGetDataAsync();
                var bitmap = clipboardData != null
                    ? await clipboardData.TryGetBitmapAsync()
                    : null;
                if (bitmap != null)
                {
                    using var ms = new MemoryStream();
                    bitmap.Save(ms);
                    AddImageToCollection(ms.ToArray(), "image/png", TagImageKind.Cover, "clipboard-image");
                    ImageSearchStatus = "Pasted image from clipboard.";
                    return;
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Clipboard bitmap read failed; trying text fallback.", ex);
            }

            try
            {
                var clipboardText = await mainWindow.Clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(clipboardText))
                    await AddImageFromUrlAsync(clipboardText.Trim());
            }
            catch (Exception ex)
            {
                SLog.Warn("Clipboard text read failed.", ex);
            }
        }

        [RelayCommand]
        private async Task SearchImagesByTitleAsync()
        {
            var titleNorm = NormalizeSearchTitle(Title);
            if (string.IsNullOrWhiteSpace(titleNorm))
                titleNorm = NormalizeSearchTitle(_currentSelectedMedia?.Title);
            
            var artistNorm = NormalizeSearchTitle(Artists);
            if (string.IsNullOrWhiteSpace(artistNorm))
                artistNorm = NormalizeSearchTitle(_currentSelectedMedia?.Artist);

            var albumNorm = NormalizeSearchTitle(Album);
            if (string.IsNullOrWhiteSpace(albumNorm))
                albumNorm = NormalizeSearchTitle(_currentSelectedMedia?.Album);

            if (string.IsNullOrWhiteSpace(titleNorm)
                && string.IsNullOrWhiteSpace(artistNorm)
                && string.IsNullOrWhiteSpace(albumNorm))
            {
                titleNorm = NormalizeSearchTitle(GetSearchFallbackFromFilename());
            }

            var searchQueries = BuildMetadataSearchQueries(titleNorm, artistNorm, albumNorm);
            if (searchQueries.Count == 0)
                return;

            var activeQuery = searchQueries[0];
            ImageSearchQuery = activeQuery;
            await SearchImagesCoreAsync(activeQuery, searchQueries);
        }

        [RelayCommand]
        private async Task SearchImagesAsync(string? query = null)
        {
            var activeQuery = string.IsNullOrWhiteSpace(query) ? ImageSearchQuery : query;
            if (string.IsNullOrWhiteSpace(activeQuery))
                return;

            await SearchImagesCoreAsync(activeQuery.Trim(), [activeQuery.Trim()]);
        }

        private async Task SearchImagesCoreAsync(string activeQuery, IReadOnlyList<string> searchQueries)
        {
            ImageSearchQuery = activeQuery;
            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = "Searching web images...";

            try
            {
                var results = await SearchWebImagesAsync(searchQueries);
                ImageSearchResults = new AvaloniaList<WebImageSearchResult>(results.Take(MaxImageSearchResults).ToList());
                ImageSearchStatus = ImageSearchResults.Count == 0
                    ? "No images found."
                    : $"Found {ImageSearchResults.Count} image candidates.";
            }
            catch (Exception ex)
            {
                SLog.Warn("Image search failed.", ex);
                ImageSearchStatus = "Image search failed.";
                ImageSearchResults = [];
            }
            finally
            {
                IsImageSearchLoading = false;
            }
        }

        [RelayCommand]
        private void CloseImageSearchOverlay()
        {
            IsImageSearchOverlayOpen = false;
        }

        [RelayCommand]
        private async Task SelectSearchImageAsync(WebImageSearchResult? result)
        {
            if (result == null)
                return;

            if (await TryAddImageFromUrlAsCoverAsync(result.FullImageUrl))
            {
                IsImageSearchOverlayOpen = false;
                ImageSearchStatus = "Selected image added as cover.";
            }
        }

        public async Task<bool> TryPopulateCoverFromLocalMetadataOrGoogleAsync(MediaItem item, string? albumName, CancellationToken cancellationToken = default)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return false;

            var acquired = false;
            try
            {
                await AutoCoverLookupThrottle.WaitAsync(cancellationToken);
                acquired = true;

                if (await TryApplyCoverFromLocalMetadataAsync(item, cancellationToken).ConfigureAwait(false))
                    return true;

                var searchQueries = BuildAutoCoverQueries(item, albumName)
                    .Take(MaxAutoCoverQueries)
                    .ToList();
                if (searchQueries.Count == 0)
                    return false;

                SLog.Debug($"Auto cover lookup queries for '{item.FileName}': {string.Join(" | ", searchQueries)}");

                foreach (var searchQuery in searchQueries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var bingResults = await FindBingImageResultsForAutoCoverAsync(searchQuery, cancellationToken).ConfigureAwait(false);
                    if (bingResults.Count == 0)
                    {
                        SLog.Debug($"Auto cover lookup returned no Bing candidates for query '{searchQuery}'.");
                        continue;
                    }

                    SLog.Debug($"Auto cover lookup returned {bingResults.Count} Bing candidates for query '{searchQuery}'.");

                    foreach (var candidate in bingResults)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            var download = await TryDownloadImageBytesAsync(candidate.FullImageUrl, cancellationToken).ConfigureAwait(false);
                            if (download.Bytes == null || string.IsNullOrWhiteSpace(download.MimeType))
                            {
                                SLog.Debug($"Skipping Bing candidate that could not be downloaded as an image: {candidate.FullImageUrl}");
                                continue;
                            }

                            await ApplyCoverBytesToItemAsync(item, download.Bytes, download.MimeType, cancellationToken).ConfigureAwait(false);
                            await SaveCoverToMetadataCacheAsync(item, download.Bytes, download.MimeType).ConfigureAwait(false);
                            SLog.Info($"Auto cover applied for '{item.Title}' from '{candidate.FullImageUrl}' using query '{searchQuery}'.");
                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            SLog.Warn($"Failed auto cover candidate for '{item.FileName}' from '{candidate.FullImageUrl}'.", ex);
                        }
                    }
                }

                SLog.Warn($"Auto cover lookup found no usable Bing candidates for '{item.FileName}'.");
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to populate auto cover for {item.FileName}", ex);
                return false;
            }
            finally
            {
                if (acquired)
                    AutoCoverLookupThrottle.Release();
            }
        }

        private void Close()
        {
            IsMetadataLoaded = false;
        }

        private void OnDeleteImage(TagImageModel img)
        {
            Dispatcher.UIThread.Post(() => { Images.Remove(img); img.Dispose(); });
        }

        private static string NormalizeSearchTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var normalized = BracketCleanupRegex.Replace(title, " ");
            normalized = normalized.Replace('_', ' ').Replace('|', ' ');

            foreach (var token in NoiseTokens)
                normalized = Regex.Replace(normalized, $@"\b{Regex.Escape(token)}\b", " ", RegexOptions.IgnoreCase);

            normalized = normalized.Replace(" - ", " ");
            normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();
            return normalized;
        }

        private string GetSearchFallbackFromFilename()
        {
            var candidates = new[]
            {
                FilePath,
                _currentSelectedMedia?.FileName
            };

            foreach (var candidate in candidates)
            {
                var normalized = ExtractFilenameForSearch(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }

            return string.Empty;
        }

        private static string ExtractFilenameForSearch(string? pathOrUrl)
        {
            if (string.IsNullOrWhiteSpace(pathOrUrl))
                return string.Empty;

            string fileName;
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
            {
                fileName = Path.GetFileNameWithoutExtension(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
            }
            else
            {
                fileName = Path.GetFileNameWithoutExtension(pathOrUrl);
            }

            return fileName.Replace('.', ' ')
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Trim();
        }

        private static List<string> BuildMetadataSearchQueries(string? title, string? artist, string? album)
        {
            var queries = new List<string>();

            AddDistinctQuery(queries, title, artist, album);
            AddDistinctQuery(queries, title, artist);
            AddDistinctQuery(queries, title, album);
            AddDistinctQuery(queries, artist, album, title);
            AddDistinctQuery(queries, title);
            AddDistinctQuery(queries, artist, album);
            AddDistinctQuery(queries, artist);
            AddDistinctQuery(queries, album);

            return queries;
        }

        private static void AddDistinctQuery(List<string> queries, params string?[] parts)
        {
            var value = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
            value = MultiSpaceRegex.Replace(value, " ").Trim();
            if (!string.IsNullOrWhiteSpace(value) && !queries.Contains(value, StringComparer.OrdinalIgnoreCase))
                queries.Add(value);
        }

        private static async Task<List<WebImageSearchResult>> SearchWebImagesAsync(IReadOnlyList<string> queries)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedQueries = queries
                .Select(NormalizeSearchTitle)
                .Where(static query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var query in normalizedQueries)
            {
                if (results.Count >= MaxImageSearchResults)
                    break;

                // 1. Try iTunes for high-quality music-specific metadata
                var songUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=80";
                await LoadItunesResults(songUri, seen, results);

                if (results.Count >= MaxImageSearchResults)
                    break;

                var albumUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=album&limit=80";
                await LoadItunesResults(albumUri, seen, results);
            }

            foreach (var query in normalizedQueries)
            {
                if (results.Count >= MaxImageSearchResults)
                    break;

                // 2. Bing Images fallback
                await LoadBingImageResults(query, seen, results);
            }

            foreach (var query in normalizedQueries)
            {
                if (results.Count >= MaxImageSearchResults)
                    break;

                // 3. Google Image Search fallback
                await LoadGoogleImageResults(query, seen, results);
            }

            return results;
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
            catch
            {
                // Keep the best-effort decoded value.
            }

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

        private async Task<bool> TryAddImageFromUrlAsCoverAsync(string url)
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

                AddImageToCollection(bytes, mimeType, TagImageKind.Cover, uri.AbsoluteUri);
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

        private async Task LoadImageAsync(TagImageModel model)
        {
            var ffmpegPath = FFmpegLocator.FindFFmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath) || model.Kind != TagImageKind.LiveWallpaper)
                return;

            try
            {
                var bitmap = await Task.Run(() =>
                {
                    var tempVideoPath = Path.GetTempFileName() + ".mp4";
                    File.WriteAllBytes(tempVideoPath, model.Data);
                    var outputFile = Path.GetTempFileName() + ".png";
                    var psi = new ProcessStartInfo(ffmpegPath, $"-ss 00:00:01 -i \"{tempVideoPath}\" -vframes 1 \"{outputFile}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    process?.WaitForExit();
                    if (process?.ExitCode != 0)
                        throw new Exception("FFmpeg failed");

                    var bmp = new Bitmap(outputFile);
                    File.Delete(tempVideoPath);
                    File.Delete(outputFile);
                    return bmp;
                });

                // Update cache on UI thread
                await Dispatcher.UIThread.InvokeAsync(() => { model.Image = bitmap; });
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to generate thumbnail for live wallpaper", ex);
                // Fallback: set to null or default
                await Dispatcher.UIThread.InvokeAsync(() => { model.Image = null; });
            }
        }

        private static List<string> BuildAutoCoverQueries(MediaItem item, string? albumName)
        {
            var rawTitle = NormalizeSearchTitle(item.Title);
            if (string.IsNullOrWhiteSpace(rawTitle))
                rawTitle = NormalizeSearchTitle(ExtractFilenameForSearch(item.FileName));

            var title = NormalizeRomSearchTitle(item.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeRomSearchTitle(ExtractFilenameForSearch(item.FileName));

            var normalizedAlbum = NormalizeSearchTitle(albumName ?? item.Album);
            var queries = new List<string>();
            var strippedTitle = StripRomReleaseTokens(title);
            var consoleTerms = EmulationConsoleCatalog.GetSearchQueryTerms(normalizedAlbum);

            if (!string.IsNullOrWhiteSpace(normalizedAlbum))
            {
                if (!string.IsNullOrWhiteSpace(rawTitle))
                {
                    AddDistinctQuery(queries, rawTitle, normalizedAlbum, "cover");
                    AddDistinctQuery(queries, rawTitle, normalizedAlbum, "box art");
                }

                AddDistinctQuery(queries, title, normalizedAlbum, "cover");
                AddDistinctQuery(queries, title, normalizedAlbum, "box art");

                foreach (var term in consoleTerms)
                {
                    if (!string.IsNullOrWhiteSpace(rawTitle))
                    {
                        AddDistinctQuery(queries, rawTitle, term, "cover");
                        AddDistinctQuery(queries, rawTitle, term, "box art");
                        AddDistinctQuery(queries, rawTitle, term);
                    }

                    AddDistinctQuery(queries, title, term, "cover");
                    AddDistinctQuery(queries, title, term, "box art");
                    AddDistinctQuery(queries, title, term);

                    if (!string.IsNullOrWhiteSpace(strippedTitle) &&
                        !string.Equals(strippedTitle, title, StringComparison.OrdinalIgnoreCase))
                    {
                        AddDistinctQuery(queries, strippedTitle, term, "cover");
                        AddDistinctQuery(queries, strippedTitle, term, "box art");
                        AddDistinctQuery(queries, strippedTitle, term);
                    }
                }

                AddDistinctQuery(queries, title, normalizedAlbum);

                if (!string.IsNullOrWhiteSpace(strippedTitle) &&
                    !string.Equals(strippedTitle, title, StringComparison.OrdinalIgnoreCase))
                {
                    AddDistinctQuery(queries, strippedTitle, normalizedAlbum, "cover");
                    AddDistinctQuery(queries, strippedTitle, normalizedAlbum, "box art");
                    AddDistinctQuery(queries, strippedTitle, normalizedAlbum);
                }
            }

            AddDistinctQuery(queries, title, "cover");
            AddDistinctQuery(queries, title, "box art");
            AddDistinctQuery(queries, title);

            if (!string.IsNullOrWhiteSpace(strippedTitle) &&
                !string.Equals(strippedTitle, title, StringComparison.OrdinalIgnoreCase))
            {
                AddDistinctQuery(queries, strippedTitle, "cover");
                AddDistinctQuery(queries, strippedTitle, "box art");
                AddDistinctQuery(queries, strippedTitle);
            }

            return queries;
        }

        private static IEnumerable<string> ExpandConsoleAlbumAliases(string albumName)
        {
            if (string.IsNullOrWhiteSpace(albumName))
                yield break;

            var compact = albumName.Replace(" ", string.Empty);

            foreach (var alias in EmulationConsoleCatalog.GetSearchAliases(albumName))
            {
                yield return alias;
            }

            if (!string.Equals(compact, albumName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var alias in EmulationConsoleCatalog.GetSearchAliases(compact))
                    yield return alias;
            }
        }

        private async Task<IReadOnlyList<WebImageSearchResult>> FindBingImageResultsForAutoCoverAsync(string query, CancellationToken cancellationToken)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            await LoadBingImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            return results
                .Take(MaxAutoCoverCandidatesPerQuery)
                .ToList();
        }

        private async Task<bool> TryApplyCoverFromLocalMetadataAsync(MediaItem item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), cancellationToken).ConfigureAwait(false);
            var cover = metadata?.Images?.FirstOrDefault(image => image.Kind == TagImageKind.Cover && image.Data.Length > 0);
            if (cover == null)
                return false;

            await ApplyCoverBytesToItemAsync(item, cover.Data, cover.MimeType ?? GuessMimeTypeFromBytes(cover.Data), cancellationToken, cachePath)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(metadata?.Title))
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.Title = metadata!.Title);
            }

            if (string.IsNullOrWhiteSpace(item.Album) && !string.IsNullOrWhiteSpace(metadata?.Album))
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.Album = metadata!.Album);
            }

            return true;
        }

        private static string GetMetadataCachePath(string? filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

        private async Task<IReadOnlyList<WebImageSearchResult>> FindWebImageResultsAsync(string query, CancellationToken cancellationToken)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cancellationToken.ThrowIfCancellationRequested();
            await LoadBingImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
                await LoadGoogleImageResultsForExactQuery(query, seen, results, cancellationToken).ConfigureAwait(false);
            return results;
        }

        private static async Task LoadBingImageResultsForExactQuery(string query, HashSet<string> seen, List<WebImageSearchResult> sink, CancellationToken cancellationToken)
        {
            try
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(query)}&form=HDRSC3&first=1";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.Referrer = new Uri("https://www.bing.com/");

                using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    SLog.Warn($"Bing image search returned HTTP {(int)response.StatusCode} for exact query '{query}'.");
                    return;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ExtractBingImageResults(html, seen, sink);
                SLog.Debug($"Bing image search extracted {sink.Count} candidate URLs for exact query '{query}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Bing image search failed for exact query: {query}", ex);
            }
        }

        private static async Task LoadGoogleImageResultsForExactQuery(string query, HashSet<string> seen, List<WebImageSearchResult> sink, CancellationToken cancellationToken)
        {
            try
            {
                if (sink.Count >= MaxImageSearchResults)
                    return;

                var url = $"https://www.google.com/search?hl=en&q={Uri.EscapeDataString(query)}&udm=2";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                request.Headers.Add("Cookie", GoogleConsentCookie);
                request.Headers.Referrer = new Uri("https://www.google.com/");

                using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    SLog.Warn($"Google image search returned HTTP {(int)response.StatusCode} for exact query '{query}'.");
                    return;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ExtractGoogleImageResults(html, seen, sink);
                SLog.Debug($"Google image search extracted {sink.Count} candidate URLs for exact query '{query}'.");

                if (sink.Count == 0)
                {
                    var snippet = html.Length <= 400 ? html : html[..400];
                    SLog.Warn($"Google image search extracted 0 candidates for '{query}'. Response snippet: {snippet}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Google image search failed for exact query: {query}", ex);
            }
        }

        private async Task<(byte[]? Bytes, string? MimeType)> TryDownloadImageBytesAsync(string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return (null, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await ImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, null);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
                return (null, null);

            var mimeType = response.Content.Headers.ContentType?.MediaType;
            mimeType ??= GuessMimeTypeFromUrl(uri.AbsolutePath);
            if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return (null, null);

            return (bytes, mimeType);
        }

        private async Task SaveCoverToMetadataCacheAsync(MediaItem item, byte[] bytes, string mimeType)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            var cachePath = GetMetadataCachePath(item.FileName);
            var cacheDirectory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(cacheDirectory) && !Directory.Exists(cacheDirectory))
                Directory.CreateDirectory(cacheDirectory);

            await Task.Run(() =>
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                metadata.Title = string.IsNullOrWhiteSpace(item.Title) ? metadata.Title : item.Title;
                metadata.Artist = string.IsNullOrWhiteSpace(item.Artist) ? metadata.Artist : item.Artist;
                metadata.Album = string.IsNullOrWhiteSpace(item.Album) ? metadata.Album : item.Album;
                metadata.Track = item.Track == 0 ? metadata.Track : item.Track;
                metadata.Year = item.Year == 0 ? metadata.Year : item.Year;
                metadata.Duration = item.Duration <= 0 ? metadata.Duration : item.Duration;
                metadata.Genre = string.IsNullOrWhiteSpace(item.Genre) ? metadata.Genre : item.Genre;
                metadata.Comment = string.IsNullOrWhiteSpace(item.Comment) ? metadata.Comment : item.Comment;
                metadata.Lyrics = string.IsNullOrWhiteSpace(item.Lyrics) ? metadata.Lyrics : item.Lyrics;
                metadata.ReplayGainTrackGain = item.ReplayGainTrackGain;
                metadata.ReplayGainAlbumGain = item.ReplayGainAlbumGain;
                metadata.Images ??= [];
                metadata.Images.RemoveAll(image => image.Kind == TagImageKind.Cover);
                metadata.Images.Insert(0, new ImageData
                {
                    Data = bytes,
                    MimeType = mimeType,
                    Kind = TagImageKind.Cover
                });

                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }).ConfigureAwait(false);
        }

        private async Task ApplyCoverBytesToItemAsync(MediaItem item, byte[] bytes, string mimeType, CancellationToken cancellationToken, string? cachePath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Bitmap bitmap;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                bitmap = new Bitmap(stream);
            }

            var resolvedCachePath = cachePath ?? GetMetadataCachePath(item.FileName);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.CoverBitmap = bitmap;
                item.CoverFound = true;
                item.LocalCoverPath = resolvedCachePath;
                item.SaveCoverBitmapAction = saveItem =>
                {
                    _ = SaveCoverToMetadataCacheAsync(saveItem, bytes, mimeType);
                };
            }, DispatcherPriority.Background);
        }

        private static string GuessMimeTypeFromBytes(byte[] bytes)
        {
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            {
                return "image/gif";
            }

            return "image/jpeg";
        }

        private static string NormalizeRomSearchTitle(string? title)
        {
            var normalized = NormalizeSearchTitle(title);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            normalized = RomDumpTokenRegex.Replace(normalized, " ");
            normalized = RomReleaseTokenRegex.Replace(normalized, " ");
            normalized = normalized.Replace('!', ' ')
                .Replace(',', ' ')
                .Replace('.', ' ')
                .Replace("  ", " ");

            return MultiSpaceRegex.Replace(normalized, " ").Trim();
        }

        private static string StripRomReleaseTokens(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var stripped = RomReleaseTokenRegex.Replace(title, " ");
            return MultiSpaceRegex.Replace(stripped, " ").Trim();
        }
    }
}
