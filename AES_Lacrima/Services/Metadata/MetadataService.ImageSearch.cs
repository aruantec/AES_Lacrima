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
using File = System.IO.File;
using Path = System.IO.Path;


namespace AES_Lacrima.Services
{
    public partial class MetadataService : ViewModelBase, IMetadataService 
    {
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

            if (await TryAddImageFromUrlAsync(url, SelectedImageKind))
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
                    AddImageToCollection(ms.ToArray(), "image/png", SelectedImageKind, "clipboard-image");
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

            IReadOnlyList<string> searchQueries;
            if (_currentSelectedMedia != null)
            {
                // For emulation cover searches triggered from the metadata overlay,
                // prefer the exact leading query shown in the textbox. Falling back
                // through multiple alternates here can dilute results even when the
                // visible query is already correct.
                var romQueries = BuildAutoCoverQueries(_currentSelectedMedia, albumNorm);
                searchQueries = romQueries.Count == 0
                    ? []
                    : [romQueries[0]];
            }
            else
            {
                searchQueries = BuildMetadataSearchQueries(titleNorm, artistNorm, albumNorm);
            }

            if (searchQueries.Count == 0)
                return;

            _searchMode = MetadataSearchMode.Images;
            NotifyImageSearchOverlayPresentationChanged();
            var activeQuery = searchQueries[0];
            ImageSearchQuery = activeQuery;
            await SearchImagesCoreAsync(activeQuery, searchQueries, isRomSearch: _currentSelectedMedia != null);
        }

        [RelayCommand]
        private async Task AddGameplayAsync()
        {
            if (_currentSelectedMedia == null)
                return;

            var gameplayQuery = BuildGameplayVideoQuery(_currentSelectedMedia, Album);
            if (string.IsNullOrWhiteSpace(gameplayQuery))
                return;

            _searchMode = MetadataSearchMode.GameplayVideo;
            NotifyImageSearchOverlayPresentationChanged();
            ImageSearchQuery = gameplayQuery;
            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = $"Searching YouTube gameplay videos for \"{gameplayQuery}\"...";

            try
            {
                var results = await SearchYouTubeGameplayVideosAsync(gameplayQuery);
                ImageSearchResults = new AvaloniaList<WebImageSearchResult>(results.Take(MaxImageSearchResults).ToList());
                ImageSearchStatus = ImageSearchResults.Count == 0
                    ? $"No YouTube gameplay videos found for \"{gameplayQuery}\"."
                    : $"Found {ImageSearchResults.Count} YouTube gameplay videos for \"{gameplayQuery}\".";
            }
            catch (Exception ex)
            {
                SLog.Warn("Gameplay video search failed.", ex);
                ImageSearchStatus = "Gameplay video search failed.";
                ImageSearchResults = [];
            }
            finally
            {
                IsImageSearchLoading = false;
            }
        }

        [RelayCommand]
        private async Task SearchImagesAsync(string? query = null)
        {
            var activeQuery = string.IsNullOrWhiteSpace(query) ? ImageSearchQuery : query;
            if (string.IsNullOrWhiteSpace(activeQuery))
                return;

            _searchMode = MetadataSearchMode.Images;
            NotifyImageSearchOverlayPresentationChanged();
            await SearchImagesCoreAsync(activeQuery.Trim(), [activeQuery.Trim()], isRomSearch: true);
        }

        private void NotifyImageSearchOverlayPresentationChanged()
        {
            OnPropertyChanged(nameof(ImageSearchOverlayHeader));
        }

        private async Task SearchImagesCoreAsync(string activeQuery, IReadOnlyList<string> searchQueries, bool isRomSearch = false)
        {
            ImageSearchQuery = activeQuery;
            IsImageSearchOverlayOpen = true;
            IsImageSearchLoading = true;
            ImageSearchStatus = "Searching web images...";

            try
            {
                var results = await SearchWebImagesAsync(searchQueries, isRomSearch);
                ImageSearchResults = new AvaloniaList<WebImageSearchResult>(results.Take(MaxImageSearchResults).ToList());
                ImageSearchStatus = ImageSearchResults.Count == 0
                    ? $"No images found for \"{activeQuery}\"."
                    : $"Found {ImageSearchResults.Count} image candidates for \"{activeQuery}\".";
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

            if (_searchMode == MetadataSearchMode.GameplayVideo)
            {
                VideoUrl = result.FullImageUrl;
                if (_currentSelectedMedia != null)
                    _currentSelectedMedia.VideoUrl = VideoUrl;
                IsImageSearchOverlayOpen = false;
                ImageSearchStatus = $"Selected gameplay video: {VideoUrl}";
                return;
            }

            if (await TryAddImageFromUrlAsync(result.FullImageUrl, SelectedImageKind))
            {
                IsImageSearchOverlayOpen = false;
                ImageSearchStatus = $"Selected image added as {SelectedImageKind}.";
            }
        }
    }
}
