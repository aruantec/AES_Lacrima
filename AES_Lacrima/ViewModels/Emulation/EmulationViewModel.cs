using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation;
using AES_Emulation.Controls;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;
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
        private readonly Dictionary<FolderMediaItem, CancellationTokenSource> _albumTilePreviewCtsMap = [];
        private readonly HashSet<FolderMediaItem> _activeAlbumPreviewCoverLoads = [];
        private readonly object _albumPreviewCoverLoadGate = new();

        private const int FolderPreviewCoverCount = 3;
        private const int RomRestoreUiBatchSize = 200;
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
        private bool _isSyncingCurrentSectionXemuVersionSelection;
        private bool _isSyncingCurrentSectionXemuIncludePrereleases;
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
        private bool _isRpcs3PatchesOverlayOpen;
        private bool _isRpcs3PatchesBusy;
        private bool _isCurrentSectionRpcs3PatchDirty;
        private Rpcs3PatchCatalog _rpcs3ActivePatchCatalog = Rpcs3PatchCatalog.Official;
        private string _rpcs3PatchesStatus = "Select a PlayStation 3 game to manage patches.";
        private string? _rpcs3PatchGameTitle;
        private string? _rpcs3DetectedTitleId;
        private string? _rpcs3DetectedAppVersion;
        private string? _rpcs3SelectedGamePath;
        private bool _isRpcs3ArtemisImportOverlayOpen;
        private string _rpcs3ArtemisImportText = string.Empty;
        private string _rpcs3ArtemisImportStatus = string.Empty;
        private readonly AvaloniaList<Rpcs3PatchEntry> _currentSectionRpcs3PatchEntries = [];
        private bool _isCemuGraphicPacksOverlayOpen;
        private bool _isCemuGraphicPacksBusy;
        private bool _isCurrentSectionCemuGraphicPackDirty;
        private string _cemuGraphicPacksStatus = "Select a Wii U game to manage graphic packs.";
        private string? _cemuGraphicPackGameTitle;
        private string? _cemuDetectedTitleId;
        private readonly AvaloniaList<CemuGraphicPackEntry> _currentSectionCemuGraphicPackEntries = [];

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
        private int _lastRoundedSelectedIndexForPreview = -1;
        private DispatcherTimer? _carouselHighlightDebounceTimer;
        private int _pendingHighlightedCarouselIndex = -1;
        private string? _pendingGameplayPreviewItemPath;
        private string? _activeGameplayPreviewItemPath;
        private long _gameplayPreviewRequestVersion;
        private Process? _activeEmulatorProcess;
        private AES_Emulation.Linux.LinuxCompositorSession? _linuxCompositorSession;
        private int _linuxCompositorPid;
        private string? _activeEmulatorRomPath;
        private string? _activeEmulatorGameTitle;
        private ShadPs4IpcSession? _shadPs4IpcSession;
        private readonly EmulatorAudioVolumeController _emulatorAudioVolume = new();
        private LinuxEmulatorAudioVolumeController? _linuxEmulatorAudioVolume;
        private bool _syncingEmulatorVolumeFromSystem;
        private CancellationTokenSource? _retroArchLogWatcherCts;
        private CancellationTokenSource? _activeEmulatorWatchdogCts;
        private CancellationTokenSource? _appTopmostRestoreCts;
        private string? _currentSetupLaunchIconExecutablePath;
        private Bitmap? _currentSetupLaunchIcon;
        private PendingEmulatorLaunchRequest? _pendingEmulatorLaunchRequest;
        private DateTime _emulatorLaunchStartedUtc;
        private MediaItem? _activeEmulationSessionItem;
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
        private XemuEmulatorUpdateService? _xemuEmulatorUpdateService;

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
        [NotifyPropertyChangedFor(nameof(IsEmulationFolderAnimationPaused))]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewPublishBoundsActive))]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewViewportVisible))]
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
        [NotifyPropertyChangedFor(nameof(CurrentCaptureStretch))]
        private Stretch _selectedStretch = Stretch.Fill;

        [ObservableProperty]
        private bool _useHostWindowCapture;

        [ObservableProperty]
        private bool _disableVSync;

        [ObservableProperty]
        private bool _lowLatencyCapture = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FrameGenerationModeIndex))]
        private EmulationFrameGenerationMode _frameGenerationMode;

        [ObservableProperty]
        private int _emulatorCaptureDelayMs = 3000;

        [ObservableProperty]
        private double _renderBrightness = 1.0;

        [ObservableProperty]
        private double _portalCaptureBrightness = 1.0;

        [ObservableProperty]
        private double _renderSaturation = 1.0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenMetadataCommand))]
        [NotifyPropertyChangedFor(nameof(HasActiveAlbumItems))]
        private AvaloniaList<MediaItem> _coverItems = [];

        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _albumList = [];

        [ObservableProperty]
        private bool _isAlbumListLoading;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddRomsCommand))]
        [NotifyCanExecuteChangedFor(nameof(ScanFolderCommand))]
        [NotifyCanExecuteChangedFor(nameof(ClearAlbumCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenMetadataCommand))]
        [NotifyPropertyChangedFor(nameof(HasActiveAlbumItems))]
        [NotifyPropertyChangedFor(nameof(CanShowRenderOptions))]
        private FolderMediaItem? _selectedAlbum;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanShowRenderOptions))]
        [NotifyCanExecuteChangedFor(nameof(OpenMetadataCommand))]
        [NotifyPropertyChangedFor(nameof(HasActiveAlbumItems))]
        private FolderMediaItem? _loadedAlbum;

        [ObservableProperty]
        private int _selectedAlbumIndex = -1;

        [ObservableProperty]
        private double _selectedIndex = -1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsItemPointed))]
        private int _pointedIndex = -1;

        public bool IsItemPointed => PointedIndex != -1 && PointedIndex < CoverItems.Count;
        public bool HasActiveAlbumItems =>
            CoverItems.Count > 0 ||
            LoadedAlbum?.Children.Count > 0 ||
            !string.IsNullOrWhiteSpace(HighlightedItem?.FileName);
        public bool ShowEmptyActiveAlbumHint => LoadedAlbum != null && !HasActiveAlbumItems;
        public string EmptyLoadedAlbumMessage =>
            LoadedAlbum != null
                ? "Right-click to add ROMs or scan folder"
                : "No album loaded";

        private FolderMediaItem? GetBrowseAlbum() => LoadedAlbum;

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
            CurrentEmulationSectionItem != null &&
            (!HasActiveAlbumItems ||
             SelectedCaptureMode == EmulatorCaptureMode.DirectComposition ||
             CurrentEmulatorHandler?.IsWindowEmbeddingSupported != true);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleEmulatorPauseCommand))]
        [NotifyPropertyChangedFor(nameof(CanShowGameplayRecording))]
        [NotifyPropertyChangedFor(nameof(CanToggleGameplayRecording))]
        private bool _isEmulatorRunning;

        [ObservableProperty]
        private bool _isEmulatorPaused;

        [ObservableProperty]
        private bool _isEmulatorLaunchInProgress;

        [ObservableProperty]
        private IntPtr _emulatorTargetHwnd;

        [ObservableProperty]
        private int _emulatorTargetProcessId;

        [ObservableProperty]
        private double _emulatorVolume = 100.0;

        partial void OnEmulatorVolumeChanged(double value)
        {
            if (_syncingEmulatorVolumeFromSystem)
                return;

            ApplyEmulatorVolumeToProcess(value);
            AutoSave();
        }

        internal void ApplyEmulatorVolumeAfterCaptureActive()
        {
            if (OperatingSystem.IsLinux() && _linuxCompositorPid > 0)
            {
                try
                {
                    AttachLinuxEmulatorAudioVolume(_linuxCompositorPid, _activeEmulatorProcess, CurrentEmulatorHandler);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to refresh Linux emulator volume attachment.", ex);
                }
            }

            ApplyEmulatorVolumeToProcess(EmulatorVolume);
        }

        internal void CompleteEmulatorLaunchAfterCaptureFrames()
        {
            if (!IsEmulatorLaunchInProgress)
                return;

            var handler = CurrentEmulatorHandler;
            if (!OperatingSystem.IsLinux() || handler?.HideUntilCaptured != true)
            {
                IsEmulatorLaunchInProgress = false;
                return;
            }

            // Keep the black launch gate briefly so the first PipeWire frames (often
            // Xenia's empty window) stay hidden, then dismiss as soon as capture is live.
            var minimumGateMs = Math.Max(900, handler.CaptureStartupDelayMs);
            const int frameSettleMs = 200;

            var elapsedMs = (DateTime.UtcNow - _emulatorLaunchStartedUtc).TotalMilliseconds;
            var remainingMs = (int)Math.Max(0, minimumGateMs - elapsedMs) + frameSettleMs;
            if (remainingMs <= 0)
            {
                IsEmulatorLaunchInProgress = false;
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(remainingMs).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (IsEmulatorLaunchInProgress)
                            IsEmulatorLaunchInProgress = false;
                    }, DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to defer emulator launch overlay dismissal.", ex);
                }
            });
        }

        private void ApplyEmulatorVolumeToProcess(double volumePercent)
        {
            if (EmulatorTargetProcessId <= 0 && _activeEmulatorProcess == null)
                return;

            float normalized = (float)Math.Clamp(volumePercent / 100.0, 0.0, 1.0);

            if (OperatingSystem.IsWindows())
            {
                _emulatorAudioVolume.EnsureSession();
                _emulatorAudioVolume.Volume = normalized;
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                var controller = GetOrCreateLinuxEmulatorAudioVolume();
                if (!controller.IsAttached && _linuxCompositorPid > 0)
                    AttachLinuxEmulatorAudioVolume(_linuxCompositorPid, _activeEmulatorProcess, CurrentEmulatorHandler);

                controller.Volume = normalized;
            }
        }

        private void AttachLinuxEmulatorAudioVolume(int compositorPid, Process? process = null, IEmulatorHandler? handler = null)
        {
            var compositorRoot = LinuxCompositorProcessHelper.ResolveCompositorRootPid(compositorPid);
            if (compositorRoot <= 0)
                compositorRoot = compositorPid;

            _linuxCompositorPid = compositorRoot;

            var seeds = new HashSet<int> { compositorPid, compositorRoot };
            var primaryEmulatorPid = LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(compositorRoot);
            if (primaryEmulatorPid > 0)
                seeds.Add(primaryEmulatorPid);

            if (process != null)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                        seeds.Add(process.Id);
                }
                catch (Exception ex)
                {
                    SLog.Debug("Failed to resolve emulator pid for Linux volume attach.", ex);
                }
            }

            var audioNameHints = BuildLinuxEmulatorAudioNameHints(handler, process);
            GetOrCreateLinuxEmulatorAudioVolume().Attach(compositorPid, seeds, audioNameHints);
        }

        private static IEnumerable<string> BuildLinuxEmulatorAudioNameHints(IEmulatorHandler? handler, Process? process)
        {
            var hints = new List<string>();
            if (handler != null)
            {
                hints.Add(handler.DisplayName);
                hints.Add(handler.HandlerId);

                if (!string.IsNullOrWhiteSpace(handler.LauncherPath))
                {
                    hints.Add(Path.GetFileNameWithoutExtension(handler.LauncherPath));
                    hints.Add(Path.GetFileName(handler.LauncherPath));
                }
            }

            if (process != null)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                        hints.Add(process.ProcessName);
                }
                catch
                {
                    // ignored
                }
            }

            return hints;
        }

