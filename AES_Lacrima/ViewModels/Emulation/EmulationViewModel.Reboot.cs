using System.IO;
using AES_Core.Logging;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    private bool CanRebootEmulator() =>
        IsEmulatorRunning &&
        !IsEmulatorLaunchInProgress &&
        !_isClosingActiveEmulatorForRelaunch &&
        _activeEmulationSessionItem is { FileName: { Length: > 0 } } &&
        CurrentEmulatorHandler?.IsLauncherPathValid(CurrentEmulatorHandler.LauncherPath) == true;

    [RelayCommand(CanExecute = nameof(CanRebootEmulator))]
    private void RebootEmulator()
    {
        var request = TryCreatePendingEmulatorLaunchRequestFromActiveSession();
        if (request == null)
            return;

        SLog.Info($"EmulationViewModel.RebootEmulator requested for '{request.ItemTitle}'.");
        ClearRetroArchErrorState();
        RequestEmulatorLaunch(request);
    }

    private PendingEmulatorLaunchRequest? TryCreatePendingEmulatorLaunchRequestFromActiveSession()
    {
        var item = _activeEmulationSessionItem;
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return null;

        var album = LoadedAlbum ?? SelectedAlbum;
        var albumTitle = album?.Title ?? item.Album ?? string.Empty;

        var handler = CurrentEmulatorHandler;
        if (handler == null && !string.IsNullOrWhiteSpace(albumTitle))
            handler = SettingsViewModel?.GetConfiguredEmulatorHandler(albumTitle);

        if (handler == null || !handler.IsLauncherPathValid(handler.LauncherPath))
            return null;

        var launchSettings = SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(albumTitle);
        return new PendingEmulatorLaunchRequest(
            albumTitle,
            item.Title ?? Path.GetFileNameWithoutExtension(item.FileName),
            handler,
            item.FileName,
            launchSettings);
    }
}
