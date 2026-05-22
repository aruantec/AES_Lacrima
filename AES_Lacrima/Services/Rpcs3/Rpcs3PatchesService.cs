using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AES_Core.Logging;
using log4net;
using YamlDotNet.RepresentationModel;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3PatchDefinition(
    string PpuHash,
    string Name,
    string GameTitle,
    string Serial,
    string AppVersion,
    string? Author,
    string? Notes,
    string? Group,
    string? PatchVersion);

public static class Rpcs3PatchesService
{
    private static readonly ILog Log = LogHelper.For(typeof(Rpcs3PatchesService));

    public const string PatchEngineVersion = "1.2";
    public const string OfficialPatchFileName = "patch.yml";
    public const string ArtemisCheatsPatchFileName = "artemis_cheats.yml";
    public const string ArtemisGameTitleMarker = "(Artemis)";
    /// <summary>Matches RPCS3 <c>patch_key::enabled</c> in Utilities/bin_patch.h.</summary>
    public const string EnabledKey = "Enabled";

    private static readonly Regex TitleIdTokenRegex = new(
        @"\b(BLUS|BLES|BLJM|BLJS|NPUB|NPEB|NPJB|NPUJ|NPUA|NPUX|BCUS|BCES|MRTC)\d{5}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private const string VersionKey = "Version";
    private const string GamesKey = "Games";
    private const string AuthorKey = "Author";
    private const string NotesKey = "Notes";
    private const string GroupKey = "Group";
    private const string PatchVersionKey = "Patch Version";
    private const string AnchorsKey = "Anchors";

    public static string GetPatchesDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "patches");
    }

    public static string GetPatchYmlPath(string? emulatorDirectory) =>
        GetPatchYmlPath(emulatorDirectory, Rpcs3PatchCatalog.Official);

    public static string GetPatchYmlPath(string? emulatorDirectory, Rpcs3PatchCatalog catalog) =>
        Path.Combine(GetPatchesDirectory(emulatorDirectory), GetPatchFileName(catalog));

    public static string GetPatchFileName(Rpcs3PatchCatalog catalog) =>
        catalog == Rpcs3PatchCatalog.ArtemisCheats ? ArtemisCheatsPatchFileName : OfficialPatchFileName;

    public static bool IsArtemisCheatPatch(Rpcs3PatchDefinition definition) =>
        definition.GameTitle.Contains(ArtemisGameTitleMarker, StringComparison.OrdinalIgnoreCase);

    public static string GetPatchConfigPath(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "config", "patch_config.yml");
    }

    public static bool PatchFileExists(string? emulatorDirectory) =>
        PatchFileExists(emulatorDirectory, Rpcs3PatchCatalog.Official);

    public static bool PatchFileExists(string? emulatorDirectory, Rpcs3PatchCatalog catalog) =>
        File.Exists(GetPatchYmlPath(emulatorDirectory, catalog));

    /// <summary>
    /// Resolves the RPCS3 root directory that contains <c>patches/patch.yml</c>.
    /// Prefers the AES-managed folder when patches exist there, even if the launcher lives elsewhere.
    /// </summary>
    public static string ResolveEmulatorDirectory(string? preferredDirectory, string? launcherPath)
    {
        var candidates = new List<string>();

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var trimmed = path.Trim();
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                candidates.Add(trimmed);
        }

        AddCandidate(preferredDirectory);
        AddCandidate(Rpcs3CustomConfigService.GetDefaultEmulatorDirectory());

        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            try
            {
                var current = Path.GetDirectoryName(Path.GetFullPath(launcherPath.Trim()));
                while (!string.IsNullOrWhiteSpace(current))
                {
                    AddCandidate(current);

                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrWhiteSpace(parent) ||
                        string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                        break;

                    current = parent;
                }
            }
            catch
            {
            }
        }

        foreach (var candidate in candidates)
        {
            if (PatchFileExists(candidate))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(preferredDirectory))
            return preferredDirectory.Trim();

        return Rpcs3CustomConfigService.ResolveEmulatorDirectory(launcherPath);
    }

    public static string? TryReadLocalPatchEngineVersion(string? emulatorDirectory)
    {
        var path = GetPatchYmlPath(emulatorDirectory);
        if (!File.Exists(path))
            return null;

        try
        {
            if (!TryLoadPatchYamlRoot(path, out var root, out _))
                return null;

            if (!TryGetChild(root, VersionKey, out var versionNode))
                return null;

            return versionNode?.ToString()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<Rpcs3PatchDefinition> GetPatchesForTitleId(
        string? emulatorDirectory,
        string titleId,
        string? appVersion = null,
        Rpcs3PatchCatalog catalog = Rpcs3PatchCatalog.Official)
    {
        _ = TryGetPatchesForTitleId(emulatorDirectory, titleId, appVersion, catalog, out var patches, out _);
        return patches;
    }

    public static bool TryGetPatchesForTitleId(
        string? emulatorDirectory,
        string titleId,
        string? appVersion,
        out IReadOnlyList<Rpcs3PatchDefinition> patches,
        out string? errorMessage) =>
        TryGetPatchesForTitleId(emulatorDirectory, titleId, appVersion, Rpcs3PatchCatalog.Official, out patches, out errorMessage);

    public static bool TryGetPatchesForTitleId(
        string? emulatorDirectory,
        string titleId,
        string? appVersion,
        Rpcs3PatchCatalog catalog,
        out IReadOnlyList<Rpcs3PatchDefinition> patches,
        out string? errorMessage)
    {
        patches = Array.Empty<Rpcs3PatchDefinition>();
        errorMessage = null;

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var patchPath = GetPatchYmlPath(emulatorDirectory, catalog);
        var patchFileLabel = GetPatchFileName(catalog);
        if (!File.Exists(patchPath))
        {
            errorMessage = catalog == Rpcs3PatchCatalog.ArtemisCheats
                ? $"{patchFileLabel} was not found at '{patchPath}'. Download Artemis cheats to get started."
                : $"{patchFileLabel} was not found at '{patchPath}'.";
            return false;
        }

        var normalizedAppVersion = NormalizeAppVersion(appVersion);

        try
        {
            if (!TryLoadPatchYamlRoot(patchPath, out var root, out errorMessage))
                return false;

            var mappingAnchors = LoadAnchorMappings(root);
            var documentAnchors = BuildDocumentAnchorIndex(root);
            var matches = new List<Rpcs3PatchDefinition>();

            foreach (var hashEntry in root.Children)
            {
                var hash = NormalizePpuHashKey(hashEntry.Key.ToString());
                if (!hash.StartsWith("PPU-", StringComparison.OrdinalIgnoreCase) ||
                    hashEntry.Value is not YamlMappingNode patchContainer)
                    continue;

                foreach (var patchEntry in patchContainer.Children)
                {
                    if (patchEntry.Value is not YamlMappingNode patchNode)
                        continue;

                    var patchName = patchEntry.Key.ToString().Trim();
                    if (!TryGetChild(patchNode, GamesKey, out var gamesNode))
                        continue;

                    var author = ReadScalar(patchNode, AuthorKey, mappingAnchors, documentAnchors);
                    var notes = ReadScalar(patchNode, NotesKey, mappingAnchors, documentAnchors);
                    var group = ReadScalar(patchNode, GroupKey, mappingAnchors, documentAnchors);
                    var patchVersion = ReadScalar(patchNode, PatchVersionKey, mappingAnchors, documentAnchors);

                    foreach (var target in EnumerateGameTargets(gamesNode, mappingAnchors, documentAnchors))
                    {
                        if (!string.Equals(target.Serial, normalizedTitleId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!IsAppVersionApplicable(target.AppVersion, normalizedAppVersion))
                            continue;

                        var definition = new Rpcs3PatchDefinition(
                            hash,
                            patchName,
                            target.GameTitle,
                            target.Serial,
                            target.AppVersion,
                            author,
                            notes,
                            group,
                            patchVersion);

                        if (!MatchesCatalog(catalog, definition))
                            continue;

                        matches.Add(definition);
                    }
                }
            }

            patches = matches
                .OrderBy(static patch => patch.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static patch => patch.AppVersion, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to parse RPCS3 patch file '{patchPath}' for title ID '{normalizedTitleId}'.", ex);
            errorMessage ??= $"Failed to read {patchFileLabel}: {ex.Message}";
            return false;
        }
    }

    private static bool MatchesCatalog(Rpcs3PatchCatalog catalog, Rpcs3PatchDefinition definition) =>
        catalog switch
        {
            Rpcs3PatchCatalog.ArtemisCheats => IsArtemisCheatPatch(definition),
            Rpcs3PatchCatalog.Official => !IsArtemisCheatPatch(definition),
            _ => true,
        };

    public static IReadOnlyList<string> ExtractTitleIdsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return TitleIdTokenRegex.Matches(text)
            .Select(static match => match.Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyDictionary<string, bool> BuildEnabledStateMap(
        IReadOnlyList<Rpcs3PatchDefinition> definitions,
        string? configPath)
    {
        var lookup = LoadEnabledLookup(configPath);
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            var key = BuildEntryKey(definition);
            result[key] = lookup.TryGetValue(key, out var enabled) && enabled;
        }

        return result;
    }

    public static void SaveEnabledStates(string? emulatorDirectory, IReadOnlyList<Rpcs3PatchToggle> toggles)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return;

        var configPath = GetPatchConfigPath(emulatorDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? emulatorDirectory);

        var root = LoadRootMapping(configPath);
        var enabledLookup = toggles
            .GroupBy(static toggle => toggle.EntryKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().IsEnabled, StringComparer.OrdinalIgnoreCase);

        foreach (var toggle in toggles)
        {
            ApplyToggle(root, toggle, enabledLookup);
        }

        SaveRootMapping(root, configPath);
    }

    public static string BuildEntryKey(Rpcs3PatchDefinition definition) =>
        string.Join('\u001F', definition.PpuHash, definition.Name, definition.GameTitle, definition.Serial, definition.AppVersion);

    private static void ApplyToggle(YamlMappingNode root, Rpcs3PatchToggle toggle, IReadOnlyDictionary<string, bool> enabledLookup)
    {
        if (!enabledLookup.TryGetValue(toggle.EntryKey, out var isEnabled) || !isEnabled)
        {
            RemoveToggle(root, toggle);
            return;
        }

        var hashNode = GetOrCreateMapping(root, toggle.PpuHash);
        var descriptionNode = GetOrCreateMapping(hashNode, toggle.Name);
        var titleNode = GetOrCreateMapping(descriptionNode, toggle.GameTitle);
        var serialNode = GetOrCreateMapping(titleNode, toggle.Serial);
        var versionNode = GetOrCreateMapping(serialNode, toggle.AppVersion);
        versionNode.Children[new YamlScalarNode(EnabledKey)] = new YamlScalarNode("true");
    }

    private static void RemoveChild(YamlMappingNode parent, string key)
    {
        YamlNode? existingKey = null;
        foreach (var entry in parent.Children)
        {
            if (!string.Equals(entry.Key.ToString(), key, StringComparison.Ordinal))
                continue;

            existingKey = entry.Key;
            break;
        }

        if (existingKey != null)
            parent.Children.Remove(existingKey);
    }

    private static void RemoveToggle(YamlMappingNode root, Rpcs3PatchToggle toggle)
    {
        if (!TryGetMapping(root, toggle.PpuHash, out var hashNode))
            return;

        if (!TryGetMapping(hashNode, toggle.Name, out var descriptionNode))
            return;

        if (!TryGetMapping(descriptionNode, toggle.GameTitle, out var titleNode))
            return;

        if (!TryGetMapping(titleNode, toggle.Serial, out var serialNode))
            return;

        if (!TryGetMapping(serialNode, toggle.AppVersion, out var versionNode))
            return;

        RemoveChild(versionNode, EnabledKey);

        if (versionNode.Children.Count == 0)
            RemoveChild(serialNode, toggle.AppVersion);

        if (serialNode.Children.Count == 0)
            RemoveChild(titleNode, toggle.Serial);

        if (titleNode.Children.Count == 0)
            RemoveChild(descriptionNode, toggle.Name);

        if (descriptionNode.Children.Count == 0)
            RemoveChild(root, toggle.PpuHash);
    }

    private static Dictionary<string, bool> LoadEnabledLookup(string? configPath)
    {
        var lookup = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return lookup;

        try
        {
            using var reader = new StreamReader(configPath);
            var yaml = new YamlStream();
            yaml.Load(reader);

            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                return lookup;

            foreach (var hashEntry in root.Children)
            {
                var hash = hashEntry.Key.ToString().Trim();
                if (hashEntry.Value is not YamlMappingNode descriptionContainer)
                    continue;

                foreach (var descriptionEntry in descriptionContainer.Children)
                {
                    var description = descriptionEntry.Key.ToString().Trim();
                    if (descriptionEntry.Value is not YamlMappingNode titleContainer)
                        continue;

                    foreach (var titleEntry in titleContainer.Children)
                    {
                        var title = titleEntry.Key.ToString().Trim();
                        if (titleEntry.Value is not YamlMappingNode serialContainer)
                            continue;

                        foreach (var serialEntry in serialContainer.Children)
                        {
                            var serial = serialEntry.Key.ToString().Trim();
                            if (serialEntry.Value is not YamlMappingNode versionContainer)
                                continue;

                            foreach (var versionEntry in versionContainer.Children)
                            {
                                var appVersion = versionEntry.Key.ToString().Trim();
                                var enabled = ReadEnabledValue(versionEntry.Value);
                                if (!enabled)
                                    continue;

                                var key = string.Join('\u001F', hash, description, title, serial, appVersion);
                                lookup[key] = true;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return lookup;
    }

    private static bool ReadEnabledValue(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            if (TryGetChild(mapping, EnabledKey, out var enabledNode) ||
                TryGetChild(mapping, "enabled", out enabledNode))
                return ParseBool(enabledNode);

            return false;
        }

        return ParseBool(node);
    }

    private static bool ParseBool(YamlNode? node)
    {
        if (node is not YamlScalarNode scalar)
            return false;

        return bool.TryParse(scalar.Value, out var enabled) && enabled;
    }

    private static Dictionary<string, YamlMappingNode> LoadAnchorMappings(YamlMappingNode root)
    {
        var anchors = new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);

        foreach (var sectionEntry in root.Children)
        {
            if (!string.Equals(sectionEntry.Key.ToString(), AnchorsKey, StringComparison.Ordinal))
                continue;

            if (ResolveNode(sectionEntry.Value, anchors) is not YamlMappingNode anchorsSection)
                continue;

            foreach (var entry in anchorsSection.Children)
            {
                var anchorName = entry.Key.ToString().Trim();
                if (ResolveNode(entry.Value, anchors) is YamlMappingNode mapping)
                    anchors[anchorName] = mapping;
            }
        }

        return anchors;
    }

    /// <summary>
    /// Legacy helper kept for tests only. Production loading uses <see cref="Rpcs3PatchYamlLoader"/>
    /// which merges duplicate keys without rewriting the file (rewriting breaks interleaved blocks).
    /// </summary>
    internal static string NormalizePatchYamlContentForLoading(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var result = new List<string>(lines.Length);
        var seenRootKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (TryGetRootLevelKey(line, out var rootKey))
            {
                if (!seenRootKeys.Add(rootKey))
                    continue;

                result.Add(line);
                continue;
            }

            result.Add(line);
        }

        return string.Join('\n', result);
    }

    internal static bool TryGetRootLevelKey(string line, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
            return false;

        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0)
            return false;

        key = line[..colonIndex].Trim();
        return key.Length > 0;
    }

    private static bool TryLoadPatchYamlRoot(string patchPath, out YamlMappingNode root, out string? errorMessage) =>
        Rpcs3PatchYamlLoader.TryLoadRoot(patchPath, out root, out errorMessage);

    private static string NormalizePpuHashKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        var trimmed = rawKey.Trim();
        var colonIndex = trimmed.IndexOf(':');
        return colonIndex >= 0 ? trimmed[..colonIndex].Trim() : trimmed;
    }

    private static string? NormalizeAppVersion(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion))
            return null;

        return appVersion.Trim();
    }

    private static bool IsAppVersionApplicable(string patchAppVersion, string? detectedAppVersion)
    {
        if (string.IsNullOrWhiteSpace(detectedAppVersion))
            return true;

        if (string.Equals(patchAppVersion, "All", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(patchAppVersion, detectedAppVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, YamlNode> BuildDocumentAnchorIndex(YamlNode root)
    {
        var anchors = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        foreach (var node in root.AllNodes)
        {
            if (node.Anchor.IsEmpty)
                continue;

            var anchorName = node.Anchor.ToString();
            if (!string.IsNullOrWhiteSpace(anchorName))
                anchors[anchorName] = node;
        }

        return anchors;
    }

    private static IEnumerable<(string GameTitle, string Serial, string AppVersion)> EnumerateGameTargets(
        YamlNode gamesNode,
        IReadOnlyDictionary<string, YamlMappingNode> mappingAnchors,
        IReadOnlyDictionary<string, YamlNode> documentAnchors)
    {
        var resolved = ResolveNode(gamesNode, mappingAnchors, documentAnchors);
        if (resolved is not YamlMappingNode gamesMap)
            yield break;

        foreach (var gameEntry in gamesMap.Children)
        {
            var gameTitle = gameEntry.Key.ToString().Trim();
            if (gameEntry.Value is not YamlMappingNode serialsMap)
                continue;

            foreach (var serialEntry in serialsMap.Children)
            {
                var serial = serialEntry.Key.ToString().Trim().ToUpperInvariant();
                foreach (var appVersion in ReadAppVersions(serialEntry.Value))
                    yield return (gameTitle, serial, appVersion);
            }
        }
    }

    private static IEnumerable<string> ReadAppVersions(YamlNode versionsNode)
    {
        var resolved = ResolveNode(versionsNode);
        switch (resolved)
        {
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    var version = child.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(version))
                        yield return version;
                }

                break;
            case YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value):
                yield return scalar.Value.Trim();
                break;
        }
    }

    private static YamlNode ResolveNode(
        YamlNode node,
        IReadOnlyDictionary<string, YamlMappingNode>? mappingAnchors = null,
        IReadOnlyDictionary<string, YamlNode>? documentAnchors = null)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            if (node.NodeType == YamlNodeType.Alias && !node.Anchor.IsEmpty)
            {
                var anchorName = node.Anchor.ToString();
                if (!string.IsNullOrWhiteSpace(anchorName))
                {
                    if (documentAnchors != null &&
                        documentAnchors.TryGetValue(anchorName, out var documentTarget))
                    {
                        node = documentTarget;
                        continue;
                    }

                    if (mappingAnchors != null &&
                        mappingAnchors.TryGetValue(anchorName, out var mappingTarget))
                    {
                        node = mappingTarget;
                        continue;
                    }
                }
            }

            if (mappingAnchors != null &&
                node is YamlScalarNode scalar &&
                scalar.Value is { Length: > 0 } value &&
                value[0] == '*')
            {
                var anchorName = value[1..].Trim();
                if (mappingAnchors.TryGetValue(anchorName, out var anchorMapping))
                {
                    node = anchorMapping;
                    continue;
                }

                if (documentAnchors != null &&
                    documentAnchors.TryGetValue(anchorName, out var documentTarget))
                {
                    node = documentTarget;
                    continue;
                }
            }

            return node;
        }

        return node;
    }

    private static string? ReadScalar(
        YamlMappingNode mapping,
        string key,
        IReadOnlyDictionary<string, YamlMappingNode>? mappingAnchors = null,
        IReadOnlyDictionary<string, YamlNode>? documentAnchors = null)
    {
        if (!TryGetChild(mapping, key, out var valueNode))
            return null;

        var resolved = ResolveNode(valueNode, mappingAnchors, documentAnchors);
        if (resolved is YamlScalarNode scalar)
            return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value.Trim();

        return resolved?.ToString()?.Trim();
    }

    private static YamlMappingNode LoadRootMapping(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new YamlMappingNode();

        try
        {
            using var reader = new StreamReader(path);
            var yaml = new YamlStream();
            yaml.Load(reader);

            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root)
                return root;
        }
        catch
        {
        }

        return new YamlMappingNode();
    }

    private static void SaveRootMapping(YamlMappingNode root, string path) =>
        Rpcs3YamlSaveHelper.SaveMapping(root, path);

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key)
    {
        if (TryGetMapping(parent, key, out var existing))
            return existing;

        var created = new YamlMappingNode();
        parent.Children[new YamlScalarNode(key)] = created;
        return created;
    }

    private static bool TryGetMapping(YamlMappingNode parent, string key, out YamlMappingNode mapping)
    {
        mapping = null!;
        if (!TryGetChild(parent, key, out var node))
            return false;

        if (ResolveNode(node, mappingAnchors: null, documentAnchors: null) is not YamlMappingNode resolved)
            return false;

        mapping = resolved;
        return true;
    }

    private static bool TryGetChild(YamlMappingNode parent, string key, out YamlNode child)
    {
        foreach (var entry in parent.Children)
        {
            if (!string.Equals(entry.Key.ToString(), key, StringComparison.Ordinal))
                continue;

            child = entry.Value;
            return true;
        }

        child = null!;
        return false;
    }
}

public sealed record Rpcs3PatchToggle(
    string EntryKey,
    string PpuHash,
    string Name,
    string GameTitle,
    string Serial,
    string AppVersion,
    bool IsEnabled);
