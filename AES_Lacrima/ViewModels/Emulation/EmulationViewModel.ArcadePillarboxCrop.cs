using AES_Controls.Helpers;
using AES_Emulation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    public bool HasArcadeLockedPillarboxCrop =>
        ArcadePillarboxCropMetadataHelper.HasLockedCrop(_activeCaptureRomPath);

    private bool CanManageArcadePillarboxCrop() =>
        IsEmulatorRunning &&
        SupportsArcadePillarboxRemoval &&
        RemoveArcadePillarboxBars &&
        !string.IsNullOrWhiteSpace(_activeCaptureRomPath) &&
        _activeCaptureHost != null;

    private bool CanClearArcadeLockedPillarboxCrop() =>
        CanManageArcadePillarboxCrop() && HasArcadeLockedPillarboxCrop;

    [RelayCommand(CanExecute = nameof(CanManageArcadePillarboxCrop))]
    private void SaveCurrentArcadePillarboxCrop()
    {
        if (!CanManageArcadePillarboxCrop())
            return;

        if (!_activeCaptureHost!.TryGetPillarboxCrop(out var left, out var right, out var frameWidth))
            return;

        if (left <= 0 && right <= 0)
            return;

        var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(_activeCaptureRomPath);
        ArcadePillarboxCropMetadataHelper.SaveLockedCrop(romPath, left, right, frameWidth);
        _activeCaptureHost.ApplyArcadeLockedPillarboxCrop(left, right, frameWidth, romPath);
        OnPropertyChanged(nameof(HasArcadeLockedPillarboxCrop));
        ClearArcadeLockedPillarboxCropCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearArcadeLockedPillarboxCrop))]
    private void ClearArcadeLockedPillarboxCrop()
    {
        if (!CanClearArcadeLockedPillarboxCrop())
            return;

        var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(_activeCaptureRomPath);
        ArcadePillarboxCropMetadataHelper.ClearLockedCrop(romPath);
        _activeCaptureHost!.UnlockArcadePillarboxCrop(romPath);
        OnPropertyChanged(nameof(HasArcadeLockedPillarboxCrop));
        ClearArcadeLockedPillarboxCropCommand.NotifyCanExecuteChanged();
        SaveCurrentArcadePillarboxCropCommand.NotifyCanExecuteChanged();
    }

    private void NotifyArcadePillarboxCropCommandsChanged()
    {
        SaveCurrentArcadePillarboxCropCommand.NotifyCanExecuteChanged();
        ClearArcadeLockedPillarboxCropCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasArcadeLockedPillarboxCrop));
        ReloadArcadeLockedCropOnCaptureHost();
    }

    private void ReloadArcadeLockedCropOnCaptureHost()
    {
        if (string.IsNullOrWhiteSpace(_activeCaptureRomPath))
            return;

        var romPath = EmulationCoverCacheHelper.ResolveRomPathForCache(_activeCaptureRomPath);
        _activeCaptureHost?.ReloadArcadeLockedPillarboxCropFromMetadata(romPath);
    }
}
