using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Lacrima.Services.Dolphin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class DolphinCheatEntryViewModel : ObservableObject
{
    public DolphinCheatEntryViewModel(DolphinGameIniEntry definition, bool isEnabled)
    {
        Definition = definition;
        Name = definition.Name;
        Kind = definition.Kind;
        LineCount = definition.Lines.Count;
        IsEnabled = isEnabled;
    }

    public DolphinGameIniEntry Definition { get; }

    public string Name { get; }

    public DolphinGameIniEntryKind Kind { get; }

    public string KindLabel => Kind switch
    {
        DolphinGameIniEntryKind.OnFrame => "Patch",
        DolphinGameIniEntryKind.ActionReplay => "Action Replay",
        _ => "Gecko"
    };

    public int LineCount { get; }

    [ObservableProperty]
    private bool _isEnabled;
}

public partial class DolphinCheatsEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _launcherPath;
    private string? _userDirectory;
    private string? _gameId;
    private DolphinGameSettingsDocument? _document;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Select a GameCube or Wii game to manage patches and cheats.";

    [ObservableProperty]
    private string? _gameIdDisplay;

    public bool CanDownloadGeckoCodes =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(_userDirectory) &&
        !string.IsNullOrWhiteSpace(_gameId);

    [ObservableProperty]
    private string? _gameTitle;

    [ObservableProperty]
    private string _selectedKindFilter = "All";

    public ObservableCollection<DolphinCheatEntryViewModel> CheatEntries { get; } = [];

    public IReadOnlyList<string> KindFilterOptions { get; } =
    [
        "All",
        "Patches",
        "Action Replay",
        "Gecko"
    ];

    public string OverlayHeader =>
        string.IsNullOrWhiteSpace(GameTitle)
            ? "Dolphin Patches & Cheats"
            : $"{GameTitle} — Patches & Cheats";

    partial void OnGameTitleChanged(string? value) =>
        OnPropertyChanged(nameof(OverlayHeader));

    partial void OnSelectedKindFilterChanged(string value) =>
        OnPropertyChanged(nameof(FilteredCheatEntries));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadGeckoCodes));
        DownloadGeckoCodesCommand.NotifyCanExecuteChanged();
    }

    partial void OnGameIdDisplayChanged(string? value)
    {
        OnPropertyChanged(nameof(CanDownloadGeckoCodes));
        DownloadGeckoCodesCommand.NotifyCanExecuteChanged();
    }

    public IEnumerable<DolphinCheatEntryViewModel> FilteredCheatEntries =>
        SelectedKindFilter switch
        {
            "Patches" => CheatEntries.Where(static e => e.Kind == DolphinGameIniEntryKind.OnFrame),
            "Action Replay" => CheatEntries.Where(static e => e.Kind == DolphinGameIniEntryKind.ActionReplay),
            "Gecko" => CheatEntries.Where(static e => e.Kind == DolphinGameIniEntryKind.Gecko),
            _ => CheatEntries
        };

    public async Task LoadAsync(string? emulatorDirectory, string? launcherPath, string romPath, string? gameTitle, string? albumTitle)
    {
        IsOpen = true;
        await LoadGameSettingsAsync(emulatorDirectory, launcherPath, romPath, gameTitle, albumTitle).ConfigureAwait(true);
    }

    private async Task LoadGameSettingsAsync(
        string? emulatorDirectory,
        string? launcherPath,
        string romPath,
        string? gameTitle,
        string? albumTitle)
    {
        IsBusy = true;
        GameTitle = gameTitle;
        GameIdDisplay = null;
        _gameId = null;
        _document = null;
        _userDirectory = null;
        DetachEntryListeners();
        CheatEntries.Clear();
        Status = "Resolving game id from metadata...";

        try
        {
            var gameId = await Task.Run(() =>
                DolphinGameIniService.ResolveGameIdFromMetadata(romPath, albumTitle)).ConfigureAwait(true);

            GameIdDisplay = gameId;
            _gameId = gameId;

            if (string.IsNullOrWhiteSpace(gameId))
            {
                Status = "No GameCube/Wii title id in metadata. Scan or refresh metadata first.";
                return;
            }

            _emulatorDirectory = DolphinGameIniService.ResolveEmulatorDirectory(emulatorDirectory, launcherPath);
            _launcherPath = launcherPath;
            _userDirectory = DolphinGameIniService.ResolvePortableUserDirectory(_emulatorDirectory, launcherPath);

            if (string.IsNullOrWhiteSpace(_userDirectory))
            {
                Status = "Unable to locate Dolphin user directory.";
                return;
            }

            var sysDir = DolphinGameIniService.GetSysGameSettingsDirectory(_emulatorDirectory);
            var userGameSettingsDir = DolphinGameIniService.GetUserGameSettingsDirectory(_userDirectory);

            var document = await Task.Run(() =>
                DolphinGameIniService.LoadMergedSettings(gameId, sysDir, userGameSettingsDir)).ConfigureAwait(true);

            _document = document;
            OnPropertyChanged(nameof(CanDownloadGeckoCodes));
            DownloadGeckoCodesCommand.NotifyCanExecuteChanged();

            foreach (var entry in document.Entries)
            {
                var vm = new DolphinCheatEntryViewModel(entry, entry.Enabled);
                vm.PropertyChanged += OnCheatEntryPropertyChanged;
                CheatEntries.Add(vm);
            }

            Status = CheatEntries.Count == 0
                ? $"No patches or cheats found for {gameId}. Download Gecko codes or add entries in Dolphin."
                : $"Loaded {CheatEntries.Count} patch/cheat entries for {gameId}.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(FilteredCheatEntries));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadGeckoCodes))]
    private async Task DownloadGeckoCodes()
    {
        if (!CanDownloadGeckoCodes)
            return;

        IsBusy = true;
        Status = $"Downloading Gecko codes for {_gameId} from codes.rc24.xyz...";
        try
        {
            var sysDir = DolphinGameIniService.GetSysGameSettingsDirectory(_emulatorDirectory);
            var result = await DolphinGameIniService
                .DownloadGeckoCodesAsync(_userDirectory!, _gameId!, sysDir)
                .ConfigureAwait(true);

            Status = result.Message;
            if (result.Success)
            {
                SelectedKindFilter = "Gecko";
                await ReloadEntriesAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllCheats()
    {
        if (IsBusy)
            return;

        foreach (var entry in FilteredCheatEntries)
            entry.IsEnabled = true;

        SaveEnabledState();
    }

    [RelayCommand]
    private void UnselectAllCheats()
    {
        if (IsBusy)
            return;

        foreach (var entry in FilteredCheatEntries)
            entry.IsEnabled = false;

        SaveEnabledState();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private async Task ReloadEntriesAsync()
    {
        if (_document == null || string.IsNullOrWhiteSpace(_userDirectory) || string.IsNullOrWhiteSpace(_gameId))
            return;

        var sysDir = DolphinGameIniService.GetSysGameSettingsDirectory(_emulatorDirectory);
        var userGameSettingsDir = DolphinGameIniService.GetUserGameSettingsDirectory(_userDirectory);

        var document = await Task.Run(() =>
            DolphinGameIniService.LoadMergedSettings(_gameId, sysDir, userGameSettingsDir)).ConfigureAwait(true);

        _document = document;
        DetachEntryListeners();
        CheatEntries.Clear();

        foreach (var entry in document.Entries)
        {
            var vm = new DolphinCheatEntryViewModel(entry, entry.Enabled);
            vm.PropertyChanged += OnCheatEntryPropertyChanged;
            CheatEntries.Add(vm);
        }

        OnPropertyChanged(nameof(FilteredCheatEntries));
    }

    private void OnCheatEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DolphinCheatEntryViewModel.IsEnabled))
            SaveEnabledState();
    }

    private void SaveEnabledState()
    {
        if (_document == null || string.IsNullOrWhiteSpace(_userDirectory))
            return;

        foreach (var vm in CheatEntries)
            vm.Definition.Enabled = vm.IsEnabled;

        try
        {
            DolphinGameIniService.SaveEnabledState(_userDirectory, _document);
            DolphinGameIniService.EnsureCheatsEnabled(_userDirectory, null);

            var enabledCount = CheatEntries.Count(static e => e.IsEnabled);
            Status = enabledCount == 0
                ? "All entries disabled. Saved to User/GameSettings — restart the game in Dolphin to apply."
                : $"{enabledCount} enabled. Saved to User/GameSettings — restart the game in Dolphin to apply.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to save: {ex.Message}";
        }
    }

    private void DetachEntryListeners()
    {
        foreach (var entry in CheatEntries)
            entry.PropertyChanged -= OnCheatEntryPropertyChanged;
    }

    public void ClearSession()
    {
        IsOpen = false;
        _emulatorDirectory = null;
        _launcherPath = null;
        _userDirectory = null;
        _gameId = null;
        _document = null;
        GameIdDisplay = null;
        GameTitle = null;
        SelectedKindFilter = "All";
        DetachEntryListeners();
        CheatEntries.Clear();
        Status = "Select a GameCube or Wii game to manage patches and cheats.";
        OnPropertyChanged(nameof(CanDownloadGeckoCodes));
        DownloadGeckoCodesCommand.NotifyCanExecuteChanged();
    }
}
