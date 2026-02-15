using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
using System.Threading.Tasks;
using TagLib;
using File = System.IO.File;
using Path = System.IO.Path;

namespace AES_Lacrima.Services
{
    public interface IMetadataService;

    [AutoRegister]
    internal partial class MetadataService : ViewModelBase, IMetadataService
    {
        private static readonly ILog SLog = LogManager.GetLogger(typeof(MetadataService));

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
        [ObservableProperty] private TagImageKind _selectedImageKind;

        [ObservableProperty]
        private bool _isMetadataLoaded;

        [ObservableProperty]
        private AvaloniaList<TagImageModel> _images = [];

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
                    var metaData = Path.Combine(AppContext.BaseDirectory, "Cache", cacheId) + ".meta";
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
                    var metaData = Path.Combine(AppContext.BaseDirectory, "Cache", cacheId) + ".meta";

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

        private void Close()
        {
            IsMetadataLoaded = false;
        }

        private void OnDeleteImage(TagImageModel img)
        {
            Dispatcher.UIThread.Post(() => { Images.Remove(img); img.Dispose(); });
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
