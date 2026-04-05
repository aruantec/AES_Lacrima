using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System.Windows.Input;

namespace AES_Lacrima.ViewModels;

/// <summary>
/// Marker interface for the settings view model used by the view locator
/// and dependency injection container.
/// </summary>
public interface ISettingsViewModel;

/// <summary>
/// Represents a shader resource with its file path and display name.
/// </summary>
/// <param name="Path">The file system path to the shader resource. Cannot be null or empty.</param>
/// <param name="Name">The display name of the shader. Cannot be null or empty.</param>
public record ShaderItem(string Path, string Name);

public sealed class EmulationHandlerAppItem : ObservableObject
{
    private readonly EmulationSectionItem _section;

    public EmulationHandlerAppItem(IEmulatorHandler handler, EmulationSectionItem section)
    {
        Handler = handler;
        _section = section;
        Handler.PropertyChanged += Handler_PropertyChanged;
        SetDefaultHandlerCommand = new RelayCommand(() => _section.SelectedHandlerId = Handler.HandlerId);
    }

    public IEmulatorHandler Handler { get; }

    public string HandlerId => Handler.HandlerId;

    public string DisplayName => Handler.DisplayName;

    public string LauncherDisplayPath => Handler.LauncherDisplayPath;

    public bool HasLauncherPath => Handler.HasLauncherPath;

    public ICommand? BrowseLauncherCommand => Handler.BrowseLauncherCommand;

    public ICommand? ClearLauncherCommand => Handler.ClearLauncherCommand;

    public ICommand SetDefaultHandlerCommand { get; }

    public bool IsRetroArchHandler => string.Equals(HandlerId, RetroArchHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase);

    public string? SelectedRetroArchCore
    {
        get => _section.SelectedRetroArchCore;
        set => _section.SelectedRetroArchCore = value;
    }

    public AvaloniaList<string> AvailableRetroArchCores => _section.RetroArchCores;

    public bool IsDefault => string.Equals(_section.SelectedHandlerId, Handler.HandlerId, StringComparison.OrdinalIgnoreCase);

    public void NotifyDefaultSelectionChanged()
    {
        OnPropertyChanged(nameof(IsDefault));
    }

    private void Handler_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IEmulatorHandler.LauncherPath) &&
            e.PropertyName != nameof(IEmulatorHandler.LauncherDisplayPath) &&
            e.PropertyName != nameof(IEmulatorHandler.HasLauncherPath) &&
            e.PropertyName != nameof(IEmulatorHandler.BrowseLauncherCommand) &&
            e.PropertyName != nameof(IEmulatorHandler.ClearLauncherCommand))
        {
            return;
        }

        OnPropertyChanged(nameof(LauncherDisplayPath));
        OnPropertyChanged(nameof(HasLauncherPath));
        OnPropertyChanged(nameof(BrowseLauncherCommand));
        OnPropertyChanged(nameof(ClearLauncherCommand));
    }
}

public partial class EmulationSectionItem : ObservableObject
{
    public EmulationSectionItem()
    {
        Handlers.CollectionChanged += Handlers_CollectionChanged;
    }

    [ObservableProperty]
    private string _sectionKey = string.Empty;

    [ObservableProperty]
    private string _sectionTitle = string.Empty;

    [ObservableProperty]
    private string? _albumImagePath;

    [ObservableProperty]
    private EmulationSectionLaunchSettings _launchSettings = new();

    [ObservableProperty]
    private AvaloniaList<EmulationHandlerAppItem> _handlers = [];

    [ObservableProperty]
    private AvaloniaList<string> _retroArchCores = [];

    [ObservableProperty]
    private string? _selectedHandlerId;

    [ObservableProperty]
    private bool _isExpanded;

    public string? SelectedRetroArchCore
    {
        get => LaunchSettings?.SelectedRetroArchCore;
        set
        {
            if (LaunchSettings == null)
                LaunchSettings = new EmulationSectionLaunchSettings();

            if (string.Equals(LaunchSettings.SelectedRetroArchCore, value, StringComparison.OrdinalIgnoreCase))
                return;

            LaunchSettings.SelectedRetroArchCore = value;
            OnPropertyChanged(nameof(SelectedRetroArchCore));
        }
    }

    public bool HasRetroArchCores => RetroArchCores.Count > 0;

    public bool HasHandlers => Handlers.Count > 0;

    partial void OnHandlersChanged(AvaloniaList<EmulationHandlerAppItem> value)
    {
        value.CollectionChanged += Handlers_CollectionChanged;
        OnPropertyChanged(nameof(HasHandlers));
    }

    partial void OnRetroArchCoresChanged(AvaloniaList<string> value)
    {
        value.CollectionChanged += RetroArchCores_CollectionChanged;
        OnPropertyChanged(nameof(HasRetroArchCores));
    }

    private void RetroArchCores_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasRetroArchCores));

    partial void OnSelectedHandlerIdChanged(string? value)
    {
        foreach (var handlerAppItem in Handlers)
            handlerAppItem.NotifyDefaultSelectionChanged();
    }

    private void Handlers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasHandlers));
}

public sealed class EmulationSectionLaunchSettings
{
    public bool? StartFullscreen { get; set; }

    public string? SelectedRetroArchCore { get; set; }

    public EmulationSectionLaunchSettings Clone() =>
        new()
        {
            StartFullscreen = StartFullscreen,
            SelectedRetroArchCore = SelectedRetroArchCore
        };
}

public sealed class EmulationSectionConfiguration
{
    public string? LauncherPath { get; set; }

    public string? DefaultHandlerId { get; set; }

    public EmulationSectionLaunchSettings LaunchSettings { get; set; } = new();
}

/// <summary>
/// View model that exposes application settings used by the UI. Settings
/// are loaded and saved via the inherited settings infrastructure.
/// </summary>
[AutoRegister]
public partial class SettingsViewModel : ViewModelBase, ISettingsViewModel
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<SettingsViewModel>();
    private static readonly string[] SupportedConsoleImageExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    ];
    private const string EmulationSectionLauncherPathsSettingName = "EmulationSectionLauncherPaths";
    private const string EmulationSectionConfigurationsSettingName = "EmulationSectionConfigurations";
    private const string EmulatorHandlerLauncherPathsSettingName = "EmulatorHandlerLauncherPaths";
    private const string SettingsViewModelsSectionName = "ViewModels";
    private const string EmulationViewModelSettingsSectionName = "EmulationViewModel";
    private const string EmulationAlbumOrderSettingName = "AlbumOrder";
#if NATIVE_AOT
    private const bool DefaultPreferAotAppUpdates = true;
#else
    private const bool DefaultPreferAotAppUpdates = false;
