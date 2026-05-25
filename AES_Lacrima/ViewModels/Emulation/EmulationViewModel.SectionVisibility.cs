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
        public bool ShowCurrentSectionRetroArchCoreSelection =>
            CurrentSectionEmulatorHandler?.UsesRetroArchCores == true &&
            CurrentSectionRetroArchCores.Count > 0;

        public bool ShowCurrentSectionRetroArchUpdateControls =>
            CurrentSectionEmulatorHandler?.UsesRetroArchCores == true &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionEdenUpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, EdenHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionShadPs4UpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, ShadPs4Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionXeniaUpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, XeniaHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionRpcs3UpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, Rpcs3Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionPcsx2UpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionDolphinUpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, DolphinHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionFlycastUpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, FlyCastHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionDuckStationUpdateControls =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionCemuSection =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, CemuHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentEmulationSectionItem != null;

        public bool ShowCurrentSectionPcsx2SetupLaunchButton =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, Pcsx2Handler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentSectionEmulatorHandler.IsLauncherPathValid(CurrentSectionEmulatorHandler.LauncherPath) &&
            !IsEmulatorRunning &&
            !IsEmulatorLaunchInProgress;

        public bool ShowCurrentSectionDuckStationSetupLaunchButton =>
            CurrentSectionEmulatorHandler != null &&
            string.Equals(CurrentSectionEmulatorHandler.HandlerId, DuckStationHandler.Instance.HandlerId, StringComparison.OrdinalIgnoreCase) &&
            CurrentSectionEmulatorHandler.IsLauncherPathValid(CurrentSectionEmulatorHandler.LauncherPath) &&
            !IsEmulatorRunning &&
            !IsEmulatorLaunchInProgress;

        public Bitmap? CurrentSectionSetupLaunchIcon => ResolveCurrentSectionSetupLaunchIcon();

        public bool HasCurrentSectionSetupLaunchIcon => CurrentSectionSetupLaunchIcon != null;

        public string CurrentSectionSetupLaunchToolTip =>
            CurrentSectionEmulatorHandler?.DisplayName is { Length: > 0 } handlerName
                ? $"Launch {handlerName}"
                : "Launch emulator";

        public bool CanLaunchCurrentSectionHandlerSetup =>
            CurrentSectionEmulatorHandler != null &&
            CurrentSectionEmulatorHandler?.IsLauncherPathValid(CurrentSectionEmulatorHandler.LauncherPath) == true &&
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

        public bool ShowCurrentSectionRpcs3CustomConfigMenuItem =>
            ShowCurrentSectionRpcs3UpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionRpcs3PatchesMenuItem =>
            ShowCurrentSectionRpcs3UpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionRpcs3CheatsMenuItem =>
            ShowCurrentSectionRpcs3PatchesMenuItem;

        public bool ShowCurrentSectionDuckStationCheatsMenuItem =>
            ShowCurrentSectionDuckStationUpdateControls && HasActiveAlbumItems;

        public bool ShowCurrentSectionCemuGraphicPacksMenuItem =>
            ShowCurrentSectionCemuSection && HasActiveAlbumItems;

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
            OnPropertyChanged(nameof(CurrentSectionEmulatorHandler));
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
            OnPropertyChanged(nameof(ShowCurrentSectionRpcs3CustomConfigMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionRpcs3PatchesMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionRpcs3CheatsMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionDuckStationCheatsMenuItem));
            OnPropertyChanged(nameof(ShowCurrentSectionCemuGraphicPacksMenuItem));
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
                DuckStationCheatsEditor.ClearSession();
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

            if (!ShowCurrentSectionRpcs3UpdateControls)
            {
                IsRpcs3PatchesOverlayOpen = false;
                Rpcs3DetectedTitleId = null;
                Rpcs3DetectedAppVersion = null;
                Rpcs3PatchGameTitle = null;
                Rpcs3PatchesStatus = "Select a PlayStation 3 game to manage patches.";
                IsCurrentSectionRpcs3PatchDirty = false;
                DetachRpcs3PatchEntryListeners();
                CurrentSectionRpcs3PatchEntries.Clear();
                Rpcs3CustomConfigEditor.Reset();
            }

            if (!ShowCurrentSectionCemuSection)
            {
                IsCemuGraphicPacksOverlayOpen = false;
                CemuDetectedTitleId = null;
                CemuGraphicPackGameTitle = null;
                CemuGraphicPacksStatus = "Select a Wii U game to manage graphic packs.";
                IsCurrentSectionCemuGraphicPackDirty = false;
                DetachCemuGraphicPackEntryListeners();
                CurrentSectionCemuGraphicPackEntries.Clear();
            }
        }
    }
}
