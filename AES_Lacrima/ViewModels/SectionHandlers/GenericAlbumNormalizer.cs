using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.IO;
using AES_Lacrima.Helpers;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Generic album ROM title normalizer.
    /// Handles common normalization logic across all album types.
    /// </summary>
    public class GenericAlbumNormalizer : IAlbumNormalizer
    {
        private static readonly object LookupLock = new();
        private static readonly Xbox360MetadataService Xbox360TitleService = new();
        private static Dictionary<string, string>? _psxTitleLookup;
        private static Dictionary<string, string>? _ps2TitleLookup;

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
                catch
                {
                    // Silently fail - extraction is optional
                }
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
                var entries = JsonSerializer.Deserialize<List<RomTitleEntry>>(json) ?? [];

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
            catch
            {
                // Ignore database load failures and fall back to existing titles.
            }

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

        private sealed class RomTitleEntry
        {
            [JsonPropertyName("serial")]
            public string? Serial { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }
    }
}
