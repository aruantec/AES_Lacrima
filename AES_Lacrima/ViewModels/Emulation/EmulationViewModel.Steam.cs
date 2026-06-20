using AES_Controls.Player.Models;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Steam;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel
    {
        private readonly object _steamSyncGate = new();
        private readonly List<FileSystemWatcher> _steamLibraryWatchers = [];
        private CancellationTokenSource? _steamLibrarySyncCts;
        private CancellationTokenSource? _steamLibraryWatcherDebounceCts;

        private static bool IsSteamAlbum(FolderMediaItem? album)
        {
            if (album == null)
                return false;

            if (EmulationConsoleCatalog.IsSteamSection(album.Title))
                return true;

            var persistenceKey = GetAlbumPersistenceKey(album);
            return EmulationConsoleCatalog.IsSteamSection(persistenceKey);
        }

        private async Task SyncSteamLibrariesAfterInitializeAsync()
        {
            if (!OperatingSystem.IsLinux())
                return;

            List<EmulationAlbumItem> steamAlbums;
            try
            {
                steamAlbums = await Dispatcher.UIThread.InvokeAsync(
                    () => AlbumList.OfType<EmulationAlbumItem>()
                        .Where(IsSteamAlbum)
                        .ToList(),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to collect Steam albums for library sync.", ex);
                return;
            }

            foreach (var album in steamAlbums)
                await SyncSteamLibraryAsync(album).ConfigureAwait(false);
        }

        private async Task SyncSteamLibraryAsync(EmulationAlbumItem album)
        {
            if (!OperatingSystem.IsLinux() || !IsSteamAlbum(album))
                return;

            IReadOnlyList<SteamInstalledGame> installedGames;
            try
            {
                installedGames = await Task.Run(SteamInstalledGameHelper.GetInstalledGames).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to enumerate installed Steam games.", ex);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsEmulatorRunning)
                    return;

                ApplySteamLibrarySnapshot(album, installedGames);
            }, DispatcherPriority.Background);
        }

        private void ApplySteamLibrarySnapshot(EmulationAlbumItem album, IReadOnlyList<SteamInstalledGame> installedGames)
        {
            lock (_steamSyncGate)
            {
                if (TryUpdateSteamLibrarySnapshotInPlace(album, installedGames))
                    return;

                var installedByPath = installedGames
                    .ToDictionary(game => game.GamePath, StringComparer.OrdinalIgnoreCase);

                var nextItems = new AvaloniaList<MediaItem>();
                bool anyCoverChanged = false;
                bool addedNewItems = false;

                foreach (var existing in album.Children)
                {
                    if (string.IsNullOrWhiteSpace(existing.FileName))
                        continue;

                    if (!installedByPath.TryGetValue(existing.FileName, out var game))
                        continue;

                    existing.Title = game.Name;
                    if (TryApplySteamIconCover(existing, album, game.IconPath))
                        anyCoverChanged = true;

                    nextItems.Add(existing);
                }

                var existingPaths = nextItems
                    .Select(item => item.FileName)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var game in installedGames)
                {
                    if (existingPaths.Contains(game.GamePath))
                        continue;

                    addedNewItems = true;
                    var created = CreateSteamGameItem(game, album);
                    if (created.CoverBitmap != null && !ReferenceEquals(created.CoverBitmap, album.CoverBitmap))
                        anyCoverChanged = true;

                    nextItems.Add(created);
                }

                album.Children = nextItems;

                SyncAlbumTotalChildCount(album);
                UpdatePreviewItems(album);
                QueueAlbumPreviewCoverLoad(album);

                if (ReferenceEquals(LoadedAlbum, album))
                {
                    ApplyFilter();
                    PrepareAlbumItemsForCoverDisplay(album);
                    if (album.Children.Count <= 1)
                    {
                        SelectedIndex = 0;
                        PointedIndex = -1;
                        CarouselSliderPreview = null;
                    }

                    if (addedNewItems || anyCoverChanged)
                        NotifyAlbumCoverDisplayChanged(album);

                    OnPropertyChanged(nameof(HasActiveAlbumItems));
                    OnPropertyChanged(nameof(ShowEmptyActiveAlbumHint));
                }
            }
        }

        /// <summary>
        /// Updates titles/covers without clearing the carousel when the installed game set is unchanged.
        /// </summary>
        private bool TryUpdateSteamLibrarySnapshotInPlace(
            EmulationAlbumItem album,
            IReadOnlyList<SteamInstalledGame> installedGames)
        {
            if (album.Children.Count != installedGames.Count)
                return false;

            var installedByPath = installedGames
                .ToDictionary(game => game.GamePath, StringComparer.OrdinalIgnoreCase);

            if (installedByPath.Count != installedGames.Count)
                return false;

            foreach (var existing in album.Children)
            {
                if (string.IsNullOrWhiteSpace(existing.FileName) ||
                    !installedByPath.ContainsKey(existing.FileName))
                {
                    return false;
                }
            }

            var anyCoverChanged = false;
            var anyNeedsCover = false;
            foreach (var existing in album.Children)
            {
                var game = installedByPath[existing.FileName!];
                existing.Title = game.Name;
                if (TryApplySteamIconCover(existing, album, game.IconPath))
                    anyCoverChanged = true;
                else if (NeedsPreviewCoverHydration(existing, album))
                    anyNeedsCover = true;
            }

            if (ReferenceEquals(LoadedAlbum, album))
            {
                if (anyCoverChanged)
                    NotifyAlbumCoverDisplayChanged(album);
                if (anyNeedsCover)
                    QueueAlbumPreviewCoverLoad(album);
            }

            return true;
        }

        public bool ShowSteamLibraryRefreshMenuItem =>
            OperatingSystem.IsLinux() && IsSteamAlbum(GetBrowseAlbum());

        private bool CanRefreshSteamLibrary() => ShowSteamLibraryRefreshMenuItem;

        [RelayCommand(CanExecute = nameof(CanRefreshSteamLibrary))]
        private async Task RefreshSteamLibraryAsync()
        {
            if (GetBrowseAlbum() is not EmulationAlbumItem album || !IsSteamAlbum(album))
                return;

            SLog.Info($"Manual Steam library refresh requested for album '{album.Title}'.");
            await SyncSteamLibraryAsync(album).ConfigureAwait(false);
        }

        private static MediaItem CreateSteamGameItem(SteamInstalledGame game, FolderMediaItem album)
        {
            var item = new MediaItem
            {
                FileName = game.GamePath,
                Title = game.Name,
                Album = album.Title,
                LocalCoverPath = game.IconPath,
                CoverBitmap = album.CoverBitmap
            };

            TryApplySteamIconCover(item, album, game.IconPath);
            return item;
        }

        private static bool TryApplySteamIconCover(MediaItem item, FolderMediaItem album, string? iconPath)
        {
            if (item.CoverBitmap != null && !ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
                return false;

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                return false;

            try
            {
                item.LocalCoverPath = iconPath;
                item.CoverBitmap = new Bitmap(iconPath);
                item.CoverFound = true;
                item.IsLoadingCover = false;
                return true;
            }
            catch (Exception ex)
            {
                SLog.Debug($"Failed to apply Steam icon cover from '{iconPath}'.", ex);
                return false;
            }
        }

        internal async Task<bool> TryLoadSteamGameCoverAsync(MediaItem item, CancellationToken cancellationToken = default)
        {
            if (!SteamInstalledGameHelper.IsSteamGamePath(item.FileName))
                return false;

            var iconPath = item.LocalCoverPath;
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                iconPath = SteamInstalledGameHelper.GetPreferredIconPath(item.FileName);

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            {
                await Dispatcher.UIThread.InvokeAsync(() => item.IsLoadingCover = false, DispatcherPriority.Normal);
                return false;
            }

            try
            {
                var bitmap = await Task.Run(() => new Bitmap(iconPath), cancellationToken).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.LocalCoverPath = iconPath;
                    item.CoverBitmap = bitmap;
                    item.CoverFound = true;
                    item.IsLoadingCover = false;
                }, DispatcherPriority.Normal);

                return true;
            }
            catch (Exception ex)
            {
                SLog.Debug($"Failed to load Steam cover from '{iconPath}'.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => item.IsLoadingCover = false, DispatcherPriority.Normal);
                return false;
            }
        }

        private void StartSteamLibraryWatcher(EmulationAlbumItem album)
        {
            if (!OperatingSystem.IsLinux() || !IsSteamAlbum(album))
                return;

            StopSteamLibraryWatcher();

            var watchPaths = SteamInstalledGameHelper.GetWatchPaths().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (watchPaths.Count == 0)
                return;

            _steamLibrarySyncCts = new CancellationTokenSource();
            var token = _steamLibrarySyncCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await SyncSteamLibraryAsync(album).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn("Steam library polling sync failed.", ex);
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }, token);

            try
            {
                foreach (var watchPath in watchPaths)
                {
                    var watcher = new FileSystemWatcher(watchPath)
                    {
                        Filter = "appmanifest_*.acf",
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += OnSteamLibraryChanged;
                    watcher.Changed += OnSteamLibraryChanged;
                    watcher.Deleted += OnSteamLibraryChanged;
                    watcher.Renamed += OnSteamLibraryRenamed;
                    _steamLibraryWatchers.Add(watcher);
                }

                foreach (var iconWatchPath in SteamInstalledGameHelper.GetIconWatchPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var watcher = new FileSystemWatcher(iconWatchPath)
                    {
                        Filter = "library_*.jpg",
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += OnSteamLibraryChanged;
                    watcher.Changed += OnSteamLibraryChanged;
                    watcher.Renamed += OnSteamLibraryRenamed;
                    _steamLibraryWatchers.Add(watcher);
                }

                foreach (var iconWatchPath in SteamInstalledGameHelper.GetIconWatchPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var watcher = new FileSystemWatcher(iconWatchPath)
                    {
                        Filter = "*_icon.jpg",
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };
                    watcher.Created += OnSteamLibraryChanged;
                    watcher.Changed += OnSteamLibraryChanged;
                    watcher.Renamed += OnSteamLibraryRenamed;
                    _steamLibraryWatchers.Add(watcher);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to start Steam library watcher; polling sync remains active.", ex);
            }
        }

        private void QueueSteamLibraryResync(EmulationAlbumItem album)
        {
            if (!OperatingSystem.IsLinux() || !IsSteamAlbum(album))
                return;

            try
            {
                _steamLibraryWatcherDebounceCts?.Cancel();
                _steamLibraryWatcherDebounceCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to reset Steam library watcher debounce.", ex);
            }

            _steamLibraryWatcherDebounceCts = new CancellationTokenSource();
            var token = _steamLibraryWatcherDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, token).ConfigureAwait(false);
                    await SyncSteamLibraryAsync(album).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (Exception ex)
                {
                    SLog.Warn("Debounced Steam library resync failed.", ex);
                }
            }, token);
        }

        private void StopSteamLibraryWatcher()
        {
            try
            {
                _steamLibrarySyncCts?.Cancel();
                _steamLibrarySyncCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to stop Steam library polling sync.", ex);
            }
            finally
            {
                _steamLibrarySyncCts = null;
            }

            try
            {
                _steamLibraryWatcherDebounceCts?.Cancel();
                _steamLibraryWatcherDebounceCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to stop Steam library watcher debounce.", ex);
            }
            finally
            {
                _steamLibraryWatcherDebounceCts = null;
            }

            if (_steamLibraryWatchers.Count == 0)
                return;

            foreach (var watcher in _steamLibraryWatchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnSteamLibraryChanged;
                    watcher.Changed -= OnSteamLibraryChanged;
                    watcher.Deleted -= OnSteamLibraryChanged;
                    watcher.Renamed -= OnSteamLibraryRenamed;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to dispose Steam library watcher.", ex);
                }
            }

            _steamLibraryWatchers.Clear();
        }

        private EmulationAlbumItem? _watchedSteamAlbum;

        private void OnSteamLibraryChanged(object sender, FileSystemEventArgs e)
        {
            if (_watchedSteamAlbum != null)
                QueueSteamLibraryResync(_watchedSteamAlbum);
        }

        private void OnSteamLibraryRenamed(object sender, RenamedEventArgs e)
        {
            if (_watchedSteamAlbum != null)
                QueueSteamLibraryResync(_watchedSteamAlbum);
        }

        private void ManageSteamLibraryWatcher(FolderMediaItem? album)
        {
            if (!OperatingSystem.IsLinux())
            {
                StopSteamLibraryWatcher();
                _watchedSteamAlbum = null;
                return;
            }

            if (album is EmulationAlbumItem steamAlbum && IsSteamAlbum(steamAlbum))
            {
                _watchedSteamAlbum = steamAlbum;
                StartSteamLibraryWatcher(steamAlbum);
                _ = SyncSteamLibraryAsync(steamAlbum);
                return;
            }

            StopSteamLibraryWatcher();
            _watchedSteamAlbum = null;
        }
    }
}
