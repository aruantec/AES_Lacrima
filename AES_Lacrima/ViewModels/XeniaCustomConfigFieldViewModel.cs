using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AES_Lacrima.Services.ShadPs4;
using AES_Lacrima.Services.Xenia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public partial class XeniaCustomConfigFieldViewModel : ObservableObject
{
    private bool _boolValue;
    private int _intValue;
    private double _doubleValue;
    private string? _stringValue;
    private int _selectedChoiceIndex;
    private int _selectedGpuAdapterIndex;

    public XeniaCustomConfigFieldViewModel(
        XeniaConfigFieldDefinition definition,
        IReadOnlyList<ShadPs4GpuOption> gpuOptions)
    {
        Definition = definition;
        GpuOptions = gpuOptions;
        ChoiceLabels = definition.ChoiceLabels?.ToList() ?? [];
        ChoiceValues = definition.ChoiceValues ?? definition.ChoiceLabels ?? [];
    }

    public XeniaConfigFieldDefinition Definition { get; }

    public string Section => Definition.Section;

    public string Key => Definition.Key;

    public string Label => Definition.Label;

    public string? Description => Definition.Description;

    public bool IsBoolean => Definition.Kind == XeniaConfigValueKind.Boolean;

    public bool IsInteger => Definition.Kind == XeniaConfigValueKind.Integer;

    public bool IsFloat => Definition.Kind == XeniaConfigValueKind.Float;

    public bool IsString => Definition.Kind == XeniaConfigValueKind.String;

    public bool IsChoice => Definition.Kind == XeniaConfigValueKind.Choice;

    public bool IsGpuAdapter => Definition.Kind == XeniaConfigValueKind.GpuAdapterIndex;

    public int? IntMinimum => Definition.IntMin;

    public int? IntMaximum => Definition.IntMax;

    public double? FloatMinimum => Definition.FloatMin;

    public double? FloatMaximum => Definition.FloatMax;

    public IReadOnlyList<string> ChoiceLabels { get; }

    public IReadOnlyList<string> ChoiceValues { get; }

    public IReadOnlyList<ShadPs4GpuOption> GpuOptions { get; }

    public IReadOnlyList<string> GpuAdapterLabels =>
        GpuOptions.Select(static option => option.Label).ToList();

    public string CompositeKey => $"{Section}.{Key}";

    public bool BoolValue
    {
        get => _boolValue;
        set => SetProperty(ref _boolValue, value);
    }

    public int IntValue
    {
        get => _intValue;
        set => SetProperty(ref _intValue, value);
    }

    public double DoubleValue
    {
        get => _doubleValue;
        set => SetProperty(ref _doubleValue, value);
    }

    public string? StringValue
    {
        get => _stringValue;
        set => SetProperty(ref _stringValue, value);
    }

    public int SelectedChoiceIndex
    {
        get => _selectedChoiceIndex;
        set
        {
            if (SetProperty(ref _selectedChoiceIndex, value))
                OnPropertyChanged(nameof(SelectedChoiceLabel));
        }
    }

    public string? SelectedChoiceLabel
    {
        get
        {
            if (SelectedChoiceIndex < 0 || SelectedChoiceIndex >= ChoiceLabels.Count)
                return ChoiceLabels.FirstOrDefault();

            return ChoiceLabels[SelectedChoiceIndex];
        }
        set
        {
            var index = ChoiceLabels
                .Select((label, idx) => (label, idx))
                .FirstOrDefault(entry => string.Equals(entry.label, value, StringComparison.OrdinalIgnoreCase))
                .idx;

            if (index < 0)
                index = 0;

            SelectedChoiceIndex = index;
        }
    }

    public int SelectedGpuAdapterIndex
    {
        get => _selectedGpuAdapterIndex;
        set => SetProperty(ref _selectedGpuAdapterIndex, value);
    }

    public void LoadFromString(string? rawValue)
    {
        switch (Definition.Kind)
        {
            case XeniaConfigValueKind.Boolean:
                BoolValue = bool.TryParse(rawValue, out var boolean) && boolean;
                break;
            case XeniaConfigValueKind.Integer:
            case XeniaConfigValueKind.GpuAdapterIndex:
                IntValue = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : Definition.Kind == XeniaConfigValueKind.GpuAdapterIndex ? ShadPs4HardwareEnumeration.AutoSelectGpuId : 0;
                if (Definition.Kind == XeniaConfigValueKind.GpuAdapterIndex)
                {
                    for (var index = 0; index < GpuOptions.Count; index++)
                    {
                        if (GpuOptions[index].GpuId == IntValue)
                        {
                            SelectedGpuAdapterIndex = index;
                            break;
                        }
                    }
                }
                break;
            case XeniaConfigValueKind.Float:
                DoubleValue = double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    ? floating
                    : 0;
                break;
            case XeniaConfigValueKind.Choice:
                var value = rawValue ?? string.Empty;
                var choiceIndex = ChoiceValues
                    .Select((choice, idx) => (choice, idx))
                    .FirstOrDefault(entry => string.Equals(entry.choice, value, StringComparison.OrdinalIgnoreCase))
                    .idx;
                SelectedChoiceIndex = choiceIndex >= 0 ? choiceIndex : 0;
                break;
            default:
                StringValue = rawValue ?? string.Empty;
                break;
        }
    }

    public string? ToStorageString()
    {
        return Definition.Kind switch
        {
            XeniaConfigValueKind.Boolean => BoolValue ? "true" : "false",
            XeniaConfigValueKind.Integer => IntValue.ToString(CultureInfo.InvariantCulture),
            XeniaConfigValueKind.Float => DoubleValue.ToString(CultureInfo.InvariantCulture),
            XeniaConfigValueKind.GpuAdapterIndex => GpuOptions.ElementAtOrDefault(SelectedGpuAdapterIndex)?.GpuId.ToString(CultureInfo.InvariantCulture)
                ?? IntValue.ToString(CultureInfo.InvariantCulture),
            XeniaConfigValueKind.Choice => ChoiceValues.ElementAtOrDefault(SelectedChoiceIndex) ?? string.Empty,
            _ => StringValue ?? string.Empty
        };
    }
}
