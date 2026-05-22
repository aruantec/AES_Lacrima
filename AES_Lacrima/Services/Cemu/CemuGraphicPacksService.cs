using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Cemu;

public sealed record CemuGraphicPackPresetGroupModel(
    string Category,
    string CategoryLabel,
    IReadOnlyList<string> PresetNames,
    string? SelectedPresetName);

public sealed record CemuGraphicPackEntryModel(
    string EntryKey,
    string RelativeRulesPath,
    string Name,
    string? UiPath,
    string? Description,
    bool IsEnabled,
    IReadOnlyList<CemuGraphicPackPresetGroupModel> PresetGroups);

public sealed record CemuGraphicPackToggle(
    string EntryKey,
    string RelativeRulesPath,
    bool IsEnabled,
    IReadOnlyDictionary<string, string> ActivePresetsByCategory);

public static class CemuGraphicPacksService
{
    private static readonly ILog Log = LogHelper.For(typeof(CemuGraphicPacksService));

    public static bool TryGetGraphicPacksForTitleId(
        string? emulatorDirectory,
        string? launcherPath,
        string titleId,
        out IReadOnlyList<CemuGraphicPackEntryModel> packs,
        out string? errorMessage)
    {
        packs = Array.Empty<CemuGraphicPackEntryModel>();
        errorMessage = null;

        if (!CemuPathsService.TryResolveUserDataDirectory(emulatorDirectory, launcherPath, out var userDataDirectory))
        {
            errorMessage = "Cemu user data directory was not found.";
            return false;
        }

        if (!CemuPathsService.TryResolveSettingsPath(emulatorDirectory, launcherPath, out var settingsPath))
        {
            errorMessage = "Cemu settings.xml was not found.";
            return false;
        }

        var normalizedTitleId = CemuTitleIdHelper.NormalizeDisplayTitleId(titleId);
        if (string.IsNullOrWhiteSpace(normalizedTitleId))
        {
            errorMessage = "Title ID is invalid.";
            return false;
        }

        try
        {
            var configLookup = LoadConfigLookup(settingsPath);
            var matches = new List<CemuGraphicPackEntryModel>();

            foreach (var rulesPath in EnumerateRulesFiles(userDataDirectory))
            {
                var relativePath = CemuPathsService.MakeRelativeRulesPath(userDataDirectory, rulesPath);
                if (!CemuRulesTxtParser.TryParseGraphicPack(rulesPath, relativePath, out var definition))
                    continue;

                if (!definition.IsUniversal && !CemuTitleIdHelper.MatchesTitleId(definition.TitleIds, normalizedTitleId))
                    continue;

                var entryKey = BuildEntryKey(relativePath);
                configLookup.TryGetValue(relativePath, out var config);
                var isEnabled = config?.IsEnabled ?? definition.EnabledByDefault;
                var presetGroups = BuildPresetGroups(definition.Presets, config?.ActivePresetsByCategory);

                matches.Add(new CemuGraphicPackEntryModel(
                    entryKey,
                    relativePath,
                    definition.Name,
                    definition.UiPath,
                    definition.Description,
                    isEnabled,
                    presetGroups));
            }

            packs = matches
                .OrderBy(static pack => pack.UiPath ?? pack.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to enumerate Cemu graphic packs for title ID '{normalizedTitleId}'.", ex);
            errorMessage = $"Failed to read graphic packs: {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyDictionary<string, bool> BuildEnabledStateMap(
        IReadOnlyList<CemuGraphicPackEntryModel> entries,
        string? settingsPath)
    {
        var lookup = LoadConfigLookup(settingsPath);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            result[entry.EntryKey] = lookup.TryGetValue(entry.RelativeRulesPath, out var config) && config.IsEnabled;
        }

        return result;
    }

    public static void SaveEnabledStates(
        string? emulatorDirectory,
        string? launcherPath,
        IReadOnlyList<CemuGraphicPackToggle> toggles)
    {
        if (!CemuPathsService.TryResolveSettingsPath(emulatorDirectory, launcherPath, out var settingsPath))
            return;

        var document = LoadOrCreateSettingsDocument(settingsPath);
        var graphicPackElement = GetOrCreateGraphicPackElement(document);
        if (graphicPackElement == null)
            return;

        foreach (var toggle in toggles)
        {
            UpsertGraphicPackEntry(
                graphicPackElement,
                toggle.RelativeRulesPath,
                toggle.IsEnabled,
                toggle.ActivePresetsByCategory);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? string.Empty);
        document.Save(settingsPath);
        Log.Info($"Saved {toggles.Count} Cemu graphic pack toggle(s) to '{settingsPath}'.");
    }

    public static string BuildEntryKey(string relativeRulesPath) => relativeRulesPath.Replace('\\', '/');

    internal static IReadOnlyList<CemuGraphicPackPresetGroupModel> BuildPresetGroups(
        IReadOnlyList<CemuGraphicPackPresetDefinition> presets,
        IReadOnlyDictionary<string, string>? activePresetsByCategory)
    {
        if (presets.Count == 0)
            return Array.Empty<CemuGraphicPackPresetGroupModel>();

        return presets
            .GroupBy(static preset => preset.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var category = group.Key;
                var presetNames = group.Select(static preset => preset.Name).ToArray();
                var selected = activePresetsByCategory != null &&
                               activePresetsByCategory.TryGetValue(category, out var configured) &&
                               presetNames.Contains(configured, StringComparer.OrdinalIgnoreCase)
                    ? configured
                    : group.FirstOrDefault(static preset => preset.IsDefault)?.Name ?? presetNames[0];

                return new CemuGraphicPackPresetGroupModel(
                    category,
                    string.IsNullOrWhiteSpace(category) ? "Active preset" : category,
                    presetNames,
                    selected);
            })
            .ToArray();
    }

