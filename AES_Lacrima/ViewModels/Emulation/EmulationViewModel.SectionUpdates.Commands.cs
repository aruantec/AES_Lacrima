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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
            var handler = CurrentSectionEmulatorHandler;
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
    }
}
