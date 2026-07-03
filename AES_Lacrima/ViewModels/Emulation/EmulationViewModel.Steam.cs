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
        private readonly SemaphoreSlim _steamSyncSemaphore = new(1, 1);
        private CancellationTokenSource? _steamLibrarySyncCts;
        private CancellationTokenSource? _steamLibraryWatcherDebounceCts;
        private CancellationTokenSource? _steamEnterSyncDebounceCts;
        private CancellationTokenSource? _steamStartupSyncCts;
        private int _steamStartupSyncState;
        private readonly HashSet<EmulationAlbumItem> _steamAlbumsPendingPresentation = new();
        private EmulationAlbumItem? _pendingSteamSyncAlbum;
        private bool _pendingSteamSyncForcePresentation;
        private bool _pendingSteamSyncForceRefresh;

        private void ScheduleDeferredSteamStartupSync()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
                return;

            if (Volatile.Read(ref _steamStartupSyncState) == 2)
                return;

            if (Interlocked.CompareExchange(ref _steamStartupSyncState, 1, 0) != 0)
                return;

            _steamStartupSyncCts?.Cancel();
            _steamStartupSyncCts?.Dispose();
            _steamStartupSyncCts = new CancellationTokenSource();
            var token = _steamStartupSyncCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested && !IsActive)
                        await Task.Delay(250, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested)
                        return;

                    // Let the album row finish layout after navigation.
                    await Task.Delay(900, token).ConfigureAwait(false);
                    await SyncSteamLibrariesAfterInitializeAsync().ConfigureAwait(false);
                    Interlocked.Exchange(ref _steamStartupSyncState, 2);

                    await Dispatcher.UIThread.InvokeAsync(
                        FlushDeferredSteamPresentationUpdates,
                        DispatcherPriority.Background);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    SLog.Warn("Deferred Steam startup sync failed.", ex);
                    Interlocked.Exchange(ref _steamStartupSyncState, 0);
                }
            }, token);
        }

        private void FlushDeferredSteamPresentationUpdates()
        {
            if (!OperatingSystem.IsLinux())
                return;

            List<EmulationAlbumItem> pending;
            lock (_steamSyncGate)
            {
                if (_steamAlbumsPendingPresentation.Count == 0)
                    return;

                pending = _steamAlbumsPendingPresentation.ToList();
                _steamAlbumsPendingPresentation.Clear();
            }

            foreach (var album in pending)
                ApplySteamLibraryPresentationUpdates(album, addedNewItems: false);
        }

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

        private async Task SyncSteamLibraryAsync(
            EmulationAlbumItem album,
            bool forcePresentation = false,
            bool forceRefresh = false)
        {
            if (!OperatingSystem.IsLinux() || !IsSteamAlbum(album))
                return;

            if (!await _steamSyncSemaphore.WaitAsync(forceRefresh || forcePresentation ? Timeout.InfiniteTimeSpan : TimeSpan.Zero)
                    .ConfigureAwait(false))
            {
                lock (_steamSyncGate)
                {
                    _pendingSteamSyncAlbum = album;
                    _pendingSteamSyncForcePresentation |= forcePresentation;
                    _pendingSteamSyncForceRefresh |= forceRefresh;
                }

                return;
            }

            try
            {
                do
                {
                    if (forceRefresh)
                        SteamInstalledGameHelper.InvalidateInstalledGamesCache();

                    IReadOnlyList<SteamInstalledGame> installedGames;
                    try
                    {
                        installedGames = await Task.Run(SteamInstalledGameHelper.GetInstalledGames).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn("Failed to enumerate installed Steam games.", ex);
                        break;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (IsEmulatorRunning)
                            return;

                        ApplySteamLibrarySnapshot(album, installedGames, forcePresentation);
                    }, DispatcherPriority.Background);

                    EmulationAlbumItem? pendingAlbum = null;
                    bool pendingForcePresentation = false;
                    bool pendingForceRefresh = false;
                    lock (_steamSyncGate)
                    {
                        if (_pendingSteamSyncAlbum != null)
                        {
                            pendingAlbum = _pendingSteamSyncAlbum;
                            pendingForcePresentation = _pendingSteamSyncForcePresentation;
                            pendingForceRefresh = _pendingSteamSyncForceRefresh;
                            _pendingSteamSyncAlbum = null;
                            _pendingSteamSyncForcePresentation = false;
                            _pendingSteamSyncForceRefresh = false;
                        }
                    }

                    if (pendingAlbum == null)
                        break;

                    album = pendingAlbum;
                    forcePresentation |= pendingForcePresentation;
                    forceRefresh = pendingForceRefresh;
                }
                while (true);
            }
            finally
            {
                _steamSyncSemaphore.Release();
            }
        }

        private void ApplySteamLibrarySnapshot(
            EmulationAlbumItem album,
            IReadOnlyList<SteamInstalledGame> installedGames,
            bool forcePresentation = false)
        {
            var shouldPresent = forcePresentation || IsActive;

            lock (_steamSyncGate)
            {
                if (TryUpdateSteamLibrarySnapshotInPlace(album, installedGames, shouldPresent))
                    return;

                var installedByPath = installedGames
                    .ToDictionary(game => game.GamePath, StringComparer.OrdinalIgnoreCase);

                var nextItems = new AvaloniaList<MediaItem>();
                bool addedNewItems = false;

                foreach (var existing in album.Children)
                {
                    if (string.IsNullOrWhiteSpace(existing.FileName))
                        continue;

                    if (!installedByPath.TryGetValue(existing.FileName, out var game))
                        continue;

                    existing.Title = game.Name;
                    TryApplySteamIconCover(existing, album, game.IconPath);

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
                    nextItems.Add(CreateSteamGameItem(game, album));
                }

                album.Children = nextItems;
                SyncAlbumTotalChildCount(album);

                if (shouldPresent)
                    ApplySteamLibraryPresentationUpdates(album, addedNewItems);
                else
                    _steamAlbumsPendingPresentation.Add(album);
            }
        }

        private void ApplySteamLibraryPresentationUpdates(EmulationAlbumItem album, bool addedNewItems)
        {
            UpdatePreviewItems(album);
            QueueAlbumPreviewCoverLoad(album, forceRestart: addedNewItems);
            NotifyAlbumCoverDisplayChanged(album);

            if (!ReferenceEquals(LoadedAlbum, album))
                return;

            ApplyFilter();
            if (album.Children.Count <= 1)
            {
                SelectedIndex = 0;
                PointedIndex = -1;
                CarouselSliderPreview = null;
            }

            OnPropertyChanged(nameof(HasActiveAlbumItems));
            OnPropertyChanged(nameof(ShowEmptyActiveAlbumHint));
        }

        /// <summary>
        /// Updates titles/covers without clearing the carousel when the installed game set is unchanged.
        /// </summary>
        private bool TryUpdateSteamLibrarySnapshotInPlace(
            EmulationAlbumItem album,
            IReadOnlyList<SteamInstalledGame> installedGames,
            bool shouldPresent)
        {
            var installedByPath = installedGames
                .ToDictionary(game => game.GamePath, StringComparer.OrdinalIgnoreCase);

            if (installedByPath.Count != installedGames.Count)
                return false;

            var existingPaths = album.Children
                .Select(item => item.FileName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existingPaths.Count != album.Children.Count ||
                existingPaths.Count != installedGames.Count)
            {
                return false;
            }

            foreach (var existing in album.Children)
            {
                if (string.IsNullOrWhiteSpace(existing.FileName) ||
                    !installedByPath.ContainsKey(existing.FileName))
                {
                    return false;
                }
            }

            foreach (var game in installedGames)
            {
                if (!existingPaths.Contains(game.GamePath))
                    return false;
            }

            var anyNeedsCover = false;
            foreach (var existing in album.Children)
            {
                var game = installedByPath[existing.FileName!];
                existing.Title = game.Name;
                TryApplySteamIconCover(existing, album, game.IconPath);
                if (NeedsPreviewCoverHydration(existing, album))
                    anyNeedsCover = true;
            }

            if (anyNeedsCover)
            {
                if (shouldPresent)
                    QueueAlbumPreviewCoverLoad(album);
                else
                    _steamAlbumsPendingPresentation.Add(album);
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
            await SyncSteamLibraryAsync(album, forcePresentation: true, forceRefresh: true).ConfigureAwait(false);
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
                iconPath = SteamInstalledGameHelper.GetPreferredIconPath(item.FileName);

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                return false;

            if (string.Equals(item.LocalCoverPath, iconPath, StringComparison.OrdinalIgnoreCase) &&
                item.CoverFound)
            {
                item.IsLoadingCover = false;
                return false;
            }

            item.LocalCoverPath = iconPath;
            item.CoverFound = true;
            item.IsLoadingCover = false;
            return true;
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
                await Dispatcher.UIThread.InvokeAsync(() => item.IsLoadingCover = false, DispatcherPriority.Background);
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
                }, DispatcherPriority.Background);

                return true;
            }
            catch (Exception ex)
            {
                SLog.Debug($"Failed to load Steam cover from '{iconPath}'.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => item.IsLoadingCover = false, DispatcherPriority.Background);
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
                try
                {
                    await SyncSteamLibraryAsync(album, forceRefresh: true).ConfigureAwait(false);
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await SyncSteamLibraryAsync(album, forceRefresh: true).ConfigureAwait(false);
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
                }
                catch (OperationCanceledException)
                {
                    // ignored
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
                    await SyncSteamLibraryAsync(album, forceRefresh: true).ConfigureAwait(false);
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
                _steamStartupSyncCts?.Cancel();
                _steamStartupSyncCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to stop Steam startup sync.", ex);
            }
            finally
            {
                _steamStartupSyncCts = null;
            }

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

            try
            {
                _steamEnterSyncDebounceCts?.Cancel();
                _steamEnterSyncDebounceCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to stop Steam enter sync debounce.", ex);
            }
            finally
            {
                _steamEnterSyncDebounceCts = null;
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
            SteamInstalledGameHelper.InvalidateInstalledGamesCache();
            if (_watchedSteamAlbum != null)
                QueueSteamLibraryResync(_watchedSteamAlbum);
        }

        private void OnSteamLibraryRenamed(object sender, RenamedEventArgs e)
        {
            SteamInstalledGameHelper.InvalidateInstalledGamesCache();
            if (_watchedSteamAlbum != null)
                QueueSteamLibraryResync(_watchedSteamAlbum);
        }

        private void QueueDeferredSteamLibrarySync(EmulationAlbumItem album)
        {
            if (!OperatingSystem.IsLinux() || !IsSteamAlbum(album))
                return;

            try
            {
                _steamEnterSyncDebounceCts?.Cancel();
                _steamEnterSyncDebounceCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to reset Steam enter sync debounce.", ex);
            }

            _steamEnterSyncDebounceCts = new CancellationTokenSource();
            var token = _steamEnterSyncDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Let the carousel finish layout before scanning manifests and hydrating covers.
                    await Task.Delay(500, token).ConfigureAwait(false);
                    await SyncSteamLibraryAsync(album, forceRefresh: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (Exception ex)
                {
                    SLog.Warn("Deferred Steam library sync failed.", ex);
                }
            }, token);
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
                QueueDeferredSteamLibrarySync(steamAlbum);
                return;
            }

            StopSteamLibraryWatcher();
            _watchedSteamAlbum = null;
        }
    }
}
