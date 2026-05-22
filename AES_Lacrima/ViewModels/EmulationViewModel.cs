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
using AES_Lacrima.Services.Emulation;
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

    public partial class ShadPs4PatchEntry : ObservableObject
    {
        [ObservableProperty]
        private bool _isEnabled;

        public string Name { get; }
        public string Note { get; }
        public string AppVer { get; }

        public ShadPs4PatchEntry(bool isEnabled, string name, string note, string appVer)
        {
            _isEnabled = isEnabled;
            Name = name;
            Note = note;
            AppVer = appVer;
        }
    }

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
        private readonly HashSet<FolderMediaItem> _albumsWithMetadataScanned = [];
        private AvaloniaList<string> _pendingAlbumOrder = [];
        private Dictionary<string, List<MediaItem>> _pendingAlbumRoms = new(StringComparer.OrdinalIgnoreCase);
        private bool _isPreparing;
        private bool _isSyncingAlbumSelection;
        private CancellationTokenSource? _albumCoverScanCts;
        private CancellationTokenSource? _gameplayPreviewCts;
        private bool _isGameplayPreviewActive;
        private bool _suppressSelectionStopForGameplayPreview;
        private bool _isSyncingCurrentSectionCoreSelection;
        private bool _isSyncingCurrentSectionRetroArchVersionSelection;
        private bool _isSyncingCurrentSectionRetroArchRepositoryOverride;
        private bool _isCurrentSectionRetroArchRepositoryDirty;
        private bool _isSyncingCurrentSectionRetroArchIncludeCores;
        private bool _isSyncingCurrentSectionEdenVersionSelection;
        private bool _isSyncingCurrentSectionEdenRepositoryOverride;
        private bool _isCurrentSectionEdenRepositoryDirty;
        private bool _isSyncingCurrentSectionEdenIncludePrereleases;
        private bool _isSyncingCurrentSectionShadPs4VersionSelection;
        private bool _isSyncingCurrentSectionShadPs4RepositoryOverride;
        private bool _isCurrentSectionShadPs4RepositoryDirty;
        private bool _isSyncingCurrentSectionShadPs4IncludePrereleases;
        private bool _isSyncingCurrentSectionDolphinVersionSelection;
        private bool _isSyncingCurrentSectionDolphinIncludePrereleases;
        private bool _isSyncingCurrentSectionRpcs3VersionSelection;
        private bool _isSyncingCurrentSectionRpcs3IncludePrereleases;
        private bool _isSyncingCurrentSectionPcsx2VersionSelection;
        private bool _isSyncingCurrentSectionPcsx2IncludePrereleases;
        private bool _isSyncingCurrentSectionDuckStationVersionSelection;
        private bool _isSyncingCurrentSectionDuckStationIncludePrereleases;
        private bool _isSyncingCurrentSectionXeniaVersionSelection;
        private bool _isXeniaPatchesOverlayOpen;
        private bool _isXeniaPatchesBusy;
        private bool _isCurrentSectionXeniaPatchDirty;
        private bool _isXeniaPatchSwitchPromptVisible;
        private bool _isSwitchingCurrentSectionXeniaPatchFile;
        private string _xeniaPatchesStatus = "Select an Xbox 360 game to manage patches.";
        private string? _xeniaDetectedTitleId;
        private string? _xeniaDetectedMediaId;
        private string? _xeniaPatchOverlayGameTitle;
        private XeniaPatchFileItem? _selectedCurrentSectionXeniaPatchFileItem;
        private string? _selectedCurrentSectionXeniaPatchFile;
        private string? _pendingCurrentSectionXeniaPatchFile;
        private string? _activeXeniaPatchDocumentPath;
        private string? _activeXeniaPatchDocumentText;
        private readonly AvaloniaList<XeniaPatchFileItem> _currentSectionXeniaPatchFiles = [];
        private readonly AvaloniaList<XeniaPatchEntry> _currentSectionXeniaPatchEntries = [];
#pragma warning disable CS0649
private bool _isShadPs4PatchesOverlayOpen;
        private bool _isShadPs4PatchesBusy;
        private bool _isCurrentSectionShadPs4PatchDirty;
        private bool _isSwitchingCurrentSectionShadPs4PatchFile;
        private bool _isShadPs4PatchSwitchPromptVisible;
        private ShadPs4PatchFileItem? _selectedCurrentSectionShadPs4PatchFileItem;
        private string? _pendingCurrentSectionShadPs4PatchFile;
