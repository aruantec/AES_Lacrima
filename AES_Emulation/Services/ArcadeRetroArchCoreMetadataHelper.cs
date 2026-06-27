using AES_Controls.Helpers;

namespace AES_Emulation.Services;

public static class ArcadeRetroArchCoreMetadataHelper
{
    public static bool HasCoreOverride(string? romPath)
    {
        return !string.IsNullOrWhiteSpace(GetCoreOverride(romPath));
    }

    public static string? GetCoreOverride(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        var resolvedRomPath = EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);
        var metadata = BinaryMetadataHelper.LoadMetadata(EmulationCoverCacheHelper.GetMetadataCachePath(resolvedRomPath));
        return string.IsNullOrWhiteSpace(metadata?.ArcadeRetroArchCore)
            ? null
            : metadata.ArcadeRetroArchCore;
    }

    public static void SaveCoreOverride(string? romPath, string? coreFileName)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return;

        var resolvedRomPath = EmulationCoverCacheHelper.ResolveRomPathForCache(romPath);
        var cachePath = EmulationCoverCacheHelper.GetMetadataCachePath(resolvedRomPath);
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
        metadata.ArcadeRetroArchCore = string.IsNullOrWhiteSpace(coreFileName) ? string.Empty : coreFileName.Trim();
        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
    }

    public static void ClearCoreOverride(string? romPath)
        => SaveCoreOverride(romPath, null);
}
