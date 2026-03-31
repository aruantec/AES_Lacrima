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
        private static readonly Regex RomWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
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
        private FolderMediaItem? _selectedAlbum;

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

            ApplyFilter();
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
            ApplyFilter();
            QueueSelectedAlbumCoverScan(value);

            if (IsPrepared)
                SaveSettings();
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
            if (SelectedAlbum == null)
                return;

            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
                desktop.MainWindow?.StorageProvider is not { } storageProvider)
            {
                return;
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Add Roms to {SelectedAlbum.Title}",
                AllowMultiple = true
            });

            if (files.Count == 0)
                return;

            bool addedAny = false;
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (SelectedAlbum.Children.Any(existing =>
                        string.Equals(existing.FileName, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SelectedAlbum.Children.Add(CreateRomItem(path, SelectedAlbum));
                addedAny = true;
            }

            if (!addedAny)
                return;

            ApplyFilter();
            QueueSelectedAlbumCoverScan(SelectedAlbum);
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
            return fileName.Replace('_', ' ').Replace('-', ' ').Trim();
        }

        private static string GetAlbumOrderKey(FolderMediaItem album)
            => string.IsNullOrWhiteSpace(album.FileName)
                ? album.Title ?? string.Empty
                : Path.GetFileName(album.FileName);

        private bool CanAddRoms() => SelectedAlbum != null;

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

                    if (!ReferenceEquals(SelectedAlbum, album))
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
            normalized = RomBracketTokenRegex.Replace(normalized, " ");
            normalized = normalized.Replace("!", " ");
            normalized = RomWhitespaceRegex.Replace(normalized, " ").Trim();
            return normalized;
        }

        private static bool NeedsCoverLookup(MediaItem item, FolderMediaItem album)
        {
            if (item.CoverBitmap == null)
                return true;

            return ReferenceEquals(item.CoverBitmap, album.CoverBitmap);
        }

        private void ApplyFilter()
        {
            var source = SelectedAlbum?.Children;
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
