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

            var album = LoadedAlbum ?? SelectedAlbum;
            if (album == null)
                return;

            var handler = SettingsViewModel?.GetConfiguredEmulatorHandler(album.Title);
            var launcherPath = handler?.LauncherPath;
            if (handler == null || !handler.IsLauncherPathValid(launcherPath))
                return;

            var launchSettings = SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(album.Title);
            var launchRequest = new PendingEmulatorLaunchRequest(
                album.Title ?? string.Empty,
                item.Title ?? Path.GetFileNameWithoutExtension(item.FileName),
                handler,
                item.FileName,
                launchSettings);

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
            WriteSetting(section, nameof(DisableVSync), DisableVSync);
            WriteSetting(section, nameof(LowLatencyCapture), LowLatencyCapture);
            WriteSetting(section, nameof(FrameGenerationMode), (int)FrameGenerationMode);
            WriteSetting(section, nameof(RenderBrightness), RenderBrightness);
            WriteSetting(section, nameof(RenderSaturation), RenderSaturation);
            WriteSetting(section, nameof(SelectedShaderPath), SelectedShaderPath);

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
                var previewBitmap = LoadBitmap(imagePath);
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
                UpdatePreviewItems(AlbumList.Last() as EmulationAlbumItem);
            }

            ApplySavedAlbumOrder();
        }

        private async Task InitializeAlbumsAsync()
        {
            var albums = await Task.Run(() =>
            {
                var result = new List<EmulationAlbumItem>();
                foreach (var imagePath in FindConsoleImagePaths())
                {
                    var title = GetConsoleTitle(imagePath);
                    var previewBitmap = LoadBitmap(imagePath);
                    var albumKey = GetAlbumPersistenceKeyFromPath(imagePath, title);

                    var album = new EmulationAlbumItem
                    {
                        Title = title,
                        Album = title,
                        FileName = imagePath,
                        LocalCoverPath = imagePath,
                        CoverBitmap = previewBitmap,
                        Children = RestoreAlbumRoms(albumKey, title, previewBitmap)
                    };

                    result.Add(album);
                }

                return result;
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AlbumList.Clear();
                foreach (var album in albums)
                {
                    AlbumList.Add(album);
                    UpdatePreviewItems(album);
                }

                ApplySavedAlbumOrder();

                foreach (var album in AlbumList)
                {
                    if (album.Children.Count > 0)
                    {
                        QueueSelectedAlbumCoverScan(album);
                    }
                }

                SelectedAlbum = AlbumList.FirstOrDefault();
                LoadedAlbum = null;
                UpdateCurrentEmulatorHandlerForSelection(GetActiveEmulationAlbum());
                _sharedAlbumCache = new AvaloniaList<FolderMediaItem>(AlbumList);
                IsPrepared = true;
                _isPreparing = false;
                ApplyFilter();
            });
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

            var album = LoadedAlbum ?? SelectedAlbum;
            if (album == null)
                return;

            if (album.Children.Remove(target))
            {
                ApplyFilter();
                UpdatePreviewItems(album as EmulationAlbumItem);
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
            var target = parameter switch
            {
                MediaItem mi => mi,
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => HighlightedItem
            };

            if (target == null || MetadataService == null)
                return;

            if (MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;

            await MetadataService.LoadMetadataForItemAsync(target);
        }

        [RelayCommand(CanExecute = nameof(CanClearLoadedAlbum))]
        private async Task ClearAlbumCache()
        {
            var album = SelectedAlbum;
            if (album == null)
                return;

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
            lock (_albumsWithMetadataScanned)
            {
                _albumsWithMetadataScanned.Remove(album);
            }
            ApplyFilter();
            SaveSettings();
            return Task.CompletedTask;
        }

        private bool CanOpenMetadata(object? parameter) => HasActiveAlbumItems;

        private bool CanClearLoadedAlbum() => HasActiveAlbumItems;

        private static IReadOnlyList<string> FindConsoleImagePaths()
        {
            foreach (var directory in EnumerateConsoleAssetDirectories())
            {
                if (!Directory.Exists(directory))
                    continue;

                var files = Directory
                    .EnumerateFiles(directory)
                    .Where(IsSupportedConsoleImage)
                    .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
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

        private void UpdatePreviewItems(EmulationAlbumItem? album)
        {
            if (album == null)
                return;

            bool useFirstItemCover = SettingsViewModel?.EmulationUseFirstItemCover == true;
            Bitmap? topCover = album.CoverBitmap;
            var firstChild = album.Children.FirstOrDefault();

            if (useFirstItemCover && firstChild != null)
            {
                topCover = firstChild.CoverBitmap ?? album.CoverBitmap;
            }

            var previewItems = new AvaloniaList<MediaItem>();
            foreach (var child in album.Children)
            {
                if (child == firstChild)
                    continue;

                if (child.CoverBitmap == null)
                    continue;

                if (topCover != null && ReferenceEquals(child.CoverBitmap, topCover))
                    continue;

                previewItems.Add(child);
                if (previewItems.Count >= 2)
                    break;
            }

            previewItems.Add(new MediaItem
            {
                Title = album.Title,
                Album = album.Title,
                FileName = album.FileName,
                CoverBitmap = topCover
            });

            album.PreviewItems = previewItems;
        }

        private bool CanAddRoms() => SelectedAlbum != null;

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
            lock (_albumsWithMetadataScanned)
            {
                _albumsWithMetadataScanned.Remove(album);
            }

            if (ReferenceEquals(LoadedAlbum, album))
                ApplyFilter();

            UpdatePreviewItems(album as EmulationAlbumItem);
            QueueSelectedAlbumCoverScan(album);
            SaveSettings();
        }

        private static bool IsWiiUPackageFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                return Directory.Exists(Path.Combine(path, "code")) &&
                       Directory.Exists(Path.Combine(path, "content")) &&
                       Directory.Exists(Path.Combine(path, "meta"));
            }
            catch (Exception ex)
            {
                SLog.Debug($"Failed to inspect Wii U package folder '{path}'.", ex);
                return false;
            }
        }

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

        private void QueueSelectedAlbumCoverScan(FolderMediaItem? album)
        {
            if (album == null || album.Children.Count == 0)
                return;

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

            SLog.Debug($"Queueing emulation metadata and cover scan for album '{album.Title}' with {album.Children.Count} items.");

            var cts = new CancellationTokenSource();
            _albumScanCtsMap[album] = cts;
            var cancellationToken = cts.Token;
            _ = Task.Run(() => LoadAlbumCoversAsync(album, cancellationToken), cancellationToken);
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
                .Where(album => album.Children.Count > 0)
                .ToDictionary(
                    GetAlbumPersistenceKey,
                    album => album.Children.Select(item => CloneRomItem(item, album.Title, null)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private async Task LoadAlbumCoversAsync(FolderMediaItem album, CancellationToken cancellationToken)
        {
            // Kick off the ROM metadata pass in parallel so cover loading
            // (which is what the user actually sees) is never blocked by
            // expensive disc/ROM header inspection. The metadata pass runs
            // relaxed in the background and updates titles as it goes.
            var metadataTask = Task.Run(
                () => ApplyAlbumRomMetadataAsync(album, cancellationToken),
                cancellationToken);

            try
            {
                if (MetadataService == null)
                {
                    await metadataTask.ConfigureAwait(false);
                    return;
                }

                var itemsToLoad = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var candidates = album.Children
                        .Select((item, index) => (item, index))
                        .Where(pair => NeedsCoverLookup(pair.item, album))
                        .ToList();

                    int centerIndex = GetRoundedSelectedIndex(SelectedIndex);
                    return candidates
                        .OrderBy(pair => Math.Abs(pair.index - centerIndex))
                        .Select(pair => pair.item)
                        .ToList();
                }, DispatcherPriority.Background);
                SLog.Debug($"Starting emulation cover scan for album '{album.Title}'. {itemsToLoad.Count} roms need lookup.");

                const int coverLookupDelayMs = 80;
                foreach (var item in itemsToLoad)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var populated = await MetadataService.TryPopulateCoverFromLocalMetadataOrGoogleAsync(item, album.Title, cancellationToken);
                    SLog.Debug(
                        populated
                            ? $"Auto cover resolved for rom '{item.Title}' in album '{album.Title}'."
                            : $"Auto cover not found for rom '{item.Title}' in album '{album.Title}'.");

                    try
                    {
                        await Task.Delay(coverLookupDelayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                // Update preview tiles once per album scan pass to avoid flickering from incremental updates.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdatePreviewItems(album as EmulationAlbumItem);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                SLog.Debug($"Emulation cover scan canceled for album '{album.Title}'.");
            }
            catch (Exception ex)
            {
                SLog.Warn($"Emulation cover scan failed for album '{album.Title}'.", ex);
            }

            try
            {
                await metadataTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cover scan and metadata scan share the same token; ignore.
            }
            catch (Exception ex)
            {
                SLog.Warn($"Emulation ROM metadata scan failed for album '{album.Title}'.", ex);
            }
        }

        private static MediaItem CreateRomItem(string filePath, FolderMediaItem album)
        {
            var title = SectionHandlers.GenericAlbumNormalizer.ResolveRomTitle(filePath, album.Title);
            if (string.IsNullOrWhiteSpace(title))
                title = SectionHandlers.RomTitleNormalizationUtil.GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(filePath));

            return new MediaItem
            {
                FileName = filePath,
                Title = title,
                Album = album.Title,
                CoverBitmap = album.CoverBitmap
            };
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

        private static string? TryReadCachedMetadataTitle(string filePath)
        {
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

        private static string GetLocalMetadataCachePath(string? filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

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
                VideoUrl = source.VideoUrl,
                CoverBitmap = previewBitmap
            };
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

            // Incremental updates: post a batch periodically so titles appear
            // progressively instead of all-at-once after a long pass.
            const int UiBatchSize = 8;
            // Pause between actual ROM inspections to keep the scanner relaxed.
            // Cached / already-scanned items skip this delay entirely.
            const int RelaxedInspectionDelayMs = 40;

            var pendingUpdates = new List<(MediaItem item, string title)>(UiBatchSize);

            async Task FlushAsync()
            {
                if (pendingUpdates.Count == 0)
                    return;

                var snapshot = pendingUpdates.ToArray();
                pendingUpdates.Clear();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var (item, title) in snapshot)
                        item.Title = title;

                    if (ReferenceEquals(LoadedAlbum, album) && !string.IsNullOrWhiteSpace(SearchText?.Trim()))
                        ApplyFilter();
                }, DispatcherPriority.Background);
            }

            try
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(item.FileName))
                        continue;

                    var wasPreviouslyScanned =
                        SectionHandlers.GenericAlbumNormalizer.IsRomMetadataAlreadyScanned(item.FileName);

                    var resolvedTitle = SectionHandlers.GenericAlbumNormalizer.ResolveRomTitle(
                        item.FileName,
                        album.Title,
                        item.Title);

                    if (!string.IsNullOrWhiteSpace(resolvedTitle) &&
                        !string.Equals(item.Title, resolvedTitle, StringComparison.Ordinal))
                    {
                        pendingUpdates.Add((item, resolvedTitle));
                        if (pendingUpdates.Count >= UiBatchSize)
                            await FlushAsync().ConfigureAwait(false);
                    }

                    // Only throttle when we actually touched disk for inspection.
                    if (!wasPreviouslyScanned)
                        await Task.Delay(RelaxedInspectionDelayMs, cancellationToken).ConfigureAwait(false);
                }

                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort flush of any titles we already resolved before cancel.
                try
                {
                    await FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore secondary failures during cancellation.
                }
                throw;
            }
        }

        private static bool NeedsCoverLookup(MediaItem item, FolderMediaItem album)
        {
            if (item.CoverBitmap == null)
                return true;

            return ReferenceEquals(item.CoverBitmap, album.CoverBitmap);
        }

        private void ApplyFilter()
        {
            var source = LoadedAlbum?.Children;
            if (source == null || source.Count == 0)
            {
                CoverItems = [];
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = CreateEmptyMediaItem();
                IsNoAlbumLoadedVisible = true;
                RefreshActiveAlbumState();
                return;
            }

            var query = SearchText?.Trim();
            MediaItem? preferredItem = null;
            int currentSelectedIndex = GetRoundedSelectedIndex(SelectedIndex);

            if (currentSelectedIndex >= 0 && currentSelectedIndex < CoverItems.Count)
                preferredItem = CoverItems[currentSelectedIndex];

            preferredItem ??= HighlightedItem;

            CoverItems = string.IsNullOrWhiteSpace(query)
                ? source
                : new AvaloniaList<MediaItem>(source.Where(item => Matches(item, query)));

            if (CoverItems.Count == 0)
            {
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = CreateEmptyMediaItem();
                IsNoAlbumLoadedVisible = true;
                RefreshActiveAlbumState();
                return;
            }

            int nextIndex = preferredItem != null ? CoverItems.IndexOf(preferredItem) : -1;
            if (nextIndex < 0 || nextIndex >= CoverItems.Count)
                nextIndex = 0;

            SelectedIndex = nextIndex;
            if (PointedIndex >= CoverItems.Count)
                PointedIndex = -1;

            HighlightedItem = CoverItems[nextIndex];
            IsNoAlbumLoadedVisible = false;
            RefreshActiveAlbumState();
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
            if (_pendingAlbumOrder.Count == 0 || AlbumList.Count <= 1)
                return;

            var orderMap = _pendingAlbumOrder
                .Select((key, index) => (key, index))
                .GroupBy(entry => entry.key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

            var reordered = AlbumList
                .OrderBy(album =>
                    orderMap.TryGetValue(GetAlbumOrderKey(album), out var index)
                        ? index
                        : int.MaxValue)
                .ThenBy(album => album.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AlbumList.Clear();
            AlbumList.AddRange(reordered);
        }
    }
}
