using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Utility for ROM title normalization.
    /// Extracted into separate class to reduce EmulationViewModel bloat.
    /// </summary>
    public static class RomTitleNormalizationUtil
    {
        private static readonly Regex RomBracketTokenRegex = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex RomMediaLabelRegex = new(
            @"[\(\[\{]\s*((?:disc|disk|cd|dvd|gd|side)\s*(?:\d+|[ivx]+|[a-z]))\s*[\)\]\}]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RomMediaLabelPartsRegex = new(
            @"^(\w+)\s+(.+)$",
            RegexOptions.Compiled);
        private static readonly Regex RomWhitespaceRegex = new(
            @"\s+",
            RegexOptions.Compiled);

        /// <summary>Get normalized ROM title, stripping brackets and normalizing formatting</summary>
        public static string GetNormalizedRomTitle(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
                return string.Empty;

            var normalized = rawTitle.Replace('_', ' ').Replace('.', ' ').Trim();
            var preservedMediaLabels = RomMediaLabelRegex
                .Matches(normalized)
                .Cast<Match>()
                .Select(match => NormalizeRomMediaLabel(match.Groups[1].Value))
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            normalized = RomBracketTokenRegex.Replace(normalized, " ");
            normalized = normalized.Replace("!", " ");
            normalized = RomWhitespaceRegex.Replace(normalized, " ").Trim();

            if (preservedMediaLabels.Length > 0)
            {
                var suffix = string.Join(" ", preservedMediaLabels.Select(label => $"({label})"));
                normalized = string.IsNullOrWhiteSpace(normalized)
                    ? suffix
                    : $"{normalized} {suffix}";
            }

            return normalized;
        }

        /// <summary>Normalize a ROM media label (disc, cd, dvd, etc.) to standard format</summary>
        public static string NormalizeRomMediaLabel(string rawLabel)
        {
            if (string.IsNullOrWhiteSpace(rawLabel))
                return string.Empty;

            var compact = RomWhitespaceRegex.Replace(rawLabel, " ").Trim();
            var match = RomMediaLabelPartsRegex.Match(compact);
            if (!match.Success)
                return compact;

            var prefix = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "disc" => "Disc",
                "disk" => "Disk",
                "cd" => "CD",
                "dvd" => "DVD",
                "gd" => "GD",
                "side" => "Side",
                _ => match.Groups[1].Value
            };

            var value = match.Groups[2].Value;
            return $"{prefix} {value.ToUpperInvariant()}";
        }
    }
}
