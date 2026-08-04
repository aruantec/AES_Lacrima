using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Lacrima.Serialization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel
    {
        private static readonly FilePickerFileType AplFileType = new("AES Online Playlist")
        {
            Patterns = ["*.apl"]
        };

        private bool CanExportOnlinePlaylist() =>
            SelectedAlbum?.Children?.Any(IsOnlineMediaItem) == true;

        private bool CanImportOnlinePlaylist() => true;

        [RelayCommand(CanExecute = nameof(CanExportOnlinePlaylist))]
        private async Task ExportOnlinePlaylist()
        {
            var album = SelectedAlbum;
            if (album?.Children == null)
                return;

            var onlineItems = album.Children.Where(IsOnlineMediaItem).ToList();
            if (onlineItems.Count == 0)
                return;

            var storageProvider = GetStorageProvider();
            if (storageProvider == null)
                return;

            var suggestedName = SanitizeFileName(album.Title);
            if (string.IsNullOrWhiteSpace(suggestedName))
                suggestedName = "playlist";

            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Online Playlist",
                SuggestedFileName = suggestedName,
                DefaultExtension = "apl",
                FileTypeChoices = [AplFileType]
            }).ConfigureAwait(true);

            if (file == null)
                return;

            var document = new AplPlaylistDocument
            {
                Name = album.Title,
                Items = onlineItems.Select(ToAplItem).ToList()
            };

            try
            {
                await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    AplPlaylistJsonContext.Default.AplPlaylistDocument).ConfigureAwait(true);
                Log.Info($"Exported album '{document.Name}' with {document.Items.Count} online item(s) to '{file.Name}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to export online playlist to '{file.Name}'", ex);
            }
        }

        [RelayCommand(CanExecute = nameof(CanImportOnlinePlaylist))]
        private async Task ImportOnlinePlaylist()
        {
            var storageProvider = GetStorageProvider();
            if (storageProvider == null)
                return;

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Online Playlist",
                AllowMultiple = false,
                FileTypeFilter = [AplFileType]
            }).ConfigureAwait(true);

            if (files.Count == 0)
                return;

            AplPlaylistDocument? document;
            try
            {
                await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
                document = await JsonSerializer.DeserializeAsync(
                    stream,
                    AplPlaylistJsonContext.Default.AplPlaylistDocument).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to read online playlist '{files[0].Name}'", ex);
                return;
            }

            if (document?.Items == null || document.Items.Count == 0)
            {
                Log.Warn($"Online playlist '{files[0].Name}' contained no items.");
                return;
            }

            ImportOnlinePlaylistAsAlbum(document);
        }

        private void ImportOnlinePlaylistAsAlbum(AplPlaylistDocument document)
        {
            if (DefaultFolderCover == null)
                DefaultFolderCover = GenerateDefaultFolderCover();

            var baseName = string.IsNullOrWhiteSpace(document.Name) ? "Imported Album" : document.Name.Trim();
            var uniqueName = CreateUniqueAlbumTitle(baseName);

            var album = new FolderMediaItem
            {
                Title = uniqueName,
                Children = new AvaloniaList<MediaItem>(),
                CoverBitmap = DefaultFolderCover
            };

            var addedItems = new List<MediaItem>();
            foreach (var entry in document.Items)
            {
                var url = entry.Url?.Trim();
                if (string.IsNullOrWhiteSpace(url) || !IsOnlineMediaUrl(url))
                    continue;

                if (addedItems.Any(i => string.Equals(i.FileName, url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var youtubeId = TryExtractYouTubeVideoId(url);
                if (!string.IsNullOrWhiteSpace(youtubeId) &&
                    addedItems.Any(i => string.Equals(TryExtractYouTubeVideoId(i.FileName), youtubeId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var item = new MediaItem
                {
                    FileName = url,
                    Title = string.IsNullOrWhiteSpace(entry.Title)
                        ? (YouTubeThumbnail.ExtractVideoId(url) ?? url)
                        : entry.Title,
                    Artist = entry.Artist,
                    Album = entry.Album ?? uniqueName,
                    Duration = entry.Duration,
                    Track = entry.Track,
                    CoverBitmap = DefaultFolderCover
                };

                album.Children.Add(item);
                addedItems.Add(item);
            }

            if (addedItems.Count == 0)
            {
                Log.Warn("Online playlist import created no items (all entries invalid).");
                return;
            }

            album.RebuildPreviewItems(useFirstItemCover: true);
            AlbumList.Add(album);
            SelectedAlbum = album;
            OpenSelectedFolder();
            RefreshAlbumListState();
            RefreshLoadedAlbumState();

            if (AllowOnlineCoverLookup)
            {
                for (int i = 0; i < addedItems.Count; i++)
                {
                    var item = addedItems[i];
                    var delayMs = OperatingSystem.IsMacOS() ? i * 90 : i * 30;
                    _ = Task.Run(async () =>
                    {
                        if (delayMs > 0)
                            await Task.Delay(delayMs).ConfigureAwait(false);
                        await TryLoadYouTubeThumbnailFastAsync(item).ConfigureAwait(false);
                    });
                }
            }

            _ = Task.Run(async () => await PopulateMissingStreamMetadataAsync(addedItems).ConfigureAwait(false));

            var scanList = new AvaloniaList<MediaItem>(addedItems.Where(ShouldScanMetadataForItem));
            if (scanList.Count > 0 && AudioPlayer != null)
            {
                var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                var allowOnlineForScan = scanList.Any(IsOnlineMediaItem) || AllowOnlineCoverLookup;
                _ = new MetadataScrapper(scanList, AudioPlayer, DefaultFolderCover, agentInfo, 512, allowOnlineLookup: allowOnlineForScan);
            }

            Log.Info($"Imported album '{uniqueName}' with {addedItems.Count} online item(s).");
        }

        private string CreateUniqueAlbumTitle(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported Album";

            var uniqueName = baseName;
            var counter = 1;
            while (AlbumList.Any(a => string.Equals(a.Title, uniqueName, StringComparison.OrdinalIgnoreCase)) ||
                   FilteredAlbumList.Any(a => string.Equals(a.Title, uniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                uniqueName = $"{baseName} ({counter++})";
            }

            return uniqueName;
        }

        private static AplPlaylistItem ToAplItem(MediaItem item) => new()
        {
            Url = item.FileName ?? string.Empty,
            Title = item.Title,
            Artist = item.Artist,
            Album = item.Album,
            Duration = item.Duration,
            Track = item.Track
        };

        private static IStorageProvider? GetStorageProvider()
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            return lifetime?.MainWindow?.StorageProvider;
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim(' ', '.');
        }
    }
}
