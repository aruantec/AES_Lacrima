using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AES_Lacrima.Services.Rpcs3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public partial class Rpcs3CustomConfigFieldViewModel : ObservableObject
{
    private bool _boolValue;
    private int _intValue;
    private double _doubleValue;
    private string? _stringValue;
    private int _selectedChoiceIndex;

    public Rpcs3CustomConfigFieldViewModel(Rpcs3ConfigFieldDefinition definition)
    {
        Definition = definition;
        ChoiceLabels = definition.ChoiceLabels?.ToList() ?? [];
        ChoiceValues = definition.ChoiceValues ?? definition.ChoiceLabels ?? [];
    }

    public Rpcs3ConfigFieldDefinition Definition { get; }

    public string Section => Definition.Section;

    public string Key => Definition.Key;

    public string Label => Definition.Label;

    public string? Description => Definition.Description;

    public bool IsBoolean => Definition.Kind == Rpcs3ConfigValueKind.Boolean;

    public bool IsInteger => Definition.Kind == Rpcs3ConfigValueKind.Integer;

    public bool IsFloat => Definition.Kind == Rpcs3ConfigValueKind.Float;

    public bool IsString => Definition.Kind == Rpcs3ConfigValueKind.String;

    public bool IsChoice => Definition.Kind == Rpcs3ConfigValueKind.Choice;

    public int? IntMinimum => Definition.IntMin;

    public int? IntMaximum => Definition.IntMax;

    public double? FloatMinimum => Definition.FloatMin;

    public double? FloatMaximum => Definition.FloatMax;

    public IReadOnlyList<string> ChoiceLabels { get; }

    public IReadOnlyList<string> ChoiceValues { get; }

    public string CompositeKey => Rpcs3ConfigSchema.ComposeKey(Section, Key, Definition.ParentSection);

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

            SelectedChoiceIndex = index < 0 ? 0 : index;
        }
    }

    public void LoadFromString(string? rawValue)
    {
        switch (Definition.Kind)
        {
            case Rpcs3ConfigValueKind.Boolean:
                BoolValue = bool.TryParse(rawValue, out var boolean) && boolean;
                break;
            case Rpcs3ConfigValueKind.Integer:
                IntValue = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? integer
                    : Definition.IntMin ?? 0;
                break;
            case Rpcs3ConfigValueKind.Float:
                DoubleValue = double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    ? floating
                    : 0;
                break;
            case Rpcs3ConfigValueKind.Choice:
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

    public string? ToStorageString() =>
        Definition.Kind switch
        {
            Rpcs3ConfigValueKind.Boolean => BoolValue ? "true" : "false",
            Rpcs3ConfigValueKind.Integer => IntValue.ToString(CultureInfo.InvariantCulture),
            Rpcs3ConfigValueKind.Float => DoubleValue.ToString(CultureInfo.InvariantCulture),
            Rpcs3ConfigValueKind.Choice => ChoiceValues.ElementAtOrDefault(SelectedChoiceIndex) ?? string.Empty,
            _ => StringValue ?? string.Empty
        };
}
