using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services;

internal static class EmulatorReleaseNotesHelper
{
    private static readonly Regex ExcessiveBlankLinesRegex = new(@"\n{3,}", RegexOptions.Compiled);

    public static string? ParseGitHubReleaseBody(JsonObject item)
        => Normalize(item["body"]?.GetValue<string>());

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Replace("\r\n", "\n").Trim();
        text = ExcessiveBlankLinesRegex.Replace(text, "\n\n");

        const int maxLength = 6000;
        if (text.Length > maxLength)
            text = text[..maxLength].TrimEnd() + "\n\n…";

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
