using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.IO;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Generic album ROM title normalizer.
    /// Handles common normalization logic across all album types.
    /// </summary>
    public class GenericAlbumNormalizer : IAlbumNormalizer
    {
        public void NormalizeRomTitles(FolderMediaItem album)
        {
            if (album?.Children == null)
                return;

            foreach (var item in album.Children)
            {
                var ps3Title = Ps3InstalledGameHelper.GetTitleName(item.FileName);
                var ps4Title = Ps4InstalledGameHelper.GetTitleName(item.FileName);
                var normalized = RomTitleNormalizationUtil.GetNormalizedRomTitle(item.Title);
                if (string.IsNullOrWhiteSpace(normalized) && !string.IsNullOrWhiteSpace(item.FileName))
                    normalized = RomTitleNormalizationUtil.GetNormalizedRomTitle(Path.GetFileNameWithoutExtension(item.FileName));

                // Scan PSX and PS2 discs to extract GameId in the background.
                var isPsxAlbum = IsPsxAlbum(album.Title);
                var isPs2Album = IsPs2Album(album.Title);
                if ((isPsxAlbum || isPs2Album) && !string.IsNullOrWhiteSpace(item.FileName) && File.Exists(item.FileName))
                {
                    _ = ExtractAndPersistPsxGameIdAsync(item, isPs2Album);
                }

                if (!string.IsNullOrWhiteSpace(ps3Title) &&
                    (string.IsNullOrWhiteSpace(item.Title) ||
                     string.Equals(item.Title, normalized, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Title, Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase)))
                {
                    item.Title = ps3Title;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(ps4Title) &&
                    (string.IsNullOrWhiteSpace(item.Title) ||
                     string.Equals(item.Title, normalized, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(item.Title, Path.GetFileNameWithoutExtension(item.FileName), StringComparison.OrdinalIgnoreCase)))
                {
                    item.Title = ps4Title;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !string.Equals(item.Title, normalized, StringComparison.Ordinal))
                {
                    item.Title = normalized;
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

        private static string GetLocalMetadataCachePath(string filePath)
        {
            var cacheId = BinaryMetadataHelper.GetCacheId(filePath ?? string.Empty);
            return ApplicationPaths.GetCacheFile(cacheId + ".meta");
        }
    }
}
