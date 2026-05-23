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
        [RelayCommand]
        private async Task OpenCurrentSectionXeniaPatches(object? parameter)
        {
            if (!ShowCurrentSectionXeniaPatchesMenuItem)
                return;

            var selectedItem = GetCurrentCarouselSelectedItem();
            var target = parameter switch
            {
                MediaItem mi when CoverItems.Contains(mi) => mi,
                MediaItem mi => mi,
                double selected when !double.IsNaN(selected) => GetCarouselItemByIndex(GetRoundedSelectedIndex(selected)),
                int idx when idx >= 0 && idx < CoverItems.Count => CoverItems[idx],
                _ => selectedItem ?? HighlightedItem
            };

            if (target == null)
                return;

            IsXeniaPatchesOverlayOpen = true;
            IsXeniaPatchesBusy = true;
            _xeniaPatchOverlayGameTitle = target.Title;
            OnPropertyChanged(nameof(XeniaPatchOverlayHeader));
            XeniaPatchesStatus = "Detecting title ID and loading patches...";
            XeniaDetectedTitleId = null;
            XeniaDetectedMediaId = null;
            IsXeniaPatchSwitchPromptVisible = false;
            IsCurrentSectionXeniaPatchDirty = false;
            _pendingCurrentSectionXeniaPatchFile = null;

            try
            {
                var xeniaDirectory = CurrentSectionXeniaEmulatorPath;
                var metadataService = _xbox360MetadataService;
                var availablePatchTitleIds = await Task.Run(() => GetAvailableXeniaPatchTitleIds(xeniaDirectory)).ConfigureAwait(false);
                var metadata = await Task.Run(() => metadataService?.TryReadGameMetadata(target.FileName)).ConfigureAwait(false);
                var titleId = metadata?.TitleId;
                var mediaId = metadata?.MediaId;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    XeniaDetectedTitleId = titleId;
                    XeniaDetectedMediaId = mediaId;
                });

                if (string.IsNullOrWhiteSpace(titleId) &&
                    !string.IsNullOrWhiteSpace(target.FileName) &&
                    !string.IsNullOrWhiteSpace(target.Title))
                {
                    var fallbackMatch = availablePatchTitleIds
                        .Select(id => new { Id = id, Score = ComputeXeniaPatchCandidateScore(target.Title, id, xeniaDirectory) })
                        .Where(static candidate => candidate.Score > 0)
                        .OrderByDescending(static candidate => candidate.Score)
                        .ThenBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (fallbackMatch != null)
                    {
                        titleId = fallbackMatch.Id;
                        await Dispatcher.UIThread.InvokeAsync(() => XeniaDetectedTitleId = titleId);
                    }
                }

                if (string.IsNullOrWhiteSpace(titleId))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CurrentSectionXeniaPatchFiles.Clear();
                        DetachXeniaPatchEntryListeners();
                        CurrentSectionXeniaPatchEntries.Clear();
                        IsCurrentSectionXeniaPatchDirty = false;
                        SelectedCurrentSectionXeniaPatchFile = null;
                        XeniaPatchesStatus = "Unable to detect Xbox 360 Title ID for the selected game.";
                    });
                    return;
                }

                var patchFiles = await Task.Run(() => FindXeniaPatchFiles(xeniaDirectory, titleId)).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentSectionXeniaPatchFiles.Clear();
                    foreach (var item in patchFiles)
                        CurrentSectionXeniaPatchFiles.Add(item);

                    DetachXeniaPatchEntryListeners();
                    CurrentSectionXeniaPatchEntries.Clear();
                    IsCurrentSectionXeniaPatchDirty = false;
                    _activeXeniaPatchDocumentPath = null;
                    _activeXeniaPatchDocumentText = null;
                    _pendingCurrentSectionXeniaPatchFile = null;
                    IsXeniaPatchSwitchPromptVisible = false;

                    SelectedCurrentSectionXeniaPatchFile = CurrentSectionXeniaPatchFiles.FirstOrDefault()?.FilePath;

                    XeniaPatchesStatus = CurrentSectionXeniaPatchFiles.Count == 0
                        ? $"No patch files found for title ID {titleId}."
                        : $"Loaded {CurrentSectionXeniaPatchFiles.Count} patch file(s) for title ID {titleId}.";
                });
            }
            finally
            {
                IsXeniaPatchesBusy = false;
            }
        }

        private MediaItem? GetCurrentCarouselSelectedItem()
        {
            var roundedIndex = GetRoundedSelectedIndex(SelectedIndex);
            return GetCarouselItemByIndex(roundedIndex);
        }

        private MediaItem? GetCarouselItemByIndex(int idx)
        {
            if (idx < 0 || idx >= CoverItems.Count)
                return null;

            return CoverItems[idx];
        }

        [RelayCommand]
        private async Task SaveCurrentSectionXeniaPatches()
        {
            var saved = await SaveCurrentSectionXeniaPatchesCore().ConfigureAwait(false);
            if (saved)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsXeniaPatchesOverlayOpen = false;
                    IsXeniaPatchSwitchPromptVisible = false;
                    _pendingCurrentSectionXeniaPatchFile = null;
                });
            }
        }

        [RelayCommand]
        private void SelectAllCurrentSectionXeniaPatches()
        {
            if (IsXeniaPatchesBusy)
                return;

            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.IsEnabled = true;

            if (CurrentSectionXeniaPatchEntries.Count > 0)
                XeniaPatchesStatus = $"Enabled {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
        }

        [RelayCommand]
        private void UnselectAllCurrentSectionXeniaPatches()
        {
            if (IsXeniaPatchesBusy)
                return;

            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.IsEnabled = false;

            if (CurrentSectionXeniaPatchEntries.Count > 0)
                XeniaPatchesStatus = $"Disabled {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
        }

        private async Task<bool> SaveCurrentSectionXeniaPatchesCore()
        {
            if (!IsXeniaPatchesOverlayOpen)
                return false;

            var activePath = _activeXeniaPatchDocumentPath;
            var activeText = _activeXeniaPatchDocumentText;
            if (string.IsNullOrWhiteSpace(activePath) || string.IsNullOrWhiteSpace(activeText))
            {
                XeniaPatchesStatus = "Select a patch file before saving.";
                return false;
            }

            IsXeniaPatchesBusy = true;
            try
            {
                var updated = BuildUpdatedPatchDocument(activeText, CurrentSectionXeniaPatchEntries);
                await Task.Run(() => File.WriteAllText(activePath, updated)).ConfigureAwait(false);
                _activeXeniaPatchDocumentText = updated;
                IsCurrentSectionXeniaPatchDirty = false;
                XeniaPatchesStatus = "Patch settings saved.";
                return true;
            }
            catch (Exception ex)
            {
                XeniaPatchesStatus = $"Failed to save patches: {ex.Message}";
                return false;
            }
            finally
            {
                IsXeniaPatchesBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveAndSwitchCurrentSectionXeniaPatchFile()
        {
            var pending = _pendingCurrentSectionXeniaPatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsXeniaPatchSwitchPromptVisible = false;
                return;
            }

            var saved = await SaveCurrentSectionXeniaPatchesCore().ConfigureAwait(false);
            if (!saved)
                return;

            ApplyPendingCurrentSectionXeniaPatchFileSelection(pending);
        }

        [RelayCommand]
        private void SkipAndSwitchCurrentSectionXeniaPatchFile()
        {
            var pending = _pendingCurrentSectionXeniaPatchFile;
            if (string.IsNullOrWhiteSpace(pending))
            {
                IsXeniaPatchSwitchPromptVisible = false;
                return;
            }

            ApplyPendingCurrentSectionXeniaPatchFileSelection(pending);
        }

        [RelayCommand]
        private void CloseCurrentSectionXeniaPatches()
        {
            IsXeniaPatchesOverlayOpen = false;
            IsXeniaPatchSwitchPromptVisible = false;
            _pendingCurrentSectionXeniaPatchFile = null;
        }

        private void ApplyPendingCurrentSectionXeniaPatchFileSelection(string patchFilePath)
        {
            _pendingCurrentSectionXeniaPatchFile = null;
            IsXeniaPatchSwitchPromptVisible = false;

            try
            {
                _isSwitchingCurrentSectionXeniaPatchFile = true;
                SelectedCurrentSectionXeniaPatchFile = patchFilePath;
            }
            finally
            {
                _isSwitchingCurrentSectionXeniaPatchFile = false;
            }
        }

        private void SyncSelectedXeniaPatchFileItemFromPath()
        {
            _selectedCurrentSectionXeniaPatchFileItem = string.IsNullOrWhiteSpace(_selectedCurrentSectionXeniaPatchFile)
                ? null
                : CurrentSectionXeniaPatchFiles.FirstOrDefault(file =>
                    string.Equals(file.FilePath, _selectedCurrentSectionXeniaPatchFile, StringComparison.OrdinalIgnoreCase));

            OnPropertyChanged(nameof(SelectedCurrentSectionXeniaPatchFileItem));
        }

        private void LoadSelectedXeniaPatchEntries(string? patchFilePath)
        {
            DetachXeniaPatchEntryListeners();
            CurrentSectionXeniaPatchEntries.Clear();
            _activeXeniaPatchDocumentPath = null;
            _activeXeniaPatchDocumentText = null;
            IsCurrentSectionXeniaPatchDirty = false;

            if (string.IsNullOrWhiteSpace(patchFilePath) || !File.Exists(patchFilePath))
                return;

            try
            {
                var text = File.ReadAllText(patchFilePath);
                var entries = ParseXeniaPatchEntries(text);

                _activeXeniaPatchDocumentPath = patchFilePath;
                _activeXeniaPatchDocumentText = text;
                foreach (var entry in entries)
                {
                    CurrentSectionXeniaPatchEntries.Add(entry);
                    entry.PropertyChanged += OnXeniaPatchEntryPropertyChanged;
                }

                XeniaPatchesStatus = CurrentSectionXeniaPatchEntries.Count == 0
                    ? "Selected file has no patch blocks."
                    : $"Loaded {CurrentSectionXeniaPatchEntries.Count} patch option(s).";
            }
            catch (Exception ex)
            {
                XeniaPatchesStatus = $"Failed to load patch file: {ex.Message}";
            }
        }

        private void OnXeniaPatchEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(XeniaPatchEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionXeniaPatchDirty = true;
        }

        private void DetachXeniaPatchEntryListeners()
        {
            foreach (var entry in CurrentSectionXeniaPatchEntries)
                entry.PropertyChanged -= OnXeniaPatchEntryPropertyChanged;
        }

        private void OnShadPs4PatchEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(ShadPs4PatchEntry.IsEnabled), StringComparison.Ordinal))
                return;

            IsCurrentSectionShadPs4PatchDirty = true;
        }

        private void DetachShadPs4PatchEntryListeners()
        {
            foreach (var entry in CurrentSectionShadPs4PatchEntries)
                entry.PropertyChanged -= OnShadPs4PatchEntryPropertyChanged;
        }

        private static IReadOnlyList<XeniaPatchEntry> ParseXeniaPatchEntries(string document)
        {
            var entries = new List<XeniaPatchEntry>();
            if (string.IsNullOrWhiteSpace(document))
                return entries;

            var patchPattern = new Regex(@"\[\[patch\]\](?<body>.*?)(?=\n\s*\[\[patch\]\]|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var namePattern = new Regex(@"^\s*name\s*=\s*\""(?<value>.*?)\""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var descPattern = new Regex(@"^\s*desc\s*=\s*\""(?<value>.*?)\""\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var enabledPattern = new Regex(@"^\s*is_enabled\s*=\s*(?<value>true|false)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            foreach (Match match in patchPattern.Matches(document))
            {
                var body = match.Groups["body"].Value;
                var name = namePattern.Match(body).Groups["value"].Value;
                var description = descPattern.Match(body).Groups["value"].Value;
                var enabledMatch = enabledPattern.Match(body);
                var isEnabled = enabledMatch.Success &&
                                bool.TryParse(enabledMatch.Groups["value"].Value, out var parsedEnabled) &&
                                parsedEnabled;

                if (string.IsNullOrWhiteSpace(name))
                    name = "Unnamed patch";

                entries.Add(new XeniaPatchEntry(isEnabled, name, description));
            }

            return entries;
        }

        private static string BuildUpdatedPatchDocument(string original, IEnumerable<XeniaPatchEntry> entries)
        {
            var patchBlocks = new Regex(@"\[\[patch\]\].*?(?=\n\s*\[\[patch\]\]|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var enabledLine = new Regex(@"^(\s*is_enabled\s*=\s*)(true|false)(\s*(#.*)?)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var queue = new Queue<XeniaPatchEntry>(entries);

            return patchBlocks.Replace(original, m =>
            {
                if (queue.Count == 0)
                    return m.Value;

                var entry = queue.Dequeue();
                return enabledLine.Replace(
                    m.Value,
                    enabled => enabled.Groups[1].Value +
                               entry.IsEnabled.ToString().ToLowerInvariant() +
                               enabled.Groups[3].Value,
                    1);
            });
        }

        private static IReadOnlyList<XeniaPatchFileItem> FindXeniaPatchFiles(string? emulatorDirectory, string titleId)
        {
            if (string.IsNullOrWhiteSpace(emulatorDirectory))
                return Array.Empty<XeniaPatchFileItem>();

            var root = Path.Combine(emulatorDirectory, "patches");
            if (!Directory.Exists(root))
                return Array.Empty<XeniaPatchFileItem>();

            var normalizedTitleId = titleId.ToUpperInvariant();
            return Directory
                .EnumerateFiles(root, "*.patch.toml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).StartsWith(normalizedTitleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new XeniaPatchFileItem(path, Path.GetFileNameWithoutExtension(path)))
                .ToArray();
        }

        private static int ComputeXeniaPatchCandidateScore(string gameTitle, string candidateTitleId, string? emulatorDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameTitle) || string.IsNullOrWhiteSpace(candidateTitleId) || string.IsNullOrWhiteSpace(emulatorDirectory))
                return 0;

            try
            {
                var patchesRoot = Path.Combine(emulatorDirectory, "patches");
                if (!Directory.Exists(patchesRoot))
                    return 0;

                var normalizedTitle = NormalizeXeniaPatchSearchText(gameTitle);
                if (normalizedTitle.Length == 0)
                    return 0;

                var candidateFiles = Directory
                    .EnumerateFiles(patchesRoot, "*.patch.toml", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).StartsWith(candidateTitleId, StringComparison.OrdinalIgnoreCase))
                    .Take(4)
                    .ToArray();

                var score = 0;
                foreach (var candidateFile in candidateFiles)
                {
                    var displayName = Path.GetFileNameWithoutExtension(candidateFile);
                    var normalizedDisplayName = NormalizeXeniaPatchSearchText(displayName);
                    if (normalizedDisplayName.Length == 0)
                        continue;

                    if (normalizedDisplayName.Contains(normalizedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        score = Math.Max(score, 100);
                        continue;
                    }

                    var common = LongestCommonXeniaPatchSubstring(normalizedTitle, normalizedDisplayName);
                    if (common >= 6)
                        score = Math.Max(score, common * 4);
                }

                return score;
            }
            catch
            {
                return 0;
            }
        }

        private static string NormalizeXeniaPatchSearchText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var normalized = Regex.Replace(text, "[^A-Za-z0-9]+", " ").Trim();
            normalized = Regex.Replace(normalized, "\\s{2,}", " ");
            return normalized.ToUpperInvariant();
        }

        private static int LongestCommonXeniaPatchSubstring(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return 0;

            var best = 0;
            var table = new int[a.Length + 1, b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    if (a[i - 1] != b[j - 1])
                    {
                        table[i, j] = 0;
                        continue;
                    }

                    table[i, j] = table[i - 1, j - 1] + 1;
                    if (table[i, j] > best)
                        best = table[i, j];
                }
            }

            return best;
        }



        private static HashSet<string> GetAvailableXeniaPatchTitleIds(string? emulatorDirectory)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(emulatorDirectory))
                return set;

            var root = Path.Combine(emulatorDirectory, "patches");
            if (!Directory.Exists(root))
                return set;

            foreach (var path in Directory.EnumerateFiles(root, "*.patch.toml", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);
                var match = Regex.Match(fileName, @"^(?<id>[0-9A-Fa-f]{8})\s*-", RegexOptions.IgnoreCase);
                if (match.Success)
                    set.Add(match.Groups["id"].Value.ToUpperInvariant());
            }

            return set;
        }


    }
}
