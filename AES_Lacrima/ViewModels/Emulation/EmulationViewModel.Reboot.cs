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
        CurrentEmulatorHandler?.HasLauncherPath == true;

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

        var handler = album != null
            ? ResolveEmulatorHandlerForAlbum(album)
            : CurrentEmulatorHandler;

        if (handler == null || !handler.HasLauncherPath)
            return null;

        var launchSettings = album != null
            ? ResolveEmulationLaunchSettingsForAlbum(album)
            : SettingsViewModel?.GetResolvedEmulationSectionLaunchSettings(albumTitle);
        return new PendingEmulatorLaunchRequest(
            albumTitle,
            item.Title ?? Path.GetFileNameWithoutExtension(item.FileName),
            handler,
            item.FileName,
            launchSettings);
    }
}
