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
        private void AlbumList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var folder in _subscribedFolders.ToArray())
                    UnsubscribeFolder(folder);

                foreach (var folder in AlbumList)
                    SubscribeFolder(folder);
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (var folder in e.OldItems.OfType<FolderMediaItem>())
                        UnsubscribeFolder(folder);
                }

                if (e.NewItems != null)
                {
                    foreach (var folder in e.NewItems.OfType<FolderMediaItem>())
                        SubscribeFolder(folder);
                }
            }

            if (!_isApplyingDeferredAlbumList)
                ApplyAlbumFilter();

            RefreshAlbumListState();
        }

        private void SubscribeFolder(FolderMediaItem folder)
        {
            if (_subscribedFolders.Add(folder))
                folder.PropertyChanged += Folder_PropertyChanged;

            AttachFolderChildren(folder, folder.Children);
        }

        private void UnsubscribeFolder(FolderMediaItem folder)
        {
            if (_subscribedFolders.Remove(folder))
                folder.PropertyChanged -= Folder_PropertyChanged;

            if (_folderChildrenCollections.Remove(folder, out var children))
            {
                children.CollectionChanged -= FolderChildren_CollectionChanged;
                foreach (var child in children)
                    UnsubscribeAlbumChild(child);
            }
        }

        private void AttachFolderChildren(FolderMediaItem folder, AvaloniaList<MediaItem> children)
        {
            if (_folderChildrenCollections.TryGetValue(folder, out var existingChildren))
            {
                if (ReferenceEquals(existingChildren, children))
                    return;

                existingChildren.CollectionChanged -= FolderChildren_CollectionChanged;
                foreach (var child in existingChildren)
                    UnsubscribeAlbumChild(child);
            }

            _folderChildrenCollections[folder] = children;
            children.CollectionChanged += FolderChildren_CollectionChanged;
            foreach (var child in children)
                SubscribeAlbumChild(child);
        }

        private void SubscribeAlbumChild(MediaItem child)
        {
            if (_subscribedAlbumChildren.Add(child))
                child.PropertyChanged += AlbumChild_PropertyChanged;
        }

        private void UnsubscribeAlbumChild(MediaItem child)
        {
            if (_subscribedAlbumChildren.Remove(child))
                child.PropertyChanged -= AlbumChild_PropertyChanged;
        }

        private static bool IsSearchRelevantProperty(string? propertyName) =>
            string.IsNullOrEmpty(propertyName) ||
            propertyName == nameof(MediaItem.Title) ||
            propertyName == nameof(MediaItem.Artist) ||
            propertyName == nameof(MediaItem.Album);

        private void RefreshAlbumFilterIfNeeded()
        {
            if (_isApplyingDeferredAlbumList)
                return;

            if (!string.IsNullOrWhiteSpace(SearchAlbumText) || !ReferenceEquals(FilteredAlbumList, AlbumList))
                ApplyAlbumFilter();
        }

        private void RefreshTrackFilterIfNeeded()
        {
            if (_isApplyingDeferredAlbumList)
                return;

            if (LoadedAlbum == null)
                return;

            if (!string.IsNullOrWhiteSpace(SearchText) || !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                ApplyFilter();
        }

        private void FolderChildren_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (sender is not AvaloniaList<MediaItem> children)
                return;

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var folder in _subscribedFolders.ToArray())
                    AttachFolderChildren(folder, folder.Children);
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (var child in e.OldItems.OfType<MediaItem>())
                        UnsubscribeAlbumChild(child);
                }

                if (e.NewItems != null)
                {
                    foreach (var child in e.NewItems.OfType<MediaItem>())
                        SubscribeAlbumChild(child);
                }
            }

            RefreshAlbumFilterIfNeeded();
            if (ReferenceEquals(children, LoadedAlbum?.Children))
                RefreshTrackFilterIfNeeded();
        }

        private void AlbumChild_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MediaItem item || !IsSearchRelevantProperty(e.PropertyName))
                return;

            RefreshAlbumFilterIfNeeded();
            if (LoadedAlbum?.Children.Contains(item) == true)
                RefreshTrackFilterIfNeeded();
        }

        private void ApplyAlbumFilter()
        {
            var query = SearchAlbumText?.Trim();
            var previousSelection = SelectedAlbum;

            if (string.IsNullOrWhiteSpace(query))
            {
                FilteredAlbumList = AlbumList;
            }
            else
            {
                var filtered = AlbumList.Where(a =>
                    (a.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    // Children is never null (FolderMediaItem initializes it), so skip null check
                    a.Children.Any(c =>
                         (c.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    ).ToList();
                FilteredAlbumList = new AvaloniaList<FolderMediaItem>(filtered);
            }

            if (previousSelection != null && FilteredAlbumList.Contains(previousSelection))
                SelectedAlbum = previousSelection;
            else if (FilteredAlbumList.Count == 0)
                SelectedAlbum = null;

            SyncSelectedAlbumIndexFromAlbum(SelectedAlbum);
        }

        private void SyncSelectedAlbumIndexFromAlbum(FolderMediaItem? album)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextIndex = album == null ? -1 : FilteredAlbumList.IndexOf(album);
            if (SelectedAlbumIndex == nextIndex)
                return;

            try
            {
                _isSyncingAlbumSelection = true;
                SelectedAlbumIndex = nextIndex;
            }
            finally
            {
                _isSyncingAlbumSelection = false;
            }
        }

        private void SortAlbums(bool alphabeticalAscending)
        {
            if (AlbumList.Count < 2)
                return;

            var comparer = StringComparer.OrdinalIgnoreCase;
            var orderedAlbums = alphabeticalAscending
                ? AlbumList
                    .OrderBy(GetAlbumSortKey, comparer)
                    .ThenBy(album => album.FileName ?? string.Empty, comparer)
                    .ToList()
                : AlbumList
                    .OrderByDescending(GetAlbumSortKey, comparer)
                    .ThenByDescending(album => album.FileName ?? string.Empty, comparer)
                    .ToList();

            if (AlbumList.SequenceEqual(orderedAlbums))
                return;

            AlbumList = new AvaloniaList<FolderMediaItem>(orderedAlbums);
        }

        private static string GetAlbumSortKey(FolderMediaItem album)
        {
            if (!string.IsNullOrWhiteSpace(album.Title))
                return album.Title.Trim();

            if (!string.IsNullOrWhiteSpace(album.FileName))
                return Path.GetFileName(album.FileName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.Empty;
        }

        private void Folder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not FolderMediaItem folder)
                return;

            if (e.PropertyName == nameof(MediaItem.Title))
            {
                if (folder.IsRenaming)
                    ValidateFolderTitle(folder);

                RefreshAlbumFilterIfNeeded();
            }
            else if (e.PropertyName == nameof(FolderMediaItem.Children))
            {
                AttachFolderChildren(folder, folder.Children);
                RefreshAlbumFilterIfNeeded();
                if (ReferenceEquals(folder, LoadedAlbum))
                    RefreshTrackFilterIfNeeded();
            }
        }

        private void ValidateFolderTitle(FolderMediaItem folder)
        {
            if (string.IsNullOrWhiteSpace(folder.Title))
            {
                folder.IsNameInvalid = true;
                folder.NameInvalidMessage = "Title cannot be empty.";
                return;
            }
            var duplicate = AlbumList.Any(a => a != folder && string.Equals(a.Title, folder.Title, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                folder.IsNameInvalid = true;
                folder.NameInvalidMessage = $"Album name '{folder.Title}' already exists.";
            }
            else
            {
                folder.IsNameInvalid = false;
                folder.NameInvalidMessage = null;
            }
        }

        private bool IsMediaDuplicate(string path, out MediaItem? existing)
        {
            existing = null;
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool isYt = path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || 
                        path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
            var id = isYt ? YouTubeThumbnail.ExtractVideoId(path) : null;

            bool Matches(MediaItem m)
            {
                if (string.IsNullOrEmpty(m.FileName)) return false;
                if (m.FileName == path) return true;
                if (id != null)
                {
                    bool itemIsYt = m.FileName.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || 
                                   m.FileName.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
                    if (itemIsYt)
                        return YouTubeThumbnail.ExtractVideoId(m.FileName) == id;
                }
                return false;
            }

            // If we have a LoadedAlbum (specific album selected), only check that album
            if (LoadedAlbum != null)
            {
                existing = LoadedAlbum.Children.FirstOrDefault(Matches);
                return existing != null;
            }

            // Otherwise check the general/global list
            existing = CoverItems.FirstOrDefault(Matches);
            return existing != null;
        }

        private static bool IsOnlineMediaItem(MediaItem item)
        {
            var fileName = item.FileName;
            return !string.IsNullOrWhiteSpace(fileName) &&
                   (fileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("http", StringComparison.OrdinalIgnoreCase));
        }

        private bool ShouldScanMetadataForItem(MediaItem item)
        {
            if (IsOnlineMediaItem(item))
                return true;

            return ShouldScanLocalMediaMetadata;
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
                OnPropertyChanged(nameof(IsTagIconDimmed));
        }

        

        private void StartMetadataScrappersForLoadedFolders(bool forceUpdate = false)
        {
            if (AudioPlayer == null || AlbumList.Count == 0 || IsAddingPlaylist) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            IsAddingPlaylist = true;
            Log.Info(
                $"StartMetadataScrappersForLoadedFolders queued. ForceUpdate={forceUpdate}, " +
                $"Albums={AlbumList.Count}, Items={GetAlbumItemCount()}, LoadedAlbum='{LoadedAlbum?.Title ?? "<none>"}'.");

            _ = Task.Run(async () =>
            {
                try
                {
                    var albums = AlbumList.ToList();
                    var loadedAlbum = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LoadedAlbum);
                    foreach (var folder in albums)
                    {
                        if (folder == null || folder.Children.Count == 0) continue;

                        int maxItemsToLoad = ReferenceEquals(folder, loadedAlbum) ? int.MaxValue : FolderPreviewCoverCount;
                        await LoadAlbumCoversAsync(folder, agentInfo, forceUpdate, maxItemsToLoad);
                    }
                }
                finally
                {
                    Log.Info(
                        $"StartMetadataScrappersForLoadedFolders finished queueing. ForceUpdate={forceUpdate}, " +
                        $"Albums={AlbumList.Count}, Items={GetAlbumItemCount()}.");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsAddingPlaylist = false;
                    });
                }
            });
        }

        private void QueueAlbumCoverLoad(FolderMediaItem folder, bool forceUpdate = false, int maxItemsToLoad = int.MaxValue)
        {
            if (AudioPlayer == null || folder.Children.Count == 0) return;
            if (!TryBeginAlbumCoverLoad(folder)) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadAlbumCoversAsync(folder, agentInfo, forceUpdate, maxItemsToLoad);
                }
                finally
                {
                    EndAlbumCoverLoad(folder);
                }
            });
        }

    }
}
