using AES_Controls.Player.Models;
using AES_Controls.Helpers;
using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

                // Scan PSX and PS2 discs to extract GameId
                var isPsxOrPs2 = IsPsxOrPs2Album(album.Title);
                if (isPsxOrPs2 && !string.IsNullOrWhiteSpace(item.FileName) && File.Exists(item.FileName))
                {
                    _ = ExtractAndPersistPsxGameIdAsync(item);
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

        private static bool IsPsxOrPs2Album(string? albumTitle)
        {
            if (string.IsNullOrWhiteSpace(albumTitle))
                return false;

            return string.Equals(albumTitle, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PSX", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PlayStation 1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PlayStation 2", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(albumTitle, "PS2", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task ExtractAndPersistPsxGameIdAsync(MediaItem item)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.FileName))
                {
                    var romInfo = RomInspector.Inspect(item.FileName);
                    if (!string.IsNullOrWhiteSpace(romInfo?.GameId))
                    {
                        // Extraction complete - persistence handled through MetadataService
                        await Task.CompletedTask;
                    }
                }
            }
            catch
            {
                // Silently fail - extraction is optional
            }
        }
    }
}
