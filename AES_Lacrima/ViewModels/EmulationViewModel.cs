using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Platform;
using AES_Lacrima.Services;
using Avalonia;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;

namespace AES_Lacrima.ViewModels
{
    public interface IEmulationViewModel
    {
        IntPtr EmulatorTargetHwnd { get; }
    }

    public record ShaderFileItem(string Path, string Name);

    public partial class EmulationAlbumItem : FolderMediaItem
    {
        [ObservableProperty]
        private AvaloniaList<MediaItem> _previewItems = [];
    }

    [AutoRegister]
    public partial class EmulationViewModel : ViewModelBase, IEmulationViewModel
    {
        private static readonly ILog SLog = AES_Core.Logging.LogHelper.For<EmulationViewModel>();
        private static AvaloniaList<FolderMediaItem>? _sharedAlbumCache;

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

        private readonly Dictionary<FolderMediaItem, CancellationTokenSource> _albumScanCtsMap = [];
        private AvaloniaList<string> _pendingAlbumOrder = [];
        private Dictionary<string, List<MediaItem>> _pendingAlbumRoms = new(StringComparer.OrdinalIgnoreCase);
        private bool _isPreparing;
        private bool _isSyncingAlbumSelection;
        private CancellationTokenSource? _albumCoverScanCts;
        private CancellationTokenSource? _gameplayPreviewCts;
        private bool _isGameplayPreviewActive;
        private bool _suppressSelectionStopForGameplayPreview;
        private double _lastSelectedIndexForPreview = double.NaN;
        private string? _pendingGameplayPreviewItemPath;
        private string? _activeGameplayPreviewItemPath;
        private long _gameplayPreviewRequestVersion;
        private Process? _activeEmulatorProcess;
        private PendingEmulatorLaunchRequest? _pendingEmulatorLaunchRequest;
        private bool _isClosingActiveEmulatorForRelaunch;
        private const int GameplayPreviewHoverDelayMs = 2000;
        private const int GameplayPreviewResizeAnimationMs = 800;
        private const int GameplayPreviewPostAnimationDelayMs = 200;
        private const int GameplayPreviewResizeDelayMs = GameplayPreviewResizeAnimationMs + GameplayPreviewPostAnimationDelayMs;

        private sealed record PersistedEmulationState(
            bool IsAlbumListCollapsed,
            AvaloniaList<string> AlbumOrder,
            Dictionary<string, List<MediaItem>> AlbumRoms);

        private sealed record GameplayPreviewSource(MediaItem PreviewItem, double? AspectRatio);
        private sealed record PendingEmulatorLaunchRequest(
            string AlbumTitle,
            string ItemTitle,
            IEmulatorHandler Handler,
            string RomPath,
            EmulationSectionLaunchSettings? LaunchSettings);

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        private SettingsViewModel? _subscribedSettingsViewModel;

        [AutoResolve]
        [ObservableProperty]
        private MetadataService? _metadataService;
        private MetadataService? _subscribedMetadataService;

        [ObservableProperty]
        private AudioPlayer? _audioPlayer;

        [AutoResolve]
        private MediaUrlService? _mediaUrlService;

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
        [NotifyPropertyChangedFor(nameof(IsItemPointed))]
        private int _pointedIndex = -1;

        public bool IsItemPointed => PointedIndex != -1 && PointedIndex < CoverItems.Count;
        public bool HasActiveAlbumItems => (LoadedAlbum ?? SelectedAlbum)?.Children.Count > 0;
        public bool ShowEmptyActiveAlbumHint => (LoadedAlbum ?? SelectedAlbum) != null && !HasActiveAlbumItems;
        public string EmptyLoadedAlbumMessage =>
            LoadedAlbum != null
                ? "Right-click to add ROMs or scan folder"
                : "No album loaded";

        [ObservableProperty]
        private MediaItem _highlightedItem = CreateEmptyMediaItem();

        [ObservableProperty]
        private bool _isNoAlbumLoadedVisible = true;

