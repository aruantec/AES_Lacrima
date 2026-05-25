using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.IO;
using AES_Lacrima.Services.DuckStation;
using AES_Lacrima.Services.Emulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class DuckStationCheatEntryViewModel : ObservableObject
{
    public DuckStationCheatEntryViewModel(DuckStationCheatEntry definition, bool isEnabled)
    {
        Definition = definition;
        Name = definition.Name;
        Group = definition.Group;
        Description = definition.Description;
        Author = definition.Author;
        IsManual = definition.IsManual;
        IsEnabled = isEnabled;
    }

    public DuckStationCheatEntry Definition { get; }

    public string Name { get; }

    public string? Group { get; }

    public string? Description { get; }

    public string? Author { get; }

    public bool IsManual { get; }

    [ObservableProperty]
    private bool _isEnabled;
}

public partial class DuckStationCheatsEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _romPath;
    private string? _resolvedSerial;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Select a PlayStation game to manage cheats.";

    [ObservableProperty]
    private string? _titleId;

    [ObservableProperty]
    private string? _gameTitle;

    public ObservableCollection<DuckStationCheatFileItem> CheatFiles { get; } = [];

    public ObservableCollection<DuckStationCheatEntryViewModel> CheatEntries { get; } = [];

    [ObservableProperty]
    private DuckStationCheatFileItem? _selectedCheatFileItem;

    public string OverlayHeader =>
        string.IsNullOrWhiteSpace(GameTitle) ? "DuckStation Cheats" : $"{GameTitle} — Cheats";

    partial void OnSelectedCheatFileItemChanged(DuckStationCheatFileItem? value) =>
        _ = LoadSelectedCheatFileAsync(value?.FilePath);

    partial void OnGameTitleChanged(string? value) =>
        OnPropertyChanged(nameof(OverlayHeader));

    public async Task LoadAsync(string? emulatorDirectory, string romPath, string? gameTitle)
    {
        IsOpen = true;
        await LoadGameCheatsAsync(emulatorDirectory, romPath, gameTitle).ConfigureAwait(true);
    }

    private async Task LoadGameCheatsAsync(string? emulatorDirectory, string romPath, string? gameTitle)
    {
        IsBusy = true;
        _emulatorDirectory = emulatorDirectory;
        _romPath = romPath;
        GameTitle = gameTitle;
        TitleId = null;
        _resolvedSerial = null;
        CheatFiles.Clear();
        DetachEntryListeners();
        CheatEntries.Clear();
        SelectedCheatFileItem = null;
        Status = "Detecting PSX Title ID...";

        try
        {
            var serial = await Task.Run(() => ResolvePsxSerial(romPath)).ConfigureAwait(true);
            TitleId = serial;
            _resolvedSerial = serial;

            if (string.IsNullOrWhiteSpace(serial))
            {
                Status = "Unable to detect PSX serial for the selected game.";
                return;
            }

            await RefreshCheatFilesAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadCheats()
    {
        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(_emulatorDirectory))
        {
            Status = "Emulator directory is not configured.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_resolvedSerial))
        {
            Status = "Serial number is required before downloading cheats.";
            return;
        }

        IsBusy = true;
        Status = "Downloading cheat database from chtdb...";

        try
        {
            var result = await DuckStationCheatsDownloadService.DownloadCheatsForSerialAsync(
                _emulatorDirectory,
                _resolvedSerial).ConfigureAwait(true);

            Status = result.Message;
            if (result.Success)
                await RefreshCheatFilesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Failed to download cheats: {ex.Message}";
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

    [RelayCommand]
    private void SelectAllCheats()
    {
        if (IsBusy)
            return;

        foreach (var entry in CheatEntries.Where(static e => !e.IsManual))
            entry.IsEnabled = true;

        SaveEnabledState();
        Status = CheatEntries.Count == 0
            ? Status
            : $"Enabled {CheatEntries.Count(static e => e.IsEnabled)} cheat(s).";
    }

    [RelayCommand]
    private void UnselectAllCheats()
    {
        if (IsBusy)
            return;

        foreach (var entry in CheatEntries)
            entry.IsEnabled = false;

        SaveEnabledState();
        Status = "Disabled all cheats.";
    }

    private async Task RefreshCheatFilesAsync()
    {
        if (string.IsNullOrWhiteSpace(_resolvedSerial) || string.IsNullOrWhiteSpace(_emulatorDirectory))
            return;

        var previousSelection = SelectedCheatFileItem?.FilePath;
        var cheatFiles = await Task.Run(() =>
            DuckStationCheatsService.FindCheatFiles(_emulatorDirectory, _resolvedSerial)).ConfigureAwait(true);

        CheatFiles.Clear();
        foreach (var file in cheatFiles)
            CheatFiles.Add(file);

        if (cheatFiles.Count == 0)
        {
            SelectedCheatFileItem = null;
            DetachEntryListeners();
            CheatEntries.Clear();
            Status = $"No cheat files found for serial {_resolvedSerial}. Try downloading from the database.";
            return;
        }

        SelectedCheatFileItem =
            previousSelection == null
                ? cheatFiles[0]
                : cheatFiles.FirstOrDefault(f =>
                      string.Equals(f.FilePath, previousSelection, StringComparison.OrdinalIgnoreCase)) ??
                  cheatFiles[0];

        Status = $"Loaded {cheatFiles.Count} cheat file(s) for serial {_resolvedSerial}.";
    }

    private async Task LoadSelectedCheatFileAsync(string? cheatFilePath)
    {
        DetachEntryListeners();
        CheatEntries.Clear();

        if (string.IsNullOrWhiteSpace(cheatFilePath) || !File.Exists(cheatFilePath))
            return;

        IsBusy = true;
        try
        {
            var document = await Task.Run(() =>
                DuckStationCheatsService.ParseChtFile(cheatFilePath)).ConfigureAwait(true);

            if (document == null)
            {
                Status = "Failed to parse the selected cheat file.";
                return;
            }

            var enabledCheats = await Task.Run(() =>
                DuckStationCheatsService.LoadEnabledCheats(_emulatorDirectory, _resolvedSerial ?? document.Serial)).ConfigureAwait(true);

            foreach (var cheat in document.Entries)
            {
                var fullName = string.IsNullOrWhiteSpace(cheat.Group)
                    ? cheat.Name
                    : $"{cheat.Group}\\{cheat.Name}";
                var isEnabled = enabledCheats.Contains(fullName);
                var entry = new DuckStationCheatEntryViewModel(cheat, isEnabled);
                entry.PropertyChanged += OnCheatEntryPropertyChanged;
                CheatEntries.Add(entry);
            }

            Status = CheatEntries.Count == 0
                ? "Selected cheat file has no entries."
                : $"Loaded {CheatEntries.Count} cheat(s) from {Path.GetFileName(cheatFilePath)}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCheatEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuckStationCheatEntryViewModel.IsEnabled))
            SaveEnabledState();
    }

    private void SaveEnabledState()
    {
        if (string.IsNullOrWhiteSpace(_emulatorDirectory) || string.IsNullOrWhiteSpace(_resolvedSerial))
            return;

        var enabledNames = CheatEntries
            .Where(static e => e.IsEnabled)
            .Select(e =>
            {
                var def = e.Definition;
                return string.IsNullOrWhiteSpace(def.Group)
                    ? def.Name
                    : $"{def.Group}\\{def.Name}";
            })
            .ToList();

        try
        {
            DuckStationCheatsService.SaveEnabledCheats(_emulatorDirectory, _resolvedSerial, enabledNames);
            var count = enabledNames.Count;
            Status = count == 0
                ? "All cheats disabled. Changes saved."
                : $"{count} cheat(s) enabled. Changes saved — restart game to apply.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to save cheat state: {ex.Message}";
        }
    }

    private void DetachEntryListeners()
    {
        foreach (var entry in CheatEntries)
            entry.PropertyChanged -= OnCheatEntryPropertyChanged;
    }

    private static string? ResolvePsxSerial(string romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        var cacheId = BinaryMetadataHelper.GetCacheId(romPath);
        var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

        if (!string.IsNullOrWhiteSpace(metadata?.PsXTitleId))
            return metadata!.PsXTitleId;

        try
        {
            var romInfo = RomInspector.Inspect(romPath, DiscSection.PSX);
            return romInfo.GameId;
        }
        catch
        {
            return null;
        }
    }

    public void ClearSession()
    {
        IsOpen = false;
        _emulatorDirectory = null;
        _romPath = null;
        _resolvedSerial = null;
        TitleId = null;
        GameTitle = null;
        DetachEntryListeners();
        CheatFiles.Clear();
        CheatEntries.Clear();
        Status = "Select a PlayStation game to manage cheats.";
    }
}
