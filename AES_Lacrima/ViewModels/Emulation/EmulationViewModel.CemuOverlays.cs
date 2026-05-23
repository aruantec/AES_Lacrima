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
        // --- Cemu Graphic Packs ---

        [RelayCommand]
        private async Task OpenCurrentSectionCemuGraphicPacks(object? parameter)
        {
            if (!ShowCurrentSectionCemuGraphicPacksMenuItem)
                return;

            var target = ResolveCemuContextMenuTarget(parameter);
            if (target == null || string.IsNullOrWhiteSpace(target.FileName))
                return;

            IsCemuGraphicPacksOverlayOpen = true;
            IsCemuGraphicPacksBusy = true;
            CemuGraphicPacksStatus = "Detecting Wii U Title ID and loading graphic packs...";
            CemuDetectedTitleId = null;
            CemuGraphicPackGameTitle = target.Title;
            DetachCemuGraphicPackEntryListeners();
            CurrentSectionCemuGraphicPackEntries.Clear();
            IsCurrentSectionCemuGraphicPackDirty = false;

            try
            {
                var emulatorDirectory = !string.IsNullOrWhiteSpace(CurrentSectionCemuEmulatorPath)
                    ? CurrentSectionCemuEmulatorPath
                    : CemuPathsService.ResolveEmulatorDirectory(null, CurrentSectionEmulatorHandler?.LauncherPath);

                var titleId = ResolveWiiUTitleId(target);
                await Dispatcher.UIThread.InvokeAsync(() => CemuDetectedTitleId = titleId);

                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        CemuGraphicPacksStatus = "Unable to detect Wii U Title ID for the selected game.");
                    return;
                }

                await LoadCurrentSectionCemuGraphicPacksAsync(emulatorDirectory, titleId).ConfigureAwait(false);
            }
            finally
            {
                IsCemuGraphicPacksBusy = false;
            }
        }

        [RelayCommand]
        private async Task DownloadCurrentSectionCemuGraphicPacks()
        {
            if (IsCemuGraphicPacksBusy)
                return;

            var emulatorDirectory = !string.IsNullOrWhiteSpace(CurrentSectionCemuEmulatorPath)
                ? CurrentSectionCemuEmulatorPath
                : CemuPathsService.ResolveEmulatorDirectory(null, CurrentSectionEmulatorHandler?.LauncherPath);

            if (string.IsNullOrWhiteSpace(emulatorDirectory))
            {
                CemuGraphicPacksStatus = "Emulator directory is not configured.";
                return;
            }

            IsCemuGraphicPacksBusy = true;
            CemuGraphicPacksStatus = "Downloading latest Cemu graphic packs...";

            try
            {
                var result = await CemuGraphicPacksDownloadService.DownloadLatestAsync(
                    emulatorDirectory,
                    CurrentSectionEmulatorHandler?.LauncherPath).ConfigureAwait(true);

                CemuGraphicPacksStatus = result.Message;
                if (!result.Success || string.IsNullOrWhiteSpace(CemuDetectedTitleId))
                    return;

                await LoadCurrentSectionCemuGraphicPacksAsync(emulatorDirectory, CemuDetectedTitleId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                CemuGraphicPacksStatus = $"Failed to download graphic packs: {ex.Message}";
            }
            finally
            {
                IsCemuGraphicPacksBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveCurrentSectionCemuGraphicPacks()
        {
            var saved = await SaveCurrentSectionCemuGraphicPacksCore().ConfigureAwait(false);
            if (saved)
                CloseCurrentSectionCemuGraphicPacks();
        }

        private async Task<bool> SaveCurrentSectionCemuGraphicPacksCore()
        {
            if (!IsCemuGraphicPacksOverlayOpen)
                return false;

            if (CurrentSectionCemuGraphicPackEntries.Count == 0)
            {
                CemuGraphicPacksStatus = "No graphic packs loaded to save.";
                return false;
            }

            var emulatorDirectory = !string.IsNullOrWhiteSpace(CurrentSectionCemuEmulatorPath)
                ? CurrentSectionCemuEmulatorPath
                : CemuPathsService.ResolveEmulatorDirectory(null, CurrentSectionEmulatorHandler?.LauncherPath);

            if (string.IsNullOrWhiteSpace(emulatorDirectory))
            {
                CemuGraphicPacksStatus = "Emulator directory is not configured.";
                return false;
            }

            IsCemuGraphicPacksBusy = true;
            try
            {
                var toggles = CurrentSectionCemuGraphicPackEntries
                    .Select(BuildCemuGraphicPackToggle)
                    .ToArray();

                await Task.Run(() => CemuGraphicPacksService.SaveEnabledStates(
                    emulatorDirectory,
                    CurrentSectionEmulatorHandler?.LauncherPath,
                    toggles)).ConfigureAwait(false);

                IsCurrentSectionCemuGraphicPackDirty = false;
                CemuGraphicPacksStatus = "Graphic pack settings saved.";
                return true;
            }
            catch (Exception ex)
            {
                CemuGraphicPacksStatus = $"Failed to save graphic packs: {ex.Message}";
                return false;
            }
            finally
            {
                IsCemuGraphicPacksBusy = false;
            }
        }

        [RelayCommand]
        private void SelectAllCurrentSectionCemuGraphicPacks()
        {
            if (IsCemuGraphicPacksBusy)
                return;

            foreach (var entry in CurrentSectionCemuGraphicPackEntries)
                entry.IsEnabled = true;

            if (CurrentSectionCemuGraphicPackEntries.Count > 0)
                CemuGraphicPacksStatus = $"Enabled {CurrentSectionCemuGraphicPackEntries.Count} graphic pack(s).";
        }

        [RelayCommand]
        private void UnselectAllCurrentSectionCemuGraphicPacks()
        {
            if (IsCemuGraphicPacksBusy)
                return;

            foreach (var entry in CurrentSectionCemuGraphicPackEntries)
                entry.IsEnabled = false;

            if (CurrentSectionCemuGraphicPackEntries.Count > 0)
                CemuGraphicPacksStatus = $"Disabled {CurrentSectionCemuGraphicPackEntries.Count} graphic pack(s).";
        }

        [RelayCommand]
        private void CloseCurrentSectionCemuGraphicPacks()
        {
            IsCemuGraphicPacksOverlayOpen = false;
            DetachCemuGraphicPackEntryListeners();
            CurrentSectionCemuGraphicPackEntries.Clear();
            IsCurrentSectionCemuGraphicPackDirty = false;
        }

        private async Task LoadCurrentSectionCemuGraphicPacksAsync(string? emulatorDirectory, string titleId)
        {
            var loadResult = await Task.Run(() =>
            {
                var success = CemuGraphicPacksService.TryGetGraphicPacksForTitleId(
                    emulatorDirectory,
                    CurrentSectionEmulatorHandler?.LauncherPath,
                    titleId,
                    out var packs,
                    out var errorMessage);
                return (success, packs, errorMessage);
            }).ConfigureAwait(false);

            string? settingsPath = null;
            if (CemuPathsService.TryResolveSettingsPath(emulatorDirectory, CurrentSectionEmulatorHandler?.LauncherPath, out var resolvedSettingsPath))
                settingsPath = resolvedSettingsPath;

            var enabledMap = await Task.Run(() =>
                CemuGraphicPacksService.BuildEnabledStateMap(loadResult.packs, settingsPath)).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DetachCemuGraphicPackEntryListeners();
                CurrentSectionCemuGraphicPackEntries.Clear();
                IsCurrentSectionCemuGraphicPackDirty = false;

                foreach (var pack in loadResult.packs)
                {
                    enabledMap.TryGetValue(pack.EntryKey, out var isEnabled);
                    var entry = new CemuGraphicPackEntry(
                        isEnabled,
                        pack.EntryKey,
                        pack.RelativeRulesPath,
                        pack.Name,
                        BuildCemuGraphicPackSubtitle(pack),
                        pack.PresetGroups.Select(static group => new CemuGraphicPackPresetGroupEntry(
                            group.Category,
                            group.CategoryLabel,
                            group.PresetNames,
                            group.SelectedPresetName)));

                    entry.PropertyChanged += OnCemuGraphicPackEntryPropertyChanged;
                    foreach (var presetGroup in entry.PresetGroups)
                        presetGroup.PropertyChanged += OnCemuGraphicPackPresetGroupPropertyChanged;

                    CurrentSectionCemuGraphicPackEntries.Add(entry);
                }

                if (!loadResult.success)
                {
                    CemuGraphicPacksStatus = loadResult.errorMessage ?? "Failed to load graphic packs.";
                }
                else if (CurrentSectionCemuGraphicPackEntries.Count == 0)
                {
                    CemuGraphicPacksStatus = $"No graphic packs found for title ID {titleId}. Download packs to get started.";
                }
                else
                {
                    CemuGraphicPacksStatus = $"Loaded {CurrentSectionCemuGraphicPackEntries.Count} graphic pack(s) for title ID {titleId}.";
                }
            });
        }

        private static string? BuildCemuGraphicPackSubtitle(CemuGraphicPackEntryModel pack)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(pack.UiPath))
                parts.Add(pack.UiPath.Trim());

            if (!string.IsNullOrWhiteSpace(pack.Description))
                parts.Add(pack.Description.Trim());

            return parts.Count == 0 ? pack.RelativeRulesPath : string.Join(" \u2014 ", parts);
        }

        private static string? ResolveWiiUTitleId(MediaItem target)
        {
            try
            {
                var cachePath = GetLocalMetadataCachePath(target.FileName);
                var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                var fromCache = CemuTitleIdHelper.NormalizeDisplayTitleId(metadata?.WiiUTitleId);
                if (!string.IsNullOrWhiteSpace(fromCache))
                    return fromCache;
            }
            catch
            {
            }

            return CemuTitleIdHelper.NormalizeDisplayTitleId(WiiUInstalledGameHelper.GetTitleId(target.FileName));
        }

        private static CemuGraphicPackToggle BuildCemuGraphicPackToggle(CemuGraphicPackEntry entry)
        {
            var activePresets = entry.PresetGroups
                .Where(static group => !string.IsNullOrWhiteSpace(group.SelectedPresetName))
                .ToDictionary(static group => group.Category, static group => group.SelectedPresetName!, StringComparer.OrdinalIgnoreCase);

            return new CemuGraphicPackToggle(
                entry.EntryKey,
                entry.RelativeRulesPath,
                entry.IsEnabled,
                activePresets);
        }

        private void OnCemuGraphicPackEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(CemuGraphicPackEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionCemuGraphicPackDirty = true;
        }

        private void OnCemuGraphicPackPresetGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(CemuGraphicPackPresetGroupEntry.SelectedPresetName), StringComparison.Ordinal))
                return;

            IsCurrentSectionCemuGraphicPackDirty = true;
        }

        private void DetachCemuGraphicPackEntryListeners()
        {
            foreach (var entry in CurrentSectionCemuGraphicPackEntries)
            {
                entry.PropertyChanged -= OnCemuGraphicPackEntryPropertyChanged;
                foreach (var presetGroup in entry.PresetGroups)
                    presetGroup.PropertyChanged -= OnCemuGraphicPackPresetGroupPropertyChanged;
            }
        }

        private MediaItem? ResolveCemuContextMenuTarget(object? parameter) =>
            ResolveShadPs4ContextMenuTarget(parameter);

        private static string? BuildRpcs3PatchSubtitle(Rpcs3PatchDefinition definition)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(definition.GameTitle))
            {
                var versionLabel = string.Equals(definition.AppVersion, "All", StringComparison.OrdinalIgnoreCase)
                    ? "All versions"
                    : $"v{definition.AppVersion.Trim()}";
                parts.Add($"{definition.GameTitle.Trim()} \u00B7 {definition.Serial} \u00B7 {versionLabel}");
            }

            if (!string.IsNullOrWhiteSpace(definition.Author))
                parts.Add(definition.Author.Trim());

            if (!string.IsNullOrWhiteSpace(definition.Group))
                parts.Add($"Group: {definition.Group.Trim()}");

            if (!string.IsNullOrWhiteSpace(definition.PatchVersion))
                parts.Add($"Patch v{definition.PatchVersion.Trim()}");

            if (!string.IsNullOrWhiteSpace(definition.Notes))
                parts.Add(definition.Notes.Trim());

            return parts.Count == 0 ? null : string.Join(" \u2014 ", parts);
        }

        private void OnRpcs3PatchEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(Rpcs3PatchEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionRpcs3PatchDirty = true;
        }

        private void DetachRpcs3PatchEntryListeners()
        {
            foreach (var entry in CurrentSectionRpcs3PatchEntries)
                entry.PropertyChanged -= OnRpcs3PatchEntryPropertyChanged;
        }

        private MediaItem? ResolveXeniaContextMenuTarget(object? parameter) =>
            ResolveShadPs4ContextMenuTarget(parameter);

        private MediaItem? ResolveRpcs3ContextMenuTarget(object? parameter) =>
            ResolveShadPs4ContextMenuTarget(parameter);

        private MediaItem? ResolveShadPs4ContextMenuTarget(object? parameter)
        {
            return parameter switch
            {
                MediaItem mediaItem when CoverItems.Contains(mediaItem) => mediaItem,
                MediaItem mediaItem => mediaItem,
                double selected when !double.IsNaN(selected) => GetCarouselItemByIndex(GetRoundedSelectedIndex(selected)),
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => GetCurrentCarouselSelectedItem() ?? HighlightedItem
            };
        }

        private void LoadSelectedShadPs4PatchEntries(string? patchFilePath)
        {
            DetachShadPs4PatchEntryListeners();
            CurrentSectionShadPs4PatchEntries.Clear();
            _activeShadPs4PatchDocumentPath = null;
            _activeShadPs4PatchDocumentText = null;
            IsCurrentSectionShadPs4PatchDirty = false;

            if (string.IsNullOrWhiteSpace(patchFilePath) || !File.Exists(patchFilePath))
                return;

            try
            {
                var text = File.ReadAllText(patchFilePath);
                var entries = ParseShadPs4PatchEntries(text);

                _activeShadPs4PatchDocumentPath = patchFilePath;
                _activeShadPs4PatchDocumentText = text;
                foreach (var entry in entries)
                {
                    CurrentSectionShadPs4PatchEntries.Add(entry);
                    entry.PropertyChanged += OnShadPs4PatchEntryPropertyChanged;
                }

                ShadPs4PatchesStatus = CurrentSectionShadPs4PatchEntries.Count == 0
                    ? "Selected file has no patch elements."
                    : $"Loaded {CurrentSectionShadPs4PatchEntries.Count} patch(es).";
            }
            catch (Exception ex)
            {
                ShadPs4PatchesStatus = $"Failed to load patch file: {ex.Message}";
            }
        }

        private static IReadOnlyList<ShadPs4PatchEntry> ParseShadPs4PatchEntries(string document)
        {
            var entries = new List<ShadPs4PatchEntry>();
            if (string.IsNullOrWhiteSpace(document))
                return entries;

            try
            {
                var doc = XDocument.Parse(document);
                var metadataElements = doc.Descendants("Metadata").ToList();

                foreach (var metadataElement in metadataElements)
                {
                    var name = metadataElement.Attribute("Name")?.Value?.Trim() ?? "Unnamed patch";
                    var note = metadataElement.Attribute("Note")?.Value?.Trim() ?? string.Empty;
                    var appVer = metadataElement.Attribute("AppVer")?.Value?.Trim() ?? string.Empty;
                    var isEnabled = bool.TryParse(metadataElement.Attribute("isEnabled")?.Value, out var parsed) && parsed;

                    entries.Add(new ShadPs4PatchEntry(isEnabled, name, note, appVer));
                }
            }
            catch
            {
            }

            return entries;
        }

        private static string BuildUpdatedShadPs4PatchDocument(string original, IEnumerable<ShadPs4PatchEntry> entries)
        {
            try
            {
                var doc = XDocument.Parse(original);
                var metadataElements = doc.Descendants("Metadata").ToList();
                var entryList = entries.ToList();

                for (var i = 0; i < metadataElements.Count && i < entryList.Count; i++)
                {
                    var metadata = metadataElements[i];
                    var entry = entryList[i];
                    var enabledAttr = metadata.Attribute("isEnabled");

                    if (entry.IsEnabled)
                    {
                        if (enabledAttr == null)
                            metadata.SetAttributeValue("isEnabled", "true");
                        else if (!string.Equals(enabledAttr.Value, "true", StringComparison.OrdinalIgnoreCase))
                            metadata.SetAttributeValue("isEnabled", "true");
                    }
                    else
                    {
                        enabledAttr?.Remove();
                    }
                }

                return doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.None);
            }
            catch
            {
                return original;
            }
        }


        private void ApplyShadPs4UpdateState(ShadPs4UpdateState state)
        {
            CurrentSectionShadPs4CurrentVersion = state.CurrentVersion;
            CurrentSectionShadPs4LatestVersion = state.LatestVersion;
            IsCurrentSectionShadPs4UpdateAvailable = state.IsUpdateAvailable;
            CurrentSectionShadPs4Status = state.StatusMessage;
            CurrentSectionShadPs4EmulatorPath = state.EmulatorDirectory;
            CurrentSectionShadPs4UpdatePath = state.UpdateDirectory;

            CurrentSectionShadPs4AvailableVersions.Clear();
            foreach (var version in state.AvailableVersions.Take(10))
                CurrentSectionShadPs4AvailableVersions.Add(version);

            var selectedVersion = SelectedCurrentSectionShadPs4Version;
            if (string.IsNullOrWhiteSpace(selectedVersion) ||
                !CurrentSectionShadPs4AvailableVersions.Contains(selectedVersion, StringComparer.OrdinalIgnoreCase))
            {
                selectedVersion = CurrentSectionShadPs4AvailableVersions.FirstOrDefault() ?? state.LatestVersion;
            }

            try
            {
                _isSyncingCurrentSectionShadPs4VersionSelection = true;
                SelectedCurrentSectionShadPs4Version = selectedVersion;
            }
            finally
            {
                _isSyncingCurrentSectionShadPs4VersionSelection = false;
            }

            if (!string.IsNullOrWhiteSpace(state.Repository) &&
                !string.Equals(CurrentSectionShadPs4RepositoryOverride, state.Repository, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state.Repository, "shadps4-emu/shadPS4", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = true;
                    CurrentSectionShadPs4RepositoryOverride = state.Repository;
                }
                finally
                {
                    _isSyncingCurrentSectionShadPs4RepositoryOverride = false;
                }
            }

            _sectionLatestReleaseNotes = state.LatestReleaseNotes;
            SyncEmulatorUpdateNoticeOverlay();
        }

    }
}
