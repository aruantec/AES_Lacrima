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

        [RelayCommand]
        private async Task OpenCurrentSectionRpcs3CustomConfig(object? parameter)
        {
            if (!ShowCurrentSectionRpcs3CustomConfigMenuItem)
                return;

            var target = ResolveRpcs3ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            var emulatorDirectory = !string.IsNullOrWhiteSpace(CurrentSectionRpcs3EmulatorPath)
                ? CurrentSectionRpcs3EmulatorPath
                : Rpcs3CustomConfigService.ResolveEmulatorDirectory(CurrentEmulatorHandler?.LauncherPath);

            await Rpcs3CustomConfigEditor.LoadAsync(
                emulatorDirectory,
                target.FileName,
                target.Title).ConfigureAwait(true);
        }

        // --- DuckStation Cheats ---

        [RelayCommand]
        private async Task OpenCurrentSectionDuckStationCheats(object? parameter)
        {
            if (!ShowCurrentSectionDuckStationCheatsMenuItem)
                return;

            var target = ResolveShadPs4ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            await DuckStationCheatsEditor.LoadAsync(
                CurrentSectionDuckStationEmulatorPath,
                target.FileName,
                target.Title).ConfigureAwait(true);
        }

    }
}
