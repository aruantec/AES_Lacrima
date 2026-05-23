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
    }
}
