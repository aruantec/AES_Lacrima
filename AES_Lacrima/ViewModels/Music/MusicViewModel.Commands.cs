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
        #region Commands
        // Commands
        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private void AddUrl()
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;
            if (MetadataService != null && MetadataService.IsMetadataLoaded) 
                MetadataService.IsMetadataLoaded = false;

            AddUrlText = string.Empty;
            IsAddUrlPopupOpen = true;
        }

        [RelayCommand]
        private void SubmitAddUrl() => IsAddUrlPopupOpen = false;

        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private void AddPlaylist()
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;
            if (MetadataService != null && MetadataService.IsMetadataLoaded) 
                MetadataService.IsMetadataLoaded = false;

            AddPlaylistText = string.Empty;
            IsAddPlaylistPopupOpen = true;
        }

        [RelayCommand]
        private void SubmitAddPlaylist() => IsAddPlaylistPopupOpen = false;

        [RelayCommand(CanExecute = nameof(CanDeletePointedItem))]
        private void DeletePointedItem()
        {
            var itemToDelete = PointedIndex != -1 && CoverItems.Count > PointedIndex 
                ? CoverItems[PointedIndex] 
                : HighlightedItem;

            if (itemToDelete == null) return;
            if (itemToDelete == SelectedMediaItem)
            {
                AudioPlayer?.Stop();
                AudioPlayer?.ClearMedia();
                SelectedMediaItem = null;
            }
            CoverItems.Remove(itemToDelete);
            if (CoverItems.Count > 0)
            {
                var newIndex = Math.Max(0, PointedIndex - 1);
                HighlightedItem = CoverItems[newIndex];
                SelectedIndex = newIndex;
            }
            else
            {
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                SelectedIndex = -1;
            }
        }

        [RelayCommand(CanExecute = nameof(CanClearLoadedAlbum))]
        private void ClearAlbum()
        {
            if (LoadedAlbum == null)
                return;

            if (SelectedMediaItem != null && LoadedAlbum.Children.Contains(SelectedMediaItem))
            {
                AudioPlayer?.Stop();
                AudioPlayer?.ClearMedia();
                SelectedMediaItem = null;
                PlaybackQueue = new AvaloniaList<MediaItem>();
            }

            LoadedAlbum.Children.Clear();
            PointedIndex = -1;
            SelectedIndex = -1;
            HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
            ApplyFilter();
            SaveSettings();
        }

        [RelayCommand]
        private void CloseVideoViewport()
        {
            if (!IsVideoMode)
                return;

            IsVideoViewportDismissed = true;
        }

        [RelayCommand]
        private void ToggleVideoViewport()
        {
            if (!IsVideoMode || AudioPlayer?.CurrentMediaItem == null)
                return;

            IsVideoViewportDismissed = !IsVideoViewportDismissed;
        }

        [RelayCommand]
        private void ToggleFullscreen()
        {
            if (!IsFullscreen)
            {
                IsFullscreen = true;
                IsVideoExpanded = true;
            }
            else
            {
                IsVideoExpanded = !IsVideoExpanded;
            }
        }

        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private async Task AddItems()
        {
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var supportedTypes = SupportedTypes.ToArray();
                var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = FilePickerTitle,
                    AllowMultiple = true,
                    FileTypeFilter = new[] 
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType(FilePickerTypeName)
                        {
                            Patterns = supportedTypes
                        }
                    }
                });

                if (files.Count > 0)
                {
                    var newMediaItems = new AvaloniaList<MediaItem>();
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        if (IsMediaDuplicate(localPath, out _)) continue;

                        var item = new MediaItem
                        {
                            FileName = localPath,
                            Title = Path.GetFileName(localPath),
                            CoverBitmap = DefaultFolderCover
                        };
                        newMediaItems.Add(item);
                        CoverItems.Add(item);

                        if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                        {
                            if (!LoadedAlbum.Children.Any(c => c.FileName == item.FileName))
                                LoadedAlbum.Children.Add(item);
                        }
                    }

                    if (newMediaItems.Count > 0)
                    {
                        var scanCandidates = new AvaloniaList<MediaItem>(newMediaItems.Where(ShouldScanMetadataForItem));
                        if (scanCandidates.Count > 0)
                        {
                            var allowOnlineForBatch = scanCandidates.Any(IsOnlineMediaItem) || AllowOnlineCoverLookup;
                            _ = new MetadataScrapper(scanCandidates, AudioPlayer!, DefaultFolderCover, agentInfo, 512, allowOnlineLookup: allowOnlineForBatch);
                        }
                    }
                }
            }
        }

        [RelayCommand]
        private void CreateAlbum()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var baseName = "New Album";
            var uniqueName = baseName;
            int counter = 1;
            while (AlbumList.Any(a => string.Equals(a.Title, uniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                uniqueName = $"{baseName} ({counter++})";
            }

            var newAlbum = new FolderMediaItem
            {
                Title = uniqueName,
                Children = new AvaloniaList<MediaItem>(),
                CoverBitmap = DefaultFolderCover
            };
            AlbumList.Add(newAlbum);
            SelectedAlbum = newAlbum;
            OpenSelectedFolder();
            RenameFolder(newAlbum);
        }

        [RelayCommand(CanExecute = nameof(CanSortAlbums))]
        private void SortAlbumsAscending() => SortAlbums(alphabeticalAscending: true);

        [RelayCommand(CanExecute = nameof(CanSortAlbums))]
        private void SortAlbumsDescending() => SortAlbums(alphabeticalAscending: false);

        [RelayCommand]
        private void RenameFolder(FolderMediaItem? folder)
        {
            if (folder == null) return;
            foreach (var album in AlbumList)
            {
                if (album.IsRenaming && album.IsNameInvalid)
                    album.Title = _originalFolderTitle;
                album.IsRenaming = false;
            }

            _originalFolderTitle = folder.Title;
            folder.IsNameInvalid = false;
            folder.NameInvalidMessage = null;
            folder.IsRenaming = true;
        }

        [RelayCommand]
        private void EndRename(FolderMediaItem? folder)
        {
            if (folder != null)
            {
                ValidateFolderTitle(folder);
                if (folder.IsNameInvalid) return;
                folder.IsRenaming = false;
                _originalFolderTitle = null;
            }
        }

        [RelayCommand]
        private void CancelRename(FolderMediaItem? folder)
        {
            if (folder != null)
            {
                folder.Title = _originalFolderTitle;
                folder.IsNameInvalid = false;
                folder.NameInvalidMessage = null;
                folder.IsRenaming = false;
                _originalFolderTitle = null;
            }
        }

        [RelayCommand]
        private void DeleteFolder(FolderMediaItem? folder)
        {
            var target = folder ?? PointedFolder;
            if (target != null)
            {
                // If a song from this album is currently playing, stop the player
                if (AudioPlayer?.CurrentMediaItem != null && target.Children.Contains(AudioPlayer.CurrentMediaItem))
                {
                    AudioPlayer.Stop();
                }

                AlbumList.Remove(target);
                if (target == PointedFolder)
                {
                    PointedFolder = null;
                }

                // If the deleted album was the one loaded in the view, clear the view
                if (target == LoadedAlbum)
                {
                    LoadedAlbum = null;
                    IsNoAlbumLoadedVisible = true;
                    ApplyFilter();
                }
            }
        }

        [RelayCommand]
        private async Task OpenMetadata(object? parameter)
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;

            MediaItem? target;
            if (parameter is MediaItem mi) target = mi;
            else if (parameter is int index && index >= 0 && index < CoverItems.Count) target = CoverItems[index];
            else target = SelectedMediaItem ?? HighlightedItem ?? AudioPlayer?.CurrentMediaItem;

            if (target == null || MetadataService == null) return;

            if (MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;

            await MetadataService.LoadMetadataAsync(target);
        }

        [RelayCommand]
        private async Task ReloadMetadata(object? parameter)
        {
            MediaItem? target;
            if (parameter is MediaItem mi) target = mi;
            else if (parameter is int index && index >= 0 && index < CoverItems.Count) target = CoverItems[index];
            else target = SelectedMediaItem ?? HighlightedItem ?? AudioPlayer?.CurrentMediaItem;

            if (target == null || AudioPlayer == null) return;

            if (!ShouldScanMetadataForItem(target))
                return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();

            // Use the scrapper to force a reload, which will bypass the cache and update the item
            var allowOnlineForTarget = IsOnlineMediaItem(target) || AllowOnlineCoverLookup;
            var scrapper = new MetadataScrapper(new AvaloniaList<MediaItem>(), AudioPlayer, DefaultFolderCover, agentInfo, 512, allowOnlineLookup: allowOnlineForTarget);
            await scrapper.EnqueueLoadForPublic(target);
        }

        [RelayCommand]
        private void ToggleEqualizer()
        {
            if (MetadataService != null && MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;
            IsEqualizerVisible = !IsEqualizerVisible;
        }

        [RelayCommand]
        private void SetPosition(double position)
        {
            AudioPlayer?.SetPosition(position);
        }

        [RelayCommand]
        private void ToggleAlbumlist() => IsAlbumlistOpen = !IsAlbumlistOpen;

        [RelayCommand]
        private void Stop() => AudioPlayer?.Stop();

        [RelayCommand]
        private async Task PlayNext()
        {
            if (!GetCurrentIndex(out int currentIndex)) return;
            currentIndex++;
            if (currentIndex > PlaybackQueue.Count - 1)
            {
                if (AudioPlayer?.RepeatMode == RepeatMode.All)
                    currentIndex = 0;
                else
                    return;
            }
            await PlayIndexSelection(currentIndex);
        }

        [RelayCommand]
        private async Task PlayPrevious()
        {
            if (!GetCurrentIndex(out int currentIndex)) return;
            currentIndex--;
            if (currentIndex < 0) return;
            await PlayIndexSelection(currentIndex);
        }

        [RelayCommand]
        private void OpenSelectedFolder()
        {
            if (IsVideoMode)
                IsVideoViewportDismissed = true;

            var selectedAlbum = SelectedAlbum;
            if (selectedAlbum != null && SettingsViewModel?.SortAlbumsByTrackNameInMiniView == true)
            {
                var sorted = selectedAlbum.Children.OrderBy(item => item.Track).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
                if (!selectedAlbum.Children.SequenceEqual(sorted))
                {
                    selectedAlbum.Children.Clear();
                    foreach (var item in sorted)
                        selectedAlbum.Children.Add(item);
                }
            }

            var isSameAlbum = selectedAlbum != null && ReferenceEquals(LoadedAlbum, selectedAlbum);
            if (!isSameAlbum && ResetPlaybackOnAlbumSwitch)
                ResetPlaybackStateForAlbumSwitch();

            _scanMissingStreamDurationsOnLoadedAlbum = true;
            LoadedAlbum = selectedAlbum;

            if (selectedAlbum != null)
            {
                // Reset the processed flag for the new album so we can re-evaluate its items.
                // This ensures that if we switch back and forth, the scrapper will check
                // for missing metadata again if it wasn't successfully retrieved before.
                foreach (var child in selectedAlbum.Children)
                {
                    child.MetadataProcessed = false;
                }
            }

            if (isSameAlbum && selectedAlbum != null)
            {
                QueueLoadedAlbumCoverLoad(selectedAlbum);
                QueueOpenedAlbumStreamDurationScan(selectedAlbum);
                _scanMissingStreamDurationsOnLoadedAlbum = false;
            }
        }

        private void ResetPlaybackStateForAlbumSwitch()
        {
            AudioPlayer?.Stop();
            AudioPlayer?.ClearMedia();
            _pendingTrackLoadItem = null;
            IsTrackLoadPending = false;
            SelectedMediaItem = null;
            PlaybackQueue = new AvaloniaList<MediaItem>();
            PointedIndex = -1;
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void ClearSearchAlbum() => SearchAlbumText = string.Empty;

        [RelayCommand]
        private async Task OpenSelectedItem(int index)
        {
            if (CoverItems.Count > index && CoverItems[index] is { } selectedItem)
            {
                PlaybackQueue = CoverItems;
                SelectedMediaItem = selectedItem;
                await PlayMediaItemAsync(selectedItem);
            }
        }

        [RelayCommand]
        private void Drop(FolderMediaItem item)
        {
        }

        [RelayCommand]
        private async Task TogglePlay()
        {
            if (AudioPlayer == null || AudioPlayer.IsLoadingMedia) return;

            if (AudioPlayer.IsPlaying)
            {
                AudioPlayer.Pause();
            }
            else
            {
                // nothing loaded? just make sure player is stopped and bail out.
                if (SelectedMediaItem == null)
                {
                    AudioPlayer.Stop();
                    return;
                }

                // If we have an item but no duration yet (i.e. never played), load it.
                if (AudioPlayer.Duration <= 0)
                {
                    if (PlaybackQueue.Count == 0) PlaybackQueue = CoverItems;
                    await PlayMediaItemAsync(SelectedMediaItem);
                }
                else
                {
                    AudioPlayer.Play();
                }
            }
        }

        [RelayCommand]
        private void ToggleRepeat()
        {
            if (AudioPlayer == null) return;
            AudioPlayer.RepeatMode = AudioPlayer.RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                RepeatMode.One => RepeatMode.Off,
                _ => RepeatMode.Off
            };
        }

        [RelayCommand]
        private async Task OpenFolder()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select folder",
                    AllowMultiple = false,
                });
                if (folders.Count > 0)
                {
                    var path = folders[0].Path.LocalPath;
                    var existing = AlbumList.FirstOrDefault(a => a.FileName == path);
                    if (existing != null)
                    {
                        if (Directory.Exists(path))
                        {
                            var mediaItems = LoadMediaItemsWithTrackOrder(path).ToList();

                            var addedItems = new AvaloniaList<MediaItem>();
                            foreach (var item in mediaItems)
                            {
                                if (!existing.Children.Any(c => c.FileName == item.FileName))
                                {
                                    existing.Children.Add(item);
                                    addedItems.Add(item);
                                }
                            }

                            if (addedItems.Count > 0)
                            {
                                QueueAlbumCoverLoad(existing);
                            }
                        }
                        SelectedAlbum = existing;
                        OpenSelectedFolder();
                        return;
                    }

                    var folderItem = new FolderMediaItem
                    {
                        FileName = path,
                        Title = GetUniqueAlbumName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                        CoverBitmap = DefaultFolderCover
                    };
                    if (Directory.Exists(path))
                    {
                        var mediaItems = LoadMediaItemsWithTrackOrder(path);
                        folderItem.Children.AddRange(mediaItems);
                    }
                    if (folderItem.Children.Count > 0)
                    {
                        AlbumList.Add(folderItem);
                        SelectedAlbum = folderItem;
                        OpenSelectedFolder();
                    }
                }
            }
        }

        [RelayCommand]
        private async Task ScanFolders()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Folder to Scan",
                    AllowMultiple = false,
                });

                if (folders.Count > 0)
                {
                    var rootPath = folders[0].Path.LocalPath;
                    if (Directory.Exists(rootPath))
                    {
                        var supportedTypes = SupportedTypes.ToArray();
                        await Task.Run(() => 
                        {
                            var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories).ToList();
                            directories.Insert(0, rootPath); // Include root folder

                            foreach (var dir in directories)
                            {
                                var mediaFiles = supportedTypes
                                    .SelectMany(pattern => Directory.EnumerateFiles(dir, pattern))
                                    .Where(file => 
                                    {
                                        var name = Path.GetFileName(file);
                                        return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                                    })
                                    .ToList();

                                if (mediaFiles.Count > 0)
                                {
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                                    {
                                        // Check if already in list
                                        if (AlbumList.Any(a => a.FileName == dir)) return;

                                        var folderItem = new FolderMediaItem
                                        {
                                            FileName = dir,
                                            Title = GetUniqueAlbumName(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                                            CoverBitmap = DefaultFolderCover
                                        };

                                        var mediaItems = mediaFiles.Select(file => new MediaItem
                                        {
                                            FileName = file,
                                            Title = Path.GetFileName(file),
                                            CoverBitmap = DefaultFolderCover
                                        }).ToList();

                                        folderItem.Children.AddRange(mediaItems);
                                        AlbumList.Add(folderItem);

                                        QueueAlbumCoverLoad(folderItem, maxItemsToLoad: FolderPreviewCoverCount);
                                    });
                                }
                            }
                        });
                    }
                }
            }
        }
        #endregion
    }
}