#pragma warning disable CA1416 // Linux-only audio volume controller
        private LinuxEmulatorAudioVolumeController GetOrCreateLinuxEmulatorAudioVolume()
        {
            if (_linuxEmulatorAudioVolume != null)
                return _linuxEmulatorAudioVolume;

            var controller = new LinuxEmulatorAudioVolumeController();
            controller.SystemVolumeChanged += OnLinuxEmulatorSystemVolumeChanged;
            _linuxEmulatorAudioVolume = controller;
            return controller;
        }

        private void OnLinuxEmulatorSystemVolumeChanged(float normalizedVolume)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsEmulatorRunning)
                    return;

                var percent = Math.Round(normalizedVolume * 100.0, 1);
                if (Math.Abs(EmulatorVolume - percent) < 0.5)
                    return;

                _syncingEmulatorVolumeFromSystem = true;
                try
                {
                    EmulatorVolume = percent;
                }
                finally
                {
                    _syncingEmulatorVolumeFromSystem = false;
                }
            });
        }
#pragma warning restore CA1416

        [ObservableProperty]
        private bool _requestStopEmulatorCapture;

        [ObservableProperty]
        private bool _restoreTargetWindowOnStop = true;

        [ObservableProperty]
        private bool _isRenderOptionsOpen;

        public bool UseInTreeRenderOptionsDismiss => true;

        [ObservableProperty]
        private bool _isFullscreen;

        [ObservableProperty]
        private bool _isRetroArchErrorOverlayOpen;

        [ObservableProperty]
        private string _emulatorErrorOverlayTitle = "Emulator launch warning";

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

            OnPropertyChanged(nameof(CaptureWindowAspectRatio));
            OnPropertyChanged(nameof(CanShowRenderOptions));
            OnPropertyChanged(nameof(ForceUseTargetClientAreaCapture));
            OnPropertyChanged(nameof(EnableCapturePillarboxCrop));
            OnPropertyChanged(nameof(HideTargetWindowAfterCaptureStarts));
            OnPropertyChanged(nameof(ClientAreaCropLeftInset));
            OnPropertyChanged(nameof(ClientAreaCropTopInset));
            OnPropertyChanged(nameof(ClientAreaCropRightInset));
            OnPropertyChanged(nameof(ClientAreaCropBottomInset));
            OnPropertyChanged(nameof(CaptureWindowAspectRatio));
            OnPropertyChanged(nameof(CurrentEmulatorWindowTitleHint));
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
            RefreshCurrentSectionLaunchOptionsState();
            SyncCurrentSectionRetroArchCoreSelection();
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
        {
            OnPropertyChanged(nameof(CanShowRenderOptions));
            NotifyCaptureChromeMarginChanged();
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmulatorViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayPreviewViewportVisible))]
        [NotifyPropertyChangedFor(nameof(IsGameplayVideoSurfaceVisible))]
        [NotifyPropertyChangedFor(nameof(IsCarouselVisible))]
        [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
        [NotifyPropertyChangedFor(nameof(IsRomCarouselAnimationPaused))]
        [NotifyPropertyChangedFor(nameof(CanShowGameplayRecording))]
        [NotifyPropertyChangedFor(nameof(CanToggleGameplayRecording))]
        private bool _isEmulatorViewportDismissed;

        public bool IsGameplayPreviewAvailable =>
            IsGameplayAutoplayEnabled && IsYtDlpInstalled && !IsEmulatorRunning;
        public bool IsEmulatorViewportVisible =>
            EmulatorCapturePlatform.SupportsCompositionCapture &&
            IsEmulatorRunning &&
            !IsEmulatorViewportDismissed;
        public bool IsCompositionCaptureVisible => IsActive && IsEmulatorViewportVisible;
        public bool IsCarouselVisible => !IsEmulatorViewportVisible;
        public bool IsRomCarouselAnimationPaused => IsCompositionCaptureVisible;

        /// <summary>
        /// Pauses album-strip folder fan timers while the strip is expanded or during capture.
        /// Covers remain visible (snapped layout); only the per-tile 60 Hz UI timers stop so the
        /// ROM carousel compositor is not competing with the album list during scrolling.
        /// </summary>
        public bool IsEmulationFolderAnimationPaused => IsCompositionCaptureVisible || !IsAlbumListCollapsed;
        public bool IsSearchOverlayVisible => MetadataService?.IsImageSearchOverlayOpen == true && !IsCompositionCaptureVisible;
        public bool IsSearchBoxVisible => IsCarouselVisible && !(MetadataService?.IsMetadataLoaded == true);
        /// <summary>
        /// Gameplay preview tracks the selected cover via <see cref="CompositionCarouselControl.PublishSelectedItemBounds"/>.
        /// Disable while the album strip is expanded so ROM scrolling is not competing with
        /// video decode and per-frame layout on the preview overlay (music has no equivalent).
        /// </summary>
        public bool IsGameplayPreviewPublishBoundsActive =>
            IsGameplayPreviewHostVisible && !IsEmulatorViewportVisible && IsAlbumListCollapsed;

        public bool IsRomCarouselSelectedItemBoundsActive =>
            IsGameplayPreviewPublishBoundsActive;

        public bool IsGameplayPreviewViewportVisible => IsGameplayPreviewPublishBoundsActive;
        public bool IsGameplayVideoSurfaceVisible => IsGameplayVideoVisible && !IsEmulatorViewportVisible;
        public bool ForceUseTargetClientAreaCapture => CurrentEmulatorHandler?.ForceUseTargetClientAreaCapture == true;

        public bool EnableCapturePillarboxCrop => CurrentEmulatorHandler?.EnableCapturePillarboxCrop == true;
        public bool HideTargetWindowAfterCaptureStarts =>
            OperatingSystem.IsLinux() ? false : CurrentEmulatorHandler?.HideUntilCaptured != false;
        public int ClientAreaCropLeftInset => CurrentEmulatorHandler?.ClientAreaCropLeftInset ?? 0;
        public int ClientAreaCropTopInset => CurrentEmulatorHandler?.ClientAreaCropTopInset ?? 0;
        public int ClientAreaCropRightInset => CurrentEmulatorHandler?.ClientAreaCropRightInset ?? 0;
        public int ClientAreaCropBottomInset => CurrentEmulatorHandler?.ClientAreaCropBottomInset ?? 0;
        public Stretch CurrentCaptureStretch => SelectedStretch;
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
            // Both platforms list Shaders/hlsl/*.hlsl. Windows compiles them through D3D;
            // Linux composition capture runs the paired Shaders/glsl/*.glsl preset at runtime.
            const string hlslSubDir = "hlsl";
            var shaderDirectory = Path.Combine(ApplicationPaths.ShadersDirectory, hlslSubDir);
            var files = Directory.Exists(shaderDirectory)
                ? Directory.EnumerateFiles(shaderDirectory, "*.hlsl", SearchOption.TopDirectoryOnly).ToList()
                : [];

            var entries = new List<ShaderFileItem> { new(string.Empty, "None") };
            entries.AddRange(files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var hasGlslTwin = OperatingSystem.IsWindows() ||
                        File.Exists(path
                            .Replace($"{Path.DirectorySeparatorChar}hlsl{Path.DirectorySeparatorChar}",
                                $"{Path.DirectorySeparatorChar}glsl{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                            .Replace(".hlsl", ".glsl", StringComparison.OrdinalIgnoreCase));

                    var displayName = hasGlslTwin
                        ? Path.GetFileName(path)
                        : $"{Path.GetFileName(path)} (no OpenGL preset on Linux)";

                    return new ShaderFileItem(path, displayName, hasGlslTwin);
                }));
            return entries;
        }

        public ShadPs4CustomConfigEditorViewModel ShadPs4CustomConfigEditor { get; } = new();

        public ShadPs4CheatsEditorViewModel ShadPs4CheatsEditor { get; } = new();

        public DuckStationCheatsEditorViewModel DuckStationCheatsEditor { get; } = new();

        public DolphinCheatsEditorViewModel DolphinCheatsEditor { get; } = new();

        public XeniaCustomConfigEditorViewModel XeniaCustomConfigEditor { get; } = new();

        public Rpcs3CustomConfigEditorViewModel Rpcs3CustomConfigEditor { get; } = new();

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
                OnPropertyChanged(nameof(IsRomCarouselAnimationPaused));
                OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
                NotifyGameplayRecordingAvailabilityChanged();
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
                _subscribedSettingsViewModel.EmulationUseBackCoverLetterboxFillChanged -= OnEmulationUseBackCoverLetterboxFillChanged;
            }

            _subscribedSettingsViewModel = settings;
            AttachEmulationSectionSubscriptions(_subscribedSettingsViewModel);
            _subscribedSettingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
            _subscribedSettingsViewModel.EmulationUseFirstItemCoverChanged += OnEmulationUseFirstItemCoverChanged;
            _subscribedSettingsViewModel.EmulationGameplayAutoplayChanged += OnEmulationGameplayAutoplayChanged;
            _subscribedSettingsViewModel.EmulationUseBackCoverLetterboxFillChanged += OnEmulationUseBackCoverLetterboxFillChanged;
            OnPropertyChanged(nameof(UseBackCoverLetterboxFill));
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

            var isCoreChange = string.Equals(e.PropertyName, nameof(EmulationSectionItem.SelectedRetroArchCore), StringComparison.Ordinal);

            if (!isCoreChange &&
                !string.Equals(e.PropertyName, nameof(EmulationSectionItem.SelectedHandlerId), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(EmulationSectionItem.LaunchSettings), StringComparison.Ordinal) &&
                !string.Equals(e.PropertyName, nameof(EmulationSectionItem.GroupedRetroArchCores), StringComparison.Ordinal))
            {
                return;
            }

            var activeSection = TryResolveEmulationSection(GetActiveEmulationSectionAlbum());
            if (activeSection == null || !ReferenceEquals(activeSection, section))
                return;

            if (isCoreChange)
                SyncCurrentSectionRetroArchCoreSelection();
            else
                SyncCurrentSectionEmulatorContext();
        }

        private void RefreshCurrentSectionHandlerState() => SyncCurrentSectionEmulatorContext();

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
            {
                _subscribedMetadataService.PropertyChanged -= MetadataService_PropertyChanged;
                ReleaseLetterboxMetadataSubscription(_subscribedMetadataService);
            }

            _subscribedMetadataService = metadata;
            _subscribedMetadataService.PropertyChanged += MetadataService_PropertyChanged;
            EnsureLetterboxMetadataSubscription(metadata);
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
            {
                oldValue.PropertyChanged -= MetadataService_PropertyChanged;
                ReleaseLetterboxMetadataSubscription(oldValue);
            }

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
            NotifyGameplayRecordingAvailabilityChanged();
            if (!value && IsGameplayRecording)
                StopGameplayRecording();

            OnPropertyChanged(nameof(CanShowRenderOptions));
            OnPropertyChanged(nameof(ShowShadPs4InGameCheatsButton));
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
            OnPropertyChanged(nameof(IsGameplayPreviewAvailable));
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsRomCarouselAnimationPaused));
            OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
            OnPropertyChanged(nameof(IsGameplayPreviewViewportVisible));
            OnPropertyChanged(nameof(IsGameplayVideoSurfaceVisible));
            NotifyCaptureChromeMarginChanged();
            RefreshCurrentSectionLaunchOptionsState();

            if (value)
            {
                IsEmulatorViewportDismissed = false;
                StopGameplayPreview();
                OpenMetadataCommand.NotifyCanExecuteChanged();
                RebootEmulatorCommand.NotifyCanExecuteChanged();
                return;
            }

            if (_pendingEmulatorLaunchRequest == null)
                _activeEmulationSessionItem = null;

            IsRenderOptionsOpen = false;
            OpenMetadataCommand.NotifyCanExecuteChanged();
            RebootEmulatorCommand.NotifyCanExecuteChanged();
            ClearRetroArchErrorState();
            SyncCurrentSectionEmulatorContext();

            if (IsActive && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        partial void OnIsEmulatorLaunchInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowCurrentSectionPcsx2SetupLaunchButton));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationSetupLaunchButton));
            NotifyGameplayRecordingAvailabilityChanged();
            RebootEmulatorCommand.NotifyCanExecuteChanged();
            RefreshCurrentSectionLaunchOptionsState();
        }

        partial void OnIsEmulatorViewportDismissedChanged(bool value)
        {
            NotifyGameplayRecordingAvailabilityChanged();
            OnPropertyChanged(nameof(IsEmulatorViewportVisible));
            OnPropertyChanged(nameof(IsCompositionCaptureVisible));
            OnPropertyChanged(nameof(IsRomCarouselAnimationPaused));
            OnPropertyChanged(nameof(IsEmulationFolderAnimationPaused));
            OnPropertyChanged(nameof(IsCarouselVisible));
            OnPropertyChanged(nameof(IsSearchOverlayVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
            OnPropertyChanged(nameof(IsGameplayPreviewViewportVisible));
            OnPropertyChanged(nameof(IsGameplayVideoSurfaceVisible));
            NotifyCaptureChromeMarginChanged();
            RefreshCurrentSectionLaunchOptionsState();
        }

        private void RefreshAlbumPreviews()
        {
            foreach (var album in AlbumList.OfType<EmulationAlbumItem>())
            {
                UpdatePreviewItems(album);
                if (album.Children.Count > 0)
                    QueueAlbumPreviewCoverLoad(album);
            }

            // Force update of album list for view refresh.
            AlbumList = new AvaloniaList<FolderMediaItem>(AlbumList);
            ApplyFilter();
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded) && MetadataService != null && !MetadataService.IsMetadataLoaded)
            {
                RestoreCarouselAfterMetadataClosed();
                Dispatcher.UIThread.Post(SaveSettings, DispatcherPriority.Background);

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
                        IsAlbumListLoading = true;
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
            AutoSave();
            if (!value)
            {
                StopGameplayPreview();
                return;
            }

            if (IsActive && IsGameplayPreviewAvailable)
                QueueGameplayPreview(HighlightedItem, immediate: true);
        }

        partial void OnShowStatisticsOverlayChanged(bool value) => AutoSave();

        partial void OnShowFrametimeGraphChanged(bool value) => AutoSave();

        partial void OnShowDetailedGpuInfoChanged(bool value) => AutoSave();

        partial void OnRenderOverlayOpacityChanged(double value) => AutoSave();

        partial void OnSelectedStretchChanged(Stretch value) => AutoSave();

        partial void OnDisableVSyncChanged(bool value) => AutoSave();

        partial void OnLowLatencyCaptureChanged(bool value) => AutoSave();

        partial void OnFrameGenerationModeChanged(EmulationFrameGenerationMode value)
        {
            if (value == EmulationFrameGenerationMode.Software120Hz && !DisableVSync)
                DisableVSync = true;

            AutoSave();
        }

        public int FrameGenerationModeIndex
        {
            get => (int)FrameGenerationMode;
            set => FrameGenerationMode = (EmulationFrameGenerationMode)Math.Clamp(value, 0, 2);
        }

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
            SyncCurrentSectionEmulatorContext();
            OnPropertyChanged(nameof(ShowAlbumRomImportMenuItems));

            if (value is EmulationAlbumItem album)
                QueueAlbumPreviewCoverLoad(album);

            AutoSave();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            if (IsEmulatorRunning && !IsEmulatorViewportDismissed)
                IsEmulatorViewportDismissed = true;

            IsEmulatorUpdateNoticeOverlayOpen = false;

            if (value != null &&
                !string.Equals(_emulatorUpdateNoticeSuppressedAlbumTitle, value.Title, StringComparison.OrdinalIgnoreCase))
            {
                _emulatorUpdateNoticeSuppressedAlbumTitle = null;
            }

            ApplyFilter();
            QueueSelectedAlbumCoverScan(value);
            RefreshActiveAlbumState();
            SyncCurrentSectionEmulatorContext();

            OnPropertyChanged(nameof(ShowAlbumRomImportMenuItems));
        }

    }
}
