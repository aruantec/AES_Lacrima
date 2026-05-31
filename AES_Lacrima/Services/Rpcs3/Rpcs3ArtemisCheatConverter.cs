using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Rpcs3;

public sealed record Rpcs3ArtemisCheatDefinition(
    string Name,
    string? Author,
    IReadOnlyList<(uint Address, uint Value)> Lines);

public static class Rpcs3ArtemisCheatConverter
{
    private static readonly Regex CodeLineRegex = new(
        @"^\s*0\s+([0-9A-Fa-f]{1,8})\s+([0-9A-Fa-f]{1,8})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<Rpcs3ArtemisCheatDefinition> ParseRawCheats(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return Array.Empty<Rpcs3ArtemisCheatDefinition>();

        var blocks = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<Rpcs3ArtemisCheatDefinition>(blocks.Length);

        foreach (var block in blocks)
        {
            var definition = ParseBlock(block);
            if (definition != null)
                results.Add(definition);
        }

        return results;
    }

    public static string BuildYamlChunk(
        IReadOnlyList<Rpcs3ArtemisCheatDefinition> cheats,
        string ppuHash,
        string titleId,
        string gameTitle,
        string? appVersion)
    {
        if (cheats.Count == 0)
            return string.Empty;

        var normalizedHash = NormalizePpuHash(ppuHash);
        var normalizedTitleId = titleId.Trim().ToUpperInvariant();
        var artemisGameTitle = BuildArtemisGameTitle(gameTitle);
        var versionToken = FormatAppVersion(appVersion);

        var builder = new StringBuilder();

        foreach (var cheat in cheats)
        {
            if (cheat.Lines.Count == 0)
                continue;

            builder.Append(normalizedHash).Append(':').AppendLine();
            builder.Append("  ").Append(QuoteYaml(cheat.Name)).Append(':').AppendLine();
            builder.AppendLine("    Games:");
            builder.Append("      ").Append(QuoteYaml(artemisGameTitle)).Append(':').AppendLine();
            builder.Append("        ").Append(normalizedTitleId).Append(": [ ").Append(versionToken).Append(" ]").AppendLine();

            if (!string.IsNullOrWhiteSpace(cheat.Author))
                builder.Append("    Author: ").Append(QuoteYaml(cheat.Author.Trim())).AppendLine();

            builder.AppendLine("    Patch:");
            foreach (var (address, value) in cheat.Lines)
            {
                builder.Append("      - [ be32, ")
                    .Append(FormatHex(address))
                    .Append(", ")
                    .Append(FormatHex(value))
                    .Append(" ]")
                    .AppendLine();
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static Rpcs3ArtemisCheatDefinition? ParseBlock(string block)
    {
        var lines = block
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (lines.Length == 0)
            return null;

        var name = lines[0].Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var startIndex = 1;
        if (startIndex < lines.Length && lines[startIndex] is "0" or "1")
            startIndex++;

        string? author = null;
        if (startIndex < lines.Length && !CodeLineRegex.IsMatch(lines[startIndex]))
        {
            author = lines[startIndex].Trim();
            startIndex++;
        }

        var codeLines = new List<(uint Address, uint Value)>();
        for (var i = startIndex; i < lines.Length; i++)
        {
            var match = CodeLineRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            if (!TryParseHex(match.Groups[1].Value, out var address) ||
                !TryParseHex(match.Groups[2].Value, out var value))
                continue;

            codeLines.Add((address, value));
        }

        if (codeLines.Count == 0)
            return null;

        return new Rpcs3ArtemisCheatDefinition(name, author, codeLines);
    }

    private static string BuildArtemisGameTitle(string gameTitle)
    {
        var trimmed = gameTitle.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return $"Custom Game {Rpcs3PatchesService.ArtemisGameTitleMarker}";

        if (trimmed.Contains(Rpcs3PatchesService.ArtemisGameTitleMarker, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"{trimmed} {Rpcs3PatchesService.ArtemisGameTitleMarker}";
    }

    private static string FormatAppVersion(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion))
            return "All";

        return appVersion.Trim();
    }

    private static string NormalizePpuHash(string ppuHash)
    {
        var trimmed = ppuHash.Trim();
        return trimmed.StartsWith("PPU-", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"PPU-{trimmed}";
    }

    private static string FormatHex(uint value) => $"0x{value:X8}";

    private static bool TryParseHex(string token, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return uint.TryParse(token.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string QuoteYaml(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
