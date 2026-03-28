using AES_Lacrima.Services;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.Prompts;

/// <summary>
/// View model for the application update prompt displayed when a new release
/// or a preferred build-flavor switch is available.
/// </summary>
public partial class AppUpdatePromptViewModel : ViewModelBase
{
    public AppUpdatePromptViewModel(AppUpdateService updateService, AppReleaseInfo release)
    {
        UpdateService = updateService;
        Release = release;
    }

    /// <summary>
    /// Occurs when the prompt should be closed.
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// Gets the updater service backing the prompt state.
    /// </summary>
    public AppUpdateService UpdateService { get; }

    /// <summary>
    /// Gets the release the user is being prompted to install.
    /// </summary>
    public AppReleaseInfo Release { get; }

    public string Title => "Update Available";

    public string Message =>
        UpdateService.IsFlavorSwitchRelease(Release)
            ? $"AES - Lacrima {Release.Version} is available as a {UpdateService.PreferredUpdateFlavorLabel} build. You are currently running {UpdateService.CurrentVersionDisplay}."
            : $"AES - Lacrima {Release.Version} is available. You are currently running {UpdateService.CurrentVersionDisplay}.";

    public string SecondaryMessage =>
        UpdateService.IsFlavorSwitchRelease(Release)
            ? "Download this build to switch between AOT and non-AOT without waiting for a new version. Skipping only closes this prompt for the current session."
            : "Download the update now and the app will restart to apply it. Skipping only closes this prompt for the current session.";

    [RelayCommand]
    private async Task DownloadAndRestart()
    {
        await UpdateService.DownloadAndRestartToApplyUpdateAsync(Release);
    }

    [RelayCommand]
    private void Skip()
    {
        RequestClose?.Invoke();
    }
}
