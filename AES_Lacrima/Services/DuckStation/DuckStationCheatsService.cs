using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AES_Lacrima.Services.DuckStation;

public static class DuckStationCheatsService
{
    public static string GetCheatsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "cheats");
    }

    public static string GetGameSettingsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "gamesettings");
    }

    public static IReadOnlyList<DuckStationCheatFileItem> FindCheatFiles(string? emulatorDirectory, string serial)
    {
        var cheatsDir = GetCheatsDirectory(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(cheatsDir) || !Directory.Exists(cheatsDir))
            return [];

        var normalizedSerial = serial.Trim().ToUpperInvariant();
        var results = new List<DuckStationCheatFileItem>();

        foreach (var path in Directory.EnumerateFiles(cheatsDir, $"{normalizedSerial}*.cht", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            results.Add(new DuckStationCheatFileItem(path, fileName));
        }

        return results
            .OrderBy(static item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DuckStationChtDocument? ParseChtFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            var content = File.ReadAllText(filePath);
            return ParseChtContent(content, Path.GetFileName(filePath));
        }
        catch
        {
            return null;
        }
    }

    public static DuckStationChtDocument ParseChtContent(string content, string fileName)
    {
        var document = new DuckStationChtDocument
        {
            FileName = fileName,
            Serial = Path.GetFileNameWithoutExtension(fileName).Split('_')[0]
        };

        DuckStationCheatEntry? current = null;
        var bodyBuilder = new StringBuilder();
        var lines = content.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i].TrimEnd('\r');
            var trimmed = rawLine.TrimStart();

            if (trimmed.Length == 0)
                continue;

            if (trimmed[0] == ';' || trimmed[0] == '#')
                continue;

            if (trimmed[0] == '[' && trimmed.Contains(']'))
            {
                FinalizeEntry(current, bodyBuilder, document);

                var closeBracket = trimmed.IndexOf(']');
                var fullName = trimmed[1..closeBracket];

                string? group = null;
                string name = fullName;
                var lastBackslash = fullName.LastIndexOf('\\');
                if (lastBackslash >= 0)
                {
                    group = fullName[..lastBackslash];
                    name = fullName[(lastBackslash + 1)..];
                }

                current = new DuckStationCheatEntry { Name = name, Group = group };
                bodyBuilder.Clear();
                continue;
            }

            if (current == null)
                continue;

            if (TryParseMetadataLine(trimmed, "Type", out var typeValue))
            {
                current.Type = typeValue;
            }
            else if (TryParseMetadataLine(trimmed, "Activation", out var activationValue))
            {
                current.Activation = activationValue;
            }
            else if (TryParseMetadataLine(trimmed, "Description", out var descValue))
            {
                current.Description = descValue;
            }
            else if (TryParseMetadataLine(trimmed, "Author", out var authorValue))
            {
                current.Author = authorValue;
            }
            else if (TryParseMetadataLine(trimmed, "Option", out var optionValue))
            {
                var colonIdx = optionValue.IndexOf(':');
                if (colonIdx >= 0)
                {
                    current.Options ??= [];
                    current.Options.Add(new DuckStationCheatOption
                    {
                        Label = optionValue[..colonIdx],
                        Value = optionValue[(colonIdx + 1)..]
                    });
                }
            }
            else if (TryParseMetadataLine(trimmed, "OptionRange", out var rangeValue))
            {
                var colonIdx = rangeValue.IndexOf(':');
                if (colonIdx >= 0 &&
                    int.TryParse(rangeValue[..colonIdx], out var min) &&
                    int.TryParse(rangeValue[(colonIdx + 1)..], out var max))
                {
                    current.OptionRange = new DuckStationCheatOptionRange { Min = min, Max = max };
                }
            }
            else
            {
                if (bodyBuilder.Length > 0)
                    bodyBuilder.Append('\n');
                bodyBuilder.Append(rawLine);
            }
        }

        FinalizeEntry(current, bodyBuilder, document);
        return document;
    }

    public static HashSet<string> LoadEnabledCheats(string? emulatorDirectory, string serial)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var iniPath = GetGameSettingsPath(emulatorDirectory, serial);

        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            return result;

        try
        {
            var lines = File.ReadAllLines(iniPath);
            var inCheatsSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inCheatsSection = string.Equals(trimmed, "[Cheats]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inCheatsSection)
                    continue;

                if (TryParseMetadataLine(trimmed, "Enable", out var cheatName))
                    result.Add(cheatName);
            }
        }
        catch
        {
        }

        return result;
    }

    public static void SaveEnabledCheats(string? emulatorDirectory, string serial, IEnumerable<string> enabledCheatNames)
    {
        var iniPath = GetGameSettingsPath(emulatorDirectory, serial);
        if (string.IsNullOrWhiteSpace(iniPath))
            return;

        var directory = Path.GetDirectoryName(iniPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var enabledList = enabledCheatNames.ToList();
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : [];

        RemoveSection(lines, "Cheats");

        if (enabledList.Count > 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");

            lines.Add("[Cheats]");
            lines.Add("EnableCheats = true");
            foreach (var name in enabledList)
                lines.Add($"Enable = {name}");
        }
        else
        {
            EnsureEnableCheatsRemoved(lines);
        }

        File.WriteAllLines(iniPath, lines);
    }

    private static string? GetGameSettingsPath(string? emulatorDirectory, string serial)
    {
        var dir = GetGameSettingsDirectory(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        return Path.Combine(dir, $"{serial.Trim().ToUpperInvariant()}.ini");
    }

    private static void RemoveSection(List<string> lines, string sectionName)
    {
        var sectionHeader = $"[{sectionName}]";
        var startIdx = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0)
            return;

        var endIdx = startIdx + 1;
        while (endIdx < lines.Count)
        {
            var trimmed = lines[endIdx].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                break;
            endIdx++;
        }

        lines.RemoveRange(startIdx, endIdx - startIdx);
    }

    private static void EnsureEnableCheatsRemoved(List<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                lines.RemoveAt(i);
        }
    }

    private static void FinalizeEntry(DuckStationCheatEntry? entry, StringBuilder bodyBuilder, DuckStationChtDocument document)
    {
        if (entry == null)
            return;

        entry.Body = bodyBuilder.ToString().Trim();
        document.Entries.Add(entry);
    }

    private static bool TryParseMetadataLine(string line, string key, out string value)
    {
        value = string.Empty;

        var eqIdx = line.IndexOf('=');
        if (eqIdx < 0)
            return false;

        var lineKey = line[..eqIdx].TrimEnd();
        if (!string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            return false;

        value = line[(eqIdx + 1)..].TrimStart();
        return true;
    }
}
