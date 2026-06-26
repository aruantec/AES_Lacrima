using AES_Code.Models;
using AES_Controls.Composition;
using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Platform;
using AES_Emulation.Windows.API;
using AES_Lacrima.Mac.API;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.Services.Steam;
using AES_Lacrima.Services.Cemu;
using AES_Lacrima.Services.Rpcs3;
using AES_Lacrima.Services.ShadPs4;
using AES_Lacrima.Services.Xenia;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AES_Core.Logging;
using DrawingIcon = System.Drawing.Icon;


namespace AES_Lacrima.ViewModels
{
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {

        [RelayCommand]
        private void OpenSelectedAlbum()
        {
            if (SelectedAlbum == null)
                return;

            LoadedAlbum = SelectedAlbum;
        }

        [RelayCommand]
        private void OpenSelectedItem(object? parameter)
        {
            var item = parameter switch
            {
                MediaItem mediaItem => mediaItem,
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => HighlightedItem
            };

            if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                return;

            var album = GetBrowseAlbum();
            if (album == null)
                return;

            var handler = ResolveEmulatorHandlerForAlbum(album);
            if (handler == null)
            {
                SLog.Warn($"No emulator handler resolved for album '{album.Title}'.");
                ShowEmulatorLaunchFailure(
                    null,
                    item.Title,
                    "No emulator is configured for this section. Open render options and select an emulator.");
                return;
            }

            if (!handler.HasLauncherPath)
            {
                SLog.Warn($"Emulator handler '{handler.HandlerId}' has no launcher for album '{album.Title}'.");
                var details = handler.UsesRetroArchCores
                    ? "RetroArch is selected but no launcher was found. Use the setup button to install RetroArch, configure a Flatpak app, or set the executable path in emulation settings."
                    : $"Set the launcher path for {handler.DisplayName} in emulation settings.";
                ShowEmulatorLaunchFailure(handler, item.Title, details);
                return;
            }

            var launchSettings = TryResolveEmulationSection(album) is { } section
                ? SettingsViewModel?.GetResolvedEmulationSectionLaunchSettingsForLaunch(section, handler)
                : ResolveEmulationLaunchSettingsForAlbum(album);
            var launchRequest = new PendingEmulatorLaunchRequest(
                album.Title ?? string.Empty,
                item.Title ?? Path.GetFileNameWithoutExtension(item.FileName),
                handler,
                item.FileName,
                launchSettings);

            _activeEmulationSessionItem = item;
            NotifyPlayingItemIndexChanged();
            RequestEmulatorLaunch(launchRequest);
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            IsAlbumListCollapsed = ReadBoolSetting(section, nameof(IsAlbumListCollapsed));
            ShowStatisticsOverlay = ReadBoolSetting(section, nameof(ShowStatisticsOverlay), false);
            ShowFrametimeGraph = ReadBoolSetting(section, nameof(ShowFrametimeGraph), false);
            ShowDetailedGpuInfo = ReadBoolSetting(section, nameof(ShowDetailedGpuInfo), false);
            RenderOverlayOpacity = ReadDoubleSetting(section, nameof(RenderOverlayOpacity), 0.55);
            SelectedStretch = ReadStringSetting(section, nameof(SelectedStretch), "Uniform") is string stretchString && Enum.TryParse<Stretch>(stretchString, out var stretchValue)
                ? stretchValue
                : Stretch.Uniform;
            SelectedCaptureAspectRatioKey = ReadStringSetting(section, nameof(SelectedCaptureAspectRatioKey), "handler") ?? "handler";
            DisableVSync = ReadBoolSetting(section, nameof(DisableVSync), false);
            LowLatencyCapture = ReadBoolSetting(section, nameof(LowLatencyCapture), true);
            FrameGenerationMode = ReadIntSetting(section, nameof(FrameGenerationMode), (int)EmulationFrameGenerationMode.Off) switch
            {
                (int)EmulationFrameGenerationMode.Software120Hz => EmulationFrameGenerationMode.Software120Hz,
                (int)EmulationFrameGenerationMode.AmdAfmf => EmulationFrameGenerationMode.AmdAfmf,
                _ => EmulationFrameGenerationMode.Off,
            };
            RenderBrightness = ReadDoubleSetting(section, nameof(RenderBrightness), 1.0);
            RenderSaturation = ReadDoubleSetting(section, nameof(RenderSaturation), 1.0);
            SelectedShaderPath = ReadStringSetting(section, nameof(SelectedShaderPath), string.Empty) ?? string.Empty;
            SelectedShaderFileItem = ShaderFileItems.FirstOrDefault(item =>
                string.Equals(item.FilePath, SelectedShaderPath, StringComparison.OrdinalIgnoreCase))
                ?? ShaderFileItems.FirstOrDefault()
                ?? new(string.Empty, string.Empty);
            EmulatorVolume = ReadDoubleSetting(section, nameof(EmulatorVolume), 100.0);

            SLog.Info("EmulationViewModel.OnLoadSettings applied lightweight settings on the UI thread.");
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(IsAlbumListCollapsed), IsAlbumListCollapsed);
            WriteSetting(section, nameof(ShowStatisticsOverlay), ShowStatisticsOverlay);
            WriteSetting(section, nameof(ShowFrametimeGraph), ShowFrametimeGraph);
            WriteSetting(section, nameof(ShowDetailedGpuInfo), ShowDetailedGpuInfo);
            WriteSetting(section, nameof(RenderOverlayOpacity), RenderOverlayOpacity);
            WriteSetting(section, nameof(SelectedStretch), SelectedStretch.ToString());
            WriteSetting(section, nameof(SelectedCaptureAspectRatioKey), SelectedCaptureAspectRatioKey);
            WriteSetting(section, nameof(DisableVSync), DisableVSync);
            WriteSetting(section, nameof(LowLatencyCapture), LowLatencyCapture);
            WriteSetting(section, nameof(FrameGenerationMode), (int)FrameGenerationMode);
            WriteSetting(section, nameof(RenderBrightness), RenderBrightness);
            WriteSetting(section, nameof(RenderSaturation), RenderSaturation);
            WriteSetting(section, nameof(SelectedShaderPath), SelectedShaderPath);
            WriteSetting(section, nameof(EmulatorVolume), EmulatorVolume);

            _pendingAlbumOrder = new AvaloniaList<string>(AlbumList.Select(GetAlbumOrderKey));
            _pendingAlbumRoms = BuildAlbumRomMap();

            WriteCollectionSetting(section, "AlbumOrder", "string", _pendingAlbumOrder);
            WriteObjectSetting(section, "AlbumRoms", _pendingAlbumRoms);
        }

        private void LoadConsoleAlbums()
        {
            AlbumList.Clear();

            foreach (var imagePath in FindConsoleImagePaths())
            {
                var title = GetConsoleTitle(imagePath);
                var previewBitmap = LoadAlbumShellBitmap(imagePath);
                QueueConsoleCoverBarCropPersist(imagePath);
                var albumKey = GetAlbumPersistenceKeyFromPath(imagePath, title);

                AlbumList.Add(new EmulationAlbumItem
                {
                    Title = title,
                    Album = title,
                    FileName = imagePath,
                    LocalCoverPath = imagePath,
                    CoverBitmap = previewBitmap,
                    Children = RestoreAlbumRoms(albumKey, title, previewBitmap)
                });
                var addedAlbum = AlbumList.Last() as EmulationAlbumItem;
                UpdatePreviewItems(addedAlbum);
                if (addedAlbum?.Children.Count > 0)
                    QueueAlbumPreviewCoverLoad(addedAlbum);
            }

            ApplySavedAlbumOrder();
        }

