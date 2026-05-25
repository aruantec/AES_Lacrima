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


using AES_Core.Logging;
namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
        private async Task PopulateMissingStreamMetadataAsync(IReadOnlyList<MediaItem> items)
        {
            foreach (var item in items)
            {
                if (item.Duration > 0)
                    continue;

                await TryPopulateStreamMetadataAsync(item);
                await Task.Delay(MetadataStaggerDelayMs);
            }
        }

        private static Bitmap GenerateDefaultFolderCover() => PlaceholderGenerator.GenerateMusicPlaceholder();

        private async Task TryPopulateStreamMetadataAsync(MediaItem item)
        {
            try
            {
                if (item.FileName == null)
                    return;

                var info = await YtDlpMetadata.GetBasicMetadataAsync(item.FileName);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(info.Title) && (string.IsNullOrWhiteSpace(item.Title) || item.Title == (YouTubeThumbnail.ExtractVideoId(item.FileName) ?? item.FileName)))
                        item.Title = info.Title;

                    if (!string.IsNullOrWhiteSpace(info.Artist) && string.IsNullOrWhiteSpace(item.Artist))
                        item.Artist = info.Artist;

                    if (!string.IsNullOrWhiteSpace(info.Album) && string.IsNullOrWhiteSpace(item.Album))
                        item.Album = info.Album;

                    if (info.DurationSeconds is > 0 && item.Duration <= 0)
                        item.Duration = info.DurationSeconds.Value;
                });

                if (info.DurationSeconds is > 0)
                {
                    await Task.Run(() =>
                    {
                        var cacheId = BinaryMetadataHelper.GetCacheId(item.FileName);
                        var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                        var cacheDir = Path.GetDirectoryName(cachePath);
                        if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
                            Directory.CreateDirectory(cacheDir);

                        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                        if (metadata.Duration <= 0)
                        {
                            metadata.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.Title ?? string.Empty : metadata.Title;
                            metadata.Artist = string.IsNullOrWhiteSpace(metadata.Artist) ? item.Artist ?? string.Empty : metadata.Artist;
                            metadata.Album = string.IsNullOrWhiteSpace(metadata.Album) ? item.Album ?? string.Empty : metadata.Album;
                            metadata.Track = metadata.Track == 0 ? item.Track : metadata.Track;
                            metadata.Year = metadata.Year == 0 ? item.Year : metadata.Year;
                            metadata.Genre = string.IsNullOrWhiteSpace(metadata.Genre) ? item.Genre ?? string.Empty : metadata.Genre;
                            metadata.Comment = string.IsNullOrWhiteSpace(metadata.Comment) ? item.Comment ?? string.Empty : metadata.Comment;
                            metadata.Lyrics = string.IsNullOrWhiteSpace(metadata.Lyrics) ? item.Lyrics ?? string.Empty : metadata.Lyrics;
                            metadata.ReplayGainTrackGain = metadata.ReplayGainTrackGain == 0 ? item.ReplayGainTrackGain : metadata.ReplayGainTrackGain;
                            metadata.ReplayGainAlbumGain = metadata.ReplayGainAlbumGain == 0 ? item.ReplayGainAlbumGain : metadata.ReplayGainAlbumGain;
                            metadata.Duration = info.DurationSeconds.Value;
                            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
                        }
                    });
                }
            }
            catch (Exception logEx) { Log.Warn("Leave the item as-is when yt-dlp metadata is unavailable.", logEx); }
        }

        private async Task TryLoadYouTubeThumbnailFastAsync(MediaItem item)
        {
            try
            {
                if (item.FileName == null) return;

                // Let metadata cache win only when it already contains a usable cover.
                var cacheId = BinaryMetadataHelper.GetCacheId(item.FileName);
                var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                if (System.IO.File.Exists(cachePath))
                {
                    var cachedMetadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath));
                    var hasCachedCover = cachedMetadata?.Images?.Any(image =>
                        image is { Data.Length: > 0 } &&
                        image.Kind != TagImageKind.Wallpaper &&
                        image.Kind != TagImageKind.LiveWallpaper) == true;

                    if (hasCachedCover)
                        return;
                }

                // Do not replace a cover that was already restored from local metadata cache.
                var shouldFetch = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var hasCover = item.CoverBitmap != null;
                    var hasCustomCover = hasCover && (DefaultFolderCover == null || item.CoverBitmap != DefaultFolderCover);
                    return !hasCustomCover;
                });
                if (!shouldFetch) return;

                var videoId = YouTubeThumbnail.ExtractVideoId(item.FileName);
                if (string.IsNullOrWhiteSpace(videoId)) return;

                var urls = new[]
                {
                    $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg",
                    $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg",
                    $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg"
                };

                await FastThumbnailThrottle.WaitAsync();
                try
                {
                    foreach (var url in urls)
                    {
                        try
                        {
                            using var response = await FastThumbnailClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (!response.IsSuccessStatusCode) continue;

                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes.Length == 0) continue;

                            using var stream = new MemoryStream(bytes);
                            var bitmap = Bitmap.DecodeToWidth(stream, FastThumbnailDecodeWidth);
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var hasCurrentCover = item.CoverBitmap != null;
                                var hasCustomCover = hasCurrentCover && (DefaultFolderCover == null || item.CoverBitmap != DefaultFolderCover);
                                if (hasCustomCover)
                                {
                                    bitmap.Dispose();
                                    return;
                                }

                                item.CoverBitmap = bitmap;
                                item.IsLoadingCover = false;
                            });
                            return;
                        }
                        catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }
                    }
                }
                finally
                {
                    FastThumbnailThrottle.Release();
                }
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }
        }

        private IEnumerable<MediaItem> LoadMediaItemsWithTrackOrder(string path)
        {
            var files = SupportedTypes
                .SelectMany(pattern => Directory.EnumerateFiles(path, pattern))
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                });

            var ordered = files
                .Select(file => new { File = file, Track = TryReadTrackNumber(file) })
                .OrderBy(x => x.Track.HasValue ? 0 : 1)
                .ThenBy(x => x.Track ?? uint.MaxValue)
                .ThenBy(x => Path.GetFileName(x.File), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ordered)
            {
                yield return new MediaItem
                {
                    FileName = entry.File,
                    Title = Path.GetFileName(entry.File),
                    CoverBitmap = DefaultFolderCover,
                    Track = entry.Track ?? 0
                };
            }
        }

        private static uint? TryReadTrackNumber(string file)
        {
            try
            {
                using var tagFile = TagLib.File.Create(file);
                var track = tagFile.Tag.Track;
                return track > 0 ? track : null;
            }
            catch
            {
                return null;
            }
        }

        private bool CanDeletePointedItem() => PointedIndex != -1 && PointedIndex < CoverItems.Count;

        private bool CanAddItems() => LoadedAlbum != null;

        private bool CanClearLoadedAlbum() => HasLoadedAlbumItems;

        private async Task PlayIndexSelection(int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= PlaybackQueue.Count) return;
            SelectedMediaItem = PlaybackQueue[currentIndex];

            // Sync UI selection if the item exists in the currently viewed collection
            int viewIndex = CoverItems.IndexOf(SelectedMediaItem);
            if (viewIndex != -1) SelectedIndex = viewIndex;

            await PlayMediaItemAsync(SelectedMediaItem);
        }

        /// <summary>
        /// Plays the specified media item asynchronously using the audio player.
        /// </summary>
        /// <remarks>If the media item's file name is a URL, the media URL service is used to open and
        /// play the item. Otherwise, the file is played directly by the audio player.</remarks>
        /// <param name="item">The media item to play. Must not be null and must have a valid file name.</param>
        /// <returns>A task that represents the asynchronous operation of playing the media item.</returns>
        private async Task PlayMediaItemAsync(MediaItem item)
        {
            // 'item' is non-nullable; only check other nullable dependencies and the file name
            if (AudioPlayer == null || item.FileName == null) return;

            var currentItem = AudioPlayer.CurrentMediaItem;
            var isRequestedTrackCurrent = currentItem != null &&
                                          (ReferenceEquals(currentItem, item) ||
                                           string.Equals(currentItem.FileName, item.FileName, StringComparison.Ordinal));

            if (IsVideoMode && isRequestedTrackCurrent)
            {
                // Do not reload the same playing video; just bring the viewport back.
                IsVideoViewportDismissed = false;
                _pendingTrackLoadItem = null;
                IsTrackLoadPending = false;
                return;
            }

            if (IsVideoMode)
                IsVideoViewportDismissed = true;

            _pendingTrackLoadItem = item;
            IsTrackLoadPending = true;
            EnsureMediaItemCoverIsLoaded(item);

            if (!await TryPlayMediaItemAsync(item).ConfigureAwait(false))
            {
                await SkipInvalidItemsAsync(item).ConfigureAwait(false);
            }
        }

        private async Task<bool> TryPlayMediaItemAsync(MediaItem item)
        {
            if (AudioPlayer == null || item.FileName == null)
                return false;

            if (item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                if (_mediaUrlService == null)
                    return false;

                try
                {
                    return await _mediaUrlService.OpenMediaItemAsync(AudioPlayer, item, IsVideoMode).ConfigureAwait(false);
                }
                catch
                {
                    return false;
                }
            }

            if (!System.IO.File.Exists(item.FileName))
                return false;

            try
            {
                await AudioPlayer.PlayFile(item, IsVideoMode).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task SkipInvalidItemsAsync(MediaItem failedItem)
        {
            if (PlaybackQueue.Count == 0)
                return;

            var currentIndex = PlaybackQueue.IndexOf(failedItem);
            if (currentIndex < 0)
                return;

            var total = PlaybackQueue.Count;
            for (var offset = 1; offset < total; offset++)
            {
                var nextIndex = currentIndex + offset;
                if (nextIndex >= total)
                {
                    if (AudioPlayer?.RepeatMode == RepeatMode.All)
                        nextIndex %= total;
                    else
                        break;
                }

                var nextItem = PlaybackQueue[nextIndex];
                _pendingTrackLoadItem = nextItem;
                IsTrackLoadPending = true;
                await InvokeOnUiAsync(() =>
                {
                    SelectedMediaItem = nextItem;
                    var coverIndex = CoverItems.IndexOf(nextItem);
                    if (coverIndex >= 0)
                        SelectedIndex = coverIndex;
                }).ConfigureAwait(false);

                if (await TryPlayMediaItemAsync(nextItem).ConfigureAwait(false))
                {
                    return;
                }
            }

            _pendingTrackLoadItem = null;
            IsTrackLoadPending = false;
        }

        private bool GetCurrentIndex(out int currentIndex)
        {
            currentIndex = -1;
            if (SelectedMediaItem == null || PlaybackQueue.Count == 0) return false;
            currentIndex = PlaybackQueue.IndexOf(SelectedMediaItem);
            return currentIndex != -1;
        }

        private void ApplyFilter()
        {
            if (LoadedAlbum?.Children == null)
            {
                CoverItems = new AvaloniaList<MediaItem>();
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                IsNoAlbumLoadedVisible = true;
                RefreshLoadedAlbumState();
                return;
            }

            var query = SearchText?.Trim();
            MediaItem? preferredItem = null;
            int currentSelectedIndex = GetRoundedSelectedIndex(SelectedIndex);
            if (currentSelectedIndex >= 0 && currentSelectedIndex < CoverItems.Count)
                preferredItem = CoverItems[currentSelectedIndex];
            preferredItem ??= HighlightedItem;
            preferredItem ??= SelectedMediaItem;

            if (string.IsNullOrWhiteSpace(query))
                CoverItems = LoadedAlbum.Children;
            else
            {
                var filtered = LoadedAlbum.Children
                    .Where(item =>
                        (item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                CoverItems = new AvaloniaList<MediaItem>(filtered);
            }

            if (CoverItems.Count == 0)
            {
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                IsNoAlbumLoadedVisible = true;
                RefreshLoadedAlbumState();
                return;
            }

            int nextIndex = preferredItem != null ? CoverItems.IndexOf(preferredItem) : -1;
            if (nextIndex < 0)
                nextIndex = 0;

            SelectedIndex = nextIndex;
            if (PointedIndex >= CoverItems.Count)
                PointedIndex = -1;

            HighlightedItem = CoverItems[nextIndex];
            IsNoAlbumLoadedVisible = false;
            RefreshLoadedAlbumState();
        }

        private void RefreshLoadedAlbumState()
        {
            OnPropertyChanged(nameof(HasLoadedAlbumItems));
            OnPropertyChanged(nameof(ShowEmptyLoadedAlbumHint));
            OnPropertyChanged(nameof(EmptyLoadedAlbumMessage));
            ClearAlbumCommand.NotifyCanExecuteChanged();
        }

        private void RefreshAlbumListState()
        {
            OnPropertyChanged(nameof(HasAlbums));
            OnPropertyChanged(nameof(ShowEmptyAlbumListHint));
            OnPropertyChanged(nameof(CanSortAlbums));
            OnPropertyChanged(nameof(EmptyAlbumListMessage));
            SortAlbumsAscendingCommand.NotifyCanExecuteChanged();
            SortAlbumsDescendingCommand.NotifyCanExecuteChanged();
        }

        private static int GetRoundedSelectedIndex(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return -1;

            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private string GetUniqueAlbumName(string baseName)
        {
            if (!AlbumList.Any(a => string.Equals(a.Title, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;
            int i = 1;
            while (AlbumList.Any(a => string.Equals(a.Title, $"{baseName} ({i})", StringComparison.OrdinalIgnoreCase)))
                i++;
            return $"{baseName} ({i})";
        }

        private void QueueDeferredPersistedAlbumRestore()
        {
            if (_hasQueuedDeferredAlbumListRestore || _pendingPersistedAlbumList == null || _pendingPersistedAlbumList.Count == 0)
                return;

            _hasQueuedDeferredAlbumListRestore = true;
            Log.Info(
                $"MusicViewModel scheduling deferred playlist restore. Albums={_pendingPersistedAlbumList.Count}, " +
                $"Items={_pendingPersistedAlbumList.Sum(folder => folder.Children?.Count ?? 0)}.");

            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                await ApplyDeferredPersistedAlbumRestoreAsync();
            });
        }

        private async Task ApplyDeferredPersistedAlbumRestoreAsync()
        {
            var pendingAlbums = _pendingPersistedAlbumList;
            if (pendingAlbums == null || pendingAlbums.Count == 0)
                return;

            var restoreStopwatch = Stopwatch.StartNew();
            _isApplyingDeferredAlbumList = true;

            try
            {
                _pendingPersistedAlbumRestoreIndex = 0;

                for (int start = 0; start < pendingAlbums.Count; start += PlaylistUiAddBatchSize)
                {
                    var batch = pendingAlbums.Skip(start).Take(PlaylistUiAddBatchSize).ToList();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        AlbumList.AddRange(batch);
                    }, Avalonia.Threading.DispatcherPriority.Background);

                    _pendingPersistedAlbumRestoreIndex = Math.Min(pendingAlbums.Count, start + batch.Count);
                    await Task.Yield();
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyAlbumFilter();
                    if (LoadedAlbum != null)
                        ApplyFilter();

                    StartDeferredLibraryMetadataWarmupIfNeeded();
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
            finally
            {
                restoreStopwatch.Stop();
                _isApplyingDeferredAlbumList = false;

                if (ReferenceEquals(_pendingPersistedAlbumList, pendingAlbums))
                {
                    _pendingPersistedAlbumList = null;
                    _pendingPersistedAlbumRestoreIndex = 0;
                }

                Log.Info(
                    $"MusicViewModel deferred playlist restore completed in {restoreStopwatch.ElapsedMilliseconds} ms. " +
                    $"Albums={AlbumList.Count}, Items={GetAlbumItemCount()}.");
            }
        }

        private void StartDeferredLibraryMetadataWarmupIfNeeded()
        {
            if (_hasDeferredLibraryMetadataWarmupStarted || !_isMusicViewVisible || AlbumList.Count == 0)
                return;

            _hasDeferredLibraryMetadataWarmupStarted = true;
            Log.Info(
                $"MusicViewModel view became visible; starting deferred library metadata warmup. Albums={AlbumList.Count}, " +
                $"Items={GetAlbumItemCount()}, LoadedAlbum='{LoadedAlbum?.Title ?? "<none>"}'.");
            StartMetadataScrappersForLoadedFolders();
        }

        private async Task<PersistedPlaylistSnapshot> LoadPersistedPlaylistSnapshotAsync()
        {
            var section = await LoadSettingsSectionAsync();
            if (section == null)
            {
                Log.Info("MusicViewModel.LoadPersistedPlaylistSnapshotAsync found no persisted playlist snapshot.");
                return new PersistedPlaylistSnapshot(DefaultPersistedVolume, IsAlbumlistOpen, new AvaloniaList<FolderMediaItem>());
            }

            var restoreStopwatch = Stopwatch.StartNew();
            var volume = ReadDoubleSetting(section, "Volume", DefaultPersistedVolume);
            var isAlbumListOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen));
            var restoredAlbums = ReadPlaylistAlbums(section);
            InitializeRestoredAlbumRuntimeState(restoredAlbums);

            restoreStopwatch.Stop();
            Log.Info(
                $"MusicViewModel.LoadPersistedPlaylistSnapshotAsync parsed playlist snapshot in {restoreStopwatch.ElapsedMilliseconds} ms. " +
                $"Albums={restoredAlbums.Count}, Items={restoredAlbums.Sum(folder => folder.Children?.Count ?? 0)}.");
            return new PersistedPlaylistSnapshot(volume, isAlbumListOpen, restoredAlbums);
        }

        private void ApplyPersistedPlaylistSnapshot(PersistedPlaylistSnapshot snapshot)
        {
            if (AudioPlayer != null)
                AudioPlayer.Volume = snapshot.Volume;

            IsAlbumlistOpen = snapshot.IsAlbumListOpen;
            _pendingPersistedAlbumList = snapshot.Albums.Count > 0 ? snapshot.Albums : null;
            _pendingPersistedAlbumRestoreIndex = 0;
        }

        private static AvaloniaList<FolderMediaItem> ReadPlaylistAlbums(JsonObject section)
        {
            var restoredAlbums = new AvaloniaList<FolderMediaItem>();
            if (!section.TryGetPropertyValue(nameof(AlbumList), out var node) || node == null)
                return restoredAlbums;

            try
            {
                var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<FolderMediaItem>>)SettingsJsonContext.Default.GetTypeInfo(typeof(List<FolderMediaItem>))!;
                var list = node.Deserialize(typeInfo);
                if (list != null)
                    restoredAlbums.AddRange(list);
            }
            catch (Exception ex)
            {
                Log.Warn("MusicViewModel.ReadPlaylistAlbums failed to parse persisted playlist snapshot.", ex);
            }

            return restoredAlbums;
        }

        private static void InitializeRestoredAlbumRuntimeState(IEnumerable<FolderMediaItem> albums)
        {
            foreach (var folder in albums)
            {
                foreach (var child in folder.Children)
                    child.SaveCoverBitmapAction ??= _ => { };
            }
        }

        private IEnumerable<FolderMediaItem> GetAlbumsForPersistence()
        {
            if (_pendingPersistedAlbumList == null)
                return AlbumList;

            if (_pendingPersistedAlbumRestoreIndex <= 0 && AlbumList.Count == 0)
                return _pendingPersistedAlbumList;

            return AlbumList.Concat(_pendingPersistedAlbumList.Skip(_pendingPersistedAlbumRestoreIndex));
        }

        private int GetAlbumItemCount() => AlbumList.Sum(folder => folder.Children?.Count ?? 0);
    }
}
