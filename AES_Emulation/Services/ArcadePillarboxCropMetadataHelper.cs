using AES_Controls.Helpers;

namespace AES_Emulation.Services;

public static class ArcadePillarboxCropMetadataHelper
{
    public static bool HasLockedCrop(string? romPath)
    {
        return TryLoadLockedCrop(romPath, out _, out _, out _);
    }

    public static bool TryLoadLockedCrop(string? romPath, out int left, out int right, out int frameWidth)
    {
        left = right = frameWidth = 0;
        if (string.IsNullOrWhiteSpace(romPath))
            return false;

        var resolvedRomPath = EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);
        var metadata = BinaryMetadataHelper.LoadMetadata(EmulationCoverCacheHelper.GetMetadataCachePath(resolvedRomPath));
        if (metadata == null ||
            metadata.ArcadeLockedPillarboxCropFrameWidth <= 0 ||
            (metadata.ArcadeLockedPillarboxCropLeft <= 0 && metadata.ArcadeLockedPillarboxCropRight <= 0))
        {
            return false;
        }

        left = metadata.ArcadeLockedPillarboxCropLeft;
        right = metadata.ArcadeLockedPillarboxCropRight;
        frameWidth = metadata.ArcadeLockedPillarboxCropFrameWidth;
        return true;
    }

    public static void SaveLockedCrop(string? romPath, int left, int right, int frameWidth)
    {
        if (frameWidth <= 0 || (left <= 0 && right <= 0) || string.IsNullOrWhiteSpace(romPath))
            return;

        var resolvedRomPath = EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);
        var cachePath = EmulationCoverCacheHelper.GetMetadataCachePath(resolvedRomPath);
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
        metadata.ArcadeLockedPillarboxCropLeft = left;
        metadata.ArcadeLockedPillarboxCropRight = right;
        metadata.ArcadeLockedPillarboxCropFrameWidth = frameWidth;
        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
    }

    public static void ClearLockedCrop(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return;

        var resolvedRomPath = EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);
        var cachePath = EmulationCoverCacheHelper.GetMetadataCachePath(resolvedRomPath);
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
        if (metadata == null)
            return;

        if (metadata.ArcadeLockedPillarboxCropLeft == 0 &&
            metadata.ArcadeLockedPillarboxCropRight == 0 &&
            metadata.ArcadeLockedPillarboxCropFrameWidth == 0)
        {
            return;
        }

        metadata.ArcadeLockedPillarboxCropLeft = 0;
        metadata.ArcadeLockedPillarboxCropRight = 0;
        metadata.ArcadeLockedPillarboxCropFrameWidth = 0;
        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
    }
}
