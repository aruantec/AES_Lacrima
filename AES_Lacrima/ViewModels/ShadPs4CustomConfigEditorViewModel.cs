using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Lacrima.Services.ShadPs4;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class ShadPs4CustomConfigEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _titleId;
    private string? _gameTitle;
    private string? _configFilePath;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isDirty;
    private string _status = "Select a PlayStation 4 game to edit custom config.";
    private int _selectedTabIndex;

    [ObservableProperty] private int _audioBackend;
    [ObservableProperty] private string _sdlMainOutputDevice = "Default Device";
    [ObservableProperty] private string _sdlMicDevice = "Default Device";
    [ObservableProperty] private string _sdlPadSpkOutputDevice = "Default Device";
    [ObservableProperty] private string _openalMainOutputDevice = "Default Device";
    [ObservableProperty] private string _openalMicDevice = "Default Device";
    [ObservableProperty] private string _openalPadSpkOutputDevice = "Default Device";

    [ObservableProperty] private bool _debugDump;
    [ObservableProperty] private bool _shaderCollect;

    [ObservableProperty] private bool _copyGpuBuffers;
    [ObservableProperty] private bool _directMemoryAccessEnabled;
    [ObservableProperty] private bool _dumpShaders;
    [ObservableProperty] private bool _fsrEnabled;
    [ObservableProperty] private bool _fullScreen;
    [ObservableProperty] private string _fullScreenMode = "Windowed";
    [ObservableProperty] private bool _hdrAllowed;
    [ObservableProperty] private bool _nullGpu;
    [ObservableProperty] private bool _patchShaders;
    [ObservableProperty] private string _presentMode = "Immediate";
    [ObservableProperty] private int _rcasAttenuation = 250;
    [ObservableProperty] private bool _rcasEnabled = true;
    [ObservableProperty] private bool _readbackLinearImagesEnabled;
    [ObservableProperty] private int _readbacksMode;
    [ObservableProperty] private int _vblankFrequency = 60;
    [ObservableProperty] private string _selectedResolution = "1280 x 720";

    [ObservableProperty] private bool _connectedToNetwork;
    [ObservableProperty] private bool _devKitMode;
    [ObservableProperty] private int _extraDmemInMbytes;
    [ObservableProperty] private bool _neoMode;
    [ObservableProperty] private bool _psnSignedIn;
    [ObservableProperty] private bool _showSplash;
    [ObservableProperty] private double _trophyNotificationDuration = 6.0;
    [ObservableProperty] private string _trophyNotificationSide = "right";
    [ObservableProperty] private bool _trophyPopupDisabled;
    [ObservableProperty] private int _volumeSlider = 100;

    [ObservableProperty] private bool _backgroundControllerInput = true;
    [ObservableProperty] private int _cameraId = -1;
    [ObservableProperty] private int _cursorHideTimeout = 5;
    [ObservableProperty] private int _cursorState = 1;
    [ObservableProperty] private bool _motionControlsEnabled = true;
    [ObservableProperty] private int _usbDeviceBackend;

    [ObservableProperty] private bool _logAppend;
    [ObservableProperty] private bool _logEnable = true;
    [ObservableProperty] private string _logFilter = string.Empty;
    [ObservableProperty] private int _logMaxSkipDuration = 5000;
    [ObservableProperty] private bool _logSeparate;
    [ObservableProperty] private long _logSizeLimit = 104857600;
    [ObservableProperty] private bool _logSkipDuplicate = true;
    [ObservableProperty] private bool _logSync = true;
    [ObservableProperty] private string _logType = "wincolor";

    [ObservableProperty] private int _gpuId = ShadPs4HardwareEnumeration.AutoSelectGpuId;
    [ObservableProperty] private bool _pipelineCacheArchived;
    [ObservableProperty] private bool _pipelineCacheEnabled = true;
    [ObservableProperty] private bool _renderdocEnabled;
    [ObservableProperty] private bool _vkcrashDiagnosticEnabled;
    [ObservableProperty] private bool _vkguestMarkers;
    [ObservableProperty] private bool _vkhostMarkers;
    [ObservableProperty] private bool _vkvalidationCoreEnabled = true;
    [ObservableProperty] private bool _vkvalidationEnabled;
    [ObservableProperty] private bool _vkvalidationGpuEnabled;
    [ObservableProperty] private bool _vkvalidationSyncEnabled;

    public IReadOnlyList<string> ResolutionPresets => ShadPs4CustomConfigService.ResolutionPresets;
    public IReadOnlyList<string> AudioBackendLabels => ShadPs4CustomConfigService.AudioBackendLabels;
    public IReadOnlyList<string> FullScreenModeLabels => ShadPs4CustomConfigService.FullScreenModeLabels;
    public IReadOnlyList<string> PresentModeLabels => ShadPs4CustomConfigService.PresentModeLabels;
    public IReadOnlyList<string> ReadbacksModeLabels => ShadPs4CustomConfigService.ReadbacksModeLabels;
    public IReadOnlyList<string> CursorStateLabels => ShadPs4CustomConfigService.CursorStateLabels;
    public IReadOnlyList<string> UsbDeviceBackendLabels => ShadPs4CustomConfigService.UsbDeviceBackendLabels;
    public IReadOnlyList<string> TrophyNotificationSideLabels => ShadPs4CustomConfigService.TrophyNotificationSideLabels;
    public IReadOnlyList<string> LogTypeLabels => ShadPs4CustomConfigService.LogTypeLabels;

    public IReadOnlyList<string> AudioDevices { get; private set; } = [ShadPs4HardwareEnumeration.DefaultAudioDeviceLabel];
    public IReadOnlyList<ShadPs4GpuOption> GpuOptions { get; private set; } = ShadPs4CustomConfigService.GetGpuOptions();

    public IReadOnlyList<string> GpuAdapterLabels =>
        GpuOptions.Select(static option => option.Label).ToList();

    public string? TitleId
    {
        get => _titleId;
        private set
        {
            if (SetProperty(ref _titleId, value))
                OnPropertyChanged(nameof(OverlayHeader));
        }
    }

    public string? GameTitle
    {
        get => _gameTitle;
        private set
        {
            if (SetProperty(ref _gameTitle, value))
                OnPropertyChanged(nameof(OverlayHeader));
        }
    }

    public string OverlayHeader =>
        string.IsNullOrWhiteSpace(GameTitle) ? "shadPS4 Custom Config" : $"{GameTitle} — Custom Config";

    public string? ConfigFilePath
    {
        get => _configFilePath;
        private set => SetProperty(ref _configFilePath, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public int SelectedGpuAdapterIndex
    {
        get
        {
            for (var index = 0; index < GpuOptions.Count; index++)
            {
                if (GpuOptions[index].GpuId == GpuId)
                    return index;
            }

            return 0;
        }
        set
        {
            if (value < 0 || value >= GpuOptions.Count)
                return;

            GpuId = GpuOptions[value].GpuId;
            OnPropertyChanged();
        }
    }

    public string PresentModeLabel
    {
        get => ShadPs4CustomConfigService.PresentModeLabelForValue(PresentMode);
        set => PresentMode = ShadPs4CustomConfigService.PresentModeValueForLabel(value);
    }

    public string TrophyNotificationSideLabel
    {
        get => ShadPs4CustomConfigService.TrophySideLabelForValue(TrophyNotificationSide);
        set => TrophyNotificationSide = ShadPs4CustomConfigService.TrophySideValueForLabel(value);
    }

    public async Task LoadAsync(string? emulatorDirectory, string gamePath, string? gameTitle)
    {
        IsOpen = true;
        IsBusy = true;
        IsDirty = false;
        _emulatorDirectory = emulatorDirectory;
        GameTitle = gameTitle;
        TitleId = null;
        ConfigFilePath = null;
        Status = "Detecting PS4 Title ID and loading custom config...";

        try
        {
            var titleId = await Task.Run(() => ShadPs4TitleIdResolver.Resolve(gamePath)).ConfigureAwait(true);
            TitleId = titleId;

            if (string.IsNullOrWhiteSpace(titleId))
            {
                Status = "Unable to detect PS4 Title ID for the selected game.";
                return;
            }

            var audioDevices = await Task.Run(() => ShadPs4CustomConfigService.GetAudioDeviceNames(emulatorDirectory)).ConfigureAwait(true);
            AudioDevices = audioDevices;
            OnPropertyChanged(nameof(AudioDevices));

            GpuOptions = await Task.Run(ShadPs4CustomConfigService.GetGpuOptions).ConfigureAwait(true);
            OnPropertyChanged(nameof(GpuOptions));
            OnPropertyChanged(nameof(GpuAdapterLabels));
            OnPropertyChanged(nameof(SelectedGpuAdapterIndex));

            var document = await Task.Run(() => ShadPs4CustomConfigService.LoadOrDefault(emulatorDirectory, titleId)).ConfigureAwait(true);
            ApplyDocument(document);

            ConfigFilePath = ShadPs4CustomConfigService.GetConfigFilePath(emulatorDirectory, titleId);
            Status = File.Exists(ConfigFilePath)
                ? $"Loaded custom config for {titleId}."
                : $"No custom config found for {titleId}. Defaults are shown.";
            IsDirty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MarkDirty() => IsDirty = true;

    partial void OnAudioBackendChanged(int value) => MarkDirty();
    partial void OnSdlMainOutputDeviceChanged(string value) => MarkDirty();
    partial void OnSdlMicDeviceChanged(string value) => MarkDirty();
    partial void OnSdlPadSpkOutputDeviceChanged(string value) => MarkDirty();
    partial void OnOpenalMainOutputDeviceChanged(string value) => MarkDirty();
    partial void OnOpenalMicDeviceChanged(string value) => MarkDirty();
    partial void OnOpenalPadSpkOutputDeviceChanged(string value) => MarkDirty();
    partial void OnDebugDumpChanged(bool value) => MarkDirty();
    partial void OnShaderCollectChanged(bool value) => MarkDirty();
    partial void OnCopyGpuBuffersChanged(bool value) => MarkDirty();
    partial void OnDirectMemoryAccessEnabledChanged(bool value) => MarkDirty();
    partial void OnDumpShadersChanged(bool value) => MarkDirty();
    partial void OnFsrEnabledChanged(bool value) => MarkDirty();
    partial void OnFullScreenChanged(bool value) => MarkDirty();
    partial void OnFullScreenModeChanged(string value) => MarkDirty();
    partial void OnHdrAllowedChanged(bool value) => MarkDirty();
    partial void OnNullGpuChanged(bool value) => MarkDirty();
    partial void OnPatchShadersChanged(bool value) => MarkDirty();
    partial void OnRcasAttenuationChanged(int value) => MarkDirty();
    partial void OnRcasEnabledChanged(bool value) => MarkDirty();
    partial void OnReadbackLinearImagesEnabledChanged(bool value) => MarkDirty();
    partial void OnReadbacksModeChanged(int value) => MarkDirty();
    partial void OnVblankFrequencyChanged(int value) => MarkDirty();
    partial void OnSelectedResolutionChanged(string value) => MarkDirty();
    partial void OnConnectedToNetworkChanged(bool value) => MarkDirty();
    partial void OnDevKitModeChanged(bool value) => MarkDirty();
    partial void OnExtraDmemInMbytesChanged(int value) => MarkDirty();
    partial void OnNeoModeChanged(bool value) => MarkDirty();
    partial void OnPsnSignedInChanged(bool value) => MarkDirty();
    partial void OnShowSplashChanged(bool value) => MarkDirty();
    partial void OnTrophyNotificationDurationChanged(double value) => MarkDirty();
    partial void OnTrophyPopupDisabledChanged(bool value) => MarkDirty();
    partial void OnVolumeSliderChanged(int value) => MarkDirty();
    partial void OnBackgroundControllerInputChanged(bool value) => MarkDirty();
    partial void OnCameraIdChanged(int value) => MarkDirty();
    partial void OnCursorHideTimeoutChanged(int value) => MarkDirty();
    partial void OnCursorStateChanged(int value) => MarkDirty();
    partial void OnMotionControlsEnabledChanged(bool value) => MarkDirty();
    partial void OnUsbDeviceBackendChanged(int value) => MarkDirty();
    partial void OnLogAppendChanged(bool value) => MarkDirty();
    partial void OnLogEnableChanged(bool value) => MarkDirty();
    partial void OnLogFilterChanged(string value) => MarkDirty();
    partial void OnLogMaxSkipDurationChanged(int value) => MarkDirty();
    partial void OnLogSeparateChanged(bool value) => MarkDirty();
    partial void OnLogSizeLimitChanged(long value) => MarkDirty();
    partial void OnLogSkipDuplicateChanged(bool value) => MarkDirty();
    partial void OnLogSyncChanged(bool value) => MarkDirty();
    partial void OnLogTypeChanged(string value) => MarkDirty();
    partial void OnGpuIdChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedGpuAdapterIndex));
        MarkDirty();
    }

    partial void OnPresentModeChanged(string value)
    {
        OnPropertyChanged(nameof(PresentModeLabel));
        MarkDirty();
    }

    partial void OnTrophyNotificationSideChanged(string value)
    {
        OnPropertyChanged(nameof(TrophyNotificationSideLabel));
        MarkDirty();
    }
    partial void OnPipelineCacheArchivedChanged(bool value) => MarkDirty();
    partial void OnPipelineCacheEnabledChanged(bool value) => MarkDirty();
    partial void OnRenderdocEnabledChanged(bool value) => MarkDirty();
    partial void OnVkcrashDiagnosticEnabledChanged(bool value) => MarkDirty();
    partial void OnVkguestMarkersChanged(bool value) => MarkDirty();
    partial void OnVkhostMarkersChanged(bool value) => MarkDirty();
    partial void OnVkvalidationCoreEnabledChanged(bool value) => MarkDirty();
    partial void OnVkvalidationEnabledChanged(bool value) => MarkDirty();
    partial void OnVkvalidationGpuEnabledChanged(bool value) => MarkDirty();
    partial void OnVkvalidationSyncEnabledChanged(bool value) => MarkDirty();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(TitleId))
        {
            Status = "Cannot save without a detected Title ID.";
            return;
        }

        IsBusy = true;
        try
        {
            var document = BuildDocument();
            await Task.Run(() => ShadPs4CustomConfigService.Save(_emulatorDirectory, TitleId, document)).ConfigureAwait(true);
            ConfigFilePath = ShadPs4CustomConfigService.GetConfigFilePath(_emulatorDirectory, TitleId);
            Status = $"Saved custom config for {TitleId}.";
            IsDirty = false;
            IsOpen = false;
        }
        catch (Exception ex)
        {
            Status = $"Failed to save custom config: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    public void Reset()
    {
        IsOpen = false;
        IsBusy = false;
        IsDirty = false;
        TitleId = null;
        GameTitle = null;
        ConfigFilePath = null;
        _emulatorDirectory = null;
        Status = "Select a PlayStation 4 game to edit custom config.";
        ApplyDocument(ShadPs4CustomConfigService.CreateDefault());
    }

    private void ApplyDocument(ShadPs4CustomConfigDocument document)
    {
        AudioBackend = document.Audio.AudioBackend;
        SdlMainOutputDevice = document.Audio.SdlMainOutputDevice;
        SdlMicDevice = document.Audio.SdlMicDevice;
        SdlPadSpkOutputDevice = document.Audio.SdlPadSpkOutputDevice;
        OpenalMainOutputDevice = document.Audio.OpenalMainOutputDevice;
        OpenalMicDevice = document.Audio.OpenalMicDevice;
        OpenalPadSpkOutputDevice = document.Audio.OpenalPadSpkOutputDevice;

        DebugDump = document.Debug.DebugDump;
        ShaderCollect = document.Debug.ShaderCollect;

        CopyGpuBuffers = document.Gpu.CopyGpuBuffers;
        DirectMemoryAccessEnabled = document.Gpu.DirectMemoryAccessEnabled;
        DumpShaders = document.Gpu.DumpShaders;
        FsrEnabled = document.Gpu.FsrEnabled;
        FullScreen = document.Gpu.FullScreen;
        FullScreenMode = document.Gpu.FullScreenMode;
        HdrAllowed = document.Gpu.HdrAllowed;
        NullGpu = document.Gpu.NullGpu;
        PatchShaders = document.Gpu.PatchShaders;
        PresentMode = document.Gpu.PresentMode;
        RcasAttenuation = document.Gpu.RcasAttenuation;
        RcasEnabled = document.Gpu.RcasEnabled;
        ReadbackLinearImagesEnabled = document.Gpu.ReadbackLinearImagesEnabled;
        ReadbacksMode = document.Gpu.ReadbacksMode;
        VblankFrequency = document.Gpu.VblankFrequency;
        SelectedResolution = ShadPs4CustomConfigService.FindMatchingResolutionPreset(
            document.Gpu.WindowWidth,
            document.Gpu.WindowHeight);

        ConnectedToNetwork = document.General.ConnectedToNetwork;
        DevKitMode = document.General.DevKitMode;
        ExtraDmemInMbytes = document.General.ExtraDmemInMbytes;
        NeoMode = document.General.NeoMode;
        PsnSignedIn = document.General.PsnSignedIn;
        ShowSplash = document.General.ShowSplash;
        TrophyNotificationDuration = document.General.TrophyNotificationDuration;
        TrophyNotificationSide = document.General.TrophyNotificationSide;
        TrophyPopupDisabled = document.General.TrophyPopupDisabled;
        VolumeSlider = document.General.VolumeSlider;

        BackgroundControllerInput = document.Input.BackgroundControllerInput;
        CameraId = document.Input.CameraId;
        CursorHideTimeout = document.Input.CursorHideTimeout;
        CursorState = document.Input.CursorState;
        MotionControlsEnabled = document.Input.MotionControlsEnabled;
        UsbDeviceBackend = document.Input.UsbDeviceBackend;

        LogAppend = document.Log.Append;
        LogEnable = document.Log.Enable;
        LogFilter = document.Log.Filter;
        LogMaxSkipDuration = document.Log.MaxSkipDuration;
        LogSeparate = document.Log.Separate;
        LogSizeLimit = document.Log.SizeLimit;
        LogSkipDuplicate = document.Log.SkipDuplicate;
        LogSync = document.Log.Sync;
        LogType = document.Log.Type;

        GpuId = document.Vulkan.GpuId;
        PipelineCacheArchived = document.Vulkan.PipelineCacheArchived;
        PipelineCacheEnabled = document.Vulkan.PipelineCacheEnabled;
        RenderdocEnabled = document.Vulkan.RenderdocEnabled;
        VkcrashDiagnosticEnabled = document.Vulkan.VkcrashDiagnosticEnabled;
        VkguestMarkers = document.Vulkan.VkguestMarkers;
        VkhostMarkers = document.Vulkan.VkhostMarkers;
        VkvalidationCoreEnabled = document.Vulkan.VkvalidationCoreEnabled;
        VkvalidationEnabled = document.Vulkan.VkvalidationEnabled;
        VkvalidationGpuEnabled = document.Vulkan.VkvalidationGpuEnabled;
        VkvalidationSyncEnabled = document.Vulkan.VkvalidationSyncEnabled;

        OnPropertyChanged(nameof(SelectedGpuAdapterIndex));
        OnPropertyChanged(nameof(PresentModeLabel));
        OnPropertyChanged(nameof(TrophyNotificationSideLabel));
        IsDirty = false;
    }

    private ShadPs4CustomConfigDocument BuildDocument()
    {
        if (!ShadPs4CustomConfigService.TryParseResolution(SelectedResolution, out var width, out var height))
        {
            width = 1280;
            height = 720;
        }

        return new ShadPs4CustomConfigDocument
        {
            Audio = new ShadPs4AudioConfig
            {
                AudioBackend = AudioBackend,
                SdlMainOutputDevice = SdlMainOutputDevice,
                SdlMicDevice = SdlMicDevice,
                SdlPadSpkOutputDevice = SdlPadSpkOutputDevice,
                OpenalMainOutputDevice = OpenalMainOutputDevice,
                OpenalMicDevice = OpenalMicDevice,
                OpenalPadSpkOutputDevice = OpenalPadSpkOutputDevice
            },
            Debug = new ShadPs4DebugConfig
            {
                DebugDump = DebugDump,
                ShaderCollect = ShaderCollect
            },
            Gpu = new ShadPs4GpuConfig
            {
                CopyGpuBuffers = CopyGpuBuffers,
                DirectMemoryAccessEnabled = DirectMemoryAccessEnabled,
                DumpShaders = DumpShaders,
                FsrEnabled = FsrEnabled,
                FullScreen = FullScreen,
                FullScreenMode = FullScreenMode,
                HdrAllowed = HdrAllowed,
                NullGpu = NullGpu,
                PatchShaders = PatchShaders,
                PresentMode = PresentMode,
                RcasAttenuation = RcasAttenuation,
                RcasEnabled = RcasEnabled,
                ReadbackLinearImagesEnabled = ReadbackLinearImagesEnabled,
                ReadbacksMode = ReadbacksMode,
                VblankFrequency = VblankFrequency,
                WindowWidth = width,
                WindowHeight = height
            },
            General = new ShadPs4GeneralConfig
            {
                ConnectedToNetwork = ConnectedToNetwork,
                DevKitMode = DevKitMode,
                ExtraDmemInMbytes = ExtraDmemInMbytes,
                NeoMode = NeoMode,
                PsnSignedIn = PsnSignedIn,
                ShowSplash = ShowSplash,
                TrophyNotificationDuration = TrophyNotificationDuration,
                TrophyNotificationSide = TrophyNotificationSide,
                TrophyPopupDisabled = TrophyPopupDisabled,
                VolumeSlider = VolumeSlider
            },
            Input = new ShadPs4InputConfig
            {
                BackgroundControllerInput = BackgroundControllerInput,
                CameraId = CameraId,
                CursorHideTimeout = CursorHideTimeout,
                CursorState = CursorState,
                MotionControlsEnabled = MotionControlsEnabled,
                UsbDeviceBackend = UsbDeviceBackend
            },
            Log = new ShadPs4LogConfig
            {
                Append = LogAppend,
                Enable = LogEnable,
                Filter = LogFilter,
                MaxSkipDuration = LogMaxSkipDuration,
                Separate = LogSeparate,
                SizeLimit = LogSizeLimit,
                SkipDuplicate = LogSkipDuplicate,
                Sync = LogSync,
                Type = LogType
            },
            Vulkan = new ShadPs4VulkanConfig
            {
                GpuId = GpuId,
                PipelineCacheArchived = PipelineCacheArchived,
                PipelineCacheEnabled = PipelineCacheEnabled,
                RenderdocEnabled = RenderdocEnabled,
                VkcrashDiagnosticEnabled = VkcrashDiagnosticEnabled,
                VkguestMarkers = VkguestMarkers,
                VkhostMarkers = VkhostMarkers,
                VkvalidationCoreEnabled = VkvalidationCoreEnabled,
                VkvalidationEnabled = VkvalidationEnabled,
                VkvalidationGpuEnabled = VkvalidationGpuEnabled,
                VkvalidationSyncEnabled = VkvalidationSyncEnabled
            }
        };
    }
}
