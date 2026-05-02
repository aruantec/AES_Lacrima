using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels.Prompts;

public sealed record FlatpakApplicationItem(string ApplicationId, string DisplayName, string DesktopFilePath)
{
    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(DisplayName) || string.Equals(DisplayName, ApplicationId, StringComparison.OrdinalIgnoreCase)
            ? ApplicationId
            : DisplayName;
}

public partial class FlatpakApplicationSelectorPromptViewModel : ViewModelBase
{
    private readonly Action<FlatpakApplicationItem?> _onSelectionConfirmed;

    public FlatpakApplicationSelectorPromptViewModel(IReadOnlyList<FlatpakApplicationItem> applications, Action<FlatpakApplicationItem?> onSelectionConfirmed)
    {
        Applications = applications;
        _onSelectionConfirmed = onSelectionConfirmed;

        if (Applications.Count == 1)
        {
            SelectedApplication = Applications[0];
        }
    }

    public event Action? RequestClose;

    public IReadOnlyList<FlatpakApplicationItem> Applications { get; }

    public bool HasApplications => Applications.Count > 0;

    public string Title => "Flatpak Applications";

    public string Message =>
        HasApplications
            ? "Choose an installed Flatpak application to use as the launcher for this emulator."
            : "No Flatpak applications were found on this system.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private FlatpakApplicationItem? _selectedApplication;

    private bool CanSelect() => SelectedApplication != null;

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void Select()
    {
        _onSelectionConfirmed(SelectedApplication);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }
}