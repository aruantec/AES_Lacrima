using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using AES_Lacrima.Services.Emulation;
using log4net;

namespace AES_Lacrima.Services.Dolphin;

public static class DolphinGameIniService
{
    private static readonly ILog Log = LogHelper.For(typeof(DolphinGameIniService));
    private const string GeckoCodesBaseUrl = "https://codes.rc24.xyz/txt.php?txt=";

    private static readonly HttpClient GeckoCodesHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 4
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    static DolphinGameIniService()
    {
        GeckoCodesHttp.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima/1.0");
    }

    private static readonly string[] EntrySectionNames =
    [
        "OnFrame",
        "ActionReplay",
        "Gecko"
    ];

    public static string? ResolveEmulatorDirectory(string? configuredDirectory, string? launcherPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && Directory.Exists(configuredDirectory))
            return configuredDirectory.Trim();

        var executablePath = EmulatorHandlerBase.ResolveLauncherExecutablePath(launcherPath);
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var directory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    public static string? ResolvePortableUserDirectory(string? emulatorDirectory, string? launcherPath)
    {
        var executablePath = EmulatorHandlerBase.ResolveLauncherExecutablePath(launcherPath);
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var portableUser = Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "User");
            if (Directory.Exists(portableUser))
                return portableUser;
        }

        if (!string.IsNullOrWhiteSpace(emulatorDirectory))
        {
            var candidate = Path.Combine(emulatorDirectory, "User");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return GetDefaultUserDirectory();
    }

    public static string? GetSysGameSettingsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return null;

        var sysPath = Path.Combine(emulatorDirectory, "Sys", "GameSettings");
        return Directory.Exists(sysPath) ? sysPath : null;
    }

    public static string GetUserGameSettingsDirectory(string userDirectory) =>
        Path.Combine(userDirectory, "GameSettings");

    public static string? NormalizeGameId(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            return null;

        var normalized = gameId.Trim().ToUpperInvariant();
        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 6)
            return null;

        return Regex.IsMatch(normalized, "^[A-Z0-9]{6}$") ? normalized : null;
    }

    public static string? ResolveGameIdFromMetadata(string romPath, string? albumTitle = null)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return null;

        try
        {
            var cacheId = AES_Controls.Helpers.BinaryMetadataHelper.GetCacheId(romPath);
            var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
            var metadata = AES_Controls.Helpers.BinaryMetadataHelper.LoadMetadata(cachePath);
            if (metadata == null)
                return null;

            var wiiId = NormalizeGameId(metadata.WiiTitleId);
            var gcId = NormalizeGameId(metadata.GameCubeTitleId);
            var section = NintendoDiscMetadataHelper.ResolveDiscSection(albumTitle, romPath);

            if (section == DiscSection.Wii)
                return wiiId ?? gcId;

            if (section == DiscSection.GameCube)
                return gcId ?? wiiId;

            return wiiId ?? gcId;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to resolve Dolphin game id from metadata.", ex);
            return null;
        }
    }

    public static DolphinGameSettingsDocument LoadMergedSettings(
        string gameId,
        string? sysGameSettingsDirectory,
        string userGameSettingsDirectory)
    {
        var normalizedId = NormalizeGameId(gameId) ?? gameId.Trim().ToUpperInvariant();
        var globalPath = sysGameSettingsDirectory == null
            ? null
            : Path.Combine(sysGameSettingsDirectory, normalizedId + ".ini");
        var localPath = Path.Combine(userGameSettingsDirectory, normalizedId + ".ini");

        var globalIni = globalPath != null && File.Exists(globalPath)
            ? DolphinIniFile.Load(globalPath)
            : new DolphinIniFile();
        var localIni = File.Exists(localPath)
            ? DolphinIniFile.Load(localPath)
            : new DolphinIniFile();

        var entries = new List<DolphinGameIniEntry>();
        foreach (var section in EntrySectionNames)
        {
            var kind = SectionToKind(section);
            var merged = MergeSectionEntries(kind, section, globalIni, localIni);
            entries.AddRange(merged);
        }

        return new DolphinGameSettingsDocument
        {
            GameId = normalizedId,
            Entries = entries
        };
    }

    public static void SaveEnabledState(
        string userDirectory,
        DolphinGameSettingsDocument document)
    {
        Directory.CreateDirectory(GetUserGameSettingsDirectory(userDirectory));
        var localPath = Path.Combine(GetUserGameSettingsDirectory(userDirectory), document.GameId + ".ini");
        var localIni = File.Exists(localPath) ? DolphinIniFile.Load(localPath) : new DolphinIniFile();

        foreach (var section in EntrySectionNames)
        {
            var kind = SectionToKind(section);
            var sectionEntries = document.Entries
                .Where(e => e.Kind == kind)
                .ToList();

            var bodyLines = new List<string>();
            var enabledLines = new List<string>();
            var disabledLines = new List<string>();

            foreach (var entry in sectionEntries)
            {
                if (entry.Enabled != entry.DefaultEnabled)
                    (entry.Enabled ? enabledLines : disabledLines).Add('$' + entry.Name);

                if (!entry.UserDefined)
                    continue;

                bodyLines.Add('$' + entry.Name);
                bodyLines.AddRange(entry.Lines);
            }

            localIni.SetLines(section, bodyLines);
            localIni.SetLines(section + "_Enabled", enabledLines);
            localIni.SetLines(section + "_Disabled", disabledLines);
        }

        localIni.Save(localPath);
    }

    public static void EnsureCheatsEnabled(string? portableUserDirectory, string? launcherPath)
    {
        foreach (var userDirectory in EnumerateUserDirectories(portableUserDirectory, launcherPath))
            TrySetIniValue(userDirectory, "Core", "EnableCheats", "True");
    }

    public static async Task<(bool Success, string Message, int AddedCount)> DownloadGeckoCodesAsync(
        string userDirectory,
        string gameId,
        string? sysGameSettingsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeGameId(gameId);
        if (string.IsNullOrWhiteSpace(normalizedId))
            return (false, "A valid 6-character game id is required.", 0);

        try
        {
            var endpoint = GeckoCodesBaseUrl + Uri.EscapeDataString(normalizedId);
            using var response = await GeckoCodesHttp.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, $"Gecko code download failed (HTTP {(int)response.StatusCode}).", 0);

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var downloaded = ParseGeckoTxtDownload(content);
            if (downloaded.Count == 0)
                return (false, $"No Gecko codes were found for {normalizedId} on codes.rc24.xyz.", 0);

            Directory.CreateDirectory(GetUserGameSettingsDirectory(userDirectory));
            var localPath = Path.Combine(GetUserGameSettingsDirectory(userDirectory), normalizedId + ".ini");
            var localIni = File.Exists(localPath) ? DolphinIniFile.Load(localPath) : new DolphinIniFile();

            var existingNames = CollectExistingGeckoNames(normalizedId, sysGameSettingsDirectory, localIni);

            var bodyLines = localIni.GetLines("Gecko").ToList();
            var added = 0;

            foreach (var code in downloaded)
            {
                if (string.IsNullOrWhiteSpace(code.Name) || code.Lines.Count == 0)
                    continue;

                if (!existingNames.Add(code.Name))
                    continue;

                bodyLines.Add('$' + code.Name);
                bodyLines.AddRange(code.Lines);
                added++;
            }

            localIni.SetLines("Gecko", bodyLines);
            localIni.Save(localPath);

            return added == 0
                ? (true, "Gecko codes are already up to date.", 0)
                : (true, $"Downloaded and added {added} Gecko code(s) to User/GameSettings/{normalizedId}.ini.", added);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to download Dolphin Gecko codes.", ex);
            return (false, $"Failed to download Gecko codes: {ex.Message}", 0);
        }
    }

    private static HashSet<string> CollectExistingGeckoNames(
        string gameId,
        string? sysGameSettingsDirectory,
        DolphinIniFile localIni)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in ParseNamedEntries("Gecko", localIni.GetLines("Gecko")))
            names.Add(entry.Name);

        if (!string.IsNullOrWhiteSpace(sysGameSettingsDirectory))
        {
            var globalPath = Path.Combine(sysGameSettingsDirectory, gameId + ".ini");
            if (File.Exists(globalPath))
            {
                var globalIni = DolphinIniFile.Load(globalPath);
                foreach (var entry in ParseNamedEntries("Gecko", globalIni.GetLines("Gecko")))
                    names.Add(entry.Name);
            }
        }

        return names;
    }

    private static IEnumerable<string> EnumerateUserDirectories(string? portableUserDirectory, string? launcherPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var portable = ResolvePortableUserDirectory(null, launcherPath);
        if (!string.IsNullOrWhiteSpace(portable) && seen.Add(portable))
            yield return portable;

        if (!string.IsNullOrWhiteSpace(portableUserDirectory) && seen.Add(portableUserDirectory))
            yield return portableUserDirectory;

        var defaultUser = GetDefaultUserDirectory();
        if (!string.IsNullOrWhiteSpace(defaultUser) && seen.Add(defaultUser))
            yield return defaultUser;
    }

    private static string? GetDefaultUserDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) ? null : Path.Combine(documents, "Dolphin Emulator");
        }

        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "share", "dolphin-emu");

        if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, "Library", "Application Support", "Dolphin");

        return null;
    }

    private static void TrySetIniValue(string userDirectory, string section, string key, string value)
    {
        try
        {
            var configDirectory = Path.Combine(userDirectory, "Config");
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "Dolphin.ini");
            var ini = File.Exists(configPath) ? DolphinIniFile.Load(configPath) : new DolphinIniFile();

            var lines = ini.GetLines(section).ToList();
            var keyPrefix = key + " =";
            var newLine = $"{key} = {value}";
            var found = false;

            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase) ||
                    !lines[i].Contains('='))
                {
                    continue;
                }

                lines[i] = newLine;
                found = true;
                break;
            }

            if (!found)
                lines.Add(newLine);

            ini.SetLines(section, lines);
            ini.Save(configPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to set Dolphin.ini {section}/{key}.", ex);
        }
    }

    private static DolphinGameIniEntryKind SectionToKind(string section) =>
        section switch
        {
            "OnFrame" => DolphinGameIniEntryKind.OnFrame,
            "ActionReplay" => DolphinGameIniEntryKind.ActionReplay,
            _ => DolphinGameIniEntryKind.Gecko
        };

    private static List<DolphinGameIniEntry> MergeSectionEntries(
        DolphinGameIniEntryKind kind,
        string section,
        DolphinIniFile globalIni,
        DolphinIniFile localIni)
    {
        var globalEntries = ParseNamedEntries(section, globalIni.GetLines(section));
        ApplyEnabledOverrides(globalEntries, globalIni.GetLines(section + "_Enabled"), enabled: true);
        ApplyEnabledOverrides(globalEntries, globalIni.GetLines(section + "_Disabled"), enabled: false);

        foreach (var entry in globalEntries)
        {
            entry.Kind = kind;
            entry.DefaultEnabled = entry.Enabled;
        }

        var merged = globalEntries.ToDictionary(static e => e.Name, StringComparer.OrdinalIgnoreCase);

        var localEntries = ParseNamedEntries(section, localIni.GetLines(section));
        ApplyEnabledOverrides(localEntries, localIni.GetLines(section + "_Enabled"), enabled: true);
        ApplyEnabledOverrides(localEntries, localIni.GetLines(section + "_Disabled"), enabled: false);

        foreach (var local in localEntries)
        {
            local.Kind = kind;
            local.UserDefined = true;

            if (merged.TryGetValue(local.Name, out var existing))
            {
                existing.Lines = local.Lines;
                existing.UserDefined = true;
                existing.Enabled = local.Enabled;
                if (!localIni.GetLines(section + "_Enabled").Any(l => NameFromEnabledLine(l) == local.Name) &&
                    !localIni.GetLines(section + "_Disabled").Any(l => NameFromEnabledLine(l) == local.Name))
                {
                    existing.DefaultEnabled = existing.Enabled;
                }
            }
            else
            {
                local.DefaultEnabled = local.Enabled;
                merged[local.Name] = local;
            }
        }

        foreach (var localEnabled in localIni.GetLines(section + "_Enabled"))
        {
            var name = NameFromEnabledLine(localEnabled);
            if (string.IsNullOrWhiteSpace(name) || !merged.TryGetValue(name, out var entry))
                continue;

            entry.Enabled = true;
        }

        foreach (var localDisabled in localIni.GetLines(section + "_Disabled"))
        {
            var name = NameFromEnabledLine(localDisabled);
            if (string.IsNullOrWhiteSpace(name) || !merged.TryGetValue(name, out var entry))
                continue;

            entry.Enabled = false;
        }

        return merged.Values
            .Select(static e => e.ToEntry())
            .OrderBy(static e => e.Kind)
            .ThenBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyEnabledOverrides(
        List<ParsedNamedEntry> entries,
        IReadOnlyList<string> overrideLines,
        bool enabled)
    {
        foreach (var line in overrideLines)
        {
            var name = NameFromEnabledLine(line);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var entry = entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                entry.Enabled = enabled;
        }
    }

    private static string? NameFromEnabledLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return null;

        if (trimmed[0] == '$' || trimmed[0] == '+')
            return trimmed[1..].Trim();

        return trimmed;
    }

    private static List<ParsedNamedEntry> ParseNamedEntries(string section, IReadOnlyList<string> lines)
    {
        var results = new List<ParsedNamedEntry>();
        ParsedNamedEntry? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '$' || (section == "Gecko" && line[0] == '+'))
            {
                if (current != null && current.Lines.Count > 0)
                    results.Add(current);

                var enabledInline = line[0] == '+';
                var nameStart = enabledInline ? 2 : 1;
                var name = line[nameStart..].Trim();
                var bracket = name.IndexOf('[');
                if (bracket >= 0)
                    name = name[..bracket].Trim();

                current = new ParsedNamedEntry
                {
                    Name = name,
                    Enabled = enabledInline,
                    Lines = []
                };
                continue;
            }

            if (section == "Gecko" && line[0] == '*')
                continue;

            current ??= new ParsedNamedEntry { Name = "Unnamed", Enabled = false, Lines = [] };
            current.Lines.Add(rawLine.TrimEnd());
        }

        if (current != null && (current.Lines.Count > 0 || !string.IsNullOrWhiteSpace(current.Name)))
            results.Add(current);

        return results;
    }

    /// <summary>
    /// Parses codes.rc24.xyz / geckocodes.org text using the same state machine as Dolphin's GeckoCodeConfig::DownloadCodes.
    /// </summary>
    private static List<ParsedGeckoDownload> ParseGeckoTxtDownload(string content)
    {
        var results = new List<ParsedGeckoDownload>();
        var lines = content.Split('\n');
        var lineIndex = 0;

        // Skip the 3-line file header (game id, title, blank).
        for (var skipped = 0; skipped < 3 && lineIndex < lines.Length; skipped++, lineIndex++)
        {
        }

        var readState = 0;
        ParsedGeckoDownload? current = null;

        for (; lineIndex < lines.Length; lineIndex++)
        {
            var line = StripLine(lines[lineIndex]);
            if (line.Length == 0)
            {
                if (current != null && current.Lines.Count > 0)
                    results.Add(current);
                current = null;
                readState = 0;
                continue;
            }

            switch (readState)
            {
                case 0:
                    current = new ParsedGeckoDownload
                    {
                        Name = line.Split('[')[0].Trim(),
                        Lines = []
                    };
                    readState = 1;
                    break;

                case 1:
                    if (TryParseGeckoCodeLine(line, out var codeLine))
                    {
                        current!.Lines.Add(codeLine);
                    }
                    else
                    {
                        readState = 2;
                    }
                    break;

                case 2:
                    // Notes / section labels — ignored for INI import.
                    break;
            }
        }

        if (current != null && current.Lines.Count > 0)
            results.Add(current);

        return results;
    }

    private static bool TryParseGeckoCodeLine(string line, out string codeLine)
    {
        codeLine = string.Empty;
        var parts = line.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Length != 8 || parts[1].Length != 8)
            return false;

        if (!IsHexToken(parts[0]) || !IsHexToken(parts[1]))
            return false;

        codeLine = $"{parts[0]} {parts[1]}";
        return true;
    }

    private static bool IsHexToken(string value)
    {
        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        return true;
    }

    private static string StripLine(string line) => line.TrimEnd('\r').Trim();

    private sealed class ParsedNamedEntry
    {
        public required string Name { get; init; }

        public required List<string> Lines { get; set; }

        public bool Enabled { get; set; }

        public DolphinGameIniEntryKind Kind { get; set; }

        public bool UserDefined { get; set; }

        public bool DefaultEnabled { get; set; }

        public DolphinGameIniEntry ToEntry() => new()
        {
            Kind = Kind,
            Name = Name,
            Lines = Lines,
            Enabled = Enabled,
            DefaultEnabled = DefaultEnabled,
            UserDefined = UserDefined
        };
    }

    private sealed class ParsedGeckoDownload
    {
        public required string Name { get; init; }

        public required List<string> Lines { get; init; }
    }
}

internal sealed class DolphinIniFile
{
    private readonly Dictionary<string, List<string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static DolphinIniFile Load(string path)
    {
        var ini = new DolphinIniFile();
        var currentSection = string.Empty;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSection = line[1..^1].Trim();
                ini.EnsureSection(currentSection);
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentSection))
                continue;

            ini.EnsureSection(currentSection).Add(rawLine);
        }

        return ini;
    }

    public IReadOnlyList<string> GetLines(string section)
    {
        return _sections.TryGetValue(section, out var lines)
            ? lines
            : Array.Empty<string>();
    }

    public void SetLines(string section, IReadOnlyList<string> lines)
    {
        _sections[section] = lines.Where(static l => !string.IsNullOrWhiteSpace(l)).ToList();
    }

    public void Save(string path)
    {
        var builder = new StringBuilder();
        foreach (var (section, lines) in _sections.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (lines.Count == 0)
                continue;

            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append('[').Append(section).AppendLine("]");
            foreach (var line in lines)
                builder.AppendLine(line);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, builder.ToString());
    }

    private List<string> EnsureSection(string section)
    {
        if (!_sections.TryGetValue(section, out var lines))
        {
            lines = [];
            _sections[section] = lines;
        }

        return lines;
    }
}
