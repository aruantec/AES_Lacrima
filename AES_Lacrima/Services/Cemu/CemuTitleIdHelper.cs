using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Cemu;

public static class CemuTitleIdHelper
{
    private static readonly Regex HexOnlyRegex = new(@"[^0-9A-Fa-f]", RegexOptions.Compiled);

    public static string? NormalizeDisplayTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            return null;

        var hex = HexOnlyRegex.Replace(titleId, string.Empty).ToUpperInvariant();
        if (hex.Length != 16)
            return hex.Length > 0 ? hex : null;

        return $"{hex[..8]}-{hex[8..]}";
    }

    public static string NormalizeMatchKey(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            return string.Empty;

        return HexOnlyRegex.Replace(titleId, string.Empty).ToUpperInvariant();
    }

    public static bool MatchesTitleId(IReadOnlyList<string> packTitleIds, string? gameTitleId)
    {
        if (packTitleIds.Count == 0)
            return false;

        if (packTitleIds.Contains("*", StringComparer.Ordinal))
            return true;

        var gameKey = NormalizeMatchKey(gameTitleId);
        if (gameKey.Length == 0)
            return false;

        foreach (var packTitleId in packTitleIds)
        {
            if (string.Equals(NormalizeMatchKey(packTitleId), gameKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
