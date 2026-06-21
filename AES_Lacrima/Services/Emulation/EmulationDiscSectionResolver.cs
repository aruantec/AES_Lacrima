using System;
using System.IO;
using AES_Lacrima.Services;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Maps emulation album/console labels to <see cref="DiscSection"/> values for ROM inspection.
/// </summary>
internal static class EmulationDiscSectionResolver
{
    public static DiscSection Resolve(string? albumName, string? filePath)
    {
        if (EmulationConsoleCatalog.TryGetDefinition(albumName, out var definition))
        {
            return definition.Key.ToUpperInvariant() switch
            {
                "PSX" => DiscSection.PSX,
                "PS2" => DiscSection.PS2,
                "DC" => DiscSection.Dreamcast,
                "GCN" => DiscSection.GameCube,
                "WII" => DiscSection.Wii,
                "WIIU" => DiscSection.WiiU,
                "3DS" => DiscSection.Nintendo3ds,
                "SWITCH" => DiscSection.Switch,
                "PS4" => DiscSection.PS4,
                "PSP" => DiscSection.PSP,
                _ when NintendoDiscMetadataHelper.IsNintendoDiscAlbum(albumName) =>
                    NintendoDiscMetadataHelper.ResolveDiscSection(albumName, filePath),
                _ => DiscSection.Auto
            };
        }

        if (NintendoDiscMetadataHelper.IsNintendoDiscAlbum(albumName))
            return NintendoDiscMetadataHelper.ResolveDiscSection(albumName, filePath);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var extension = Path.GetExtension(filePath);
            if (string.Equals(extension, ".pbp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".chd", StringComparison.OrdinalIgnoreCase))
            {
                return DiscSection.PSX;
            }
        }

        return DiscSection.Auto;
    }
}
