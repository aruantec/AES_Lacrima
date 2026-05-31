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
        private void QueueLoadedAlbumCoverLoad(FolderMediaItem folder, bool forceUpdate = false)
        {
            if (AudioPlayer == null || folder.Children.Count == 0)
                return;

            if (!TryBeginAlbumCoverLoad(folder))
                return;

            try
            {
                _loadedAlbumCoverCts?.Cancel();
                _loadedAlbumCoverCts?.Dispose();
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }

            _loadedAlbumCoverCts = new CancellationTokenSource();
            var ct = _loadedAlbumCoverCts.Token;
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";

            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadPriorityAlbumCoversAsync(folder, agentInfo, forceUpdate, ct);

                    while (!ct.IsCancellationRequested)
                    {
                        var hasMoreToLoad = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            folder.Children.Any(NeedsCoverLoad));

                        if (!hasMoreToLoad)
                            break;

                        await LoadAlbumCoversAsync(folder, agentInfo, forceUpdate, maxItemsToLoad: 24);

                        try
                        {
                            await Task.Delay(75, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var child in folder.Children)
                        {
                            if (!NeedsVisibleCoverLoad(child))
                                child.IsLoadingCover = false;
                        }

                        folder.IsLoadingCover = folder.Children.Any(child =>
                            NeedsVisibleCoverLoad(child) && child.IsLoadingCover);

                        folder.RebuildPreviewItems(rebuildStructure: false);
                    });

                    EndAlbumCoverLoad(folder);
                }
            }, ct);
        }

        private bool TryBeginAlbumCoverLoad(FolderMediaItem folder)
        {
            lock (_albumCoverLoadGate)
            {
                return _activeAlbumCoverLoads.Add(folder);
            }
        }

        private void EndAlbumCoverLoad(FolderMediaItem folder)
        {
            lock (_albumCoverLoadGate)
            {
                _activeAlbumCoverLoads.Remove(folder);
            }
        }

        public void RefreshLoadedAlbumMetadata()
        {
            if (LoadedAlbum == null || AudioPlayer == null)
                return;

            QueueLoadedAlbumCoverLoad(LoadedAlbum, forceUpdate: true);
            QueueOpenedAlbumStreamDurationScan(LoadedAlbum);
        }

        private void ReduceCoverResidency(FolderMediaItem? fullyLoadedAlbum)
        {
            DefaultFolderCover ??= GenerateDefaultFolderCover();
            var defaultCover = DefaultFolderCover;
            if (defaultCover == null) return;

            var protectedSelected = SelectedMediaItem;
            var protectedHighlighted = HighlightedItem;
            var protectedPlaying = AudioPlayer?.CurrentMediaItem;
            var protectedPlaybackItems = GetPlaybackItemsToKeepResident();

            foreach (var folder in AlbumList)
            {
                bool keepAllCovers = ReferenceEquals(folder, fullyLoadedAlbum);
                int previewKeepCount = Math.Min(folder.Children.Count, FolderPreviewCoverCount);

                for (int i = 0; i < folder.Children.Count; i++)
                {
                    var child = folder.Children[i];
                    if (keepAllCovers
                        || i < previewKeepCount
                        || ReferenceEquals(child, protectedSelected)
                        || ReferenceEquals(child, protectedHighlighted)
                        || ReferenceEquals(child, protectedPlaying)
                        || protectedPlaybackItems.Contains(child))
                    {
                        continue;
                    }

                    if (child.CoverBitmap != null && child.CoverBitmap != defaultCover)
                        child.CoverBitmap = defaultCover;

                    child.IsLoadingCover = false;
                }

                if (!keepAllCovers)
                {
                    folder.IsLoadingCover = false;
                    SyncAlbumFolderCoverFromChildren(folder);
                    folder.RebuildPreviewItems(rebuildStructure: false);

                    if (folder.Children.Take(FolderPreviewCoverCount).Any(NeedsCoverLoad))
                        QueueAlbumPreviewCoverLoad(folder);
                }
            }
        }

        private HashSet<MediaItem> GetPlaybackItemsToKeepResident()
        {
            var keep = new HashSet<MediaItem>(ReferenceEqualityComparer.Instance);
            if (PlaybackQueue.Count == 0)
                return keep;

            var currentItem = AudioPlayer?.CurrentMediaItem ?? SelectedMediaItem;
            var currentIndex = currentItem != null ? PlaybackQueue.IndexOf(currentItem) : -1;
            if (currentIndex < 0)
                currentIndex = SelectedMediaItem != null ? PlaybackQueue.IndexOf(SelectedMediaItem) : -1;

            if (currentIndex < 0)
                currentIndex = 0;

            int start = Math.Max(0, currentIndex - 1);
            int end = Math.Min(PlaybackQueue.Count - 1, currentIndex + 2);
            for (int i = start; i <= end; i++)
            {
                keep.Add(PlaybackQueue[i]);
            }

            return keep;
        }

        private void EnsureCurrentMediaCoverIsLoaded()
        {
            EnsureMediaItemCoverIsLoaded(AudioPlayer?.CurrentMediaItem);
        }

        private void EnsureMediaItemCoverIsLoaded(MediaItem? item)
        {
            if (item == null || !NeedsCoverLoad(item))
                return;

            var containingAlbum = AlbumList.FirstOrDefault(folder => folder.Children.Contains(item));
            if (containingAlbum != null)
                QueueAlbumCoverLoad(containingAlbum);
        }

        private bool NeedsCoverLoad(MediaItem item)
        {
            if (item.MetadataProcessed) return false;
            if (item.CoverBitmap == null) return true;
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            return (item.CoverBitmap == DefaultFolderCover && !item.CoverFound)
                || string.IsNullOrWhiteSpace(item.Title);
        }

        private bool NeedsVisibleCoverLoad(MediaItem item)
        {
            if (item.CoverBitmap == null) return true;
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            return item.CoverBitmap == DefaultFolderCover;
        }

        private List<MediaItem> GetAlbumCoverLoadBatch(IReadOnlyList<MediaItem> items, int maxItemsToLoad)
        {
            var candidates = items.Where(NeedsCoverLoad).ToList();
            if (maxItemsToLoad >= candidates.Count)
                return candidates;

            return candidates.Take(maxItemsToLoad).ToList();
        }

        private async Task LoadAlbumCoversForItemsAsync(FolderMediaItem folder, IReadOnlyList<MediaItem> itemsToLoad, bool forceUpdate)
        {
            if (AudioPlayer == null || itemsToLoad.Count == 0)
                return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                DefaultFolderCover ??= GenerateDefaultFolderCover();
                foreach (var child in itemsToLoad)
                {
                    if (NeedsVisibleCoverLoad(child))
                    {
                        child.CoverBitmap ??= DefaultFolderCover;
                        child.IsLoadingCover = true;
                    }
                    else
                    {
                        child.IsLoadingCover = false;
                    }
                }

                folder.IsLoadingCover = itemsToLoad.Any(NeedsVisibleCoverLoad);
            });

            var fastThumbCandidates = itemsToLoad
                .Where(item => !string.IsNullOrWhiteSpace(item.FileName)
                               && !string.IsNullOrWhiteSpace(YouTubeThumbnail.ExtractVideoId(item.FileName)))
                .ToList();

            if (AllowOnlineCoverLookup)
            {
                foreach (var item in fastThumbCandidates)
                    _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
            }

            var orderedItems = new AvaloniaList<MediaItem>(itemsToLoad);
            var allowOnlineForBatch = orderedItems.Any(IsOnlineMediaItem) || AllowOnlineCoverLookup;
            _ = new MetadataScrapper(
                orderedItems,
                AudioPlayer!,
                DefaultFolderCover,
                agentInfo,
                maxThumbnailWidth: 512,
                maxCacheEntries: MetadataScrapperCacheEntries,
                forceUpdate: forceUpdate,
                allowOnlineLookup: allowOnlineForBatch);

            await WaitForAlbumCoversAsync(orderedItems);

            if (AllowOnlineCoverLookup)
            {
                var unresolvedFastThumbCandidates = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    itemsToLoad
                        .Where(item => !string.IsNullOrWhiteSpace(item.FileName)
                                       && !string.IsNullOrWhiteSpace(YouTubeThumbnail.ExtractVideoId(item.FileName))
                                       && (item.CoverBitmap == null || item.CoverBitmap == DefaultFolderCover))
                        .ToList());

                foreach (var item in unresolvedFastThumbCandidates)
                    _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
            }
        }

        private async Task LoadAlbumCoversAsync(FolderMediaItem folder, string agentInfo, bool forceUpdate, int maxItemsToLoad = int.MaxValue)
        {
            if (AudioPlayer == null) return;

            var albumLoadState = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var snapshot = folder.Children.ToList();
                var itemsToLoad = GetAlbumCoverLoadBatch(snapshot, maxItemsToLoad);
                return (Snapshot: snapshot, ItemsToLoad: itemsToLoad);
            });

            var itemsToLoad = albumLoadState.ItemsToLoad;

            if (itemsToLoad.Count == 0)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    folder.IsLoadingCover = false;
                });
                return;
            }

            await LoadAlbumCoversForItemsAsync(folder, itemsToLoad, forceUpdate);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                folder.IsLoadingCover = false;
            });

            QueueOpenedAlbumStreamDurationScan(folder);
        }

        private async Task LoadPriorityAlbumCoversAsync(FolderMediaItem folder, string agentInfo, bool forceUpdate, CancellationToken ct)
        {
            if (AudioPlayer == null || ct.IsCancellationRequested)
                return;

            var priorityItems = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var snapshot = folder.Children.ToList();
                if (snapshot.Count == 0)
                    return new List<MediaItem>();

                var preferred = new List<MediaItem>();

                if (ReferenceEquals(folder, LoadedAlbum))
                {
                    var selectedIndex = Math.Clamp(GetRoundedSelectedIndex(SelectedIndex), 0, Math.Max(0, snapshot.Count - 1));
                    for (int i = selectedIndex; i < Math.Min(snapshot.Count, selectedIndex + 8); i++)
                        preferred.Add(snapshot[i]);
                }

                for (int i = 0; i < Math.Min(snapshot.Count, 8); i++)
                {
                    var item = snapshot[i];
                    if (!preferred.Contains(item))
                        preferred.Add(item);
                }

                var currentItem = AudioPlayer?.CurrentMediaItem;
                if (currentItem != null && folder.Children.Contains(currentItem) && !preferred.Contains(currentItem))
                    preferred.Insert(0, currentItem);

                return preferred.Where(NeedsCoverLoad).ToList();
            });

            if (priorityItems.Count == 0 || ct.IsCancellationRequested)
                return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                DefaultFolderCover ??= GenerateDefaultFolderCover();
                foreach (var child in priorityItems)
                {
                    if (NeedsVisibleCoverLoad(child))
                    {
                        if (child.CoverBitmap == null)
                            child.CoverBitmap = DefaultFolderCover;
                        child.IsLoadingCover = true;
                    }
                    else
                    {
                        child.IsLoadingCover = false;
                    }
                }

                folder.IsLoadingCover = priorityItems.Any(NeedsVisibleCoverLoad);
            });

            var orderedItems = new AvaloniaList<MediaItem>(priorityItems);
            var allowOnlineForBatch = orderedItems.Any(IsOnlineMediaItem) || AllowOnlineCoverLookup;
            _ = new MetadataScrapper(
                orderedItems,
                AudioPlayer!,
                DefaultFolderCover,
                agentInfo,
                maxThumbnailWidth: 512,
                maxCacheEntries: MetadataScrapperCacheEntries,
                forceUpdate: forceUpdate,
                allowOnlineLookup: allowOnlineForBatch);

            await WaitForAlbumCoversAsync(orderedItems);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                folder.IsLoadingCover = priorityItems.Any(child =>
                    NeedsVisibleCoverLoad(child) && child.IsLoadingCover);
            });
        }

        private async Task WaitForAlbumCoversAsync(IReadOnlyList<MediaItem> items)
        {
            if (items.Count == 0) return;

            // Allow the scrapper to flip IsLoadingCover before we start polling.
            await Task.Delay(50);

            // Wait until no items are marked as loading anymore.
            // Items that fail or are skipped must have their IsLoadingCover set to false
            // by the scrapper or the load logic to ensure we don't hang here.
            while (await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                       items.Any(i => i.IsLoadingCover)))
            {
                await Task.Delay(150);
            }
        }

        private void QueueOpenedAlbumStreamDurationScan(FolderMediaItem folder)
        {
            _ = Task.Run(async () =>
            {
                var streamItems = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    folder.Children
                        .Where(item => !string.IsNullOrWhiteSpace(item.FileName)
                                       && (item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                           || item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase))
                                       && item.Duration <= 0)
                        .ToList());

                await PopulateMissingStreamMetadataAsync(streamItems);
            });
        }
    }
}
