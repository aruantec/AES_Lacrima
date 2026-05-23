using System;
using System.Collections.ObjectModel;
using AES_Lacrima.Services.Xenia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public sealed class XeniaCustomConfigSectionViewModel : ObservableObject
{
    public XeniaCustomConfigSectionViewModel(
        string header,
        ObservableCollection<XeniaCustomConfigFieldViewModel> fields,
        XeniaCustomConfigEditorViewModel? editor = null)
    {
        Header = header;
        Fields = fields;
        IsGpuSection = string.Equals(header, XeniaCustomConfigService.GpuSection, StringComparison.OrdinalIgnoreCase);
        Editor = IsGpuSection ? editor : null;
    }

    public string Header { get; }

    public bool IsGpuSection { get; }

    public XeniaCustomConfigEditorViewModel? Editor { get; }

    public ObservableCollection<XeniaCustomConfigFieldViewModel> Fields { get; }
}
