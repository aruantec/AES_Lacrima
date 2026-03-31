using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace AES_Lacrima.ViewModels
{
    public interface IEmulationViewModel;

    public partial class EmulationAlbumItem : FolderMediaItem
    {
        [ObservableProperty]
        private AvaloniaList<MediaItem> _previewItems = [];
    }

    [AutoRegister]
    internal partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private static readonly ILog SLog = AES_Core.Logging.LogHelper.For<EmulationViewModel>();
        private static readonly Regex RomBracketTokenRegex = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex RomMediaLabelRegex = new(
            @"[\(\[\{]\s*((?:disc|disk|cd|dvd|gd|side)\s*(?:\d+|[ivx]+|[a-z]))\s*[\)\]\}]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomMediaLabelPartsRegex = new(
            @"^(disc|disk|cd|dvd|gd|side)\s*(\d+|[ivx]+|[a-z])$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RomWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
        private static readonly string[] DiscDescriptorExtensions =
        [
            ".m3u",
            ".cue",
            ".gdi"
        ];
        private static readonly string[] SupportedConsoleImageExtensions =
        [
            ".png",
            ".jpg",
            ".jpeg",
            ".webp"
        ];

        private AvaloniaList<string> _pendingAlbumOrder = [];
        private Dictionary<string, List<MediaItem>> _pendingAlbumRoms = new(StringComparer.OrdinalIgnoreCase);
        private bool _isSyncingAlbumSelection;
        private CancellationTokenSource? _albumCoverScanCts;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        [AutoResolve]
        [ObservableProperty]
        private MetadataService? _metadataService;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AlbumListToggleText))]
        private bool _isAlbumListCollapsed;

        [ObservableProperty]
        private AvaloniaList<MediaItem> _coverItems = [];

        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _albumList = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddRomsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ScanFolderCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearAlbumCommand))]
        private FolderMediaItem? _selectedAlbum;

        [ObservableProperty]
        private FolderMediaItem? _loadedAlbum;

        [ObservableProperty]
        private int _selectedAlbumIndex = -1;

        [ObservableProperty]
        private double _selectedIndex = -1;

        [ObservableProperty]
        private int _pointedIndex = -1;

        [ObservableProperty]
        private MediaItem _highlightedItem = CreateEmptyMediaItem();

        [ObservableProperty]
        private bool _isNoAlbumLoadedVisible = true;

        [ObservableProperty]
        private string? _searchText;

        public string AlbumListToggleText => IsAlbumListCollapsed ? "Show Albums" : "Hide Albums";

        public EmulationViewModel()
        {
            AlbumList.CollectionChanged += AlbumList_CollectionChanged;
        }

        public override void Prepare()
        {
            if (IsPrepared)
                return;

            base.Prepare();
            LoadSettings();
            LoadConsoleAlbums();
            SelectedAlbum = AlbumList.FirstOrDefault();
            LoadedAlbum = SelectedAlbum;
            IsPrepared = true;
        }

        partial void OnIsAlbumListCollapsedChanged(bool value)
        {
            if (IsPrepared)
                SaveSettings();
        }

        partial void OnSearchTextChanged(string? value) => ApplyFilter();

        partial void OnSelectedAlbumChanged(FolderMediaItem? value)
        {
            SyncSelectedAlbumIndexFromAlbum(value);

            if (IsPrepared)
                SaveSettings();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            ApplyFilter();
            QueueSelectedAlbumCoverScan(value);
        }

        partial void OnSelectedAlbumIndexChanged(int value)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextAlbum =
                value >= 0 && value < AlbumList.Count
                    ? AlbumList[value]
                    : null;

            if (!ReferenceEquals(SelectedAlbum, nextAlbum))
                SelectedAlbum = nextAlbum;
        }

        partial void OnSelectedIndexChanged(double value)
        {
            int roundedIndex = GetRoundedSelectedIndex(value);
            if (roundedIndex >= 0 && roundedIndex < CoverItems.Count)
            {
                HighlightedItem = CoverItems[roundedIndex];
            }
        }

        [RelayCommand]
        private void ToggleAlbumList() => IsAlbumListCollapsed = !IsAlbumListCollapsed;

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void OpenSelectedAlbum()
        {
            if (SelectedAlbum == null)
                return;

            LoadedAlbum = SelectedAlbum;
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            IsAlbumListCollapsed = ReadBoolSetting(section, nameof(IsAlbumListCollapsed));
            _pendingAlbumOrder = ReadCollectionSetting(section, "AlbumOrder", "string", _pendingAlbumOrder);
            _pendingAlbumRoms = ReadObjectSetting<Dictionary<string, List<MediaItem>>>(section, "AlbumRoms")
                ?? new Dictionary<string, List<MediaItem>>(StringComparer.OrdinalIgnoreCase);
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(IsAlbumListCollapsed), IsAlbumListCollapsed);
            WriteCollectionSetting(section, "AlbumOrder", "string", AlbumList.Select(GetAlbumOrderKey));
            WriteObjectSetting(section, "AlbumRoms", BuildAlbumRomMap());
        }

        private void LoadConsoleAlbums()
        {
            AlbumList.Clear();

            foreach (var imagePath in FindConsoleImagePaths())
            {
                var title = GetConsoleTitle(imagePath);
                var previewBitmap = LoadBitmap(imagePath);

                AlbumList.Add(new EmulationAlbumItem
                {
                    Title = title,
                    Album = title,
                    FileName = imagePath,
                    CoverBitmap = previewBitmap,
                    PreviewItems =
                    [
                        new MediaItem
                        {
                            Title = title,
                            Album = title,
                            FileName = imagePath,
                            CoverBitmap = previewBitmap
                        }
                    ],
                    Children = RestoreAlbumRoms(imagePath, title, previewBitmap)
                });
            }

            ApplySavedAlbumOrder();
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

            var rootPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return;

            var scanPatterns = EmulationConsoleCatalog.GetScanPatterns(album.Title);
            var paths = await Task.Run(() => ScanFolderForRomPaths(rootPath, scanPatterns));
            bool addedAny = ImportRomPaths(album, paths);

            if (!addedAny)
                return;

            FinalizeRomImport(album);
        }

        [RelayCommand(CanExecute = nameof(CanAddRoms))]
        private void ClearAlbum()
        {
            var album = SelectedAlbum;
            if (album == null)
                return;

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
            ApplyFilter();
            SaveSettings();
        }

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
            => string.IsNullOrWhiteSpace(album.FileName)
                ? album.Title ?? string.Empty
                : Path.GetFileName(album.FileName);

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
            if (ReferenceEquals(LoadedAlbum, album))
                ApplyFilter();

            QueueSelectedAlbumCoverScan(album);
            SaveSettings();
        }

        private static IReadOnlyList<string> ScanFolderForRomPaths(string rootPath, IReadOnlyList<string> patterns)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new Stack<string>();
            directories.Push(rootPath);

            while (directories.Count > 0)
            {
                var currentDirectory = directories.Pop();

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
            try
            {
                _albumCoverScanCts?.Cancel();
                _albumCoverScanCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to cancel previous emulation album cover scan.", ex);
            }

            if (album == null || album.Children.Count == 0 || MetadataService == null)
                return;

            NormalizeAlbumRomTitles(album);
            SLog.Debug($"Queueing emulation cover scan for album '{album.Title}' with {album.Children.Count} items.");

            _albumCoverScanCts = new CancellationTokenSource();
            var cancellationToken = _albumCoverScanCts.Token;
            _ = Task.Run(() => LoadAlbumCoversAsync(album, cancellationToken), cancellationToken);
        }

        private AvaloniaList<MediaItem> RestoreAlbumRoms(string imagePath, string albumTitle, Bitmap? previewBitmap)
        {
            if (!_pendingAlbumRoms.TryGetValue(Path.GetFileName(imagePath), out var savedItems) || savedItems.Count == 0)
                return [];

            return new AvaloniaList<MediaItem>(
                savedItems.Select(item => CloneRomItem(item, albumTitle, previewBitmap)));
        }

        private Dictionary<string, List<MediaItem>> BuildAlbumRomMap()
        {
            return AlbumList
                .Where(album => album.Children.Count > 0)
                .ToDictionary(
                    GetAlbumOrderKey,
                    album => album.Children.Select(item => CloneRomItem(item, album.Title, null)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private async Task LoadAlbumCoversAsync(FolderMediaItem album, CancellationToken cancellationToken)
        {
            if (MetadataService == null)
                return;

            try
            {
                var itemsToLoad = await Dispatcher.UIThread.InvokeAsync(() =>
                    album.Children.Where(item => NeedsCoverLookup(item, album)).ToList(), DispatcherPriority.Background);
                SLog.Debug($"Starting emulation cover scan for album '{album.Title}'. {itemsToLoad.Count} roms need lookup.");

                foreach (var item in itemsToLoad)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var populated = await MetadataService.TryPopulateCoverFromLocalMetadataOrGoogleAsync(item, album.Title, cancellationToken);
                    SLog.Debug(
                        populated
                            ? $"Auto cover resolved for rom '{item.Title}' in album '{album.Title}'."
                            : $"Auto cover not found for rom '{item.Title}' in album '{album.Title}'.");

                    if (!ReferenceEquals(LoadedAlbum, album))
                        continue;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(HighlightedItem, item))
                            HighlightedItem = item;
                    }, DispatcherPriority.Background);

                    try
                    {
                        await Task.Delay(10, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SLog.Debug($"Emulation cover scan canceled for album '{album.Title}'.");
            }
            catch (Exception ex)
            {
                SLog.Warn($"Emulation cover scan failed for album '{album.Title}'.", ex);
            }
        }

        private static MediaItem CreateRomItem(string filePath, FolderMediaItem album)
        {
            var title = GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(filePath));
            return new MediaItem
            {
                FileName = filePath,
                Title = title,
                Album = album.Title,
                CoverBitmap = album.CoverBitmap
            };
        }

        private static MediaItem CloneRomItem(MediaItem source, string? albumTitle, Bitmap? previewBitmap)
        {
            var fileName = source.FileName;
            return new MediaItem
            {
                FileName = fileName,
                Title = GetNormalizedRomTitle(string.IsNullOrWhiteSpace(source.Title)
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
                CoverBitmap = previewBitmap
            };
        }

        private static void NormalizeAlbumRomTitles(FolderMediaItem album)
        {
            foreach (var item in album.Children)
            {
                var normalized = GetNormalizedRomTitle(item.Title);
                if (string.IsNullOrWhiteSpace(normalized) && !string.IsNullOrWhiteSpace(item.FileName))
                    normalized = GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(item.FileName));

                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !string.Equals(item.Title, normalized, StringComparison.Ordinal))
                {
                    item.Title = normalized;
                }
            }
        }

        private static string GetNormalizedRomTitle(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return string.Empty;

            var normalized = rawTitle.Replace('_', ' ').Replace('.', ' ').Trim();
            var preservedMediaLabels = RomMediaLabelRegex
                .Matches(normalized)
                .Select(match => NormalizeRomMediaLabel(match.Groups[1].Value))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            normalized = RomBracketTokenRegex.Replace(normalized, " ");
            normalized = normalized.Replace("!", " ");
            normalized = RomWhitespaceRegex.Replace(normalized, " ").Trim();

            if (preservedMediaLabels.Length > 0)
            {
                var suffix = string.Join(" ", preservedMediaLabels.Select(label => $"({label})"));
                normalized = string.IsNullOrWhiteSpace(normalized)
                    ? suffix
                    : $"{normalized} {suffix}";
            }

            return normalized;
        }

        private static string NormalizeRomMediaLabel(string rawLabel)
        {
            if (string.IsNullOrWhiteSpace(rawLabel))
                return string.Empty;

            var compact = RomWhitespaceRegex.Replace(rawLabel, " ").Trim();
            var match = RomMediaLabelPartsRegex.Match(compact);
            if (!match.Success)
                return compact;

            var prefix = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "disc" => "Disc",
                "disk" => "Disk",
                "cd" => "CD",
                "dvd" => "DVD",
                "gd" => "GD",
                "side" => "Side",
                _ => match.Groups[1].Value
            };

            var value = match.Groups[2].Value;
            return $"{prefix} {value.ToUpperInvariant()}";
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

        private static bool Matches(MediaItem item, string query)
        {
            return
                item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static int GetRoundedSelectedIndex(double value) => (int)Math.Round(value);

        private static MediaItem CreateEmptyMediaItem() => new()
        {
            Title = string.Empty,
            Artist = string.Empty,
            Album = string.Empty
        };

        private static Bitmap? LoadBitmap(string imagePath)
        {
            try
            {
                using var stream = File.OpenRead(imagePath);
                return new Bitmap(stream);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to load console bitmap '{imagePath}'.", ex);
                return null;
            }
        }
    }
}
