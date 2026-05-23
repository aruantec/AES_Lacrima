using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Rpcs3;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class Rpcs3CustomConfigEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _titleId;
    private string? _gameTitle;
    private string? _configFilePath;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isDirty;
    private string _status = "Select a PlayStation 3 game to edit custom config.";
    private int _selectedTabIndex;
    private Rpcs3CustomConfigSectionViewModel? _selectedSection;
    private int _resolutionScale = 100;
    private int _resolutionScaleThreshold = 16;
    private IReadOnlyDictionary<string, string?> _templateValues =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<Rpcs3CustomConfigSectionViewModel> Sections { get; } = [];

    public string? TitleId
    {
        get => _titleId;
        private set
        {
            if (SetProperty(ref _titleId, value))
                OnPropertyChanged(nameof(OverlayHeader));
        }
    }

    public string? GameTitle
    {
        get => _gameTitle;
        private set
        {
            if (SetProperty(ref _gameTitle, value))
                OnPropertyChanged(nameof(OverlayHeader));
        }
    }

    public string OverlayHeader =>
        string.IsNullOrWhiteSpace(GameTitle) ? "RPCS3 Custom Config" : $"{GameTitle} — RPCS3 Config";

    public string? ConfigFilePath
    {
        get => _configFilePath;
        private set => SetProperty(ref _configFilePath, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public Rpcs3CustomConfigSectionViewModel? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value))
                return;

            var index = value == null ? 0 : Sections.IndexOf(value);
            if (index >= 0 && _selectedTabIndex != index)
            {
                _selectedTabIndex = index;
                OnPropertyChanged(nameof(SelectedTabIndex));
            }

            OnPropertyChanged(nameof(SelectedSectionHeader));
        }
    }

    public string SelectedSectionHeader =>
        SelectedSection?.Header ?? "Options";

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, value))
                return;

            if (value >= 0 && value < Sections.Count)
            {
                _selectedSection = Sections[value];
                OnPropertyChanged(nameof(SelectedSection));
                OnPropertyChanged(nameof(SelectedSectionHeader));
            }
        }
    }

    public int ResolutionScaleMin => 25;

    public int ResolutionScaleMax => 800;

    public int ResolutionScaleThresholdMin => 1;

    public int ResolutionScaleThresholdMax => 1024;

    public int ResolutionScaleThreshold
    {
        get => _resolutionScaleThreshold;
        set
        {
            var clamped = Math.Clamp(value, ResolutionScaleThresholdMin, ResolutionScaleThresholdMax);
            if (SetProperty(ref _resolutionScaleThreshold, clamped))
                MarkDirty();
        }
    }

    public int ResolutionScale
    {
        get => _resolutionScale;
        set
        {
            var clamped = Math.Clamp(value, ResolutionScaleMin, ResolutionScaleMax);
            if (SetProperty(ref _resolutionScale, clamped))
                MarkDirty();
        }
    }

    public async Task LoadAsync(string? emulatorDirectory, string gamePath, string? gameTitle)
    {
        IsOpen = true;
        IsBusy = true;
        IsDirty = false;
        _emulatorDirectory = emulatorDirectory;
        GameTitle = gameTitle;
        TitleId = null;
        ConfigFilePath = null;
        Status = "Detecting PS3 Title ID and loading config...";

        try
        {
            await Task.Run(() =>
            {
                Rpcs3CustomConfigService.EnsureDefaultTemplate(emulatorDirectory);
                Rpcs3CustomConfigService.TryMigrateLegacyStorage(emulatorDirectory);
            }).ConfigureAwait(true);

            var titleId = await Task.Run(() => Ps3InstalledGameHelper.GetTitleId(gamePath)).ConfigureAwait(true);
            TitleId = string.IsNullOrWhiteSpace(titleId) ? null : Rpcs3CustomConfigService.NormalizeTitleId(titleId);

            if (string.IsNullOrWhiteSpace(TitleId))
            {
                Sections.Clear();
                Status = "Unable to detect PS3 Title ID for the selected game.";
                return;
            }

            _templateValues = await Task.Run(() => Rpcs3CustomConfigService.ReadTemplateValues(emulatorDirectory)).ConfigureAwait(true);
            var mergedValues = await Task.Run(() => Rpcs3CustomConfigService.ReadMergedValues(emulatorDirectory, TitleId)).ConfigureAwait(true);
            BuildSections(mergedValues);
            LoadVideoSliders(mergedValues);
            SelectedSection = Sections.FirstOrDefault();
            SelectedTabIndex = 0;

            ConfigFilePath = Rpcs3CustomConfigService.GetAesCustomConfigPath(emulatorDirectory, TitleId);
            Status = File.Exists(ConfigFilePath)
                ? $"Loaded per-game config for {TitleId}."
                : $"No custom config for {TitleId}. Template defaults are shown.";
            IsDirty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Reset()
    {
        IsOpen = false;
        IsBusy = false;
        IsDirty = false;
        TitleId = null;
        GameTitle = null;
        ConfigFilePath = null;
        Sections.Clear();
        _selectedSection = null;
        OnPropertyChanged(nameof(SelectedSection));
        OnPropertyChanged(nameof(SelectedSectionHeader));
        _resolutionScale = 100;
        _resolutionScaleThreshold = 16;
        OnPropertyChanged(nameof(ResolutionScale));
        OnPropertyChanged(nameof(ResolutionScaleThreshold));
        Status = "Select a PlayStation 3 game to edit custom config.";
    }

    public void MarkDirty() => IsDirty = true;

    [RelayCommand]
    private async Task SaveAsync() => await SaveCoreAsync(closeOnSuccess: true).ConfigureAwait(true);

    [RelayCommand]
    private async Task ApplyNowAsync()
    {
        if (string.IsNullOrWhiteSpace(_emulatorDirectory) || string.IsNullOrWhiteSpace(TitleId))
            return;

        IsBusy = true;
        try
        {
            await SaveCoreAsync(closeOnSuccess: false).ConfigureAwait(true);
            await Task.Run(() => Rpcs3CustomConfigService.PrepareConfigForLaunch(_emulatorDirectory, TitleId)).ConfigureAwait(true);
            Status = $"Applied config for {TitleId} to RPCS3.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to apply config: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RestoreTemplateDefaults()
    {
        ApplyValuesToFields(_templateValues);
        LoadVideoSliders(_templateValues);
        MarkDirty();
        Status = "Restored template defaults in the editor.";
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private async Task SaveCoreAsync(bool closeOnSuccess)
    {
        if (string.IsNullOrWhiteSpace(_emulatorDirectory) || string.IsNullOrWhiteSpace(TitleId))
            return;

        IsBusy = true;
        try
        {
            var currentValues = CollectCurrentValues();
            await Task.Run(() => Rpcs3CustomConfigService.SaveValues(_emulatorDirectory, TitleId, currentValues)).ConfigureAwait(true);
            ConfigFilePath = Rpcs3CustomConfigService.GetAesCustomConfigPath(_emulatorDirectory, TitleId);
            Status = $"Saved custom config for {TitleId}.";
            IsDirty = false;

            if (closeOnSuccess)
                IsOpen = false;
        }
        catch (Exception ex)
        {
            Status = $"Failed to save config: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildSections(IReadOnlyDictionary<string, string?> values)
    {
        Sections.Clear();

        foreach (var sectionHeader in Rpcs3ConfigSchema.UiSectionHeaders)
        {
            var fields = new ObservableCollection<Rpcs3CustomConfigFieldViewModel>();
            foreach (var definition in Rpcs3ConfigSchema.GetFieldsForUiSection(sectionHeader))
            {
                if (IsVideoSliderField(definition))
                    continue;

                var fieldVm = new Rpcs3CustomConfigFieldViewModel(definition);
                values.TryGetValue(Rpcs3ConfigSchema.ComposeKey(definition.Section, definition.Key, definition.ParentSection), out var value);
                fieldVm.LoadFromString(value);
                fieldVm.PropertyChanged += (_, _) => MarkDirty();
                fields.Add(fieldVm);
            }

            Sections.Add(new Rpcs3CustomConfigSectionViewModel(sectionHeader, fields, this));
        }

        if (Sections.Count > 0 && _selectedSection == null)
            SelectedSection = Sections[0];
    }

    private void ApplyValuesToFields(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var section in Sections)
        {
            foreach (var field in section.Fields)
            {
                values.TryGetValue(field.CompositeKey, out var value);
                field.LoadFromString(value);
            }
        }
    }

    private Dictionary<string, string?> CollectCurrentValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Sections)
        {
            foreach (var field in section.Fields)
                values[field.CompositeKey] = field.ToStorageString();
        }

        ApplyVideoSlidersToValues(values);
        return values;
    }

    private static bool IsVideoSliderField(Rpcs3ConfigFieldDefinition definition) =>
        string.Equals(definition.Section, Rpcs3ConfigSchema.VideoSection, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(definition.Key, Rpcs3ConfigSchema.ResolutionScaleKey, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(definition.Key, Rpcs3ConfigSchema.ResolutionScaleThresholdKey, StringComparison.OrdinalIgnoreCase));

    private void LoadVideoSliders(IReadOnlyDictionary<string, string?> values)
    {
        var scaleKey = Rpcs3ConfigSchema.ComposeKey(Rpcs3ConfigSchema.VideoSection, Rpcs3ConfigSchema.ResolutionScaleKey);
        values.TryGetValue(scaleKey, out var rawScale);
        _resolutionScale = int.TryParse(rawScale, out var scale) ? Math.Clamp(scale, ResolutionScaleMin, ResolutionScaleMax) : 100;
        OnPropertyChanged(nameof(ResolutionScale));

        var thresholdKey = Rpcs3ConfigSchema.ComposeKey(Rpcs3ConfigSchema.VideoSection, Rpcs3ConfigSchema.ResolutionScaleThresholdKey);
        values.TryGetValue(thresholdKey, out var rawThreshold);
        _resolutionScaleThreshold = int.TryParse(rawThreshold, out var threshold)
            ? Math.Clamp(threshold, ResolutionScaleThresholdMin, ResolutionScaleThresholdMax)
            : 16;
        OnPropertyChanged(nameof(ResolutionScaleThreshold));
    }

    private void ApplyVideoSlidersToValues(IDictionary<string, string?> values)
    {
        values[Rpcs3ConfigSchema.ComposeKey(Rpcs3ConfigSchema.VideoSection, Rpcs3ConfigSchema.ResolutionScaleKey)] =
            ResolutionScale.ToString();
        values[Rpcs3ConfigSchema.ComposeKey(Rpcs3ConfigSchema.VideoSection, Rpcs3ConfigSchema.ResolutionScaleThresholdKey)] =
            ResolutionScaleThreshold.ToString();
    }
}
