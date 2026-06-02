using System;
using System.IO;
using AES_Controls.Helpers;
using AES_Core.IO;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Nintendo Switch album detection, ROM inspection, and metadata cache persistence.
/// </summary>
public static class SwitchRomMetadataHelper
{
    public static bool IsSwitchAlbum(string? albumTitle)
    {
        if (EmulationConsoleCatalog.TryGetDefinition(albumTitle, out var definition))
            return string.Equals(definition.Key, "SWITCH", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, "Nintendo Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "SWITCH", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSwitchFile(string? filePath) => Switch.SwitchRomMetadataReader.IsSwitchFile(filePath);

    public static bool ShouldLoadSwitchMetadata(string? albumTitle, string? filePath) =>
        IsSwitchAlbum(albumTitle) || IsSwitchFile(filePath);

    public static string? NormalizeRomPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        try
        {
            return Path.GetFullPath(filePath.Trim());
        }
        catch
        {
            return filePath.Trim();
        }
    }

    public static string GetMetadataCachePath(string? filePath)
    {
        var normalized = NormalizeRomPath(filePath) ?? filePath ?? string.Empty;
        return ApplicationPaths.GetCacheFile(BinaryMetadataHelper.GetCacheId(normalized) + ".meta");
    }

    public static bool HasStoredTitleId(CustomMetadata? metadata) =>
        !string.IsNullOrWhiteSpace(metadata?.SwitchTitleId);

    public static bool NeedsTitleIdRescan(CustomMetadata? metadata) => !HasStoredTitleId(metadata);

    public static bool ShouldUpdateTitle(string? currentTitle, string? extractedTitle)
    {
        if (string.IsNullOrWhiteSpace(extractedTitle))
            return false;

        return string.IsNullOrWhiteSpace(currentTitle) ||
               !string.Equals(currentTitle.Trim(), extractedTitle.Trim(), StringComparison.Ordinal);
    }

    public static SwitchInspectionResult InspectAndPersist(string? filePath, string? albumTitle)
    {
        _ = albumTitle;
        filePath = NormalizeRomPath(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return SwitchInspectionResult.Empty;

        if (Directory.Exists(filePath) || File.Exists(filePath))
        {
            // continue
        }
        else
        {
            return SwitchInspectionResult.Empty;
        }

        var cachePath = GetMetadataCachePath(filePath);
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
        var cachedId = metadata?.SwitchTitleId?.Trim();
        if (!string.IsNullOrWhiteSpace(cachedId) && metadata?.RomScanned == true)
        {
            var cachedTitle = ResolveBestTitle(null, filePath, metadata);
            if (string.IsNullOrWhiteSpace(cachedTitle))
            {
                var refreshed = InspectFromRom(filePath);
                cachedTitle = ResolveBestTitle(refreshed.Title, filePath, metadata);
                if (ShouldUpdateTitle(metadata?.Title, cachedTitle))
                {
                    metadata ??= new CustomMetadata();
                    metadata.Title = cachedTitle!.Trim();
                    BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
                }
            }

            return new SwitchInspectionResult(cachedId, cachedTitle, FromCache: true);
        }

        if (metadata?.RomScanned == true && NeedsTitleIdRescan(metadata))
            metadata.RomScanned = false;

        var inspection = InspectFromRom(filePath);
        metadata ??= new CustomMetadata();

        if (!string.IsNullOrWhiteSpace(inspection.TitleId))
            metadata.SwitchTitleId = inspection.TitleId;

        var bestTitle = ResolveBestTitle(inspection.Title, filePath, metadata);
        if (ShouldUpdateTitle(metadata.Title, bestTitle))
            metadata.Title = bestTitle!.Trim();

        // Only mark fully scanned when a title ID was found so encrypted dumps can be retried after renames.
        metadata.RomScanned = !string.IsNullOrWhiteSpace(inspection.TitleId);
        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

        return new SwitchInspectionResult(inspection.TitleId, bestTitle, FromCache: false);
    }

    public static SwitchInspectionResult InspectFromRom(string? filePath)
    {
        filePath = NormalizeRomPath(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return SwitchInspectionResult.Empty;

        var result = Switch.SwitchRomMetadataReader.TryRead(filePath);
        return new SwitchInspectionResult(result.TitleId, result.DisplayTitle, FromCache: false);
    }

    public static string? ResolveBestTitle(string? romTitle, string? filePath, CustomMetadata? metadata)
    {
        var fromImage = NintendoDiscMetadataHelper.TryReadTitleFromCoverImage(metadata);

        foreach (var candidate in new[] { romTitle, metadata?.Title, fromImage })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var trimmed = candidate.Trim();
            if (!NintendoDiscMetadataHelper.IsFilenameLikeTitle(trimmed, filePath))
                return trimmed;
        }

        if (!string.IsNullOrWhiteSpace(romTitle))
            return romTitle.Trim();

        if (!string.IsNullOrWhiteSpace(fromImage))
            return fromImage.Trim();

        return string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim();
    }
}

public readonly record struct SwitchInspectionResult(
    string? TitleId,
    string? Title,
    bool FromCache)
{
    public static SwitchInspectionResult Empty { get; } = new(null, null, false);
}