    private static IEnumerable<string> EnumerateRulesFiles(string userDataDirectory)
    {
        var graphicPacksRoot = CemuPathsService.GetGraphicPacksRoot(userDataDirectory);
        if (!Directory.Exists(graphicPacksRoot))
            yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(graphicPacksRoot, "rules.txt", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
            yield return file;
    }

    private sealed record GraphicPackConfigState(bool IsEnabled, Dictionary<string, string> ActivePresetsByCategory);

    private static Dictionary<string, GraphicPackConfigState> LoadConfigLookup(string settingsPath)
    {
        var lookup = new Dictionary<string, GraphicPackConfigState>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(settingsPath))
            return lookup;

        try
        {
            var document = XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace);
            var graphicPackElement = document.Descendants("GraphicPack").FirstOrDefault();
            if (graphicPackElement == null)
                return lookup;

            foreach (var entry in graphicPackElement.Elements("Entry"))
            {
                var filename = ((string?)entry.Attribute("filename"))?.Trim();
                if (string.IsNullOrWhiteSpace(filename))
                {
                    filename = entry.Element("filename")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(filename))
                        continue;
                }

                var normalized = filename.Replace('\\', '/');
                var disabledAttribute = (string?)entry.Attribute("disabled");
                var disabled = string.Equals(disabledAttribute, "true", StringComparison.OrdinalIgnoreCase);

                var activePresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var presetElement in entry.Elements("Preset"))
                {
                    var category = presetElement.Attribute("category")?.Value?.Trim() ?? string.Empty;
                    var presetName = presetElement.Attribute("preset")?.Value?.Trim()
                                     ?? presetElement.Element("preset")?.Value?.Trim()
                                     ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(presetName))
                        continue;

                    activePresets[category] = presetName;
                }

                lookup[normalized] = new GraphicPackConfigState(!disabled, activePresets);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read Cemu graphic pack settings from '{settingsPath}'.", ex);
        }

        return lookup;
    }

    private static XDocument LoadOrCreateSettingsDocument(string settingsPath)
    {
        if (File.Exists(settingsPath))
        {
            try
            {
                return XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace);
            }
            catch
            {
            }
        }

        return new XDocument(
            new XElement("cemu",
                new XElement("content",
                    new XElement("GraphicPack"))));
    }

    private static XElement? GetOrCreateGraphicPackElement(XDocument document)
    {
        var contentElement = document.Descendants("content").FirstOrDefault() ?? document.Root;
        if (contentElement == null)
            return null;

        var graphicPackElement = contentElement.Element("GraphicPack");
        if (graphicPackElement != null)
            return graphicPackElement;

        graphicPackElement = new XElement("GraphicPack");
        contentElement.Add(graphicPackElement);
        return graphicPackElement;
    }

    private static void UpsertGraphicPackEntry(
        XElement graphicPackElement,
        string relativeRulesPath,
        bool isEnabled,
        IReadOnlyDictionary<string, string> activePresetsByCategory)
    {
        var normalized = relativeRulesPath.Replace('\\', '/');
        var existing = graphicPackElement.Elements("Entry")
            .FirstOrDefault(entry => string.Equals((string?)entry.Attribute("filename"), normalized, StringComparison.OrdinalIgnoreCase));
        existing?.Remove();

        if (!isEnabled)
        {
            graphicPackElement.Add(new XElement("Entry",
                new XAttribute("filename", normalized),
                new XAttribute("disabled", "true")));
            return;
        }

        var entry = new XElement("Entry",
            new XAttribute("filename", normalized),
            new XAttribute("disabled", "false"));

        foreach (var presetEntry in activePresetsByCategory)
        {
            if (string.IsNullOrWhiteSpace(presetEntry.Value))
                continue;

            entry.Add(new XElement("Preset",
                new XAttribute("category", presetEntry.Key),
                new XAttribute("preset", presetEntry.Value)));
        }

        graphicPackElement.Add(entry);
    }
}
