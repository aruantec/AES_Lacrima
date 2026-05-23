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
        // --- RPCS3 Patches ---

        [RelayCommand]
        private async Task OpenCurrentSectionRpcs3Cheats(object? parameter)
        {
            if (!ShowCurrentSectionRpcs3CheatsMenuItem)
                return;

            await OpenCurrentSectionRpcs3PatchesCore(parameter, Rpcs3PatchCatalog.ArtemisCheats).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task OpenCurrentSectionRpcs3Patches(object? parameter)
        {
            if (!ShowCurrentSectionRpcs3PatchesMenuItem)
                return;

            await OpenCurrentSectionRpcs3PatchesCore(parameter, Rpcs3PatchCatalog.Official).ConfigureAwait(false);
        }

        private async Task OpenCurrentSectionRpcs3PatchesCore(object? parameter, Rpcs3PatchCatalog catalog)
        {
            var target = ResolveRpcs3ContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            _rpcs3ActivePatchCatalog = catalog;
            OnPropertyChanged(nameof(IsRpcs3CheatsOverlayMode));
            OnPropertyChanged(nameof(Rpcs3PatchOverlayHeader));
            OnPropertyChanged(nameof(Rpcs3PatchesSectionLabel));
            OnPropertyChanged(nameof(Rpcs3DownloadPatchesButtonText));

            IsRpcs3PatchesOverlayOpen = true;
            IsRpcs3PatchesBusy = true;
            Rpcs3PatchesStatus = catalog == Rpcs3PatchCatalog.ArtemisCheats
                ? "Detecting PS3 Title ID and loading Artemis cheats..."
                : "Detecting PS3 Title ID and loading patches...";
            Rpcs3DetectedTitleId = null;
            Rpcs3DetectedAppVersion = null;
            Rpcs3PatchGameTitle = target.Title;
            DetachRpcs3PatchEntryListeners();
            CurrentSectionRpcs3PatchEntries.Clear();
            IsCurrentSectionRpcs3PatchDirty = false;

            try
            {
            var emulatorDirectory = Rpcs3PatchesService.ResolveEmulatorDirectory(
                CurrentSectionRpcs3EmulatorPath,
                CurrentSectionEmulatorHandler?.LauncherPath);

            var titleId = Rpcs3CustomConfigService.NormalizeTitleId(
                Ps3InstalledGameHelper.ResolveTitleId(target.FileName));
                var appVersion = Ps3InstalledGameHelper.GetVersion(target.FileName);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Rpcs3DetectedTitleId = titleId;
                    Rpcs3DetectedAppVersion = appVersion;
                });

                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Rpcs3PatchesStatus = "Unable to detect PS3 Title ID for the selected game.";
                    });
                    return;
                }

                await LoadCurrentSectionRpcs3PatchesAsync(emulatorDirectory, titleId, catalog, appVersion)
                    .ConfigureAwait(false);

                if (catalog == Rpcs3PatchCatalog.ArtemisCheats &&
                    CurrentSectionRpcs3PatchEntries.Count == 0 &&
                    !string.IsNullOrWhiteSpace(emulatorDirectory))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Rpcs3PatchesStatus = "Downloading Artemis cheats for this game...";
                    });

                    var downloadResult = await Rpcs3ArtemisCheatsDownloadService
                        .DownloadForTitleIdAsync(emulatorDirectory, titleId)
                        .ConfigureAwait(false);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Rpcs3PatchesStatus = downloadResult.Message;
                    });

                    if (downloadResult.Success)
                        await LoadCurrentSectionRpcs3PatchesAsync(emulatorDirectory, titleId, catalog).ConfigureAwait(false);
                }
            }
            finally
            {
                IsRpcs3PatchesBusy = false;
            }
        }

        [RelayCommand]
        private async Task DownloadCurrentSectionRpcs3Patches()
        {
            if (IsRpcs3PatchesBusy)
                return;

            var emulatorDirectory = Rpcs3PatchesService.ResolveEmulatorDirectory(
                CurrentSectionRpcs3EmulatorPath,
                CurrentSectionEmulatorHandler?.LauncherPath);

            if (string.IsNullOrWhiteSpace(emulatorDirectory))
            {
                Rpcs3PatchesStatus = "Emulator directory is not configured.";
                return;
            }

            IsRpcs3PatchesBusy = true;
            Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                ? "Downloading Artemis cheats..."
                : "Downloading latest RPCS3 patches...";

            try
            {
                if (IsRpcs3CheatsOverlayMode)
                {
                    if (string.IsNullOrWhiteSpace(Rpcs3DetectedTitleId))
                    {
                        Rpcs3PatchesStatus = "Unable to detect PS3 Title ID for the selected game.";
                        return;
                    }

                    var artemisResult = await Rpcs3ArtemisCheatsDownloadService
                        .DownloadForTitleIdAsync(emulatorDirectory, Rpcs3DetectedTitleId)
                        .ConfigureAwait(true);
                    Rpcs3PatchesStatus = artemisResult.Message;

                    if (!artemisResult.Success)
                        return;

                    if (!string.IsNullOrWhiteSpace(Rpcs3DetectedTitleId))
                    {
                        await LoadCurrentSectionRpcs3PatchesAsync(
                            emulatorDirectory,
                            Rpcs3DetectedTitleId,
                            _rpcs3ActivePatchCatalog).ConfigureAwait(true);
                    }
                }
                else
                {
                    var result = await Rpcs3PatchesDownloadService.DownloadLatestAsync(emulatorDirectory)
                        .ConfigureAwait(true);
                    Rpcs3PatchesStatus = result.Message;

                    if (!result.Success)
                        return;

                    if (!string.IsNullOrWhiteSpace(Rpcs3DetectedTitleId))
                    {
                        await LoadCurrentSectionRpcs3PatchesAsync(
                            emulatorDirectory,
                            Rpcs3DetectedTitleId,
                            _rpcs3ActivePatchCatalog).ConfigureAwait(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? $"Failed to download Artemis cheats: {ex.Message}"
                    : $"Failed to download patches: {ex.Message}";
            }
            finally
            {
                IsRpcs3PatchesBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveCurrentSectionRpcs3Patches()
        {
            var saved = await SaveCurrentSectionRpcs3PatchesCore().ConfigureAwait(false);
            if (saved)
                CloseCurrentSectionRpcs3Patches();
        }

        private async Task<bool> SaveCurrentSectionRpcs3PatchesCore()
        {
            if (!IsRpcs3PatchesOverlayOpen)
                return false;

            if (CurrentSectionRpcs3PatchEntries.Count == 0)
            {
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? "No cheats loaded to save."
                    : "No patches loaded to save.";
                return false;
            }

            var emulatorDirectory = Rpcs3PatchesService.ResolveEmulatorDirectory(
                CurrentSectionRpcs3EmulatorPath,
                CurrentSectionEmulatorHandler?.LauncherPath);

            if (string.IsNullOrWhiteSpace(emulatorDirectory))
            {
                Rpcs3PatchesStatus = "Emulator directory is not configured.";
                return false;
            }

            IsRpcs3PatchesBusy = true;
            try
            {
                var toggles = CurrentSectionRpcs3PatchEntries
                    .Select(static entry => new Rpcs3PatchToggle(
                        entry.EntryKey,
                        entry.PpuHash,
                        entry.Name,
                        entry.GameTitle,
                        entry.Serial,
                        entry.AppVersion,
                        entry.IsEnabled))
                    .ToArray();

                await Task.Run(() => Rpcs3PatchesService.SaveEnabledStates(emulatorDirectory, toggles)).ConfigureAwait(false);
                IsCurrentSectionRpcs3PatchDirty = false;
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? "Cheat settings saved."
                    : "Patch settings saved.";
                return true;
            }
            catch (Exception ex)
            {
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? $"Failed to save cheats: {ex.Message}"
                    : $"Failed to save patches: {ex.Message}";
                return false;
            }
            finally
            {
                IsRpcs3PatchesBusy = false;
            }
        }

        [RelayCommand]
        private void SelectAllCurrentSectionRpcs3Patches()
        {
            if (IsRpcs3PatchesBusy)
                return;

            foreach (var entry in CurrentSectionRpcs3PatchEntries)
                entry.IsEnabled = true;

            if (CurrentSectionRpcs3PatchEntries.Count > 0)
            {
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? $"Enabled {CurrentSectionRpcs3PatchEntries.Count} cheat(s)."
                    : $"Enabled {CurrentSectionRpcs3PatchEntries.Count} patch(es).";
            }
        }

        [RelayCommand]
        private void UnselectAllCurrentSectionRpcs3Patches()
        {
            if (IsRpcs3PatchesBusy)
                return;

            foreach (var entry in CurrentSectionRpcs3PatchEntries)
                entry.IsEnabled = false;

            if (CurrentSectionRpcs3PatchEntries.Count > 0)
            {
                Rpcs3PatchesStatus = IsRpcs3CheatsOverlayMode
                    ? $"Disabled {CurrentSectionRpcs3PatchEntries.Count} cheat(s)."
                    : $"Disabled {CurrentSectionRpcs3PatchEntries.Count} patch(es).";
            }
        }

        [RelayCommand]
        private void CloseCurrentSectionRpcs3Patches()
        {
            IsRpcs3PatchesOverlayOpen = false;
            DetachRpcs3PatchEntryListeners();
            CurrentSectionRpcs3PatchEntries.Clear();
            IsCurrentSectionRpcs3PatchDirty = false;
        }

        private async Task LoadCurrentSectionRpcs3PatchesAsync(
            string? emulatorDirectory,
            string titleId,
            Rpcs3PatchCatalog? catalog = null,
            string? appVersion = null)
        {
            var activeCatalog = catalog ?? _rpcs3ActivePatchCatalog;
            var patchPath = Rpcs3PatchesService.GetPatchYmlPath(emulatorDirectory, activeCatalog);
            var entryLabel = activeCatalog == Rpcs3PatchCatalog.ArtemisCheats ? "cheat" : "patch";
            var resolvedAppVersion = appVersion ?? Rpcs3DetectedAppVersion;

            var loadResult = await Task.Run(() =>
            {
                var success = Rpcs3PatchesService.TryGetPatchesForTitleId(
                    emulatorDirectory,
                    titleId,
                    resolvedAppVersion,
                    activeCatalog,
                    out var definitions,
                    out var errorMessage);
                return (success, definitions, errorMessage);
            }).ConfigureAwait(false);

            var configPath = Rpcs3PatchesService.GetPatchConfigPath(emulatorDirectory);
            var enabledMap = await Task.Run(() => Rpcs3PatchesService.BuildEnabledStateMap(loadResult.definitions, configPath))
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DetachRpcs3PatchEntryListeners();
                CurrentSectionRpcs3PatchEntries.Clear();
                IsCurrentSectionRpcs3PatchDirty = false;

                foreach (var definition in loadResult.definitions)
                {
                    var entryKey = Rpcs3PatchesService.BuildEntryKey(definition);
                    enabledMap.TryGetValue(entryKey, out var isEnabled);

                    var entry = new Rpcs3PatchEntry(
                        isEnabled,
                        entryKey,
                        definition.PpuHash,
                        definition.Name,
                        definition.GameTitle,
                        definition.Serial,
                        definition.AppVersion,
                        BuildRpcs3PatchSubtitle(definition));

                    entry.PropertyChanged += OnRpcs3PatchEntryPropertyChanged;
                    CurrentSectionRpcs3PatchEntries.Add(entry);
                }

                if (!loadResult.success)
                {
                    Rpcs3PatchesStatus = loadResult.errorMessage ??
                                         (activeCatalog == Rpcs3PatchCatalog.ArtemisCheats
                                             ? "Failed to load Artemis cheats."
                                             : "Failed to load patches.");
                }
                else if (!Rpcs3PatchesService.PatchFileExists(emulatorDirectory, activeCatalog))
                {
                    Rpcs3PatchesStatus = activeCatalog == Rpcs3PatchCatalog.ArtemisCheats
                        ? "No artemis_cheats.yml found. Download Artemis cheats to get started."
                        : "No patch.yml found. Download patches to get started.";
                }
                else if (CurrentSectionRpcs3PatchEntries.Count == 0)
                {
                    Rpcs3PatchesStatus = activeCatalog == Rpcs3PatchCatalog.ArtemisCheats
                        ? $"No Artemis cheats found for title ID {titleId} in '{patchPath}'. Download cheats for this game."
                        : $"No patches found for title ID {titleId} in '{patchPath}'.";
                }
                else
                {
                    var gameCount = CurrentSectionRpcs3PatchEntries
                        .Select(static e => e.GameTitle)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();
                    Rpcs3PatchesStatus = gameCount > 1
                        ? $"Loaded {CurrentSectionRpcs3PatchEntries.Count} {entryLabel}(es) for title ID {titleId} across {gameCount} game entries."
                        : $"Loaded {CurrentSectionRpcs3PatchEntries.Count} {entryLabel}(es) for title ID {titleId}.";
                }
            });
        }

    }
}
