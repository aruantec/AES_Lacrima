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
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private static readonly ILog SLog = LogManager.GetLogger(typeof(MetadataService));
        private const int MaxImageSearchResults = 24;
        private static readonly HttpClient ImageHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly Regex BracketCleanupRegex = new(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
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
            FilePath = item.FileName;
            IsOnlineMedia = false;

            if (!File.Exists(FilePath))
            {
                // Pre-populate with current media item info while loading from cache
                Title = item.Title;
                Artists = item.Artist;
                Album = item.Album;
                Track = item.Track;
                Year = item.Year;
                Genres = item.Genre;
                Comment = item.Comment;
                Lyrics = item.Lyrics;
                IsOnlineMedia = true;

                await Task.Run(() =>
                {
                    // Get unique cache id for the URL/Online item
                    var cacheId = BinaryMetadataHelper.GetCacheId(FilePath!);
                    // Construct metadata path
                    var metaData = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                    // Load metadata
                    if (BinaryMetadataHelper.LoadMetadata(metaData) is not { } metadata)
                        return;

                    // Set properties on UI thread
                    Dispatcher.UIThread.Post(() =>
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
                        IsOnlineMedia = true;

                        // Clear images and dispose
                        foreach (var old in Images)
                            old.Dispose();

                        Images.Clear();
                    });

                    // Load images
                    var newImages = metadata.Images
                        .Select(img => new TagImageModel(img.Kind, img.Data, "image/png") { OnDeleteImage = OnDeleteImage })
                        .ToList();

                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var image in newImages)
                        {
                            Images.Add(image);
                            if (image.Kind == TagImageKind.LiveWallpaper)
                            {
                                // Fire-and-forget loading of live wallpaper thumbnails.
                                _ = LoadImageAsync(image);
                            }
                        }
                    });
                });

                IsMetadataLoaded = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
                throw new ArgumentException("file missing", nameof(FilePath));

            await Task.Run(() =>
            {
                using var tlFile = TagLib.File.Create(FilePath);
                var tag = tlFile.Tag;

                Title = tag.Title;
                Artists = tag.JoinedPerformers;
                Album = tag.Album;
                Track = tag.Track;
                Year = tag.Year;
                Lyrics = tag.Lyrics;
                Genres = string.Join(";", tag.Genres ?? []);
                Comment = tag.Comment;

                var pics = tag.Pictures ?? [];
                var imagesToAdd = new List<TagImageModel>();
                foreach (var p in pics)
                {
                    var kind = MapPictureToKind(p);
                    var data = p.Data.Data;
                    var mime = p.MimeType;
                    var desc = p.Description;
                    var newImage = new TagImageModel(kind, data, mime, desc)
                    {
                        OnDeleteImage = OnDeleteImage
                    };

                    imagesToAdd.Add(newImage);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var old in Images)
                        old.Dispose();

                    Images.Clear();
                    foreach (var img in imagesToAdd)
                        Images.Add(img);
                });

                IsMetadataLoaded = true;
            });
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
                        img.Description = "wallpaper";
                        wallpaperImage = img;
                    }
                    else if (img.Kind == TagImageKind.Cover || img.Kind == TagImageKind.Other)
                    {
                        coverImage = img;
                    }

                    // Create picture
                    var pic = new Picture([.. img.Data])
                    {
                        Type = MapKindToPictureType(img),
                        MimeType = img.MimeType,
                        Description = img.Description
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
            var normalized = NormalizeSearchTitle(Title);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = NormalizeSearchTitle(_currentSelectedMedia?.Title);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            ImageSearchQuery = normalized;
            await SearchImagesAsync(normalized);
        }

        [RelayCommand]
        private async Task SearchImagesAsync(string? query = null)
        {
            var activeQuery = string.IsNullOrWhiteSpace(query) ? ImageSearchQuery : query;
            if (string.IsNullOrWhiteSpace(activeQuery))
                return;

            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = "Searching web images...";

            try
            {
                var results = await SearchWebImagesAsync(activeQuery.Trim());
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

        private static async Task<List<WebImageSearchResult>> SearchWebImagesAsync(string query)
        {
            var results = new List<WebImageSearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var songUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=80";
            await LoadItunesResults(songUri, seen, results);

            if (results.Count < MaxImageSearchResults)
            {
                var albumUri = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=album&limit=80";
                await LoadItunesResults(albumUri, seen, results);
            }

            return results;
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
                var artistName = item.TryGetProperty("artistName", out var artistNode) ? artistNode.GetString() : string.Empty;

                sink.Add(new WebImageSearchResult
                {
                    ThumbnailUrl = thumb,
                    FullImageUrl = full,
                    Title = trackName ?? string.Empty,
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
    }
}
