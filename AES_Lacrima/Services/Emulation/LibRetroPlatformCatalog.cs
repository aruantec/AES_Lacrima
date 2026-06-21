using System;
using System.Collections.Generic;
using AES_Lacrima.Services;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Maps console identifiers to LibRetro thumbnail playlist folder names.
/// </summary>
internal static class LibRetroPlatformCatalog
{
    private static readonly Dictionary<string, string> ConsoleKeyToFolder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SNES"] = "Nintendo - Super Nintendo Entertainment System",
            ["NES"] = "Nintendo - Nintendo Entertainment System",
            ["N64"] = "Nintendo - Nintendo 64",
            ["GBA"] = "Nintendo - Game Boy Advance",
            ["GCN"] = "Nintendo - GameCube",
            ["WII"] = "Nintendo - Wii",
            ["WIIU"] = "Nintendo - Wii U",
            ["NDS"] = "Nintendo - Nintendo DS",
            ["3DS"] = "Nintendo - Nintendo 3DS",
            ["SWITCH"] = "Nintendo - Nintendo Switch",
            ["PSX"] = "Sony - PlayStation",
            ["PS2"] = "Sony - PlayStation 2",
            ["PSP"] = "Sony - PlayStation Portable",
            ["DC"] = "Sega - Dreamcast",
            ["GENESIS"] = "Sega - Mega Drive - Genesis",
            ["SATURN"] = "Sega - Saturn",
            ["XBOX"] = "Microsoft - Xbox",
            ["XBOX360"] = "Microsoft - Xbox 360",
            ["ARCADE"] = "MAME",
            ["FBN"] = "FBNeo - Arcade Games",
        };

    private static readonly Dictionary<string, string> HasheousPlatformToFolder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Nintendo Super Nintendo Entertainment System"] = "Nintendo - Super Nintendo Entertainment System",
            ["Super Nintendo Entertainment System"] = "Nintendo - Super Nintendo Entertainment System",
            ["Nintendo Entertainment System"] = "Nintendo - Nintendo Entertainment System",
            ["Nintendo 64"] = "Nintendo - Nintendo 64",
            ["Game Boy Advance"] = "Nintendo - Game Boy Advance",
            ["Nintendo GameCube"] = "Nintendo - GameCube",
            ["Nintendo Wii"] = "Nintendo - Wii",
            ["Nintendo Wii U"] = "Nintendo - Wii U",
            ["Nintendo DS"] = "Nintendo - Nintendo DS",
            ["Nintendo 3DS"] = "Nintendo - Nintendo 3DS",
            ["Nintendo Switch"] = "Nintendo - Nintendo Switch",
            ["Sony PlayStation"] = "Sony - PlayStation",
            ["Sony PlayStation 2"] = "Sony - PlayStation 2",
            ["Sony PlayStation Portable"] = "Sony - PlayStation Portable",
            ["Sega Dreamcast"] = "Sega - Dreamcast",
            ["Sega Mega Drive"] = "Sega - Mega Drive - Genesis",
            ["Sega Genesis"] = "Sega - Mega Drive - Genesis",
            ["Sega Saturn"] = "Sega - Saturn",
            ["Microsoft Xbox"] = "Microsoft - Xbox",
            ["Microsoft Xbox 360"] = "Microsoft - Xbox 360",
            ["Commodore Amiga"] = "Commodore - Amiga",
            ["Amiga"] = "Commodore - Amiga",
        };

    public static bool TryResolveFolder(string? albumName, string? hasheousPlatformName, out string folder)
    {
        folder = string.Empty;

        if (EmulationConsoleCatalog.TryGetDefinition(albumName, out var definition) &&
            ConsoleKeyToFolder.TryGetValue(definition.Key, out var fromAlbum))
        {
            folder = fromAlbum;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hasheousPlatformName))
        {
            if (HasheousPlatformToFolder.TryGetValue(hasheousPlatformName.Trim(), out var direct))
            {
                folder = direct;
                return true;
            }

            foreach (var pair in HasheousPlatformToFolder)
            {
                if (hasheousPlatformName.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    folder = pair.Value;
                    return true;
                }
            }
        }

        return false;
    }
}
