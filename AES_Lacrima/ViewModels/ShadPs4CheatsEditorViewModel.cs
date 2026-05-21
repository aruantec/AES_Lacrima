using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Lacrima.Services;
using AES_Lacrima.Services.ShadPs4;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class ShadPs4CheatEntryViewModel : ObservableObject
{
    public ShadPs4CheatEntryViewModel(
        ShadPs4CheatModDefinition definition,
        bool isEnabled,
        bool isCheckbox,
        string? hint)
    {
        Definition = definition;
        IsEnabled = isEnabled;
        IsCheckbox = isCheckbox;
        Hint = hint;
        Name = definition.Name;
    }

    public ShadPs4CheatModDefinition Definition { get; }

    public string Name { get; }

    public string? Hint { get; }

    public bool IsCheckbox { get; }

    public IRelayCommand? ApplyCommand { get; private set; }

    [ObservableProperty]
    private bool _isEnabled;

    public void SetApplyCommand(IRelayCommand command) => ApplyCommand = command;
}

public partial class ShadPs4CheatsEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _gamePath;
    private ShadPs4IpcSession? _ipcSession;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Select a PlayStation 4 game to manage cheats.";

    [ObservableProperty]
    private string? _titleId;

    [ObservableProperty]
    private string? _gameTitle;

    [ObservableProperty]
    private string? _gameVersion;

    [ObservableProperty]
    private string? _creditsText;

    [ObservableProperty]
    private bool _isIpcAvailable;

    [ObservableProperty]
    private bool _isGameRunning;

    public ObservableCollection<ShadPs4CheatFileItem> CheatFiles { get; } = [];

    public ObservableCollection<ShadPs4CheatEntryViewModel> CheatEntries { get; } = [];

    public string OverlayHeader =>
        string.IsNullOrWhiteSpace(GameTitle) ? "shadPS4 Cheats" : $"{GameTitle} — Cheats";

    [ObservableProperty]
    private ShadPs4CheatFileItem? _selectedCheatFileItem;

    partial void OnSelectedCheatFileItemChanged(ShadPs4CheatFileItem? value) =>
        _ = LoadSelectedCheatFileAsync(value?.FilePath);

    public void SetIpcSession(ShadPs4IpcSession? session, bool isGameRunning)
    {
        _ipcSession = session;
        IsGameRunning = isGameRunning;
        RefreshIpcAvailability();
        UpdateStatusMessage();
        TryApplyPersistedCheckboxCheats();
    }

    partial void OnIsIpcAvailableChanged(bool value)
    {
        if (value)
            TryApplyPersistedCheckboxCheats();
    }

    partial void OnIsGameRunningChanged(bool value)
    {
        if (value)
            TryApplyPersistedCheckboxCheats();
        else
            UpdateStatusMessage();
    }

    private void RefreshIpcAvailability() =>
        IsIpcAvailable = _ipcSession?.IsAttached == true && _ipcSession.IsMemoryPatchSupported;

    private void TryApplyPersistedCheckboxCheats()
    {
        if (!IsGameRunning || !IsIpcAvailable || CheatEntries.Count == 0)
            return;

        ApplyEnabledCheckboxCheats();
    }

    private void ApplyEnabledCheckboxCheats()
    {
        foreach (var entry in CheatEntries.Where(static entry => entry.IsCheckbox && entry.IsEnabled))
            TryApplyCheat(entry, enabled: true);
    }

    public async Task LoadAsync(string? emulatorDirectory, string gamePath, string? gameTitle)
    {
        IsOpen = true;
        await LoadGameCheatsAsync(emulatorDirectory, gamePath, gameTitle).ConfigureAwait(true);
    }

    public async Task EnsureLoadedForGameAsync(string? emulatorDirectory, string gamePath, string? gameTitle)
    {
        if (string.Equals(_gamePath, gamePath, StringComparison.OrdinalIgnoreCase) &&
            CheatEntries.Count > 0 &&
            !string.IsNullOrWhiteSpace(TitleId))
        {
            TryApplyPersistedCheckboxCheats();
            return;
        }

        await LoadGameCheatsAsync(emulatorDirectory, gamePath, gameTitle).ConfigureAwait(true);
    }

    private async Task LoadGameCheatsAsync(string? emulatorDirectory, string gamePath, string? gameTitle)
    {
        IsBusy = true;
        _emulatorDirectory = emulatorDirectory;
        _gamePath = gamePath;
        GameTitle = gameTitle;
        TitleId = null;
        GameVersion = null;
        CreditsText = null;
        CheatFiles.Clear();
        CheatEntries.Clear();
        SelectedCheatFileItem = null;
        Status = "Detecting PS4 Title ID and loading cheats...";

        try
        {
            var titleId = await Task.Run(() => ShadPs4TitleIdResolver.Resolve(gamePath)).ConfigureAwait(true);
            var version = await Task.Run(() => Ps4InstalledGameHelper.GetVersion(gamePath)).ConfigureAwait(true);
            TitleId = titleId;
            GameVersion = version;

            if (string.IsNullOrWhiteSpace(titleId))
            {
                Status = "Unable to detect PS4 Title ID for the selected game.";
                return;
            }

            var cheatFiles = await Task.Run(() =>
                ShadPs4CheatsService.FindCheatFiles(emulatorDirectory, titleId, version)).ConfigureAwait(true);

            CheatFiles.Clear();
            foreach (var file in cheatFiles)
                CheatFiles.Add(file);

            if (cheatFiles.Count == 0)
            {
                Status = string.IsNullOrWhiteSpace(version)
                    ? $"No cheat files found for title ID {titleId} in user/cheats."
                    : $"No cheat files found for title ID {titleId} and version {version} in user/cheats.";
                return;
            }

            SelectedCheatFileItem = cheatFiles[0];
            Status = $"Loaded {cheatFiles.Count} cheat file(s) for title ID {titleId}.";
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

        foreach (var entry in CheatEntries.Where(static entry => entry.IsCheckbox))
            entry.IsEnabled = true;

        SaveEnabledState();
        ApplyEnabledCheckboxCheats();
        Status = CheatEntries.Count == 0
            ? Status
            : $"Enabled {CheatEntries.Count(static entry => entry.IsCheckbox)} checkbox cheat(s).";
    }

    [RelayCommand]
    private void UnselectAllCheats()
    {
        if (IsBusy)
            return;

        foreach (var entry in CheatEntries.Where(static entry => entry.IsCheckbox))
        {
            entry.IsEnabled = false;
            TryApplyCheat(entry, enabled: false);
        }

        SaveEnabledState();
        Status = CheatEntries.Count == 0
            ? Status
            : $"Disabled all checkbox cheats.";
    }

    public void ClearSession()
    {
        IsOpen = false;
        Reset();
    }

    private async Task LoadSelectedCheatFileAsync(string? cheatFilePath)
    {
        CheatEntries.Clear();
        CreditsText = null;

        if (string.IsNullOrWhiteSpace(cheatFilePath) || !System.IO.File.Exists(cheatFilePath))
            return;

        IsBusy = true;
        try
        {
            var document = await Task.Run(() => ShadPs4CheatsService.LoadCheatDocument(cheatFilePath)).ConfigureAwait(true);
            if (document == null)
            {
                Status = "Failed to parse the selected cheat file.";
                return;
            }

            var enabledState = await Task.Run(() =>
                ShadPs4CheatsService.LoadEnabledState(_emulatorDirectory, Path.GetFileName(cheatFilePath))).ConfigureAwait(true);

            foreach (var mod in document.Mods)
            {
                var isCheckbox = string.Equals(mod.Type, "checkbox", StringComparison.OrdinalIgnoreCase);
                var enabled = isCheckbox &&
                              enabledState.EnabledMods.TryGetValue(mod.Name, out var stored) &&
                              stored;

                var entry = new ShadPs4CheatEntryViewModel(
                    mod,
                    enabled,
                    isCheckbox,
                    mod.Hint);
                if (!isCheckbox)
                {
                    entry.SetApplyCommand(new RelayCommand(() => TryApplyCheat(entry, enabled: true)));
                }

                entry.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(ShadPs4CheatEntryViewModel.IsEnabled))
                        return;

                    if (!entry.IsCheckbox)
                        return;

                    SaveEnabledState();
                    TryApplyCheat(entry, entry.IsEnabled);
                };
                CheatEntries.Add(entry);
            }

            CreditsText = document.Credits.Count == 0
                ? null
                : $"Author(s): {string.Join(", ", document.Credits)}";

            Status = CheatEntries.Count == 0
                ? "Selected cheat file has no mods."
                : $"Loaded {CheatEntries.Count} cheat option(s) from {Path.GetFileName(cheatFilePath)}.";
            UpdateStatusMessage();
            TryApplyPersistedCheckboxCheats();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveEnabledState()
    {
        if (SelectedCheatFileItem == null)
            return;

        var state = new ShadPs4CheatEnabledStateDocument();
        foreach (var entry in CheatEntries.Where(static entry => entry.IsCheckbox))
            state.EnabledMods[entry.Name] = entry.IsEnabled;

        try
        {
            ShadPs4CheatsService.SaveEnabledState(
                _emulatorDirectory,
                SelectedCheatFileItem.FileName,
                state);
        }
        catch (Exception ex)
        {
            Status = $"Failed to save cheat preferences: {ex.Message}";
        }
    }

    private void TryApplyCheat(ShadPs4CheatEntryViewModel entry, bool enabled)
    {
        if (!IsGameRunning)
        {
            if (enabled && entry.IsCheckbox)
                Status = "Start the game to apply cheats. Your selections are saved for next launch.";
            return;
        }

        if (_ipcSession == null || !_ipcSession.IsAttached)
        {
            Status = "shadPS4 IPC is unavailable. Launch the game from Lacrima (not an external shadPS4 window).";
            return;
        }

        if (!_ipcSession.IsMemoryPatchSupported)
        {
            Status = "Waiting for shadPS4 IPC memory patch support...";
            return;
        }

        try
        {
            var treatOffsetAsAbsolute = !ShadPs4CheatsService.ModHasHint(entry.Definition);
            foreach (var memory in entry.Definition.Memory)
            {
                var value = enabled ? memory.On : memory.Off;
                _ipcSession.SendMemoryPatch(entry.Name, memory.Offset, value, treatOffsetAsAbsolute);
            }

            Status = enabled
                ? $"Applied cheat '{entry.Name}'."
                : $"Disabled cheat '{entry.Name}'.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to apply cheat '{entry.Name}': {ex.Message}";
        }
    }

    private void UpdateStatusMessage()
    {
        if (!IsGameRunning)
        {
            if (CheatEntries.Count > 0 && Status.Contains("Loaded", StringComparison.Ordinal))
                Status += " Start the game to apply cheats via IPC.";
            return;
        }

        if (IsIpcAvailable)
            return;

        if (CheatEntries.Count > 0)
            Status = "Game is running, waiting for shadPS4 IPC memory patch support...";
    }

    private void Reset()
    {
        _emulatorDirectory = null;
        _gamePath = null;
        _ipcSession = null;
        TitleId = null;
        GameTitle = null;
        GameVersion = null;
        CreditsText = null;
        CheatFiles.Clear();
        CheatEntries.Clear();
        IsIpcAvailable = false;
        IsGameRunning = false;
        Status = "Select a PlayStation 4 game to manage cheats.";
    }
}
