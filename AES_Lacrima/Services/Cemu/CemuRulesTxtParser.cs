using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Cemu;

public sealed record CemuGraphicPackPresetDefinition(
    string Name,
    string Category,
    bool IsDefault);

public sealed record CemuGraphicPackDefinition(
    string RulesFilePath,
    string RelativeRulesPath,
    string Name,
    string? UiPath,
    string? Description,
    IReadOnlyList<string> TitleIds,
    bool IsUniversal,
    bool EnabledByDefault,
    IReadOnlyList<CemuGraphicPackPresetDefinition> Presets);

internal static class CemuRulesTxtParser
{
    private static readonly Regex TitleIdTokenRegex = new(@"[0-9A-Fa-f]{16}|\*", RegexOptions.Compiled);

    public static bool TryParseGraphicPack(string rulesFilePath, string relativeRulesPath, out CemuGraphicPackDefinition definition)
    {
        definition = null!;

        try
        {
            if (!File.Exists(rulesFilePath))
                return false;

            var (definitionValues, presets) = ReadRulesFile(rulesFilePath);
            if (definitionValues.Count == 0)
                return false;

            var name = GetValue(definitionValues, "name") ?? Path.GetFileName(Path.GetDirectoryName(rulesFilePath) ?? rulesFilePath);
            var uiPath = Unquote(GetValue(definitionValues, "path"));
            var description = Unquote(GetValue(definitionValues, "description"));
            var titleIds = ParseTitleIds(GetValue(definitionValues, "titleIds"));
            var isUniversal = titleIds.Contains("*", StringComparer.Ordinal);
            var enabledByDefault = ParseBool(GetValue(definitionValues, "default"));

            definition = new CemuGraphicPackDefinition(
                rulesFilePath,
                relativeRulesPath,
                name.Trim(),
                uiPath,
                description,
                titleIds,
                isUniversal,
                enabledByDefault,
                presets);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (Dictionary<string, string> Definition, List<CemuGraphicPackPresetDefinition> Presets) ReadRulesFile(string rulesFilePath)
    {
        var definition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var presets = new List<CemuGraphicPackPresetDefinition>();
        var lines = File.ReadAllLines(rulesFilePath);

        string? currentSection = null;
        var currentSectionValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushPresetSection()
        {
            if (!string.Equals(currentSection, "Preset", StringComparison.OrdinalIgnoreCase))
                return;

            var presetName = Unquote(GetValue(currentSectionValues, "name"));
            if (string.IsNullOrWhiteSpace(presetName))
                return;

            var category = Unquote(GetValue(currentSectionValues, "category")) ?? string.Empty;
            var isDefault = ParseBool(GetValue(currentSectionValues, "default")) ||
                            presetName.Contains("(Default)", StringComparison.OrdinalIgnoreCase);

            presets.Add(new CemuGraphicPackPresetDefinition(presetName.Trim(), category.Trim(), isDefault));
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                FlushPresetSection();

                currentSection = line[1..^1].Trim();
                currentSectionValues.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentSection))
                continue;

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
                continue;

            if (string.Equals(currentSection, "Definition", StringComparison.OrdinalIgnoreCase))
                definition[key] = value;
            else if (string.Equals(currentSection, "Preset", StringComparison.OrdinalIgnoreCase))
                currentSectionValues[key] = value;
        }

        FlushPresetSection();
        return (definition, presets);
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value : null;

    private static IReadOnlyList<string> ParseTitleIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        var matches = TitleIdTokenRegex.Matches(raw);
        if (matches.Count == 0)
            return Array.Empty<string>();

        return matches
            .Select(static match => match.Value.Trim())
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Trim() switch
        {
            "1" or "true" or "True" or "TRUE" => true,
            _ => false
        };
    }

    private static string? Unquote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
             (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
            trimmed = trimmed[1..^1];

        return trimmed.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
    }
}