        private async Task InitializeAlbumsAsync()
        {
            try
            {
                var shells = await Task.Run(() => BuildAlbumShells()).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var album in shells)
                        UpdatePreviewItems(album);

                    ApplySavedAlbumOrder(shells);
                    AlbumList = new AvaloniaList<FolderMediaItem>(shells);
                    SelectedAlbum = AlbumList.FirstOrDefault();
                    LoadedAlbum = null;
                    UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationAlbum());
                    _sharedAlbumCache = new AvaloniaList<FolderMediaItem>(AlbumList);
                    IsPrepared = true;
                    _isPreparing = false;
                    IsAlbumListLoading = false;
                    ApplyFilter();
                }, DispatcherPriority.Background);

                _ = RestorePersistedAlbumRomsAsync().ContinueWith(
                    _ => ScheduleDeferredSteamStartupSync(),
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to initialize emulation albums.", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsAlbumListLoading = false;
                    _isPreparing = false;
                }, DispatcherPriority.Background);
            }
        }

        private static List<EmulationAlbumItem> BuildAlbumShells()
        {
            var result = new List<EmulationAlbumItem>();
            foreach (var imagePath in FindConsoleImagePaths())
            {
                var title = GetConsoleTitle(imagePath);
                var previewBitmap = LoadAlbumShellBitmap(imagePath);
                QueueConsoleCoverBarCropPersist(imagePath);

                result.Add(new EmulationAlbumItem
                {
                    Title = title,
                    Album = title,
                    FileName = imagePath,
                    LocalCoverPath = imagePath,
                    CoverBitmap = previewBitmap,
                    Children = []
                });
            }

            return result;
        }

        private async Task RestorePersistedAlbumRomsAsync()
        {
            List<EmulationAlbumItem> albums;
            try
            {
                albums = await Dispatcher.UIThread.InvokeAsync(
                    () => AlbumList.OfType<EmulationAlbumItem>().ToList(),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to collect emulation albums for ROM restore.", ex);
                return;
            }

            if (albums.Count == 0)
                return;

            List<(EmulationAlbumItem Album, AvaloniaList<MediaItem> Children)> restored;
            try
            {
                restored = await Task.Run(() =>
                {
                    var pairs = new List<(EmulationAlbumItem, AvaloniaList<MediaItem>)>();
                    foreach (var album in albums)
                    {
                        if (EmulationConsoleCatalog.UsesAutoLibrarySync(album.Title))
                            continue;

                        var albumKey = GetAlbumPersistenceKey(album);
                        var children = RestoreAlbumRoms(albumKey, album.Title ?? string.Empty, album.CoverBitmap);
                        if (children.Count > 0)
                            pairs.Add((album, children));
                    }

                    return pairs;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to restore persisted emulation ROM lists.", ex);
                return;
            }

            foreach (var (album, children) in restored)
            {
                try
                {
                    var albumKey = GetAlbumPersistenceKey(album);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var item in children)
                            album.Children.Add(item);

                        SyncAlbumTotalChildCount(album);
                        UpdatePreviewItems(album);
                        QueueAlbumPreviewCoverLoad(album);

                        if (ReferenceEquals(LoadedAlbum, album))
                        {
                            ApplyFilter();
                            PrepareAlbumItemsForLocalDisplay(album);
                            QueueLocalAlbumPresentation(album);
                        }
                    }, DispatcherPriority.Background);

                    await Task.Yield();
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to restore ROM list for album '{album.Title}'.", ex);
                }
            }
        }

        private static void SyncAlbumTotalChildCount(EmulationAlbumItem album)
        {
            album.TotalChildCount = album.Children.Count;
        }

        private void QueueAllAlbumPresentationCoverLoads()
        {
            int center = SelectedAlbumIndex >= 0 ? SelectedAlbumIndex : 0;
            QueueAlbumPresentationCoverLoadsNearIndex(center);
        }

        private void ScheduleNeighborAlbumPreviewCoverLoads(int centerIndex)
        {
            _pendingNeighborCoverLoadIndex = centerIndex;

            _albumRowNeighborCoverLoadTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AlbumRowNeighborCoverLoadDelayMs)
            };

            _albumRowNeighborCoverLoadTimer.Stop();
            _albumRowNeighborCoverLoadTimer.Tick -= OnNeighborAlbumPreviewCoverLoadTick;
            _albumRowNeighborCoverLoadTimer.Tick += OnNeighborAlbumPreviewCoverLoadTick;
            _albumRowNeighborCoverLoadTimer.Start();
        }

        private void OnNeighborAlbumPreviewCoverLoadTick(object? sender, EventArgs e)
        {
            _albumRowNeighborCoverLoadTimer?.Stop();

            var index = _pendingNeighborCoverLoadIndex;
            if (index < 0 || index >= AlbumList.Count)
                return;

            QueueAlbumPresentationCoverLoadsNearIndex(index, AlbumRowPreviewCoverRadius, skipCenter: true);
        }

        private void QueueAlbumPresentationCoverLoadsNearIndex(int centerIndex, int radius = AlbumRowPreviewCoverRadius, bool skipCenter = false)
        {
            if (AlbumList.Count == 0)
                return;

            int start = Math.Max(0, centerIndex - radius);
            int end = Math.Min(AlbumList.Count - 1, centerIndex + radius);
            for (int i = start; i <= end; i++)
            {
                if (skipCenter && i == centerIndex)
                    continue;

                if (AlbumList[i] is EmulationAlbumItem album && album.Children.Count > 0)
                    QueueAlbumPreviewCoverLoad(album);
            }
        }

        private void CancelAlbumPreviewCoverLoadsOutsideRange(int centerIndex, int radius)
        {
            if (AlbumList.Count == 0)
                return;

            int start = Math.Max(0, centerIndex - radius);
            int end = Math.Min(AlbumList.Count - 1, centerIndex + radius);
            var keep = new HashSet<FolderMediaItem>();
            for (int i = start; i <= end; i++)
            {
                if (AlbumList[i] is EmulationAlbumItem album)
                    keep.Add(album);
            }

            List<EmulationAlbumItem> cancelled = [];
            lock (_albumPreviewCoverLoadGate)
            {
                foreach (var (album, cts) in _albumTilePreviewCtsMap.ToList())
                {
                    if (keep.Contains(album))
                        continue;

                    try
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to cancel preview cover load for '{album.Title}'.", ex);
                    }

                    _albumTilePreviewCtsMap.Remove(album);
                    if (album is EmulationAlbumItem emulationAlbum)
                        cancelled.Add(emulationAlbum);
                }
            }

            if (cancelled.Count == 0)
                return;

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var album in cancelled)
                        SyncAlbumPreviewLoadingState(album);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to clear cancelled album preview loading state.", ex);
            }
        }

        private void PrepareAlbumItemsForLocalDisplay(FolderMediaItem album)
        {
            if (album.Children.Count == 0)
                return;

            for (int i = 0; i < album.Children.Count; i++)
            {
                var item = album.Children[i];
                if (string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.FileName))
                {
                    item.Title = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(
                        Path.GetFileNameWithoutExtension(item.FileName));
                }

                item.CoverBitmap ??= album.CoverBitmap;
                if (ShouldIndicateLocalCoverLoading(item, album))
                {
                    TryApplyLocalCoverBitmap(item, album);
                    item.IsLoadingCover = ShouldIndicateLocalCoverLoading(item, album);
                }
                else
                {
                    item.IsLoadingCover = false;
                }
            }
        }

        private static bool ShouldIndicateLocalCoverLoading(MediaItem item, FolderMediaItem album)
        {
            if (item.CoverBitmap != null && !ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
                return false;

            if (HasLocalCoverFile(item.FileName) || HasLegacyEmbeddedCover(item.FileName))
                return true;

            if (!string.IsNullOrWhiteSpace(item.LocalCoverPath) && File.Exists(item.LocalCoverPath))
                return true;

            return false;
        }

        private static void PrepareAlbumItemsForLocalCoverPaths(FolderMediaItem album)
        {
            if (album.Children.Count == 0)
                return;

            foreach (var item in album.Children)
            {
                if (string.IsNullOrWhiteSpace(item.FileName))
                    continue;

                var sidecarPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);
                if (!string.IsNullOrWhiteSpace(sidecarPath) && File.Exists(sidecarPath))
                {
                    item.LocalCoverPath = sidecarPath;
                    continue;
                }

                var metaPath = EmulationCoverCacheHelper.GetMetadataCachePath(item.FileName);
                if (!string.IsNullOrWhiteSpace(metaPath) && File.Exists(metaPath))
                    item.LocalCoverPath = metaPath;
            }
        }

        private static void TryApplyLocalCoverBitmap(MediaItem item, FolderMediaItem album)
        {
            if (item.CoverBitmap != null && !ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
                return;

            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            try
            {
                byte[]? bytes = EmulationCoverCacheHelper.TryReadCoverBytes(item.FileName);
                string? coverPath = bytes is { Length: > 0 }
                    ? EmulationCoverCacheHelper.GetCoverCachePath(item.FileName)
                    : null;

                if (bytes is not { Length: > 0 })
                {
                    var metaPath = EmulationCoverCacheHelper.GetMetadataCachePath(item.FileName);
                    if (!string.IsNullOrWhiteSpace(metaPath) && File.Exists(metaPath))
                    {
                        bytes = TryReadMetadataCoverBytes(metaPath);
                        coverPath = metaPath;
                    }
                }

                if (bytes is not { Length: > 0 })
                    return;

                using var ms = new MemoryStream(bytes);
                item.CoverBitmap = Bitmap.DecodeToWidth(ms, 384);
                item.CoverFound = true;
                item.IsLoadingCover = false;
                if (!string.IsNullOrWhiteSpace(coverPath))
                    item.LocalCoverPath = coverPath;
            }
            catch (Exception ex)
            {
                SLog.Debug($"Failed to decode local cover for '{item.Title}'.", ex);
            }
        }

        private static byte[]? TryReadMetadataCoverBytes(string metaPath)
        {
            try
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(metaPath);
                if (metadata == null)
                    return null;

                foreach (var entry in BinaryMetadataHelper.ReadMetadataImages(metadata))
                {
                    if (entry.Kind == TagImageKind.Cover && entry.Data is { Length: > 0 })
                        return entry.Data;
                }
            }
            catch
            {
                // Caller logs when needed.
            }

            return null;
        }

        private void PrepareAlbumItemsForCoverDisplay(FolderMediaItem album)
        {
            if (album.Children.Count == 0)
                return;

            int focus = GetCoverLoadFocusIndex(album);
            int radius = InitialCoverLoadingRadius;

            for (int i = 0; i < album.Children.Count; i++)
            {
                var item = album.Children[i];
                item.CoverBitmap ??= album.CoverBitmap;
                if (HasLocalCoverFile(item.FileName))
                    item.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);

                if (Math.Abs(i - focus) > radius)
                    continue;

                if (!NeedsCoverLookup(item, album))
                    continue;

                TryInvalidateStaleCoverLookupState(item);

                if (HasLocalCoverFile(item.FileName) && !ShouldRetryCoverLookupAfterTitleImprovement(item))
                {
                    item.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);
                    continue;
                }

                item.CoverBitmap ??= album.CoverBitmap;
                item.IsLoadingCover = true;
            }
        }

        private static void MarkRomItemCoverLoading(MediaItem item, FolderMediaItem album)
        {
            item.CoverBitmap ??= album.CoverBitmap;
            if (HasLocalCoverFile(item.FileName) && !ShouldRetryCoverLookupAfterTitleImprovement(item))
                item.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(item.FileName);

            if (item.CoverBitmap != null && !ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
            {
                item.IsLoadingCover = false;
                return;
            }

            if (!NeedsCoverLookup(item, album))
            {
                item.IsLoadingCover = false;
                return;
            }

            item.CoverBitmap ??= album.CoverBitmap;
            item.IsLoadingCover = true;
        }

        private static void MarkRomItemCoverLoadComplete(MediaItem item, FolderMediaItem album)
        {
            item.IsLoadingCover = false;
            if (item.CoverFound)
                return;

            if (item.CoverBitmap == null || ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
                item.CoverBitmap = album.CoverBitmap;
        }

        private int GetCoverLoadBatchSize(FolderMediaItem album) =>
            ReferenceEquals(LoadedAlbum, album) ? LoadedAlbumCoverLoadBatchSize : AlbumCoverLoadBatchSize;

        private int GetCoverLoadParallelism(FolderMediaItem album) =>
            ReferenceEquals(LoadedAlbum, album) ? LoadedAlbumCoverLoadParallelism : AlbumCoverLoadParallelism;

        private int GetCoverLoadFocusIndex(FolderMediaItem album)
        {
            if (!ReferenceEquals(album, LoadedAlbum) || album.Children.Count == 0)
                return 0;

            if (CoverItems.Count == 0)
                return 0;

            int center = CompositionViewportState.VisibleCenterIndex;
            if (center >= 0 && center < album.Children.Count)
                return center;

            int selected = GetRoundedSelectedIndex(SelectedIndex);
            if (selected < 0 || selected >= CoverItems.Count)
                selected = 0;

            if (PointedIndex >= 0 && PointedIndex < CoverItems.Count)
                selected = PointedIndex;

            var focusItem = CoverItems[selected];
            int childIndex = album.Children.IndexOf(focusItem);
            return childIndex >= 0 ? childIndex : selected;
        }

        private List<(MediaItem Item, int Index)> GetNextCoverLoadBatch(FolderMediaItem album, int? maxItems = null)
        {
            int center = GetCoverLoadFocusIndex(album);
            int take = maxItems ?? GetCoverLoadBatchSize(album);
            var visible = CompositionViewportState.VisibleIndices;
            HashSet<int>? visibleSet = visible.Count > 0 ? new HashSet<int>(visible) : null;

            return album.Children
                .Select((item, index) => (Item: item, Index: index))
                .Where(pair => NeedsCoverLookup(pair.Item, album))
                .Where(pair => string.IsNullOrWhiteSpace(pair.Item.FileName) ||
                               (!_coverLookupAttemptedPaths.Contains(pair.Item.FileName) &&
                                !_coverLookupInFlightPaths.Contains(pair.Item.FileName)))
                .OrderBy(pair => visibleSet != null && visibleSet.Contains(pair.Index) ? 0 : 1)
                .ThenBy(pair => Math.Abs(pair.Index - center))
                .Take(take)
                .ToList();
        }

        private bool TryTakeNextCoverLoadItem(FolderMediaItem album, out MediaItem item)
        {
            item = null!;
            if (!ReferenceEquals(LoadedAlbum, album))
                return false;

            var next = GetNextCoverLoadBatch(album, maxItems: 1);
            if (next.Count == 0)
                return false;

            item = next[0].Item;
            if (!string.IsNullOrWhiteSpace(item.FileName))
                _coverLookupInFlightPaths.Add(item.FileName);

            return true;
        }

        private async Task RunAlbumCoverLoadWorkersAsync(
            FolderMediaItem album,
            int parallelism,
            CancellationToken cancellationToken)
        {
            var workers = new Task[parallelism];
            for (int i = 0; i < parallelism; i++)
                workers[i] = RunAlbumCoverLoadWorkerAsync(album, cancellationToken);

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private async Task RunAlbumCoverLoadWorkerAsync(FolderMediaItem album, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && ReferenceEquals(LoadedAlbum, album))
            {
                if (!TryTakeNextCoverLoadItem(album, out var item))
                    break;

                await LoadSingleAlbumCoverAsync(album, item, cancellationToken).ConfigureAwait(false);
                ScheduleThrottledAlbumCoverDisplayNotify(album);
            }
        }

        private void ScheduleThrottledAlbumCoverDisplayNotify(FolderMediaItem album)
        {
            _coverDisplayNotifyAlbum = album;

            _coverDisplayNotifyTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CoverDisplayNotifyMinIntervalMs)
            };

            _coverDisplayNotifyTimer.Stop();
            _coverDisplayNotifyTimer.Tick -= OnCoverDisplayNotifyDebounceTick;
            _coverDisplayNotifyTimer.Tick += OnCoverDisplayNotifyDebounceTick;
            _coverDisplayNotifyTimer.Start();
        }

        private void OnCoverDisplayNotifyDebounceTick(object? sender, EventArgs e)
        {
            _coverDisplayNotifyTimer?.Stop();

            var album = _coverDisplayNotifyAlbum;
            _coverDisplayNotifyAlbum = null;
            if (album == null || !ReferenceEquals(LoadedAlbum, album))
                return;

            if (!IsActive)
            {
                _albumCoverDisplayNotifyPending = true;
                return;
            }

            OnPropertyChanged(nameof(AlbumCoverDisplayRevision));
        }

        private void CancelCoverDisplayNotifyDebounce()
        {
            _coverDisplayNotifyTimer?.Stop();
            _coverDisplayNotifyAlbum = null;
        }

        private static bool IsItemNearVisibleCenter(MediaItem item, FolderMediaItem album, int centerIndex)
        {
            int idx = album.Children.IndexOf(item);
            return idx >= 0 && Math.Abs(idx - centerIndex) <= 2;
        }

        private async Task<bool> TryPopulateRomCoverAsync(
            MediaItem item,
            string? albumTitle,
            CancellationToken cancellationToken)
        {
            if (SteamInstalledGameHelper.IsSteamGamePath(item.FileName))
                return await TryLoadSteamGameCoverAsync(item, cancellationToken).ConfigureAwait(false);

            if (MetadataService == null)
                return false;

            bool found = await CoverLoader.EnsureCoverAsync(
                    item,
                    albumTitle,
                    MetadataService,
                    EmulationCoverLoadRequest.WithOnline(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!found && !string.IsNullOrWhiteSpace(item.FileName) && !IsOnlineCoverLookupExhausted(item.FileName))
                _deferredCoverLookupPaths.Add(item.FileName);
            else if (!string.IsNullOrWhiteSpace(item.FileName))
                _deferredCoverLookupPaths.Remove(item.FileName);

            return found;
        }

        private async Task LoadSingleAlbumCoverAsync(
            FolderMediaItem album,
            MediaItem item,
            CancellationToken cancellationToken)
        {
            bool coverFound = false;
            try
            {
                int focusIndex = GetCoverLoadFocusIndex(album);
                bool showStatus = IsItemNearVisibleCenter(item, album, focusIndex);

                if (showStatus)
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () =>
                        {
                            item.IsLoadingCover = true;
                            SetRomCarouselCoverStatus($"Searching cover art for {item.Title}...");
                        },
                        DispatcherPriority.Background);
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => item.IsLoadingCover = true,
                        DispatcherPriority.Background);
                }

                if (!string.IsNullOrWhiteSpace(item.FileName))
                    TryInvalidateStaleCoverLookupState(item);

                using var itemTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                itemTimeoutCts.CancelAfter(TimeSpan.FromSeconds(PerRomTitleResolveTimeoutSeconds));

                try
                {
                    await EnsureItemTitleBeforeCoverAsync(item, album.Title, itemTimeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    SLog.Debug($"Title lookup timed out for '{item.Title}' in album '{album.Title}'; continuing with cover lookup.");
                }

                using var coverTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                coverTimeoutCts.CancelAfter(TimeSpan.FromSeconds(PerRomCoverLoadTimeoutSeconds));

                try
                {
                    coverFound = await TryPopulateRomCoverAsync(item, album.Title, coverTimeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    SLog.Debug($"Cover lookup timed out for '{item.Title}' in album '{album.Title}'; moving on.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to load cover for rom '{item.Title}' in album '{album.Title}'.", ex);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(item.FileName))
                {
                    _coverLookupInFlightPaths.Remove(item.FileName);
                    if (coverFound || IsOnlineCoverLookupExhausted(item.FileName))
                    {
                        _coverLookupAttemptedPaths.Add(item.FileName);
                        _coverLookupRetryCounts.Remove(item.FileName);
                    }
                    else
                    {
                        var retries = _coverLookupRetryCounts.TryGetValue(item.FileName, out var count) ? count + 1 : 1;
                        _coverLookupRetryCounts[item.FileName] = retries;
                        if (retries >= 2)
                            _coverLookupAttemptedPaths.Add(item.FileName);
                        else
                            _deferredCoverLookupPaths.Add(item.FileName);
                    }
                }

                if (item.IsLoadingCover)
                {
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(
                            () => MarkRomItemCoverLoadComplete(item, album),
                            DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to clear cover loading state for '{item.Title}'.", ex);
                    }
                }
            }
        }

        private void BeginAlbumCoverScan()
        {
            if (_activeAlbumCoverScans++ == 0)
            {
                SetRomCarouselCoverStatus("Loading cover art...");
                OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
            }
        }

        private void EndAlbumCoverScan()
        {
            if (_activeAlbumCoverScans > 0 && --_activeAlbumCoverScans == 0)
            {
                SetRomCarouselCoverStatus(null);
                OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
            }
        }

        private void SetRomCarouselCoverStatus(string? status)
        {
            var next = status ?? string.Empty;
            if (string.Equals(_romCarouselCoverStatus, next, StringComparison.Ordinal))
                return;

            _romCarouselCoverStatus = next;
            OnPropertyChanged(nameof(RomCarouselCoverStatus));
            OnPropertyChanged(nameof(IsRomCarouselCoverStatusVisible));
        }

        private async Task<PersistedEmulationState> LoadPersistedEmulationStateAsync()
        {
            var section = await LoadSettingsSectionAsync().ConfigureAwait(false);
            if (section == null)
            {
                SLog.Info("EmulationViewModel.LoadPersistedEmulationStateAsync found no persisted state.");
                return new PersistedEmulationState(
                    IsAlbumListCollapsed,
                    [],
                    new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase));
            }

            var restoreStopwatch = Stopwatch.StartNew();
            var isAlbumListCollapsed = ReadBoolSetting(section, nameof(IsAlbumListCollapsed));
            var albumOrder = ReadCollectionSetting(section, "AlbumOrder", "string", new AvaloniaList<string>());
            var albumRoms = ReadObjectSetting<Dictionary<string, List<MediaItem>>>(section, "AlbumRoms")
                ?? new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);
            restoreStopwatch.Stop();

            SLog.Info(
                $"EmulationViewModel.LoadPersistedEmulationStateAsync parsed state in {restoreStopwatch.ElapsedMilliseconds} ms. " +
                $"SavedAlbums={albumRoms.Count}, SavedOrderEntries={albumOrder.Count}.");
            return new PersistedEmulationState(isAlbumListCollapsed, albumOrder, albumRoms);
        }

        private void ApplyPersistedEmulationState(PersistedEmulationState state)
        {
            IsAlbumListCollapsed = state.IsAlbumListCollapsed;
            _pendingAlbumOrder = state.AlbumOrder;
            _pendingAlbumRoms = state.AlbumRoms;
        }

        [RelayCommand(CanExecute = nameof(CanAddRoms))]
        private async Task AddRoms()
        {
            var album = SelectedAlbum;
            if (album == null)
                return;

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow?.StorageProvider is not { } storageProvider)
            {
                return;
            }

            if (EmulationConsoleCatalog.SupportsFolderImport(album.Title))
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = $"Add items to {album.Title}",
                    AllowMultiple = true
                });

                if (folders.Count == 0)
                    return;

                var folderPaths = folders
                    .Select(folder => folder.TryGetLocalPath())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>();

                bool addedAnyFromFolders = ImportRomPaths(album, folderPaths);

                if (!addedAnyFromFolders)
                    return;

                FinalizeRomImport(album);
                return;
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Add Roms to {album.Title}",
                AllowMultiple = true
                ,
                FileTypeFilter = EmulationConsoleCatalog.BuildFilePickerFilters(album.Title)
            });

            if (files.Count == 0)
                return;

            var paths = files
                .Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>();

            bool addedAny = ImportRomPaths(album, paths);

            if (!addedAny)
                return;

            FinalizeRomImport(album);
        }

        [RelayCommand(CanExecute = nameof(CanAddRoms))]
        private async Task ScanFolder()
        {
            var album = SelectedAlbum;
            if (album == null)
                return;

            string? rootPath;
            if (OperatingSystem.IsMacOS())
            {
                rootPath = MacSystemDialogs.PickFolder($"Scan Folder for {album.Title} Roms");
            }
            else
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                    desktop.MainWindow?.StorageProvider is not { } storageProvider)
                {
                    return;
                }

                var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = $"Scan Folder for {album.Title} Roms",
                    AllowMultiple = false
                });

                if (folders.Count == 0)
                    return;

                rootPath = folders[0].TryGetLocalPath();
            }

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return;

            var scanPatterns = EmulationConsoleCatalog.GetScanPatterns(album.Title);
            var paths = await Task.Run(() => ScanFolderForRomPaths(rootPath, album.Title, scanPatterns));
            bool addedAny = ImportRomPaths(album, paths);

            if (!addedAny)
                return;

            FinalizeRomImport(album);
        }

        [RelayCommand(CanExecute = nameof(CanDeleteItem))]
        private void DeleteItem(object? parameter)
        {
            var target = parameter switch
            {
                MediaItem mi => mi,
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => HighlightedItem
            };

            if (target == null)
                return;

            var album = GetBrowseAlbum();
            if (album == null)
                return;

            if (album.Children.Remove(target))
            {
                ApplyFilter();
                var emulationAlbum = album as EmulationAlbumItem;
                UpdatePreviewItems(emulationAlbum);
                QueueAlbumPreviewCoverLoad(emulationAlbum);
                SaveSettings();
            }
        }

        private bool CanDeleteItem(object? parameter) =>
            (parameter is MediaItem) ||
            (parameter is int idx && idx >= 0 && idx < CoverItems.Count) ||
            (HighlightedItem != null && !string.IsNullOrEmpty(HighlightedItem.FileName));

        [RelayCommand(CanExecute = nameof(CanOpenMetadata))]
        private async Task OpenMetadata(object? parameter)
        {
            EnsureCarouselForActiveAlbum();

            var target = ResolveMetadataMenuTarget(parameter);
            if (target == null || MetadataService == null)
                return;

            try
            {
                await MetadataService.LoadMetadataForItemAsync(
                    target,
                    LoadedAlbum?.Title ?? SelectedAlbum?.Title).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to open emulation metadata editor.", ex);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(RefreshActiveAlbumState);
            }
        }

        private void EnsureCarouselForActiveAlbum()
        {
            if (CoverItems.Count > 0)
                return;

            if (HasActiveAlbumItems)
                ApplyFilter();
        }

        private MediaItem? ResolveMetadataMenuTarget(object? parameter)
        {
            var album = GetBrowseAlbum();
            if (album == null)
                return null;

            if (IsEmulatorRunning &&
                _activeEmulationSessionItem is { FileName: { Length: > 0 } } sessionItem &&
                IsItemInBrowseAlbum(sessionItem, album))
            {
                return sessionItem;
            }

            if (parameter is MediaItem mediaItem &&
                IsItemInBrowseAlbum(mediaItem, album))
            {
                return mediaItem;
            }

            var index = ResolveContextMenuIndex(parameter);
            if (index >= 0 && index < CoverItems.Count)
                return CoverItems[index];

            if (PointedIndex >= 0 && PointedIndex < CoverItems.Count)
                return CoverItems[PointedIndex];

            int displayIndex = ResolveCarouselDisplayIndex();
            if (displayIndex >= 0 && displayIndex < CoverItems.Count)
                return CoverItems[displayIndex];

            if (HighlightedItem != null && IsItemInBrowseAlbum(HighlightedItem, album))
                return HighlightedItem;

            return null;
        }

        private static bool IsItemInBrowseAlbum(MediaItem item, FolderMediaItem album)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return false;

            return album.Children.Any(child =>
                ReferenceEquals(child, item) ||
                string.Equals(child.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));
        }

        private static int ResolveContextMenuIndex(object? parameter)
        {
            return parameter switch
            {
                int idx => idx,
                double value when !double.IsNaN(value) => (int)Math.Round(value),
                _ => -1
            };
        }

        private void ClearAlbumCoverScanSessionState(FolderMediaItem album)
        {
            lock (_albumsWithMetadataScanned)
            {
                _albumsWithMetadataScanned.Remove(album);
            }

            lock (_albumsWithLocalCoversHydrated)
            {
                _albumsWithLocalCoversHydrated.Remove(album);
            }
        }

        [RelayCommand(CanExecute = nameof(CanClearLoadedAlbum))]
        private async Task ClearAlbumCache()
        {
            var album = GetBrowseAlbum();
            if (album == null)
                return;

            CancelAllAlbumCoverScans();
            if (album is EmulationAlbumItem emulationAlbum)
                CancelAlbumPreviewCoverLoad(emulationAlbum);

            if (MetadataService != null && album.Children.Count > 0)
            {
                try
                {
                    await MetadataService.ClearCacheForItemsAsync(album.Children).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to clear metadata cache for album '{album.Title}'", ex);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var placeholder = album.CoverBitmap;
                foreach (var child in album.Children)
                {
                    child.MetadataProcessed = false;
                    child.CoverFound = false;
                    child.LocalCoverPath = null;
                    child.CoverBitmap = placeholder;
                    child.IsLoadingCover = true;
                }

                if (album is EmulationAlbumItem emulationAlbum)
                    emulationAlbum.IsLoadingCover = true;

                ClearAlbumCoverScanSessionState(album);

                _deferredCoverLookupPaths.Clear();
                _coverLookupAttemptedPaths.Clear();
                _coverLookupInFlightPaths.Clear();
                ApplyFilter();
                NotifyAlbumCoverDisplayChanged(album, forceFullRescan: true);
                QueueAlbumPreviewCoverLoad(album as EmulationAlbumItem);
                QueueSelectedAlbumCoverScan(album);
            }, DispatcherPriority.Background);
        }

        [RelayCommand(CanExecute = nameof(CanClearLoadedAlbum))]
        private Task ClearAlbum()
        {
            var album = SelectedAlbum;
            if (album == null)
                return Task.CompletedTask;

            try
            {
                _albumCoverScanCts?.Cancel();
                _albumCoverScanCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to cancel emulation album cover scan while clearing album.", ex);
            }
            finally
            {
                _albumCoverScanCts = null;
            }

            album.Children.Clear();
            album.TotalChildCount = 0;
            ClearAlbumCoverScanSessionState(album);
            ApplyFilter();
            SaveSettings();
            return Task.CompletedTask;
        }

        private bool CanRefreshCoverAndTitle(object? parameter) =>
            HasActiveAlbumItems && !IsEmulatorRunning;

        [RelayCommand(CanExecute = nameof(CanRefreshCoverAndTitle))]
        private async Task RefreshCoverAndTitle(object? parameter)
        {
            var album = GetBrowseAlbum();
            var item = ResolveMetadataMenuTarget(parameter);
            if (album == null || item == null || string.IsNullOrWhiteSpace(item.FileName))
                return;

            ClearAlbumCoverScanSessionState(album);

            _coverLookupAttemptedPaths.Remove(item.FileName);
            _coverLookupInFlightPaths.Remove(item.FileName);
            TryInvalidateStaleCoverLookupState(item);
            item.IsLoadingCover = true;
            NotifyAlbumCoverDisplayChanged(album);

            try
            {
                await EnsureItemTitleBeforeCoverAsync(item, album.Title, CancellationToken.None).ConfigureAwait(false);
                await TryPopulateRomCoverAsync(item, album.Title, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Manual cover/title refresh failed for '{item.Title}'.", ex);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MarkRomItemCoverLoadComplete(item, album);
                    UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: false);
                    NotifyAlbumCoverDisplayChanged(album);
                    RefreshCoverAndTitleCommand.NotifyCanExecuteChanged();
                });
            }
        }

        private bool CanOpenMetadata(object? parameter) =>
            (IsEmulatorRunning && _activeEmulationSessionItem is { FileName: { Length: > 0 } }) ||
            ResolveMetadataMenuTarget(parameter) != null ||
            CoverItems.Count > 0 ||
            LoadedAlbum?.Children.Count > 0 ||
            SelectedAlbum?.Children.Count > 0;

        private bool CanClearLoadedAlbum() =>
            HasActiveAlbumItems && !IsSteamAlbum(LoadedAlbum);

        private static IReadOnlyList<string> FindConsoleImagePaths()
        {
            foreach (var directory in EnumerateConsoleAssetDirectories())
            {
                if (!Directory.Exists(directory))
                    continue;

                var files = Directory
                    .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(path => IsSupportedConsoleImage(path) &&
                                   EmulationConsoleCatalog.IsConsoleAssetAvailableOnCurrentPlatform(path))
                    .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count > 0)
                    return files;
            }

            return [];
        }

        private static IEnumerable<string> EnumerateConsoleAssetDirectories()
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            };

            foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var current = new DirectoryInfo(root);
                while (current != null)
                {
                    var directAssets = Path.Combine(current.FullName, "Assets", "Consoles");
                    if (visited.Add(directAssets))
                        yield return directAssets;

                    var projectAssets = Path.Combine(current.FullName, "AES_Lacrima", "Assets", "Consoles");
                    if (visited.Add(projectAssets))
                        yield return projectAssets;

                    current = current.Parent;
                }
            }
        }

        private static bool IsSupportedConsoleImage(string path)
            => SupportedConsoleImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static string GetConsoleTitle(string imagePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(imagePath);
            var normalizedName = fileName.Replace('_', ' ').Replace('-', ' ').Trim();
            return EmulationConsoleCatalog.GetDisplayName(normalizedName);
        }

        private static string GetAlbumOrderKey(FolderMediaItem album)
            => GetAlbumPersistenceKey(album);

        private static string GetAlbumPersistenceKey(FolderMediaItem album)
        {
            if (!string.IsNullOrWhiteSpace(album.FileName))
            {
                var fileName = GetFileNameFromPath(album.FileName);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }

            return album.Title?.Trim() ?? string.Empty;
        }

        private static string GetAlbumPersistenceKeyFromPath(string imagePath, string? albumTitle)
        {
            var candidate = GetFileNameFromPath(imagePath);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            return albumTitle?.Trim() ?? string.Empty;
        }

        private static string GetFileNameFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var normalized = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFileName(normalized).Trim();
        }

        private void UpdatePreviewItems(
            EmulationAlbumItem? album,
            bool rebuildStructure = true,
            bool? useFirstItemCover = null)
        {
            if (album == null)
                return;

            album.RebuildPreviewItems(
                useFirstItemCover ?? SettingsViewModel?.EmulationUseFirstItemCover == true,
                rebuildStructure);
            SyncAlbumPreviewLoadingState(album);
        }

        private void SyncAlbumPreviewLoadingState(EmulationAlbumItem album)
        {
            if (album.Children.Count == 0)
            {
                album.IsLoadingCover = false;
                return;
            }

            bool useFirstItemCover = SettingsViewModel?.EmulationUseFirstItemCover == true;
            if (HasSatisfiedAlbumPreview(album, useFirstItemCover))
            {
                album.IsLoadingCover = false;
                return;
            }

            lock (_albumPreviewCoverLoadGate)
            {
                album.IsLoadingCover = _activeAlbumPreviewCoverLoads.Contains(album);
            }
        }

        private static bool HasSatisfiedAlbumPreview(EmulationAlbumItem album, bool useFirstItemCover)
        {
            var presentation = album.GetPresentationCoverChildren(useFirstItemCover).ToList();
            if (presentation.Count == 0)
                return album.CoverBitmap != null;

            return presentation.All(item =>
                item.CoverBitmap != null &&
                !ReferenceEquals(item.CoverBitmap, album.CoverBitmap));
        }

        private List<MediaItem> GetAlbumPreviewCoverBatch(FolderMediaItem album, bool prioritizeFirstChild = false)
        {
            bool useFirstItemCover = SettingsViewModel?.EmulationUseFirstItemCover == true;
            var candidates = album.GetPresentationCoverChildren(useFirstItemCover).ToList();

            if (prioritizeFirstChild && useFirstItemCover && album.Children.Count > 0)
            {
                var firstChild = album.Children[0];
                candidates = candidates
                    .OrderBy(item => ReferenceEquals(item, firstChild) ? 0 : 1)
                    .ToList();
            }

            return candidates
                .Where(item => NeedsPreviewCoverHydration(item, album))
                .ToList();
        }

        private void EndAlbumPreviewCoverLoad(FolderMediaItem album)
        {
            lock (_albumPreviewCoverLoadGate)
            {
                _activeAlbumPreviewCoverLoads.Remove(album);
            }
        }

        private void CancelAlbumPreviewCoverLoad(EmulationAlbumItem album)
        {
            EndAlbumPreviewCoverLoad(album);

            try
            {
                if (_albumTilePreviewCtsMap.TryGetValue(album, out var existingCts))
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                    _albumTilePreviewCtsMap.Remove(album);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to cancel previous album preview cover load for '{album.Title}'.", ex);
            }
        }

        private void QueueAlbumPreviewCoverLoad(EmulationAlbumItem? album, bool forceRestart = false)
        {
            if (album == null || album.Children.Count == 0)
                return;

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (album.PreviewItems.Count == 0)
                        UpdatePreviewItems(album, rebuildStructure: true);

                    if (GetAlbumPreviewCoverBatch(album, forceRestart).Count > 0 &&
                        ReferenceEquals(LoadedAlbum, album))
                    {
                        album.IsLoadingCover = true;
                    }
                    else
                    {
                        SyncAlbumPreviewLoadingState(album);
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to mark album preview loading for '{album.Title}'.", ex);
            }

            CancellationTokenSource cts;
            lock (_albumPreviewCoverLoadGate)
            {
                try
                {
                    if (_albumTilePreviewCtsMap.TryGetValue(album, out var existingCts))
                    {
                        existingCts.Cancel();
                        existingCts.Dispose();
                        _albumTilePreviewCtsMap.Remove(album);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to cancel previous album preview cover load for '{album.Title}'.", ex);
                }

                cts = new CancellationTokenSource();
                _albumTilePreviewCtsMap[album] = cts;
            }

            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        lock (_albumPreviewCoverLoadGate)
                        {
                            if (_activeAlbumPreviewCoverLoads.Add(album))
                                break;
                        }

                        await Task.Delay(25, token).ConfigureAwait(false);
                    }

                    await _albumPreviewCoverConcurrency.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await LoadAlbumPreviewCoversAsync(album, token, forceRestart).ConfigureAwait(false);
                    }
                    finally
                    {
                        _albumPreviewCoverConcurrency.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer preview load request.
                }
                finally
                {
                    EndAlbumPreviewCoverLoad(album);
                    try
                    {
                        Dispatcher.UIThread.Post(() => SyncAlbumPreviewLoadingState(album), DispatcherPriority.Background);
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to sync album preview loading state for '{album.Title}'.", ex);
                    }
                }
            }, token);
        }

        private async Task LoadAlbumPreviewCoversAsync(
            EmulationAlbumItem album,
            CancellationToken cancellationToken,
            bool prioritizeFirstChild = false)
        {
            bool allowOnlineLookup = false;
            List<MediaItem> itemsToLoad;
            try
            {
                itemsToLoad = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    allowOnlineLookup = ReferenceEquals(LoadedAlbum, album);
                    return GetAlbumPreviewCoverBatch(album, prioritizeFirstChild);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to collect album preview cover targets for '{album.Title}'.", ex);
                return;
            }

            try
            {
                if (itemsToLoad.Count == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        bool rebuildStructure = album.Children.Count > 0 &&
                                                (album.PreviewItems.Count <= 1 ||
                                                 album.PreviewItems.All(item => ReferenceEquals(item.CoverBitmap, album.CoverBitmap)));
                        UpdatePreviewItems(album, rebuildStructure: rebuildStructure);
                        if (ReferenceEquals(LoadedAlbum, album))
                            NotifyAlbumCoverDisplayChanged(album);
                    }, DispatcherPriority.Background);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ReferenceEquals(LoadedAlbum, album))
                    {
                        foreach (var item in itemsToLoad)
                            MarkRomItemCoverLoading(item, album);
                        album.IsLoadingCover = true;
                    }
                }, DispatcherPriority.Background);

                foreach (var item in itemsToLoad)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (!allowOnlineLookup)
                        {
                            if (HasLocalCoverFile(item.FileName) || HasLegacyEmbeddedCover(item.FileName))
                            {
                                await CoverLoader.EnsureCoverAsync(
                                        item,
                                        album.Title,
                                        MetadataService,
                                        EmulationCoverLoadRequest.LocalOnly,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            if (SectionHandlers.GenericAlbumNormalizer.IsRomMetadataAlreadyScanned(item.FileName))
                            {
                                var cachedTitle = TryReadCachedMetadataTitle(item.FileName);
                                if (string.IsNullOrWhiteSpace(cachedTitle))
                                {
                                    cachedTitle = SectionHandlers.GenericAlbumNormalizer.ResolveRomTitle(
                                        item.FileName,
                                        album.Title,
                                        item.Title);
                                }

                                if (!string.IsNullOrWhiteSpace(cachedTitle) &&
                                    !string.Equals(item.Title, cachedTitle, StringComparison.Ordinal))
                                {
                                    await Dispatcher.UIThread.InvokeAsync(
                                        () => item.Title = cachedTitle,
                                        DispatcherPriority.Background);
                                }
                            }
                            else
                            {
                                await EnsureItemTitleBeforeCoverAsync(item, album.Title, cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            if (SteamInstalledGameHelper.IsSteamGamePath(item.FileName))
                            {
                                await TryLoadSteamGameCoverAsync(item, cancellationToken).ConfigureAwait(false);
                            }
                            else if (HasLocalCoverFile(item.FileName) || HasLegacyEmbeddedCover(item.FileName))
                            {
                                await CoverLoader.EnsureCoverAsync(
                                        item,
                                        album.Title,
                                        MetadataService,
                                        EmulationCoverLoadRequest.LocalOnly,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else if (MetadataService != null)
                            {
                                await TryPopulateRomCoverAsync(item, album.Title, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to load album preview cover for '{item.Title}'.", ex);
                    }
                    finally
                    {
                        if (ReferenceEquals(LoadedAlbum, album))
                        {
                            try
                            {
                                await Dispatcher.UIThread.InvokeAsync(
                                    () => MarkRomItemCoverLoadComplete(item, album),
                                    DispatcherPriority.Background);
                            }
                            catch (Exception ex)
                            {
                                SLog.Warn($"Failed to clear preview cover loading state for '{item.Title}'.", ex);
                            }
                        }
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdatePreviewItems(album, rebuildStructure: false);
                    if (ReferenceEquals(LoadedAlbum, album))
                        NotifyAlbumCoverDisplayChanged(album);
                }, DispatcherPriority.Background);
            }
            finally
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => SyncAlbumPreviewLoadingState(album),
                        DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to clear loading state for album '{album.Title}'.", ex);
                }
            }
        }

        private bool CanAddRoms() =>
            SelectedAlbum != null && !IsSteamAlbum(SelectedAlbum);

        private bool ImportRomPaths(FolderMediaItem album, IEnumerable<string> paths)
        {
            bool addedAny = false;

            foreach (var path in paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (album.Children.Any(existing =>
                        string.Equals(existing.FileName, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                album.Children.Add(CreateRomItem(path, album));
                addedAny = true;
            }

            return addedAny;
        }

        private void FinalizeRomImport(FolderMediaItem album)
        {
            // Newly imported ROMs need a fresh metadata pass; clear the
            // session-scoped scanned marker so the queued scan actually runs.
            ClearAlbumCoverScanSessionState(album);

            if (ReferenceEquals(SelectedAlbum, album) && !ReferenceEquals(LoadedAlbum, album))
                LoadedAlbum = album;
            else if (ReferenceEquals(LoadedAlbum, album))
                ApplyFilter();

            SyncCurrentSectionEmulatorContext();

            if (album is EmulationAlbumItem emulationAlbum)
            {
                SyncAlbumTotalChildCount(emulationAlbum);
                UpdatePreviewItems(emulationAlbum);
                QueueAlbumPreviewCoverLoad(emulationAlbum);
            }

            QueueSelectedAlbumCoverScan(album);
            SaveSettings();
        }

        private static bool IsWiiUPackageFolder(string path)
            => WiiUInstalledGameHelper.IsInstalledGameFolder(path);

        private static IReadOnlyList<string> ScanFolderForRomPaths(string rootPath, IReadOnlyList<string> patterns)
            => ScanFolderForRomPaths(rootPath, null, patterns);

        private static IReadOnlyList<string> ScanFolderForRomPaths(string rootPath, string? consoleName, IReadOnlyList<string> patterns)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new Stack<string>();
            directories.Push(rootPath);

            while (directories.Count > 0)
            {
                var currentDirectory = directories.Pop();

                if (EmulationConsoleCatalog.SupportsFolderImport(consoleName) &&
                    Ps3InstalledGameHelper.IsInstalledGameFolder(currentDirectory))
                {
                    results.Add(currentDirectory);
                    continue;
                }

                if (EmulationConsoleCatalog.SupportsFolderImport(consoleName) &&
                    Ps4InstalledGameHelper.IsInstalledGameFolder(currentDirectory))
                {
                    results.Add(currentDirectory);
                    continue;
                }

                if (IsWiiUPackageFolder(currentDirectory))
                {
                    results.Add(currentDirectory);
                    continue;
                }

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
                    {
                        if (ShouldSkipFilesystemEntry(directory))
                            continue;

                        directories.Push(directory);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to enumerate subdirectories in '{currentDirectory}'.", ex);
                }

                foreach (var pattern in patterns)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(currentDirectory, pattern))
                        {
                            if (ShouldSkipFilesystemEntry(file))
                                continue;

                            results.Add(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to scan '{currentDirectory}' for pattern '{pattern}'.", ex);
                    }
                }
            }

            return CollapseDiscImageArtifacts(results)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyCollection<string> CollapseDiscImageArtifacts(IEnumerable<string> paths)
        {
            var distinctPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var pathSet = new HashSet<string>(distinctPaths, StringComparer.OrdinalIgnoreCase);
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in distinctPaths)
            {
                if (!IsDiscDescriptorFile(path))
                    continue;

                foreach (var referencedPath in GetReferencedDiscPaths(path))
                    referencedPaths.Add(referencedPath);
            }

            return distinctPaths
                .Where(path => !referencedPaths.Contains(path) || IsDiscDescriptorFile(path))
                .ToArray();
        }

        private static bool IsDiscDescriptorFile(string path)
            => DiscDescriptorExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<string> GetReferencedDiscPaths(string descriptorPath)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(descriptorPath);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to read disc descriptor '{descriptorPath}'.", ex);
                yield break;
            }

            var descriptorDirectory = Path.GetDirectoryName(descriptorPath);
            if (string.IsNullOrWhiteSpace(descriptorDirectory))
                yield break;

            var extension = Path.GetExtension(descriptorPath);
            if (extension.Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines)
                {
                    var referencedName = TryExtractCueReferencedFile(line);
                    if (string.IsNullOrWhiteSpace(referencedName))
                        continue;

                    var referencedPath = ResolveReferencedDiscPath(descriptorDirectory, referencedName);
                    if (!string.IsNullOrWhiteSpace(referencedPath))
                        yield return referencedPath;
                }

                yield break;
            }

            if (extension.Equals(".gdi", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines)
                {
                    var referencedName = TryExtractGdiReferencedFile(line);
                    if (string.IsNullOrWhiteSpace(referencedName))
                        continue;

                    var referencedPath = ResolveReferencedDiscPath(descriptorDirectory, referencedName);
                    if (!string.IsNullOrWhiteSpace(referencedPath))
                        yield return referencedPath;
                }

                yield break;
            }

            if (extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in lines)
                {
                    var referencedName = line.Trim();
                    if (string.IsNullOrWhiteSpace(referencedName) || referencedName.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    var referencedPath = ResolveReferencedDiscPath(descriptorDirectory, referencedName);
                    if (!string.IsNullOrWhiteSpace(referencedPath))
                        yield return referencedPath;
                }
            }
        }

        private static string? TryExtractCueReferencedFile(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                return null;

            var firstQuote = trimmed.IndexOf('"');
            var lastQuote = trimmed.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
                return trimmed[(firstQuote + 1)..lastQuote].Trim();

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1].Trim() : null;
        }

        private static string? TryExtractGdiReferencedFile(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !char.IsDigit(trimmed[0]))
                return null;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 5 ? parts[4].Trim().Trim('"') : null;
        }

        private static string? ResolveReferencedDiscPath(string directory, string referencedName)
        {
            if (string.IsNullOrWhiteSpace(referencedName))
                return null;

            var sanitized = referencedName.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(sanitized))
                return null;

            var combinedPath = Path.GetFullPath(Path.Combine(directory, sanitized));
            return File.Exists(combinedPath) ? combinedPath : null;
        }

        private static bool ShouldSkipFilesystemEntry(string path)
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ||
                   name.StartsWith(".", StringComparison.Ordinal) ||
                   name.StartsWith("._", StringComparison.Ordinal);
        }

        private void CancelAllAlbumPreviewCoverLoads()
        {
            _albumRowNeighborCoverLoadTimer?.Stop();

            lock (_albumPreviewCoverLoadGate)
            {
                foreach (var (album, cts) in _albumTilePreviewCtsMap.ToList())
                {
                    try
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to cancel preview cover load for '{album.Title}'.", ex);
                    }
                }

                _albumTilePreviewCtsMap.Clear();
                _activeAlbumPreviewCoverLoads.Clear();
            }
        }

        private void CancelAllAlbumCoverScans(FolderMediaItem? exceptAlbum = null)
        {
            foreach (var (album, cts) in _albumScanCtsMap.ToList())
            {
                if (exceptAlbum != null && ReferenceEquals(album, exceptAlbum))
                    continue;

                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to cancel emulation album cover scan for '{album.Title}'.", ex);
                }

                _albumScanCtsMap.Remove(album);
            }

            if (exceptAlbum == null || !_albumScanCtsMap.ContainsKey(exceptAlbum))
                ForceEndAlbumCoverScanUi();
        }

        private void CancelAllAlbumCoverScanDebounces(FolderMediaItem? exceptAlbum = null)
        {
            foreach (var (album, cts) in _albumCoverScanDebounceMap.ToList())
            {
                if (exceptAlbum != null && ReferenceEquals(album, exceptAlbum))
                    continue;

                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to cancel emulation cover scan debounce for '{album.Title}'.", ex);
                }

                _albumCoverScanDebounceMap.Remove(album);
            }
        }

        private void ForceEndAlbumCoverScanUi()
        {
            if (_activeAlbumCoverScans == 0)
                return;

            _activeAlbumCoverScans = 0;
            SetRomCarouselCoverStatus(null);
            OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
        }

        private void QueueLocalAlbumPresentation(FolderMediaItem? album)
        {
            if (album == null || album.Children.Count == 0)
                return;

            if (EmulationConsoleCatalog.UsesAutoLibrarySync(album.Title))
                return;

            if (!ReferenceEquals(LoadedAlbum, album))
                return;

            lock (_albumsWithLocalCoversHydrated)
                _albumsWithLocalCoversHydrated.Remove(album);

            StartAlbumCoverScan(album, AlbumCoverScanMode.LocalPresentation);
        }

        private void QueueSelectedAlbumCoverScan(FolderMediaItem? album)
        {
            if (album == null || album.Children.Count == 0)
                return;

            if (EmulationConsoleCatalog.UsesAutoLibrarySync(album.Title))
                return;

            if (ReferenceEquals(LoadedAlbum, album))
            {
                CancelAllAlbumPreviewCoverLoads();
                StartAlbumCoverScan(album, AlbumCoverScanMode.FullOnline);
                return;
            }

            try
            {
                if (_albumCoverScanDebounceMap.TryGetValue(album, out var existingDebounce))
                {
                    existingDebounce.Cancel();
                    existingDebounce.Dispose();
                    _albumCoverScanDebounceMap.Remove(album);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to cancel previous emulation cover scan debounce for '{album.Title}'.", ex);
            }

            var debounceCts = new CancellationTokenSource();
            _albumCoverScanDebounceMap[album] = debounceCts;
            var debounceToken = debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AlbumCoverScanDebounceMs, debounceToken).ConfigureAwait(false);
                    StartAlbumCoverScan(album, AlbumCoverScanMode.LocalPresentation);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (_albumCoverScanDebounceMap.TryGetValue(album, out var current) && ReferenceEquals(current, debounceCts))
                        _albumCoverScanDebounceMap.Remove(album);

                    debounceCts.Dispose();
                }
            }, debounceToken);
        }

        private enum AlbumCoverScanMode
        {
            LocalPresentation,
            FullOnline
        }

        private void StartAlbumCoverScan(
            FolderMediaItem album,
            AlbumCoverScanMode mode = AlbumCoverScanMode.FullOnline)
        {
            if (album.Children.Count == 0)
                return;

            if (mode == AlbumCoverScanMode.LocalPresentation &&
                _albumsWithLocalCoversHydrated.Contains(album) &&
                !ReferenceEquals(LoadedAlbum, album))
                return;

            CancelAllAlbumCoverScans(exceptAlbum: album);

            if (ReferenceEquals(LoadedAlbum, album) && mode == AlbumCoverScanMode.FullOnline)
                CancelAllAlbumPreviewCoverLoads();

            try
            {
                if (_albumScanCtsMap.TryGetValue(album, out var existingCts))
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                    _albumScanCtsMap.Remove(album);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to cancel previous emulation album cover scan for '{album.Title}'.", ex);
            }

            if (mode == AlbumCoverScanMode.FullOnline)
            {
                _albumsWithLocalCoversHydrated.Remove(album);
                _deferredCoverLookupPaths.Clear();
                _coverLookupAttemptedPaths.Clear();
                _coverLookupInFlightPaths.Clear();
                _coverLookupRetryCounts.Clear();
            }

            SLog.Debug(
                mode == AlbumCoverScanMode.LocalPresentation
                    ? $"Queueing local cover presentation for album '{album.Title}' with {album.Children.Count} items."
                    : $"Queueing emulation metadata and cover scan for album '{album.Title}' with {album.Children.Count} items.");

            var cts = new CancellationTokenSource();
            _albumScanCtsMap[album] = cts;
            var cancellationToken = cts.Token;
            _ = Task.Run(() => LoadAlbumCoversAsync(album, cancellationToken, mode), cancellationToken);
        }

        private AvaloniaList<MediaItem> RestoreAlbumRoms(string albumKey, string albumTitle, Bitmap? previewBitmap)
        {
            if (!_pendingAlbumRoms.TryGetValue(albumKey, out var savedItems) || savedItems.Count == 0)
            {
                // Backward compatibility: older save state might have centered on title keys.
                if (!string.IsNullOrWhiteSpace(albumTitle) &&
                    _pendingAlbumRoms.TryGetValue(albumTitle.Trim(), out var fallbackItems) &&
                    fallbackItems.Count > 0)
                {
                    savedItems = fallbackItems;
                }
            }

            if (savedItems == null || savedItems.Count == 0)
                return [];

            return new AvaloniaList<MediaItem>(
                savedItems.Select(item => CloneRomItem(item, albumTitle, previewBitmap)));
        }

        private Dictionary<string, List<MediaItem>> BuildAlbumRomMap()
        {
            return AlbumList
                .Where(album => album.Children.Count > 0 &&
                                !EmulationConsoleCatalog.UsesAutoLibrarySync(album.Title))
                .ToDictionary(
                    GetAlbumPersistenceKey,
                    album => album.Children
                        .Select(item => CloneRomItem(item, album.Title, null))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private async Task LoadAlbumCoversAsync(
            FolderMediaItem album,
            CancellationToken cancellationToken,
            AlbumCoverScanMode mode = AlbumCoverScanMode.FullOnline)
        {
            var localOnly = mode == AlbumCoverScanMode.LocalPresentation;

                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (localOnly)
                        {
                            PrepareAlbumItemsForLocalDisplay(album);
                            PrepareAlbumItemsForLocalCoverPaths(album);
                        }
                        else
                            PrepareAlbumItemsForCoverDisplay(album);
                    }, DispatcherPriority.Background);
                }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to prepare album '{album.Title}' for cover scan.", ex);
            }

            try
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(EnsureMetadataServiceSubscription, DispatcherPriority.Normal);
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Failed to resolve metadata service before cover scan for '{album.Title}'.", ex);
                }

                try
                {
                    if (localOnly)
                    {
                        await ApplyLocalCachedAlbumMetadataAsync(album, cancellationToken).ConfigureAwait(false);

                        if (ReferenceEquals(LoadedAlbum, album))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: true);
                            }, DispatcherPriority.Input);
                        }

                        await HydrateLocalAlbumCoversAsync(album, cancellationToken, skipTitleResolution: true)
                            .ConfigureAwait(false);
                        await FinishAlbumCoverLoadingStateAsync(album).ConfigureAwait(false);

                        if (ReferenceEquals(LoadedAlbum, album))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: false);
                                NotifyAlbumCoverDisplayChanged(album);
                            }, DispatcherPriority.Input);
                        }

                        lock (_albumsWithLocalCoversHydrated)
                        {
                            _albumsWithLocalCoversHydrated.Add(album);
                        }

                        return;
                    }

                    if (localOnly)
                        return;

                    if (MetadataService == null)
                    {
                        SLog.Warn($"MetadataService unavailable for album '{album.Title}'; online cover lookup skipped.");
                        await ApplyLocalCachedAlbumMetadataAsync(album, cancellationToken).ConfigureAwait(false);
                        await HydrateLocalAlbumCoversAsync(album, cancellationToken, skipTitleResolution: true)
                            .ConfigureAwait(false);
                        if (ReferenceEquals(LoadedAlbum, album))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: true);
                                NotifyAlbumCoverDisplayChanged(album);
                            }, DispatcherPriority.Input);
                        }

                        return;
                    }

                    await ApplyAlbumRomMetadataAsync(album, cancellationToken).ConfigureAwait(false);
                    await HydrateLocalAlbumCoversAsync(album, cancellationToken).ConfigureAwait(false);

                    BeginAlbumCoverScan();
                    int parallelism = GetCoverLoadParallelism(album);
                    await RunAlbumCoverLoadWorkersAsync(album, parallelism, cancellationToken).ConfigureAwait(false);

                    await FinishAlbumCoverLoadingStateAsync(album).ConfigureAwait(false);

                    if (ReferenceEquals(LoadedAlbum, album))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: true);
                            NotifyAlbumCoverDisplayChanged(album);
                        }, DispatcherPriority.Input);
                    }
                    else
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            UpdatePreviewItems(album as EmulationAlbumItem, rebuildStructure: false);
                        }, DispatcherPriority.Input);
                    }
                }
                catch (OperationCanceledException)
                {
                    SLog.Debug($"Emulation cover scan canceled for album '{album.Title}'.");
                    await FinishAlbumCoverLoadingStateAsync(album).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SLog.Warn($"Emulation cover scan failed for album '{album.Title}'.", ex);
                }
            }
            finally
            {
                CancelCoverDisplayNotifyDebounce();
                EndAlbumCoverScan();
            }
        }

        private async Task HydrateLocalAlbumCoversAsync(
            FolderMediaItem album,
            CancellationToken cancellationToken,
            bool skipTitleResolution = false)
        {
            List<MediaItem> candidates;
            try
            {
                candidates = await Dispatcher.UIThread.InvokeAsync(() =>
                    album.Children
                        .Where(item => NeedsCoverLookup(item, album))
                        .Where(item => HasLocalCoverFile(item.FileName) || HasLegacyEmbeddedCover(item.FileName))
                        .ToList(),
                    DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to collect local cover hydrate targets for '{album.Title}'.", ex);
                return;
            }

            if (candidates.Count == 0)
                return;

            int parallelism = GetCoverLoadParallelism(album);
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken
                },
                async (item, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(LoadedAlbum, album))
                        return;

                    try
                    {
                        if (!skipTitleResolution &&
                            !SectionHandlers.GenericAlbumNormalizer.IsRomMetadataAlreadyScanned(item.FileName))
                        {
                            await EnsureItemTitleBeforeCoverAsync(item, album.Title, ct).ConfigureAwait(false);
                        }

                        if (ShouldRetryCoverLookupAfterTitleImprovement(item))
                            return;

                        await CoverLoader.EnsureCoverAsync(
                                item,
                                album.Title,
                                MetadataService,
                                EmulationCoverLoadRequest.LocalOnly,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to hydrate local cover for '{item.Title}' in album '{album.Title}'.", ex);
                    }
                }).ConfigureAwait(false);
        }

        private void NotifyAlbumCoverDisplayChanged(FolderMediaItem album, bool forceFullRescan = false)
        {
            if (!ReferenceEquals(LoadedAlbum, album))
                return;

            _albumCoverDisplayRevision++;
            if (forceFullRescan)
                _albumCoverDisplayNeedsFullRescan = true;
            if (!IsActive)
            {
                _albumCoverDisplayNotifyPending = true;
                return;
            }

            ScheduleThrottledAlbumCoverDisplayNotify(album);
        }

        private void FlushDeferredAlbumCoverDisplayNotification()
        {
            if (!_albumCoverDisplayNotifyPending)
                return;

            _albumCoverDisplayNotifyPending = false;
            OnPropertyChanged(nameof(AlbumCoverDisplayRevision));
        }

        private void RefreshAlbumCoverAfterMetadataSave(string? savedPath)
        {
            if (string.IsNullOrWhiteSpace(savedPath) || MediaCoverPaths.UsesMetadataImageCache(savedPath))
                return;

            var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(savedPath);
            if (string.IsNullOrWhiteSpace(romPath))
                return;

            var album = GetBrowseAlbum();
            if (album is not { Children.Count: > 0 })
                return;

            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                var target = album.Children.FirstOrDefault(item =>
                    EmulationCoverCacheHelper.RomPathsShareCache(item.FileName, romPath));
                if (target == null)
                    return;

                target.CoverBitmap = null;
                if (EmulationCoverCacheHelper.HasCover(romPath))
                {
                    target.LocalCoverPath = EmulationCoverCacheHelper.GetCoverCachePath(romPath);
                    target.CoverFound = true;

                    var bytes = EmulationCoverCacheHelper.TryReadCoverBytes(romPath);
                    if (bytes is { Length: > 0 })
                    {
                        using var ms = new MemoryStream(bytes);
                        target.CoverBitmap = Bitmap.DecodeToWidth(ms, 384);
                    }
                }

                if (!string.Equals(target.FileName, romPath, StringComparison.Ordinal))
                    target.FileName = romPath;

                NotifyAlbumCoverDisplayChanged(album);
            }, DispatcherPriority.Normal);
        }

        private MediaItem CreateRomItem(string filePath, FolderMediaItem album)
        {
            var title = TryReadCachedMetadataTitle(filePath);
            if (string.IsNullOrWhiteSpace(title))
                title = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(filePath));

            var hasLocalCover = HasLocalCoverFile(filePath);
            var item = new MediaItem
            {
                FileName = filePath,
                Title = title,
                Album = album.Title,
                CoverBitmap = album.CoverBitmap,
                LocalCoverPath = hasLocalCover ? EmulationCoverCacheHelper.GetCoverCachePath(filePath) : null
            };

            if (IsRomCoverAlreadyScanned(filePath))
                item.MetadataProcessed = true;

            return item;
        }

        private async Task ApplyXbox360TitlesFromDatabaseAsync(FolderMediaItem album, CancellationToken cancellationToken = default)
        {
            if (album == null || !string.Equals(album.Title, "Xbox 360", StringComparison.OrdinalIgnoreCase) || album.Children.Count == 0)
                return;

            var metadataService = _xbox360MetadataService;
            if (metadataService == null)
                return;

            foreach (var item in album.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                    continue;

                var metadata = await Task.Run(() => metadataService.TryReadGameMetadata(item.FileName), cancellationToken).ConfigureAwait(false);
                var cachedTitle = TryReadCachedMetadataTitle(item.FileName);

                var resolvedTitle = !string.IsNullOrWhiteSpace(metadata?.Title)
                    ? metadata!.Title
                    : cachedTitle;

                if (string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    if (!string.IsNullOrWhiteSpace(metadata?.TitleId) || !string.IsNullOrWhiteSpace(metadata?.MediaId))
                        await PersistXbox360LocalMetadataAsync(item, item.Title ?? string.Empty, metadata?.TitleId, metadata?.MediaId, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                var shouldUpdateTitle = string.IsNullOrWhiteSpace(item.Title) ||
                                        !string.Equals(item.Title.Trim(), resolvedTitle.Trim(), StringComparison.Ordinal);

                if (shouldUpdateTitle)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => item.Title = resolvedTitle, DispatcherPriority.Background);
                }

                if (!string.IsNullOrWhiteSpace(metadata?.TitleId) || !string.IsNullOrWhiteSpace(metadata?.MediaId) || shouldUpdateTitle)
                {
                    await PersistXbox360LocalMetadataAsync(item, resolvedTitle, metadata?.TitleId, metadata?.MediaId, cancellationToken).ConfigureAwait(false);
                }

            }
        }

        private static bool TryReadCachedXbox360Ids(string filePath, out string? titleId, out string? mediaId)
        {
            titleId = null;
            mediaId = null;

            try
            {
                var cachePath = GetLocalMetadataCachePath(filePath);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (metadata == null)
                    return false;

                titleId = metadata.Xbox360TitleId;
                mediaId = metadata.Xbox360MediaId;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Task PersistXbox360LocalMetadataAsync(MediaItem item, string title, string? titleId, string? mediaId, CancellationToken cancellationToken)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.FileName) ||
                (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(titleId) && string.IsNullOrWhiteSpace(mediaId)))
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachePath = GetLocalMetadataCachePath(item.FileName);
                var existing = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (!string.IsNullOrWhiteSpace(title))
                    existing.Title = title;
                if (string.IsNullOrWhiteSpace(existing.Album))
                    existing.Album = item.Album ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(titleId))
                    existing.Xbox360TitleId = titleId;
                if (!string.IsNullOrWhiteSpace(mediaId))
                    existing.Xbox360MediaId = mediaId;

                BinaryMetadataHelper.SaveMetadata(cachePath, existing);
            }, cancellationToken);
        }

        private static Task PersistPsxGameIdToLocalMetadataAsync(MediaItem item, string gameId)
        {
            if (item == null ||
                string.IsNullOrWhiteSpace(item.FileName) ||
                string.IsNullOrWhiteSpace(gameId))
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                var cachePath = GetLocalMetadataCachePath(item.FileName);
                var existing = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
                if (string.IsNullOrWhiteSpace(existing.PsXTitleId))
                    existing.PsXTitleId = gameId;
                if (string.IsNullOrWhiteSpace(existing.Album))
                    existing.Album = item.Album ?? string.Empty;

                BinaryMetadataHelper.SaveMetadata(cachePath, existing);
            });
        }

        private static string? TryReadCachedMetadataTitle(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            try
            {
                var cachePath = GetLocalMetadataCachePath(filePath);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                var title = metadata?.Title;
                return string.IsNullOrWhiteSpace(title)
                    ? null
                    : title.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string GetLocalMetadataCachePath(string? filePath) =>
            NintendoDiscMetadataHelper.GetMetadataCachePath(filePath);

        private sealed class Xbox360TitleEntry
        {
            [JsonPropertyName("titleid")]
            public string? TitleId { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }

        private static MediaItem CloneRomItem(MediaItem source, string? albumTitle, Bitmap? previewBitmap)
        {
            var fileName = source.FileName;
            return new MediaItem
            {
                FileName = fileName,
                Title = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(string.IsNullOrWhiteSpace(source.Title)
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : source.Title),
                Artist = source.Artist,
                Album = string.IsNullOrWhiteSpace(albumTitle) ? source.Album : albumTitle,
                Track = source.Track,
                Year = source.Year,
                Duration = source.Duration,
                Lyrics = source.Lyrics,
                Genre = source.Genre,
                Comment = source.Comment,
                LocalCoverPath = source.LocalCoverPath,
                CoverFound = source.CoverFound && source.CoverBitmap != null && previewBitmap != null &&
                             !ReferenceEquals(source.CoverBitmap, previewBitmap),
                VideoUrl = source.VideoUrl,
                CoverBitmap = previewBitmap
            };
        }

        private async Task ApplyLocalCachedAlbumMetadataAsync(FolderMediaItem album, CancellationToken cancellationToken)
        {
            if (album.Children.Count == 0)
                return;

            var items = await Dispatcher.UIThread.InvokeAsync(
                () => album.Children.ToList(),
                DispatcherPriority.Background);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(item.FileName))
                    continue;

                var previousTitle = item.Title;
                string? resolvedTitle = TryReadCachedMetadataTitle(item.FileName);
                if (string.IsNullOrWhiteSpace(resolvedTitle) ||
                    SectionHandlers.GenericAlbumNormalizer.NeedsRomTitleImprovement(
                        item.FileName,
                        album.Title,
                        resolvedTitle ?? item.Title))
                {
                    resolvedTitle = SectionHandlers.GenericAlbumNormalizer.ResolveRomTitle(
                        item.FileName,
                        album.Title,
                        item.Title);
                }

                if (string.IsNullOrWhiteSpace(resolvedTitle) ||
                    string.Equals(previousTitle, resolvedTitle, StringComparison.Ordinal))
                {
                    continue;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TryInvalidateAutoFetchedCoverAfterTitleChange(item, previousTitle, resolvedTitle);
                    item.Title = resolvedTitle;

                    if (ReferenceEquals(LoadedAlbum, album) && !string.IsNullOrWhiteSpace(SearchText?.Trim()))
                        ApplyFilter();
                }, DispatcherPriority.Background);
            }
        }

        private async Task ApplyAlbumRomMetadataAsync(FolderMediaItem album, CancellationToken cancellationToken)
        {
            if (album.Children.Count == 0)
                return;

            // Avoid re-scanning the same album multiple times in a session
            // (album selection can fire repeatedly while the user navigates).
            lock (_albumsWithMetadataScanned)
            {
                if (!_albumsWithMetadataScanned.Add(album))
                    return;
            }

            var items = await Dispatcher.UIThread.InvokeAsync(
                () => album.Children.ToList(),
                DispatcherPriority.Background);

            const int UiBatchSize = 8;
            const int TitleResolveParallelism = 6;
            var pendingUpdates = new List<(MediaItem item, string title)>(UiBatchSize);
            var pendingLock = new object();

            async Task FlushAsync()
            {
                (MediaItem item, string title)[] snapshot;
                lock (pendingLock)
                {
                    if (pendingUpdates.Count == 0)
                        return;

                    snapshot = pendingUpdates.ToArray();
                    pendingUpdates.Clear();
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var (item, title) in snapshot)
                    {
                        var previousTitle = item.Title;
                        TryInvalidateAutoFetchedCoverAfterTitleChange(item, previousTitle, title);
                        item.Title = title;

                        if (NeedsCoverLookup(item, album))
                        {
                            item.CoverBitmap = album.CoverBitmap;
                            item.IsLoadingCover = true;
                        }
                    }

                    if (ReferenceEquals(LoadedAlbum, album) && !string.IsNullOrWhiteSpace(SearchText?.Trim()))
                        ApplyFilter();
                }, DispatcherPriority.Background);
            }

            try
            {
                await Parallel.ForEachAsync(
                    items,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = TitleResolveParallelism,
                        CancellationToken = cancellationToken
                    },
                    async (item, ct) =>
                    {
                        if (string.IsNullOrWhiteSpace(item.FileName))
                            return;

                        var previousTitle = item.Title;
                        string? resolvedTitle;

                        if (SectionHandlers.GenericAlbumNormalizer.NeedsRomTitleImprovement(
                                item.FileName,
                                album.Title,
                                previousTitle))
                        {
                            resolvedTitle = await SectionHandlers.GenericAlbumNormalizer.EnsureRomTitleResolvedAsync(
                                    item.FileName,
                                    album.Title,
                                    previousTitle,
                                    ct)
                                .ConfigureAwait(false);
                        }
                        else if (SectionHandlers.GenericAlbumNormalizer.IsRomMetadataAlreadyScanned(item.FileName))
                        {
                            resolvedTitle = TryReadCachedMetadataTitle(item.FileName);
                            if (string.IsNullOrWhiteSpace(resolvedTitle))
                            {
                                resolvedTitle = SectionHandlers.GenericAlbumNormalizer.ResolveRomTitle(
                                    item.FileName,
                                    album.Title,
                                    item.Title);
                            }
                        }
                        else
                        {
                            resolvedTitle = await SectionHandlers.GenericAlbumNormalizer.EnsureRomTitleResolvedAsync(
                                    item.FileName,
                                    album.Title,
                                    previousTitle,
                                    ct)
                                .ConfigureAwait(false);
                        }

                        if (string.IsNullOrWhiteSpace(resolvedTitle) ||
                            string.Equals(previousTitle, resolvedTitle, StringComparison.Ordinal))
                        {
                            return;
                        }

                        lock (pendingLock)
                        {
                            pendingUpdates.Add((item, resolvedTitle));
                            if (pendingUpdates.Count < UiBatchSize)
                                return;
                        }

                        await FlushAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);

                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await FlushAsync().ConfigureAwait(false);
                }
                catch (Exception logEx) { SLog.Warn("Non-critical error", logEx); }
                throw;
            }
        }

        private async Task FinishAlbumCoverLoadingStateAsync(FolderMediaItem album)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in album.Children)
                {
                    if (item.IsLoadingCover)
                        MarkRomItemCoverLoadComplete(item, album);
                }

                if (album is EmulationAlbumItem emulationAlbum)
                    emulationAlbum.IsLoadingCover = false;
            }, DispatcherPriority.Background);
        }

        private bool AlbumNeedsOnlineCoverLookup(FolderMediaItem album) =>
            album.Children.Any(item =>
                NeedsCoverLookup(item, album) &&
                !HasLocalCoverFile(item.FileName) &&
                !HasLegacyEmbeddedCover(item.FileName));

        private static bool NeedsCoverLookup(MediaItem item, FolderMediaItem album)
        {
            if (item.CoverBitmap != null && !ReferenceEquals(item.CoverBitmap, album.CoverBitmap))
                return false;

            if (SteamInstalledGameHelper.IsSteamGamePath(item.FileName))
            {
                var iconPath = item.LocalCoverPath;
                if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                    iconPath = SteamInstalledGameHelper.GetPreferredIconPath(item.FileName);

                return !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);
            }

            if (HasLocalCoverFile(item.FileName) || HasLegacyEmbeddedCover(item.FileName))
                return true;

            if (IsOnlineCoverLookupExhausted(item.FileName))
                return ShouldRetryCoverLookupAfterTitleImprovement(item);

            return true;
        }

        private async Task EnsureItemTitleBeforeCoverAsync(
            MediaItem item,
            string? albumTitle,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            var previousTitle = item.Title;
            var resolved = await SectionHandlers.GenericAlbumNormalizer.EnsureRomTitleResolvedAsync(
                    item.FileName,
                    albumTitle,
                    item.Title,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolved) ||
                string.Equals(previousTitle, resolved, StringComparison.Ordinal))
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TryInvalidateAutoFetchedCoverAfterTitleChange(item, previousTitle, resolved);
                item.Title = resolved;
            }, DispatcherPriority.Background);
        }

        private static void TryInvalidateAutoFetchedCoverAfterTitleChange(
            MediaItem item,
            string? previousTitle,
            string resolvedTitle)
        {
            if (string.IsNullOrWhiteSpace(item.FileName) || string.IsNullOrWhiteSpace(resolvedTitle))
                return;

            var previousNorm = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(previousTitle);
            var resolvedNorm = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(resolvedTitle);
            if (string.Equals(previousNorm, resolvedNorm, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var cachePath = EmulationCoverCacheHelper.GetMetadataCachePath(item.FileName);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (metadata?.UserEdited == true)
                    return;

                if (EmulationCoverCacheHelper.HasCover(item.FileName))
                    EmulationCoverCacheHelper.TryDeleteCoverSidecar(item.FileName);

                metadata ??= new CustomMetadata();
                metadata.CoverLookupExhausted = false;
                metadata.CoverScanned = false;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

                item.CoverFound = false;
                item.LocalCoverPath = null;
                item.CoverBitmap = null;
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to invalidate stale cover after title change for '{item.FileName}'.", ex);
            }
        }

        private static bool ShouldRetryCoverLookupAfterTitleImprovement(MediaItem item) =>
            !NintendoDiscMetadataHelper.IsFilenameLikeTitle(item.Title, item.FileName);

        private static void TryInvalidateStaleCoverLookupState(MediaItem item)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
                return;

            if (!ShouldRetryCoverLookupAfterTitleImprovement(item))
                return;

            try
            {
                var cachePath = GetLocalMetadataCachePath(item.FileName);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                if (metadata?.CoverLookupExhausted != true)
                    return;

                metadata.CoverLookupExhausted = false;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to clear stale cover lookup state for '{item.FileName}'.", ex);
            }
        }

        private static bool NeedsPreviewCoverHydration(MediaItem item, FolderMediaItem album) =>
            item.CoverBitmap == null || ReferenceEquals(item.CoverBitmap, album.CoverBitmap);

        private static bool IsRomCoverAlreadyScanned(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            return HasLocalCoverFile(filePath) || IsOnlineCoverLookupExhausted(filePath);
        }

        private static bool IsOnlineCoverLookupExhausted(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(GetLocalMetadataCachePath(filePath));
                return metadata?.CoverLookupExhausted == true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasLocalCoverFile(string? filePath) =>
            !string.IsNullOrWhiteSpace(filePath) && EmulationCoverCacheHelper.HasCover(filePath);

        private static bool HasLegacyEmbeddedCover(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            var metaPath = EmulationCoverCacheHelper.GetMetadataCachePath(filePath);
            if (!File.Exists(metaPath))
                return false;

            try
            {
                var metadata = BinaryMetadataHelper.LoadMetadata(metaPath);
                return metadata?.Images?.Any(image =>
                    image.Kind == TagImageKind.Cover && image.Data is { Length: > 0 }) == true;
            }
            catch
            {
                return false;
            }
        }

        private object? _coverItemsSourceRef;
        private readonly MediaItem _emptyAlbumCoverPlaceholder = new()
        {
            Title = string.Empty,
            Artist = string.Empty,
            Album = string.Empty
        };

        private void ApplyFilter()
        {
            var album = GetBrowseAlbum();
            var source = album?.Children;
            bool isNewAlbumSource = !ReferenceEquals(source, _coverItemsSourceRef);

            if (source == null || source.Count == 0)
            {
                _coverItemsSourceRef = source;
                ApplyEmptyBrowseAlbumPresentation(album);
                RefreshActiveAlbumState();
                return;
            }

            if (isNewAlbumSource)
            {
                CarouselSliderPreview = null;
                PointedIndex = -1;
                SelectedIndex = 0;
                _coverItemsSourceRef = source;
                CompositionViewportState.VisibleCenterIndex = -1;
            }

            var query = SearchText?.Trim();
            MediaItem? preferredItem = null;
            int currentSelectedIndex = GetRoundedSelectedIndex(SelectedIndex);

            if (!isNewAlbumSource)
            {
                if (currentSelectedIndex >= 0 && currentSelectedIndex < CoverItems.Count)
                    preferredItem = CoverItems[currentSelectedIndex];

                preferredItem ??= HighlightedItem;
            }

            CoverItems = string.IsNullOrWhiteSpace(query)
                ? source
                : new AvaloniaList<MediaItem>(source.Where(item => Matches(item, query)));

            if (CoverItems.Count == 0)
            {
                CarouselSliderPreview = null;
                ClearPendingCarouselHighlight();
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = CreateEmptyMediaItem();
                IsNoAlbumLoadedVisible = true;
                RefreshActiveAlbumState();
                return;
            }

            int nextIndex = isNewAlbumSource
                ? 0
                : preferredItem != null ? CoverItems.IndexOf(preferredItem) : -1;
            if (nextIndex < 0 || nextIndex >= CoverItems.Count)
                nextIndex = Math.Clamp(currentSelectedIndex, 0, CoverItems.Count - 1);

            if (!isNewAlbumSource)
                CarouselSliderPreview = null;

            SelectedIndex = nextIndex;
            if (isNewAlbumSource || PointedIndex >= CoverItems.Count)
                PointedIndex = -1;

            HighlightedItem = CoverItems[nextIndex];
            IsNoAlbumLoadedVisible = false;
            RefreshActiveAlbumState();
        }

        private void ApplyEmptyBrowseAlbumPresentation(FolderMediaItem? album)
        {
            CarouselSliderPreview = null;
            ClearPendingCarouselHighlight();

            if (album?.CoverBitmap != null)
            {
                _emptyAlbumCoverPlaceholder.CoverBitmap = album.CoverBitmap;
                _emptyAlbumCoverPlaceholder.LocalCoverPath = album.LocalCoverPath;
                _emptyAlbumCoverPlaceholder.FileName = null;
                _emptyAlbumCoverPlaceholder.Title = string.Empty;
                _emptyAlbumCoverPlaceholder.Artist = string.Empty;
                _emptyAlbumCoverPlaceholder.Album = album.Title ?? string.Empty;

                CoverItems = new AvaloniaList<MediaItem> { _emptyAlbumCoverPlaceholder };
                SelectedIndex = 0;
                PointedIndex = -1;
                HighlightedItem = _emptyAlbumCoverPlaceholder;
                IsNoAlbumLoadedVisible = false;
                return;
            }

            CoverItems = [];
            SelectedIndex = -1;
            PointedIndex = -1;
            HighlightedItem = CreateEmptyMediaItem();
            IsNoAlbumLoadedVisible = true;
        }

        private void ClearPendingCarouselHighlight()
        {
            _pendingHighlightedCarouselIndex = -1;
            _carouselHighlightDebounceTimer?.Stop();
        }

        private void RefreshActiveAlbumState()
        {
            OnPropertyChanged(nameof(HasActiveAlbumItems));
            OnPropertyChanged(nameof(ShowEmptyActiveAlbumHint));
            OnPropertyChanged(nameof(CanShowRenderOptions));

            if (!CanShowRenderOptions && IsRenderOptionsOpen)
                IsRenderOptionsOpen = false;

            ClearAlbumCommand.NotifyCanExecuteChanged();
            ClearAlbumCacheCommand.NotifyCanExecuteChanged();
            OpenMetadataCommand.NotifyCanExecuteChanged();
            RefreshCoverAndTitleCommand.NotifyCanExecuteChanged();
        }

        private void RestoreCarouselAfterMetadataClosed()
        {
            var album = GetBrowseAlbum();
            if (album?.Children is not { Count: > 0 })
            {
                ApplyFilter();
                RefreshActiveAlbumState();
                OpenMetadataCommand.NotifyCanExecuteChanged();
                return;
            }

            ApplyFilter();

            int index = GetRoundedSelectedIndex(SelectedIndex);
            if (index < 0 || index >= CoverItems.Count)
                index = PointedIndex >= 0 && PointedIndex < CoverItems.Count ? PointedIndex : 0;

            if (CoverItems.Count > 0)
            {
                SelectedIndex = index;
                HighlightedItem = CoverItems[index];
                IsNoAlbumLoadedVisible = false;
            }

            RefreshActiveAlbumState();
            NotifyAlbumCoverDisplayChanged(album);
            OpenMetadataCommand.NotifyCanExecuteChanged();
            RefreshCoverAndTitleCommand.NotifyCanExecuteChanged();
        }

        private void SyncSelectedAlbumIndexFromAlbum(FolderMediaItem? album)
        {
            if (_isSyncingAlbumSelection)
                return;

            int nextIndex = album == null ? -1 : AlbumList.IndexOf(album);
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

        private void AlbumList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (IsPrepared && e.Action == NotifyCollectionChangedAction.Move)
                SaveSettings();
        }

        private void ApplySavedAlbumOrder()
        {
            if (AlbumList.Count <= 1)
                return;

            var reordered = OrderAlbums(AlbumList);
            if (reordered.Count == 0)
                return;

            AlbumList.Clear();
            AlbumList.AddRange(reordered);
        }

        private void ApplySavedAlbumOrder(List<EmulationAlbumItem> albums)
        {
            if (albums.Count <= 1)
                return;

            var reordered = OrderAlbums(albums);
            if (reordered.Count == 0)
                return;

            albums.Clear();
            foreach (var album in reordered)
                albums.Add((EmulationAlbumItem)album);
        }

        private List<FolderMediaItem> OrderAlbums(IEnumerable<FolderMediaItem> albums)
        {
            var source = albums as IList<FolderMediaItem> ?? albums.ToList();
            if (_pendingAlbumOrder.Count == 0 || source.Count <= 1)
                return [];

            var orderMap = _pendingAlbumOrder
                .Select((key, index) => (key, index))
                .GroupBy(entry => entry.key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

            return source
                .OrderBy(album =>
                    orderMap.TryGetValue(GetAlbumOrderKey(album), out var index)
                        ? index
                        : int.MaxValue)
                .ThenBy(album => album.Title, StringComparer.OrdinalIgnoreCase)
                .Cast<FolderMediaItem>()
                .ToList();
        }
    }
}