#endif

    private string _shaderToysDirectory = Path.Combine(ApplicationPaths.ShadersDirectory, "Shadertoys");
    private string _shadersDirectory = Path.Combine(ApplicationPaths.ShadersDirectory, "glsl");
    private bool _hasPersistedScaleFactor;
    private bool _hasPersistedMiniScaleFactor;

    /// <summary>
    /// Gets or sets the collection of dummy media items used for the carousel preview.
    /// </summary>
    [ObservableProperty]
    private AvaloniaList<FolderMediaItem> _previewItems = [];

    [ObservableProperty]
    private AvaloniaList<EmulationSectionItem> _emulationSections = [];

    /// <summary>
    /// Backing field for the <c>FfmpegPath</c> observable property.
    /// The generated property contains the path to the ffmpeg executable
    /// used by the application when exporting or processing media.
    /// </summary>
    [ObservableProperty]
    private string? _ffmpegPath;

    /// <summary>
    /// Determines which application window type to create on startup.
    /// 0 AES Mode, 1 Mini Mode.
    /// This value is persisted in settings and defaults to 0.
    /// </summary>
    [ObservableProperty]
    private int _appMode = 0;

    /// <summary>
    /// When true, use the first ROM item cover inside each emulation album as the displayed album tile in AES mode.
    /// When false, keep the console default folder covers from Assets/Consoles.
    /// </summary>
    [ObservableProperty]
    private bool _emulationUseFirstItemCover = false;

    public event Action<bool>? EmulationUseFirstItemCoverChanged;

    partial void OnEmulationUseFirstItemCoverChanged(bool value)
    {
        EmulationUseFirstItemCoverChanged?.Invoke(value);
    }

    [ObservableProperty]
    private bool _emulationGameplayAutoplay = false;

    public event Action<bool>? EmulationGameplayAutoplayChanged;

    partial void OnEmulationGameplayAutoplayChanged(bool value)
    {
        EmulationGameplayAutoplayChanged?.Invoke(value);
    }

    /// <summary>
    /// Backing field for the <c>ScaleFactor</c> observable property.
    /// Controls UI scaling applied by the <c>ScalableDecorator</c>.
    /// </summary>
    [ObservableProperty]
    private double _scaleFactor = 1.0;

    public bool HasPersistedScaleFactor => _hasPersistedScaleFactor;

    [ObservableProperty]
    private double _miniScaleFactor = 1.0;

    public bool HasPersistedMiniScaleFactor => _hasPersistedMiniScaleFactor;

    public bool IsAesMode => AppMode == 0;

    partial void OnAppModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsAesMode));
    }

    /// <summary>
    /// Backing field for the <c>ParticleCount</c> observable property.
    /// Determines how many particles are rendered by the particle system.
    /// </summary>
    [ObservableProperty]
    private double _particleCount = 10;

    /// <summary>
    /// Gets or sets a value indicating whether particle effects are displayed.
    /// </summary>
    [ObservableProperty]
    private bool _showParticles = true;

    /// <summary>
    /// Gets or sets a value indicating whether the clock's animated seconds ring is displayed.
    /// </summary>
    [ObservableProperty]
    private bool _showSecondCircleAnimation = false;

    [ObservableProperty]
    private bool _showEdgeBorder = false;

    /// <summary>
    /// Backing field for the <c>ShowShaderToy</c> observable property.
    /// When true the ShaderToy view will be visible in the UI.
    /// </summary>
    [ObservableProperty]
    private bool _showShaderToy = true;

    [ObservableProperty]
    private bool _miniShowShaderToy = true;

    /// <summary>
    /// Gets or sets the collection of shader items used by the control.
    /// </summary>
    /// <remarks>The collection can be modified to add, remove, or update shader items at runtime. Changes to
    /// the collection will be observed and reflected in the control's behavior. The property may be null if no shader
    /// items are assigned.</remarks>
    [ObservableProperty]
    private AvaloniaList<ShaderItem>? _shaderToys = [];

    /// <summary>
    /// Gets or sets the currently selected Shadertoy shader item.
    /// </summary>
    [ObservableProperty]
    private ShaderItem? _selectedShadertoy;

    [ObservableProperty]
    private ShaderItem? _miniSelectedShadertoy;

    /// <summary>
    /// Gets or sets the color used for the played portion of the waveform.
    /// </summary>
    [ObservableProperty]
    private Color _waveformPlayedColor = Color.Parse("RoyalBlue");

    /// <summary>
    /// Gets or sets the color used for the unplayed portion of the waveform.
    /// </summary>
    [ObservableProperty]
    private Color _waveformUnplayedColor = Color.Parse("DimGray");

    /// <summary>
    /// Gets or sets the resolution (number of samples) used for generating the waveform.
    /// </summary>
    [ObservableProperty]
    private int _waveformResolution = 4000;

    /// <summary>
    /// Gets or sets the horizontal gap between waveform bars.
    /// </summary>
    [ObservableProperty]
    private double _waveformBarGap;

    /// <summary>
    /// Gets or sets the height of each waveform block.
    /// </summary>
    [ObservableProperty]
    private double _waveformBlockHeight;

    /// <summary>
    /// Gets or sets the vertical gap between symmetric waveform halves.
    /// </summary>
    [ObservableProperty]
    private double _waveformVerticalGap = 4.0;

    /// <summary>
    /// Gets or sets the number of visual bars to display for the waveform.
    /// </summary>
    [ObservableProperty]
    private int _waveformVisualBars;

    /// <summary>
    /// Gets or sets a value indicating whether to use a gradient for the waveform.
    /// </summary>
    [ObservableProperty]
    private bool _useWaveformGradient;

    /// <summary>
    /// Gets or sets a value indicating whether the waveform is displayed symmetrically.
    /// </summary>
    [ObservableProperty]
    private bool _isWaveformSymmetric = true;

    // Spectrum visualiser settings

    /// <summary>
    /// Gets or sets the height of the spectrum visualizer.
    /// </summary>
    [ObservableProperty]
    private double _spectrumHeight = 170.0;

    /// <summary>
    /// Gets or sets the width of each spectrum bar.
    /// </summary>
    [ObservableProperty]
    private double _barWidth = 15.0;

    /// <summary>
    /// Gets or sets the spacing between spectrum bars.
    /// </summary>
    [ObservableProperty]
    private double _barSpacing = 5.0;

    /// <summary>
    /// Gets or sets a value indicating whether spectrum bars are shown in the main view.
    /// </summary>
    [ObservableProperty]
    private bool _showSpectrum = false;

    /// <summary>
    /// Gets or sets a value indicating whether spectrum bars are shown in the music view.
    /// </summary>
    [ObservableProperty]
    private bool _showMusicSpectrum = true;

    /// <summary>
    /// Gets or sets the gradient brush used for the spectrum visualizer.
    /// </summary>
    [ObservableProperty]
    private LinearGradientBrush? _spectrumGradient;

    // ReplayGain / loudness normalization settings
    /// <summary>
    /// Master toggle for applying replay gain / loudness normalization at playback.
    /// </summary>
    [ObservableProperty]
    private bool _replayGainEnabled = false;

    /// <summary>
    /// Gets or sets a value indicating whether volume changes are applied smoothly.
    /// </summary>
    [ObservableProperty]
    private bool _smoothVolumeChange = true;

    /// <summary>
    /// Gets or sets a value indicating whether logarithmic volume control is used.
    /// </summary>
    [ObservableProperty]
    private bool _logarithmicVolumeControl = false;

    /// <summary>
    /// Gets or sets a value indicating whether loudness compensation is applied to volume control.
    /// </summary>
    [ObservableProperty]
    private bool _loudnessCompensatedVolume = true;

    /// <summary>
    /// Delay in milliseconds waited after entering trailing silence before the player
    /// automatically fires the end‑of‑track event.  This value is configurable by the
    /// user via the audio settings tab.
    /// </summary>
    [ObservableProperty]
    private int _silenceAdvanceDelayMs = 500;

    /// <summary>
    /// When true, analyze files on-the-fly to compute target gain for tracks without tags.
    /// </summary>
    [ObservableProperty]
    private bool _replayGainAnalyzeOnTheFly = true;

    /// <summary>
    /// When true, use ReplayGain metadata tags (if present) as a source for gain.
    /// </summary>
    [ObservableProperty]
    private bool _replayGainUseTags = true;

    /// <summary>
    /// Preamp (in dB) applied when using analyzed gain values.
    /// </summary>
    [ObservableProperty]
    private double _replayGainPreampDb = 0.0;

    /// <summary>
    /// Preamp (in dB) applied when using tag-specified gain values.
    /// </summary>
    [ObservableProperty]
    private double _replayGainTagsPreampDb = 0.0;

    /// <summary>
    /// Source selection for tag-based gain: 0 = Track, 1 = Album.
    /// </summary>
    [ObservableProperty]
    private int _replayGainTagSource = 1; // default to Album

    // Preset palette for gradient comboboxes
    private readonly AvaloniaList<Color> _presetSpectrumColors =
    [
        Color.Parse("#00CCFF"),
        Color.Parse("#3333FF"),
        Color.Parse("#CC00CC"),
        Color.Parse("#FF004D"),
        Color.Parse("#FFB300")
    ];

    /// <summary>
    /// Gets the collection of preset colors used for the spectrum visualization gradient.
    /// </summary>
    public AvaloniaList<Color> PresetSpectrumColors => _presetSpectrumColors;

    /// <summary>
    /// Gets or sets the first color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor0;

    /// <summary>
    /// Gets or sets the second color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor1;

    /// <summary>
    /// Gets or sets the third color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor2;

    /// <summary>
    /// Gets or sets the fourth color in the spectrum gradient.
    /// </summary]
    [ObservableProperty]
    private Color _spectrumColor3;

    /// <summary>
    /// Gets or sets the fifth color in the spectrum gradient.
    /// </summary>
    [ObservableProperty]
    private Color _spectrumColor4;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// Sets up property monitoring to update the visualization gradient dynamically.
    /// </summary>
    public SettingsViewModel()
    {
        InitializeEmulationSections();

        // Initialize individual colors from the preset list
        _spectrumColor0 = _presetSpectrumColors[0];
        _spectrumColor1 = _presetSpectrumColors[1];
        _spectrumColor2 = _presetSpectrumColors[2];
        _spectrumColor3 = _presetSpectrumColors[3];
        _spectrumColor4 = _presetSpectrumColors[4];

        // Update gradient initially and when any spectrum color changes
        PropertyChanged += OnSettingsPropertyChanged;
        UpdateSpectrumGradient();
    }

    /// <summary>
    /// Gets or sets the FFmpeg manager for managing installations and updates.
    /// </summary>
    [AutoResolve]
    [ObservableProperty]
    private FFmpegManager? _ffmpegManager;

    /// <summary>
    /// Gets or sets the libmpv manager for managing installations.
    /// </summary>
    [AutoResolve]
    [ObservableProperty]
    private MpvLibraryManager? _mpvManager;

    /// <summary>
    /// Gets or sets the yt-dlp manager for managing installations.
    /// </summary>
    [AutoResolve]
    [ObservableProperty]
    private YtDlpManager? _ytDlp;

    /// <summary>
    /// Gets or sets the application updater service.
    /// </summary>
    [AutoResolve]
    [ObservableProperty]
    private AppUpdateService? _appUpdateService;

    /// <summary>
    /// Gets or sets a value indicating whether FFmpeg is currently installed.
    /// </summary>
    [ObservableProperty]
    private bool _isFfmpegInstalled;

    /// <summary>
    /// Gets or sets the currently installed FFmpeg version.
    /// </summary>
    [ObservableProperty]
    private string? _ffmpegVersion;

    /// <summary>
    /// Gets or sets the version of an available FFmpeg update.
    /// </summary>
    [ObservableProperty]
    private string? _ffmpegUpdateVersion;

    /// <summary>
    /// Gets or sets a value indicating whether an FFmpeg update is currently available.
    /// </summary>
    [ObservableProperty]
    private bool _isFfmpegUpdateAvailable;

    /// <summary>
    /// Gets or sets a value indicating whether libmpv is currently installed.
    /// </summary>
    [ObservableProperty]
    private bool _isMpvInstalled;

    /// <summary>
    /// Gets or sets the currently installed libmpv version.
    /// </summary>
    [ObservableProperty]
    private string? _mpvVersion;

    /// <summary>
    /// Gets or sets a value indicating whether yt-dlp is currently installed.
    /// </summary>
    [ObservableProperty]
    private bool _isYtDlpInstalled;

    /// <summary>
    /// Gets or sets the currently installed yt-dlp version.
    /// </summary>
    [ObservableProperty]
    private string? _ytDlpVersion;

    /// <summary>
    /// Gets or sets the version of an available yt-dlp update.
    /// </summary>
    [ObservableProperty]
    private string? _ytDlpUpdateVersion;

    /// <summary>
    /// Gets or sets a value indicating whether a yt-dlp update is currently available.
    /// </summary>
    [ObservableProperty]
    private bool _isYtDlpUpdateAvailable;

    /// <summary>
    /// Gets or sets the currently selected tab in the compact mini settings view.
    /// This is not persisted and only exists to guide the mini-mode UX.
    /// </summary>
    [ObservableProperty]
    private int _miniSettingsSelectedTab;

    /// <summary>
    /// Gets or sets the index of the currently selected tab in the settings overlay.
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Gets or sets a value indicating whether the background image is displayed.
    /// When true, ShowShaderToy is set to false.
    /// </summary>
    [ObservableProperty]
    private bool _showBackground;

    /// <summary>
    /// Gets or sets a value indicating whether the application should check for app updates on startup.
    /// </summary>
    [ObservableProperty]
