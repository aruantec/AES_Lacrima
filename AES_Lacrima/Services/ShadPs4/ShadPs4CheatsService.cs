using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AES_Lacrima.Serialization;

namespace AES_Lacrima.Services.ShadPs4;

public static class ShadPs4CheatsService
{
    public static string GetCheatsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "user", "cheats");
    }

    public static string GetEnabledStateDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "user", "cheats_enabled");
    }

    public static string GetEnabledStatePath(string? emulatorDirectory, string cheatFileName)
    {
        var directory = GetEnabledStateDirectory(emulatorDirectory);
        var safeName = Path.GetFileName(cheatFileName);
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(safeName)}.json");
    }

    public static IReadOnlyList<ShadPs4CheatFileItem> FindCheatFiles(string? emulatorDirectory, string titleId, string? gameVersion)
    {
        var cheatsDir = GetCheatsDirectory(emulatorDirectory);
        if (string.IsNullOrWhiteSpace(cheatsDir) || !Directory.Exists(cheatsDir))
            return [];

        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var patterns = BuildSearchPatterns(normalizedTitleId, gameVersion);

        var results = new Dictionary<string, ShadPs4CheatFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(cheatsDir, pattern, SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (!results.ContainsKey(fileName))
                    results[fileName] = new ShadPs4CheatFileItem(path, fileName);
            }
        }

        return results.Values
            .OrderBy(static item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ShadPs4CheatDocument? LoadCheatDocument(string cheatFilePath)
    {
        if (string.IsNullOrWhiteSpace(cheatFilePath) || !File.Exists(cheatFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(cheatFilePath);
            return JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.ShadPs4CheatDocument);
        }
        catch
        {
            return null;
        }
    }

    public static ShadPs4CheatEnabledStateDocument LoadEnabledState(string? emulatorDirectory, string cheatFileName)
    {
        var path = GetEnabledStatePath(emulatorDirectory, cheatFileName);
        if (!File.Exists(path))
            return new ShadPs4CheatEnabledStateDocument();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.ShadPs4CheatEnabledStateDocument)
                   ?? new ShadPs4CheatEnabledStateDocument();
        }
        catch
        {
            return new ShadPs4CheatEnabledStateDocument();
        }
    }

    public static void SaveEnabledState(string? emulatorDirectory, string cheatFileName, ShadPs4CheatEnabledStateDocument state)
    {
        var path = GetEnabledStatePath(emulatorDirectory, cheatFileName);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(state, ShadPs4JsonContext.Default.ShadPs4CheatEnabledStateDocument);
        File.WriteAllText(path, json);
    }

    public static bool ModHasHint(ShadPs4CheatModDefinition mod) =>
        !string.IsNullOrWhiteSpace(mod.Hint);

    private static IEnumerable<string> BuildSearchPatterns(string titleId, string? gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            yield return $"{titleId}_*.json";
            yield break;
        }

        var trimmedVersion = gameVersion.Trim();
        yield return $"{titleId}_{trimmedVersion}*.json";

        if (trimmedVersion.Length > 1)
            yield return $"{titleId}_{trimmedVersion[1..]}*.json";
    }
}
