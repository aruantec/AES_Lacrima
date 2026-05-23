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


namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
        partial void OnSelectedMediaItemChanged(MediaItem? value)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _mprisService?.NotifyStateChanged(nameof(SelectedMediaItem));
            }
        }

        partial void OnAlbumListChanged(AvaloniaList<FolderMediaItem>? oldValue, AvaloniaList<FolderMediaItem> newValue)
        {
            if (oldValue != null)
            {
                oldValue.CollectionChanged -= AlbumList_CollectionChanged;
                foreach (var item in oldValue)
                    UnsubscribeFolder(item);
            }
            // Subscribe to changes in the new list
            newValue.CollectionChanged += AlbumList_CollectionChanged;
            foreach (var item in newValue)
                SubscribeFolder(item);
            if (!_isApplyingDeferredAlbumList)
                ApplyAlbumFilter();
            RefreshAlbumListState();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            ApplyFilter();
            ReduceCoverResidency(value);
            RefreshLoadedAlbumState();

            if (value != null && IsPrepared)
            {
                QueueLoadedAlbumCoverLoad(value);
                if (_scanMissingStreamDurationsOnLoadedAlbum)
                    QueueOpenedAlbumStreamDurationScan(value);
            }

            _scanMissingStreamDurationsOnLoadedAlbum = false;
        }

        partial void OnSearchTextChanged(string? value) => ApplyFilter();

        partial void OnSearchAlbumTextChanged(string? value) => ApplyAlbumFilter();

        partial void OnSelectedAlbumChanged(FolderMediaItem? value)
        {
            SyncSelectedAlbumIndexFromAlbum(value);
        }

        partial void OnFilteredAlbumListChanged(AvaloniaList<FolderMediaItem>? oldValue, AvaloniaList<FolderMediaItem> newValue)
        {
            SyncSelectedAlbumIndexFromAlbum(SelectedAlbum);
        }

        partial void OnIsAddUrlPopupOpenChanged(bool value)
        {
            if (!value)
            {
                if (!string.IsNullOrWhiteSpace(AddUrlText))
                {
                    var url = AddUrlText!.Trim();
                    if (IsMediaDuplicate(url, out var existing))
                    {
                        if (existing != null && CoverItems.Contains(existing))
                        {
                            SelectedIndex = CoverItems.IndexOf(existing);
                            HighlightedItem = existing;
                        }
                        AddUrlText = string.Empty;
                        return;
                    }
                    if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
                    var item = new MediaItem
                    {
                        FileName = url,
                        Title = YouTubeThumbnail.ExtractVideoId(url) ?? url,
                        CoverBitmap = DefaultFolderCover
                    };
                    CoverItems.Add(item);
                    if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                    {
                        if (!LoadedAlbum.Children.Any(c => c.FileName == item.FileName))
                            LoadedAlbum.Children.Add(item);
                    }
                    var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                    var scanList = new AvaloniaList<MediaItem> { item };
                    var allowOnlineForScan = IsOnlineMediaItem(item) || AllowOnlineCoverLookup;
                    var scrapper = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512, allowOnlineLookup: allowOnlineForScan);
                    if (AllowOnlineCoverLookup)
                        _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(MetadataStaggerDelayMs);
                        await scrapper.EnqueueLoadForPublic(item, force: false);
                    });
                    if (CoverItems.Count == 1)
                    {
                        SelectedIndex = 0;
                        HighlightedItem = item;
                        IsNoAlbumLoadedVisible = false;
                        SearchText = string.Empty;
                    }
                }
                AddUrlText = string.Empty;
            }
        }
        partial void OnIsAddPlaylistPopupOpenChanged(bool value)
        {
            if (!value)
            {
                if (!string.IsNullOrWhiteSpace(AddPlaylistText))
                {
                    var playlistUrl = AddPlaylistText!.Trim();
                    AddPlaylistText = string.Empty;
                    IsAddingPlaylist = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var urls = await GetPlaylistVideoUrls(playlistUrl);
                            if (urls == null || urls.Count == 0) return;

                            if (DefaultFolderCover == null)
                            {
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    DefaultFolderCover = GenerateDefaultFolderCover();
                                });
                            }

                            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                            var addedItems = new List<MediaItem>();
                            var existingMediaSnapshot = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var source = LoadedAlbum?.Children?.ToList() ?? CoverItems.ToList();
                                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var youtubeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                foreach (var media in source)
                                {
                                    if (string.IsNullOrWhiteSpace(media.FileName))
                                        continue;

                                    paths.Add(media.FileName);
                                    var existingVideoId = TryExtractYouTubeVideoId(media.FileName);
                                    if (!string.IsNullOrWhiteSpace(existingVideoId))
                                        youtubeIds.Add(existingVideoId);
                                }

                                return (Paths: paths, YoutubeIds: youtubeIds, InitialCoverCount: CoverItems.Count);
                            });
                            var isFirstPlaylistItem = existingMediaSnapshot.InitialCoverCount == 0;

                            foreach (var url in urls)
                            {
                                if (string.IsNullOrWhiteSpace(url)) continue;

                                // Skip YouTube Shorts
                                if (url.Contains("/shorts/") || url.Contains("shorts/")) continue;

                                if (existingMediaSnapshot.Paths.Contains(url))
                                    continue;

                                var youtubeId = TryExtractYouTubeVideoId(url);
                                if (!string.IsNullOrWhiteSpace(youtubeId) && existingMediaSnapshot.YoutubeIds.Contains(youtubeId))
                                    continue;

                                var item = new MediaItem
                                {
                                    FileName = url,
                                    Title = YouTubeThumbnail.ExtractVideoId(url) ?? url,
                                    CoverBitmap = DefaultFolderCover
                                };

                                existingMediaSnapshot.Paths.Add(url);
                                if (!string.IsNullOrWhiteSpace(youtubeId))
                                    existingMediaSnapshot.YoutubeIds.Add(youtubeId);
                                addedItems.Add(item);
                            }

                            if (addedItems.Count > 0)
                            {
                                for (int start = 0; start < addedItems.Count; start += PlaylistUiAddBatchSize)
                                {
                                    var batch = addedItems.Skip(start).Take(PlaylistUiAddBatchSize).ToList();
                                    var selectFirstFromBatch = isFirstPlaylistItem && start == 0 ? batch.FirstOrDefault() : null;

                                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        CoverItems.AddRange(batch);

                                        if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                                            LoadedAlbum.Children.AddRange(batch.Where(item => !LoadedAlbum.Children.Any(c => c.FileName == item.FileName)));

                                        if (selectFirstFromBatch != null)
                                        {
                                            SelectedIndex = 0;
                                            HighlightedItem = selectFirstFromBatch;
                                            IsNoAlbumLoadedVisible = false;
                                            SearchText = string.Empty;
                                        }
                                    });

                                    if (OperatingSystem.IsMacOS())
                                        await Task.Delay(16);
                                }

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
                                var allowOnlineForScan = scanList.Any(IsOnlineMediaItem) || AllowOnlineCoverLookup;
                                var scrapper = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512, allowOnlineLookup: allowOnlineForScan);
                                for (int i = 0; i < addedItems.Count; i++)
                                {
                                    var queuedItem = addedItems[i];
                                    var delayMs = i * MetadataStaggerDelayMs;
                                    _ = Task.Run(async () =>
                                    {
                                        if (delayMs > 0) await Task.Delay(delayMs);
                                        await scrapper.EnqueueLoadForPublic(queuedItem, force: false);
                                    });
                                }
                            }
                        }
                        finally
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                IsAddingPlaylist = false;
                            });
                        }
                    });
                }
                else
                {
                    AddPlaylistText = string.Empty;
                }
            }
        }

        private static string? TryExtractYouTubeVideoId(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return (path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                ? YouTubeThumbnail.ExtractVideoId(path)
                : null;
        }
        partial void OnSelectedIndexChanged(double value)
        {
            int roundedIndex = GetRoundedSelectedIndex(value);
            if (roundedIndex >= 0 && roundedIndex < CoverItems.Count && CoverItems[roundedIndex] is { } highlighted)
            {
                HighlightedItem = highlighted;
            }
        }

        partial void OnSelectedAlbumIndexChanged(int value)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextAlbum =
                value >= 0 && value < FilteredAlbumList.Count
                    ? FilteredAlbumList[value]
                    : null;

            if (ReferenceEquals(SelectedAlbum, nextAlbum))
                return;

            try
            {
                _isSyncingAlbumSelection = true;
                SelectedAlbum = nextAlbum;
            }
            finally
            {
                _isSyncingAlbumSelection = false;
            }
        }
    }
}
