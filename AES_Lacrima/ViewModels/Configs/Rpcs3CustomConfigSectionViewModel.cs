using System;
using System.Collections.ObjectModel;
using AES_Lacrima.Services.Rpcs3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public sealed class Rpcs3CustomConfigSectionViewModel : ObservableObject
{
    public Rpcs3CustomConfigSectionViewModel(
        string header,
        ObservableCollection<Rpcs3CustomConfigFieldViewModel> fields,
        Rpcs3CustomConfigEditorViewModel? editor = null)
    {
        Header = header;
        Fields = fields;
        IsVideoSection = string.Equals(header, Rpcs3ConfigSchema.VideoSection, StringComparison.OrdinalIgnoreCase);
        Editor = IsVideoSection ? editor : null;
    }

    public string Header { get; }

    public bool IsVideoSection { get; }

    public Rpcs3CustomConfigEditorViewModel? Editor { get; }

    public ObservableCollection<Rpcs3CustomConfigFieldViewModel> Fields { get; }
}