#if DEBUG
    private bool _checkForAppUpdatesOnStartup = false;
#else
    private bool _checkForAppUpdatesOnStartup = true;
#endif

    [ObservableProperty]
    private bool _preferAotAppUpdates = DefaultPreferAotAppUpdates;

    [ObservableProperty]
    private AvaloniaList<AppReleaseInfo> _availableAppReleases = new();

    [ObservableProperty]
    private AppReleaseInfo? _selectedAppRelease;

    [ObservableProperty]
    private string? _selectedAppReleaseAssetName;

    [ObservableProperty]
    private string _selectedAppReleaseStatus = "Release history has not been loaded yet.";

    [ObservableProperty]
    private bool _isInstallingSelectedAppRelease;

    /// <summary>
    /// Gets or sets the path to the current background image.
    /// </summary>
    [ObservableProperty]
    private string _backgroundImagePath = Path.Combine("Assets", "background.jpg");

    /// <summary>
    /// Collection of available libmpv versions from GitHub (for Windows builds).
    /// </summary>
    [ObservableProperty]
    private AvaloniaList<MpvReleaseInfo> _availableMpvVersions = new();

    /// <summary>
    /// The currently selected version from the available versions list.
    /// </summary>
    [ObservableProperty]
    private MpvReleaseInfo? _selectedMpvVersion;

    /// <summary>
    /// Refreshes the information about the current FFmpeg installation and updates.
    /// </summary>
    [RelayCommand]
    public async Task RefreshFFmpegInfo()
    {
        if (FfmpegManager == null) return;
        FfmpegManager.ReportActivity(true);
        try
        {
            IsFfmpegInstalled = FfmpegManager.IsFFmpegAvailable();
            FfmpegVersion = await FfmpegManager.GetCurrentVersionAsync();
            var updateDetails = await FfmpegManager.CheckForUpdateDetailsAsync();
            IsFfmpegUpdateAvailable = updateDetails?.UpdateAvailable ?? false;
            FfmpegUpdateVersion = updateDetails?.NewVersion;

            if (!IsFfmpegInstalled)
            {
                FfmpegManager.Status = "FFmpeg check completed: Not found.";
            }
            else if (IsFfmpegUpdateAvailable)
            {
                FfmpegManager.Status = $"FFmpeg update found: {FfmpegUpdateVersion}.";
            }
            else
            {
                FfmpegManager.Status = $"FFmpeg is up to date ({FfmpegVersion}).";
            }
        }
        catch (Exception ex)
        {
            FfmpegManager.Status = $"FFmpeg check failed: {ex.Message}";
        }
        finally
        {
            FfmpegManager.ReportActivity(false);
        }
    }

    /// <summary>
    /// Refreshes information about the libmpv installation and fetches available versions from GitHub.
    /// </summary>
    [RelayCommand]
    public async Task RefreshMpvInfo()
    {
        if (MpvManager == null) return;
        
        MpvManager.ReportActivity(true);
        try
        {
            IsMpvInstalled = MpvManager.IsLibraryInstalled();
            MpvVersion = await MpvManager.GetCurrentVersionAsync();
            var versions = await MpvManager.GetAvailableVersionsAsync();
            
            AvailableMpvVersions.Clear();
            foreach (var v in versions) AvailableMpvVersions.Add(v);

            if (SelectedMpvVersion == null && AvailableMpvVersions.Count > 0)
            {
                SelectedMpvVersion = AvailableMpvVersions[0];
            }

            if (!IsMpvInstalled)
            {
                if (MpvManager.IsNewVersionPending())
                    MpvManager.Status = "Installation is staged and will be applied on the next restart.";
                else if (File.Exists(Path.Combine(ApplicationPaths.ToolsDirectory, "libmpv-2.dll.delete")))
                    MpvManager.Status = "libmpv is uninstalled.";
                else
                    MpvManager.Status = "libmpv check completed: Not found.";

                MpvVersion = null;
            }
            else if (MpvManager.IsNewVersionPending())
            {
                MpvManager.Status = $"Update is staged and will be applied on restart (Current: {MpvVersion ?? "Unknown"}).";
            }
            else if (MpvManager.IsPendingRestart)
            {
                MpvManager.Status = $"libmpv is marked for removal or modification on next restart (Current: {MpvVersion ?? "Unknown"}).";
            }
            else
            {
                MpvManager.Status = $"libmpv is installed ({MpvVersion ?? "Unknown version"}).";
            }
        }
        finally
        {
            MpvManager.ReportActivity(false);
        }
    }

    /// <summary>
    /// Installs libmpv for the current platform.
    /// </summary>
    [RelayCommand]
    private async Task InstallMpv()
    {
        if (MpvManager == null) return;
        await MpvManager.EnsureLibraryInstalledAsync();
        await RefreshMpvInfo();
    }

    /// <summary>
    /// Installs a specific version of libmpv for Windows.
    /// </summary>
    [RelayCommand]
    private async Task InstallSpecificMpvVersion()
    {
        if (MpvManager == null || SelectedMpvVersion == null) return;
        await MpvManager.InstallVersionAsync(SelectedMpvVersion.Tag);
        await RefreshMpvInfo();
    }

    /// <summary>
    /// Uninstalls libmpv from the application directory.
    /// </summary>
    [RelayCommand]
    private async Task UninstallMpv()
    {
        if (MpvManager == null) return;
        await MpvManager.UninstallAsync();
        await RefreshMpvInfo();
    }

    /// <summary>
    /// Installs FFmpeg using the system's package manager.
    /// </summary>
    [RelayCommand]
    private async Task InstallFFmpeg()
    {
        if (FfmpegManager == null) return;
        await FfmpegManager.InstallAsync();
        await RefreshFFmpegInfo();
    }

    /// <summary>
    /// Updates FFmpeg to the latest available version using the system's package manager.
    /// </summary>
    [RelayCommand]
    private async Task UpdateFFmpeg()
    {
        if (FfmpegManager == null) return;
        await FfmpegManager.UpgradeAsync();
        await RefreshFFmpegInfo();
    }

    /// <summary>
    /// Uninstalls FFmpeg from the system using the package manager.
    /// </summary>
    [RelayCommand]
    private async Task UninstallFFmpeg()
    {
        if (FfmpegManager == null) return;
        await FfmpegManager.UninstallAsync();
        await RefreshFFmpegInfo();
    }

    /// <summary>
    /// Refreshes information about the yt-dlp installation and checks for updates.
    /// </summary>
    [RelayCommand]
    public async Task RefreshYtDlpInfo()
    {
        if (YtDlp == null) return;

        try
        {
            IsYtDlpInstalled = YtDlpManager.IsInstalled;
            YtDlpVersion = await YtDlp.GetCurrentVersionAsync();
            YtDlpUpdateVersion = await YtDlp.GetLatestVersionAsync();

            IsYtDlpUpdateAvailable = !string.IsNullOrEmpty(YtDlpVersion) && 
                                     !string.IsNullOrEmpty(YtDlpUpdateVersion) && 
                                     !YtDlpVersion.Equals(YtDlpUpdateVersion);

            if (!IsYtDlpInstalled)
            {
                YtDlp.Status = "yt-dlp check completed: Not found.";
                YtDlpVersion = null;
            }
            else if (IsYtDlpUpdateAvailable)
            {
                YtDlp.Status = $"yt-dlp update found: {YtDlpUpdateVersion}.";
            }
            else
            {
                YtDlp.Status = $"yt-dlp is up to date ({YtDlpVersion}).";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to refresh yt-dlp info", ex);
            YtDlp.Status = $"yt-dlp check failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Installs yt-dlp for the current platform.
    /// </summary>
    [RelayCommand]
    private async Task InstallYtDlp()
    {
        if (YtDlp == null) return;
        await YtDlp.EnsureInstalledAsync();
        await RefreshYtDlpInfo();
    }

    /// <summary>
    /// Updates yt-dlp to the latest available version.
    /// </summary>
    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        if (YtDlp == null) return;
        await YtDlp.UpdateAsync();
        await RefreshYtDlpInfo();
    }

    /// <summary>
    /// Uninstalls yt-dlp from the application directory.
    /// </summary>
    [RelayCommand]
    private async Task UninstallYtDlp()
    {
        if (YtDlp == null) return;
        await YtDlp.UninstallAsync();
        await RefreshYtDlpInfo();
    }

    /// <summary>
    /// Checks whether a newer AES Lacrima release is available and opens the update prompt when appropriate.
    /// </summary>
    [RelayCommand]
    private async Task CheckForAppUpdate()
    {
        if (AppUpdateService == null)
            return;

        var release = await AppUpdateService.CheckForUpdatesAsync(forceRefresh: true);
        await RefreshAppReleaseHistory(forceRefresh: true);
        if (release != null)
        {
            if (AppMode == 1)
            {
                MiniSettingsSelectedTab = 2;
            }
            else
            {
                DiLocator.ResolveViewModel<MainWindowViewModel>()?.ShowAppUpdatePrompt(release);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAppReleaseHistory(bool forceRefresh = true)
    {
        if (AppUpdateService == null)
            return;

        var releases = await AppUpdateService.GetAvailableReleasesAsync(forceRefresh);

        AvailableAppReleases.Clear();
        foreach (var release in releases)
        {
            if (AppUpdateService.PrepareReleaseForInstall(release) != null)
                AvailableAppReleases.Add(release);
        }
        OnPropertyChanged(nameof(HasAvailableAppReleases));

        if (AvailableAppReleases.Count == 0)
        {
            SelectedAppRelease = null;
            SelectedAppReleaseStatus = "No compatible application versions were found for this platform/build preference.";
            return;
        }

        if (SelectedAppRelease != null)
        {
            var matchingRelease = AvailableAppReleases.FirstOrDefault(r =>
                string.Equals(r.TagName, SelectedAppRelease.TagName, StringComparison.OrdinalIgnoreCase));
            SelectedAppRelease = matchingRelease ?? AvailableAppReleases[0];
        }
        else
        {
            SelectedAppRelease = AvailableAppReleases[0];
        }

        SyncSelectedAppReleaseState();
    }

    [RelayCommand]
    private async Task InstallSelectedAppRelease()
    {
        if (AppUpdateService == null || SelectedAppRelease == null)
            return;

        var preparedRelease = AppUpdateService.PrepareReleaseForInstall(SelectedAppRelease);
        if (preparedRelease == null)
        {
            SelectedAppReleaseStatus = $"Version {SelectedAppRelease.DisplayLabel} does not have a compatible {PreferredAppUpdateFlavorLabel} package for this platform.";
            return;
        }

        IsInstallingSelectedAppRelease = true;
        try
        {
            await AppUpdateService.DownloadAndRestartToApplyUpdateAsync(preparedRelease);
        }
        finally
        {
            IsInstallingSelectedAppRelease = false;
            SyncSelectedAppReleaseState();
        }
    }

    [RelayCommand]
    private async Task DownloadAvailableAppUpdate()
    {
        if (AppUpdateService?.AvailableRelease is not { } release)
            return;

        await AppUpdateService.DownloadAndRestartToApplyUpdateAsync(release);
    }

    [RelayCommand]
    private void DismissAvailableAppUpdate()
    {
        AppUpdateService?.DismissAvailableUpdate();
    }

    // Carousel settings (used by CompositionCarouselControl)
    [ObservableProperty]
    private double _carouselSpacing = 0.93;

    [ObservableProperty]
    private double _carouselScale = 1.88;

    [ObservableProperty]
    private double _carouselVerticalOffset = -95.0;

    [ObservableProperty]
    private double _carouselSliderVerticalOffset = 119.0;

    [ObservableProperty]
    private double _carouselSliderTrackHeight = 17.0;

    [ObservableProperty]
    private double _carouselSideTranslation = 73.0;

    [ObservableProperty]
    private double _carouselStackSpacing = 39.0;

    [ObservableProperty]
    private bool _carouselUseFullCoverSize = false;

    /// <summary>
    /// Handles property change notifications to synchronize individual color properties
    /// with the internal collection and refresh the visual gradient.
    /// </summary>
    // flag used to suppress event handling during initial settings load
    private bool _isLoadingSettings;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) return;

        // skip during the initial LoadSettings/Prepare sequence; avoids re‑entrancy
        if (_isLoadingSettings)
            return;

        if (e.PropertyName == nameof(CheckForAppUpdatesOnStartup) ||
            e.PropertyName == nameof(PreferAotAppUpdates) ||
            e.PropertyName == nameof(ShowSecondCircleAnimation) ||
            e.PropertyName == nameof(ShowEdgeBorder))
        {
            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Warn("OnSettingsPropertyChanged: failed to persist app update preferences", ex);
            }
        }

        // If one of the spectrum color properties changed, update the gradient
        var updatedColor = false;
        if (e.PropertyName == nameof(SpectrumColor0)) { _presetSpectrumColors[0] = SpectrumColor0; updatedColor = true; }
        if (e.PropertyName == nameof(SpectrumColor1)) { _presetSpectrumColors[1] = SpectrumColor1; updatedColor = true; }
        if (e.PropertyName == nameof(SpectrumColor2)) { _presetSpectrumColors[2] = SpectrumColor2; updatedColor = true; }
        if (e.PropertyName == nameof(SpectrumColor3)) { _presetSpectrumColors[3] = SpectrumColor3; updatedColor = true; }
        if (e.PropertyName == nameof(SpectrumColor4)) { _presetSpectrumColors[4] = SpectrumColor4; updatedColor = true; }

        if (updatedColor)
        {
            UpdateSpectrumGradient();
        }

        // Persist changes for replaygain-related settings and notify player to re-evaluate
        if (e.PropertyName != null && e.PropertyName.StartsWith("ReplayGain", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Ensure settings are persisted to disk immediately to stay in sync
                SaveSettings();

                // Defer resolving the music view model until after the current call stack completes.
                _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var mv = DiLocator.ResolveViewModel<MusicViewModel>();
                        if (mv != null && mv.AudioPlayer != null)
                        {
                            var enabled = ReplayGainEnabled;
                            var useTags = ReplayGainUseTags;
                            var analyze = ReplayGainAnalyzeOnTheFly;
                            var preampAnalyze = ReplayGainPreampDb;
                            var preampTags = ReplayGainTagsPreampDb;
                            var tagSource = ReplayGainTagSource;

                            // Fire-and-forget the recompute to avoid blocking the UI
                            _ = Task.Run(async () =>
                            {
                                try { await mv.AudioPlayer.RecomputeReplayGainForCurrentAsync(enabled, useTags, analyze, preampAnalyze, preampTags, tagSource).ConfigureAwait(false); }
                                catch (Exception ex) { Log.Warn("Failed to recompute replaygain on AudioPlayer", ex); }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("OnSettingsPropertyChanged (deferred): failed to resolve music view model", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warn("OnSettingsPropertyChanged: failed to persist/apply replaygain settings", ex);
            }
        }

        // Apply new volume logic settings immediately
        if (e.PropertyName == nameof(SmoothVolumeChange) || 
            e.PropertyName == nameof(LogarithmicVolumeControl) || 
            e.PropertyName == nameof(LoudnessCompensatedVolume))
        {
            try
            {
                // Persist immediately
                SaveSettings();

                var mv = DiLocator.ResolveViewModel<MusicViewModel>();
                if (mv != null && mv.AudioPlayer != null)
                {
                    mv.AudioPlayer.SmoothVolumeChange = SmoothVolumeChange;
                    mv.AudioPlayer.LogarithmicVolumeControl = LogarithmicVolumeControl;
                    mv.AudioPlayer.LoudnessCompensatedVolume = LoudnessCompensatedVolume;
                    // Force a re-application of the current volume to trigger the new curve/math
                    mv.AudioPlayer.Volume = mv.AudioPlayer.Volume;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("OnSettingsPropertyChanged: failed to push volume settings to player", ex);
            }
        }

        // Push silence delay change to audio player
        if (e.PropertyName == nameof(SilenceAdvanceDelayMs))
        {
            try
            {
                SaveSettings();
                var mv = DiLocator.ResolveViewModel<MusicViewModel>();
                if (mv != null && mv.AudioPlayer != null)
                {
                    mv.AudioPlayer.SilenceAdvanceDelayMs = SilenceAdvanceDelayMs;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("OnSettingsPropertyChanged: failed to push silence delay to player", ex);
            }
        }

        if (e.PropertyName == nameof(ShowShaderToy) && ShowShaderToy)
        {
            ShowBackground = false;
        }

        if (e.PropertyName == nameof(ShowBackground) && ShowBackground)
        {
            ShowShaderToy = false;
        }
    }

    [RelayCommand]
    private async Task SelectBackgroundImage()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.StorageProvider is { } storage)
        {
            var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Background Image",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            if (result.Count > 0)
            {
                BackgroundImagePath = result[0].Path.LocalPath;
                SaveSettings();
            }
        }
    }

    [RelayCommand]
    private void OpenLibraryDirectory()
    {
        try
        {
            var path = ApplicationPaths.DataRootDirectory;
            Directory.CreateDirectory(path);
            OpenFolderInFileManager(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to open library directory", ex);
        }
    }

    private static void OpenFolderInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Rebuilds the <see cref="SpectrumGradient"/> based on the current collection of colors,
    /// distributing them evenly across the gradient stops.
    /// </summary>
    private void UpdateSpectrumGradient()
    {
        if (_presetSpectrumColors.Count == 0) return;

        var stops = new GradientStops();
        for (int i = 0; i < _presetSpectrumColors.Count; i++)
        {
            double offset = _presetSpectrumColors.Count > 1
                ? (double)i / (_presetSpectrumColors.Count - 1)
                : 0.0;

            stops.Add(new GradientStop(_presetSpectrumColors[i], offset));
        }

        SpectrumGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops = stops
        };
    }

    partial void OnShaderToysChanged(AvaloniaList<ShaderItem>? value)
    {
        // Ensure we always have a selected shader after the list is populated.
        EnsureDefaultSelectedShaderToy();
    }

    partial void OnAppUpdateServiceChanged(AppUpdateService? oldValue, AppUpdateService? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnAppUpdateServicePropertyChanged;

        if (newValue != null)
        {
            newValue.PreferAotUpdates = PreferAotAppUpdates;
            newValue.PropertyChanged += OnAppUpdateServicePropertyChanged;
            _ = RefreshAppReleaseHistory(forceRefresh: false);
        }
    }

    partial void OnPreferAotAppUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(PreferredAppUpdateFlavorLabel));

        if (AppUpdateService != null)
            AppUpdateService.PreferAotUpdates = value;

        _ = RefreshAppReleaseHistory(forceRefresh: false);
    }

    public string PreferredAppUpdateFlavorLabel => PreferAotAppUpdates ? "AOT" : "Non-AOT";

    public bool HasAvailableAppReleases => AvailableAppReleases.Count > 0;

    public bool CanInstallSelectedAppRelease =>
        SelectedAppRelease != null &&
        !string.IsNullOrWhiteSpace(SelectedAppReleaseAssetName) &&
        (AppUpdateService?.CanSelfUpdate ?? false) &&
        !(AppUpdateService?.IsBusy ?? false);

    public string SelectedAppReleaseDisplayStatus =>
        IsInstallingSelectedAppRelease && AppUpdateService?.IsBusy == true && !string.IsNullOrWhiteSpace(AppUpdateService.Status)
            ? AppUpdateService.Status
            : SelectedAppReleaseStatus;

    public bool IsSelectedAppReleaseBusy => IsInstallingSelectedAppRelease && AppUpdateService?.IsBusy == true;

    public bool IsSelectedAppReleaseDownloading => IsInstallingSelectedAppRelease && AppUpdateService?.IsDownloading == true;

    public bool IsSelectedAppReleasePreparing =>
        IsInstallingSelectedAppRelease && AppUpdateService?.IsBusy == true && AppUpdateService.IsDownloading == false;

    public double SelectedAppReleaseProgressValue =>
        IsInstallingSelectedAppRelease
            ? AppUpdateService?.DownloadProgress ?? 0
            : 0;

    public string SelectedAppReleaseProgressText =>
        IsSelectedAppReleaseDownloading
            ? $"{SelectedAppReleaseProgressValue:0}% downloaded"
            : (IsInstallingSelectedAppRelease ? AppUpdateService?.Status ?? "Preparing update..." : string.Empty);

    public string SelectedAppReleaseActionLabel
    {
        get
        {
            if (SelectedAppRelease == null || AppUpdateService == null)
                return "Install Selected Version";

            if (AppUpdateService.IsSameVersion(SelectedAppRelease))
                return "Reinstall Selected Version";

            return AppUpdateService.IsNewerVersion(SelectedAppRelease)
                ? "Update to Selected Version"
                : "Revert to Selected Version";
        }
    }

    partial void OnSelectedAppReleaseChanged(AppReleaseInfo? value)
    {
        SyncSelectedAppReleaseState();
    }

    private void OnAppUpdateServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (AppUpdateService == null)
            return;

        if (e.PropertyName == nameof(AppUpdateService.IsUpdateAvailable) && AppUpdateService.IsUpdateAvailable)
        {
            if (AppMode == 1)
                MiniSettingsSelectedTab = 2;
        }

        if (e.PropertyName == nameof(AppUpdateService.CanSelfUpdate) ||
            e.PropertyName == nameof(AppUpdateService.IsBusy) ||
            e.PropertyName == nameof(AppUpdateService.CurrentVersion) ||
            e.PropertyName == nameof(AppUpdateService.PreferAotUpdates))
        {
            SyncSelectedAppReleaseState();
        }

        if (e.PropertyName == nameof(AppUpdateService.IsBusy) ||
            e.PropertyName == nameof(AppUpdateService.IsDownloading) ||
            e.PropertyName == nameof(AppUpdateService.DownloadProgress) ||
            e.PropertyName == nameof(AppUpdateService.Status))
        {
            RaiseSelectedAppReleaseProgressProperties();
        }
    }

    private async Task BrowseEmulatorHandlerBinaryAsync(IEmulatorHandler handler)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is not { } storage)
        {
            return;
        }

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select Executable for {handler.DisplayName}",
            AllowMultiple = false,
            FileTypeFilter = BuildEmulationAppFilePickerFilters()
        });

        var localPath = result.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        handler.LauncherPath = localPath;
        SaveSettings();
    }

    private void ClearEmulatorHandlerBinary(IEmulatorHandler handler)
    {
        if (string.IsNullOrWhiteSpace(handler.LauncherPath))
            return;

        handler.LauncherPath = null;
        SaveSettings();
    }

    private void InitializeEmulationSections()
    {
        if (EmulationSections.Count > 0)
            return;

        foreach (var handler in EmulatorHandlerRegistry.GetRegisteredHandlers())
        {
            handler.BrowseLauncherCommand = new AsyncRelayCommand(() => BrowseEmulatorHandlerBinaryAsync(handler));
            handler.ClearLauncherCommand = new RelayCommand(() => ClearEmulatorHandlerBinary(handler));
            handler.PropertyChanged += OnEmulatorHandlerPropertyChanged;
        }

        foreach (var (sectionKey, sectionTitle, albumImagePath) in DiscoverEmulationSectionDefinitions())
        {
            var item = new EmulationSectionItem
            {
                SectionKey = sectionKey,
                SectionTitle = sectionTitle,
                AlbumImagePath = albumImagePath,
                LaunchSettings = CreateDefaultEmulationSectionLaunchSettings(sectionKey, sectionTitle)
            };

            foreach (var handler in EmulatorHandlerRegistry.GetHandlersForSection(sectionTitle))
                item.Handlers.Add(new EmulationHandlerAppItem(handler, item));

            if (item.Handlers.Count == 1)
                item.SelectedHandlerId = item.Handlers[0].HandlerId;

            item.PropertyChanged += OnEmulationSectionItemPropertyChanged;
            EmulationSections.Add(item);
        }

        ApplyPersistedEmulationSectionOrder();
    }

    private void OnEmulatorHandlerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoadingSettings)
            return;

        if (string.Equals(e.PropertyName, nameof(IEmulatorHandler.LauncherPath), StringComparison.OrdinalIgnoreCase) &&
            sender is IEmulatorHandler handler &&
            string.Equals(handler.HandlerId, RetroArchHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase))
        {
            RefreshRetroArchCores();
        }
    }

    private void OnEmulationSectionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoadingSettings)
            return;

        if (string.Equals(e.PropertyName, nameof(EmulationSectionItem.SelectedRetroArchCore), StringComparison.OrdinalIgnoreCase))
            SaveSettings();
    }

    private void RefreshRetroArchCores()
    {
        var retroArchHandler = EmulatorHandlerRegistry.GetRegisteredHandlers()
            .FirstOrDefault(handler => string.Equals(handler.HandlerId, RetroArchHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase));

        var availableCores = RetroArchHandler.GetRetroArchCores(retroArchHandler?.LauncherPath);

        foreach (var section in EmulationSections)
        {
            section.RetroArchCores.Clear();
            section.RetroArchCores.AddRange(availableCores);

            if (!string.IsNullOrWhiteSpace(section.SelectedRetroArchCore) &&
                !section.RetroArchCores.Contains(section.SelectedRetroArchCore, StringComparer.OrdinalIgnoreCase))
            {
                section.SelectedRetroArchCore = null;
            }

            if (string.IsNullOrWhiteSpace(section.SelectedRetroArchCore))
            {
                section.SelectedRetroArchCore = SelectDefaultRetroArchCore(section, availableCores);
            }
        }
    }

    private static string? SelectDefaultRetroArchCore(EmulationSectionItem section, IReadOnlyList<string> availableCores)
    {
        if (availableCores.Count == 0)
            return null;

        if (IsRetroArch3DSSection(section.SectionKey, section.SectionTitle))
        {
            return availableCores.FirstOrDefault(core => core.Contains("citra", StringComparison.OrdinalIgnoreCase));
        }

        if (IsRetroArchGameCubeSection(section.SectionKey, section.SectionTitle) ||
            IsRetroArchWiiSection(section.SectionKey, section.SectionTitle))
        {
            return availableCores.FirstOrDefault(core => core.Contains("dolphin", StringComparison.OrdinalIgnoreCase));
        }

        if (IsRetroArchN64Section(section.SectionKey, section.SectionTitle))
        {
            var n64Preference = new[] { "mupen64plus_next", "parallel_n64", "angrylion", "mupen64plus" };
            foreach (var keyword in n64Preference)
            {
                var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        if (IsRetroArchSnesSection(section.SectionKey, section.SectionTitle))
        {
            var snesPreference = new[] { "snes9x_next", "bsnes", "higan", "snes9x" };
            foreach (var keyword in snesPreference)
            {
                var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        if (IsRetroArchNesSection(section.SectionKey, section.SectionTitle))
        {
            var nesPreference = new[] { "nestopia", "fceumm", "quicknes" };
            foreach (var keyword in nesPreference)
            {
                var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        if (IsRetroArchDreamcastSection(section.SectionKey, section.SectionTitle))
        {
            var dreamcastPreference = new[] { "flycast", "nulldc" };
            foreach (var keyword in dreamcastPreference)
            {
                var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        if (IsRetroArchPlayStation2Section(section.SectionKey, section.SectionTitle))
        {
            return availableCores.FirstOrDefault(core => core.Contains("pcsx2", StringComparison.OrdinalIgnoreCase));
        }

        if (IsRetroArchPlayStationSection(section.SectionKey, section.SectionTitle))
        {
            var psxPreference = new[] { "beetle_psx", "pcsx_rearmed", "mednafen_psx" };
            foreach (var keyword in psxPreference)
            {
                var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
        }

        var arcadePreference = new[] { "fbneo", "mame", "finalburn", "fbalpha", "neogeo" };
        foreach (var keyword in arcadePreference)
        {
            var match = availableCores.FirstOrDefault(core => core.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return availableCores.FirstOrDefault();
    }

    private static bool IsRetroArch3DSSection(string? sectionKey, string? sectionTitle)
    {
        if (!string.IsNullOrWhiteSpace(sectionTitle) && sectionTitle.IndexOf("3ds", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (!string.IsNullOrWhiteSpace(sectionKey) && sectionKey.IndexOf("3ds", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private static bool IsRetroArchGameCubeSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "gamecube", "gcn", "gc");
    }

    private static bool IsRetroArchWiiSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "wii");
    }

    private static bool IsRetroArchN64Section(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "n64", "nintendo 64");
    }

    private static bool IsRetroArchSnesSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "snes", "super nintendo");
    }

    private static bool IsRetroArchNesSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "nes", "nintendo entertainment system");
    }

    private static bool IsRetroArchDreamcastSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "dreamcast");
    }

    private static bool IsRetroArchPlayStation2Section(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "playstation 2", "ps2");
    }

    private static bool IsRetroArchPlayStationSection(string? sectionKey, string? sectionTitle)
    {
        return IsRetroArchSection(sectionKey, sectionTitle, "playstation", "ps1", "psx");
    }

    private static bool IsRetroArchSection(string? sectionKey, string? sectionTitle, params string[] values)
    {
        if (!string.IsNullOrWhiteSpace(sectionTitle))
        {
            var normalized = sectionTitle.ToLowerInvariant();
            foreach (var value in values)
            {
                if (normalized.Contains(value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(sectionKey))
        {
            var normalizedKey = sectionKey.ToLowerInvariant();
            foreach (var value in values)
            {
                if (normalizedKey.Contains(value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<(string SectionKey, string SectionTitle, string AlbumImagePath)> DiscoverEmulationSectionDefinitions()
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

            if (files.Count == 0)
                continue;

            return files
                .Select(path =>
                {
                    var title = GetConsoleTitle(path);
                    var fileName = Path.GetFileName(path)?.Trim();
                    var key = !string.IsNullOrWhiteSpace(fileName)
                        ? fileName
                        : title;
                    return (SectionKey: key, SectionTitle: title, AlbumImagePath: path);
                })
                .ToList();
        }

        return [];
    }

    private void ApplyPersistedEmulationSectionOrder()
    {
        if (EmulationSections.Count <= 1)
            return;

        var persistedOrder = LoadPersistedEmulationSectionOrder();
        if (persistedOrder.Count == 0)
            return;

        var orderMap = persistedOrder
            .Select((key, index) => (key, index))
            .GroupBy(entry => entry.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

        var reordered = EmulationSections
            .Select((section, originalIndex) => new { section, originalIndex })
            .OrderBy(entry =>
                orderMap.TryGetValue(GetEmulationSectionOrderKey(entry.section), out var index)
                    ? index
                    : int.MaxValue)
            .ThenBy(entry => entry.originalIndex)
            .Select(entry => entry.section)
            .ToList();

        if (reordered.SequenceEqual(EmulationSections))
            return;

        EmulationSections.Clear();
        EmulationSections.AddRange(reordered);
    }

    private IReadOnlyList<string> LoadPersistedEmulationSectionOrder()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return [];

            var root = JsonNode.Parse(File.ReadAllText(SettingsFilePath)) as JsonObject;
            if (root == null)
                return [];

            if (root[SettingsViewModelsSectionName] is not JsonObject viewModelsSection)
                return [];

            if (viewModelsSection[EmulationViewModelSettingsSectionName] is not JsonObject emulationSection)
                return [];

            if (emulationSection[EmulationAlbumOrderSettingName] is not JsonArray albumOrderArray)
                return [];

            return albumOrderArray
                .Select(node => node?.GetValue<string>()?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to load persisted emulation section order from Settings.json.", ex);
            return [];
        }
    }

    private static string GetEmulationSectionOrderKey(EmulationSectionItem item)
    {
        var imageFileName = Path.GetFileName(item.AlbumImagePath)?.Trim();
        if (!string.IsNullOrWhiteSpace(imageFileName))
            return imageFileName;

        if (!string.IsNullOrWhiteSpace(item.SectionKey))
            return item.SectionKey.Trim();

        return item.SectionTitle.Trim();
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
        var normalizedName = NormalizeConsoleTitle(fileName);
        return EmulationConsoleCatalog.GetDisplayName(normalizedName);
    }

    private static string NormalizeConsoleTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var builder = new StringBuilder(title.Length);

        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c) || c == ' ')
            {
                builder.Append(c);
            }
            else if (c == '_' || c == '-')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim().Replace("  ", " ");
    }

    private static IReadOnlyList<FilePickerFileType> BuildEmulationAppFilePickerFilters()
    {
        var filters = new List<FilePickerFileType>();

        if (OperatingSystem.IsWindows())
        {
            filters.Add(new FilePickerFileType("Applications")
            {
                Patterns = ["*.exe", "*.bat", "*.cmd", "*.com"]
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            filters.Add(new FilePickerFileType("Applications")
            {
                Patterns = ["*.app", "*.command"]
            });
        }
        else
        {
            filters.Add(new FilePickerFileType("Applications")
            {
                Patterns = ["*"]
            });
        }

        filters.Add(new FilePickerFileType("All Files")
        {
            Patterns = ["*"]
        });

        return filters;
    }

    public IEmulatorHandler? GetConfiguredEmulatorHandler(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return null;

        var match = EmulationSections.FirstOrDefault(item =>
            string.Equals(item.SectionTitle, sectionTitle, StringComparison.OrdinalIgnoreCase));

        if (match == null || match.Handlers.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(match.SelectedHandlerId))
        {
            var selectedHandler = match.Handlers
                .FirstOrDefault(handlerItem =>
                    string.Equals(handlerItem.HandlerId, match.SelectedHandlerId, StringComparison.OrdinalIgnoreCase))
                ?.Handler;

            if (selectedHandler != null)
                return selectedHandler;
        }

        var configuredHandler = match.Handlers
            .Select(item => item.Handler)
            .FirstOrDefault(handler => handler.HasLauncherPath);

        if (configuredHandler != null)
            return configuredHandler;

        if (match.Handlers.Count == 1)
            return match.Handlers[0].Handler;

        return match.Handlers.Select(item => item.Handler).FirstOrDefault();
    }

    public EmulationSectionLaunchSettings GetResolvedEmulationSectionLaunchSettings(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return new EmulationSectionLaunchSettings();

        var match = EmulationSections.FirstOrDefault(item =>
            string.Equals(item.SectionTitle, sectionTitle, StringComparison.OrdinalIgnoreCase));

        return match?.LaunchSettings?.Clone() ?? new EmulationSectionLaunchSettings();
    }

    private static EmulationSectionLaunchSettings CreateDefaultEmulationSectionLaunchSettings(string sectionKey, string sectionTitle)
    {
        var settings = new EmulationSectionLaunchSettings();

        if (IsPlayStationSection(sectionKey, sectionTitle))
            settings.StartFullscreen = true;

        return settings;
    }

    private static bool IsPlayStationSection(string? sectionKey, string? sectionTitle)
    {
        return string.Equals(sectionTitle, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sectionTitle, "PSX", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sectionTitle, "PS1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sectionKey, "playstation", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sectionKey, "psx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sectionKey, "ps1", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDefaultSelectedShaderToy()
    {
        if (ShaderToys == null || ShaderToys.Count == 0)
            return;

        var defaultShader = ShaderToys.FirstOrDefault(s => s.Name == "AnimatedBackground")
                            ?? ShaderToys.FirstOrDefault();

        if (SelectedShadertoy == null || !ShaderToys.Contains(SelectedShadertoy))
            SelectedShadertoy = defaultShader;

        if (MiniSelectedShadertoy == null || !ShaderToys.Contains(MiniSelectedShadertoy))
            MiniSelectedShadertoy = defaultShader;
    }

    public override void Prepare()
    {
        // Load shader items from the local shaders directory (Linux-safe path resolution).
        _shaderToysDirectory = ResolveShaderToysDirectory();
        ShaderToys = [.. GetLocalShaders(_shaderToysDirectory, "*.frag")];
        EnsureDefaultSelectedShaderToy();

        // Disable our property‑changed handler while we populate values from disk.
        // the constructor already wired the handler for gradient updates but we don't
        // want to react to the initial load (avoids container deadlocks and needless
        // recompute work).
        PropertyChanged -= OnSettingsPropertyChanged;
        _isLoadingSettings = true;

        // Load settings (this will set many observable properties)
        LoadSettings();

        // finished loading
        _isLoadingSettings = false;

        if (AppUpdateService != null)
            AppUpdateService.PreferAotUpdates = PreferAotAppUpdates;

        // Re‑subscribe the handler so further user changes are observed
        try
        {
            PropertyChanged -= OnSettingsPropertyChanged;
            PropertyChanged += OnSettingsPropertyChanged;
        }
        catch { }

        // Refresh status info for all external tools
        _ = RefreshFFmpegInfo();
        _ = RefreshMpvInfo();
        _ = RefreshYtDlpInfo();
        _ = RefreshAppReleaseHistory(forceRefresh: false);

        // Generate dummy preview items
        var defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();
        var items = new List<FolderMediaItem>();
        for (int i = 1; i <= 10; i++)
        {
            items.Add(new FolderMediaItem
            {
                Title = $"Title {i}",
                Album = $"Album {i}",
                Artist = $"Artist {i}",
                CoverBitmap = defaultCover
            });
        }
        PreviewItems = [.. items];
    }

    public override void OnShowViewModel()
    {
        base.OnShowViewModel();
        ApplyPersistedEmulationSectionOrder();
    }

    private void SyncSelectedAppReleaseState()
    {
        OnPropertyChanged(nameof(HasAvailableAppReleases));
        OnPropertyChanged(nameof(CanInstallSelectedAppRelease));
        OnPropertyChanged(nameof(SelectedAppReleaseActionLabel));

        if (AppUpdateService == null)
        {
            SelectedAppReleaseAssetName = null;
            SelectedAppReleaseStatus = "Application updater is not available.";
            return;
        }

        if (SelectedAppRelease == null)
        {
            SelectedAppReleaseAssetName = null;
            SelectedAppReleaseStatus = AvailableAppReleases.Count == 0
                ? "Release history has not been loaded yet."
                : "Select a version to install.";
            return;
        }

        var preparedRelease = AppUpdateService.PrepareReleaseForInstall(SelectedAppRelease);
        SelectedAppReleaseAssetName = preparedRelease?.SelectedAsset?.Name;

        if (preparedRelease == null)
        {
            SelectedAppReleaseStatus = $"Version {SelectedAppRelease.DisplayLabel} does not have a compatible {PreferredAppUpdateFlavorLabel} package for this platform.";
            return;
        }

        if (!AppUpdateService.CanSelfUpdate)
        {
            SelectedAppReleaseStatus = "This installation cannot self-update from the selected version.";
            return;
        }

        if (AppUpdateService.IsSameVersion(SelectedAppRelease))
        {
            SelectedAppReleaseStatus = SelectedAppRelease.IsPrerelease
                ? $"Selected version {SelectedAppRelease.DisplayLabel} matches the installed version. You can reinstall it or switch build flavor."
                : $"Selected version {SelectedAppRelease.DisplayLabel} matches the installed version. You can reinstall it if needed.";
            return;
        }

        SelectedAppReleaseStatus = AppUpdateService.IsNewerVersion(SelectedAppRelease)
            ? $"Selected version {SelectedAppRelease.DisplayLabel} is newer than the installed build."
            : $"Selected version {SelectedAppRelease.DisplayLabel} is older than the installed build and can be used to roll back.";
    }

    partial void OnSelectedAppReleaseStatusChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedAppReleaseDisplayStatus));
    }

    partial void OnIsInstallingSelectedAppReleaseChanged(bool value)
    {
        RaiseSelectedAppReleaseProgressProperties();
    }

    private void RaiseSelectedAppReleaseProgressProperties()
    {
        OnPropertyChanged(nameof(CanInstallSelectedAppRelease));
        OnPropertyChanged(nameof(SelectedAppReleaseDisplayStatus));
        OnPropertyChanged(nameof(IsSelectedAppReleaseBusy));
        OnPropertyChanged(nameof(IsSelectedAppReleaseDownloading));
        OnPropertyChanged(nameof(IsSelectedAppReleasePreparing));
        OnPropertyChanged(nameof(SelectedAppReleaseProgressValue));
        OnPropertyChanged(nameof(SelectedAppReleaseProgressText));
    }

    /// <summary>
    /// Called during initialization to prepare the view model; loads persisted
    /// settings into the observable properties.
    /// </summary>

    protected override void OnLoadSettings(JsonObject section)
    {
        _hasPersistedScaleFactor = section.TryGetPropertyValue(nameof(ScaleFactor), out var scaleNode) && scaleNode != null;
        ScaleFactor = ReadDoubleSetting(section, nameof(ScaleFactor), 1.0);
        _hasPersistedMiniScaleFactor = section.TryGetPropertyValue(nameof(MiniScaleFactor), out var miniScaleNode) && miniScaleNode != null;
        MiniScaleFactor = ReadDoubleSetting(section, nameof(MiniScaleFactor), 1.0);
        ParticleCount = ReadDoubleSetting(section, nameof(ParticleCount), 10);
        ShowShaderToy = ReadBoolSetting(section, nameof(ShowShaderToy), true);
        ShowBackground = ReadBoolSetting(section, nameof(ShowBackground));
        BackgroundImagePath = ReadStringSetting(section, nameof(BackgroundImagePath), Path.Combine("Assets", "background.jpg"))!;
        ShowParticles = ReadBoolSetting(section, nameof(ShowParticles), true);
        ShowEdgeBorder = ReadBoolSetting(section, nameof(ShowEdgeBorder), false);
        ShowSecondCircleAnimation = ReadBoolSetting(section, nameof(ShowSecondCircleAnimation), ShowSecondCircleAnimation);
        // Spectrum settings
        SpectrumHeight = ReadDoubleSetting(section, nameof(SpectrumHeight), SpectrumHeight);
        BarWidth = ReadDoubleSetting(section, nameof(BarWidth), BarWidth);
        BarSpacing = ReadDoubleSetting(section, nameof(BarSpacing), BarSpacing);
        ShowSpectrum = ReadBoolSetting(section, nameof(ShowSpectrum), ShowSpectrum);
        ShowMusicSpectrum = ReadBoolSetting(section, nameof(ShowMusicSpectrum), ShowMusicSpectrum);

        // application mode (window type)
        AppMode = ReadIntSetting(section, nameof(AppMode), AppMode);
        EmulationUseFirstItemCover = ReadBoolSetting(section, nameof(EmulationUseFirstItemCover), EmulationUseFirstItemCover);
        EmulationGameplayAutoplay = ReadBoolSetting(section, nameof(EmulationGameplayAutoplay), EmulationGameplayAutoplay);
        var emulationSectionConfigurations = ReadObjectSetting<Dictionary<string, EmulationSectionConfiguration>>(section, EmulationSectionConfigurationsSettingName)
            ?? new Dictionary<string, EmulationSectionConfiguration>(StringComparer.OrdinalIgnoreCase);
        var emulationSectionLauncherPaths = ReadObjectSetting<Dictionary<string, string>>(section, EmulationSectionLauncherPathsSettingName)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var emulatorHandlerLauncherPaths = ReadObjectSetting<Dictionary<string, string>>(section, EmulatorHandlerLauncherPathsSettingName)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in EmulatorHandlerRegistry.GetRegisteredHandlers())
            handler.LauncherPath = null;

        foreach (var handler in EmulatorHandlerRegistry.GetRegisteredHandlers())
        {
            if (emulatorHandlerLauncherPaths.TryGetValue(handler.HandlerId, out var launcherPath))
                handler.LauncherPath = launcherPath;
        }

        foreach (var item in EmulationSections)
        {
            var defaultLaunchSettings = CreateDefaultEmulationSectionLaunchSettings(item.SectionKey, item.SectionTitle);
            if (emulationSectionConfigurations.TryGetValue(item.SectionKey, out var configuration))
            {
                item.LaunchSettings = MergeLaunchSettings(defaultLaunchSettings, configuration.LaunchSettings);
                item.SelectedHandlerId = configuration.DefaultHandlerId;

                if (!string.IsNullOrWhiteSpace(configuration.LauncherPath))
                    MigrateLegacySectionLauncherPath(item.SectionTitle, configuration.LauncherPath);
            }
            else
            {
                item.LaunchSettings = defaultLaunchSettings;
            }

            if (item.Handlers.Count == 1 && string.IsNullOrWhiteSpace(item.SelectedHandlerId))
                item.SelectedHandlerId = item.Handlers[0].HandlerId;

            if (emulationSectionLauncherPaths.TryGetValue(item.SectionKey, out var legacyLauncherPath))
                MigrateLegacySectionLauncherPath(item.SectionTitle, legacyLauncherPath);
        }

        ApplyPersistedEmulationSectionOrder();
        RefreshRetroArchCores();
        CheckForAppUpdatesOnStartup = ReadBoolSetting(section, nameof(CheckForAppUpdatesOnStartup), CheckForAppUpdatesOnStartup);
        PreferAotAppUpdates = ReadBoolSetting(section, nameof(PreferAotAppUpdates), PreferAotAppUpdates);

        // Individual spectrum colors (persisted as strings)
        if (ReadStringSetting(section, nameof(SpectrumColor0)) is { } c0) SpectrumColor0 = Color.Parse(c0);
        if (ReadStringSetting(section, nameof(SpectrumColor1)) is { } c1) SpectrumColor1 = Color.Parse(c1);
        if (ReadStringSetting(section, nameof(SpectrumColor2)) is { } c2) SpectrumColor2 = Color.Parse(c2);
        if (ReadStringSetting(section, nameof(SpectrumColor3)) is { } c3) SpectrumColor3 = Color.Parse(c3);
        if (ReadStringSetting(section, nameof(SpectrumColor4)) is { } c4) SpectrumColor4 = Color.Parse(c4);
        WaveformPlayedColor = Color.Parse(ReadStringSetting(section, nameof(WaveformPlayedColor), "RoyalBlue")!);
        
        // Set the selected shadertoy if it exists, otherwise default to "AnimatedBackground"
        string? selectedshadertoy = ReadStringSetting(section, nameof(SelectedShadertoy));
        SelectedShadertoy = ShaderToys?.FirstOrDefault(s => s.Name == (selectedshadertoy ?? "AnimatedBackground")) 
                            ?? ShaderToys?.FirstOrDefault();
        MiniShowShaderToy = ReadBoolSetting(section, nameof(MiniShowShaderToy), MiniShowShaderToy);
        string? miniSelectedShadertoy = ReadStringSetting(section, nameof(MiniSelectedShadertoy));
        MiniSelectedShadertoy = ShaderToys?.FirstOrDefault(s => s.Name == (miniSelectedShadertoy ?? "AnimatedBackground"))
                               ?? ShaderToys?.FirstOrDefault();

        // Carousel settings
        CarouselSpacing = ReadDoubleSetting(section, nameof(CarouselSpacing), CarouselSpacing);
        CarouselScale = ReadDoubleSetting(section, nameof(CarouselScale), CarouselScale);
        CarouselVerticalOffset = ReadDoubleSetting(section, nameof(CarouselVerticalOffset), CarouselVerticalOffset);
        CarouselSliderVerticalOffset = ReadDoubleSetting(section, nameof(CarouselSliderVerticalOffset), CarouselSliderVerticalOffset);
        CarouselSliderTrackHeight = ReadDoubleSetting(section, nameof(CarouselSliderTrackHeight), CarouselSliderTrackHeight);
        CarouselSideTranslation = ReadDoubleSetting(section, nameof(CarouselSideTranslation), CarouselSideTranslation);
        CarouselStackSpacing = ReadDoubleSetting(section, nameof(CarouselStackSpacing), CarouselStackSpacing);
        CarouselUseFullCoverSize = ReadBoolSetting(section, nameof(CarouselUseFullCoverSize), CarouselUseFullCoverSize);

        // ReplayGain settings
        ReplayGainEnabled = ReadBoolSetting(section, nameof(ReplayGainEnabled), ReplayGainEnabled);
        SmoothVolumeChange = ReadBoolSetting(section, nameof(SmoothVolumeChange), SmoothVolumeChange);
        LogarithmicVolumeControl = ReadBoolSetting(section, nameof(LogarithmicVolumeControl), LogarithmicVolumeControl);
        LoudnessCompensatedVolume = ReadBoolSetting(section, nameof(LoudnessCompensatedVolume), LoudnessCompensatedVolume);
        ReplayGainAnalyzeOnTheFly = ReadBoolSetting(section, nameof(ReplayGainAnalyzeOnTheFly), ReplayGainAnalyzeOnTheFly);
        ReplayGainUseTags = ReadBoolSetting(section, nameof(ReplayGainUseTags), ReplayGainUseTags);
        ReplayGainPreampDb = ReadDoubleSetting(section, nameof(ReplayGainPreampDb), ReplayGainPreampDb);
        ReplayGainTagsPreampDb = ReadDoubleSetting(section, nameof(ReplayGainTagsPreampDb), ReplayGainTagsPreampDb);
        ReplayGainTagSource = ReadIntSetting(section, nameof(ReplayGainTagSource), ReplayGainTagSource);
        SilenceAdvanceDelayMs = ReadIntSetting(section, nameof(SilenceAdvanceDelayMs), SilenceAdvanceDelayMs);
    }

    /// <summary>
    /// Reads settings from the provided JSON section and applies them to
    /// this view model's properties.
    /// </summary>
    /// <param name="section">The JSON object that contains persisted settings.</param>

    protected override void OnSaveSettings(JsonObject section)
    {
        WriteSetting(section, nameof(ScaleFactor), ScaleFactor);
        WriteSetting(section, nameof(MiniScaleFactor), MiniScaleFactor);
        WriteSetting(section, nameof(ParticleCount), ParticleCount);
        WriteSetting(section, nameof(ShowShaderToy), ShowShaderToy);
        WriteSetting(section, nameof(MiniShowShaderToy), MiniShowShaderToy);
        WriteSetting(section, nameof(ShowBackground), ShowBackground);
        WriteSetting(section, nameof(BackgroundImagePath), BackgroundImagePath);
        WriteSetting(section, nameof(ShowParticles), ShowParticles);
        WriteSetting(section, nameof(ShowEdgeBorder), ShowEdgeBorder);
        WriteSetting(section, nameof(ShowSecondCircleAnimation), ShowSecondCircleAnimation);
        WriteSetting(section, nameof(WaveformPlayedColor), WaveformPlayedColor.ToString());
        WriteSetting(section, nameof(SelectedShadertoy), SelectedShadertoy?.Name ?? string.Empty);
        WriteSetting(section, nameof(MiniSelectedShadertoy), MiniSelectedShadertoy?.Name ?? string.Empty);
        // Spectrum settings
        WriteSetting(section, nameof(SpectrumHeight), SpectrumHeight);
        WriteSetting(section, nameof(BarWidth), BarWidth);
        WriteSetting(section, nameof(BarSpacing), BarSpacing);
        WriteSetting(section, nameof(ShowSpectrum), ShowSpectrum);
        WriteSetting(section, nameof(ShowMusicSpectrum), ShowMusicSpectrum);

        // Persist individual spectrum colors
        WriteSetting(section, nameof(SpectrumColor0), SpectrumColor0.ToString());
        WriteSetting(section, nameof(SpectrumColor1), SpectrumColor1.ToString());
        WriteSetting(section, nameof(SpectrumColor2), SpectrumColor2.ToString());
        WriteSetting(section, nameof(SpectrumColor3), SpectrumColor3.ToString());
        WriteSetting(section, nameof(SpectrumColor4), SpectrumColor4.ToString());

        // Persist application mode (window type)
        WriteSetting(section, nameof(AppMode), AppMode);
        WriteSetting(section, nameof(EmulationUseFirstItemCover), EmulationUseFirstItemCover);
        WriteSetting(section, nameof(EmulationGameplayAutoplay), EmulationGameplayAutoplay);
        WriteObjectSetting(
            section,
            EmulationSectionConfigurationsSettingName,
            EmulationSections
                .Where(HasPersistedEmulationSectionConfiguration)
                .ToDictionary(
                    item => item.SectionKey,
                    item => new EmulationSectionConfiguration
                    {
                        DefaultHandlerId = item.SelectedHandlerId,
                        LaunchSettings = item.LaunchSettings?.Clone() ?? new EmulationSectionLaunchSettings()
                    },
                    StringComparer.OrdinalIgnoreCase));
        WriteObjectSetting(
            section,
            EmulatorHandlerLauncherPathsSettingName,
            EmulatorHandlerRegistry.GetRegisteredHandlers()
                .Where(handler => !string.IsNullOrWhiteSpace(handler.HandlerId) && !string.IsNullOrWhiteSpace(handler.LauncherPath))
                .ToDictionary(handler => handler.HandlerId, handler => handler.LauncherPath!, StringComparer.OrdinalIgnoreCase));
        WriteSetting(section, nameof(CheckForAppUpdatesOnStartup), CheckForAppUpdatesOnStartup);
        WriteSetting(section, nameof(PreferAotAppUpdates), PreferAotAppUpdates);

        // Persist Carousel settings
        WriteSetting(section, nameof(CarouselSpacing), CarouselSpacing);
        WriteSetting(section, nameof(CarouselScale), CarouselScale);
        WriteSetting(section, nameof(CarouselVerticalOffset), CarouselVerticalOffset);
        WriteSetting(section, nameof(CarouselSliderVerticalOffset), CarouselSliderVerticalOffset);
        WriteSetting(section, nameof(CarouselSliderTrackHeight), CarouselSliderTrackHeight);
        WriteSetting(section, nameof(CarouselSideTranslation), CarouselSideTranslation);
        WriteSetting(section, nameof(CarouselStackSpacing), CarouselStackSpacing);
        WriteSetting(section, nameof(CarouselUseFullCoverSize), CarouselUseFullCoverSize);
        // ReplayGain settings
        WriteSetting(section, nameof(ReplayGainEnabled), ReplayGainEnabled);
        WriteSetting(section, nameof(SmoothVolumeChange), SmoothVolumeChange);
        WriteSetting(section, nameof(LogarithmicVolumeControl), LogarithmicVolumeControl);
        WriteSetting(section, nameof(LoudnessCompensatedVolume), LoudnessCompensatedVolume);
        WriteSetting(section, nameof(ReplayGainAnalyzeOnTheFly), ReplayGainAnalyzeOnTheFly);
        WriteSetting(section, nameof(ReplayGainUseTags), ReplayGainUseTags);
        WriteSetting(section, nameof(ReplayGainPreampDb), ReplayGainPreampDb);
        WriteSetting(section, nameof(ReplayGainTagsPreampDb), ReplayGainTagsPreampDb);
        WriteSetting(section, nameof(ReplayGainTagSource), ReplayGainTagSource);
        WriteSetting(section, nameof(SilenceAdvanceDelayMs), SilenceAdvanceDelayMs);
    }

    private void MigrateLegacySectionLauncherPath(string? sectionTitle, string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle) || string.IsNullOrWhiteSpace(launcherPath))
            return;

        var handler = GetConfiguredEmulatorHandler(sectionTitle)
            ?? EmulatorHandlerRegistry.GetHandlersForSection(sectionTitle).FirstOrDefault();

        if (handler == null || !string.IsNullOrWhiteSpace(handler.LauncherPath))
            return;

        handler.LauncherPath = launcherPath;
    }

    private static bool HasPersistedEmulationSectionConfiguration(EmulationSectionItem item)
    {
        if (string.IsNullOrWhiteSpace(item.SectionKey))
            return false;

        return item.LaunchSettings?.StartFullscreen is not null ||
               !string.IsNullOrWhiteSpace(item.SelectedRetroArchCore) ||
               !string.IsNullOrWhiteSpace(item.SelectedHandlerId);
    }

    private static EmulationSectionLaunchSettings MergeLaunchSettings(EmulationSectionLaunchSettings defaults, EmulationSectionLaunchSettings? persisted)
    {
        if (persisted == null)
            return defaults;

        return new EmulationSectionLaunchSettings
        {
            StartFullscreen = persisted.StartFullscreen ?? defaults.StartFullscreen,
            SelectedRetroArchCore = persisted.SelectedRetroArchCore ?? defaults.SelectedRetroArchCore
        };
    }

    partial void OnScaleFactorChanged(double value)
    {
        _hasPersistedScaleFactor = true;
    }

    partial void OnMiniScaleFactorChanged(double value)
    {
        _hasPersistedMiniScaleFactor = true;
    }

    /// <summary>
    /// Retrieves a list of local shader files from the specified directory that match the given search pattern.
    /// </summary>
    /// <param name="directory">The path to the directory to search for shader files. Must be a valid directory path.</param>
    /// <param name="pattern">The search pattern used to filter files within the directory, such as "*.shader". Supports standard wildcard
    /// characters.</param>
    /// <returns>A list of ShaderItem objects representing the shader files found in the directory. Returns an empty list if the
    /// directory does not exist or no files match the pattern.</returns>
    private List<ShaderItem> GetLocalShaders(string directory, string pattern)
    {
        if (Directory.Exists(directory))
        {
            // Sort the results by display name to ensure deterministic alphabetical order
            return [.. Directory.EnumerateFiles(directory, pattern)
                .Select(file => new ShaderItem(file, Path.GetFileNameWithoutExtension(file)))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }

        // Fallback: scan any fragment shader under the standard shaders directory.
        var shadersRoot = ApplicationPaths.ShadersDirectory;
        if (!Directory.Exists(shadersRoot)) return [];

        return [.. Directory.EnumerateFiles(shadersRoot, pattern, SearchOption.AllDirectories)
            .Select(file => new ShaderItem(file, Path.GetFileNameWithoutExtension(file)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static string ResolveShaderToysDirectory()
    {
        var shadersRoot = ApplicationPaths.ShadersDirectory;
        var candidates = new[]
        {
            Path.Combine(shadersRoot, "Shadertoys"),
            Path.Combine(shadersRoot, "Shadertoy"),
            Path.Combine(shadersRoot, "shadertoys"),
            Path.Combine(shadersRoot, "shadertoy")
        };

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                return path;
        }

        return candidates[0];
    }
}
