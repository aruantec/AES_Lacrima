using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AES_Code.Models;
using AES_Controls.Helpers;
using AES_Core.IO;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Shared GameCube / Wii album detection, ROM inspection, and cache persistence.
/// </summary>
public static class NintendoDiscMetadataHelper
{
    private static readonly string[] WiiPreferredExtensions =
    [
        ".wbfs",
        ".wad"
    ];

    private static readonly string[] GameCubePreferredExtensions =
    [
        ".gcm",
        ".dol",
        ".elf",
        ".tgc"
    ];

    private static readonly string[] SharedDiscExtensions =
    [
        ".iso",
        ".ciso",
        ".gcz",
        ".rvz",
        ".wia",
        ".bin"
    ];

    public static bool IsGameCubeAlbum(string? albumTitle)
    {
        if (EmulationConsoleCatalog.TryGetDefinition(albumTitle, out var definition))
            return string.Equals(definition.Key, "GCN", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, "Nintendo GameCube", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "GameCube", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "GCN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "GC", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWiiAlbum(string? albumTitle)
    {
        if (EmulationConsoleCatalog.TryGetDefinition(albumTitle, out var definition))
            return string.Equals(definition.Key, "WII", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, "Nintendo Wii", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Wii", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "WII", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNintendoDiscAlbum(string? albumTitle) =>
        IsGameCubeAlbum(albumTitle) || IsWiiAlbum(albumTitle);

    public static bool IsNintendoDiscFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return WiiPreferredExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               GameCubePreferredExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
               SharedDiscExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool ShouldLoadNintendoDiscMetadata(string? albumTitle, string? filePath) =>
        IsNintendoDiscAlbum(albumTitle) || IsNintendoDiscFile(filePath);

    public static string? ResolveAlbumTitle(string? itemAlbum, string? albumContext)
    {
        if (!string.IsNullOrWhiteSpace(albumContext))
            return albumContext.Trim();

        if (!string.IsNullOrWhiteSpace(itemAlbum))
            return itemAlbum.Trim();

        return null;
    }

    public static DiscSection ResolveDiscSection(string? albumTitle, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var extension = Path.GetExtension(filePath);
            if (IsWiiPreferredExtension(extension))
                return DiscSection.Wii;
            if (IsGameCubePreferredExtension(extension))
                return DiscSection.GameCube;

            if (File.Exists(filePath))
            {
                var sniffed = SniffDiscSectionFromHeader(filePath, extension);
                if (sniffed != DiscSection.Auto)
                    return sniffed;
            }
        }

        if (IsWiiAlbum(albumTitle) && !IsGameCubeAlbum(albumTitle))
            return DiscSection.Wii;

        if (IsGameCubeAlbum(albumTitle) && !IsWiiAlbum(albumTitle))
            return DiscSection.GameCube;

        if (IsWiiAlbum(albumTitle))
            return DiscSection.Wii;

        return DiscSection.GameCube;
    }

    /// <summary>
    /// Inspects a ROM, persists game id/title to the metadata cache, and returns the results.
    /// Re-runs inspection when a prior scan did not record a platform-specific game id.
    /// </summary>
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

    public static bool HasStoredGameId(CustomMetadata? metadata) =>
        !string.IsNullOrWhiteSpace(metadata?.WiiTitleId) ||
        !string.IsNullOrWhiteSpace(metadata?.GameCubeTitleId);

    public static bool NeedsGameIdRescan(CustomMetadata? metadata) => !HasStoredGameId(metadata);

    public static bool ShouldUpdateDiscTitle(string? currentTitle, string? extractedTitle)
    {
        if (string.IsNullOrWhiteSpace(extractedTitle))
            return false;

        return string.IsNullOrWhiteSpace(currentTitle) ||
               !string.Equals(currentTitle.Trim(), extractedTitle.Trim(), StringComparison.Ordinal);
    }

    public static bool IsFilenameLikeTitle(string? title, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(filePath))
            return false;

        var stem = Path.GetFileNameWithoutExtension(filePath);
        return string.Equals(title.Trim(), stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the best display title: disc internal name, cached metadata, then tags embedded in the cover image.
    /// </summary>
    public static string? ResolveBestTitle(string? discTitle, string? filePath, CustomMetadata? metadata)
    {
        var fromImage = TryReadTitleFromCoverImage(metadata);
        var cached = string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata!.Title.Trim();

        foreach (var candidate in new[] { discTitle, cached, fromImage })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var trimmed = candidate.Trim();
            if (!IsFilenameLikeTitle(trimmed, filePath))
                return trimmed;
        }

        if (!string.IsNullOrWhiteSpace(discTitle))
            return discTitle.Trim();

        if (!string.IsNullOrWhiteSpace(fromImage))
            return fromImage.Trim();

        return cached;
    }

    public static string? TryReadTitleFromCoverImage(CustomMetadata? metadata)
    {
        if (metadata == null)
            return null;

        foreach (var image in BinaryMetadataHelper.ReadMetadataImages(metadata))
        {
            if (image.Kind != TagImageKind.Cover || image.Data.Length == 0)
                continue;

            return TryReadTitleFromImageBytes(image.Data, ExtensionFromMimeType(image.MimeType));
        }

        return null;
    }

    public static string? TryReadTitleFromImageBytes(byte[] data, string extension = ".jpg")
    {
        if (data.Length == 0)
            return null;

        var ext = string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;
        if (!ext.StartsWith('.'))
            ext = "." + ext;

        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        try
        {
            System.IO.File.WriteAllBytes(tempPath, data);
            using var file = TagLib.File.Create(tempPath);
            var title = file.Tag?.Title;
            return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private static string ExtensionFromMimeType(string? mimeType) =>
        mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => ".jpg"
        };

    public static NintendoDiscInspectionResult InspectAndPersist(string? filePath, string? albumTitle)
    {
        filePath = NormalizeRomPath(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return NintendoDiscInspectionResult.Empty;

        var section = ResolveDiscSection(albumTitle, filePath);
        var cachePath = GetMetadataCachePath(filePath);
        var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

        if (metadata != null)
            TryMigrateMisfiledTitleId(metadata, section, cachePath);

        metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
        var cachedId = ReadTitleIdFromMetadata(metadata, section);
        if (!string.IsNullOrWhiteSpace(cachedId))
        {
            return new NintendoDiscInspectionResult(
                cachedId,
                ResolveBestTitle(null, filePath, metadata),
                section,
                FromCache: true);
        }

        // A prior scan may have set RomScanned without recording a game id (failed WBFS read, etc.).
        if (metadata?.RomScanned == true && NeedsGameIdRescan(metadata))
            metadata.RomScanned = false;

        var inspection = InspectFromDisc(filePath, section);
        var gameId = inspection.GameId;
        var title = inspection.Title;

        if (!string.IsNullOrWhiteSpace(gameId))
            section = inspection.Section != DiscSection.Auto
                ? inspection.Section
                : ResolveDiscSection(albumTitle, filePath);

        metadata ??= new CustomMetadata();
        ApplyTitleIdToMetadata(metadata, gameId, section);

        var bestTitle = ResolveBestTitle(title, filePath, metadata);
        if (ShouldUpdateDiscTitle(metadata.Title, bestTitle))
            metadata.Title = bestTitle!.Trim();

        metadata.RomScanned = true;
        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

        return new NintendoDiscInspectionResult(gameId, bestTitle, section, FromCache: false);
    }

    public static NintendoDiscInspectionResult InspectFromDisc(string? filePath, DiscSection section)
    {
        filePath = NormalizeRomPath(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return NintendoDiscInspectionResult.Empty;

        var dolphin = DolphinDiscMetadataReader.TryRead(filePath);
        if (!string.IsNullOrWhiteSpace(dolphin.GameId) || !string.IsNullOrWhiteSpace(dolphin.Title))
        {
            var resolvedSection = dolphin.Section != DiscSection.Auto
                ? dolphin.Section
                : ResolveDiscSection(null, filePath);
            return new NintendoDiscInspectionResult(
                dolphin.GameId,
                dolphin.Title,
                resolvedSection,
                FromCache: false);
        }

        var romInfo = InspectBestEffort(filePath, section);
        return new NintendoDiscInspectionResult(
            romInfo?.GameId,
            romInfo?.InternalTitle,
            section,
            FromCache: false);
    }

    public static bool TryMigrateMisfiledTitleId(
        CustomMetadata? metadata,
        DiscSection section,
        string cachePath)
    {
        if (metadata == null)
            return false;

        var migrated = false;
        if (section == DiscSection.Wii &&
            string.IsNullOrWhiteSpace(metadata.WiiTitleId) &&
            !string.IsNullOrWhiteSpace(metadata.GameCubeTitleId))
        {
            metadata.WiiTitleId = metadata.GameCubeTitleId;
            migrated = true;
        }
        else if (section == DiscSection.GameCube &&
                 string.IsNullOrWhiteSpace(metadata.GameCubeTitleId) &&
                 !string.IsNullOrWhiteSpace(metadata.WiiTitleId))
        {
            metadata.GameCubeTitleId = metadata.WiiTitleId;
            migrated = true;
        }

        if (!migrated)
            return false;

        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
        return true;
    }

    public static void ApplyTitleIdToMetadata(CustomMetadata metadata, string? gameId, DiscSection section)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return;

        if (section == DiscSection.Wii)
            metadata.WiiTitleId = gameId;
        else
            metadata.GameCubeTitleId = gameId;
    }

    public static string? ReadTitleIdFromMetadata(CustomMetadata? metadata, DiscSection section)
    {
        if (metadata == null)
            return null;

        var value = section == DiscSection.Wii ? metadata.WiiTitleId : metadata.GameCubeTitleId;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static void ApplyMetadataFlags(
        string? albumTitle,
        string? filePath,
        CustomMetadata? cachedMetadata,
        out bool isWiiMetadata,
        out bool isGameCubeMetadata)
    {
        var section = ResolveDiscSection(albumTitle, filePath);
        isWiiMetadata = section == DiscSection.Wii ||
                        !string.IsNullOrWhiteSpace(cachedMetadata?.WiiTitleId);
        isGameCubeMetadata = section == DiscSection.GameCube ||
                             !string.IsNullOrWhiteSpace(cachedMetadata?.GameCubeTitleId);
    }

    private static RomInfo? InspectBestEffort(string filePath, DiscSection section)
    {
        var primary = RomInspector.Inspect(filePath, section);
        if (!string.IsNullOrWhiteSpace(primary?.GameId))
            return primary;

        var auto = RomInspector.Inspect(filePath, DiscSection.Auto);
        if (!string.IsNullOrWhiteSpace(auto?.GameId))
            return auto;

        var alternate = section == DiscSection.Wii ? DiscSection.GameCube : DiscSection.Wii;
        return RomInspector.Inspect(filePath, alternate);
    }

    private static bool IsWiiPreferredExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) &&
        WiiPreferredExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static bool IsGameCubePreferredExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) &&
        GameCubePreferredExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static DiscSection SniffDiscSectionFromHeader(string filePath, string? extension)
    {
        _ = extension;
        return DolphinDiscMetadataReader.SniffDiscSection(filePath);
    }
}

public readonly record struct NintendoDiscInspectionResult(
    string? GameId,
    string? Title,
    DiscSection Section,
    bool FromCache)
{
    public static NintendoDiscInspectionResult Empty { get; } =
        new(null, null, DiscSection.Auto, false);
}
