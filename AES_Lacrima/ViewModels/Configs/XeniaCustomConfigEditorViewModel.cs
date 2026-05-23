using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AES_Lacrima.Services.ShadPs4;
using AES_Lacrima.Services.Xenia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class XeniaCustomConfigEditorViewModel : ObservableObject
{
    private string? _emulatorDirectory;
    private string? _titleId;
    private string? _gameTitle;
    private string? _configFilePath;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isDirty;
    private string _status = "Select an Xbox 360 game to edit custom config.";
    private int _selectedTabIndex;
    private XeniaCustomConfigSectionViewModel? _selectedSection;
    private int _drawResolutionScale = 1;
    private IReadOnlyDictionary<string, string?> _templateValues =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<XeniaCustomConfigSectionViewModel> Sections { get; } = [];

    public IReadOnlyList<ShadPs4GpuOption> GpuOptions { get; private set; } =
        [new ShadPs4GpuOption(ShadPs4HardwareEnumeration.AutoSelectGpuId, ShadPs4HardwareEnumeration.AutoSelectGpuLabel)];

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
        string.IsNullOrWhiteSpace(GameTitle) ? "Xenia Custom Config" : $"{GameTitle} — Xenia Config";

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

    public XeniaCustomConfigSectionViewModel? SelectedSection
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

    public int DrawResolutionScaleMin => XeniaCustomConfigService.DrawResolutionScaleMin;

    public int DrawResolutionScaleMax => XeniaCustomConfigService.DrawResolutionScaleMax;

    public int DrawResolutionScale
    {
        get => _drawResolutionScale;
        set
        {
            var clamped = Math.Clamp(value, DrawResolutionScaleMin, DrawResolutionScaleMax);
            if (SetProperty(ref _drawResolutionScale, clamped))
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
        Status = "Detecting Xbox 360 Title ID and loading config...";

        try
        {
            await Task.Run(() => XeniaCustomConfigService.EnsureDefaultTemplateAsync(emulatorDirectory)).ConfigureAwait(true);

            var titleId = await Task.Run(() => XeniaTitleIdResolver.Resolve(gamePath)).ConfigureAwait(true);
            TitleId = titleId;

            if (string.IsNullOrWhiteSpace(titleId))
            {
                Sections.Clear();
                Status = "Unable to detect Xbox 360 Title ID for the selected game.";
                return;
            }

            GpuOptions = await Task.Run(ShadPs4CustomConfigService.GetGpuOptions).ConfigureAwait(true);
            OnPropertyChanged(nameof(GpuOptions));

            var overrides = await Task.Run(() => XeniaCustomConfigService.LoadOrEmpty(emulatorDirectory, titleId)).ConfigureAwait(true);
            _templateValues = await Task.Run(() =>
            {
                var templateOnly = new XeniaCustomConfigDocument();
                return (IReadOnlyDictionary<string, string?>)XeniaCustomConfigService.ReadMergedValues(emulatorDirectory, templateOnly);
            }).ConfigureAwait(true);

            var mergedValues = await Task.Run(() => XeniaCustomConfigService.ReadMergedValues(emulatorDirectory, overrides)).ConfigureAwait(true);
            BuildSections(mergedValues);
            LoadDrawResolutionScale(mergedValues);
            SelectedSection = Sections.FirstOrDefault();
            SelectedTabIndex = 0;

            ConfigFilePath = XeniaCustomConfigService.GetJsonConfigPath(emulatorDirectory, titleId);
            Status = File.Exists(ConfigFilePath)
                ? $"Loaded per-game overrides for {titleId}."
                : $"No custom config for {titleId}. Template defaults are shown.";
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
        _drawResolutionScale = 1;
        OnPropertyChanged(nameof(DrawResolutionScale));
        Status = "Select an Xbox 360 game to edit custom config.";
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
            await Task.Run(() => XeniaCustomConfigService.PrepareConfigForLaunch(_emulatorDirectory, TitleId)).ConfigureAwait(true);
            Status = $"Applied config to {XeniaCustomConfigService.ActiveConfigFileName}.";
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
        LoadDrawResolutionScale(_templateValues);
        MarkDirty();
        Status = "Restored template defaults in the editor.";
    }

    private async Task SaveCoreAsync(bool closeOnSuccess)
    {
        if (string.IsNullOrWhiteSpace(_emulatorDirectory) || string.IsNullOrWhiteSpace(TitleId))
            return;

        IsBusy = true;
        try
        {
            var currentValues = CollectCurrentValues();
            var document = await Task.Run(() =>
                XeniaCustomConfigService.BuildOverridesFromValues(currentValues, _templateValues)).ConfigureAwait(true);

            await Task.Run(() => XeniaCustomConfigService.Save(_emulatorDirectory, TitleId, document)).ConfigureAwait(true);
            ConfigFilePath = XeniaCustomConfigService.GetJsonConfigPath(_emulatorDirectory, TitleId);
            Status = document.Overrides.Count == 0
                ? $"Saved empty override file for {TitleId} (matches defaults)."
                : $"Saved {document.Overrides.Sum(static section => section.Value.Count)} override(s) for {TitleId}.";
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

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    private void BuildSections(IReadOnlyDictionary<string, string?> values)
    {
        Sections.Clear();

        foreach (var sectionHeader in XeniaConfigSchema.SectionHeaders)
        {
            var fields = new ObservableCollection<XeniaCustomConfigFieldViewModel>();
            foreach (var definition in XeniaConfigSchema.GetFieldsForSection(sectionHeader))
            {
                var fieldVm = new XeniaCustomConfigFieldViewModel(definition, GpuOptions);
                var key = $"{definition.Section}.{definition.Key}";
                values.TryGetValue(key, out var value);
                fieldVm.LoadFromString(value);
                fieldVm.PropertyChanged += (_, _) => MarkDirty();
                fields.Add(fieldVm);
            }

            Sections.Add(new XeniaCustomConfigSectionViewModel(sectionHeader, fields, this));
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

        ApplyDrawResolutionScaleToValues(values);
        return values;
    }

    private void LoadDrawResolutionScale(IReadOnlyDictionary<string, string?> values)
    {
        var scaleXKey = $"{XeniaCustomConfigService.GpuSection}.{XeniaCustomConfigService.DrawResolutionScaleXKey}";
        var scaleYKey = $"{XeniaCustomConfigService.GpuSection}.{XeniaCustomConfigService.DrawResolutionScaleYKey}";
        values.TryGetValue(scaleXKey, out var scaleX);
        values.TryGetValue(scaleYKey, out var scaleY);

        _drawResolutionScale = DrawResolutionScaleMin;
        if (int.TryParse(scaleX, out var parsedX))
            _drawResolutionScale = parsedX;
        else if (int.TryParse(scaleY, out var parsedY))
            _drawResolutionScale = parsedY;

        _drawResolutionScale = Math.Clamp(_drawResolutionScale, DrawResolutionScaleMin, DrawResolutionScaleMax);
        OnPropertyChanged(nameof(DrawResolutionScale));
    }

    private void ApplyDrawResolutionScaleToValues(IDictionary<string, string?> values)
    {
        var scale = DrawResolutionScale.ToString();
        values[$"{XeniaCustomConfigService.GpuSection}.{XeniaCustomConfigService.DrawResolutionScaleXKey}"] = scale;
        values[$"{XeniaCustomConfigService.GpuSection}.{XeniaCustomConfigService.DrawResolutionScaleYKey}"] = scale;
    }
}