#pragma warning restore CS0649
        private string? _activeShadPs4PatchDocumentPath;
        private string? _activeShadPs4PatchDocumentText;
        private string? _selectedCurrentSectionShadPs4PatchFile;
        private string _shadPs4PatchesStatus = "Select a PlayStation 4 game to manage patches.";
        private string? _shadPs4PatchGameTitle;

        public string? ShadPs4PatchGameTitle
        {
            get => _shadPs4PatchGameTitle;
            set
            {
                if (SetProperty(ref _shadPs4PatchGameTitle, value))
                    OnPropertyChanged(nameof(ShadPs4PatchOverlayHeader));
            }
        }

        public string ShadPs4PatchOverlayHeader =>
            string.IsNullOrWhiteSpace(ShadPs4PatchGameTitle) ? "ShadPS4 Patches" : $"{ShadPs4PatchGameTitle} Patches";

        public IReadOnlyList<ShadPs4ContentRepository> ShadPs4PatchRepositories => ShadPs4ContentRepository.All;

        private ShadPs4ContentRepository _selectedShadPs4PatchRepository = ShadPs4ContentRepository.ShadPs4;

        public ShadPs4ContentRepository SelectedShadPs4PatchRepository
        {
            get => _selectedShadPs4PatchRepository;
            set
            {
                if (_selectedShadPs4PatchRepository == value)
                    return;

                _selectedShadPs4PatchRepository = value;
                OnPropertyChanged();
            }
        }

        private readonly AvaloniaList<ShadPs4PatchFileItem> _currentSectionShadPs4PatchFiles = [];
        private readonly AvaloniaList<ShadPs4PatchEntry> _currentSectionShadPs4PatchEntries = [];
        private double _lastSelectedIndexForPreview = double.NaN;
        private string? _pendingGameplayPreviewItemPath;
        private string? _activeGameplayPreviewItemPath;
        private long _gameplayPreviewRequestVersion;
        private Process? _activeEmulatorProcess;
        private string? _activeEmulatorRomPath;
        private string? _activeEmulatorGameTitle;
        private ShadPs4IpcSession? _shadPs4IpcSession;
        private CancellationTokenSource? _retroArchLogWatcherCts;
        private CancellationTokenSource? _activeEmulatorWatchdogCts;
        private CancellationTokenSource? _appTopmostRestoreCts;
        private string? _currentSetupLaunchIconExecutablePath;
        private Bitmap? _currentSetupLaunchIcon;
        private PendingEmulatorLaunchRequest? _pendingEmulatorLaunchRequest;
        private string? _activeRpcs3SessionTitleId;
        private string? _activeRpcs3SessionEmulatorDirectory;
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
        private RetroArchEmulatorUpdateService? _retroArchEmulatorUpdateService;

        [AutoResolve]
        private ShadPs4EmulatorUpdateService? _shadPs4EmulatorUpdateService;

        [AutoResolve]
        private XeniaEmulatorUpdateService? _xeniaEmulatorUpdateService;

        [AutoResolve]
        private DolphinEmulatorUpdateService? _dolphinEmulatorUpdateService;

        [AutoResolve]
        private FlycastEmulatorUpdateService? _flycastEmulatorUpdateService;

        [AutoResolve]
        private Rpcs3EmulatorUpdateService? _rpcs3EmulatorUpdateService;

        [AutoResolve]
        private Pcsx2EmulatorUpdateService? _pcsx2EmulatorUpdateService;

        [AutoResolve]
        private DuckStationEmulatorUpdateService? _duckStationEmulatorUpdateService;

        [AutoResolve]
        private CemuEmulatorUpdateService? _cemuEmulatorUpdateService;

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
        [NotifyPropertyChangedFor(nameof(CanShowRenderOptions))]
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
            LoadedAlbum?.Children.Count > 0 &&
            (SelectedCaptureMode == EmulatorCaptureMode.DirectComposition ||
             CurrentEmulatorHandler?.IsWindowEmbeddingSupported != true);

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
        private bool _isEmulatorUpdateNoticeOverlayOpen;

        [ObservableProperty]
        private string? _emulatorUpdateNoticeSummary;

        [ObservableProperty]
        private string? _emulatorUpdateNoticeDetails;

        [ObservableProperty]
        private string? _emulatorUpdateNoticeFooter;

        [ObservableProperty]
        private string? _emulatorUpdateNoticeChanges;

        private string? _sectionLatestReleaseNotes;
        private string? _emulatorUpdateNoticeSuppressedAlbumTitle;

        public bool HasEmulatorUpdateNoticeChanges => !string.IsNullOrWhiteSpace(EmulatorUpdateNoticeChanges);

        partial void OnEmulatorUpdateNoticeChangesChanged(string? value) => OnPropertyChanged(nameof(HasEmulatorUpdateNoticeChanges));

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
            OnPropertyChanged(nameof(EnableCapturePillarboxCrop));
            OnPropertyChanged(nameof(HideTargetWindowAfterCaptureStarts));
            OnPropertyChanged(nameof(ClientAreaCropLeftInset));
            OnPropertyChanged(nameof(ClientAreaCropTopInset));
            OnPropertyChanged(nameof(ClientAreaCropRightInset));
            OnPropertyChanged(nameof(ClientAreaCropBottomInset));
            OnPropertyChanged(nameof(CurrentEmulatorWindowTitleHint));
            OnPropertyChanged(nameof(CurrentCaptureStretch));
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
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

        private AvaloniaList<string> _currentSectionFlycastAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionFlycastAvailableVersions
        {
            get => _currentSectionFlycastAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionFlycastAvailableVersions, value))
                    return;

                _currentSectionFlycastAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionFlycastVersion;
        public string? SelectedCurrentSectionFlycastVersion
        {
            get => _selectedCurrentSectionFlycastVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionFlycastVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionFlycastVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionFlycastVersionChanged(value);
            }
        }

        private string? _currentSectionFlycastCurrentVersion;
        public string? CurrentSectionFlycastCurrentVersion
        {
            get => _currentSectionFlycastCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionFlycastCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionFlycastCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionFlycastLatestVersion;
        public string? CurrentSectionFlycastLatestVersion
        {
            get => _currentSectionFlycastLatestVersion;
            set
            {
                if (string.Equals(_currentSectionFlycastLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionFlycastLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionFlycastStatus = "Select a Flycast section to manage updates.";
        public string CurrentSectionFlycastStatus
        {
            get => _currentSectionFlycastStatus;
            set
            {
                if (string.Equals(_currentSectionFlycastStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionFlycastStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionFlycastNightlies;
        public bool IncludeCurrentSectionFlycastNightlies
        {
            get => _includeCurrentSectionFlycastNightlies;
            set
            {
                if (_includeCurrentSectionFlycastNightlies == value)
                    return;

                _includeCurrentSectionFlycastNightlies = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionFlycastNightlies)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeFlycastNightlies = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionFlycastInfo();
            }
        }

        private bool _isCurrentSectionFlycastUpdateAvailable;
        public bool IsCurrentSectionFlycastUpdateAvailable
        {
            get => _isCurrentSectionFlycastUpdateAvailable;
            set
            {
                if (_isCurrentSectionFlycastUpdateAvailable == value)
                    return;

                _isCurrentSectionFlycastUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionFlycastBusy;
        public bool IsCurrentSectionFlycastBusy
        {
            get => _isCurrentSectionFlycastBusy;
            set
            {
                if (_isCurrentSectionFlycastBusy == value)
                    return;

                _isCurrentSectionFlycastBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionFlycastDownloading;
        public bool IsCurrentSectionFlycastDownloading
        {
            get => _isCurrentSectionFlycastDownloading;
            set
            {
                if (_isCurrentSectionFlycastDownloading == value)
                    return;

                _isCurrentSectionFlycastDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionFlycastDownloadProgress;
        public double CurrentSectionFlycastDownloadProgress
        {
            get => _currentSectionFlycastDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionFlycastDownloadProgress - value) < 0.01)
                    return;

                _currentSectionFlycastDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionFlycastEmulatorPath;
        public string? CurrentSectionFlycastEmulatorPath
        {
            get => _currentSectionFlycastEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionFlycastEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionFlycastEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionFlycastUpdatePath;
        public string? CurrentSectionFlycastUpdatePath
        {
            get => _currentSectionFlycastUpdatePath;
            set
            {
                if (string.Equals(_currentSectionFlycastUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionFlycastUpdatePath = value;
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

        private bool _isSyncingCurrentSectionFlycastVersionSelection;
        private bool _isSyncingCurrentSectionFlycastNightlies;
        private bool _isSyncingCurrentSectionCemuVersionSelection;

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

        public bool EnableCapturePillarboxCrop => CurrentEmulatorHandler?.EnableCapturePillarboxCrop == true;
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

        public ShadPs4CustomConfigEditorViewModel ShadPs4CustomConfigEditor { get; } = new();

        public ShadPs4CheatsEditorViewModel ShadPs4CheatsEditor { get; } = new();

        public XeniaCustomConfigEditorViewModel XeniaCustomConfigEditor { get; } = new();

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
                DetachEmulationSectionSubscriptions(_subscribedSettingsViewModel);
                _subscribedSettingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
                _subscribedSettingsViewModel.EmulationUseFirstItemCoverChanged -= OnEmulationUseFirstItemCoverChanged;
                _subscribedSettingsViewModel.EmulationGameplayAutoplayChanged -= OnEmulationGameplayAutoplayChanged;
            }

            _subscribedSettingsViewModel = settings;
            AttachEmulationSectionSubscriptions(_subscribedSettingsViewModel);
            _subscribedSettingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
            _subscribedSettingsViewModel.EmulationUseFirstItemCoverChanged += OnEmulationUseFirstItemCoverChanged;
            _subscribedSettingsViewModel.EmulationGameplayAutoplayChanged += OnEmulationGameplayAutoplayChanged;
        }

        private void AttachEmulationSectionSubscriptions(SettingsViewModel settings)
        {
            settings.EmulationSections.CollectionChanged -= EmulationSections_CollectionChanged;
            settings.EmulationSections.CollectionChanged += EmulationSections_CollectionChanged;

            foreach (var section in settings.EmulationSections)
            {
                section.PropertyChanged -= EmulationSectionItem_PropertyChanged;
                section.PropertyChanged += EmulationSectionItem_PropertyChanged;
            }
        }

        private void DetachEmulationSectionSubscriptions(SettingsViewModel settings)
        {
            settings.EmulationSections.CollectionChanged -= EmulationSections_CollectionChanged;

            foreach (var section in settings.EmulationSections)
                section.PropertyChanged -= EmulationSectionItem_PropertyChanged;
        }

        private void EmulationSections_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<EmulationSectionItem>())
                    item.PropertyChanged -= EmulationSectionItem_PropertyChanged;
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<EmulationSectionItem>())
                {
                    item.PropertyChanged -= EmulationSectionItem_PropertyChanged;
                    item.PropertyChanged += EmulationSectionItem_PropertyChanged;
                }
            }

            RefreshCurrentSectionHandlerState();
        }

        private void EmulationSectionItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not EmulationSectionItem section)
                return;

            if (!string.Equals(e.PropertyName, nameof(EmulationSectionItem.SelectedHandlerId), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(EmulationSectionItem.LaunchSettings), StringComparison.Ordinal))
            {
                return;
            }

            var sectionTitle = LoadedAlbum?.Title;
            if (string.IsNullOrWhiteSpace(sectionTitle) ||
                !string.Equals(section.SectionTitle, sectionTitle, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RefreshCurrentSectionHandlerState();
        }

        private void RefreshCurrentSectionHandlerState()
        {
            OnPropertyChanged(nameof(CurrentEmulationSectionItem));
            UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum);
            RefreshCurrentSectionLaunchOptionsState();
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

            if (e.PropertyName == nameof(SettingsViewModel.EmulationSections))
            {
                EnsureSettingsViewModelSubscription();
                RefreshCurrentSectionHandlerState();
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
            OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
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
            UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum);

            if (IsActive && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        partial void OnIsEmulatorLaunchInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
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

            SyncSelectedAlbumIndexFromAlbum(value);
            RefreshCurrentSectionLaunchOptionsState();
            AutoSave();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            if (IsEmulatorRunning && !IsEmulatorViewportDismissed)
                IsEmulatorViewportDismissed = true;

            if (!IsEmulatorRunning)
                UpdateCurrentEmulatorHandlerForSelection(value);

            IsEmulatorUpdateNoticeOverlayOpen = false;

            if (value != null &&
                !string.Equals(_emulatorUpdateNoticeSuppressedAlbumTitle, value.Title, StringComparison.OrdinalIgnoreCase))
            {
                _emulatorUpdateNoticeSuppressedAlbumTitle = null;
            }

            ApplyFilter();
            QueueSelectedAlbumCoverScan(value);
            RefreshActiveAlbumState();
            RefreshCurrentSectionLaunchOptionsState();
        }

        public EmulationSectionItem? CurrentEmulationSectionItem
        {
            get
            {
                var sectionTitle = LoadedAlbum?.Title;
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

        private string? _currentSectionRetroArchRepositoryOverride;
        public string? CurrentSectionRetroArchRepositoryOverride
        {
            get => _currentSectionRetroArchRepositoryOverride;
            set
            {
                if (string.Equals(_currentSectionRetroArchRepositoryOverride, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchRepositoryOverride = value;
                OnPropertyChanged();

                if (!_isSyncingCurrentSectionRetroArchRepositoryOverride)
                    IsCurrentSectionRetroArchRepositoryDirty = true;
            }
        }

        public bool IsCurrentSectionRetroArchRepositoryDirty
        {
            get => _isCurrentSectionRetroArchRepositoryDirty;
            set
            {
                if (_isCurrentSectionRetroArchRepositoryDirty == value)
                    return;

                _isCurrentSectionRetroArchRepositoryDirty = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionRetroArchCores;
        public bool IncludeCurrentSectionRetroArchCores
        {
            get => _includeCurrentSectionRetroArchCores;
            set
            {
                if (_includeCurrentSectionRetroArchCores == value)
                    return;

                _includeCurrentSectionRetroArchCores = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionRetroArchIncludeCores)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeRetroArchCores = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionRetroArchInfo();
            }
        }

        private AvaloniaList<string> _currentSectionRetroArchAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionRetroArchAvailableVersions
        {
            get => _currentSectionRetroArchAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionRetroArchAvailableVersions, value))
                    return;

                _currentSectionRetroArchAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionRetroArchVersion;
        public string? SelectedCurrentSectionRetroArchVersion
        {
            get => _selectedCurrentSectionRetroArchVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionRetroArchVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionRetroArchVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionRetroArchVersionChanged(value);
            }
        }

        private string? _currentSectionRetroArchCurrentVersion;
        public string? CurrentSectionRetroArchCurrentVersion
        {
            get => _currentSectionRetroArchCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionRetroArchCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRetroArchLatestVersion;
        public string? CurrentSectionRetroArchLatestVersion
        {
            get => _currentSectionRetroArchLatestVersion;
            set
            {
                if (string.Equals(_currentSectionRetroArchLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionRetroArchStatus = "Select a RetroArch section to manage updates.";
        public string CurrentSectionRetroArchStatus
        {
            get => _currentSectionRetroArchStatus;
            set
            {
                if (string.Equals(_currentSectionRetroArchStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionRetroArchUpdateAvailable;
        public bool IsCurrentSectionRetroArchUpdateAvailable
        {
            get => _isCurrentSectionRetroArchUpdateAvailable;
            set
            {
                if (_isCurrentSectionRetroArchUpdateAvailable == value)
                    return;

                _isCurrentSectionRetroArchUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionRetroArchBusy;
        public bool IsCurrentSectionRetroArchBusy
        {
            get => _isCurrentSectionRetroArchBusy;
            set
            {
                if (_isCurrentSectionRetroArchBusy == value)
                    return;

                _isCurrentSectionRetroArchBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionRetroArchDownloading;
        public bool IsCurrentSectionRetroArchDownloading
        {
            get => _isCurrentSectionRetroArchDownloading;
            set
            {
                if (_isCurrentSectionRetroArchDownloading == value)
                    return;

                _isCurrentSectionRetroArchDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionRetroArchDownloadProgress;
        public double CurrentSectionRetroArchDownloadProgress
        {
            get => _currentSectionRetroArchDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionRetroArchDownloadProgress - value) < 0.01)
                    return;

                _currentSectionRetroArchDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRetroArchEmulatorPath;
        public string? CurrentSectionRetroArchEmulatorPath
        {
            get => _currentSectionRetroArchEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionRetroArchEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRetroArchUpdatePath;
        public string? CurrentSectionRetroArchUpdatePath
        {
            get => _currentSectionRetroArchUpdatePath;
            set
            {
                if (string.Equals(_currentSectionRetroArchUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionRetroArchUpdatePath = value;
                OnPropertyChanged();
            }
        }

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

        private AvaloniaList<string> _currentSectionCemuAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionCemuAvailableVersions
        {
            get => _currentSectionCemuAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionCemuAvailableVersions, value))
                    return;

                _currentSectionCemuAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionCemuVersion;
        public string? SelectedCurrentSectionCemuVersion
        {
            get => _selectedCurrentSectionCemuVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionCemuVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionCemuVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionCemuVersionChanged(value);
            }
        }

        private string? _currentSectionCemuCurrentVersion;
        public string? CurrentSectionCemuCurrentVersion
        {
            get => _currentSectionCemuCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionCemuCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionCemuCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionCemuLatestVersion;
        public string? CurrentSectionCemuLatestVersion
        {
            get => _currentSectionCemuLatestVersion;
            set
            {
                if (string.Equals(_currentSectionCemuLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionCemuLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionCemuStatus = "Select a Cemu section to manage updates.";
        public string CurrentSectionCemuStatus
        {
            get => _currentSectionCemuStatus;
            set
            {
                if (string.Equals(_currentSectionCemuStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionCemuStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionCemuUpdateAvailable;
        public bool IsCurrentSectionCemuUpdateAvailable
        {
            get => _isCurrentSectionCemuUpdateAvailable;
            set
            {
                if (_isCurrentSectionCemuUpdateAvailable == value)
                    return;

                _isCurrentSectionCemuUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionCemuBusy;
        public bool IsCurrentSectionCemuBusy
        {
            get => _isCurrentSectionCemuBusy;
            set
            {
                if (_isCurrentSectionCemuBusy == value)
                    return;

                _isCurrentSectionCemuBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionCemuDownloading;
        public bool IsCurrentSectionCemuDownloading
        {
            get => _isCurrentSectionCemuDownloading;
            set
            {
                if (_isCurrentSectionCemuDownloading == value)
                    return;

                _isCurrentSectionCemuDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionCemuDownloadProgress;
        public double CurrentSectionCemuDownloadProgress
        {
            get => _currentSectionCemuDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionCemuDownloadProgress - value) < 0.01)
                    return;

                _currentSectionCemuDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionCemuEmulatorPath;
        public string? CurrentSectionCemuEmulatorPath
        {
            get => _currentSectionCemuEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionCemuEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionCemuEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionCemuUpdatePath;
        public string? CurrentSectionCemuUpdatePath
        {
            get => _currentSectionCemuUpdatePath;
            set
            {
                if (string.Equals(_currentSectionCemuUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionCemuUpdatePath = value;
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

        private IAsyncRelayCommand? _applyCurrentSectionRetroArchRepositoryCommand;
        public IAsyncRelayCommand ApplyCurrentSectionRetroArchRepositoryCommand =>
            _applyCurrentSectionRetroArchRepositoryCommand ??= new AsyncRelayCommand(ApplyCurrentSectionRetroArchRepository);

        private IAsyncRelayCommand? _refreshCurrentSectionRetroArchInfoCommand;
        public IAsyncRelayCommand RefreshCurrentSectionRetroArchInfoCommand =>
            _refreshCurrentSectionRetroArchInfoCommand ??= new AsyncRelayCommand(RefreshCurrentSectionRetroArchInfo);

        private IAsyncRelayCommand? _downloadOrUpdateCurrentSectionRetroArchCommand;
        public IAsyncRelayCommand DownloadOrUpdateCurrentSectionRetroArchCommand =>
            _downloadOrUpdateCurrentSectionRetroArchCommand ??= new AsyncRelayCommand(DownloadOrUpdateCurrentSectionRetroArch);

        private IAsyncRelayCommand? _openCurrentSectionEdenUpdatesCommand;
        public IAsyncRelayCommand OpenCurrentSectionEdenUpdatesCommand =>
            _openCurrentSectionEdenUpdatesCommand ??= new AsyncRelayCommand(OpenCurrentSectionEdenUpdates);

        private IAsyncRelayCommand? _applyCurrentSectionShadPs4RepositoryCommand;
        public IAsyncRelayCommand ApplyCurrentSectionShadPs4RepositoryCommand =>
            _applyCurrentSectionShadPs4RepositoryCommand ??= new AsyncRelayCommand(ApplyCurrentSectionShadPs4Repository);

        private AvaloniaList<string> _currentSectionDolphinAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionDolphinAvailableVersions
        {
            get => _currentSectionDolphinAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionDolphinAvailableVersions, value))
                    return;

                _currentSectionDolphinAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionDolphinVersion;
        public string? SelectedCurrentSectionDolphinVersion
        {
            get => _selectedCurrentSectionDolphinVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionDolphinVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionDolphinVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionDolphinVersionChanged(value);
            }
        }

        private string? _currentSectionDolphinCurrentVersion;
        public string? CurrentSectionDolphinCurrentVersion
        {
            get => _currentSectionDolphinCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionDolphinCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionDolphinCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDolphinLatestVersion;
        public string? CurrentSectionDolphinLatestVersion
        {
            get => _currentSectionDolphinLatestVersion;
            set
            {
                if (string.Equals(_currentSectionDolphinLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionDolphinLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionDolphinStatus = "Select a Dolphin section to manage updates.";
        public string CurrentSectionDolphinStatus
        {
            get => _currentSectionDolphinStatus;
            set
            {
                if (string.Equals(_currentSectionDolphinStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionDolphinStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionDolphinPrereleases;
        public bool IncludeCurrentSectionDolphinPrereleases
        {
            get => _includeCurrentSectionDolphinPrereleases;
            set
            {
                if (_includeCurrentSectionDolphinPrereleases == value)
                    return;

                _includeCurrentSectionDolphinPrereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionDolphinIncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeDolphinPrereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionDolphinInfo();
            }
        }

        private bool _isCurrentSectionDolphinUpdateAvailable;
        public bool IsCurrentSectionDolphinUpdateAvailable
        {
            get => _isCurrentSectionDolphinUpdateAvailable;
            set
            {
                if (_isCurrentSectionDolphinUpdateAvailable == value)
                    return;

                _isCurrentSectionDolphinUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionDolphinBusy;
        public bool IsCurrentSectionDolphinBusy
        {
            get => _isCurrentSectionDolphinBusy;
            set
            {
                if (_isCurrentSectionDolphinBusy == value)
                    return;

                _isCurrentSectionDolphinBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionDolphinDownloading;
        public bool IsCurrentSectionDolphinDownloading
        {
            get => _isCurrentSectionDolphinDownloading;
            set
            {
                if (_isCurrentSectionDolphinDownloading == value)
                    return;

                _isCurrentSectionDolphinDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionDolphinDownloadProgress;
        public double CurrentSectionDolphinDownloadProgress
        {
            get => _currentSectionDolphinDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionDolphinDownloadProgress - value) < 0.01)
                    return;

                _currentSectionDolphinDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDolphinEmulatorPath;
        public string? CurrentSectionDolphinEmulatorPath
        {
            get => _currentSectionDolphinEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionDolphinEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionDolphinEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDolphinUpdatePath;
        public string? CurrentSectionDolphinUpdatePath
        {
            get => _currentSectionDolphinUpdatePath;
            set
            {
                if (string.Equals(_currentSectionDolphinUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionDolphinUpdatePath = value;
                OnPropertyChanged();
            }
        }

        private AvaloniaList<string> _currentSectionRpcs3AvailableVersions = [];
        public AvaloniaList<string> CurrentSectionRpcs3AvailableVersions
        {
            get => _currentSectionRpcs3AvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionRpcs3AvailableVersions, value))
                    return;

                _currentSectionRpcs3AvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionRpcs3Version;
        public string? SelectedCurrentSectionRpcs3Version
        {
            get => _selectedCurrentSectionRpcs3Version;
            set
            {
                if (string.Equals(_selectedCurrentSectionRpcs3Version, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionRpcs3Version = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionRpcs3VersionChanged(value);
            }
        }

        private string? _currentSectionRpcs3CurrentVersion;
        public string? CurrentSectionRpcs3CurrentVersion
        {
            get => _currentSectionRpcs3CurrentVersion;
            set
            {
                if (string.Equals(_currentSectionRpcs3CurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionRpcs3CurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRpcs3LatestVersion;
        public string? CurrentSectionRpcs3LatestVersion
        {
            get => _currentSectionRpcs3LatestVersion;
            set
            {
                if (string.Equals(_currentSectionRpcs3LatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionRpcs3LatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionRpcs3Status = "Select an RPCS3 section to manage updates.";
        public string CurrentSectionRpcs3Status
        {
            get => _currentSectionRpcs3Status;
            set
            {
                if (string.Equals(_currentSectionRpcs3Status, value, StringComparison.Ordinal))
                    return;

                _currentSectionRpcs3Status = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionRpcs3Prereleases;
        public bool IncludeCurrentSectionRpcs3Prereleases
        {
            get => _includeCurrentSectionRpcs3Prereleases;
            set
            {
                if (_includeCurrentSectionRpcs3Prereleases == value)
                    return;

                _includeCurrentSectionRpcs3Prereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionRpcs3IncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeRpcs3Prereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionRpcs3Info();
            }
        }

        private bool _isCurrentSectionRpcs3UpdateAvailable;
        public bool IsCurrentSectionRpcs3UpdateAvailable
        {
            get => _isCurrentSectionRpcs3UpdateAvailable;
            set
            {
                if (_isCurrentSectionRpcs3UpdateAvailable == value)
                    return;

                _isCurrentSectionRpcs3UpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionRpcs3Busy;
        public bool IsCurrentSectionRpcs3Busy
        {
            get => _isCurrentSectionRpcs3Busy;
            set
            {
                if (_isCurrentSectionRpcs3Busy == value)
                    return;

                _isCurrentSectionRpcs3Busy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionRpcs3Downloading;
        public bool IsCurrentSectionRpcs3Downloading
        {
            get => _isCurrentSectionRpcs3Downloading;
            set
            {
                if (_isCurrentSectionRpcs3Downloading == value)
                    return;

                _isCurrentSectionRpcs3Downloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionRpcs3DownloadProgress;
        public double CurrentSectionRpcs3DownloadProgress
        {
            get => _currentSectionRpcs3DownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionRpcs3DownloadProgress - value) < 0.01)
                    return;

                _currentSectionRpcs3DownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRpcs3EmulatorPath;
        public string? CurrentSectionRpcs3EmulatorPath
        {
            get => _currentSectionRpcs3EmulatorPath;
            set
            {
                if (string.Equals(_currentSectionRpcs3EmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionRpcs3EmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionRpcs3UpdatePath;
        public string? CurrentSectionRpcs3UpdatePath
        {
            get => _currentSectionRpcs3UpdatePath;
            set
            {
                if (string.Equals(_currentSectionRpcs3UpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionRpcs3UpdatePath = value;
                OnPropertyChanged();
            }
        }

        private AvaloniaList<string> _currentSectionPcsx2AvailableVersions = [];
        public AvaloniaList<string> CurrentSectionPcsx2AvailableVersions
        {
            get => _currentSectionPcsx2AvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionPcsx2AvailableVersions, value))
                    return;

                _currentSectionPcsx2AvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionPcsx2Version;
        public string? SelectedCurrentSectionPcsx2Version
        {
            get => _selectedCurrentSectionPcsx2Version;
            set
            {
                if (string.Equals(_selectedCurrentSectionPcsx2Version, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionPcsx2Version = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionPcsx2VersionChanged(value);
            }
        }

        private string? _currentSectionPcsx2CurrentVersion;
        public string? CurrentSectionPcsx2CurrentVersion
        {
            get => _currentSectionPcsx2CurrentVersion;
            set
            {
                if (string.Equals(_currentSectionPcsx2CurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionPcsx2CurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionPcsx2LatestVersion;
        public string? CurrentSectionPcsx2LatestVersion
        {
            get => _currentSectionPcsx2LatestVersion;
            set
            {
                if (string.Equals(_currentSectionPcsx2LatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionPcsx2LatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionPcsx2Status = "Select a PCSX2 section to manage updates.";
        public string CurrentSectionPcsx2Status
        {
            get => _currentSectionPcsx2Status;
            set
            {
                if (string.Equals(_currentSectionPcsx2Status, value, StringComparison.Ordinal))
                    return;

                _currentSectionPcsx2Status = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionPcsx2Prereleases;
        public bool IncludeCurrentSectionPcsx2Prereleases
        {
            get => _includeCurrentSectionPcsx2Prereleases;
            set
            {
                if (_includeCurrentSectionPcsx2Prereleases == value)
                    return;

                _includeCurrentSectionPcsx2Prereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionPcsx2IncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludePcsx2Prereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionPcsx2Info();
            }
        }

        private bool _isCurrentSectionPcsx2UpdateAvailable;
        public bool IsCurrentSectionPcsx2UpdateAvailable
        {
            get => _isCurrentSectionPcsx2UpdateAvailable;
            set
            {
                if (_isCurrentSectionPcsx2UpdateAvailable == value)
                    return;

                _isCurrentSectionPcsx2UpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionPcsx2Busy;
        public bool IsCurrentSectionPcsx2Busy
        {
            get => _isCurrentSectionPcsx2Busy;
            set
            {
                if (_isCurrentSectionPcsx2Busy == value)
                    return;

                _isCurrentSectionPcsx2Busy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionPcsx2Downloading;
        public bool IsCurrentSectionPcsx2Downloading
        {
            get => _isCurrentSectionPcsx2Downloading;
            set
            {
                if (_isCurrentSectionPcsx2Downloading == value)
                    return;

                _isCurrentSectionPcsx2Downloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionPcsx2DownloadProgress;
        public double CurrentSectionPcsx2DownloadProgress
        {
            get => _currentSectionPcsx2DownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionPcsx2DownloadProgress - value) < 0.01)
                    return;

                _currentSectionPcsx2DownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionPcsx2EmulatorPath;
        public string? CurrentSectionPcsx2EmulatorPath
        {
            get => _currentSectionPcsx2EmulatorPath;
            set
            {
                if (string.Equals(_currentSectionPcsx2EmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionPcsx2EmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionPcsx2UpdatePath;
        public string? CurrentSectionPcsx2UpdatePath
        {
            get => _currentSectionPcsx2UpdatePath;
            set
            {
                if (string.Equals(_currentSectionPcsx2UpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionPcsx2UpdatePath = value;
                OnPropertyChanged();
            }
        }

        private AvaloniaList<string> _currentSectionDuckStationAvailableVersions = [];
        public AvaloniaList<string> CurrentSectionDuckStationAvailableVersions
        {
            get => _currentSectionDuckStationAvailableVersions;
            set
            {
                if (ReferenceEquals(_currentSectionDuckStationAvailableVersions, value))
                    return;

                _currentSectionDuckStationAvailableVersions = value;
                OnPropertyChanged();
            }
        }

        private string? _selectedCurrentSectionDuckStationVersion;
        public string? SelectedCurrentSectionDuckStationVersion
        {
            get => _selectedCurrentSectionDuckStationVersion;
            set
            {
                if (string.Equals(_selectedCurrentSectionDuckStationVersion, value, StringComparison.Ordinal))
                    return;

                _selectedCurrentSectionDuckStationVersion = value;
                OnPropertyChanged();
                OnSelectedCurrentSectionDuckStationVersionChanged(value);
            }
        }

        private string? _currentSectionDuckStationCurrentVersion;
        public string? CurrentSectionDuckStationCurrentVersion
        {
            get => _currentSectionDuckStationCurrentVersion;
            set
            {
                if (string.Equals(_currentSectionDuckStationCurrentVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionDuckStationCurrentVersion = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDuckStationLatestVersion;
        public string? CurrentSectionDuckStationLatestVersion
        {
            get => _currentSectionDuckStationLatestVersion;
            set
            {
                if (string.Equals(_currentSectionDuckStationLatestVersion, value, StringComparison.Ordinal))
                    return;

                _currentSectionDuckStationLatestVersion = value;
                OnPropertyChanged();
            }
        }

        private string _currentSectionDuckStationStatus = "Select a DuckStation section to manage updates.";
        public string CurrentSectionDuckStationStatus
        {
            get => _currentSectionDuckStationStatus;
            set
            {
                if (string.Equals(_currentSectionDuckStationStatus, value, StringComparison.Ordinal))
                    return;

                _currentSectionDuckStationStatus = value;
                OnPropertyChanged();
            }
        }

        private bool _includeCurrentSectionDuckStationPrereleases;
        public bool IncludeCurrentSectionDuckStationPrereleases
        {
            get => _includeCurrentSectionDuckStationPrereleases;
            set
            {
                if (_includeCurrentSectionDuckStationPrereleases == value)
                    return;

                _includeCurrentSectionDuckStationPrereleases = value;
                OnPropertyChanged();

                if (_isSyncingCurrentSectionDuckStationIncludePrereleases)
                    return;

                var section = CurrentEmulationSectionItem;
                if (section?.LaunchSettings == null)
                    return;

                section.LaunchSettings.IncludeDuckStationPrereleases = value;
                SettingsViewModel?.SaveSettings();
                _ = RefreshCurrentSectionDuckStationInfo();
            }
        }

        private bool _isCurrentSectionDuckStationUpdateAvailable;
        public bool IsCurrentSectionDuckStationUpdateAvailable
        {
            get => _isCurrentSectionDuckStationUpdateAvailable;
            set
            {
                if (_isCurrentSectionDuckStationUpdateAvailable == value)
                    return;

                _isCurrentSectionDuckStationUpdateAvailable = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));
            }
        }

        private bool _isCurrentSectionDuckStationBusy;
        public bool IsCurrentSectionDuckStationBusy
        {
            get => _isCurrentSectionDuckStationBusy;
            set
            {
                if (_isCurrentSectionDuckStationBusy == value)
                    return;

                _isCurrentSectionDuckStationBusy = value;
                OnPropertyChanged();
            }
        }

        private bool _isCurrentSectionDuckStationDownloading;
        public bool IsCurrentSectionDuckStationDownloading
        {
            get => _isCurrentSectionDuckStationDownloading;
            set
            {
                if (_isCurrentSectionDuckStationDownloading == value)
                    return;

                _isCurrentSectionDuckStationDownloading = value;
                OnPropertyChanged();
            }
        }

        private double _currentSectionDuckStationDownloadProgress;
        public double CurrentSectionDuckStationDownloadProgress
        {
            get => _currentSectionDuckStationDownloadProgress;
            set
            {
                if (Math.Abs(_currentSectionDuckStationDownloadProgress - value) < 0.01)
                    return;

                _currentSectionDuckStationDownloadProgress = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDuckStationEmulatorPath;
        public string? CurrentSectionDuckStationEmulatorPath
        {
            get => _currentSectionDuckStationEmulatorPath;
            set
            {
                if (string.Equals(_currentSectionDuckStationEmulatorPath, value, StringComparison.Ordinal))
                    return;

                _currentSectionDuckStationEmulatorPath = value;
                OnPropertyChanged();
            }
        }

        private string? _currentSectionDuckStationUpdatePath;
        public string? CurrentSectionDuckStationUpdatePath
        {
            get => _currentSectionDuckStationUpdatePath;
            set
            {
                if (string.Equals(_currentSectionDuckStationUpdatePath, value, StringComparison.Ordinal))
                    return;

                _currentSectionDuckStationUpdatePath = value;
                OnPropertyChanged();
            }
        }

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

        public bool IsShadPs4PatchesOverlayOpen
        {
            get => _isShadPs4PatchesOverlayOpen;
            set
            {
                if (_isShadPs4PatchesOverlayOpen == value)
                    return;

                _isShadPs4PatchesOverlayOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsShadPs4PatchesBusy
        {
            get => _isShadPs4PatchesBusy;
            set
            {
                if (_isShadPs4PatchesBusy == value)
                    return;

                _isShadPs4PatchesBusy = value;
                OnPropertyChanged();
            }
        }

        public string ShadPs4PatchesStatus
        {
            get => _shadPs4PatchesStatus;
            set
            {
                if (string.Equals(_shadPs4PatchesStatus, value, StringComparison.Ordinal))
                    return;

                _shadPs4PatchesStatus = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<ShadPs4PatchFileItem> CurrentSectionShadPs4PatchFiles => _currentSectionShadPs4PatchFiles;

        public bool IsShadPs4PatchSwitchPromptVisible
        {
            get => _isShadPs4PatchSwitchPromptVisible;
            private set
            {
                if (_isShadPs4PatchSwitchPromptVisible == value)
                    return;

                _isShadPs4PatchSwitchPromptVisible = value;
                OnPropertyChanged();
            }
        }

        public ShadPs4PatchFileItem? SelectedCurrentSectionShadPs4PatchFileItem
        {
            get => _selectedCurrentSectionShadPs4PatchFileItem;
            set
            {
                if (ReferenceEquals(_selectedCurrentSectionShadPs4PatchFileItem, value))
                    return;

                SelectedCurrentSectionShadPs4PatchFile = value?.FilePath;

                if (!IsShadPs4PatchSwitchPromptVisible)
                {
                    _selectedCurrentSectionShadPs4PatchFileItem = value;
                    OnPropertyChanged();
                }
                else
                {
                    OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFileItem));
                }
            }
        }

        public string? SelectedCurrentSectionShadPs4PatchFile
        {
            get => _selectedCurrentSectionShadPs4PatchFile;
            set
            {
                if (string.Equals(_selectedCurrentSectionShadPs4PatchFile, value, StringComparison.Ordinal))
                    return;

                var hasUnsavedChanges = IsCurrentSectionShadPs4PatchDirty && !string.IsNullOrWhiteSpace(_activeShadPs4PatchDocumentPath);
                if (!_isSwitchingCurrentSectionShadPs4PatchFile &&
                    hasUnsavedChanges &&
                    !string.Equals(value, _activeShadPs4PatchDocumentPath, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingCurrentSectionShadPs4PatchFile = value;
                    IsShadPs4PatchSwitchPromptVisible = true;
                    ShadPs4PatchesStatus = "You have unsaved patch changes. Save or Skip before switching patch file.";
                    OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFile));
                    OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFileItem));
                    return;
                }

                _selectedCurrentSectionShadPs4PatchFile = value;
                OnPropertyChanged();
                IsShadPs4PatchSwitchPromptVisible = false;
                SyncSelectedShadPs4PatchFileItemFromPath();
                LoadSelectedShadPs4PatchEntries(value);
            }
        }

        public AvaloniaList<ShadPs4PatchEntry> CurrentSectionShadPs4PatchEntries => _currentSectionShadPs4PatchEntries;

        public bool IsCurrentSectionShadPs4PatchDirty
        {
            get => _isCurrentSectionShadPs4PatchDirty;
            set
            {
                if (_isCurrentSectionShadPs4PatchDirty == value)
                    return;

                _isCurrentSectionShadPs4PatchDirty = value;
                OnPropertyChanged();
            }
        }

        private string? _shadPs4DetectedTitleId;
        public string? ShadPs4DetectedTitleId
        {
            get => _shadPs4DetectedTitleId;
            private set
            {
                if (string.Equals(_shadPs4DetectedTitleId, value, StringComparison.Ordinal))
                    return;

                _shadPs4DetectedTitleId = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<XeniaPatchFileItem> CurrentSectionXeniaPatchFiles => _currentSectionXeniaPatchFiles;

        public string XeniaPatchOverlayHeader =>
            string.IsNullOrWhiteSpace(_xeniaPatchOverlayGameTitle)
                ? "Xenia Patches"
                : $"{_xeniaPatchOverlayGameTitle} — Patches";

        public XeniaPatchFileItem? SelectedCurrentSectionXeniaPatchFileItem
        {
            get => _selectedCurrentSectionXeniaPatchFileItem;
            set
            {
                if (ReferenceEquals(_selectedCurrentSectionXeniaPatchFileItem, value))
                    return;

                SelectedCurrentSectionXeniaPatchFile = value?.FilePath;

                if (!IsXeniaPatchSwitchPromptVisible)
                {
                    _selectedCurrentSectionXeniaPatchFileItem = value;
                    OnPropertyChanged();
                }
                else
                {
                    OnPropertyChanged(nameof(SelectedCurrentSectionXeniaPatchFileItem));
                }
            }
        }

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
                    OnPropertyChanged(nameof(SelectedCurrentSectionXeniaPatchFileItem));
                    return;
                }

                _selectedCurrentSectionXeniaPatchFile = value;
                OnPropertyChanged();
                IsXeniaPatchSwitchPromptVisible = false;
                SyncSelectedXeniaPatchFileItemFromPath();
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

        private void OnSelectedCurrentSectionRetroArchVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionRetroArchVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedRetroArchVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedRetroArchVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private async Task ApplyCurrentSectionRetroArchRepository()
        {
            if (!ShowCurrentSectionRetroArchUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(CurrentSectionRetroArchRepositoryOverride)
                ? null
                : CurrentSectionRetroArchRepositoryOverride.Trim();

            if (!string.Equals(section.LaunchSettings.RetroArchRepositoryOverride, normalized, StringComparison.OrdinalIgnoreCase))
            {
                section.LaunchSettings.RetroArchRepositoryOverride = normalized;
                SettingsViewModel?.SaveSettings();
            }

            IsCurrentSectionRetroArchRepositoryDirty = false;
            await RefreshCurrentSectionRetroArchInfo();
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

        private async Task RefreshCurrentSectionCemuInfo()
        {
            if (!ShowCurrentSectionCemuSection)
            {
                CurrentSectionCemuStatus = "Select a Cemu section to manage updates.";
                CurrentSectionCemuAvailableVersions.Clear();
                CurrentSectionCemuCurrentVersion = null;
                CurrentSectionCemuLatestVersion = null;
                IsCurrentSectionCemuUpdateAvailable = false;
                CurrentSectionCemuEmulatorPath = null;
                CurrentSectionCemuUpdatePath = null;
                CurrentSectionCemuDownloadProgress = 0;
                IsCurrentSectionCemuDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _cemuEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionCemuBusy = true;
            IsCurrentSectionCemuDownloading = false;
            CurrentSectionCemuDownloadProgress = 0;

            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyCemuUpdateState(state));
            }
            catch (Exception ex)
            {
                CurrentSectionCemuStatus = $"Failed to check Cemu releases: {ex.Message}";
            }
            finally
            {
                IsCurrentSectionCemuBusy = false;
                IsCurrentSectionCemuDownloading = false;
            }
        }

        private void ApplyCemuUpdateState(CemuUpdateState state)
        {
            CurrentSectionCemuCurrentVersion = state.CurrentVersion;
            CurrentSectionCemuLatestVersion = state.LatestVersion;
            IsCurrentSectionCemuUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionCemuStatus = state.StatusMessage;
            CurrentSectionCemuEmulatorPath = state.EmulatorDirectory;
            CurrentSectionCemuUpdatePath = state.UpdateDirectory;

            CurrentSectionCemuAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions)
                CurrentSectionCemuAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionCemuVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionCemuAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionCemuAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionCemuVersionSelection = true;
                SelectedCurrentSectionCemuVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionCemuVersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private IAsyncRelayCommand? _refreshCurrentSectionCemuInfoCommand;
        public IAsyncRelayCommand RefreshCurrentSectionCemuInfoCommand =>
            _refreshCurrentSectionCemuInfoCommand ??= new AsyncRelayCommand(RefreshCurrentSectionCemuInfo);

        private async Task DownloadOrUpdateCurrentSectionCemu()
        {
            if (!ShowCurrentSectionCemuSection)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _cemuEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionCemuBusy = true;
            IsCurrentSectionCemuDownloading = true;
            CurrentSectionCemuDownloadProgress = 0;

            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    SelectedCurrentSectionCemuVersion,
                    progress =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            CurrentSectionCemuDownloadProgress = progress.Percent;
                            if (!string.IsNullOrWhiteSpace(progress.StatusMessage))
                                CurrentSectionCemuStatus = progress.StatusMessage;
                        });
                    }).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionCemuDownloadProgress = 100;
                    ApplyCemuUpdateState(state);

                    if (!string.IsNullOrWhiteSpace(state.ResolvedLauncherPath))
                    {
                        var updatedPath = state.ResolvedLauncherPath;
                        if (!string.Equals(handler.LauncherPath, updatedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            handler.LauncherPath = updatedPath;
                            SettingsViewModel?.SaveSettings();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                CurrentSectionCemuStatus = $"Cemu update failed: {ex.Message}";
            }
            finally
            {
                IsCurrentSectionCemuBusy = false;
                IsCurrentSectionCemuDownloading = false;
            }
        }

        private IAsyncRelayCommand? _downloadOrUpdateCurrentSectionCemuCommand;
        public IAsyncRelayCommand DownloadOrUpdateCurrentSectionCemuCommand =>
            _downloadOrUpdateCurrentSectionCemuCommand ??= new AsyncRelayCommand(DownloadOrUpdateCurrentSectionCemu);

        private void OnSelectedCurrentSectionCemuVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionCemuVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedCemuVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedCemuVersion = normalized;
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

        private void OnSelectedCurrentSectionPcsx2VersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionPcsx2VersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedPcsx2Version, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedPcsx2Version = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionDolphinVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionDolphinVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedDolphinVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedDolphinVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionFlycastVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionFlycastVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedFlycastVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedFlycastVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionDuckStationVersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionDuckStationVersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedDuckStationVersion, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedDuckStationVersion = normalized;
            SettingsViewModel?.SaveSettings();
        }

        private void OnSelectedCurrentSectionRpcs3VersionChanged(string? value)
        {
            if (_isSyncingCurrentSectionRpcs3VersionSelection)
                return;

            var section = CurrentEmulationSectionItem;
            if (section?.LaunchSettings == null)
                return;

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(section.LaunchSettings.SelectedRpcs3Version, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            section.LaunchSettings.SelectedRpcs3Version = normalized;
            SettingsViewModel?.SaveSettings();
        }

        public bool ShowCurrentSectionRetroArchCoreSelection =>
            CurrentEmulatorHandler?.UsesRetroArchCores == true &&
            CurrentSectionRetroArchCores.Count > 0;

        public bool ShowCurrentSectionRetroArchUpdateControls =>
            CurrentEmulatorHandler?.UsesRetroArchCores == true &&
            CurrentEmulationSectionItem != null;

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

        public bool ShowCurrentSectionRpcs3UpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, Rpcs3Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionPcsx2UpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionDolphinUpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionFlycastUpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, FlyCastHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionDuckStationUpdateControls =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionCemuSection =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, CemuHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionPcsx2SetupLaunchButton =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulatorHandler.IsLauncherPathValid(CurrentEmulatorHandler.LauncherPath) &&
            !IsEmulatorRunning &&
            !IsEmulatorLaunchInProgress;

        public bool ShowCurrentSectionDuckStationSetupLaunchButton =>
            CurrentEmulatorHandler != null &&
            string.Equals(CurrentEmulatorHandler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulatorHandler.IsLauncherPathValid(CurrentEmulatorHandler.LauncherPath) &&
            !IsEmulatorRunning &&
            !IsEmulatorLaunchInProgress;

        public Bitmap? CurrentSectionSetupLaunchIcon => ResolveCurrentSectionSetupLaunchIcon();

        public bool HasCurrentSectionSetupLaunchIcon => CurrentSectionSetupLaunchIcon != null;

        public string CurrentSectionSetupLaunchToolTip =>
            CurrentEmulatorHandler?.DisplayName is { Length: > 0 } handlerName
                ? $"Launch {handlerName}"
                : "Launch emulator";

        public bool CanLaunchCurrentSectionHandlerSetup =>
            CurrentEmulatorHandler != null &&
            CurrentEmulatorHandler?.IsLauncherPathValid(CurrentEmulatorHandler.LauncherPath) == true &&
            !IsEmulatorRunning &&
            !IsEmulatorLaunchInProgress;

        public bool ShowCurrentSectionXeniaPatchesMenuItem =>
            ShowCurrentSectionXeniaUpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionShadPs4PatchesMenuItem =>
            ShowCurrentSectionShadPs4UpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionShadPs4CustomConfigMenuItem =>
            ShowCurrentSectionShadPs4UpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionShadPs4CheatsMenuItem =>
            ShowCurrentSectionShadPs4UpdateControls && HasActiveAlbumItems;

        public bool ShowShadPs4InGameCheatsButton =>
            IsEmulatorRunning &&
            string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);

        public bool ShowCurrentSectionXeniaCustomConfigMenuItem =>
            ShowCurrentSectionXeniaUpdateControls && HasActiveAlbumItems;

        public bool IsCurrentSectionHandlerUpdateAvailable =>
            (ShowCurrentSectionRetroArchUpdateControls && IsCurrentSectionRetroArchUpdateAvailable) ||
            (ShowCurrentSectionEdenUpdateControls && IsCurrentSectionEdenUpdateAvailable) ||
            (ShowCurrentSectionShadPs4UpdateControls && IsCurrentSectionShadPs4UpdateAvailable) ||
            (ShowCurrentSectionXeniaUpdateControls && IsCurrentSectionXeniaUpdateAvailable) ||
            (ShowCurrentSectionRpcs3UpdateControls && IsCurrentSectionRpcs3UpdateAvailable) ||
            (ShowCurrentSectionDolphinUpdateControls && IsCurrentSectionDolphinUpdateAvailable) ||
            (ShowCurrentSectionFlycastUpdateControls && IsCurrentSectionFlycastUpdateAvailable) ||
            (ShowCurrentSectionPcsx2UpdateControls && IsCurrentSectionPcsx2UpdateAvailable) ||
            (ShowCurrentSectionCemuSection && IsCurrentSectionCemuUpdateAvailable) ||
            (ShowCurrentSectionDuckStationUpdateControls && IsCurrentSectionDuckStationUpdateAvailable);

        private void RefreshCurrentSectionLaunchOptionsState()
        {
            OnPropertyChanged(nameof(CurrentSectionSetupLaunchIcon));
            OnPropertyChanged(nameof(HasCurrentSectionSetupLaunchIcon));
            OnPropertyChanged(nameof(CurrentSectionSetupLaunchToolTip));
            OnPropertyChanged(nameof(CanLaunchCurrentSectionHandlerSetup));
            LaunchCurrentSectionHandlerSetupCommand.NotifyCanExecuteChanged();

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
            var sectionRetroArchRepoOverride = section?.LaunchSettings?.RetroArchRepositoryOverride;
            if (!string.Equals(CurrentSectionRetroArchRepositoryOverride, sectionRetroArchRepoOverride, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionRetroArchRepositoryOverride = true;
                    CurrentSectionRetroArchRepositoryOverride = sectionRetroArchRepoOverride;
                }
                finally
                {
                    _isSyncingCurrentSectionRetroArchRepositoryOverride = false;
                }
            }

            IsCurrentSectionRetroArchRepositoryDirty = false;

            var includeRetroArchCores = section?.LaunchSettings?.IncludeRetroArchCores == true;
            if (IncludeCurrentSectionRetroArchCores != includeRetroArchCores)
            {
                try
                {
                    _isSyncingCurrentSectionRetroArchIncludeCores = true;
                    IncludeCurrentSectionRetroArchCores = includeRetroArchCores;
                }
                finally
                {
                    _isSyncingCurrentSectionRetroArchIncludeCores = false;
                }
            }

            var sectionRetroArchVersion = section?.LaunchSettings?.SelectedRetroArchVersion;
            if (!string.Equals(SelectedCurrentSectionRetroArchVersion, sectionRetroArchVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionRetroArchVersionSelection = true;
                    SelectedCurrentSectionRetroArchVersion = sectionRetroArchVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionRetroArchVersionSelection = false;
                }
            }
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

            var sectionCemuVersion = section?.LaunchSettings?.SelectedCemuVersion;
            if (!string.Equals(SelectedCurrentSectionCemuVersion, sectionCemuVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionCemuVersionSelection = true;
                    SelectedCurrentSectionCemuVersion = sectionCemuVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionCemuVersionSelection = false;
                }
            }

            OnPropertyChanged(nameof(CurrentEmulationSectionItem));
            OnPropertyChanged(nameof(CurrentSectionRetroArchCores));
            OnPropertyChanged(nameof(ShowCurrentSectionRetroArchCoreSelection));
            OnPropertyChanged(nameof(ShowCurrentSectionRetroArchUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionEdenUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionShadPs4UpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionShadPs4PatchesMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionShadPs4CustomConfigMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionShadPs4CheatsMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionXeniaUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionXeniaPatchesMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionXeniaCustomConfigMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionRpcs3UpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionDolphinUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionFlycastUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2UpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationUpdateControls));
            OnPropertyChanged(nameof(ShowCurrentSectionCemuSection));
            OnPropertyChanged(nameof(IsCurrentSectionHandlerUpdateAvailable));

            if (ShowCurrentSectionRetroArchUpdateControls)
            {
                _ = RefreshCurrentSectionRetroArchInfo();
            }
            else
            {
                CurrentSectionRetroArchAvailableVersions.Clear();
                CurrentSectionRetroArchCurrentVersion = null;
                CurrentSectionRetroArchLatestVersion = null;
                CurrentSectionRetroArchStatus = "Select a RetroArch section to manage updates.";
                IsCurrentSectionRetroArchUpdateAvailable = false;
                CurrentSectionRetroArchEmulatorPath = null;
                CurrentSectionRetroArchUpdatePath = null;
                CurrentSectionRetroArchDownloadProgress = 0;
                IsCurrentSectionRetroArchDownloading = false;
                IsCurrentSectionRetroArchRepositoryDirty = false;
                try
                {
                    _isSyncingCurrentSectionRetroArchIncludeCores = true;
                    IncludeCurrentSectionRetroArchCores = false;
                }
                finally
                {
                    _isSyncingCurrentSectionRetroArchIncludeCores = false;
                }
            }

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

            var sectionRpcs3Version = section?.LaunchSettings?.SelectedRpcs3Version;
            if (!string.Equals(SelectedCurrentSectionRpcs3Version, sectionRpcs3Version, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionRpcs3VersionSelection = true;
                    SelectedCurrentSectionRpcs3Version = sectionRpcs3Version;
                }
                finally
                {
                    _isSyncingCurrentSectionRpcs3VersionSelection = false;
                }
            }

            var includeRpcs3Prereleases = section?.LaunchSettings?.IncludeRpcs3Prereleases == true;
            if (IncludeCurrentSectionRpcs3Prereleases != includeRpcs3Prereleases)
            {
                try
                {
                    _isSyncingCurrentSectionRpcs3IncludePrereleases = true;
                    IncludeCurrentSectionRpcs3Prereleases = includeRpcs3Prereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionRpcs3IncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionRpcs3UpdateControls)
            {
                _ = RefreshCurrentSectionRpcs3Info();
            }
            else
            {
                CurrentSectionRpcs3AvailableVersions.Clear();
                CurrentSectionRpcs3CurrentVersion = null;
                CurrentSectionRpcs3LatestVersion = null;
                CurrentSectionRpcs3Status = "Select an RPCS3 section to manage updates.";
                IsCurrentSectionRpcs3UpdateAvailable = false;
                CurrentSectionRpcs3EmulatorPath = null;
                CurrentSectionRpcs3UpdatePath = null;
                CurrentSectionRpcs3DownloadProgress = 0;
                IsCurrentSectionRpcs3Downloading = false;
                try
                {
                    _isSyncingCurrentSectionRpcs3IncludePrereleases = true;
                    IncludeCurrentSectionRpcs3Prereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionRpcs3IncludePrereleases = false;
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

            var sectionPcsx2Version = section?.LaunchSettings?.SelectedPcsx2Version;
            if (!string.Equals(SelectedCurrentSectionPcsx2Version, sectionPcsx2Version, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionPcsx2VersionSelection = true;
                    SelectedCurrentSectionPcsx2Version = sectionPcsx2Version;
                }
                finally
                {
                    _isSyncingCurrentSectionPcsx2VersionSelection = false;
                }
            }

            var sectionDolphinVersion = section?.LaunchSettings?.SelectedDolphinVersion;
            if (!string.Equals(SelectedCurrentSectionDolphinVersion, sectionDolphinVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionDolphinVersionSelection = true;
                    SelectedCurrentSectionDolphinVersion = sectionDolphinVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionDolphinVersionSelection = false;
                }
            }

            var includeDolphinPrereleases = section?.LaunchSettings?.IncludeDolphinPrereleases == true;
            if (IncludeCurrentSectionDolphinPrereleases != includeDolphinPrereleases)
            {
                try
                {
                    _isSyncingCurrentSectionDolphinIncludePrereleases = true;
                    IncludeCurrentSectionDolphinPrereleases = includeDolphinPrereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionDolphinIncludePrereleases = false;
                }
            }

            var includeFlycastNightlies = section?.LaunchSettings?.IncludeFlycastNightlies == true;
            if (IncludeCurrentSectionFlycastNightlies != includeFlycastNightlies)
            {
                try
                {
                    _isSyncingCurrentSectionFlycastNightlies = true;
                    IncludeCurrentSectionFlycastNightlies = includeFlycastNightlies;
                }
                finally
                {
                    _isSyncingCurrentSectionFlycastNightlies = false;
                }
            }

            var sectionFlycastVersion = section?.LaunchSettings?.SelectedFlycastVersion;
            if (!string.Equals(SelectedCurrentSectionFlycastVersion, sectionFlycastVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionFlycastVersionSelection = true;
                    SelectedCurrentSectionFlycastVersion = sectionFlycastVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionFlycastVersionSelection = false;
                }
            }

            var includePcsx2Prereleases = section?.LaunchSettings?.IncludePcsx2Prereleases == true;
            if (IncludeCurrentSectionPcsx2Prereleases != includePcsx2Prereleases)
            {
                try
                {
                    _isSyncingCurrentSectionPcsx2IncludePrereleases = true;
                    IncludeCurrentSectionPcsx2Prereleases = includePcsx2Prereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionPcsx2IncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionDolphinUpdateControls)
            {
                _ = RefreshCurrentSectionDolphinInfo();
            }
            else
            {
                CurrentSectionDolphinAvailableVersions.Clear();
                CurrentSectionDolphinCurrentVersion = null;
                CurrentSectionDolphinLatestVersion = null;
                CurrentSectionDolphinStatus = "Select a Dolphin section to manage updates.";
                IsCurrentSectionDolphinUpdateAvailable = false;
                CurrentSectionDolphinEmulatorPath = null;
                CurrentSectionDolphinUpdatePath = null;
                CurrentSectionDolphinDownloadProgress = 0;
                IsCurrentSectionDolphinDownloading = false;
                try
                {
                    _isSyncingCurrentSectionDolphinIncludePrereleases = true;
                    IncludeCurrentSectionDolphinPrereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionDolphinIncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionFlycastUpdateControls)
            {
                _ = RefreshCurrentSectionFlycastInfo();
            }
            else
            {
                CurrentSectionFlycastAvailableVersions.Clear();
                CurrentSectionFlycastCurrentVersion = null;
                CurrentSectionFlycastLatestVersion = null;
                CurrentSectionFlycastStatus = "Select a Flycast section to manage updates.";
                IsCurrentSectionFlycastUpdateAvailable = false;
                CurrentSectionFlycastEmulatorPath = null;
                CurrentSectionFlycastUpdatePath = null;
                CurrentSectionFlycastDownloadProgress = 0;
                IsCurrentSectionFlycastDownloading = false;
                try
                {
                    _isSyncingCurrentSectionFlycastNightlies = true;
                    IncludeCurrentSectionFlycastNightlies = false;
                }
                finally
                {
                    _isSyncingCurrentSectionFlycastNightlies = false;
                }

                try
                {
                    _isSyncingCurrentSectionFlycastVersionSelection = true;
                    SelectedCurrentSectionFlycastVersion = null;
                }
                finally
                {
                    _isSyncingCurrentSectionFlycastVersionSelection = false;
                }
            }

            var sectionDuckStationVersion = section?.LaunchSettings?.SelectedDuckStationVersion;
            if (!string.Equals(SelectedCurrentSectionDuckStationVersion, sectionDuckStationVersion, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionDuckStationVersionSelection = true;
                    SelectedCurrentSectionDuckStationVersion = sectionDuckStationVersion;
                }
                finally
                {
                    _isSyncingCurrentSectionDuckStationVersionSelection = false;
                }
            }

            var includeDuckStationPrereleases = section?.LaunchSettings?.IncludeDuckStationPrereleases == true;
            if (IncludeCurrentSectionDuckStationPrereleases != includeDuckStationPrereleases)
            {
                try
                {
                    _isSyncingCurrentSectionDuckStationIncludePrereleases = true;
                    IncludeCurrentSectionDuckStationPrereleases = includeDuckStationPrereleases;
                }
                finally
                {
                    _isSyncingCurrentSectionDuckStationIncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionDuckStationUpdateControls)
            {
                _ = RefreshCurrentSectionDuckStationInfo();
            }
            else
            {
                CurrentSectionDuckStationAvailableVersions.Clear();
                CurrentSectionDuckStationCurrentVersion = null;
                CurrentSectionDuckStationLatestVersion = null;
                CurrentSectionDuckStationStatus = "Select a DuckStation section to manage updates.";
                IsCurrentSectionDuckStationUpdateAvailable = false;
                CurrentSectionDuckStationEmulatorPath = null;
                CurrentSectionDuckStationUpdatePath = null;
                CurrentSectionDuckStationDownloadProgress = 0;
                IsCurrentSectionDuckStationDownloading = false;
                try
                {
                    _isSyncingCurrentSectionDuckStationIncludePrereleases = true;
                    IncludeCurrentSectionDuckStationPrereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionDuckStationIncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionPcsx2UpdateControls)
            {
                _ = RefreshCurrentSectionPcsx2Info();
            }
            else
            {
                CurrentSectionPcsx2AvailableVersions.Clear();
                CurrentSectionPcsx2CurrentVersion = null;
                CurrentSectionPcsx2LatestVersion = null;
                CurrentSectionPcsx2Status = "Select a PCSX2 section to manage updates.";
                IsCurrentSectionPcsx2UpdateAvailable = false;
                CurrentSectionPcsx2EmulatorPath = null;
                CurrentSectionPcsx2UpdatePath = null;
                CurrentSectionPcsx2DownloadProgress = 0;
                IsCurrentSectionPcsx2Downloading = false;
                try
                {
                    _isSyncingCurrentSectionPcsx2IncludePrereleases = true;
                    IncludeCurrentSectionPcsx2Prereleases = false;
                }
                finally
                {
                    _isSyncingCurrentSectionPcsx2IncludePrereleases = false;
                }
            }

            if (ShowCurrentSectionCemuSection)
            {
                _ = RefreshCurrentSectionCemuInfo();
            }

            if (!ShowCurrentSectionXeniaUpdateControls)
            {
                XeniaCustomConfigEditor.Reset();
                IsXeniaPatchesOverlayOpen = false;
                XeniaDetectedTitleId = null;
                XeniaDetectedMediaId = null;
                _xeniaPatchOverlayGameTitle = null;
                OnPropertyChanged(nameof(XeniaPatchOverlayHeader));
                XeniaPatchesStatus = "Select an Xbox 360 game to manage patches.";
                IsXeniaPatchSwitchPromptVisible = false;
                IsCurrentSectionXeniaPatchDirty = false;
                _pendingCurrentSectionXeniaPatchFile = null;
                _activeXeniaPatchDocumentPath = null;
                _activeXeniaPatchDocumentText = null;
                CurrentSectionXeniaPatchFiles.Clear();
                DetachXeniaPatchEntryListeners();
                CurrentSectionXeniaPatchEntries.Clear();
                _selectedCurrentSectionXeniaPatchFileItem = null;
                SelectedCurrentSectionXeniaPatchFile = null;
            }

            if (!ShowCurrentSectionShadPs4UpdateControls)
            {
                IsShadPs4PatchesOverlayOpen = false;
                IsShadPs4PatchSwitchPromptVisible = false;
                ShadPs4DetectedTitleId = null;
                ShadPs4PatchesStatus = "Select a PlayStation 4 game to manage patches.";
                IsCurrentSectionShadPs4PatchDirty = false;
                _activeShadPs4PatchDocumentPath = null;
                _activeShadPs4PatchDocumentText = null;
                _selectedCurrentSectionShadPs4PatchFile = null;
                _selectedCurrentSectionShadPs4PatchFileItem = null;
                _pendingCurrentSectionShadPs4PatchFile = null;
                CurrentSectionShadPs4PatchFiles.Clear();
                DetachShadPs4PatchEntryListeners();
                CurrentSectionShadPs4PatchEntries.Clear();
                ShadPs4CustomConfigEditor.Reset();
                ShadPs4CheatsEditor.ClearSession();
            }
        }

        private async Task RefreshCurrentSectionRetroArchInfo()
        {
            if (!ShowCurrentSectionRetroArchUpdateControls)
            {
                CurrentSectionRetroArchStatus = "Select a RetroArch section to manage updates.";
                CurrentSectionRetroArchAvailableVersions.Clear();
                CurrentSectionRetroArchCurrentVersion = null;
                CurrentSectionRetroArchLatestVersion = null;
                IsCurrentSectionRetroArchUpdateAvailable = false;
                CurrentSectionRetroArchEmulatorPath = null;
                CurrentSectionRetroArchUpdatePath = null;
                CurrentSectionRetroArchDownloadProgress = 0;
                IsCurrentSectionRetroArchDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _retroArchEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionRetroArchBusy = true;
            IsCurrentSectionRetroArchDownloading = false;
            CurrentSectionRetroArchDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionRetroArchRepositoryOverride,
                    IncludeCurrentSectionRetroArchCores,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyRetroArchUpdateState(state));
            }
            finally
            {
                IsCurrentSectionRetroArchBusy = false;
                IsCurrentSectionRetroArchDownloading = false;
            }
        }

        private async Task DownloadOrUpdateCurrentSectionRetroArch()
        {
            if (!ShowCurrentSectionRetroArchUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _retroArchEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionRetroArchBusy = true;
            IsCurrentSectionRetroArchDownloading = true;
            CurrentSectionRetroArchDownloadProgress = 0;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    CurrentSectionRetroArchRepositoryOverride,
                    IncludeCurrentSectionRetroArchCores,
                    SelectedCurrentSectionRetroArchVersion,
                    progress =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            CurrentSectionRetroArchDownloadProgress = progress.Percent;
                            if (!string.IsNullOrWhiteSpace(progress.StatusMessage))
                                CurrentSectionRetroArchStatus = progress.StatusMessage;
                        });
                    }).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionRetroArchDownloadProgress = 100;
                    ApplyRetroArchUpdateState(state);

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
                IsCurrentSectionRetroArchBusy = false;
                IsCurrentSectionRetroArchDownloading = false;
            }
        }

        private void ApplyRetroArchUpdateState(RetroArchUpdateState state)
        {
            CurrentSectionRetroArchCurrentVersion = state.CurrentVersion;
            CurrentSectionRetroArchLatestVersion = state.LatestVersion;
            IsCurrentSectionRetroArchUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionRetroArchStatus = state.StatusMessage;
            CurrentSectionRetroArchEmulatorPath = state.EmulatorDirectory;
            CurrentSectionRetroArchUpdatePath = state.UpdateDirectory;

            CurrentSectionRetroArchAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionRetroArchAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionRetroArchVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionRetroArchAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionRetroArchAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionRetroArchVersionSelection = true;
                SelectedCurrentSectionRetroArchVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionRetroArchVersionSelection = false;
            }

            if (!string.IsNullOrWhiteSpace(state.Repository) &&
                !string.Equals(CurrentSectionRetroArchRepositoryOverride, state.Repository, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state.Repository, "libretro/RetroArch", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionRetroArchRepositoryOverride = true;
                    CurrentSectionRetroArchRepositoryOverride = state.Repository;
                }
                finally
                {
                    _isSyncingCurrentSectionRetroArchRepositoryOverride = false;
                }
            }

            if (ShowCurrentSectionCemuSection)
            {
                _ = RefreshCurrentSectionCemuInfo();
            }
            else
            {
                CurrentSectionCemuAvailableVersions.Clear();
                CurrentSectionCemuCurrentVersion = null;
                CurrentSectionCemuLatestVersion = null;
                CurrentSectionCemuStatus = "Select a Cemu section to manage updates.";
                IsCurrentSectionCemuUpdateAvailable = false;
                CurrentSectionCemuEmulatorPath = null;
                CurrentSectionCemuUpdatePath = null;
                CurrentSectionCemuDownloadProgress = 0;
                IsCurrentSectionCemuDownloading = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
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
        private async Task RefreshCurrentSectionRpcs3Info()
        {
            if (!ShowCurrentSectionRpcs3UpdateControls)
            {
                CurrentSectionRpcs3Status = "Select an RPCS3 section to manage updates.";
                CurrentSectionRpcs3AvailableVersions.Clear();
                CurrentSectionRpcs3CurrentVersion = null;
                CurrentSectionRpcs3LatestVersion = null;
                IsCurrentSectionRpcs3UpdateAvailable = false;
                CurrentSectionRpcs3EmulatorPath = null;
                CurrentSectionRpcs3UpdatePath = null;
                CurrentSectionRpcs3DownloadProgress = 0;
                IsCurrentSectionRpcs3Downloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _rpcs3EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionRpcs3Busy = true;
            IsCurrentSectionRpcs3Downloading = false;
            CurrentSectionRpcs3DownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionRpcs3Prereleases,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyRpcs3UpdateState(state));
            }
            finally
            {
                IsCurrentSectionRpcs3Busy = false;
                IsCurrentSectionRpcs3Downloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionRpcs3()
        {
            if (!ShowCurrentSectionRpcs3UpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _rpcs3EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionRpcs3Busy = true;
            IsCurrentSectionRpcs3Downloading = true;
            CurrentSectionRpcs3DownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionRpcs3Prereleases,
                    SelectedCurrentSectionRpcs3Version).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionRpcs3DownloadProgress = 100;
                    ApplyRpcs3UpdateState(state);

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
                IsCurrentSectionRpcs3Busy = false;
                IsCurrentSectionRpcs3Downloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionPcsx2Info()
        {
            if (!ShowCurrentSectionPcsx2UpdateControls)
            {
                CurrentSectionPcsx2Status = "Select a PCSX2 section to manage updates.";
                CurrentSectionPcsx2AvailableVersions.Clear();
                CurrentSectionPcsx2CurrentVersion = null;
                CurrentSectionPcsx2LatestVersion = null;
                IsCurrentSectionPcsx2UpdateAvailable = false;
                CurrentSectionPcsx2EmulatorPath = null;
                CurrentSectionPcsx2UpdatePath = null;
                CurrentSectionPcsx2DownloadProgress = 0;
                IsCurrentSectionPcsx2Downloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _pcsx2EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionPcsx2Busy = true;
            IsCurrentSectionPcsx2Downloading = false;
            CurrentSectionPcsx2DownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionPcsx2Prereleases,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyPcsx2UpdateState(state));
            }
            finally
            {
                IsCurrentSectionPcsx2Busy = false;
                IsCurrentSectionPcsx2Downloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionDolphinInfo()
        {
            if (!ShowCurrentSectionDolphinUpdateControls)
            {
                CurrentSectionDolphinStatus = "Select a Dolphin section to manage updates.";
                CurrentSectionDolphinAvailableVersions.Clear();
                CurrentSectionDolphinCurrentVersion = null;
                CurrentSectionDolphinLatestVersion = null;
                IsCurrentSectionDolphinUpdateAvailable = false;
                CurrentSectionDolphinEmulatorPath = null;
                CurrentSectionDolphinUpdatePath = null;
                CurrentSectionDolphinDownloadProgress = 0;
                IsCurrentSectionDolphinDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _dolphinEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionDolphinBusy = true;
            IsCurrentSectionDolphinDownloading = false;
            CurrentSectionDolphinDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    false,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyDolphinUpdateState(state));
            }
            finally
            {
                IsCurrentSectionDolphinBusy = false;
                IsCurrentSectionDolphinDownloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionFlycastInfo()
        {
            if (!ShowCurrentSectionFlycastUpdateControls)
            {
                CurrentSectionFlycastStatus = "Select a Flycast section to manage updates.";
                CurrentSectionFlycastAvailableVersions.Clear();
                CurrentSectionFlycastCurrentVersion = null;
                CurrentSectionFlycastLatestVersion = null;
                IsCurrentSectionFlycastUpdateAvailable = false;
                CurrentSectionFlycastEmulatorPath = null;
                CurrentSectionFlycastUpdatePath = null;
                CurrentSectionFlycastDownloadProgress = 0;
                IsCurrentSectionFlycastDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _flycastEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionFlycastBusy = true;
            IsCurrentSectionFlycastDownloading = false;
            CurrentSectionFlycastDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionFlycastNightlies,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyFlycastUpdateState(state));
            }
            finally
            {
                IsCurrentSectionFlycastBusy = false;
                IsCurrentSectionFlycastDownloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionFlycast()
        {
            if (!ShowCurrentSectionFlycastUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _flycastEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionFlycastBusy = true;
            IsCurrentSectionFlycastDownloading = true;
            CurrentSectionFlycastDownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionFlycastNightlies,
                    SelectedCurrentSectionFlycastVersion).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionFlycastDownloadProgress = 100;
                    ApplyFlycastUpdateState(state);

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
                IsCurrentSectionFlycastBusy = false;
                IsCurrentSectionFlycastDownloading = false;
            }
        }

        [RelayCommand]
        private async Task RefreshCurrentSectionDuckStationInfo()
        {
            if (!ShowCurrentSectionDuckStationUpdateControls)
            {
                CurrentSectionDuckStationStatus = "Select a DuckStation section to manage updates.";
                CurrentSectionDuckStationAvailableVersions.Clear();
                CurrentSectionDuckStationCurrentVersion = null;
                CurrentSectionDuckStationLatestVersion = null;
                IsCurrentSectionDuckStationUpdateAvailable = false;
                CurrentSectionDuckStationEmulatorPath = null;
                CurrentSectionDuckStationUpdatePath = null;
                CurrentSectionDuckStationDownloadProgress = 0;
                IsCurrentSectionDuckStationDownloading = false;
                return;
            }

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _duckStationEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionDuckStationBusy = true;
            IsCurrentSectionDuckStationDownloading = false;
            CurrentSectionDuckStationDownloadProgress = 0;
            try
            {
                var state = await updater.GetUpdateInfoAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionDuckStationPrereleases,
                    forceRefresh: false).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyDuckStationUpdateState(state));
            }
            finally
            {
                IsCurrentSectionDuckStationBusy = false;
                IsCurrentSectionDuckStationDownloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionDuckStation()
        {
            if (!ShowCurrentSectionDuckStationUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _duckStationEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionDuckStationBusy = true;
            IsCurrentSectionDuckStationDownloading = true;
            CurrentSectionDuckStationDownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionDuckStationPrereleases,
                    SelectedCurrentSectionDuckStationVersion).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionDuckStationDownloadProgress = 100;
                    ApplyDuckStationUpdateState(state);

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
                IsCurrentSectionDuckStationBusy = false;
                IsCurrentSectionDuckStationDownloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionPcsx2()
        {
            if (!ShowCurrentSectionPcsx2UpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _pcsx2EmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionPcsx2Busy = true;
            IsCurrentSectionPcsx2Downloading = true;
            CurrentSectionPcsx2DownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    IncludeCurrentSectionPcsx2Prereleases,
                    SelectedCurrentSectionPcsx2Version).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionPcsx2DownloadProgress = 100;
                    ApplyPcsx2UpdateState(state);

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
                IsCurrentSectionPcsx2Busy = false;
                IsCurrentSectionPcsx2Downloading = false;
            }
        }

        [RelayCommand]
        private async Task DownloadOrUpdateCurrentSectionDolphin()
        {
            if (!ShowCurrentSectionDolphinUpdateControls)
                return;

            var section = CurrentEmulationSectionItem;
            var handler = CurrentEmulatorHandler;
            var updater = _dolphinEmulatorUpdateService;
            if (section == null || handler == null || updater == null)
                return;

            IsCurrentSectionDolphinBusy = true;
            IsCurrentSectionDolphinDownloading = true;
            CurrentSectionDolphinDownloadProgress = 5;
            try
            {
                var state = await updater.DownloadOrUpdateAsync(
                    section.SectionKey,
                    section.SectionTitle,
                    handler.LauncherPath,
                    false,
                    SelectedCurrentSectionDolphinVersion).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionDolphinDownloadProgress = 100;
                    ApplyDolphinUpdateState(state);

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
                IsCurrentSectionDolphinBusy = false;
                IsCurrentSectionDolphinDownloading = false;
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

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
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

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private void ApplyRpcs3UpdateState(Rpcs3UpdateState state)
        {
            CurrentSectionRpcs3CurrentVersion = state.CurrentVersion;
            CurrentSectionRpcs3LatestVersion = state.LatestVersion;
            IsCurrentSectionRpcs3UpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionRpcs3Status = state.StatusMessage;
            CurrentSectionRpcs3EmulatorPath = state.EmulatorDirectory;
            CurrentSectionRpcs3UpdatePath = state.UpdateDirectory;

            CurrentSectionRpcs3AvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionRpcs3AvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionRpcs3Version;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionRpcs3AvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionRpcs3AvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionRpcs3VersionSelection = true;
                SelectedCurrentSectionRpcs3Version = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionRpcs3VersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private void ApplyPcsx2UpdateState(Pcsx2UpdateState state)
        {
            CurrentSectionPcsx2CurrentVersion = state.CurrentVersion;
            CurrentSectionPcsx2LatestVersion = state.LatestVersion;
            IsCurrentSectionPcsx2UpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionPcsx2Status = state.StatusMessage;
            CurrentSectionPcsx2EmulatorPath = state.EmulatorDirectory;
            CurrentSectionPcsx2UpdatePath = state.UpdateDirectory;

            CurrentSectionPcsx2AvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionPcsx2AvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionPcsx2Version;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionPcsx2AvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionPcsx2AvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionPcsx2VersionSelection = true;
                SelectedCurrentSectionPcsx2Version = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionPcsx2VersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private void ApplyDolphinUpdateState(DolphinUpdateState state)
        {
            CurrentSectionDolphinCurrentVersion = state.CurrentVersion;
            CurrentSectionDolphinLatestVersion = state.LatestVersion;
            IsCurrentSectionDolphinUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionDolphinStatus = state.StatusMessage;
            CurrentSectionDolphinEmulatorPath = state.EmulatorDirectory;
            CurrentSectionDolphinUpdatePath = state.UpdateDirectory;

            CurrentSectionDolphinAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionDolphinAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionDolphinVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionDolphinAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionDolphinAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionDolphinVersionSelection = true;
                SelectedCurrentSectionDolphinVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionDolphinVersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private void ApplyFlycastUpdateState(FlycastUpdateState state)
        {
            CurrentSectionFlycastCurrentVersion = state.CurrentVersion;
            CurrentSectionFlycastLatestVersion = state.LatestVersion;
            IsCurrentSectionFlycastUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionFlycastStatus = state.StatusMessage;
            CurrentSectionFlycastEmulatorPath = state.EmulatorDirectory;
            CurrentSectionFlycastUpdatePath = state.UpdateDirectory;

            CurrentSectionFlycastAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionFlycastAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionFlycastVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionFlycastAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionFlycastAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionFlycastVersionSelection = true;
                SelectedCurrentSectionFlycastVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionFlycastVersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

        private void ApplyDuckStationUpdateState(DuckStationUpdateState state)
        {
            CurrentSectionDuckStationCurrentVersion = state.CurrentVersion;
            CurrentSectionDuckStationLatestVersion = state.LatestVersion;
            IsCurrentSectionDuckStationUpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionDuckStationStatus = state.StatusMessage;
            CurrentSectionDuckStationEmulatorPath = state.EmulatorDirectory;
            CurrentSectionDuckStationUpdatePath = state.UpdateDirectory;

            CurrentSectionDuckStationAvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionDuckStationAvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionDuckStationVersion;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionDuckStationAvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionDuckStationAvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionDuckStationVersionSelection = true;
                SelectedCurrentSectionDuckStationVersion = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionDuckStationVersionSelection = false;
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
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
            _xeniaPatchOverlayGameTitle = target.Title;
            OnPropertyChanged(nameof(XeniaPatchOverlayHeader));
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
            var saved = await SaveCurrentSectionXeniaPatchesCore().ConfigureAwait(false);
            if (saved)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsXeniaPatchesOverlayOpen = false;
                    IsXeniaPatchSwitchPromptVisible = false;
                    _pendingCurrentSectionXeniaPatchFile = null;
                });
            }
        }

        [RelayCommand]
        private void SelectAllCurrentSectionXeniaPatches()
        {
            if (IsXeniaPatchesBusy)
                return;

            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.IsEnabled = true;

            if (CurrentSectionXeniaPatchEntries.Count > 0)
                XeniaPatchesStatus = $"Enabled {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
        }

        [RelayCommand]
        private void UnselectAllCurrentSectionXeniaPatches()
        {
            if (IsXeniaPatchesBusy)
                return;

            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.IsEnabled = false;

            if (CurrentSectionXeniaPatchEntries.Count > 0)
                XeniaPatchesStatus = $"Disabled {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
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

        private void SyncSelectedXeniaPatchFileItemFromPath()
        {
            _selectedCurrentSectionXeniaPatchFileItem = string.IsNullOrWhiteSpace(_selectedCurrentSectionXeniaPatchFile)
                ? null
                : CurrentSectionXeniaPatchFiles.FirstOrDefault(file =>
                    string.Equals(file.FilePath, _selectedCurrentSectionXeniaPatchFile, StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(SelectedCurrentSectionXeniaPatchFileItem));
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

        private void OnShadPs4PatchEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(ShadPs4PatchEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionShadPs4PatchDirty = true;
        }

        private void DetachShadPs4PatchEntryListeners()
        {
            foreach (var entry in CurrentSectionShadPs4PatchEntries)
                entry.PropertyChanged -= OnShadPs4PatchEntryPropertyChanged;
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


        // --- shadPS4 Patches ---

        [RelayCommand]
        private async Task OpenCurrentSectionShadPs4Patches(object? parameter)
        {
            if (!ShowCurrentSectionShadPs4PatchesMenuItem)
                return;

            var target = ResolveShadPs4ContextMenuTarget(parameter);
            if (target == null)
                return;

            IsShadPs4PatchesOverlayOpen = true;
            IsShadPs4PatchesBusy = true;
            ShadPs4PatchesStatus = "Detecting PS4 Title ID and loading patches...";
            ShadPs4DetectedTitleId = null;
            ShadPs4PatchGameTitle = null;
            CurrentSectionShadPs4PatchFiles.Clear();
            DetachShadPs4PatchEntryListeners();
            CurrentSectionShadPs4PatchEntries.Clear();
            IsCurrentSectionShadPs4PatchDirty = false;
            _selectedCurrentSectionShadPs4PatchFile = null;
            _selectedCurrentSectionShadPs4PatchFileItem = null;
            IsShadPs4PatchSwitchPromptVisible = false;
            _pendingCurrentSectionShadPs4PatchFile = null;

            try
            {
                var shadPs4Directory = CurrentSectionShadPs4EmulatorPath;
                var titleId = ShadPs4TitleIdResolver.Resolve(target.FileName);

                var gameTitle = target.Title;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShadPs4DetectedTitleId = titleId;
                    ShadPs4PatchGameTitle = gameTitle;
                });

                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ShadPs4PatchesStatus = "Unable to detect PS4 Title ID for the selected game.";
                    });
                    return;
                }

                var patchFile = await Task.Run(() => ShadPs4PatchesService.FindPatchFile(shadPs4Directory, titleId)).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (patchFile != null)
                        CurrentSectionShadPs4PatchFiles.Add(patchFile);

                    ShadPs4PatchesStatus = patchFile == null
                        ? $"No patch file found for title ID {titleId}."
                        : $"Loaded patch file for title ID {titleId}.";

                    if (patchFile != null)
                        SelectedCurrentSectionShadPs4PatchFile = patchFile.FilePath;
                });
            }
            finally
            {
                IsShadPs4PatchesBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveCurrentSectionShadPs4Patches()
        {
            var saved = await SaveCurrentSectionShadPs4PatchesCore().ConfigureAwait(false);
            if (saved)
                CloseCurrentSectionShadPs4Patches();
        }

        private async Task<bool> SaveCurrentSectionShadPs4PatchesCore()
        {
            if (!IsShadPs4PatchesOverlayOpen)
                return false;

            var activePath = _activeShadPs4PatchDocumentPath;
            var activeText = _activeShadPs4PatchDocumentText;
            if (string.IsNullOrWhiteSpace(activePath) || string.IsNullOrWhiteSpace(activeText))
            {
                ShadPs4PatchesStatus = "No patch file loaded to save.";
                return false;
            }

            IsShadPs4PatchesBusy = true;
            try
            {
                var updated = BuildUpdatedShadPs4PatchDocument(activeText, CurrentSectionShadPs4PatchEntries);
                await Task.Run(() => File.WriteAllText(activePath, updated)).ConfigureAwait(false);
                _activeShadPs4PatchDocumentText = updated;
                IsCurrentSectionShadPs4PatchDirty = false;
                ShadPs4PatchesStatus = "Patch settings saved.";
                return true;
            }
            catch (Exception ex)
            {
                ShadPs4PatchesStatus = $"Failed to save patches: {ex.Message}";
                return false;
            }
            finally
            {
                IsShadPs4PatchesBusy = false;
            }
        }

        private void ApplyPendingCurrentSectionShadPs4PatchFileSelection(string patchFilePath)
        {
            _pendingCurrentSectionShadPs4PatchFile = null;
            IsShadPs4PatchSwitchPromptVisible = false;

            try
            {
                _isSwitchingCurrentSectionShadPs4PatchFile = true;
                SelectedCurrentSectionShadPs4PatchFile = patchFilePath;
            }
            finally
            {
                _isSwitchingCurrentSectionShadPs4PatchFile = false;
            }
        }

        private void SyncSelectedShadPs4PatchFileItemFromPath()
        {
            _selectedCurrentSectionShadPs4PatchFileItem = string.IsNullOrWhiteSpace(_selectedCurrentSectionShadPs4PatchFile)
                ? null
                : CurrentSectionShadPs4PatchFiles.FirstOrDefault(item =>
                    string.Equals(item.FilePath, _selectedCurrentSectionShadPs4PatchFile, StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFileItem));
        }

        [RelayCommand]
        private void SelectAllCurrentSectionShadPs4Patches()
        {
            if (IsShadPs4PatchesBusy)
                return;

            foreach (var entry in CurrentSectionShadPs4PatchEntries)
                entry.IsEnabled = true;

            if (CurrentSectionShadPs4PatchEntries.Count > 0)
                ShadPs4PatchesStatus = $"Selected {CurrentSectionShadPs4PatchEntries.Count} patch(s).";
        }

        [RelayCommand]
        private void UnselectAllCurrentSectionShadPs4Patches()
        {
            if (IsShadPs4PatchesBusy)
                return;

            foreach (var entry in CurrentSectionShadPs4PatchEntries)
                entry.IsEnabled = false;

            if (CurrentSectionShadPs4PatchEntries.Count > 0)
                ShadPs4PatchesStatus = $"Unselected {CurrentSectionShadPs4PatchEntries.Count} patch(s).";
        }

        [RelayCommand]
        private async Task SaveAndSwitchCurrentSectionShadPs4PatchFile()
        {
            var pending = _pendingCurrentSectionShadPs4PatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsShadPs4PatchSwitchPromptVisible = false;
                return;
            }

            var saved = await SaveCurrentSectionShadPs4PatchesCore().ConfigureAwait(false);
            if (!saved)
                return;

            ApplyPendingCurrentSectionShadPs4PatchFileSelection(pending);
        }

        [RelayCommand]
        private void SkipAndSwitchCurrentSectionShadPs4PatchFile()
        {
            var pending = _pendingCurrentSectionShadPs4PatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsShadPs4PatchSwitchPromptVisible = false;
                return;
            }

            ApplyPendingCurrentSectionShadPs4PatchFileSelection(pending);
        }

        [RelayCommand]
        private void CloseCurrentSectionShadPs4Patches()
        {
            IsShadPs4PatchesOverlayOpen = false;
            IsShadPs4PatchSwitchPromptVisible = false;
            _pendingCurrentSectionShadPs4PatchFile = null;
            _activeShadPs4PatchDocumentPath = null;
            _activeShadPs4PatchDocumentText = null;
        }

        [RelayCommand]
        private async Task DownloadCurrentSectionShadPs4Patches()
        {
            if (IsShadPs4PatchesBusy)
                return;

            var shadPs4Directory = CurrentSectionShadPs4EmulatorPath;
            if (string.IsNullOrWhiteSpace(shadPs4Directory))
            {
                ShadPs4PatchesStatus = "Emulator directory is not configured.";
                return;
            }

            IsShadPs4PatchesBusy = true;
            ShadPs4PatchesStatus = $"Downloading patches from {SelectedShadPs4PatchRepository.DisplayName}...";

            try
            {
                var result = await ShadPs4ContentDownloadService.DownloadPatchesAsync(
                    shadPs4Directory,
                    SelectedShadPs4PatchRepository).ConfigureAwait(true);

                ShadPs4PatchesStatus = result.Message;

                if (!result.Success)
                    return;

                if (!string.IsNullOrWhiteSpace(ShadPs4DetectedTitleId))
                    await ReloadCurrentSectionShadPs4PatchesAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShadPs4PatchesStatus = $"Failed to download patches: {ex.Message}";
            }
            finally
            {
                IsShadPs4PatchesBusy = false;
            }
        }

        private async Task ReloadCurrentSectionShadPs4PatchesAsync()
        {
            var shadPs4Directory = CurrentSectionShadPs4EmulatorPath;
            var titleId = ShadPs4DetectedTitleId;
            if (string.IsNullOrWhiteSpace(shadPs4Directory) || string.IsNullOrWhiteSpace(titleId))
                return;

            var patchFile = await Task.Run(() => ShadPs4PatchesService.FindPatchFile(shadPs4Directory, titleId)).ConfigureAwait(true);
            CurrentSectionShadPs4PatchFiles.Clear();
            if (patchFile != null)
            {
                CurrentSectionShadPs4PatchFiles.Add(patchFile);
                SelectedCurrentSectionShadPs4PatchFile = patchFile.FilePath;
            }
            else
            {
                DetachShadPs4PatchEntryListeners();
                CurrentSectionShadPs4PatchEntries.Clear();
                _selectedCurrentSectionShadPs4PatchFile = null;
                _selectedCurrentSectionShadPs4PatchFileItem = null;
                OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFile));
                OnPropertyChanged(nameof(SelectedCurrentSectionShadPs4PatchFileItem));
                ShadPs4PatchesStatus = "No patch file found for this title ID after download.";
            }
        }

        [RelayCommand]
        private async Task OpenCurrentSectionShadPs4CustomConfig(object? parameter)
        {
            if (!ShowCurrentSectionShadPs4CustomConfigMenuItem)
                return;

            var target = ResolveShadPs4ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            await ShadPs4CustomConfigEditor.LoadAsync(
                CurrentSectionShadPs4EmulatorPath,
                target.FileName,
                target.Title).ConfigureAwait(true);
        }

        [RelayCommand]
        private async Task OpenCurrentSectionShadPs4Cheats(object? parameter)
        {
            if (!ShowCurrentSectionShadPs4CheatsMenuItem)
                return;

            var target = ResolveShadPs4ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            UpdateShadPs4CheatsIpcState();
            await ShadPs4CheatsEditor.LoadAsync(
                CurrentSectionShadPs4EmulatorPath,
                target.FileName,
                target.Title).ConfigureAwait(true);
        }

        [RelayCommand]
        private async Task OpenCurrentSectionXeniaCustomConfig(object? parameter)
        {
            if (!ShowCurrentSectionXeniaCustomConfigMenuItem)
                return;

            var target = ResolveXeniaContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            await XeniaCustomConfigEditor.LoadAsync(
                CurrentSectionXeniaEmulatorPath,
                target.FileName,
                target.Title).ConfigureAwait(true);
        }

        private MediaItem? ResolveXeniaContextMenuTarget(object? parameter) =>
            ResolveShadPs4ContextMenuTarget(parameter);

        private MediaItem? ResolveShadPs4ContextMenuTarget(object? parameter)
        {
            return parameter switch
            {
                MediaItem mediaItem when CoverItems.Contains(mediaItem) => mediaItem,
                MediaItem mediaItem => mediaItem,
                double selected when !double.IsNaN(selected) => GetCarouselItemByIndex(GetRoundedSelectedIndex(selected)),
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => GetCurrentCarouselSelectedItem() ?? HighlightedItem
            };
        }

        private void LoadSelectedShadPs4PatchEntries(string? patchFilePath)
        {
            DetachShadPs4PatchEntryListeners();
            CurrentSectionShadPs4PatchEntries.Clear();
            _activeShadPs4PatchDocumentPath = null;
            _activeShadPs4PatchDocumentText = null;
            IsCurrentSectionShadPs4PatchDirty = false;

            if (string.IsNullOrWhiteSpace(patchFilePath) || !File.Exists(patchFilePath))
                return;

            try
            {
                var text = File.ReadAllText(patchFilePath);
                var entries = ParseShadPs4PatchEntries(text);

                _activeShadPs4PatchDocumentPath = patchFilePath;
                _activeShadPs4PatchDocumentText = text;
                foreach (var entry in entries)
                {
                    CurrentSectionShadPs4PatchEntries.Add(entry);
                    entry.PropertyChanged += OnShadPs4PatchEntryPropertyChanged;
                }

                ShadPs4PatchesStatus = CurrentSectionShadPs4PatchEntries.Count == 0
                    ? "Selected file has no patch elements."
                    : $"Loaded {CurrentSectionShadPs4PatchEntries.Count} patch(es).";
            }
            catch (Exception ex)
            {
                ShadPs4PatchesStatus = $"Failed to load patch file: {ex.Message}";
            }
        }

        private static IReadOnlyList<ShadPs4PatchEntry> ParseShadPs4PatchEntries(string document)
        {
            var entries = new List<ShadPs4PatchEntry>();
            if (string.IsNullOrWhiteSpace(document))
                return entries;

            try
            {
                var doc = XDocument.Parse(document);
                var metadataElements = doc.Descendants("Metadata").ToList();

                foreach (var metadataElement in metadataElements)
                {
                    var name = metadataElement.Attribute("Name")?.Value?.Trim() ?? "Unnamed patch";
                    var note = metadataElement.Attribute("Note")?.Value?.Trim() ?? string.Empty;
                    var appVer = metadataElement.Attribute("AppVer")?.Value?.Trim() ?? string.Empty;
                    var isEnabled = bool.TryParse(metadataElement.Attribute("isEnabled")?.Value, out var parsed) && parsed;

                    entries.Add(new ShadPs4PatchEntry(isEnabled, name, note, appVer));
                }
            }
            catch
            {
            }

            return entries;
        }

        private static string BuildUpdatedShadPs4PatchDocument(string original, IEnumerable<ShadPs4PatchEntry> entries)
        {
            try
            {
                var doc = XDocument.Parse(original);
                var metadataElements = doc.Descendants("Metadata").ToList();
                var entryList = entries.ToList();

                for (var i = 0; i < metadataElements.Count && i < entryList.Count; i++)
                {
                    var metadata = metadataElements[i];
                    var entry = entryList[i];
                    var enabledAttr = metadata.Attribute("isEnabled");

                    if (entry.IsEnabled)
                    {
                        if (enabledAttr == null)
                            metadata.SetAttributeValue("isEnabled", "true");
                        else if (!string.Equals(enabledAttr.Value, "true", StringComparison.OrdinalIgnoreCase))
                            metadata.SetAttributeValue("isEnabled", "true");
                    }
                    else
                    {
                        enabledAttr?.Remove();
                    }
                }

                return doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.None);
            }
            catch
            {
                return original;
            }
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

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
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
            else if (ShowCurrentSectionRpcs3UpdateControls)
                await RefreshCurrentSectionRpcs3Info();
            else if (ShowCurrentSectionDolphinUpdateControls)
                await RefreshCurrentSectionDolphinInfo();
            else if (ShowCurrentSectionPcsx2UpdateControls)
                await RefreshCurrentSectionPcsx2Info();
        }

        [RelayCommand]
        private void LaunchCurrentSectionHandlerSetup()
        {
            if (!CanLaunchCurrentSectionHandlerSetup)
                return;

            var handlerId = CurrentEmulatorHandler?.HandlerId;
            if (string.Equals(handlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionDuckStationSetup();
                return;
            }

            if (string.Equals(handlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionPcsx2Setup();
                return;
            }

            if (string.Equals(handlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                LaunchCurrentSectionDolphinSetup();
                return;
            }

            LaunchCurrentSectionGenericHandlerSetup();
        }

        [RelayCommand]
        private void LaunchCurrentSectionDolphinSetup()
        {
            var handler = CurrentEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                var executableDirectory = Path.GetDirectoryName(startInfo.FileName);
                var dolphinUserDirectory = string.IsNullOrWhiteSpace(executableDirectory)
                    ? startInfo.WorkingDirectory
                    : Path.Combine(executableDirectory, "User");

                if (!string.IsNullOrWhiteSpace(dolphinUserDirectory))
                {
                    startInfo.ArgumentList.Add("-u");
                    startInfo.ArgumentList.Add(dolphinUserDirectory);
                }

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch Dolphin.", ex);
            }
        }

        private Bitmap? ResolveCurrentSectionSetupLaunchIcon()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var executablePath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(CurrentEmulatorHandler?.LauncherPath);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return null;

            if (string.Equals(_currentSetupLaunchIconExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase) &&
                _currentSetupLaunchIcon != null)
            {
                return _currentSetupLaunchIcon;
            }

            _currentSetupLaunchIcon?.Dispose();
            _currentSetupLaunchIcon = TryLoadExecutableIcon(executablePath);
            _currentSetupLaunchIconExecutablePath = executablePath;
            return _currentSetupLaunchIcon;
        }

        private static Bitmap? TryLoadExecutableIcon(string executablePath)
        {
#pragma warning disable CA1416 // Windows-only System.Drawing APIs
            try
            {
                using var icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
                if (icon == null)
                    return null;

                using var drawingBitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                drawingBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
#pragma warning restore CA1416
        }

        private static bool IsCurrentSectionSetupLaunchSupported()
            => true;

        [RelayCommand]
        private void LaunchCurrentSectionGenericHandlerSetup()
        {
            var handler = CurrentEmulatorHandler;
            if (handler == null)
                return;

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn($"Failed to launch {handler.DisplayName}.", ex);
            }
        }

        [RelayCommand]
        private void LaunchCurrentSectionPcsx2Setup()
        {
            var handler = CurrentEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                startInfo.ArgumentList.Add("-portable");

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch PCSX2.", ex);
            }
        }

        [RelayCommand]
        private void LaunchCurrentSectionDuckStationSetup()
        {
            var handler = CurrentEmulatorHandler;
            if (handler == null ||
                !string.Equals(handler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsEmulatorRunning || IsEmulatorLaunchInProgress)
                return;

            var launcherPath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(handler.LauncherPath);
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                return;

            try
            {
                RestoreAppTopMost();

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    UseShellExecute = false,
                    WorkingDirectory = EmulatorHandlerBase.ResolveLauncherWorkingDirectory(handler.LauncherPath)
                                       ?? Path.GetDirectoryName(launcherPath)
                                       ?? string.Empty
                };

                DuckStationHandler.EnsurePortableModeMarker(startInfo.FileName, startInfo.WorkingDirectory);

                _ = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                SLog.Warn("Failed to launch DuckStation.", ex);
            }
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

        [RelayCommand]
        private void DismissEmulatorUpdateNoticeOverlay()
        {
            _emulatorUpdateNoticeSuppressedAlbumTitle = LoadedAlbum?.Title;
            IsEmulatorUpdateNoticeOverlayOpen = false;
        }

        [RelayCommand]
        private async Task OpenEmulatorUpdateNoticeOverlay()
        {
            IsEmulatorUpdateNoticeOverlayOpen = false;
            await OpenCurrentSectionEdenUpdates();
        }

        private void SyncEmulatorUpdateNoticeOverlay()
        {
            if (LoadedAlbum == null || !IsCurrentSectionHandlerUpdateAvailable)
                return;

            if (string.Equals(_emulatorUpdateNoticeSuppressedAlbumTitle, LoadedAlbum.Title, StringComparison.OrdinalIgnoreCase))
                return;

            var (currentVersion, latestVersion) = GetCurrentSectionUpdateVersionInfo();
            var emulatorName = string.IsNullOrWhiteSpace(CurrentEmulatorHandler?.DisplayName)
                ? "emulator"
                : CurrentEmulatorHandler.DisplayName;

            EmulatorUpdateNoticeSummary = $"A new version of {emulatorName} is available.";

            var details = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(currentVersion))
                details.Append("Installed: ").AppendLine(currentVersion);
            if (!string.IsNullOrWhiteSpace(latestVersion))
                details.Append("Latest: ").AppendLine(latestVersion);

            EmulatorUpdateNoticeDetails = details.Length > 0 ? details.ToString().TrimEnd() : null;
            EmulatorUpdateNoticeChanges = _sectionLatestReleaseNotes;
            EmulatorUpdateNoticeFooter = "Open Render Options → Updates to download and install the update.";
            IsEmulatorUpdateNoticeOverlayOpen = true;
        }

        private (string? CurrentVersion, string? LatestVersion) GetCurrentSectionUpdateVersionInfo()
        {
            if (ShowCurrentSectionRetroArchUpdateControls && IsCurrentSectionRetroArchUpdateAvailable)
                return (CurrentSectionRetroArchCurrentVersion, CurrentSectionRetroArchLatestVersion);
            if (ShowCurrentSectionEdenUpdateControls && IsCurrentSectionEdenUpdateAvailable)
                return (CurrentSectionEdenCurrentVersion, CurrentSectionEdenLatestVersion);
            if (ShowCurrentSectionShadPs4UpdateControls && IsCurrentSectionShadPs4UpdateAvailable)
                return (CurrentSectionShadPs4CurrentVersion, CurrentSectionShadPs4LatestVersion);
            if (ShowCurrentSectionXeniaUpdateControls && IsCurrentSectionXeniaUpdateAvailable)
                return (CurrentSectionXeniaCurrentVersion, CurrentSectionXeniaLatestVersion);
            if (ShowCurrentSectionRpcs3UpdateControls && IsCurrentSectionRpcs3UpdateAvailable)
                return (CurrentSectionRpcs3CurrentVersion, CurrentSectionRpcs3LatestVersion);
            if (ShowCurrentSectionPcsx2UpdateControls && IsCurrentSectionPcsx2UpdateAvailable)
                return (CurrentSectionPcsx2CurrentVersion, CurrentSectionPcsx2LatestVersion);
            if (ShowCurrentSectionDolphinUpdateControls && IsCurrentSectionDolphinUpdateAvailable)
                return (CurrentSectionDolphinCurrentVersion, CurrentSectionDolphinLatestVersion);
            if (ShowCurrentSectionFlycastUpdateControls && IsCurrentSectionFlycastUpdateAvailable)
                return (CurrentSectionFlycastCurrentVersion, CurrentSectionFlycastLatestVersion);
            if (ShowCurrentSectionDuckStationUpdateControls && IsCurrentSectionDuckStationUpdateAvailable)
                return (CurrentSectionDuckStationCurrentVersion, CurrentSectionDuckStationLatestVersion);
            if (ShowCurrentSectionCemuSection && IsCurrentSectionCemuUpdateAvailable)
                return (CurrentSectionCemuCurrentVersion, CurrentSectionCemuLatestVersion);

            return (null, null);
        }

        private void ClearRetroArchErrorState()
        {
            RetroArchErrorSummary = null;
            RetroArchErrorDetails = null;
            IsRetroArchErrorOverlayOpen = false;
        }

        private void ShowEmulatorCaptureFailure(string romPath, IEmulatorHandler handler, string? details = null)
        {
            var handlerName = string.IsNullOrWhiteSpace(handler.DisplayName) ? "emulator" : handler.DisplayName;
            RetroArchErrorSummary = $"{handlerName} capture failed.";
            RetroArchErrorDetails = string.IsNullOrWhiteSpace(details)
                ? $"AES could not capture '{romPath}'. The emulator may still be running. Please retry, or reopen the emulator window and try again."
                : details;
            IsRetroArchErrorOverlayOpen = true;
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
            UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum);
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
                        QueueSelectedAlbumCoverScan(album);
                    }
                }

                SelectedAlbum = AlbumList.FirstOrDefault();
                LoadedAlbum = null;
                UpdateCurrentEmulatorHandlerForSelection(LoadedAlbum);
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

                    if (ReferenceEquals(LoadedAlbum, album))
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
            var launchStopwatch = Stopwatch.StartNew();
            try
            {
                ClearRetroArchErrorState();

                var handler = request.Handler;
                CurrentEmulatorHandler = handler;
                SetSessionCaptureStretchOverride(string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(handler.HandlerId, "cemu", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(handler.HandlerId, "duckstation", StringComparison.OrdinalIgnoreCase)
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

                if (string.Equals(handler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(handler.LauncherPath))
                {
                    var xeniaTitleId = XeniaTitleIdResolver.Resolve(request.RomPath);
                    var xeniaDirectory = Path.GetDirectoryName(handler.LauncherPath);
                    await Task.Run(() => XeniaCustomConfigService.PrepareConfigForLaunch(xeniaDirectory, xeniaTitleId))
                        .ConfigureAwait(false);
                }

                var rpcs3TitleId = string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase)
                    ? Ps3InstalledGameHelper.GetTitleId(request.RomPath)
                    : null;
                if (!string.IsNullOrWhiteSpace(rpcs3TitleId))
                {
                    SLog.Info($"EmulationViewModel resolved RPCS3 title id '{rpcs3TitleId}' for '{request.RomPath}'.");
                    var rpcs3Directory = !string.IsNullOrWhiteSpace(CurrentSectionRpcs3EmulatorPath)
                        ? CurrentSectionRpcs3EmulatorPath
                        : Rpcs3CustomConfigService.ResolveEmulatorDirectory(handler.LauncherPath);
                    await Task.Run(() => Rpcs3CustomConfigService.PrepareConfigForLaunch(rpcs3Directory, rpcs3TitleId))
                        .ConfigureAwait(false);
                    _activeRpcs3SessionTitleId = Rpcs3CustomConfigService.NormalizeTitleId(rpcs3TitleId);
                    _activeRpcs3SessionEmulatorDirectory = rpcs3Directory;
                }
                else
                {
                    _activeRpcs3SessionTitleId = null;
                    _activeRpcs3SessionEmulatorDirectory = null;
                }

                EnsureAppTopMostBeforeLaunch();

                var launchRomPath = request.RomPath;
                if (string.Equals(handler.HandlerId, "rpcs3", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(rpcs3TitleId))
                {
                    var preferredBootPath = Ps3InstalledGameHelper.GetPreferredBootPath(request.RomPath);
                    if (!string.IsNullOrWhiteSpace(preferredBootPath))
                    {
                        launchRomPath = preferredBootPath;
                        SLog.Info($"EmulationViewModel booting RPCS3 using EBOOT path '{launchRomPath}'.");
                    }
                    else
                    {
                        launchRomPath = Rpcs3Handler.BuildGameIdBootPath(rpcs3TitleId);
                        SLog.Info($"EmulationViewModel fallback booting RPCS3 by GAMEID using '{launchRomPath}'.");
                    }
                }

                if (handler is ShadPs4Handler shadPs4LaunchHandler)
                    shadPs4LaunchHandler.UseIpcForCheatsLaunch = true;

                var startInfo = handler.BuildStartInfo(
                    handler.LauncherPath ?? string.Empty,
                    launchRomPath,
                    request.LaunchSettings?.StartFullscreen == true,
                    request.AlbumTitle,
                    request.LaunchSettings?.SelectedRetroArchCore);

                PrepareLinuxAppImageStartInfo(startInfo);
                var process = Process.Start(startInfo);
                SLog.Info($"Emulation launch started for '{request.AlbumTitle}'/'{request.ItemTitle}' after {launchStopwatch.ElapsedMilliseconds} ms. pid={(process?.Id ?? 0)}.");

                if (process != null)
                {
                    SLog.Info($"Emulator process launched: pid={process.Id}, name={process.ProcessName}, hasExited={process.HasExited}.");
                }

                AttachShadPs4IpcSessionIfNeeded(handler, process);

                RestoreHostWindowFocus();

                Process? runtimeProcess = process;
                if (process != null)
                {
                    try
                    {
                        runtimeProcess = await handler.ResolveRuntimeProcessAsync(process, CancellationToken.None).ConfigureAwait(false) ?? process;
                        SLog.Info($"Emulator runtime process resolution completed in {launchStopwatch.ElapsedMilliseconds} ms for '{request.AlbumTitle}'/'{request.ItemTitle}'. runtimePid={runtimeProcess?.Id ?? 0}.");
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

                    if (handler.HideUntilCaptured &&
                        !handler.DeferWindowHidingUntilCaptured &&
                        runtimeProcess != null)
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

                    TrackEmulatorProcess(runtimeProcess, request.RomPath, handler, request.ItemTitle);
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
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "duckstation", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "dolphin", StringComparison.OrdinalIgnoreCase);
                    forceKillFirst |= string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);
                    if (!forceKillFirst)
                    {
                        try
                        {
                            forceKillFirst = process.ProcessName.Contains("pcsx2", StringComparison.OrdinalIgnoreCase) ||
                                             process.ProcessName.Contains("duckstation", StringComparison.OrdinalIgnoreCase) ||
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
                            SLog.Info($"EmulationViewModel using direct termination for pid={process.Id} to bypass confirm-shutdown dialogs.");
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

        private void TrackEmulatorProcess(Process? process, string romPath, IEmulatorHandler handler, string? gameTitle = null)
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

            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();
            _activeEmulatorWatchdogCts = null;

            _activeEmulatorProcess = process;
            _activeEmulatorRomPath = romPath;
            _activeEmulatorGameTitle = gameTitle;
            EmulatorTargetProcessId = process?.Id ?? 0;

            if (_shadPs4IpcSession == null)
                AttachShadPs4IpcSessionIfNeeded(handler, process);

            UpdateShadPs4CheatsIpcState();
            OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));

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
            {
                StartActiveEmulatorWatchdog(process);
                _ = ResolveEmulatorTargetHwndAsync(process, romPath, handler);
            }

            RestoreHostWindowFocus();
        }

        private void StartActiveEmulatorWatchdog(Process process)
        {
            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();

            var cts = new CancellationTokenSource();
            _activeEmulatorWatchdogCts = cts;
            _ = MonitorActiveEmulatorAsync(process, cts.Token);
        }

        private async Task MonitorActiveEmulatorAsync(Process process, CancellationToken cancellationToken)
        {
            const int pollDelayMs = 500;
            const int missingWindowThreshold = 6;
            var missingWindowCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!ReferenceEquals(_activeEmulatorProcess, process))
                    break;

                try
                {
                    process.Refresh();
                    if (process.HasExited)
                        break;
                }
                catch
                {
                    break;
                }

                if (!OperatingSystem.IsWindows())
                    continue;

                var targetHwnd = EmulatorTargetHwnd;
                if (targetHwnd == IntPtr.Zero)
                {
                    missingWindowCount = 0;
                    continue;
                }

                if (NativeIsWindow(targetHwnd))
                {
                    missingWindowCount = 0;
                    continue;
                }

                missingWindowCount++;
                if (missingWindowCount < missingWindowThreshold)
                    continue;

                SLog.Warn($"EmulationViewModel detected that emulator target hwnd 0x{targetHwnd.ToInt64():X} is no longer valid while process pid={process.Id} is still tracked. Triggering close flow.");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_activeEmulatorProcess, process))
                        return;

                    RequestStopEmulatorCapture = true;
                    CloseTrackedEmulatorForPendingLaunch(process);
                }, DispatcherPriority.Background);

                break;
            }
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

            if (string.Equals(currentHandler?.HandlerId, Rpcs3Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_activeRpcs3SessionTitleId) &&
                !string.IsNullOrWhiteSpace(_activeRpcs3SessionEmulatorDirectory))
            {
                var rpcs3TitleId = _activeRpcs3SessionTitleId;
                var rpcs3Directory = _activeRpcs3SessionEmulatorDirectory;
                _ = Task.Run(() => Rpcs3CustomConfigService.ImportFromRpcs3AfterSession(rpcs3Directory, rpcs3TitleId));
            }

            _activeRpcs3SessionTitleId = null;
            _activeRpcs3SessionEmulatorDirectory = null;

            DetachTrackedEmulatorProcess();
            IsEmulatorRunning = false;
            RequestStopEmulatorCapture = true;
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
            _activeEmulatorWatchdogCts?.Cancel();
            _activeEmulatorWatchdogCts?.Dispose();
            _activeEmulatorWatchdogCts = null;

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
                _activeEmulatorRomPath = null;
                _activeEmulatorGameTitle = null;
                EmulatorTargetHwnd = IntPtr.Zero;
                EmulatorTargetProcessId = 0;
                DetachShadPs4IpcSession();
                OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));
            }
        }

        private void AttachShadPs4IpcSessionIfNeeded(IEmulatorHandler handler, Process? process)
        {
            if (handler is not ShadPs4Handler shadPs4Handler || !shadPs4Handler.UseIpcForCheatsLaunch || process == null)
                return;

            try
            {
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            if (_shadPs4IpcSession != null && _shadPs4IpcSession.ProcessId == process.Id)
                return;

            DetachShadPs4IpcSession();
            _shadPs4IpcSession = ShadPs4IpcSession.TryAttach(process, shadPs4Handler.CurrentLaunchTranscriptPath);
            if (_shadPs4IpcSession != null)
                _shadPs4IpcSession.CapabilitiesChanged += OnShadPs4IpcCapabilitiesChanged;
        }

        private void DetachShadPs4IpcSession()
        {
            if (_shadPs4IpcSession != null)
                _shadPs4IpcSession.CapabilitiesChanged -= OnShadPs4IpcCapabilitiesChanged;

            _shadPs4IpcSession?.Dispose();
            _shadPs4IpcSession = null;
            UpdateShadPs4CheatsIpcState();
        }

        private void OnShadPs4IpcCapabilitiesChanged()
        {
            Dispatcher.UIThread.Post(UpdateShadPs4CheatsIpcState, DispatcherPriority.Background);
        }

        private void UpdateShadPs4CheatsIpcState()
        {
            var isShadPs4Running = IsEmulatorRunning &&
                                   string.Equals(CurrentEmulatorHandler?.HandlerId, "shadps4-qtlauncher", StringComparison.OrdinalIgnoreCase);
            ShadPs4CheatsEditor.SetIpcSession(_shadPs4IpcSession, isShadPs4Running);

            if (isShadPs4Running && !string.IsNullOrWhiteSpace(_activeEmulatorRomPath))
            {
                _ = ShadPs4CheatsEditor.EnsureLoadedForGameAsync(
                    CurrentSectionShadPs4EmulatorPath,
                    _activeEmulatorRomPath,
                    _activeEmulatorGameTitle);
            }
        }

        [RelayCommand]
        private async Task ToggleShadPs4CheatsOverlay()
        {
            if (!ShowShadPs4InGameCheatsButton)
                return;

            if (ShadPs4CheatsEditor.IsOpen)
            {
                ShadPs4CheatsEditor.IsOpen = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeEmulatorRomPath))
                return;

            UpdateShadPs4CheatsIpcState();
            await ShadPs4CheatsEditor.LoadAsync(
                CurrentSectionShadPs4EmulatorPath,
                _activeEmulatorRomPath,
                _activeEmulatorGameTitle).ConfigureAwait(true);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool NativeIsWindow(IntPtr hWnd);

        private async Task ResolveEmulatorTargetHwndAsync(Process process, string romPath, IEmulatorHandler handler)
        {
            var captureStopwatch = Stopwatch.StartNew();
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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsEmulatorLaunchInProgress = false;
                        ShowEmulatorCaptureFailure(romPath, handler);
                    });
                    return;
                }

                UseHostWindowCapture = false;
                SLog.Info($"Emulation capture target resolved for '{romPath}' in {captureStopwatch.ElapsedMilliseconds} ms. hwnd=0x{hwnd.ToInt64():X}.");
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsEmulatorLaunchInProgress = false;
                    ShowEmulatorCaptureFailure(romPath, handler, ex.Message);
                });
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

        private async Task<bool> TryApplyEmulatorTargetHwndAsync(Process process, IntPtr hwnd, bool showWindowForCapture)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var handoffStopwatch = Stopwatch.StartNew();
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
                SLog.Info($"Emulation capture handoff completed in {handoffStopwatch.ElapsedMilliseconds} ms for pid={process.Id}. hwnd=0x{hwnd.ToInt64():X}, showWindowForCapture={showWindowForCapture}.");

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
