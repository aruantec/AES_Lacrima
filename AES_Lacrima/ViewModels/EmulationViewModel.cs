using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Platform;
using AES_Emulation.Windows.API;
using AES_Lacrima.Mac.API;
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

    public record ShaderFileItem(string FilePath, string Name, bool IsSupportedInDirectComposition = true);
    public partial class XeniaPatchEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string Name { get; }

        public string Description { get; }

        public XeniaPatchEntry(bool isEnabled, string name, string description)
        {
            _isEnabled = isEnabled;
            Name = name;
            Description = description;
        }
    }

    public sealed record XeniaPatchFileItem(string FilePath, string DisplayName);

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
        private bool _isSyncingCurrentSectionCoreSelection;
        private bool _isSyncingCurrentSectionEdenVersionSelection;
        private bool _isSyncingCurrentSectionEdenRepositoryOverride;
        private bool _isCurrentSectionEdenRepositoryDirty;
        private bool _isSyncingCurrentSectionEdenIncludePrereleases;
        private bool _isSyncingCurrentSectionShadPs4VersionSelection;
        private bool _isSyncingCurrentSectionShadPs4RepositoryOverride;
        private bool _isCurrentSectionShadPs4RepositoryDirty;
        private bool _isSyncingCurrentSectionShadPs4IncludePrereleases;
        private bool _isSyncingCurrentSectionXeniaVersionSelection;
        private bool _isXeniaPatchesOverlayOpen;
        private bool _isXeniaPatchesBusy;
        private bool _isCurrentSectionXeniaPatchDirty;
        private bool _isXeniaPatchSwitchPromptVisible;
        private bool _isSwitchingCurrentSectionXeniaPatchFile;
        private string _xeniaPatchesStatus = "Select an Xbox 360 game to manage patches.";
        private string? _xeniaDetectedTitleId;
        private string? _xeniaDetectedMediaId;
        private Dictionary<string, string>? _xbox360TitleLookup;
        private readonly HashSet<string> _xbox360MetadataSeededItems = new(StringComparer.OrdinalIgnoreCase);
        private string? _selectedCurrentSectionXeniaPatchFile;
        private string? _pendingCurrentSectionXeniaPatchFile;
        private string? _activeXeniaPatchDocumentPath;
        private string? _activeXeniaPatchDocumentText;
        private readonly AvaloniaList<XeniaPatchFileItem> _currentSectionXeniaPatchFiles = [];
        private readonly AvaloniaList<XeniaPatchEntry> _currentSectionXeniaPatchEntries = [];
        private double _lastSelectedIndexForPreview = double.NaN;
        private string? _pendingGameplayPreviewItemPath;
        private string? _activeGameplayPreviewItemPath;
        private long _gameplayPreviewRequestVersion;
        private Process? _activeEmulatorProcess;
        private CancellationTokenSource? _retroArchLogWatcherCts;
        private CancellationTokenSource? _appTopmostRestoreCts;
        private PendingEmulatorLaunchRequest? _pendingEmulatorLaunchRequest;
        private bool _isClosingActiveEmulatorForRelaunch;
        private bool _appTopmostOverride;
        private bool _appWasTopmostBeforeEmulatorLaunch;
        private IntPtr _appWindowHandleBeforeEmulatorLaunch = IntPtr.Zero;
        private static readonly TimeSpan AppTopmostRestoreTimeout = TimeSpan.FromSeconds(10);
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

        [AutoResolve]
        private EdenEmulatorUpdateService? _edenEmulatorUpdateService;

        [AutoResolve]
        private ShadPs4EmulatorUpdateService? _shadPs4EmulatorUpdateService;

        [AutoResolve]
        private XeniaEmulatorUpdateService? _xeniaEmulatorUpdateService;

        [AutoResolve]
        private Xbox360MetadataService? _xbox360MetadataService;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AlbumListToggleText))]
        private bool _isAlbumListCollapsed;

        [ObservableProperty]
        private bool _showStatisticsOverlay;

        [ObservableProperty]
        private bool _showFrametimeGraph;

        [ObservableProperty]
        private bool _showDetailedGpuInfo;

        [ObservableProperty]
        private double _renderOverlayOpacity = 0.55;

        [ObservableProperty]
        private Stretch _selectedStretch = Stretch.Uniform;

        private Stretch? _sessionCaptureStretchOverride;

        [ObservableProperty]
        private bool _useHostWindowCapture;

        [ObservableProperty]
        private bool _disableVSync;

        [ObservableProperty]
        private int _emulatorCaptureDelayMs = 3000;

        [ObservableProperty]
        private double _renderBrightness = 1.0;

        [ObservableProperty]
        private double _portalCaptureBrightness = 1.0;

        [ObservableProperty]
        private double _renderSaturation = 1.0;

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

        public bool CanShowRenderOptions =>
            SelectedCaptureMode == EmulatorCaptureMode.DirectComposition ||
            CurrentEmulatorHandler?.IsWindowEmbeddingSupported != true;

        [ObservableProperty]
        private bool _isEmulatorRunning;

        [ObservableProperty]
        private bool _isEmulatorLaunchInProgress;

        [ObservableProperty]
        private IntPtr _emulatorTargetHwnd;

        [ObservableProperty]
        private int _emulatorTargetProcessId;

        [ObservableProperty]
        private bool _requestStopEmulatorCapture;

        [ObservableProperty]
        private bool _isRenderOptionsOpen;

        [ObservableProperty]
        private bool _isFullscreen;

        [ObservableProperty]
        private bool _isRetroArchErrorOverlayOpen;

        [ObservableProperty]
        private string? _retroArchErrorSummary;

        [ObservableProperty]
        private string? _retroArchErrorDetails;

        public bool HasRetroArchError => !string.IsNullOrWhiteSpace(RetroArchErrorSummary);

        partial void OnRetroArchErrorSummaryChanged(string? value) => OnPropertyChanged(nameof(HasRetroArchError));

        [ObservableProperty]
        private int _renderOptionsSelectedTabIndex;

        [ObservableProperty]
        private EmulatorCaptureMode _selectedCaptureMode = EmulatorCaptureMode.DirectComposition;

        [ObservableProperty]
        private IEmulatorHandler? _currentEmulatorHandler;

        partial void OnCurrentEmulatorHandlerChanged(IEmulatorHandler? value)
        {
            SelectedCaptureMode = EmulatorCaptureMode.DirectComposition;

            OnPropertyChanged(nameof(CanShowRenderOptions));
            OnPropertyChanged(nameof(ForceUseTargetClientAreaCapture));
            OnPropertyChanged(nameof(HideTargetWindowAfterCaptureStarts));
            OnPropertyChanged(nameof(ClientAreaCropLeftInset));
            OnPropertyChanged(nameof(ClientAreaCropTopInset));
            OnPropertyChanged(nameof(ClientAreaCropRightInset));
            OnPropertyChanged(nameof(ClientAreaCropBottomInset));
            OnPropertyChanged(nameof(CurrentEmulatorWindowTitleHint));
            OnPropertyChanged(nameof(CurrentCaptureStretch));
            RefreshCurrentSectionLaunchOptionsState();
        }

        public bool IsCurrentSectionXeniaPatchDirty
        {
            get => _isCurrentSectionXeniaPatchDirty;
            set
            {
                if (_isCurrentSectionXeniaPatchDirty == value)
                    return;

                _isCurrentSectionXeniaPatchDirty = value;
                OnPropertyChanged();
            }
        }

        public bool IsXeniaPatchSwitchPromptVisible
        {
            get => _isXeniaPatchSwitchPromptVisible;
            set
            {
                if (_isXeniaPatchSwitchPromptVisible == value)
                    return;

                _isXeniaPatchSwitchPromptVisible = value;
                OnPropertyChanged();
            }
        }

        partial void OnSelectedCaptureModeChanged(EmulatorCaptureMode value)
            => OnPropertyChanged(nameof(CanShowRenderOptions));

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmulatorViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayVideoSurfaceVisible))]
        [NotifyPropertyChangedFor(nameof(IsCarouselVisible))]
        [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
        private bool _isEmulatorViewportDismissed;

        public bool IsGameplayPreviewAvailable => IsGameplayAutoplayEnabled && IsYtDlpInstalled && !IsEmulatorRunning;
        public bool IsEmulatorViewportVisible => IsEmulatorRunning && !IsEmulatorViewportDismissed;
        public bool IsCompositionCaptureVisible => IsActive && IsEmulatorViewportVisible;
        public bool IsCarouselVisible => !IsEmulatorViewportVisible;
        public bool IsSearchOverlayVisible => MetadataService?.IsImageSearchOverlayOpen == true && !IsCompositionCaptureVisible;
        public bool IsSearchBoxVisible => IsCarouselVisible && !(MetadataService?.IsMetadataLoaded == true);
        public bool IsGameplayPreviewViewportVisible => IsGameplayPreviewHostVisible && !IsEmulatorViewportVisible;
        public bool IsGameplayVideoSurfaceVisible => IsGameplayVideoVisible && !IsEmulatorViewportVisible;
        public bool ForceUseTargetClientAreaCapture => CurrentEmulatorHandler?.ForceUseTargetClientAreaCapture == true;
        public bool HideTargetWindowAfterCaptureStarts => CurrentEmulatorHandler?.HideUntilCaptured != false;
        public int ClientAreaCropLeftInset => CurrentEmulatorHandler?.ClientAreaCropLeftInset ?? 0;
        public int ClientAreaCropTopInset => CurrentEmulatorHandler?.ClientAreaCropTopInset ?? 0;
        public int ClientAreaCropRightInset => CurrentEmulatorHandler?.ClientAreaCropRightInset ?? 0;
        public int ClientAreaCropBottomInset => CurrentEmulatorHandler?.ClientAreaCropBottomInset ?? 0;
        public Stretch CurrentCaptureStretch => _sessionCaptureStretchOverride ?? SelectedStretch;
        public string? CurrentEmulatorWindowTitleHint
        {
            get
            {
                var handler = CurrentEmulatorHandler;
                if (handler == null)
                    return null;

                var launcherPath = handler.LauncherPath;
                if (!string.IsNullOrWhiteSpace(launcherPath))
                {
                    var launcherName = Path.GetFileNameWithoutExtension(
                        launcherPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(launcherName))
                        return launcherName;
                }

                return handler.DisplayName;
            }
        }

        public IReadOnlyList<Stretch> CaptureStretchOptions { get; } = new[] { Stretch.Uniform, Stretch.UniformToFill, Stretch.Fill };

        [ObservableProperty]
        private IReadOnlyList<ShaderFileItem> _shaderFileItems = LoadShaderFileItems();

        [ObservableProperty]
        private ShaderFileItem _selectedShaderFileItem = new(string.Empty, string.Empty);

        [ObservableProperty]
        private string _selectedShaderPath = string.Empty;

        [ObservableProperty]
        private bool _clearShaderWhenPathEmpty = true;

        private static IReadOnlyList<ShaderFileItem> LoadShaderFileItems()
        {
            var extensions = new[] { "*.glsl", "*.slang", "*.hlsl" };
            var subDirs = new[] { "glsl", "hlsl" };

            var files = new List<string>();
            foreach (var subDir in subDirs)
            {
                var shaderDirectory = Path.Combine(ApplicationPaths.ShadersDirectory, subDir);
                if (!Directory.Exists(shaderDirectory)) continue;

                foreach (var ext in extensions)
                {
                    files.AddRange(Directory.EnumerateFiles(shaderDirectory, ext, SearchOption.TopDirectoryOnly));
                }
            }

            var entries = new List<ShaderFileItem> { new(string.Empty, "None") };
            entries.AddRange(files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var extension = Path.GetExtension(path);
                    var isSupportedInDirectComposition =
                        extension.Equals(".hlsl", StringComparison.OrdinalIgnoreCase);

                    var displayName = isSupportedInDirectComposition
                        ? Path.GetFileName(path)
                        : $"{Path.GetFileName(path)} (OpenGL only)";

                    return new ShaderFileItem(path, displayName, isSupportedInDirectComposition);
                }));
            return entries;
        }

        public EmulationViewModel()
        {
            AlbumList.CollectionChanged += AlbumList_CollectionChanged;
            PropertyChanged += EmulationViewModel_PropertyChanged;
            _selectedShaderFileItem = ShaderFileItems.FirstOrDefault() ?? new(string.Empty, string.Empty);
            _selectedShaderPath = _selectedShaderFileItem.FilePath;
            _clearShaderWhenPathEmpty = string.IsNullOrWhiteSpace(_selectedShaderFileItem.FilePath);
        }

        partial void OnSelectedShaderFileItemChanged(ShaderFileItem value)
        {
            if (value is { IsSupportedInDirectComposition: false })
            {
                SelectedShaderPath = string.Empty;
                ClearShaderWhenPathEmpty = true;
            }
            else
            {
                SelectedShaderPath = value?.FilePath ?? string.Empty;
                ClearShaderWhenPathEmpty = string.IsNullOrWhiteSpace(value?.FilePath);
            }

            AutoSave();
        }

        partial void OnSelectedShaderPathChanged(string value) => AutoSave();

        private void RefreshShaderFileItems()
        {
            var currentPath = SelectedShaderPath;
            ShaderFileItems = LoadShaderFileItems();
            SelectedShaderFileItem = ShaderFileItems.FirstOrDefault(item =>
                string.Equals(item.FilePath, currentPath, StringComparison.OrdinalIgnoreCase))
                ?? ShaderFileItems.FirstOrDefault()
                ?? new(string.Empty, string.Empty);
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
            RefreshCurrentSectionLaunchOptionsState();
        }

        partial void OnMetadataServiceChanged(MetadataService? oldValue, MetadataService? newValue)
        {
            if (ReferenceEquals(oldValue, _subscribedMetadataService) && oldValue != null)
                oldValue.PropertyChanged -= MetadataService_PropertyChanged;

            _subscribedMetadataService = null;
            EnsureMetadataServiceSubscription();
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
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
            OnPropertyChanged(nameof(CanShowRenderOptions));
            OnPropertyChanged(nameof(IsGameplayPreviewAvailable));
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
            OnPropertyChanged(nameof(IsGameplayPreviewViewportVisible));
            OnPropertyChanged(nameof(IsGameplayVideoSurfaceVisible));

            if (value)
            {
                IsEmulatorViewportDismissed = false;
                StopGameplayPreview();
                return;
            }

            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();
            UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum ?? SelectedAlbum);

            if (IsActive && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        partial void OnIsEmulatorViewportDismissedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
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

            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
            {
                OnPropertyChanged(nameof(IsSearchBoxVisible));
            }

            if (e.PropertyName == nameof(MetadataService.IsImageSearchOverlayOpen))
            {
                OnPropertyChanged(nameof(IsSearchOverlayVisible));
                OnPropertyChanged(nameof(IsSearchBoxVisible));
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
            Program.EnsureBundledShaderResources();
            RefreshShaderFileItems();
            LoadSettings(); // Load lightweight settings (toggles, opacity, etc)
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


        partial void OnIsAlbumListCollapsedChanged(bool value) => AutoSave();

        partial void OnShowStatisticsOverlayChanged(bool value) => AutoSave();

        partial void OnShowFrametimeGraphChanged(bool value) => AutoSave();

        partial void OnShowDetailedGpuInfoChanged(bool value) => AutoSave();

        partial void OnRenderOverlayOpacityChanged(double value) => AutoSave();

        partial void OnSelectedStretchChanged(Stretch value)
        {
            OnPropertyChanged(nameof(CurrentCaptureStretch));
            AutoSave();
        }

        partial void OnDisableVSyncChanged(bool value) => AutoSave();

        partial void OnRenderBrightnessChanged(double value)
        {
            AutoSave();
            PortalCaptureBrightness = value;
        }

        partial void OnRenderSaturationChanged(double value) => AutoSave();

        private void AutoSave()
        {
            if (IsPrepared)
                SaveSettings();
        }

        partial void OnSearchTextChanged(string? value) => ApplyFilter();

        partial void OnSelectedAlbumChanged(FolderMediaItem? value)
        {
            if (IsEmulatorRunning && !IsEmulatorViewportDismissed)
                IsEmulatorViewportDismissed = true;

            if (!IsEmulatorRunning)
                UpdateCurrentEmulatorHandlerForSelection(value);

            SyncSelectedAlbumIndexFromAlbum(value);
            RefreshCurrentSectionLaunchOptionsState();
            AutoSave();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            if (IsEmulatorRunning && !IsEmulatorViewportDismissed)
                IsEmulatorViewportDismissed = true;

            if (!IsEmulatorRunning)
                UpdateCurrentEmulatorHandlerForSelection(value ?? SelectedAlbum);

            ApplyFilter();
            QueueSelectedAlbumCoverScan(value);
            RefreshActiveAlbumState();
            RefreshCurrentSectionLaunchOptionsState();
        }

        public EmulationSectionItem? CurrentEmulationSectionItem
        {
            get
            {
                var sectionTitle = (SelectedAlbum ?? LoadedAlbum)?.Title;
                if (string.IsNullOrWhiteSpace(sectionTitle))
                    return null;

                return SettingsViewModel?.EmulationSections.FirstOrDefault(section =>
                    string.Equals(section.SectionTitle, sectionTitle, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IReadOnlyList<string> CurrentSectionRetroArchCores =>
            (IReadOnlyList<string>?)CurrentEmulationSectionItem?.RetroArchCores ?? Array.Empty<string>();

        [ObservableProperty]
        private string? _selectedCurrentSectionRetroArchCore;

        private string? _currentSectionEdenRepositoryOverride;
        public string? CurrentSectionEdenRepositoryOverride
        {
            get => _currentSectionEdenRepositoryOverride;
            set
            {
                if (string.Equals(_currentSectionEdenRepositoryOverride, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenRepositoryOverride = value;
                OnPropertyChanged();

                if (!_isSyncingCurrentSectionEdenRepositoryOverride)
                    IsCurrentSectionEdenRepositoryDirty = true;
            }
        }

        public bool IsCurrentSectionEdenRepositoryDirty
        {
            get => _isCurrentSectionEdenRepositoryDirty;
            set
            {
                if (_isCurrentSectionEdenRepositoryDirty == value)
                    return;

                _isCurrentSectionEdenRepositoryDirty = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionEdenPrereleases;
        public bool IncludeCurrentSectionEdenPrereleases
        {
            get => _includeCurrentSectionEdenPrereleases;
            set
            {
                if (_includeCurrentSectionEdenPrereleases == value)
                    return;

                _includeCurrentSectionEdenPrereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionEdenIncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeEdenPrereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionEdenInfo();
            }
        }

        private string? _currentSectionShadPs4RepositoryOverride;
        public string? CurrentSectionShadPs4RepositoryOverride
        {
            get => _currentSectionShadPs4RepositoryOverride;
            set
            {
                if (string.Equals(_currentSectionShadPs4RepositoryOverride, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4RepositoryOverride = value;
                OnPropertyChanged();

                if (!_isSyncingCurrentSectionShadPs4RepositoryOverride)
                    IsCurrentSectionShadPs4RepositoryDirty = true;
            }
        }

        private AvaloniaList<string> _currentSectionShadPs4AvailableVersions = [];
        public AvaloniaList<string> CurrentSectionShadPs4AvailableVersions
        {
            get => _currentSectionShadPs4AvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionShadPs4AvailableVersions, value))
                    return;

                _currentSectionShadPs4AvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionShadPs4Version;
        public string? SelectedCurrentSectionShadPs4Version
        {
            get => _selectedCurrentSectionShadPs4Version;
            set
            {
                if (string.Equals(_selectedCurrentSectionShadPs4Version, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionShadPs4Version = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionShadPs4VersionChanged(value);
            }
        }

        private string? _currentSectionShadPs4CurrentVersion;
        public string? CurrentSectionShadPs4CurrentVersion
        {
            get => _currentSectionShadPs4CurrentVersion;
            set
            {
                if (string.Equals(_currentSectionShadPs4CurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4CurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionShadPs4LatestVersion;
        public string? CurrentSectionShadPs4LatestVersion
        {
            get => _currentSectionShadPs4LatestVersion;
            set
            {
                if (string.Equals(_currentSectionShadPs4LatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4LatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionShadPs4Status = "Select a shadPS4 section to manage updates.";
        public string CurrentSectionShadPs4Status
        {
            get => _currentSectionShadPs4Status;
            set
            {
                if (string.Equals(_currentSectionShadPs4Status, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4Status = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionShadPs4UpdateAvailable;
        public bool IsCurrentSectionShadPs4UpdateAvailable
        {
            get => _isCurrentSectionShadPs4UpdateAvailable;
            set
            {
                if (_isCurrentSectionShadPs4UpdateAvailable == value)
                    return;

                _isCurrentSectionShadPs4UpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionShadPs4Busy;
        public bool IsCurrentSectionShadPs4Busy
        {
            get => _isCurrentSectionShadPs4Busy;
            set
            {
                if (_isCurrentSectionShadPs4Busy == value)
                    return;

                _isCurrentSectionShadPs4Busy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionShadPs4Downloading;
        public bool IsCurrentSectionShadPs4Downloading
        {
            get => _isCurrentSectionShadPs4Downloading;
            set
            {
                if (_isCurrentSectionShadPs4Downloading == value)
                    return;

                _isCurrentSectionShadPs4Downloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionShadPs4DownloadProgress;
        public double CurrentSectionShadPs4DownloadProgress
        {
            get => _currentSectionShadPs4DownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionShadPs4DownloadProgress - value) < 0.01)
                    return;

                _currentSectionShadPs4DownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionShadPs4EmulatorPath;
        public string? CurrentSectionShadPs4EmulatorPath
        {
            get => _currentSectionShadPs4EmulatorPath;
            set
            {
                if (string.Equals(_currentSectionShadPs4EmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4EmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionShadPs4UpdatePath;
        public string? CurrentSectionShadPs4UpdatePath
        {
            get => _currentSectionShadPs4UpdatePath;
            set
            {
                if (string.Equals(_currentSectionShadPs4UpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionShadPs4UpdatePath = value;
                OnPropertyChanged();
            }
        }

        public bool IsCurrentSectionShadPs4RepositoryDirty
        {
            get => _isCurrentSectionShadPs4RepositoryDirty;
            set
            {
                if (_isCurrentSectionShadPs4RepositoryDirty == value)
                    return;

                _isCurrentSectionShadPs4RepositoryDirty = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionShadPs4Prereleases;
        public bool IncludeCurrentSectionShadPs4Prereleases
        {
            get => _includeCurrentSectionShadPs4Prereleases;
            set
            {
                if (_includeCurrentSectionShadPs4Prereleases == value)
                    return;

                _includeCurrentSectionShadPs4Prereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionShadPs4IncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeShadPs4Prereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionShadPs4Info();
            }
        }

        private AvaloniaList<string> _currentSectionEdenAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionEdenAvailableVersions
        {
            get => _currentSectionEdenAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionEdenAvailableVersions, value))
                    return;

                _currentSectionEdenAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionEdenVersion;
        public string? SelectedCurrentSectionEdenVersion
        {
            get => _selectedCurrentSectionEdenVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionEdenVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionEdenVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionEdenVersionChanged(value);
            }
        }

        private string? _currentSectionEdenCurrentVersion;
        public string? CurrentSectionEdenCurrentVersion
        {
            get => _currentSectionEdenCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionEdenCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionEdenLatestVersion;
        public string? CurrentSectionEdenLatestVersion
        {
            get => _currentSectionEdenLatestVersion;
            set
            {
                if (string.Equals(_currentSectionEdenLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionEdenStatus = "Select an Eden section to manage updates.";
        public string CurrentSectionEdenStatus
        {
            get => _currentSectionEdenStatus;
            set
            {
                if (string.Equals(_currentSectionEdenStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionEdenUpdateAvailable;
        public bool IsCurrentSectionEdenUpdateAvailable
        {
            get => _isCurrentSectionEdenUpdateAvailable;
            set
            {
                if (_isCurrentSectionEdenUpdateAvailable == value)
                    return;

                _isCurrentSectionEdenUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionEdenBusy;
        public bool IsCurrentSectionEdenBusy
        {
            get => _isCurrentSectionEdenBusy;
            set
            {
                if (_isCurrentSectionEdenBusy == value)
                    return;

                _isCurrentSectionEdenBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionEdenDownloading;
        public bool IsCurrentSectionEdenDownloading
        {
            get => _isCurrentSectionEdenDownloading;
            set
            {
                if (_isCurrentSectionEdenDownloading == value)
                    return;

                _isCurrentSectionEdenDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionEdenDownloadProgress;
        public double CurrentSectionEdenDownloadProgress
        {
            get => _currentSectionEdenDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionEdenDownloadProgress - value) < 0.01)
                    return;

                _currentSectionEdenDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionEdenEmulatorPath;
        public string? CurrentSectionEdenEmulatorPath
        {
            get => _currentSectionEdenEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionEdenEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionEdenUpdatePath;
        public string? CurrentSectionEdenUpdatePath
        {
            get => _currentSectionEdenUpdatePath;
            set
            {
                if (string.Equals(_currentSectionEdenUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionEdenUpdatePath = value;
                OnPropertyChanged();
            }
        }

        private IAsyncRelayCommand? _applyCurrentSectionEdenRepositoryCommand;
        public IAsyncRelayCommand ApplyCurrentSectionEdenRepositoryCommand =>
            _applyCurrentSectionEdenRepositoryCommand ??= new AsyncRelayCommand(ApplyCurrentSectionEdenRepository);

        private IAsyncRelayCommand? _openCurrentSectionEdenUpdatesCommand;
        public IAsyncRelayCommand OpenCurrentSectionEdenUpdatesCommand =>
            _openCurrentSectionEdenUpdatesCommand ??= new AsyncRelayCommand(OpenCurrentSectionEdenUpdates);

        private IAsyncRelayCommand? _applyCurrentSectionShadPs4RepositoryCommand;
        public IAsyncRelayCommand ApplyCurrentSectionShadPs4RepositoryCommand =>
            _applyCurrentSectionShadPs4RepositoryCommand ??= new AsyncRelayCommand(ApplyCurrentSectionShadPs4Repository);

        private AvaloniaList<string> _currentSectionXeniaAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionXeniaAvailableVersions
        {
            get => _currentSectionXeniaAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionXeniaAvailableVersions, value))
                    return;

                _currentSectionXeniaAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionXeniaVersion;
        public string? SelectedCurrentSectionXeniaVersion
        {
            get => _selectedCurrentSectionXeniaVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionXeniaVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionXeniaVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionXeniaVersionChanged(value);
            }
        }

        private string? _currentSectionXeniaCurrentVersion;
        public string? CurrentSectionXeniaCurrentVersion
        {
            get => _currentSectionXeniaCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionXeniaCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionXeniaCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionXeniaLatestVersion;
        public string? CurrentSectionXeniaLatestVersion
        {
            get => _currentSectionXeniaLatestVersion;
            set
            {
                if (string.Equals(_currentSectionXeniaLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionXeniaLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionXeniaStatus = "Select a Xenia section to manage updates.";
        public string CurrentSectionXeniaStatus
        {
            get => _currentSectionXeniaStatus;
            set
            {
                if (string.Equals(_currentSectionXeniaStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionXeniaStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionXeniaUpdateAvailable;
        public bool IsCurrentSectionXeniaUpdateAvailable
        {
            get => _isCurrentSectionXeniaUpdateAvailable;
            set
            {
                if (_isCurrentSectionXeniaUpdateAvailable == value)
                    return;

                _isCurrentSectionXeniaUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionXeniaBusy;
        public bool IsCurrentSectionXeniaBusy
        {
            get => _isCurrentSectionXeniaBusy;
            set
            {
                if (_isCurrentSectionXeniaBusy == value)
                    return;

                _isCurrentSectionXeniaBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionXeniaDownloading;
        public bool IsCurrentSectionXeniaDownloading
        {
            get => _isCurrentSectionXeniaDownloading;
            set
            {
                if (_isCurrentSectionXeniaDownloading == value)
                    return;

                _isCurrentSectionXeniaDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionXeniaDownloadProgress;
        public double CurrentSectionXeniaDownloadProgress
        {
            get => _currentSectionXeniaDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionXeniaDownloadProgress - value) < 0.01)
                    return;

                _currentSectionXeniaDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionXeniaEmulatorPath;
        public string? CurrentSectionXeniaEmulatorPath
        {
            get => _currentSectionXeniaEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionXeniaEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionXeniaEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionXeniaUpdatePath;
        public string? CurrentSectionXeniaUpdatePath
        {
            get => _currentSectionXeniaUpdatePath;
            set
            {
                if (string.Equals(_currentSectionXeniaUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionXeniaUpdatePath = value;
                OnPropertyChanged();
            }
        }

        public bool IsXeniaPatchesOverlayOpen
        {
            get => _isXeniaPatchesOverlayOpen;
            set
            {
                if (_isXeniaPatchesOverlayOpen == value)
                    return;

                _isXeniaPatchesOverlayOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsXeniaPatchesBusy
        {
            get => _isXeniaPatchesBusy;
            set
            {
                if (_isXeniaPatchesBusy == value)
                    return;

                _isXeniaPatchesBusy = value;
                OnPropertyChanged();
            }
        }

        public string XeniaPatchesStatus
        {
            get => _xeniaPatchesStatus;
            set
            {
                if (string.Equals(_xeniaPatchesStatus, value, StringComparison.Ordinal))
                    return;

                _xeniaPatchesStatus = value;
                OnPropertyChanged();
            }
        }

        public string? XeniaDetectedTitleId
        {
            get => _xeniaDetectedTitleId;
            set
            {
                if (string.Equals(_xeniaDetectedTitleId, value, StringComparison.Ordinal))
                    return;

                _xeniaDetectedTitleId = value;
                OnPropertyChanged();
            }
        }

        public string? XeniaDetectedMediaId
        {
            get => _xeniaDetectedMediaId;
            set
            {
                if (string.Equals(_xeniaDetectedMediaId, value, StringComparison.Ordinal))
                    return;

                _xeniaDetectedMediaId = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<XeniaPatchFileItem> CurrentSectionXeniaPatchFiles => _currentSectionXeniaPatchFiles;

        public string? SelectedCurrentSectionXeniaPatchFile
        {
            get => _selectedCurrentSectionXeniaPatchFile;
            set
            {
                if (string.Equals(_selectedCurrentSectionXeniaPatchFile, value, StringComparison.Ordinal))
                    return;

                var hasUnsavedChanges = IsCurrentSectionXeniaPatchDirty && !string.IsNullOrWhiteSpace(_activeXeniaPatchDocumentPath);
                if (!_isSwitchingCurrentSectionXeniaPatchFile &&
                    hasUnsavedChanges &&
                    !string.Equals(value, _activeXeniaPatchDocumentPath, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingCurrentSectionXeniaPatchFile = value;
                    IsXeniaPatchSwitchPromptVisible = true;
                    XeniaPatchesStatus = "You have unsaved patch changes. Save or Skip before switching patch file.";
                    OnPropertyChanged(nameof(SelectedCurrentSectionXeniaPatchFile));
                    return;
                }

                _selectedCurrentSectionXeniaPatchFile = value;
                OnPropertyChanged();
                IsXeniaPatchSwitchPromptVisible = false;
                LoadSelectedXeniaPatchEntries(value);
            }
        }

        public AvaloniaList<XeniaPatchEntry> CurrentSectionXeniaPatchEntries => _currentSectionXeniaPatchEntries;

        partial void OnSelectedCurrentSectionRetroArchCoreChanged(string? value)
        {
            if (_isSyncingCurrentSectionCoreSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section == null || string.Equals(section.SelectedRetroArchCore, value, StringComparison.OrdinalIgnoreCase))
                return;

            section.SelectedRetroArchCore = value;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionShadPs4VersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionShadPs4VersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedShadPs4Version, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedShadPs4Version = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private async Task ApplyCurrentSectionEdenRepository()
        {
            if (!ShowCurrentSectionEdenUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(CurrentSectionEdenRepositoryOverride)
                ? null
                : CurrentSectionEdenRepositoryOverride.Trim();

            if (!string.Equals(section.LaunchSettings.EdenRepositoryOverride, normalized, StringComparison.OrdinalIgnoreCase))
            {
                section.LaunchSettings.EdenRepositoryOverride = normalized;
                SettingsViewModel?.SaveSettings();
            }

            IsCurrentSectionEdenRepositoryDirty = false;
            await RefreshCurrentSectionEdenInfo();
        }

        private async Task ApplyCurrentSectionShadPs4Repository()
        {
            if (!ShowCurrentSectionShadPs4UpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(CurrentSectionShadPs4RepositoryOverride)
                ? null
                : CurrentSectionShadPs4RepositoryOverride.Trim();

            if (!string.Equals(section.LaunchSettings.ShadPs4RepositoryOverride, normalized, StringComparison.OrdinalIgnoreCase))
            {
                section.LaunchSettings.ShadPs4RepositoryOverride = normalized;
                SettingsViewModel?.SaveSettings();
            }

            IsCurrentSectionShadPs4RepositoryDirty = false;
            await RefreshCurrentSectionShadPs4Info();
        }

        private void OnSelectedCurrentSectionEdenVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionEdenVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedEdenVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedEdenVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionXeniaVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionXeniaVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedXeniaVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedXeniaVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        public bool ShowCurrentSectionRetroArchCoreSelection =>
            CurrentEmulatorHandler?.UsesRetroArchCores == true &&
            CurrentSectionRetroArchCores.Count > 0;

        public bool ShowCurrentSectionEdenUpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, EdenHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionShadPs4UpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, ShadPs4Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionXeniaUpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionXeniaPatchesMenuItem =>
            ShowCurrentSectionXeniaUpdateControls && HasActiveAlbumItems;

        public bool IsCurrentSectionHandlerUpdateAvailable =>
            (ShowCurrentSectionEdenUpdateControls && IsCurrentSectionEdenUpdateAvailable) ||
            (ShowCurrentSectionShadPs4UpdateControls && IsCurrentSectionShadPs4UpdateAvailable) ||
            (ShowCurrentSectionXeniaUpdateControls && IsCurrentSectionXeniaUpdateAvailable);

        private void RefreshCurrentSectionLaunchOptionsState()
        {
            var sectionCore = CurrentEmulationSectionItem?.SelectedRetroArchCore;
            if (!string.Equals(SelectedCurrentSectionRetroArchCore, sectionCore, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionCoreSelection = true;
                    SelectedCurrentSectionRetroArchCore = sectionCore;
                }
                finally
                {
                    _isSyncingCurrentSectionCoreSelection = false;
                }
            }

            var section = CurrentEmulationSectionItem;
            var sectionRepoOverride = section?.LaunchSettings?.EdenRepositoryOverride;
            if (!string.Equals(CurrentSectionEdenRepositoryOverride, sectionRepoOverride, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionEdenRepositoryOverride = true;
                    CurrentSectionEdenRepositoryOverride = sectionRepoOverride;
                }
                finally
                {
                    _isSyncingCurrentSectionEdenRepositoryOverride = false;
                }
            }

            IsCurrentSectionEdenRepositoryDirty = false;

            var includeEdenPrereleases = section?.LaunchSettings?.IncludeEdenPrereleases == true;
            if (IncludeCurrentSectionEdenPrereleases != includeEdenPrereleases)
            {
                try
                {
                    _isSyncingCurrentSectionEdenIncludePrereleases = true;
                    IncludeCurrentSectionEdenPrereleases = includeEdenPrereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionEdenIncludePrereleases = false;
                }
            }

            var sectionEdenVersion = section?.LaunchSettings?.SelectedEdenVersion;
            if (!string.Equals(SelectedCurrentSectionEdenVersion, sectionEdenVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionEdenVersionSelection = true;
                    SelectedCurrentSectionEdenVersion = sectionEdenVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionEdenVersionSelection = false;
                }
            }

            OnPropertyChanged(nameof(CurrentEmulationSectionItem));
            OnPropertyChanged(nameof(CurrentSectionRetroArchCores));
            OnPropertyChanged(nameof(ShowCurrentSectionRetroArchCoreSelection));
            OnPropertyChanged(nameof(ShowCurrentSectionEdenUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionShadPs4UpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionXeniaUpdateControls));
            OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));

            if (ShowCurrentSectionEdenUpdateControls)
            {
                _ = RefreshCurrentSectionEdenInfo();
            }
            else
            {
                CurrentSectionEdenAvailableVersions.Clear();
                CurrentSectionEdenCurrentVersion = null;
                CurrentSectionEdenLatestVersion = null;
                CurrentSectionEdenStatus = "Select an Eden section to manage updates.";
                IsCurrentSectionEdenUpdateAvailable = false;
                CurrentSectionEdenEmulatorPath = null;
                CurrentSectionEdenUpdatePath = null;
                IsCurrentSectionEdenRepositoryDirty = false;
                try
                {
                    _isSyncingCurrentSectionEdenIncludePrereleases = true;
                    IncludeCurrentSectionEdenPrereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionEdenIncludePrereleases = false;
                }
            }

            var sectionShadPs4RepoOverride = section?.LaunchSettings?.ShadPs4RepositoryOverride;
            if (!string.Equals(CurrentSectionShadPs4RepositoryOverride, sectionShadPs4RepoOverride, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = true;
                    CurrentSectionShadPs4RepositoryOverride = sectionShadPs4RepoOverride;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = false;
                }
            }

            IsCurrentSectionShadPs4RepositoryDirty = false;

            var includeShadPs4Prereleases = section?.LaunchSettings?.IncludeShadPs4Prereleases == true;
            if (IncludeCurrentSectionShadPs4Prereleases != includeShadPs4Prereleases)
            {
                try
                {
                    _isSyncingCurrentSectionShadPs4IncludePrereleases = true;
                    IncludeCurrentSectionShadPs4Prereleases = includeShadPs4Prereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4IncludePrereleases = false;
                }
            }

            var sectionShadPs4Version = section?.LaunchSettings?.SelectedShadPs4Version;
            if (!string.Equals(SelectedCurrentSectionShadPs4Version, sectionShadPs4Version, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionShadPs4VersionSelection = true;
                    SelectedCurrentSectionShadPs4Version = sectionShadPs4Version;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4VersionSelection = false;
                }
            }

            if (ShowCurrentSectionShadPs4UpdateControls)
            {
                _ = RefreshCurrentSectionShadPs4Info();
            }
            else
            {
                CurrentSectionShadPs4AvailableVersions.Clear();
                CurrentSectionShadPs4CurrentVersion = null;
                CurrentSectionShadPs4LatestVersion = null;
                CurrentSectionShadPs4Status = "Select a shadPS4 section to manage updates.";
                IsCurrentSectionShadPs4UpdateAvailable = false;
                CurrentSectionShadPs4EmulatorPath = null;
                CurrentSectionShadPs4UpdatePath = null;
                CurrentSectionShadPs4DownloadProgress = 0;
                IsCurrentSectionShadPs4Downloading = false;
                IsCurrentSectionShadPs4RepositoryDirty = false;
                try
                {
                    _isSyncingCurrentSectionShadPs4IncludePrereleases = true;
                    IncludeCurrentSectionShadPs4Prereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4IncludePrereleases = false;
                }
            }

            var sectionXeniaVersion = section?.LaunchSettings?.SelectedXeniaVersion;
            if (!string.Equals(SelectedCurrentSectionXeniaVersion, sectionXeniaVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionXeniaVersionSelection = true;
                    SelectedCurrentSectionXeniaVersion = sectionXeniaVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionXeniaVersionSelection = false;
                }
            }

            if (ShowCurrentSectionXeniaUpdateControls)
            {
                _ = RefreshCurrentSectionXeniaInfo();
            }
            else
            {
                CurrentSectionXeniaAvailableVersions.Clear();
                CurrentSectionXeniaCurrentVersion = null;
                CurrentSectionXeniaLatestVersion = null;
                CurrentSectionXeniaStatus = "Select a Xenia section to manage updates.";
                IsCurrentSectionXeniaUpdateAvailable = false;
                CurrentSectionXeniaEmulatorPath = null;
                CurrentSectionXeniaUpdatePath = null;
                CurrentSectionXeniaDownloadProgress = 0;
                IsCurrentSectionXeniaDownloading = false;
            }

            if (!ShowCurrentSectionXeniaUpdateControls)
            {
                IsXeniaPatchesOverlayOpen = false;
                XeniaDetectedTitleId = null;
                XeniaDetectedMediaId = null;
                XeniaPatchesStatus = "Select an Xbox 360 game to manage patches.";
                IsXeniaPatchSwitchPromptVisible = false;
                IsCurrentSectionXeniaPatchDirty = false;
                _pendingCurrentSectionXeniaPatchFile = null;
                _activeXeniaPatchDocumentPath = null;
                _activeXeniaPatchDocumentText = null;
                CurrentSectionXeniaPatchFiles.Clear();
                DetachXeniaPatchEntryListeners();
                CurrentSectionXeniaPatchEntries.Clear();
                SelectedCurrentSectionXeniaPatchFile = null;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionEdenInfo()
        {
            if (!ShowCurrentSectionEdenUpdateControls)
            {
                CurrentSectionEdenStatus = "Select an Eden section to manage updates.";
                CurrentSectionEdenAvailableVersions.Clear();
                CurrentSectionEdenCurrentVersion = null;
                CurrentSectionEdenLatestVersion = null;
                IsCurrentSectionEdenUpdateAvailable = false;
                CurrentSectionEdenEmulatorPath = null;
                CurrentSectionEdenUpdatePath = null;
                CurrentSectionEdenDownloadProgress = 0;
                IsCurrentSectionEdenDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _edenEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionEdenBusy = true;
            IsCurrentSectionEdenDownloading = false;
            CurrentSectionEdenDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionEdenRepositoryOverride,
                    IncludeCurrentSectionEdenPrereleases,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyEdenUpdateState(state));
            }
            finally
            {
                IsCurrentSectionEdenBusy = false;
                IsCurrentSectionEdenDownloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionXeniaInfo()
        {
            if (!ShowCurrentSectionXeniaUpdateControls)
            {
                CurrentSectionXeniaStatus = "Select a Xenia section to manage updates.";
                CurrentSectionXeniaAvailableVersions.Clear();
                CurrentSectionXeniaCurrentVersion = null;
                CurrentSectionXeniaLatestVersion = null;
                IsCurrentSectionXeniaUpdateAvailable = false;
                CurrentSectionXeniaEmulatorPath = null;
                CurrentSectionXeniaUpdatePath = null;
                CurrentSectionXeniaDownloadProgress = 0;
                IsCurrentSectionXeniaDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _xeniaEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionXeniaBusy = true;
            IsCurrentSectionXeniaDownloading = false;
            CurrentSectionXeniaDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyXeniaUpdateState(state));
            }
            finally
            {
                IsCurrentSectionXeniaBusy = false;
                IsCurrentSectionXeniaDownloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionXenia()
        {
            if (!ShowCurrentSectionXeniaUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _xeniaEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionXeniaBusy = true;
            IsCurrentSectionXeniaDownloading = true;
            CurrentSectionXeniaDownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    SelectedCurrentSectionXeniaVersion).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionXeniaDownloadProgress = 100;
                    ApplyXeniaUpdateState(state);

                    if (!string.IsNullOrWhiteSpace(state.ResolvedLauncherPath) &&
                        !string.Equals(handler.LauncherPath, state.ResolvedLauncherPath, StringComparison.OrdinalIgnoreCase))
                    {
                        handler.LauncherPath = state.ResolvedLauncherPath;
                        SettingsViewModel?.SaveSettings();
                    }
                });
            }
            finally
            {
                IsCurrentSectionXeniaBusy = false;
                IsCurrentSectionXeniaDownloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionShadPs4Info()
        {
            if (!ShowCurrentSectionShadPs4UpdateControls)
            {
                CurrentSectionShadPs4Status = "Select a shadPS4 section to manage updates.";
                CurrentSectionShadPs4AvailableVersions.Clear();
                CurrentSectionShadPs4CurrentVersion = null;
                CurrentSectionShadPs4LatestVersion = null;
                IsCurrentSectionShadPs4UpdateAvailable = false;
                CurrentSectionShadPs4EmulatorPath = null;
                CurrentSectionShadPs4UpdatePath = null;
                CurrentSectionShadPs4DownloadProgress = 0;
                IsCurrentSectionShadPs4Downloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _shadPs4EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionShadPs4Busy = true;
            IsCurrentSectionShadPs4Downloading = false;
            CurrentSectionShadPs4DownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionShadPs4RepositoryOverride,
                    IncludeCurrentSectionShadPs4Prereleases,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyShadPs4UpdateState(state));
            }
            finally
            {
                IsCurrentSectionShadPs4Busy = false;
                IsCurrentSectionShadPs4Downloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionShadPs4()
        {
            if (!ShowCurrentSectionShadPs4UpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _shadPs4EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionShadPs4Busy = true;
            IsCurrentSectionShadPs4Downloading = true;
            CurrentSectionShadPs4DownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionShadPs4RepositoryOverride,
                    IncludeCurrentSectionShadPs4Prereleases,
                    SelectedCurrentSectionShadPs4Version).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionShadPs4DownloadProgress = 100;
                    ApplyShadPs4UpdateState(state);

                    if (!string.IsNullOrWhiteSpace(state.ResolvedLauncherPath) &&
                        !string.Equals(handler.LauncherPath, state.ResolvedLauncherPath, StringComparison.OrdinalIgnoreCase))
                    {
                        handler.LauncherPath = state.ResolvedLauncherPath;
                        SettingsViewModel?.SaveSettings();
                    }
                });
            }
            finally
            {
                IsCurrentSectionShadPs4Busy = false;
                IsCurrentSectionShadPs4Downloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionEden()
        {
            if (!ShowCurrentSectionEdenUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _edenEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionEdenBusy = true;
            IsCurrentSectionEdenDownloading = true;
            CurrentSectionEdenDownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionEdenRepositoryOverride,
                    IncludeCurrentSectionEdenPrereleases,
                    SelectedCurrentSectionEdenVersion).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionEdenDownloadProgress = 100;
                    ApplyEdenUpdateState(state);

                    if (!string.IsNullOrWhiteSpace(state.ResolvedLauncherPath) &&
                        !string.Equals(handler.LauncherPath, state.ResolvedLauncherPath, StringComparison.OrdinalIgnoreCase))
                    {
                        handler.LauncherPath = state.ResolvedLauncherPath;
                        SettingsViewModel?.SaveSettings();
                    }
                });
            }
            finally
            {
                IsCurrentSectionEdenBusy = false;
                IsCurrentSectionEdenDownloading = false;
            }
        }

        private void ApplyEdenUpdateState(EdenUpdateState state)
        {
            CurrentSectionEdenCurrentVersion = state.CurrentVersion;
            CurrentSectionEdenLatestVersion = state.LatestVersion;
            IsCurrentSectionEdenUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionEdenStatus = state.StatusMessage;
            CurrentSectionEdenEmulatorPath = state.EmulatorDirectory;
            CurrentSectionEdenUpdatePath = state.UpdateDirectory;

            CurrentSectionEdenAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionEdenAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionEdenVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionEdenAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionEdenAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionEdenVersionSelection = true;
                SelectedCurrentSectionEdenVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionEdenVersionSelection = false;
            }

            if (!string.IsNullOrWhiteSpace(state.Repository) &&
                !string.Equals(CurrentSectionEdenRepositoryOverride, state.Repository, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state.Repository, "eden-emu/eden", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionEdenRepositoryOverride = true;
                    CurrentSectionEdenRepositoryOverride = state.Repository;
                }
                finally
                {
                    _isSyncingCurrentSectionEdenRepositoryOverride = false;
                }
            }
        }

        private void ApplyXeniaUpdateState(XeniaUpdateState state)
        {
            CurrentSectionXeniaCurrentVersion = state.CurrentVersion;
            CurrentSectionXeniaLatestVersion = state.LatestVersion;
            IsCurrentSectionXeniaUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionXeniaStatus = state.StatusMessage;
            CurrentSectionXeniaEmulatorPath = state.EmulatorDirectory;
            CurrentSectionXeniaUpdatePath = state.UpdateDirectory;

            CurrentSectionXeniaAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionXeniaAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionXeniaVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionXeniaAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionXeniaAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionXeniaVersionSelection = true;
                SelectedCurrentSectionXeniaVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionXeniaVersionSelection = false;
            }
        }

        [RelayCommand]
        private async Task OpenCurrentSectionXeniaPatches(object? parameter)
        {
            if (!ShowCurrentSectionXeniaPatchesMenuItem)
                return;

            var selectedItem = GetCurrentCarouselSelectedItem();
            var target = parameter switch
            {
                MediaItem mi when CoverItems.Contains(mi) => mi,
                MediaItem mi => mi,
                double selected when !double.IsNaN(selected) => GetCarouselItemByIndex(GetRoundedSelectedIndex(selected)),
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => selectedItem ?? HighlightedItem
            };

            if (target == null)
                return;

            IsXeniaPatchesOverlayOpen = true;
            IsXeniaPatchesBusy = true;
            XeniaPatchesStatus = "Detecting title ID and loading patches...";
            XeniaDetectedTitleId = null;
            XeniaDetectedMediaId = null;
            IsXeniaPatchSwitchPromptVisible = false;
            IsCurrentSectionXeniaPatchDirty = false;
            _pendingCurrentSectionXeniaPatchFile = null;

            try
            {
                var xeniaDirectory = CurrentSectionXeniaEmulatorPath;
                var metadataService = _xbox360MetadataService;
                var availablePatchTitleIds = await Task.Run(() => GetAvailableXeniaPatchTitleIds(xeniaDirectory)).ConfigureAwait(false);
                var metadata = await Task.Run(() => metadataService?.TryReadGameMetadata(target.FileName)).ConfigureAwait(false);
                var titleId = metadata?.TitleId;
                var mediaId = metadata?.MediaId;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    XeniaDetectedTitleId = titleId;
                    XeniaDetectedMediaId = mediaId;
                });

                if (string.IsNullOrWhiteSpace(titleId) &&
                    !string.IsNullOrWhiteSpace(target.FileName) &&
                    !string.IsNullOrWhiteSpace(target.Title))
                {
                    var fallbackMatch = availablePatchTitleIds
                        .Select(id => new { Id = id, Score = ComputeXeniaPatchCandidateScore(target.Title, id, xeniaDirectory) })
                        .Where(static candidate => candidate.Score > 0)
                        .OrderByDescending(static candidate => candidate.Score)
                        .ThenBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (fallbackMatch != null)
                    {
                        titleId = fallbackMatch.Id;
                        await Dispatcher.UIThread.InvokeAsync(() => XeniaDetectedTitleId = titleId);
                    }
                }

                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CurrentSectionXeniaPatchFiles.Clear();
                        DetachXeniaPatchEntryListeners();
                        CurrentSectionXeniaPatchEntries.Clear();
                        IsCurrentSectionXeniaPatchDirty = false;
                        SelectedCurrentSectionXeniaPatchFile = null;
                        XeniaPatchesStatus = "Unable to detect Xbox 360 Title ID for the selected game.";
                    });
                    return;
                }

                var patchFiles = await Task.Run(() => FindXeniaPatchFiles(xeniaDirectory, titleId)).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionXeniaPatchFiles.Clear();
                    foreach (var item in patchFiles)
                        CurrentSectionXeniaPatchFiles.Add(item);

                    DetachXeniaPatchEntryListeners();
                    CurrentSectionXeniaPatchEntries.Clear();
                    IsCurrentSectionXeniaPatchDirty = false;
                    _activeXeniaPatchDocumentPath = null;
                    _activeXeniaPatchDocumentText = null;
                    _pendingCurrentSectionXeniaPatchFile = null;
                    IsXeniaPatchSwitchPromptVisible = false;

                    SelectedCurrentSectionXeniaPatchFile = CurrentSectionXeniaPatchFiles.FirstOrDefault()?.FilePath;

                    XeniaPatchesStatus = CurrentSectionXeniaPatchFiles.Count == 0
                        ? $"No patch files found for title ID {titleId}."
                        : $"Loaded {CurrentSectionXeniaPatchFiles.Count} patch file(s) for title ID {titleId}.";
                });
            }
            finally
            {
                IsXeniaPatchesBusy = false;
            }
        }

        private MediaItem? GetCurrentCarouselSelectedItem()
        {
            var roundedIndex = GetRoundedSelectedIndex(SelectedIndex);
            return GetCarouselItemByIndex(roundedIndex);
        }

        private MediaItem? GetCarouselItemByIndex(int idx)
        {
            if (idx < 0 || idx >= CoverItems.Count)
                return null;

            return CoverItems[idx];
        }

        [RelayCommand]
        private async Task SaveCurrentSectionXeniaPatches()
        {
            await SaveCurrentSectionXeniaPatchesCore().ConfigureAwait(false);
        }

        private async Task<bool> SaveCurrentSectionXeniaPatchesCore()
        {
            if (!IsXeniaPatchesOverlayOpen)
                return false;

            var activePath = _activeXeniaPatchDocumentPath;
            var activeText = _activeXeniaPatchDocumentText;
            if (string.IsNullOrWhiteSpace(activePath) || string.IsNullOrWhiteSpace(activeText))
            {
                XeniaPatchesStatus = "Select a patch file before saving.";
                return false;
            }

            IsXeniaPatchesBusy = true;
            try
            {
                var updated = BuildUpdatedPatchDocument(activeText, CurrentSectionXeniaPatchEntries);
                await Task.Run(() => File.WriteAllText(activePath, updated)).ConfigureAwait(false);
                _activeXeniaPatchDocumentText = updated;
                IsCurrentSectionXeniaPatchDirty = false;
                XeniaPatchesStatus = "Patch settings saved.";
                return true;
            }
            catch (Exception ex)
            {
                XeniaPatchesStatus = $"Failed to save patches: {ex.Message}";
                return false;
            }
            finally
            {
                IsXeniaPatchesBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveAndSwitchCurrentSectionXeniaPatchFile()
        {
            var pending = _pendingCurrentSectionXeniaPatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsXeniaPatchSwitchPromptVisible = false;
                return;
            }

            var saved = await SaveCurrentSectionXeniaPatchesCore().ConfigureAwait(false);
            if (!saved)
                return;

            ApplyPendingCurrentSectionXeniaPatchFileSelection(pending);
        }

        [RelayCommand]
        private void SkipAndSwitchCurrentSectionXeniaPatchFile()
        {
            var pending = _pendingCurrentSectionXeniaPatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsXeniaPatchSwitchPromptVisible = false;
                return;
            }

            ApplyPendingCurrentSectionXeniaPatchFileSelection(pending);
        }

        [RelayCommand]
        private void CloseCurrentSectionXeniaPatches()
        {
            IsXeniaPatchesOverlayOpen = false;
            IsXeniaPatchSwitchPromptVisible = false;
            _pendingCurrentSectionXeniaPatchFile = null;
        }

        private void ApplyPendingCurrentSectionXeniaPatchFileSelection(string patchFilePath)
        {
            _pendingCurrentSectionXeniaPatchFile = null;
            IsXeniaPatchSwitchPromptVisible = false;

            try
            {
                _isSwitchingCurrentSectionXeniaPatchFile = true;
                SelectedCurrentSectionXeniaPatchFile = patchFilePath;
            }
            finally
            {
                _isSwitchingCurrentSectionXeniaPatchFile = false;
            }
        }

        private void LoadSelectedXeniaPatchEntries(string? patchFilePath)
        {
            DetachXeniaPatchEntryListeners();
            CurrentSectionXeniaPatchEntries.Clear();
            _activeXeniaPatchDocumentPath = null;
            _activeXeniaPatchDocumentText = null;
            IsCurrentSectionXeniaPatchDirty = false;

            if (string.IsNullOrWhiteSpace(patchFilePath) || !File.Exists(patchFilePath))
                return;

            try
            {
                var text = File.ReadAllText(patchFilePath);
                var entries = ParseXeniaPatchEntries(text);

                _activeXeniaPatchDocumentPath = patchFilePath;
                _activeXeniaPatchDocumentText = text;
                foreach (var entry in entries)
                {
                    CurrentSectionXeniaPatchEntries.Add(entry);
                    entry.PropertyChanged += OnXeniaPatchEntryPropertyChanged;
                }

                XeniaPatchesStatus = CurrentSectionXeniaPatchEntries.Count == 0
                    ? "Selected file has no patch blocks."
                    : $"Loaded {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
            }
            catch (Exception ex)
            {
                XeniaPatchesStatus = $"Failed to load patch file: {ex.Message}";
            }
        }

        private void OnXeniaPatchEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(XeniaPatchEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionXeniaPatchDirty = true;
        }

        private void DetachXeniaPatchEntryListeners()
        {
            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.PropertyChanged -= OnXeniaPatchEntryPropertyChanged;
        }

        private static IReadOnlyList<XeniaPatchEntry> ParseXeniaPatchEntries(string document)
        {
            var entries = new List<XeniaPatchEntry>();
            if (string.IsNullOrWhiteSpace(document))
                return entries;

            var patchPattern = new Regex(@"\[\[patch\]\](?<body>.*?)(?=\n\s*\[\[patch\]\]|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var namePattern = new Regex(@"^\s*name\s*=\s*\""(?<value>.*?)\""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var descPattern = new Regex(@"^\s*desc\s*=\s*\""(?<value>.*?)\""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var enabledPattern = new Regex(@"^\s*is_enabled\s*=\s*(?<value>true|false)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match match in patchPattern.Matches(document))
            {
                var body = match.Groups["body"].Value;
                var name = namePattern.Match(body).Groups["value"].Value;
                var description = descPattern.Match(body).Groups["value"].Value;
                var enabledMatch = enabledPattern.Match(body);
                var isEnabled = enabledMatch.Success &&
                                bool.TryParse(enabledMatch.Groups["value"].Value, out var parsedEnabled) &&
                                parsedEnabled;

                if (string.IsNullOrWhiteSpace(name))
                    name = "Unnamed patch";

                entries.Add(new XeniaPatchEntry(isEnabled, name, description));
            }

            return entries;
        }

        private static string BuildUpdatedPatchDocument(string original, IEnumerable<XeniaPatchEntry> entries)
        {
            var patchBlocks = new Regex(@"\[\[patch\]\].*?(?=\n\s*\[\[patch\]\]|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var enabledLine = new Regex(@"^(\s*is_enabled\s*=\s*)(true|false)(\s*(#.*)?)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var queue = new Queue<XeniaPatchEntry>(entries);

            return patchBlocks.Replace(original, m =>
            {
                if (queue.Count == 0)
                    return m.Value;

                var entry = queue.Dequeue();
                return enabledLine.Replace(
                    m.Value,
                    enabled => enabled.Groups[1].Value +
                               entry.IsEnabled.ToString().ToLowerInvariant() +
                               enabled.Groups[3].Value,
                    1);
            });
        }

        private static IReadOnlyList<XeniaPatchFileItem> FindXeniaPatchFiles(string? emulatorDirectory, string titleId)
        {
            if (string.IsNullOrWhiteSpace(emulatorDirectory))
                return Array.Empty<XeniaPatchFileItem>();

            var root = Path.Combine(emulatorDirectory, "patches");
            if (!Directory.Exists(root))
                return Array.Empty<XeniaPatchFileItem>();

            var normalizedTitleId = titleId.ToUpperInvariant();
            return Directory
                .EnumerateFiles(root, "*.patch.toml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).StartsWith(normalizedTitleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new XeniaPatchFileItem(path, Path.GetFileNameWithoutExtension(path)))
                .ToArray();
        }

        private static int ComputeXeniaPatchCandidateScore(string gameTitle, string candidateTitleId, string? emulatorDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameTitle) || string.IsNullOrWhiteSpace(candidateTitleId) || string.IsNullOrWhiteSpace(emulatorDirectory))
                return 0;

            try
            {
                var patchesRoot = Path.Combine(emulatorDirectory, "patches");
                if (!Directory.Exists(patchesRoot))
                    return 0;

                var normalizedTitle = NormalizeXeniaPatchSearchText(gameTitle);
                if (normalizedTitle.Length == 0)
                    return 0;

                var candidateFiles = Directory
                    .EnumerateFiles(patchesRoot, "*.patch.toml", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).StartsWith(candidateTitleId, StringComparison.OrdinalIgnoreCase))
                    .Take(4)
                    .ToArray();

                var score = 0;
                foreach (var candidateFile in candidateFiles)
                {
                    var displayName = Path.GetFileNameWithoutExtension(candidateFile);
                    var normalizedDisplayName = NormalizeXeniaPatchSearchText(displayName);
                    if (normalizedDisplayName.Length == 0)
                        continue;

                    if (normalizedDisplayName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        score = Math.Max(score, 100);
                        continue;
                    }

                    var common = LongestCommonXeniaPatchSubstring(normalizedTitle, normalizedDisplayName);
                    if (common >= 6)
                        score = Math.Max(score, common * 4);
                }

                return score;
            }
            catch
            {
                return 0;
            }
        }

        private static string NormalizeXeniaPatchSearchText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var normalized = Regex.Replace(text, "[^A-Za-z0-9]+", " ").Trim();
            normalized = Regex.Replace(normalized, "\\s{2,}", " ");
            return normalized.ToUpperInvariant();
        }

        private static int LongestCommonXeniaPatchSubstring(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return 0;

            var best = 0;
            var table = new int[a.Length + 1, b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    if (a[i - 1] != b[j - 1])
                    {
                        table[i, j] = 0;
                        continue;
                    }

                    table[i, j] = table[i - 1, j - 1] + 1;
                    if (table[i, j] > best)
                        best = table[i, j];
                }
            }

            return best;
        }



        private static HashSet<string> GetAvailableXeniaPatchTitleIds(string? emulatorDirectory)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(emulatorDirectory))
                return set;

            var root = Path.Combine(emulatorDirectory, "patches");
            if (!Directory.Exists(root))
                return set;

            foreach (var path in Directory.EnumerateFiles(root, "*.patch.toml", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);
                var match = Regex.Match(fileName, @"^(?<id>[0-9A-Fa-f]{8})\s*-", RegexOptions.IgnoreCase);
                if (match.Success)
                    set.Add(match.Groups["id"].Value.ToUpperInvariant());
            }

            return set;
        }




        private void ApplyShadPs4UpdateState(ShadPs4UpdateState state)
        {
            CurrentSectionShadPs4CurrentVersion = state.CurrentVersion;
            CurrentSectionShadPs4LatestVersion = state.LatestVersion;
            IsCurrentSectionShadPs4UpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionShadPs4Status = state.StatusMessage;
            CurrentSectionShadPs4EmulatorPath = state.EmulatorDirectory;
            CurrentSectionShadPs4UpdatePath = state.UpdateDirectory;

            CurrentSectionShadPs4AvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionShadPs4AvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionShadPs4Version;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionShadPs4AvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionShadPs4AvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionShadPs4VersionSelection = true;
                SelectedCurrentSectionShadPs4Version = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionShadPs4VersionSelection = false;
            }

            if (!string.IsNullOrWhiteSpace(state.Repository) &&
                !string.Equals(CurrentSectionShadPs4RepositoryOverride, state.Repository, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state.Repository, "shadps4-emu/shadPS4", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = true;
                    CurrentSectionShadPs4RepositoryOverride = state.Repository;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = false;
                }
            }
        }

        private void UpdateCurrentEmulatorHandlerForSelection(FolderMediaItem? album)
        {
            if (album == null)
            {
                CurrentEmulatorHandler = null;
                return;
            }

            var configuredHandler = SettingsViewModel?.GetConfiguredEmulatorHandler(album.Title);
            if (configuredHandler == null)
            {
                CurrentEmulatorHandler = null;
                return;
            }

            CurrentEmulatorHandler = configuredHandler;
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
            IsRenderOptionsOpen = !IsRenderOptionsOpen;
        }

        private async Task OpenCurrentSectionEdenUpdates()
        {
            IsRenderOptionsOpen = true;
            RenderOptionsSelectedTabIndex = 1;
            if (ShowCurrentSectionEdenUpdateControls)
                await RefreshCurrentSectionEdenInfo();
            else if (ShowCurrentSectionShadPs4UpdateControls)
                await RefreshCurrentSectionShadPs4Info();
        }

        [RelayCommand]
        private void ToggleFullscreen()
        {
            if (!IsEmulatorRunning)
                return;

            IsFullscreen = !IsFullscreen;
        }

        [RelayCommand]
        private void ToggleRetroArchErrorOverlay()
        {
            if (!HasRetroArchError)
                return;

            IsRetroArchErrorOverlayOpen = !IsRetroArchErrorOverlayOpen;
        }

        private void ClearRetroArchErrorState()
        {
            RetroArchErrorSummary = null;
            RetroArchErrorDetails = null;
            IsRetroArchErrorOverlayOpen = false;
        }

        [RelayCommand]
        private void CloseEmulator()
        {
            SLog.Info("EmulationViewModel.CloseEmulator requested by the user.");
            _pendingEmulatorLaunchRequest = null;
            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();

            if (TryGetRunningTrackedEmulatorProcess(out var process))
            {
                RequestStopEmulatorCapture = true;
                CloseTrackedEmulatorForPendingLaunch(process);
                return;
            }

            RequestStopEmulatorCapture = true;
            EmulatorTargetHwnd = IntPtr.Zero;
            IsEmulatorRunning = false;
            UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum ?? SelectedAlbum);
            DetachTrackedEmulatorProcess();
        }

        public void ShutdownForApplicationExit()
        {
            SLog.Info("EmulationViewModel.ShutdownForApplicationExit started.");
            _pendingEmulatorLaunchRequest = null;
            IsRenderOptionsOpen = false;
            ClearRetroArchErrorState();
            RequestStopEmulatorCapture = true;

            if (EmulatorTargetHwnd != IntPtr.Zero)
            {
                SLog.Info($"EmulationViewModel clearing emulator hwnd 0x{EmulatorTargetHwnd.ToInt64():X} for application shutdown.");
                EmulatorTargetHwnd = IntPtr.Zero;
            }

            if (!TryGetRunningTrackedEmulatorProcess(out var process))
            {
                IsEmulatorRunning = false;
                CurrentEmulatorHandler = null;
                ClearSessionCaptureStretchOverride();
                DetachTrackedEmulatorProcess();
                return;
            }

            try
            {
                if (string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                {
                    TryRequestRpcs3Shutdown(process);
                    return;
                }

                var forceKillFirst = string.Equals(CurrentEmulatorHandler?.HandlerId, "pcsx2", StringComparison.OrdinalIgnoreCase);
                forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "dolphin", StringComparison.OrdinalIgnoreCase);

                if (!forceKillFirst)
                {
                    try
                    {
                        forceKillFirst = process.ProcessName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) ||
                                         process.ProcessName.Contains("dolphin", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                    }
                }

                if (forceKillFirst)
                {
                    SLog.Info($"EmulationViewModel force-terminating emulator pid={process.Id} during application shutdown.");
                    process.Kill(true);
                }
                else
                {
                    var closeMainWindowResult = process.CloseMainWindow();
                    SLog.Info($"EmulationViewModel CloseMainWindow returned {closeMainWindowResult} for pid={process.Id} during application shutdown.");
                    if (!closeMainWindowResult)
                    {
                        process.Kill(true);
                    }
                    else if (!process.WaitForExit(3000))
                    {
                        SLog.Info($"EmulationViewModel force-closing emulator pid={process.Id} after graceful shutdown timed out during application shutdown.");
                        process.Kill(true);
                    }
                }

                if (!process.HasExited)
                {
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to stop tracked emulator cleanly during application shutdown.", ex);

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception killEx)
                {
                    SLog.Debug("Failed to force-close emulator during application shutdown.", killEx);
                }
            }
            finally
            {
                IsEmulatorRunning = false;
                CurrentEmulatorHandler = null;
                ClearSessionCaptureStretchOverride();
                DetachTrackedEmulatorProcess();
                SLog.Info("EmulationViewModel.ShutdownForApplicationExit finished.");
            }
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
                    {
                        _ = ApplyXbox360TitlesFromDatabaseAsync(album);
                        QueueSelectedAlbumCoverScan(album);
                    }
                }

                SelectedAlbum = AlbumList.FirstOrDefault();
                LoadedAlbum = null;
                UpdateCurrentEmulatorHandlerForSelection(SelectedAlbum);
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
            _ = ApplyXbox360TitlesFromDatabaseAsync(album);

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
            var title = Ps3InstalledGameHelper.GetTitleName(filePath);
            if (string.IsNullOrWhiteSpace(title))
                title = GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(filePath));

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

            var lookup = await LoadXbox360TitleLookupAsync(cancellationToken).ConfigureAwait(false);
            if (lookup.Count == 0)
                return;

            foreach (var item in album.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item == null || string.IsNullOrWhiteSpace(item.FileName))
                    continue;

                if (_xbox360MetadataSeededItems.Contains(item.FileName) &&
                    TryReadCachedXbox360Ids(item.FileName, out var seededTitleId, out var seededMediaId) &&
                    !string.IsNullOrWhiteSpace(seededTitleId) &&
                    !string.IsNullOrWhiteSpace(seededMediaId))
                {
                    continue;
                }

                var cachedTitle = TryReadCachedMetadataTitle(item.FileName);
                if (!string.IsNullOrWhiteSpace(cachedTitle))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!string.Equals(item.Title, cachedTitle, StringComparison.Ordinal))
                            item.Title = cachedTitle;
                    }, DispatcherPriority.Background);

                    if (!TryReadCachedXbox360Ids(item.FileName, out var cachedTitleId, out var cachedMediaId) ||
                        string.IsNullOrWhiteSpace(cachedTitleId) ||
                        string.IsNullOrWhiteSpace(cachedMediaId))
                    {
                        var detectedMetadata = await Task.Run(() => metadataService.TryReadGameMetadata(item.FileName), cancellationToken).ConfigureAwait(false);
                        await PersistXbox360LocalMetadataAsync(item, cachedTitle, detectedMetadata?.TitleId, detectedMetadata?.MediaId, cancellationToken).ConfigureAwait(false);
                    }

                    _xbox360MetadataSeededItems.Add(item.FileName);
                    continue;
                }

                var metadata = await Task.Run(() => metadataService.TryReadGameMetadata(item.FileName), cancellationToken).ConfigureAwait(false);
                var titleId = metadata?.TitleId;
                var mediaId = metadata?.MediaId;
                if (string.IsNullOrWhiteSpace(titleId) || !lookup.TryGetValue(titleId, out var dbTitle) || string.IsNullOrWhiteSpace(dbTitle))
                {
                    if (!string.IsNullOrWhiteSpace(titleId) || !string.IsNullOrWhiteSpace(mediaId))
                        await PersistXbox360LocalMetadataAsync(item, item.Title ?? string.Empty, titleId, mediaId, cancellationToken).ConfigureAwait(false);

                    _xbox360MetadataSeededItems.Add(item.FileName);
                    continue;
                }

                var normalizedCurrentTitle = GetNormalizedRomTitle(item.Title);
                var normalizedDbTitle = GetNormalizedRomTitle(dbTitle);
                var shouldUpdateTitle = string.IsNullOrWhiteSpace(normalizedCurrentTitle) ||
                                        normalizedCurrentTitle.Equals(GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(item.FileName)), StringComparison.OrdinalIgnoreCase) ||
                                        !string.Equals(normalizedCurrentTitle, normalizedDbTitle, StringComparison.OrdinalIgnoreCase);

                if (!shouldUpdateTitle)
                {
                    await PersistXbox360LocalMetadataAsync(item, item.Title ?? dbTitle, titleId, mediaId, cancellationToken).ConfigureAwait(false);
                    _xbox360MetadataSeededItems.Add(item.FileName);
                    continue;
                }

                await Dispatcher.UIThread.InvokeAsync(() => item.Title = dbTitle, DispatcherPriority.Background);
                await PersistXbox360LocalMetadataAsync(item, dbTitle, titleId, mediaId, cancellationToken).ConfigureAwait(false);
                _xbox360MetadataSeededItems.Add(item.FileName);
            }
        }

        private async Task<Dictionary<string, string>> LoadXbox360TitleLookupAsync(CancellationToken cancellationToken)
        {
            if (_xbox360TitleLookup != null)
                return _xbox360TitleLookup;

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var dbPath = Path.Combine(AppContext.BaseDirectory, "Database", "x360.json");
            if (!File.Exists(dbPath))
            {
                var projectDbPath = Path.Combine(Directory.GetCurrentDirectory(), "AES_Lacrima", "Database", "x360.json");
                if (File.Exists(projectDbPath))
                    dbPath = projectDbPath;
            }

            if (!File.Exists(dbPath))
            {
                _xbox360TitleLookup = lookup;
                return lookup;
            }

            try
            {
                await using var stream = File.OpenRead(dbPath);
                var entries = await JsonSerializer.DeserializeAsync<List<Xbox360TitleEntry>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                              ?? [];

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry?.TitleId) || string.IsNullOrWhiteSpace(entry.Title))
                        continue;

                    var id = entry.TitleId.Trim().ToUpperInvariant();
                    if (id.Length != 8 || !Regex.IsMatch(id, "^[0-9A-F]{8}$"))
                        continue;

                    if (!lookup.ContainsKey(id))
                        lookup[id] = entry.Title.Trim();
                }
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to load Xbox 360 title database (Database/x360.json).", ex);
            }

            _xbox360TitleLookup = lookup;
            return lookup;
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

        private static string GetLocalMetadataCachePath(string filePath)
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
                var ps3Title = Ps3InstalledGameHelper.GetTitleName(item.FileName);
                var normalized = GetNormalizedRomTitle(item.Title);
                if (string.IsNullOrWhiteSpace(normalized) && !string.IsNullOrWhiteSpace(item.FileName))
                    normalized = GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(item.FileName));

                if (!string.IsNullOrWhiteSpace(ps3Title) &&
                    (string.IsNullOrWhiteSpace(item.Title) ||
                     string.Equals(item.Title, normalized, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Title, Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase)))
                {
                    item.Title = ps3Title;
                    continue;
                }

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
            IsEmulatorLaunchInProgress = true;

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
            _ = LaunchEmulatorAsync(request);
        }

        private async Task LaunchEmulatorAsync(PendingEmulatorLaunchRequest request)
        {
            try
            {
                ClearRetroArchErrorState();

                var handler = request.Handler;
                CurrentEmulatorHandler = handler;
                SetSessionCaptureStretchOverride(string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(handler.HandlerId, "cemu", StringComparison.OrdinalIgnoreCase)
                    ? Stretch.UniformToFill
                    : null);
                
                SelectedCaptureMode = handler.PreferredCaptureMode;
                EmulatorCaptureDelayMs = handler.IsWindowEmbeddingSupported
                    ? 0
                    : handler.CaptureStartupDelayMs;

                SLog.Info($"Selected capture mode for '{handler.HandlerId}' is {SelectedCaptureMode}.");

                if (!handler.IsPrepared)
                    handler.Prepare();

                if (handler is CemuHandler cemuHandler)
                    cemuHandler.ApplyFullscreenScalingWorkaround(handler.LauncherPath ?? string.Empty);
                var rpcs3TitleId = string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase)
                    ? Ps3InstalledGameHelper.GetTitleId(request.RomPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(rpcs3TitleId))
                    SLog.Info($"EmulationViewModel resolved RPCS3 title id '{rpcs3TitleId}' for '{request.RomPath}'.");

                EnsureAppTopMostBeforeLaunch();

                var launchRomPath = request.RomPath;
                if (string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(rpcs3TitleId))
                {
                    launchRomPath = Rpcs3Handler.BuildGameIdBootPath(rpcs3TitleId);
                    SLog.Info($"EmulationViewModel booting RPCS3 by GAMEID using '{launchRomPath}'.");
                }

                var startInfo = handler.BuildStartInfo(
                    handler.LauncherPath ?? string.Empty,
                    launchRomPath,
                    request.LaunchSettings?.StartFullscreen == true,
                    request.AlbumTitle,
                    request.LaunchSettings?.SelectedRetroArchCore);

                PrepareLinuxAppImageStartInfo(startInfo);
                var process = Process.Start(startInfo);

                if (process != null)
                {
                    SLog.Info($"Emulator process launched: pid={process.Id}, name={process.ProcessName}, hasExited={process.HasExited}.");
                }

                RestoreHostWindowFocus();

                Process? runtimeProcess = process;
                if (process != null)
                {
                    try
                    {
                        runtimeProcess = await handler.ResolveRuntimeProcessAsync(process, CancellationToken.None).ConfigureAwait(false) ?? process;
                    }
                    catch (OperationCanceledException)
                    {
                        runtimeProcess = process;
                    }
                    catch (Exception ex)
                    {
                        SLog.Warn($"Failed to resolve emulator runtime process for '{request.AlbumTitle}' item '{request.ItemTitle}'.", ex);
                        runtimeProcess = process;
                    }

                    if (handler.HideUntilCaptured && runtimeProcess != null)
                    {
                        try
                        {
                            runtimeProcess.Refresh();
                            if (!runtimeProcess.HasExited)
                                handler.PrepareProcessForCapture(runtimeProcess);
                        }
                        catch
                        {
                        }
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (process != null && runtimeProcess != null && !ReferenceEquals(process, runtimeProcess))
                    {
                        try
                        {
                            SLog.Info($"Emulator runtime process resolved: launcherPid={process.Id}, runtimePid={runtimeProcess.Id}, runtimeName={runtimeProcess.ProcessName}.");
                        }
                        catch
                        {
                            SLog.Info("Emulator runtime process resolved to a spawned process.");
                        }
                    }

                    TrackEmulatorProcess(runtimeProcess, request.RomPath, handler);
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch emulator for '{request.AlbumTitle}' item '{request.ItemTitle}'.", ex);
                if (request.Handler is CemuHandler cemuHandler)
                    cemuHandler.RestoreFullscreenScalingWorkaround(request.Handler.LauncherPath ?? string.Empty);
                ClearSessionCaptureStretchOverride();
                RestoreAppTopMost();
                RestoreHostWindowFocus();
                IsEmulatorLaunchInProgress = false;
            }
        }

        private void SetSessionCaptureStretchOverride(Stretch? stretch)
        {
            if (_sessionCaptureStretchOverride == stretch)
                return;

            _sessionCaptureStretchOverride = stretch;
            OnPropertyChanged(nameof(CurrentCaptureStretch));
        }

        private void ClearSessionCaptureStretchOverride()
        {
            SetSessionCaptureStretchOverride(null);
        }

        private static void PrepareLinuxAppImageStartInfo(ProcessStartInfo startInfo)
        {
            if (!OperatingSystem.IsLinux())
                return;

            if (string.IsNullOrWhiteSpace(startInfo.FileName) ||
                !startInfo.FileName.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var appImagePath = startInfo.FileName;
            var originalArgs = startInfo.ArgumentList.ToArray();

            startInfo.FileName = "env";
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add("APPIMAGE_EXTRACT_AND_RUN=1");
            startInfo.ArgumentList.Add(appImagePath);
            startInfo.ArgumentList.Add("--appimage-extract-and-run");

            foreach (var arg in originalArgs)
                startInfo.ArgumentList.Add(arg);
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

        private bool TryRequestRpcs3Shutdown(Process process)
        {
            if (!string.Equals(CurrentEmulatorHandler?.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase))
                return false;

            var mainWindowHandle = ResolveProcessMainWindowHandle(process);
            if (mainWindowHandle == IntPtr.Zero)
            {
                SLog.Info($"EmulationViewModel could not resolve the RPCS3 main window handle for pid={process.Id}.");
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Debug($"EmulationViewModel failed to force-close RPCS3 pid={process.Id} after it could not resolve the main window.", ex);
                }

                return true;
            }

            var sent = Win32API.TrySendControlS(mainWindowHandle);
            if (sent)
            {
                SLog.Info($"EmulationViewModel sent the RPCS3 stop shortcut to pid={process.Id}.");

                try
                {
                    SLog.Info($"EmulationViewModel waiting up to 5000 ms for RPCS3 pid={process.Id} to exit after sending the stop shortcut.");
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Timed wait for RPCS3 shutdown failed; continuing with final state checks.", ex);
                }

                try
                {
                    if (!process.HasExited)
                    {
                        SLog.Info($"EmulationViewModel is force-closing RPCS3 pid={process.Id} after the stop shortcut timed out.");
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    SLog.Debug("Final forced RPCS3 shutdown hit a process race.", ex);
                }

                return true;
            }

            SLog.Info($"EmulationViewModel failed to send the RPCS3 stop shortcut to pid={process.Id}; forcing termination without closing the window.");

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                SLog.Debug($"EmulationViewModel failed to force-close RPCS3 pid={process.Id} after the stop shortcut could not be sent.", ex);
            }

            return true;
        }

        private static IntPtr ResolveProcessMainWindowHandle(Process process, int maxAttempts = 20, int delayMs = 100)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    process.Refresh();
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                        return hwnd;
                }
                catch
                {
                    // Ignore and keep polling for the main window.
                }

                Thread.Sleep(delayMs);
            }

            return IntPtr.Zero;
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
            _ = CloseTrackedEmulatorForPendingLaunchAsync(process);
        }

        private async Task CloseTrackedEmulatorForPendingLaunchAsync(Process process)
        {
            try
            {
                await WaitForCaptureStopBeforeClosingProcessAsync().ConfigureAwait(false);

                if (TryRequestRpcs3Shutdown(process))
                {
                    return;
                }

                await Task.Run(() =>
                {
                    var forceKillFirst = string.Equals(CurrentEmulatorHandler?.HandlerId, "pcsx2", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "dolphin", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);
                    if (!forceKillFirst)
                    {
                        try
                        {
                            forceKillFirst = process.ProcessName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) ||
                                             process.ProcessName.Contains("dolphin", StringComparison.OrdinalIgnoreCase);
                            forceKillFirst |= process.ProcessName.Contains("shadps4", StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            // ignore and keep current value
                        }
                    }

                    try
                    {
                        if (forceKillFirst)
                        {
                            SLog.Info($"EmulationViewModel using direct termination for PCSX2 pid={process.Id} to bypass confirm-shutdown dialogs.");
                            process.Kill(true);
                        }
                        else
                        {
                            var closeMainWindowResult = process.CloseMainWindow();
                            SLog.Info($"EmulationViewModel CloseMainWindow returned {closeMainWindowResult} for pid={process.Id}.");
                            if (!closeMainWindowResult)
                                process.Kill(true);
                        }
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

        private async Task WaitForCaptureStopBeforeClosingProcessAsync()
        {
            const int maxAttempts = 50;
            const int delayMs = 50;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (!RequestStopEmulatorCapture)
                    break;

                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (EmulatorTargetHwnd != IntPtr.Zero)
                {
                    SLog.Info($"EmulationViewModel clearing emulator hwnd 0x{EmulatorTargetHwnd.ToInt64():X} after capture stop request.");
                    EmulatorTargetHwnd = IntPtr.Zero;
                }
            }, DispatcherPriority.Background);

            await Task.Delay(150).ConfigureAwait(false);
        }

        private void TrackEmulatorProcess(Process? process, string romPath, IEmulatorHandler handler)
        {
            EmulatorTargetHwnd = IntPtr.Zero;

            if (process == null)
            {
                SLog.Warn($"Emulator launch for '{romPath}' did not expose a trackable process handle.");
                if (handler is CemuHandler cemuHandler)
                    cemuHandler.RestoreFullscreenScalingWorkaround(handler.LauncherPath ?? string.Empty);
                RestoreAppTopMost();
                RestoreHostWindowFocus();
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                IsEmulatorLaunchInProgress = false;
                StopGameplayPreview();
                return;
            }

            DetachTrackedEmulatorProcess();

            _retroArchLogWatcherCts?.Cancel();
            _retroArchLogWatcherCts?.Dispose();
            _retroArchLogWatcherCts = null;

            _activeEmulatorProcess = process;
            EmulatorTargetProcessId = process?.Id ?? 0;

            CancelAppTopmostRestoreTimeout();

            try
            {
                process!.EnableRaisingEvents = true;
                process!.Exited += ActiveEmulatorProcess_Exited;
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to subscribe to emulator exit events.", ex);
            }

            IsEmulatorRunning = !process!.HasExited;

            if (handler is RetroArchHandler retroArchHandler)
                StartRetroArchLogWatcher(process, retroArchHandler);

            if (process.HasExited)
                HandleTrackedEmulatorExited(process);
            else
                _ = ResolveEmulatorTargetHwndAsync(process, romPath, handler);

            RestoreHostWindowFocus();
        }

        private void ActiveEmulatorProcess_Exited(object? sender, EventArgs e)
        {
            if (sender is not Process process)
                return;

            Dispatcher.UIThread.Post(() => HandleTrackedEmulatorExited(process), DispatcherPriority.Background);
        }

        private void HandleTrackedEmulatorExited(Process process)
        {
            var currentHandler = CurrentEmulatorHandler;

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
            RequestStopEmulatorCapture = false;
            ClearSessionCaptureStretchOverride();
            if (currentHandler is CemuHandler cemuHandler)
                cemuHandler.RestoreFullscreenScalingWorkaround(currentHandler.LauncherPath ?? string.Empty);
            RestoreAppTopMost();

            if (CurrentEmulatorHandler is RetroArchHandler retroArchHandler)
            {
                TryShowRetroArchErrorPrompt(process, retroArchHandler);
            }

            var hadPendingLaunch = _pendingEmulatorLaunchRequest != null;
            TryLaunchPendingEmulatorRequest();

            if (!hadPendingLaunch)
                IsEmulatorLaunchInProgress = false;
        }

        private void DetachTrackedEmulatorProcess()
        {
            if (_activeEmulatorProcess == null)
            {
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                _retroArchLogWatcherCts?.Cancel();
                _retroArchLogWatcherCts?.Dispose();
                _retroArchLogWatcherCts = null;
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
                EmulatorTargetProcessId = 0;
            }
        }

        private async Task ResolveEmulatorTargetHwndAsync(Process process, string romPath, IEmulatorHandler handler)
        {
            try
            {
                var hwnd = await ResolveCaptureTargetForCurrentPlatformAsync(process, handler).ConfigureAwait(false);
                if (hwnd == IntPtr.Zero)
                {
                    var maxRetries = handler is RetroArchHandler ? 4 : 1;
                    for (int i = 0; i < maxRetries && hwnd == IntPtr.Zero; i++)
                    {
                        SLog.Warn($"Failed to resolve emulator capture target for '{romPath}' (attempt {i + 1}). Retrying...");
                        await Task.Delay(2000).ConfigureAwait(false);
                        hwnd = await ResolveCaptureTargetForCurrentPlatformAsync(process, handler).ConfigureAwait(false);
                    }
                }

                if (hwnd == IntPtr.Zero)
                {
                    SLog.Warn($"Failed to resolve emulator capture target for '{romPath}' after retry.");
                    RestoreAppTopMost();
                    await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);
                    return;
                }

                UseHostWindowCapture = false;
                await TryApplyEmulatorTargetHwndAsync(process, hwnd, showWindowForCapture: handler.HideUntilCaptured).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SLog.Debug($"Emulator capture target resolution canceled for '{romPath}'.");
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to resolve emulator capture target for '{romPath}'.", ex);
                RestoreAppTopMost();
                await Dispatcher.UIThread.InvokeAsync(() => IsEmulatorLaunchInProgress = false);
            }
        }

        private static async Task<IntPtr> ResolveCaptureTargetForCurrentPlatformAsync(Process process, IEmulatorHandler handler)
        {
            if (OperatingSystem.IsWindows())
                return await handler.ResolveCaptureTargetAsync(process, CancellationToken.None).ConfigureAwait(false);

            if (handler.CaptureStartupDelayMs > 0)
                await Task.Delay(handler.CaptureStartupDelayMs).ConfigureAwait(false);

            var captureProcess = ResolveCaptureProcessForCurrentPlatform(process, handler);
            try
            {
                captureProcess.Refresh();
                if (captureProcess.HasExited)
                    return IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }

            return new IntPtr(captureProcess.Id);
        }

        private static Process ResolveCaptureProcessForCurrentPlatform(Process process, IEmulatorHandler handler)
        {
            if (TryGetLiveProcess(process, out var liveProcess))
                return liveProcess;

            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var startInfoName = Path.GetFileNameWithoutExtension(process.StartInfo?.FileName ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(startInfoName))
                    candidateNames.Add(startInfoName);
            }
            catch
            {
            }

            try
            {
                var executablePath = EmulatorHandlerBase.ResolveLauncherExecutablePath(handler.LauncherPath);
                var executableName = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(executableName))
                    candidateNames.Add(executableName);
            }
            catch
            {
            }

            var titleHint = handler.LauncherPath is { Length: > 0 }
                ? Path.GetFileNameWithoutExtension(handler.LauncherPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : handler.DisplayName;
            if (!string.IsNullOrWhiteSpace(titleHint))
                candidateNames.Add(titleHint);

            Process? bestCandidate = null;
            DateTime bestStartTime = DateTime.MinValue;

            foreach (var candidateName in candidateNames)
            {
                Process[] candidates;
                try
                {
                    candidates = Process.GetProcessesByName(candidateName);
                }
                catch
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (!TryGetLiveProcess(candidate, out var liveCandidate))
                        continue;

                    try
                    {
                        if (liveCandidate.StartTime > bestStartTime)
                        {
                            bestStartTime = liveCandidate.StartTime;
                            bestCandidate = liveCandidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return bestCandidate ?? process;
        }

        private static bool TryGetLiveProcess(Process process, out Process liveProcess)
        {
            liveProcess = process;

            try
            {
                process.Refresh();
                if (!process.HasExited)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private void TryShowRetroArchErrorPrompt(Process process, RetroArchHandler handler)
        {
            if (!RetroArchHandler.TryGetRetroArchErrorDetails(handler.LauncherPath, out var summary, out var details))
                return;

            if (string.IsNullOrWhiteSpace(details))
                return;

            RetroArchErrorSummary = string.IsNullOrWhiteSpace(summary)
                ? "RetroArch reported an error during launch."
                : summary;
            RetroArchErrorDetails = details;

            SLog.Warn($"RetroArch launch issue detected: {RetroArchErrorSummary}");
        }

        private void StartRetroArchLogWatcher(Process process, RetroArchHandler handler)
        {
            if (process.HasExited)
                return;

            _retroArchLogWatcherCts?.Cancel();
            _retroArchLogWatcherCts?.Dispose();
            _retroArchLogWatcherCts = new CancellationTokenSource();
            var token = _retroArchLogWatcherCts.Token;

            _ = Task.Run(async () =>
            {
                var logFilePath = RetroArchHandler.GetRetroArchLogFilePath(handler.LauncherPath);
                if (string.IsNullOrWhiteSpace(logFilePath))
                    return;

                var lastLineCount = 0;
                var startTime = DateTime.UtcNow;
                while (!token.IsCancellationRequested && !process.HasExited && DateTime.UtcNow - startTime < TimeSpan.FromSeconds(12))
                {
                    try
                    {
                        if (!File.Exists(logFilePath))
                        {
                            await Task.Delay(250, token).ConfigureAwait(false);
                            continue;
                        }

                        var lines = File.ReadAllLines(logFilePath);
                        if (lines.Length <= lastLineCount)
                        {
                            await Task.Delay(250, token).ConfigureAwait(false);
                            continue;
                        }

                        var newLines = lines.Skip(lastLineCount).ToArray();
                        lastLineCount = lines.Length;

                        if (RetroArchHandler.TryExtractRetroArchErrorDetails(newLines, out var summary, out var details))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                RetroArchErrorSummary = string.IsNullOrWhiteSpace(summary)
                                    ? "RetroArch reported an error during launch."
                                    : summary;
                                RetroArchErrorDetails = details;
                            }, DispatcherPriority.Background);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }, token);
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

                RestoreAppTopMost();
                RestoreHostWindowFocus();

                if (EmulatorTargetHwnd != hwnd)
                    EmulatorTargetHwnd = hwnd;

                IsEmulatorLaunchInProgress = false;

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


        private void EnsureAppTopMostBeforeLaunch()
        {
            if (_appTopmostOverride)
                return;

            var hwnd = GetHostWindowHandle();
            if (hwnd == IntPtr.Zero)
                return;

            _appWasTopmostBeforeEmulatorLaunch = Win32API.IsWindowTopMost(hwnd);
            _appWindowHandleBeforeEmulatorLaunch = hwnd;

            if (!_appWasTopmostBeforeEmulatorLaunch)
            {
                Win32API.SetWindowTopMost(hwnd);
                _appTopmostOverride = true;
                StartAppTopmostRestoreTimeout();
            }
        }

        private void StartAppTopmostRestoreTimeout()
        {
            _appTopmostRestoreCts?.Cancel();
            _appTopmostRestoreCts?.Dispose();
            _appTopmostRestoreCts = new CancellationTokenSource();
            var token = _appTopmostRestoreCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AppTopmostRestoreTimeout, token).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_appTopmostOverride)
                        {
                            SLog.Info("Restoring app topmost because emulator launch did not complete within timeout.");
                            RestoreAppTopMost();
                        }
                    }, DispatcherPriority.Background);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation expected when restore happens normally.
                }
                catch (Exception ex)
                {
                    SLog.Warn("App topmost restore timeout task failed.", ex);
                }
            }, token);
        }

        private void CancelAppTopmostRestoreTimeout()
        {
            _appTopmostRestoreCts?.Cancel();
            _appTopmostRestoreCts?.Dispose();
            _appTopmostRestoreCts = null;
        }

        private void RestoreAppTopMost()
        {
            if (!_appTopmostOverride)
                return;

            if (_appWindowHandleBeforeEmulatorLaunch == IntPtr.Zero)
                return;

            CancelAppTopmostRestoreTimeout();

            Win32API.SetWindowNotTopMost(_appWindowHandleBeforeEmulatorLaunch);
            _appTopmostOverride = false;
            _appWindowHandleBeforeEmulatorLaunch = IntPtr.Zero;
        }

        private static void RestoreHostWindowFocus()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow is { } mainWindow)
                {
                    mainWindow.Activate();
                }
            }, DispatcherPriority.Background);
        }

        private static IntPtr GetHostWindowHandle()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return IntPtr.Zero;

            return desktop.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
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