        [ObservableProperty]
        private string? _searchText;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGameplayVideoSurfaceVisible))]
        private bool _isGameplayVideoVisible;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewViewportVisible))]
        private bool _isGameplayPreviewHostVisible;

        [ObservableProperty]
        private double _gameplayPreviewTargetAspectRatio;

        public string AlbumListToggleText => IsAlbumListCollapsed ? "Show Albums" : "Hide Albums";
        [ObservableProperty]
        private bool _isEmulatorRunning;

        [ObservableProperty]
        private IntPtr _emulatorTargetHwnd;

        [ObservableProperty]
        private bool _requestStopEmulatorCapture;

        [ObservableProperty]
        private bool _isRenderOptionsOpen;

        [ObservableProperty]
        private int _renderOptionsSelectedTabIndex;

        [ObservableProperty]
        private IEmulatorHandler? _currentEmulatorHandler;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmulatorViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayVideoSurfaceVisible))]
        private bool _isEmulatorViewportDismissed;

        public bool IsGameplayPreviewAvailable => IsGameplayAutoplayEnabled && IsYtDlpInstalled && !IsEmulatorRunning;
        public bool IsEmulatorViewportVisible => IsEmulatorRunning && !IsEmulatorViewportDismissed;
        public bool IsCompositionCaptureVisible => IsActive && IsEmulatorViewportVisible;
        public bool IsCarouselVisible => !IsEmulatorViewportVisible;
        public bool IsSearchOverlayVisible => MetadataService?.IsImageSearchOverlayOpen == true && !IsCompositionCaptureVisible;
        public bool IsGameplayPreviewViewportVisible => IsGameplayPreviewHostVisible && !IsEmulatorViewportVisible;
        public bool IsGameplayVideoSurfaceVisible => IsGameplayVideoVisible && !IsEmulatorViewportVisible;

        public IReadOnlyList<Stretch> CaptureStretchOptions { get; } = new[] { Stretch.Uniform, Stretch.UniformToFill, Stretch.Fill };
        public IReadOnlyList<ShaderFileItem> ShaderFileItems { get; } = LoadShaderFileItems();

        [ObservableProperty]
        private ShaderFileItem _selectedShaderFileItem = new(string.Empty, string.Empty);

        private static IReadOnlyList<ShaderFileItem> LoadShaderFileItems()
        {
            var shaderDirectories = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Shaders", "glsl"),
                Path.Combine(ApplicationPaths.ShadersDirectory, "glsl")
            };

            var files = shaderDirectories
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.glsl", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Select(path => new ShaderFileItem(path, Path.GetFileName(path)))
                .ToList();

            var entries = new List<ShaderFileItem> { new(string.Empty, string.Empty) };
            entries.AddRange(files);
            return entries;
        }

        public EmulationViewModel()
        {
            AlbumList.CollectionChanged += AlbumList_CollectionChanged;
            PropertyChanged += EmulationViewModel_PropertyChanged;
            _selectedShaderFileItem = ShaderFileItems.FirstOrDefault() ?? new(string.Empty, string.Empty);
        }

        private void EmulationViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IsActive) && !IsActive)
            {
                StopGameplayPreview();
            }

            if (e.PropertyName == nameof(IsActive))
            {
                OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            }
        }

        private void EnsureSettingsViewModelSubscription()
        {
            var settings = SettingsViewModel ?? DiLocator.ResolveViewModel<SettingsViewModel>();
            if (settings == null)
                return;

            if (ReferenceEquals(settings, _subscribedSettingsViewModel))
                return;

            if (_subscribedSettingsViewModel != null)
            {
                _subscribedSettingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
                _subscribedSettingsViewModel.EmulationUseFirstItemCoverChanged -= OnEmulationUseFirstItemCoverChanged;
                _subscribedSettingsViewModel.EmulationGameplayAutoplayChanged -= OnEmulationGameplayAutoplayChanged;
            }

            _subscribedSettingsViewModel = settings;
            _subscribedSettingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
            _subscribedSettingsViewModel.EmulationUseFirstItemCoverChanged += OnEmulationUseFirstItemCoverChanged;
            _subscribedSettingsViewModel.EmulationGameplayAutoplayChanged += OnEmulationGameplayAutoplayChanged;
        }

        private void EnsureMetadataServiceSubscription()
        {
            var metadata = MetadataService ?? DiLocator.ResolveViewModel<MetadataService>();
            if (metadata == null)
                return;

            if (!ReferenceEquals(MetadataService, metadata))
                MetadataService = metadata;

            if (ReferenceEquals(metadata, _subscribedMetadataService))
                return;

            if (_subscribedMetadataService != null)
                _subscribedMetadataService.PropertyChanged -= MetadataService_PropertyChanged;

            _subscribedMetadataService = metadata;
            _subscribedMetadataService.PropertyChanged += MetadataService_PropertyChanged;
        }

        partial void OnSettingsViewModelChanged(SettingsViewModel? value)
        {
            EnsureSettingsViewModelSubscription();
            OnPropertyChanged(nameof(IsGameplayPreviewAvailable));
        }

        partial void OnMetadataServiceChanged(MetadataService? oldValue, MetadataService? newValue)
        {
            if (ReferenceEquals(oldValue, _subscribedMetadataService) && oldValue != null)
                oldValue.PropertyChanged -= MetadataService_PropertyChanged;

            _subscribedMetadataService = null;
            EnsureMetadataServiceSubscription();
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
        }

        private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.EmulationUseFirstItemCover) || e.PropertyName == nameof(SettingsViewModel.AppMode))
            {
                RefreshAlbumPreviews();
            }

            if (e.PropertyName == nameof(SettingsViewModel.IsYtDlpInstalled))
            {
                OnPropertyChanged(nameof(IsGameplayPreviewAvailable));

                if (IsGameplayPreviewAvailable)
                    QueueGameplayPreview(HighlightedItem);
                else
                    StopGameplayPreview();
            }
        }

        private void OnEmulationUseFirstItemCoverChanged(bool useFirstItem)
        {
            RefreshAlbumPreviews();
        }

        private void OnEmulationGameplayAutoplayChanged(bool enabled)
        {
            OnPropertyChanged(nameof(IsGameplayPreviewAvailable));

            if (IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem);
            else
                StopGameplayPreview();
        }

        partial void OnIsEmulatorRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(IsGameplayPreviewAvailable));
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsGameplayPreviewViewportVisible));
            OnPropertyChanged(nameof(IsGameplayVideoSurfaceVisible));

            if (value)
            {
                IsEmulatorViewportDismissed = false;
                StopGameplayPreview();
                return;
            }

            IsRenderOptionsOpen = false;
            CurrentEmulatorHandler = null;

            if (IsActive && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        partial void OnIsEmulatorViewportDismissedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsGameplayPreviewViewportVisible));
            OnPropertyChanged(nameof(IsGameplayVideoSurfaceVisible));
        }

        private void RefreshAlbumPreviews()
        {
            foreach (var album in AlbumList.OfType<EmulationAlbumItem>())
                UpdatePreviewItems(album);

            // Force update of album list for view refresh.
            AlbumList = new AvaloniaList<FolderMediaItem>(AlbumList);
            ApplyFilter();
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded) && MetadataService != null && !MetadataService.IsMetadataLoaded)
            {
                ApplyFilter();
                SaveSettings();

                if (IsGameplayAutoplayEnabled)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsGameplayAutoplayEnabled)
                            return;

                        var value = ResolveMetadataTargetItem();
                        if (value == null)
                            return;

                        if (!string.IsNullOrWhiteSpace(MetadataService.VideoUrl))
                            value.VideoUrl = MetadataService.VideoUrl;

                        QueueGameplayPreview(value);
                    }, DispatcherPriority.Background);
                }
            }

            if (e.PropertyName == nameof(MetadataService.IsImageSearchOverlayOpen))
            {
                OnPropertyChanged(nameof(IsSearchOverlayVisible));
            }

            if (e.PropertyName == nameof(MetadataService.VideoUrl) &&
                MetadataService != null &&
                !string.IsNullOrWhiteSpace(MetadataService.VideoUrl))
            {
                var target = ResolveMetadataTargetItem();
                if (target != null)
                {
                    target.VideoUrl = MetadataService.VideoUrl;
                }
            }
        }

        private MediaItem? ResolveMetadataTargetItem()
        {
            var metadata = MetadataService;
            if (metadata == null)
                return HighlightedItem;

            var targetPath = metadata.FilePath;
            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                var item = CoverItems.FirstOrDefault(candidate =>
                    string.Equals(candidate.FileName, targetPath, StringComparison.OrdinalIgnoreCase));

                if (item != null)
                    return item;
            }

            return HighlightedItem;
        }

        private void ForceRestartGameplayPreview(MediaItem item, bool immediate = false)
        {
            if (immediate)
                _suppressSelectionStopForGameplayPreview = true;

            StopGameplayPreview();
            QueueGameplayPreview(item, immediate);
        }

        public override void Prepare()
        {
            if (IsPrepared || _isPreparing)
                return;

            _isPreparing = true;
            base.Prepare();
            EnsureSettingsViewModelSubscription();
            EnsureMetadataServiceSubscription();

            _ = Task.Run(async () =>
            {
                try
                {
                    var persistedStateStopwatch = Stopwatch.StartNew();
                    var persistedState = await LoadPersistedEmulationStateAsync().ConfigureAwait(false);
                    persistedStateStopwatch.Stop();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ApplyPersistedEmulationState(persistedState);
                        SLog.Info(
                            $"EmulationViewModel.LoadPersistedEmulationStateAsync completed in {persistedStateStopwatch.ElapsedMilliseconds} ms. " +
                            $"SavedAlbums={persistedState.AlbumRoms.Count}, SavedOrderEntries={persistedState.AlbumOrder.Count}.");

                        if (_sharedAlbumCache != null && _sharedAlbumCache.Count > 0)
                        {
                            AlbumList = new AvaloniaList<FolderMediaItem>(_sharedAlbumCache);
                            SelectedAlbum = AlbumList.FirstOrDefault();
                            LoadedAlbum = null;
                            IsPrepared = true;
                            _isPreparing = false;
                            RefreshActiveAlbumState();
                            return;
                        }

                        // Load emulation albums in background so the UI can render immediately.
                        _ = InitializeAlbumsAsync();
                    }, DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    _isPreparing = false;
                    SLog.Warn("Failed to load persisted emulation state during Prepare.", ex);
                }
            });
        }

        public override void OnShowViewModel()
        {
            base.OnShowViewModel();
            EnsureSettingsViewModelSubscription();
            EnsureMetadataServiceSubscription();
            if (!IsPrepared)
                RefreshAlbumPreviews();
            else if (!IsEmulatorRunning && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        public override void OnLeaveViewModel()
        {
            base.OnLeaveViewModel();
            StopGameplayPreview();
            SaveSettings();
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
            RefreshActiveAlbumState();
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
            if (!_suppressSelectionStopForGameplayPreview &&
                !double.IsNaN(_lastSelectedIndexForPreview) &&
                Math.Abs(value - _lastSelectedIndexForPreview) > 0.0001)
            {
                StopGameplayPreview();
            }
            _lastSelectedIndexForPreview = value;

            if (Math.Abs(value - Math.Round(value)) > 0.001)
                return;

            int roundedIndex = GetRoundedSelectedIndex(value);
            if (roundedIndex >= 0 && roundedIndex < CoverItems.Count)
            {
                HighlightedItem = CoverItems[roundedIndex];
            }
        }

        partial void OnHighlightedItemChanged(MediaItem value)
        {
            QueueGameplayPreview(value);
        }

        [RelayCommand]
        private void ToggleAlbumList() => IsAlbumListCollapsed = !IsAlbumListCollapsed;

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void ToggleEmulatorViewport()
        {
            if (!IsEmulatorRunning)
                return;

            IsEmulatorViewportDismissed = !IsEmulatorViewportDismissed;
        }

        [RelayCommand]
        private void ToggleRenderOptions()
        {
            if (!IsEmulatorRunning)
                return;

            IsRenderOptionsOpen = !IsRenderOptionsOpen;
        }

        [RelayCommand]
        private void CloseEmulator()
        {
            SLog.Info("EmulationViewModel.CloseEmulator requested by the user.");
            _pendingEmulatorLaunchRequest = null;
            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;
            IsEmulatorRunning = false;
            IsRenderOptionsOpen = false;
            CurrentEmulatorHandler = null;

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            DetachTrackedEmulatorProcess();
        }

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
            if (handler == null || string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
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
            SLog.Info("EmulationViewModel.OnLoadSettings applied lightweight settings on the UI thread.");
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(IsAlbumListCollapsed), IsAlbumListCollapsed);

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
                        QueueSelectedAlbumCoverScan(album);
                }

                SelectedAlbum = AlbumList.FirstOrDefault();
                LoadedAlbum = null;
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
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new Stack<string>();
            directories.Push(rootPath);

            while (directories.Count > 0)
            {
                var currentDirectory = directories.Pop();

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
            if (album == null || album.Children.Count == 0 || MetadataService == null)
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

            NormalizeAlbumRomTitles(album);
            SLog.Debug($"Queueing emulation cover scan for album '{album.Title}' with {album.Children.Count} items.");

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

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(LoadedAlbum, album) && ReferenceEquals(HighlightedItem, item))
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
                VideoUrl = source.VideoUrl,
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

        private static bool Matches(MediaItem item, string query)
        {
            return
                item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                item.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void RequestEmulatorLaunch(PendingEmulatorLaunchRequest request)
        {
            _pendingEmulatorLaunchRequest = request;
            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            TryLaunchPendingEmulatorRequest();
        }

        private void TryLaunchPendingEmulatorRequest()
        {
            if (_pendingEmulatorLaunchRequest is not { } request)
                return;

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            _pendingEmulatorLaunchRequest = null;
            LaunchEmulator(request);
        }

        private void LaunchEmulator(PendingEmulatorLaunchRequest request)
        {
            try
            {
                var handler = request.Handler;
                CurrentEmulatorHandler = handler;

                if (!handler.IsPrepared)
                    handler.Prepare();

                var startInfo = handler.BuildStartInfo(
                    handler.LauncherPath ?? string.Empty,
                    request.RomPath,
                    request.LaunchSettings?.StartFullscreen == true);
                var process = Process.Start(startInfo);
                TrackEmulatorProcess(process, request.RomPath, handler);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch emulator for '{request.AlbumTitle}' item '{request.ItemTitle}'.", ex);
            }
        }

        private bool TryGetRunningTrackedEmulatorProcess(out Process process)
        {
            process = _activeEmulatorProcess!;
            if (process == null)
                return false;

            try
            {
                if (process.HasExited)
                    return false;
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to inspect the tracked emulator process state.", ex);
                return false;
            }

            return true;
        }

        private void CloseTrackedEmulatorForPendingLaunch(Process process)
        {
            if (_isClosingActiveEmulatorForRelaunch)
            {
                SLog.Info("EmulationViewModel ignored a duplicate emulator close request because shutdown is already in progress.");
                return;
            }

            _isClosingActiveEmulatorForRelaunch = true;
            SLog.Info($"EmulationViewModel starting tracked emulator shutdown. pid={process.Id}.");
            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;
            _ = CloseTrackedEmulatorForPendingLaunchAsync(process);
        }

        private async Task CloseTrackedEmulatorForPendingLaunchAsync(Process process)
        {
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        var closeMainWindowResult = process.CloseMainWindow();
                        SLog.Info($"EmulationViewModel CloseMainWindow returned {closeMainWindowResult} for pid={process.Id}.");
                        if (!closeMainWindowResult)
                            process.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Failed to close the emulator gracefully; forcing termination.", ex);

                        try
                        {
                            process.Kill(true);
                        }
                        catch (Exception killEx)
                        {
                            SLog.Debug("Failed to force-close the emulator process during relaunch.", killEx);
                        }
                    }

                    try
                    {
                        SLog.Info($"EmulationViewModel waiting up to 5000 ms for emulator pid={process.Id} to exit.");
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Timed wait for emulator shutdown failed; continuing with final state checks.", ex);
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            SLog.Info($"EmulationViewModel is force-closing emulator pid={process.Id} after graceful shutdown timed out.");
                            process.Kill(true);
                            process.WaitForExit(3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        SLog.Debug("Final forced emulator shutdown hit a process race.", ex);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SLog.Info("EmulationViewModel finished the tracked emulator shutdown flow.");
                    _isClosingActiveEmulatorForRelaunch = false;
                    TryLaunchPendingEmulatorRequest();
                }, DispatcherPriority.Background);
            }
        }

        private void TrackEmulatorProcess(Process? process, string romPath, IEmulatorHandler handler)
        {
            EmulatorTargetHwnd = IntPtr.Zero;

            if (process == null)
            {
                SLog.Warn($"Emulator launch for '{romPath}' did not expose a trackable process handle.");
                EmulatorTargetHwnd = IntPtr.Zero;
                StopGameplayPreview();
                return;
            }

            DetachTrackedEmulatorProcess();

            _activeEmulatorProcess = process;

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += ActiveEmulatorProcess_Exited;
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to subscribe to emulator exit events.", ex);
            }

            IsEmulatorRunning = !process.HasExited;

            if (process.HasExited)
                HandleTrackedEmulatorExited(process);
            else
                _ = ResolveEmulatorTargetHwndAsync(process, romPath, handler);
        }

        private void ActiveEmulatorProcess_Exited(object? sender, EventArgs e)
        {
            if (sender is not Process process)
                return;

            Dispatcher.UIThread.Post(() => HandleTrackedEmulatorExited(process), DispatcherPriority.Background);
        }

        private void HandleTrackedEmulatorExited(Process process)
        {
            if (!ReferenceEquals(_activeEmulatorProcess, process))
            {
                try
                {
                    process.Exited -= ActiveEmulatorProcess_Exited;
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to clean up a stale emulator process reference.", ex);
                }

                return;
            }

            DetachTrackedEmulatorProcess();
            IsEmulatorRunning = false;
            TryLaunchPendingEmulatorRequest();
        }

        private void DetachTrackedEmulatorProcess()
        {
            if (_activeEmulatorProcess == null)
            {
                EmulatorTargetHwnd = IntPtr.Zero;
                return;
            }

            try
            {
                _activeEmulatorProcess.Exited -= ActiveEmulatorProcess_Exited;
                _activeEmulatorProcess.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to detach the active emulator process cleanly.", ex);
            }
            finally
            {
                _activeEmulatorProcess = null;
                EmulatorTargetHwnd = IntPtr.Zero;
            }
        }

        private async Task ResolveEmulatorTargetHwndAsync(Process process, string romPath, IEmulatorHandler handler)
        {
            const int maxAttempts = 80;
            const int delayMs = 250;
            const int stableAttemptsBeforeStop = 12;
            const int stableAttemptsBeforeAssign = 3;

            IntPtr observedHwnd = IntPtr.Zero;
            var observedStableAttempts = 0;
            IntPtr assignedHwnd = IntPtr.Zero;
            var assignedStableAttempts = 0;
            var hasAssignedHandle = false;
            var hideUntilCaptured = handler.HideUntilCaptured;

            try
            {
                TryWaitForInputIdle(process, 2000);

                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    if (!IsTrackedProcessAlive(process))
                        return;

                    if (hideUntilCaptured)
                        handler.PrepareProcessForCapture(process);

                    var hwnd = handler.FindPreferredWindowHandle(process);
                    if (hwnd != IntPtr.Zero)
                    {
                        if (hideUntilCaptured)
                            handler.PrepareWindowForCapture(hwnd);

                        if (hwnd == observedHwnd)
                        {
                            observedStableAttempts++;
                        }
                        else
                        {
                            observedHwnd = hwnd;
                            observedStableAttempts = 1;
                        }

                        var canAssign = !hideUntilCaptured || handler.CanAssignWindow(hwnd, process.MainWindowHandle);

                        if (canAssign &&
                            hwnd != assignedHwnd &&
                            observedStableAttempts >= stableAttemptsBeforeAssign)
                        {
                            if (!await TryApplyEmulatorTargetHwndAsync(
                                    process,
                                    hwnd,
                                    showWindowForCapture: hideUntilCaptured).ConfigureAwait(false))
                                return;

                            assignedHwnd = hwnd;
                            assignedStableAttempts = observedStableAttempts;
                            hasAssignedHandle = true;
                        }
                        else if (hwnd == assignedHwnd)
                        {
                            assignedStableAttempts = observedStableAttempts;
                        }

                        if (hasAssignedHandle && assignedStableAttempts >= stableAttemptsBeforeStop)
                            return;
                    }
                    else
                    {
                        observedHwnd = IntPtr.Zero;
                        observedStableAttempts = 0;
                    }

                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                if (!hasAssignedHandle)
                    SLog.Warn($"Failed to resolve emulator HWND for '{romPath}'.");
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to resolve emulator HWND for '{romPath}'.", ex);
            }
        }

        private bool IsTrackedProcessAlive(Process process)
        {
            if (!ReferenceEquals(_activeEmulatorProcess, process))
                return false;

            if (_isClosingActiveEmulatorForRelaunch)
                return false;

            try
            {
                return !process.HasExited;
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to poll the tracked emulator process state.", ex);
                return false;
            }
        }

        private async Task<bool> TryApplyEmulatorTargetHwndAsync(Process process, IntPtr hwnd, bool showWindowForCapture)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_activeEmulatorProcess, process))
                    return false;

                if (_isClosingActiveEmulatorForRelaunch)
                {
                    SLog.Info(
                        $"EmulationViewModel skipped applying emulator hwnd 0x{hwnd.ToInt64():X} because emulator shutdown is in progress.");
                    return false;
                }

                try
                {
                    if (process.HasExited)
                        return false;
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to confirm emulator process state before applying the capture target.", ex);
                    return false;
                }

                if (showWindowForCapture)
                    RevealCaptureWindow(hwnd);

                if (EmulatorTargetHwnd != hwnd)
                    EmulatorTargetHwnd = hwnd;

                return true;
            }, DispatcherPriority.Background);
        }

        private static void TryWaitForInputIdle(Process process, int timeoutMs)
        {
            try
            {
                process.WaitForInputIdle(timeoutMs);
            }
            catch (Exception ex)
            {
                SLog.Debug("Emulator did not provide an input-idle state; falling back to polling.", ex);
            }
        }

        private static void RevealCaptureWindow(IntPtr platformWindowHandle)
        {
            try
            {
                EmulatorCapturePlatform.RevealWindowForCapture(platformWindowHandle);
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to reveal the emulator window for the active capture platform.", ex);
            }
        }


        private static int GetRoundedSelectedIndex(double value) => (int)Math.Round(value);

        private static MediaItem CreateEmptyMediaItem() => new()
        {
            Title = string.Empty,
            Artist = string.Empty,
            Album = string.Empty
        };

        private bool IsGameplayAutoplayEnabled => SettingsViewModel?.EmulationGameplayAutoplay == true;
        private bool IsYtDlpInstalled => SettingsViewModel?.IsYtDlpInstalled ?? YtDlpManager.IsInstalled;

        private void QueueGameplayPreview(MediaItem? item, bool immediate = false)
        {
            if (!IsGameplayPreviewAvailable || item == null || string.IsNullOrWhiteSpace(item.FileName))
            {
                if (immediate)
                    _suppressSelectionStopForGameplayPreview = false;
                StopGameplayPreview();
                return;
            }

            var requestedPath = item.FileName;
            if (string.Equals(_pendingGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                if (immediate)
                    _suppressSelectionStopForGameplayPreview = false;
                return;
            }

            if (_isGameplayPreviewActive &&
                string.Equals(_activeGameplayPreviewItemPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                var currentPlaybackUrl = AudioPlayer?.CurrentMediaItem?.FileName;
                var requestedVideoUrl = item.VideoUrl;
                if (!string.IsNullOrWhiteSpace(requestedVideoUrl) &&
                    !string.Equals(currentPlaybackUrl, requestedVideoUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // Same selected item but gameplay URL changed -> force restart with new URL.
                }
                else
                {
                    IsGameplayVideoVisible = true;
                    if (immediate)
                        _suppressSelectionStopForGameplayPreview = false;
                    return;
                }
            }

            // Selection actually changed -> stop/hide immediately, then delay-start the next item.
            StopGameplayPreview();
            _pendingGameplayPreviewItemPath = requestedPath;
            long requestVersion = Interlocked.Increment(ref _gameplayPreviewRequestVersion);

            var cts = new CancellationTokenSource();
            _gameplayPreviewCts = cts;
            var token = cts.Token;
            _ = StartGameplayPreviewAsync(item, token, immediate, requestVersion);
        }

        private async Task StartGameplayPreviewAsync(MediaItem item, CancellationToken cancellationToken, bool immediate, long requestVersion)
        {
            try
            {
                if (!immediate)
                    await Task.Delay(GameplayPreviewHoverDelayMs, cancellationToken);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                // Start resolving the final playback source immediately so the shell reveal
                // and the yt-dlp/stream work overlap as much as possible.
                var previewSourceTask = ResolveGameplayPreviewSourceAsync(item, cancellationToken);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsGameplayPreviewHostVisible = true;
                    IsGameplayVideoVisible = false;
                    GameplayPreviewTargetAspectRatio = 0;
                }, DispatcherPriority.Background);

                var previewSource = await previewSourceTask.ConfigureAwait(false);
                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                if (previewSource == null)
                {
                    StopGameplayPreview();
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GameplayPreviewTargetAspectRatio = previewSource.AspectRatio ?? 0;
                }, DispatcherPriority.Background);

                await Task.Delay(GameplayPreviewResizeDelayMs, cancellationToken);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                    return;

                EnsureGameplayAudioPlayer();
                var player = AudioPlayer;
                if (player == null)
                {
                    StopGameplayPreview();
                    return;
                }

                await player.PlayFile(previewSource.PreviewItem, video: true);

                if (requestVersion != Interlocked.Read(ref _gameplayPreviewRequestVersion))
                {
                    try
                    {
                        player.Stop();
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn("Failed to stop stale gameplay preview video.", ex);
                    }

                    return;
                }

                _isGameplayPreviewActive = true;
                _activeGameplayPreviewItemPath = item.FileName;
                _pendingGameplayPreviewItemPath = null;
                await Dispatcher.UIThread.InvokeAsync(() => IsGameplayVideoVisible = true, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                // Ignore: selection changed before delayed preview start.
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to autoplay gameplay preview for '{item.Title}'.", ex);
                await Dispatcher.UIThread.InvokeAsync(() => IsGameplayVideoVisible = false, DispatcherPriority.Background);
                _isGameplayPreviewActive = false;
            }
            finally
            {
                _suppressSelectionStopForGameplayPreview = false;
            }
        }

        private void StopGameplayPreview()
        {
            try
            {
                _gameplayPreviewCts?.Cancel();
                _gameplayPreviewCts?.Dispose();
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to cancel or dispose the gameplay preview token source cleanly.", ex);
            }
            finally
            {
                _gameplayPreviewCts = null;
            }

            Interlocked.Increment(ref _gameplayPreviewRequestVersion);

            _pendingGameplayPreviewItemPath = null;
            _activeGameplayPreviewItemPath = null;
            IsGameplayPreviewHostVisible = false;
            IsGameplayVideoVisible = false;
            GameplayPreviewTargetAspectRatio = 0;

            try
            {
                AudioPlayer?.Stop();
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to stop gameplay preview video.", ex);
            }

            _isGameplayPreviewActive = false;
        }

        private static string GetMetadataCachePath(string? filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

        private async Task<string?> ResolveGameplayVideoUrlAsync(MediaItem item, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(item.VideoUrl))
                return item.VideoUrl;

            var cachePath = GetMetadataCachePath(item.FileName);
            var metadata = await Task.Run(() => BinaryMetadataHelper.LoadMetadata(cachePath), cancellationToken).ConfigureAwait(false);
            var cachedVideoUrl = metadata?.VideoUrl;
            if (string.IsNullOrWhiteSpace(cachedVideoUrl))
                return null;

            await Dispatcher.UIThread.InvokeAsync(() => item.VideoUrl = cachedVideoUrl, DispatcherPriority.Background);
            return cachedVideoUrl;
        }

        private async Task<GameplayPreviewSource?> ResolveGameplayPreviewSourceAsync(MediaItem item, CancellationToken cancellationToken)
        {
            var videoUrl = await ResolveGameplayVideoUrlAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(videoUrl))
                return null;

            var previewItem = new MediaItem
            {
                FileName = videoUrl,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                VideoUrl = videoUrl
            };

            double? aspectRatio = null;

            if (videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                _mediaUrlService ??= DiLocator.ResolveViewModel<MediaUrlService>();
                if (_mediaUrlService == null)
                    return null;

                var resolvedSource = await _mediaUrlService.ResolveMediaSourceAsync(videoUrl, preferVideo: true).ConfigureAwait(false);
                if (resolvedSource == null)
                    return null;

                previewItem.OnlineUrls = resolvedSource.OnlineUrls;
                aspectRatio = resolvedSource.AspectRatio;
            }

            return new GameplayPreviewSource(previewItem, aspectRatio);
        }

        private void EnsureGameplayAudioPlayer()
        {
            if (AudioPlayer != null)
            {
                AudioPlayer.RepeatMode = RepeatMode.One;
                return;
            }

            var ffmpegManager = DiLocator.ResolveViewModel<FFmpegManager>();
            var mpvLibraryManager = DiLocator.ResolveViewModel<MpvLibraryManager>();
            AudioPlayer = new AudioPlayer(ffmpegManager, mpvLibraryManager);
            AudioPlayer.RepeatMode = RepeatMode.One;
        }

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
