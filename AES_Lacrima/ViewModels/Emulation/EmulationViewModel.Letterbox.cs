using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    private string? _activeCaptureRomPath;
    private int _letterboxLoadVersion;

    [ObservableProperty]
    private Bitmap? _captureLetterboxBitmap;

    public bool UseBackCoverLetterboxFill =>
        SettingsViewModel?.EmulationUseBackCoverLetterboxFill == true;

    public bool SupportsArcadePillarboxRemoval =>
        EmulationConsoleCatalog.SupportsArcadePillarboxRemoval(
            CurrentEmulationSectionItem?.SectionKey,
            CurrentEmulationSectionItem?.SectionTitle);

    public bool RemoveArcadePillarboxBars
    {
        get => CurrentEmulationSectionItem?.LaunchSettings?.RemoveArcadePillarboxBars == true;
        set
        {
            var section = CurrentEmulationSectionItem;
            if (section == null || section.RemoveArcadePillarboxBars == value)
                return;

            section.RemoveArcadePillarboxBars = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnableCapturePillarboxCrop));
            OnPropertyChanged(nameof(AggressivePillarboxCrop));
            OnPropertyChanged(nameof(EnableLetterboxPillarboxCrop));
            NotifyArcadePillarboxCropCommandsChanged();
        }
    }

    public bool AggressivePillarboxCrop => RemoveArcadePillarboxBars;

    public bool EnableLetterboxPillarboxCrop => EnableCapturePillarboxCrop;

    internal void SetActiveCaptureRomPath(string? romPath)
    {
        _activeCaptureRomPath = string.IsNullOrWhiteSpace(romPath) ? null : romPath.Trim();
        OnPropertyChanged(nameof(ActiveCaptureRomPath));
        ReloadArcadeLockedCropOnCaptureHost();
        NotifyArcadePillarboxCropCommandsChanged();
        _ = RefreshCaptureLetterboxBitmapAsync();
    }

    public string? ActiveCaptureRomPath => _activeCaptureRomPath;

    internal void ClearActiveCaptureRomPath()
    {
        _activeCaptureRomPath = null;
        OnPropertyChanged(nameof(ActiveCaptureRomPath));
        NotifyArcadePillarboxCropCommandsChanged();
        CaptureLetterboxBitmap = null;
    }

    partial void OnCaptureLetterboxBitmapChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(EnableCapturePillarboxCrop));
        OnPropertyChanged(nameof(EnableLetterboxPillarboxCrop));
    }

    private void OnEmulationUseBackCoverLetterboxFillChanged(bool enabled)
    {
        OnPropertyChanged(nameof(UseBackCoverLetterboxFill));
        OnPropertyChanged(nameof(EnableCapturePillarboxCrop));
        OnPropertyChanged(nameof(EnableLetterboxPillarboxCrop));

        if (!enabled)
        {
            CaptureLetterboxBitmap = null;
            return;
        }

        _ = RefreshCaptureLetterboxBitmapAsync();
    }

    internal void EnsureLetterboxMetadataSubscription(MetadataService metadata)
    {
        metadata.MetadataCacheSaved -= OnMetadataCacheSaved;
        metadata.MetadataCacheSaved += OnMetadataCacheSaved;
        metadata.Images.CollectionChanged -= MetadataImages_CollectionChanged;
        metadata.Images.CollectionChanged += MetadataImages_CollectionChanged;
    }

    internal void ReleaseLetterboxMetadataSubscription(MetadataService metadata)
    {
        metadata.MetadataCacheSaved -= OnMetadataCacheSaved;
        metadata.Images.CollectionChanged -= MetadataImages_CollectionChanged;
    }

    private void OnMetadataCacheSaved(string? savedPath)
    {
        RefreshAlbumCoverAfterMetadataSave(savedPath);

        if (!ShouldRefreshLetterboxForMetadataPath(savedPath))
            return;

        _ = RefreshCaptureLetterboxBitmapAsync();

        if (!string.IsNullOrWhiteSpace(_activeCaptureRomPath) &&
            EmulationCoverCacheHelper.RomPathsShareCache(savedPath, _activeCaptureRomPath))
        {
            NotifyArcadePillarboxCropCommandsChanged();
        }
    }

    private void MetadataImages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!UseBackCoverLetterboxFill || !IsEmulatorRunning)
            return;

        if (!ShouldRefreshLetterboxForMetadataPath(MetadataService?.FilePath))
            return;

        if (!CollectionChangeIncludesBackCover(e))
            return;

        _ = RefreshCaptureLetterboxBitmapAsync();
    }

    private static bool CollectionChangeIncludesBackCover(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (TagImageModel img in e.NewItems)
            {
                if (img.Kind == TagImageKind.BackCover && img.Data.Length > 0)
                    return true;
            }
        }

        if (e.OldItems != null && e.NewItems == null)
        {
            foreach (TagImageModel img in e.OldItems)
            {
                if (img.Kind == TagImageKind.BackCover)
                    return true;
            }
        }

        return false;
    }

    private bool ShouldRefreshLetterboxForMetadataPath(string? metadataPath)
    {
        if (!UseBackCoverLetterboxFill || string.IsNullOrWhiteSpace(_activeCaptureRomPath))
            return false;

        if (string.IsNullOrWhiteSpace(metadataPath))
            return false;

        var activeCachePath = Path.GetFullPath(EmulationLetterboxHelper.GetMetadataCachePath(_activeCaptureRomPath));
        var savedCachePath = Path.GetFullPath(
            metadataPath.Trim().EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? metadataPath.Trim()
                : EmulationLetterboxHelper.GetMetadataCachePath(metadataPath.Trim()));

        return string.Equals(activeCachePath, savedCachePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshCaptureLetterboxBitmapAsync()
    {
        var romPath = _activeCaptureRomPath;
        if (!UseBackCoverLetterboxFill || string.IsNullOrWhiteSpace(romPath))
        {
            await Dispatcher.UIThread.InvokeAsync(() => CaptureLetterboxBitmap = null, DispatcherPriority.Background);
            return;
        }

        var version = Interlocked.Increment(ref _letterboxLoadVersion);
        var bitmap = await Task.Run(() => EmulationLetterboxHelper.TryLoadBackCoverBitmap(romPath)).ConfigureAwait(false);
        if (version != _letterboxLoadVersion)
        {
            bitmap?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _letterboxLoadVersion)
            {
                bitmap?.Dispose();
                return;
            }

            var previous = CaptureLetterboxBitmap;
            CaptureLetterboxBitmap = bitmap;
            previous?.Dispose();
        }, DispatcherPriority.Background);
    }
}
