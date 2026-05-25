using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.IO;
using AES_Lacrima.Helpers;
using AES_Lacrima.Serialization;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Generic album ROM title normalizer.
    /// Handles common normalization logic across all album types.
    /// </summary>
    public class GenericAlbumNormalizer : IAlbumNormalizer
    {
        private static readonly ILog Log = LogHelper.For<GenericAlbumNormalizer>();
        private static readonly object LookupLock = new();
        private static readonly Xbox360MetadataService Xbox360TitleService = new();
        private static Dictionary<string, string>? _psxTitleLookup;
        private static Dictionary<string, string>? _ps2TitleLookup;

        // Session-scoped record of files we've already attempted to inspect.
        // Prevents repeated heavy inspection (large ISOs, header scans) when the
        // cache yielded no usable title/id and a future album reopen would otherwise
        // trigger the same work again.
        private static readonly ConcurrentDictionary<string, byte> _inspectionAttempted =
            new(StringComparer.OrdinalIgnoreCase);

        public static string? ResolveRomTitle(string? filePath, string? albumTitle, string? currentTitle = null)
        {
            var normalizedCurrent = RomTitleNormalizationUtil.GetNormalizedRomTitle(currentTitle);
            if (string.IsNullOrWhiteSpace(normalizedCurrent) && !string.IsNullOrWhiteSpace(filePath))
                normalizedCurrent = RomTitleNormalizationUtil.GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(filePath));

            if (IsXbox360Album(albumTitle))
            {
                var xbox360Title = ResolveXbox360Title(filePath);
                if (!string.IsNullOrWhiteSpace(xbox360Title))
                    return xbox360Title;
            }

            if (IsPsxAlbum(albumTitle))
            {
                var resolvedTitle = ResolvePsTitle(filePath, false);
                if (!string.IsNullOrWhiteSpace(resolvedTitle))
                    return resolvedTitle;
            }

            if (IsPs2Album(albumTitle))
            {
                var resolvedTitle = ResolvePsTitle(filePath, true);
                if (!string.IsNullOrWhiteSpace(resolvedTitle))
                    return resolvedTitle;
            }

            if (IsPs3Album(albumTitle))
            {
                var ps3Title = Ps3InstalledGameHelper.GetTitleName(filePath);
                if (!string.IsNullOrWhiteSpace(ps3Title))
                    return ps3Title;
            }

            if (IsPs4Album(albumTitle))
            {
                var ps4Title = Ps4InstalledGameHelper.GetTitleName(filePath);
                if (!string.IsNullOrWhiteSpace(ps4Title))
                    return ps4Title;
            }

            if (IsGameCubeAlbum(albumTitle))
            {
                var gameCubeTitle = ResolveNintendoDiscTitle(filePath, DiscSection.GameCube);
                if (!string.IsNullOrWhiteSpace(gameCubeTitle))
                    return gameCubeTitle;
            }

            if (IsWiiAlbum(albumTitle))
            {
                var wiiTitle = ResolveNintendoDiscTitle(filePath, DiscSection.Wii);
                if (!string.IsNullOrWhiteSpace(wiiTitle))
                    return wiiTitle;
            }

            if (IsWiiUAlbum(albumTitle) || WiiUInstalledGameHelper.IsInstalledGameFolder(filePath))
            {
                var wiiUTitle = ResolveWiiUTitle(filePath);
                if (!string.IsNullOrWhiteSpace(wiiUTitle))
                    return wiiUTitle;
            }

            if (IsNintendo3dsAlbum(albumTitle))
            {
                var threeDsTitle = ResolveNintendo3dsTitle(filePath);
                if (!string.IsNullOrWhiteSpace(threeDsTitle))
                    return threeDsTitle;
            }

            if (IsSnesAlbum(albumTitle) || IsNesAlbum(albumTitle) || IsN64Album(albumTitle))
            {
                var cartridgeTitle = ResolveCartridgeRomTitle(filePath);
                if (!string.IsNullOrWhiteSpace(cartridgeTitle))
                    return cartridgeTitle;
            }

            return string.IsNullOrWhiteSpace(normalizedCurrent) ? null : normalizedCurrent;
        }

        public void NormalizeRomTitles(FolderMediaItem album)
        {
            if (album?.Children == null)
                return;

            foreach (var item in album.Children)
            {
                var resolvedTitle = ResolveRomTitle(item.FileName, album.Title, item.Title);
                if (!string.IsNullOrWhiteSpace(resolvedTitle) &&
                    !string.Equals(item.Title, resolvedTitle, StringComparison.Ordinal))
                {
                    item.Title = resolvedTitle;
                }
            }
        }

        private static bool IsPsxAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PSX", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PlayStation 1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPs2Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation 2", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPs3Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation 3", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPs4Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation 4", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS4", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsXbox360Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Xbox 360", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Xenia", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "X360", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGameCubeAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo GameCube", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GameCube", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GCN", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "GC", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWiiAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo Wii", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Wii", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWiiUAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo Wii U", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Wii U", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "WiiU", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "WII U", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNintendo3dsAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo 3DS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "3DS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "N3DS", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSnesAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Super Nintendo", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "SNES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "Super Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNesAlbum(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "NES", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsN64Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "Nintendo 64", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "N64", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the file has been inspected previously and we can rely
        /// on the cached metadata without touching disk. Honors both the persistent
        /// <see cref="CustomMetadata.RomScanned"/> flag and the session-scoped marker.
        /// </summary>
        public static bool IsRomMetadataAlreadyScanned(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            if (_inspectionAttempted.ContainsKey(filePath))
                return true;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
            return metadata?.RomScanned == true;
        }

        private static string? ResolveNintendoDiscTitle(string? filePath, DiscSection section)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

            // Persistent fast path: previous scan already ran for this file, so
            // trust the cache regardless of whether it yielded data.
            if (metadata?.RomScanned == true || _inspectionAttempted.ContainsKey(filePath))
                return string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim();

            if (!File.Exists(filePath) && !Directory.Exists(filePath))
                return null;

            _inspectionAttempted[filePath] = 1;

            var romInfo = RomInspector.Inspect(filePath, section);
            var gameId = romInfo?.GameId;
            var extractedTitle = romInfo?.InternalTitle;

            metadata ??= new CustomMetadata();
            if (!string.IsNullOrWhiteSpace(gameId))
            {
                if (section == DiscSection.Wii)
                    metadata.WiiTitleId = gameId;
                else
                    metadata.GameCubeTitleId = gameId;
            }

            if (ShouldUpdateMetadataTitle(metadata.Title, extractedTitle))
                metadata.Title = extractedTitle!.Trim();

            metadata.RomScanned = true;
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

            return !string.IsNullOrWhiteSpace(extractedTitle)
                ? extractedTitle.Trim()
                : (string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title.Trim());
        }

        private static string? ResolveWiiUTitle(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

            if (metadata?.RomScanned == true || _inspectionAttempted.ContainsKey(filePath))
                return string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim();

            if (!File.Exists(filePath) && !WiiUInstalledGameHelper.IsInstalledGameFolder(filePath))
                return null;

            _inspectionAttempted[filePath] = 1;

            var romInfo = RomInspector.Inspect(filePath, DiscSection.WiiU);
            var titleId = romInfo?.GameId ?? WiiUInstalledGameHelper.GetTitleId(filePath);
            var extractedTitle = romInfo?.InternalTitle ?? WiiUInstalledGameHelper.GetTitleName(filePath);

            metadata ??= new CustomMetadata();
            if (!string.IsNullOrWhiteSpace(titleId))
                metadata.WiiUTitleId = titleId;

            if (ShouldUpdateMetadataTitle(metadata.Title, extractedTitle))
                metadata.Title = extractedTitle!.Trim();

            metadata.RomScanned = true;
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

            return !string.IsNullOrWhiteSpace(extractedTitle)
                ? extractedTitle.Trim()
                : (string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title.Trim());
        }

        private static string? ResolveNintendo3dsTitle(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

            // Older versions persisted the 3DS product code (e.g. "CTR-P-AQEE")
            // as the title. Self-heal those entries by re-running inspection so
            // the SMDH-based real title can replace the product code.
            var hasStaleProductCodeTitle = metadata?.RomScanned == true &&
                                           LooksLike3dsProductCode(metadata.Title);

            if (!hasStaleProductCodeTitle &&
                (metadata?.RomScanned == true || _inspectionAttempted.ContainsKey(filePath)))
            {
                return string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim();
            }

            if (hasStaleProductCodeTitle && metadata != null)
            {
                metadata.Title = string.Empty;
                metadata.RomScanned = false;
            }

            _inspectionAttempted[filePath] = 1;

            var romInfo = RomInspector.Inspect(filePath, DiscSection.Nintendo3ds);
            var titleId = romInfo?.GameId;
            var extractedTitle = romInfo?.InternalTitle;

            metadata ??= new CustomMetadata();
            if (!string.IsNullOrWhiteSpace(titleId))
                metadata.Nintendo3dsTitleId = titleId;

            if (ShouldUpdateMetadataTitle(metadata.Title, extractedTitle))
                metadata.Title = extractedTitle!.Trim();

            metadata.RomScanned = true;
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

            if (!string.IsNullOrWhiteSpace(extractedTitle))
                return extractedTitle.Trim();

            if (!string.IsNullOrWhiteSpace(metadata.Title))
                return metadata.Title.Trim();

            // No SMDH/internal title available (encrypted dump, .3ds without ExeFS,
            // etc.). When we just self-healed away a stale product-code title, the
            // caller (item.Title) still holds that bad value, so explicitly fall
            // back to the filename-normalized title to overwrite it.
            if (hasStaleProductCodeTitle)
            {
                var fallback = RomTitleNormalizationUtil.GetNormalizedRomTitle(
                    Path.GetFileNameWithoutExtension(filePath));
                return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
            }

            return null;
        }

        private static string? ResolveCartridgeRomTitle(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);

            if (metadata?.RomScanned == true || _inspectionAttempted.ContainsKey(filePath))
                return string.IsNullOrWhiteSpace(metadata?.Title) ? null : metadata.Title.Trim();

            _inspectionAttempted[filePath] = 1;

            var romInfo = RomInspector.Inspect(filePath);
            var extractedTitle = romInfo?.InternalTitle;

            metadata ??= new CustomMetadata();
            if (!string.IsNullOrWhiteSpace(extractedTitle) &&
                ShouldUpdateMetadataTitle(metadata.Title, extractedTitle))
            {
                metadata.Title = extractedTitle.Trim();
            }

            metadata.RomScanned = true;
            BinaryMetadataHelper.SaveMetadata(cachePath, metadata);

            return string.IsNullOrWhiteSpace(metadata.Title) ? null : metadata.Title.Trim();
        }

        private static bool ShouldUpdateMetadataTitle(string? currentTitle, string? extractedTitle)
        {
            if (string.IsNullOrWhiteSpace(extractedTitle))
                return false;

            return string.IsNullOrWhiteSpace(currentTitle) ||
                   !string.Equals(currentTitle.Trim(), extractedTitle.Trim(), StringComparison.Ordinal);
        }

        private static bool LooksLike3dsProductCode(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            var trimmed = title.Trim();
            // 3DS product codes follow CTR-?-XXXX / KTR-?-XXXX / TWL-?-XXXX.
            return trimmed.Length >= 8 &&
                   trimmed.Length <= 16 &&
                   (trimmed.StartsWith("CTR-", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("KTR-", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("TWL-", StringComparison.OrdinalIgnoreCase));
        }

        private static string? ResolveXbox360Title(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var xbox360Metadata = Xbox360TitleService.TryReadGameMetadata(filePath);
            var xbox360Title = xbox360Metadata?.Title;
            if (string.IsNullOrWhiteSpace(xbox360Title))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath) ?? new CustomMetadata();
            var shouldUpdate = string.IsNullOrWhiteSpace(metadata.Title) ||
                               !string.Equals(metadata.Title.Trim(), xbox360Title.Trim(), StringComparison.Ordinal) ||
                               !string.Equals(metadata.Xbox360TitleId?.Trim(), xbox360Metadata?.TitleId?.Trim(), StringComparison.Ordinal) ||
                               !string.Equals(metadata.Xbox360MediaId?.Trim(), xbox360Metadata?.MediaId?.Trim(), StringComparison.Ordinal);

            if (shouldUpdate)
            {
                metadata.Title = xbox360Title;
                if (!string.IsNullOrWhiteSpace(xbox360Metadata?.TitleId))
                    metadata.Xbox360TitleId = xbox360Metadata.TitleId;
                if (!string.IsNullOrWhiteSpace(xbox360Metadata?.MediaId))
                    metadata.Xbox360MediaId = xbox360Metadata.MediaId;
                BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
            }

            return xbox360Title;
        }

        private static Task ExtractAndPersistPsxGameIdAsync(MediaItem item, bool preferPs2TitleId)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(item.FileName))
                        return;

                    var cachePath = GetLocalMetadataCachePath(item.FileName);
                    var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
                    var cachedGameId = preferPs2TitleId ? metadata?.Ps2TitleId : metadata?.PsXTitleId;
                    if (!string.IsNullOrWhiteSpace(cachedGameId))
                        return;

                    var romInfo = RomInspector.Inspect(item.FileName, preferPs2TitleId ? DiscSection.PS2 : DiscSection.PSX);
                    if (!string.IsNullOrWhiteSpace(romInfo?.GameId))
                    {
                        var updatedMetadata = metadata ?? new CustomMetadata();
                        if (preferPs2TitleId)
                        {
                            if (string.IsNullOrWhiteSpace(updatedMetadata.Ps2TitleId))
                                updatedMetadata.Ps2TitleId = romInfo.GameId;
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(updatedMetadata.PsXTitleId))
                                updatedMetadata.PsXTitleId = romInfo.GameId;
                        }

                        BinaryMetadataHelper.SaveMetadata(cachePath, updatedMetadata);
                    }
                }
                catch (Exception logEx) { Log.Warn("Silently fail - extraction is optional", logEx); }
            });
        }

        private static string? ResolvePsTitle(string? filePath, bool preferPs2TitleId)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var cachePath = GetLocalMetadataCachePath(filePath);
            var metadata = BinaryMetadataHelper.LoadMetadata(cachePath);
            var gameId = preferPs2TitleId ? metadata?.Ps2TitleId : metadata?.PsXTitleId;

            if (string.IsNullOrWhiteSpace(gameId))
            {
                if (!File.Exists(filePath))
                    return null;

                var romInfo = RomInspector.Inspect(filePath, preferPs2TitleId ? DiscSection.PS2 : DiscSection.PSX);
                gameId = romInfo?.GameId;

                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    var updatedMetadata = metadata ?? new CustomMetadata();
                    if (preferPs2TitleId)
                    {
                        if (string.IsNullOrWhiteSpace(updatedMetadata.Ps2TitleId))
                            updatedMetadata.Ps2TitleId = gameId;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(updatedMetadata.PsXTitleId))
                            updatedMetadata.PsXTitleId = gameId;
                    }

                    BinaryMetadataHelper.SaveMetadata(cachePath, updatedMetadata);
                }
            }

            if (string.IsNullOrWhiteSpace(gameId))
                return null;

            var lookup = preferPs2TitleId ? LoadPs2TitleLookup() : LoadPsxTitleLookup();
            if (!lookup.TryGetValue(NormalizeSerialKey(gameId), out var title) || string.IsNullOrWhiteSpace(title))
                return null;

            if (string.IsNullOrWhiteSpace(metadata?.Title) || !string.Equals(metadata.Title.Trim(), title, StringComparison.Ordinal))
            {
                var updatedMetadata = metadata ?? new CustomMetadata();
                updatedMetadata.Title = title;
                BinaryMetadataHelper.SaveMetadata(cachePath, updatedMetadata);
            }

            return title;
        }

        private static Dictionary<string, string> LoadPsxTitleLookup()
        {
            if (_psxTitleLookup != null)
                return _psxTitleLookup;

            lock (LookupLock)
            {
                if (_psxTitleLookup != null)
                    return _psxTitleLookup;

                _psxTitleLookup = LoadTitleLookupFromDatabase("psx.json", "PSX title database");
                return _psxTitleLookup;
            }
        }

        private static Dictionary<string, string> LoadPs2TitleLookup()
        {
            if (_ps2TitleLookup != null)
                return _ps2TitleLookup;

            lock (LookupLock)
            {
                if (_ps2TitleLookup != null)
                    return _ps2TitleLookup;

                _ps2TitleLookup = LoadTitleLookupFromDatabase("ps2.json", "PS2 title database");
                return _ps2TitleLookup;
            }
        }

        private static Dictionary<string, string> LoadTitleLookupFromDatabase(string fileName, string logLabel)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = EmbeddedDatabaseResource.ReadText(fileName);
            if (string.IsNullOrWhiteSpace(json))
                return lookup;

            try
            {
                var entries = JsonSerializer.Deserialize(json, RomTitleDatabaseJsonContext.Default.ListRomTitleEntry) ?? [];

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry?.Serial) || string.IsNullOrWhiteSpace(entry.Title))
                        continue;

                    var serial = NormalizeSerialKey(entry.Serial);
                    if (string.IsNullOrWhiteSpace(serial))
                        continue;

                    if (!lookup.ContainsKey(serial))
                        lookup[serial] = entry.Title.Trim();
                }
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }

            return lookup;
        }
        private static string NormalizeSerialKey(string serial)
        {
            return serial.Trim()
                         .Replace(' ', '-')
                         .Replace('_', '-')
                         .Replace('.', '-')
                         .ToUpperInvariant();
        }

        private static string GetLocalMetadataCachePath(string filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }

    }
}
