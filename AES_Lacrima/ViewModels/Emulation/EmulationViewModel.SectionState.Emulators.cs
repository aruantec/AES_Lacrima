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

        public bool IsRpcs3PatchesOverlayOpen
        {
            get => _isRpcs3PatchesOverlayOpen;
            set
            {
                if (_isRpcs3PatchesOverlayOpen == value)
                    return;

                _isRpcs3PatchesOverlayOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsRpcs3PatchesBusy
        {
            get => _isRpcs3PatchesBusy;
            set
            {
                if (_isRpcs3PatchesBusy == value)
                    return;

                _isRpcs3PatchesBusy = value;
                OnPropertyChanged();
            }
        }

        public string Rpcs3PatchesStatus
        {
            get => _rpcs3PatchesStatus;
            set
            {
                if (string.Equals(_rpcs3PatchesStatus, value, StringComparison.Ordinal))
                    return;

                _rpcs3PatchesStatus = value;
                OnPropertyChanged();
            }
        }

        public string? Rpcs3PatchGameTitle
        {
            get => _rpcs3PatchGameTitle;
            set
            {
                if (SetProperty(ref _rpcs3PatchGameTitle, value))
                    OnPropertyChanged(nameof(Rpcs3PatchOverlayHeader));
            }
        }

        public bool IsRpcs3CheatsOverlayMode =>
            _rpcs3ActivePatchCatalog == Rpcs3PatchCatalog.ArtemisCheats;

        public string Rpcs3PatchOverlayHeader =>
            string.IsNullOrWhiteSpace(Rpcs3PatchGameTitle)
                ? IsRpcs3CheatsOverlayMode ? "RPCS3 Cheats" : "RPCS3 Patches"
                : IsRpcs3CheatsOverlayMode
                    ? $"{Rpcs3PatchGameTitle} Cheats"
                    : $"{Rpcs3PatchGameTitle} Patches";

        public string Rpcs3PatchesSectionLabel =>
            IsRpcs3CheatsOverlayMode ? "Cheats" : "Patches";

        public string Rpcs3DownloadPatchesButtonText =>
            IsRpcs3CheatsOverlayMode ? "Download Artemis Cheats" : "Download Patches";

        public bool IsRpcs3ArtemisImportOverlayOpen
        {
            get => _isRpcs3ArtemisImportOverlayOpen;
            set
            {
                if (_isRpcs3ArtemisImportOverlayOpen == value)
                    return;

                _isRpcs3ArtemisImportOverlayOpen = value;
                OnPropertyChanged();
            }
        }

        public string Rpcs3ArtemisImportText
        {
            get => _rpcs3ArtemisImportText;
            set
            {
                if (string.Equals(_rpcs3ArtemisImportText, value, StringComparison.Ordinal))
                    return;

                _rpcs3ArtemisImportText = value;
                OnPropertyChanged();
            }
        }

        public string Rpcs3ArtemisImportStatus
        {
            get => _rpcs3ArtemisImportStatus;
            set
            {
                if (string.Equals(_rpcs3ArtemisImportStatus, value, StringComparison.Ordinal))
                    return;

                _rpcs3ArtemisImportStatus = value;
                OnPropertyChanged();
            }
        }

        public string? Rpcs3DetectedTitleId
        {
            get => _rpcs3DetectedTitleId;
            private set
            {
                if (string.Equals(_rpcs3DetectedTitleId, value, StringComparison.Ordinal))
                    return;

                _rpcs3DetectedTitleId = value;
                OnPropertyChanged();
            }
        }

        public string? Rpcs3DetectedAppVersion
        {
            get => _rpcs3DetectedAppVersion;
            private set
            {
                if (string.Equals(_rpcs3DetectedAppVersion, value, StringComparison.Ordinal))
                    return;

                _rpcs3DetectedAppVersion = value;
                OnPropertyChanged();
            }
        }

        public string? Rpcs3SelectedGamePath
        {
            get => _rpcs3SelectedGamePath;
            private set
            {
                if (string.Equals(_rpcs3SelectedGamePath, value, StringComparison.Ordinal))
                    return;

                _rpcs3SelectedGamePath = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<Rpcs3PatchEntry> CurrentSectionRpcs3PatchEntries => _currentSectionRpcs3PatchEntries;

        public bool IsCurrentSectionRpcs3PatchDirty
        {
            get => _isCurrentSectionRpcs3PatchDirty;
            set
            {
                if (_isCurrentSectionRpcs3PatchDirty == value)
                    return;

                _isCurrentSectionRpcs3PatchDirty = value;
                OnPropertyChanged();
            }
        }

        public bool IsCemuGraphicPacksOverlayOpen
        {
            get => _isCemuGraphicPacksOverlayOpen;
            set
            {
                if (_isCemuGraphicPacksOverlayOpen == value)
                    return;

                _isCemuGraphicPacksOverlayOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsCemuGraphicPacksBusy
        {
            get => _isCemuGraphicPacksBusy;
            set
            {
                if (_isCemuGraphicPacksBusy == value)
                    return;

                _isCemuGraphicPacksBusy = value;
                OnPropertyChanged();
            }
        }

        public string CemuGraphicPacksStatus
        {
            get => _cemuGraphicPacksStatus;
            set
            {
                if (string.Equals(_cemuGraphicPacksStatus, value, StringComparison.Ordinal))
                    return;

                _cemuGraphicPacksStatus = value;
                OnPropertyChanged();
            }
        }

        public string? CemuGraphicPackGameTitle
        {
            get => _cemuGraphicPackGameTitle;
            set
            {
                if (SetProperty(ref _cemuGraphicPackGameTitle, value))
                    OnPropertyChanged(nameof(CemuGraphicPackOverlayHeader));
            }
        }

        public string CemuGraphicPackOverlayHeader =>
            string.IsNullOrWhiteSpace(CemuGraphicPackGameTitle) ? "Cemu Graphic Packs" : $"{CemuGraphicPackGameTitle} Graphic Packs";

        public string? CemuDetectedTitleId
        {
            get => _cemuDetectedTitleId;
            private set
            {
                if (string.Equals(_cemuDetectedTitleId, value, StringComparison.Ordinal))
                    return;

                _cemuDetectedTitleId = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<CemuGraphicPackEntry> CurrentSectionCemuGraphicPackEntries => _currentSectionCemuGraphicPackEntries;

        public bool IsCurrentSectionCemuGraphicPackDirty
        {
            get => _isCurrentSectionCemuGraphicPackDirty;
            set
            {
                if (_isCurrentSectionCemuGraphicPackDirty == value)
                    return;

                _isCurrentSectionCemuGraphicPackDirty = value;
                OnPropertyChanged();
            }
        }

        public AvaloniaList<XeniaPatchFileItem> CurrentSectionXeniaPatchFiles => _currentSectionXeniaPatchFiles;

        public string XeniaPatchOverlayHeader =>
            string.IsNullOrWhiteSpace(_xeniaPatchOverlayGameTitle)
                ? "Xenia Patches"
                : $"{_xeniaPatchOverlayGameTitle} \u2014 Patches";

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

        partial void OnSelectedCurrentSectionRetroArchCoreItemChanged(RetroArchCoreItem? value)
        {
            if (value is { IsGroupHeader: true })
                return;

            var newCore = value?.FileName;
            if (!string.Equals(_selectedCurrentSectionRetroArchCore, newCore, StringComparison.OrdinalIgnoreCase))
                SelectedCurrentSectionRetroArchCore = newCore;
        }

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

        private void SyncCurrentSectionRetroArchCoreSelection()
        {
            var section = CurrentEmulationSectionItem;
            var coreName = section?.LaunchSettings?.SelectedRetroArchCore;

            RetroArchCoreItem? match = null;
            if (!string.IsNullOrWhiteSpace(coreName))
            {
                match = CurrentSectionRetroArchCores
                    .FirstOrDefault(c => string.Equals(c.FileName, coreName, StringComparison.OrdinalIgnoreCase) && !c.IsGroupHeader);
            }

            if (!ReferenceEquals(SelectedCurrentSectionRetroArchCoreItem, match))
            {
                _isSyncingCurrentSectionCoreSelection = true;
                SelectedCurrentSectionRetroArchCoreItem = match;
                _isSyncingCurrentSectionCoreSelection = false;
            }
        }
    }
}
