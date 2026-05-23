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
    }
}
