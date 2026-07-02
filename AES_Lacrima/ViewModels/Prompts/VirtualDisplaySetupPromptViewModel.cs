using AES_Controls.Helpers;
using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.Prompts;

public partial class VirtualDisplaySetupPromptViewModel : ViewModelBase
{
    private readonly VirtualDisplayDriverManager? _virtualDisplayDriver;
    private readonly Action _onOpenSettings;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _message =
        "AES needs the Virtual Display Driver for reliable game capture on Windows (the same role gamescope plays on Linux). " +
        "Click Install and approve the administrator prompt — Lacrima handles the rest automatically.";

    public event Action? RequestClose;

    public VirtualDisplaySetupPromptViewModel(
        VirtualDisplayDriverManager? virtualDisplayDriver,
        Action onOpenSettings,
        string? message = null)
    {
        _virtualDisplayDriver = virtualDisplayDriver;
        _onOpenSettings = onOpenSettings;
        if (!string.IsNullOrWhiteSpace(message))
            Message = message;
    }

    [RelayCommand]
    private async Task Install()
    {
        if (_virtualDisplayDriver == null)
        {
            Message = "Virtual Display Driver manager is unavailable.";
            return;
        }

        IsInstalling = true;
        try
        {
            var success = await _virtualDisplayDriver.EnsureInstalledAsync().ConfigureAwait(true);

            if (DiLocator.ResolveViewModel<SettingsViewModel>() is { } settings)
                await settings.RefreshVirtualDisplayDriverInfo().ConfigureAwait(true);

            if (success)
            {
                RequestClose?.Invoke();
                return;
            }

            Message = string.IsNullOrWhiteSpace(_virtualDisplayDriver.Status)
                ? "Virtual Display Driver installation did not complete. Open Settings → Tools for details, then retry."
                : _virtualDisplayDriver.Status;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _onOpenSettings();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Skip()
    {
        RequestClose?.Invoke();
    }
}
