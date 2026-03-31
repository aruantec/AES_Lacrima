using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Lacrima.Services
{
    internal sealed record EmulationConsoleDefinition(
        string Key,
        string DisplayName,
        string[] SearchAliases,
        string[] FilePatterns,
        params string[] AdditionalLookupKeys);

    internal static class EmulationConsoleCatalog
    {
        private static readonly string[] ArchivePatterns =
        [
            "*.zip",
            "*.7z",
            "*.rar"
        ];

        private static readonly string[] AllFilePatterns =
        [
            "*"
        ];

        private static readonly EmulationConsoleDefinition[] Definitions =
        [
            new(
                "SNES",
                "Super Nintendo",
                ["Super Nintendo", "Super Nintendo Entertainment System", "SNES"],
                ["*.sfc", "*.smc", "*.fig", "*.swc"]),
            new(
                "NES",
                "Nintendo Entertainment System",
                ["Nintendo Entertainment System", "NES"],
                ["*.nes", "*.fds", "*.unf", "*.unif"]),
            new(
                "N64",
                "Nintendo 64",
                ["Nintendo 64", "N64"],
                ["*.n64", "*.z64", "*.v64"]),
            new(
                "DC",
                "Dreamcast",
                ["Sega Dreamcast", "Dreamcast"],
                ["*.cdi", "*.gdi", "*.chd", "*.cue", "*.iso"],
                "DREAMCAST"),
            new(
                "GCN",
                "Nintendo GameCube",
                ["Nintendo GameCube", "GameCube", "GCN", "GC"],
                ["*.iso", "*.gcm", "*.ciso", "*.gcz", "*.rvz", "*.wia", "*.dol", "*.elf", "*.tgc"],
                "GC"),
            new(
                "GBA",
                "Game Boy Advance",
                ["Game Boy Advance", "GBA"],
                ["*.gba"]),
            new(
                "NDS",
                "Nintendo DS",
                ["Nintendo DS", "NDS", "DS"],
                ["*.nds", "*.srl"]),
            new(
                "3DS",
                "Nintendo 3DS",
                ["Nintendo 3DS", "3DS"],
                ["*.3ds", "*.3dsx", "*.cci", "*.cxi", "*.cia"]),
            new(
                "WII",
                "Nintendo Wii",
                ["Nintendo Wii", "Wii"],
                ["*.iso", "*.wbfs", "*.wad"]),
            new(
                "WIIU",
                "Nintendo Wii U",
                ["Nintendo Wii U", "Wii U", "WiiU"],
                ["*.wud", "*.wux", "*.wua", "*.rpx"],
                "WII U"),
            new(
                "SWITCH",
                "Nintendo Switch",
                ["Nintendo Switch", "Switch"],
                ["*.xci", "*.nsp", "*.nsz", "*.nca"]),
            new(
                "PSX",
                "PlayStation",
                ["PlayStation"],
                ["*.cue", "*.bin", "*.img", "*.iso", "*.chd", "*.pbp", "*.m3u"],
                "PS1"),
            new(
                "PS2",
                "PlayStation 2",
                ["PlayStation 2"],
                ["*.iso", "*.bin", "*.img", "*.mdf", "*.nrg", "*.chd"]),
            new(
                "PS3",
                "PlayStation 3",
                ["PlayStation 3"],
                ["*.iso", "*.pkg", "*.ps3"]),
            new(
                "PS4",
                "PlayStation 4",
                ["PlayStation 4"],
                ["*.pkg"]),
            new(
                "ARCADE",
                "Arcade",
                ["Arcade", "Arcade Machine", "Arcade Machines", "MAME", "FinalBurn Neo", "FBNeo"],
                ["*.chd", "*.zip", "*.7z"],
                "MAME",
                "FBNEO",
                "FINALBURNNEO"),
            new(
                "XBOX360",
                "Xbox 360",
                ["Xbox 360"],
                ["*.iso", "*.xex"],
                "XBOX 360",
                "X360")
        ];

        private static readonly Dictionary<string, EmulationConsoleDefinition> DefinitionsByLookupKey = BuildDefinitionsByLookupKey();

        public static IReadOnlyDictionary<string, string[]> SearchAliases { get; } = BuildSearchAliases();

        public static IReadOnlyList<FilePickerFileType> BuildFilePickerFilters(string? consoleName)
        {
            var filters = new List<FilePickerFileType>();

            if (TryGetDefinition(consoleName, out var definition) && definition.FilePatterns.Length > 0)
            {
                filters.Add(new FilePickerFileType($"{definition.DisplayName} ROMs")
                {
                    Patterns = definition.FilePatterns
                });
            }

            filters.Add(new FilePickerFileType("ROM Archives")
            {
                Patterns = ArchivePatterns
            });

            filters.Add(new FilePickerFileType("All Files")
            {
                Patterns = AllFilePatterns
            });

            return filters;
        }

        public static IReadOnlyList<string> GetScanPatterns(string? consoleName)
        {
            var patterns = new List<string>();

            if (TryGetDefinition(consoleName, out var definition) && definition.FilePatterns.Length > 0)
                patterns.AddRange(definition.FilePatterns);

            patterns.AddRange(ArchivePatterns);
            return [.. patterns.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public static IEnumerable<string> GetSearchAliases(string? consoleName)
        {
            if (!TryGetDefinition(consoleName, out var definition))
                return [];

            return definition.SearchAliases;
        }

        public static IReadOnlyList<string> GetSearchQueryTerms(string? consoleName)
        {
            if (!TryGetDefinition(consoleName, out var definition))
                return [];

            return new[]
                {
                    definition.Key,
                    definition.DisplayName
                }
                .Concat(definition.AdditionalLookupKeys)
                .Concat(definition.SearchAliases)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string GetDisplayName(string? consoleName)
        {
            if (TryGetDefinition(consoleName, out var definition))
                return definition.DisplayName;

            return string.IsNullOrWhiteSpace(consoleName)
                ? string.Empty
                : consoleName;
        }

        public static bool IsArcadeConsole(string? consoleName)
        {
            if (!TryGetDefinition(consoleName, out var definition))
                return false;

            return string.Equals(definition.Key, "ARCADE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDefinition(string? consoleName, out EmulationConsoleDefinition definition)
        {
            definition = default!;
            if (string.IsNullOrWhiteSpace(consoleName))
                return false;

            if (!DefinitionsByLookupKey.TryGetValue(NormalizeLookupKey(consoleName), out var resolvedDefinition))
                return false;

            definition = resolvedDefinition;
            return true;
        }

        private static Dictionary<string, EmulationConsoleDefinition> BuildDefinitionsByLookupKey()
        {
            var map = new Dictionary<string, EmulationConsoleDefinition>(StringComparer.OrdinalIgnoreCase);

            foreach (var definition in Definitions)
            {
                RegisterLookupKey(map, definition.Key, definition);
                RegisterLookupKey(map, definition.DisplayName, definition);

                foreach (var alias in definition.SearchAliases)
                    RegisterLookupKey(map, alias, definition);

                foreach (var lookupKey in definition.AdditionalLookupKeys)
                    RegisterLookupKey(map, lookupKey, definition);
            }

            return map;
        }

        private static IReadOnlyDictionary<string, string[]> BuildSearchAliases()
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var definition in Definitions)
            {
                map[definition.Key] = definition.SearchAliases;

                foreach (var lookupKey in definition.AdditionalLookupKeys)
                    map[lookupKey] = definition.SearchAliases;
            }

            return map;
        }

        private static void RegisterLookupKey(
            IDictionary<string, EmulationConsoleDefinition> map,
            string lookupKey,
            EmulationConsoleDefinition definition)
        {
            var normalized = NormalizeLookupKey(lookupKey);
            if (!string.IsNullOrWhiteSpace(normalized))
                map[normalized] = definition;
        }

        private static string NormalizeLookupKey(string value)
            => new(value.Where(char.IsLetterOrDigit).ToArray());
    }
}
