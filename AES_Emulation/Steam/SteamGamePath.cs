using System;
using System.Linq;

namespace AES_Emulation.Steam;

public static class SteamGamePath
{
    public const string AppIdPathPrefix = "%STEAM_APPID%:";

    public static string Build(string appId) => AppIdPathPrefix + appId.Trim();

    public static bool IsSteamGamePath(string? path)
        => !string.IsNullOrWhiteSpace(GetAppId(path));

    public static string? GetAppId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();
        if (trimmed.StartsWith(AppIdPathPrefix, StringComparison.OrdinalIgnoreCase))
            return ParseAppId(trimmed[AppIdPathPrefix.Length..]);

        var embeddedPrefixIndex = trimmed.IndexOf(AppIdPathPrefix, StringComparison.OrdinalIgnoreCase);
        if (embeddedPrefixIndex >= 0)
        {
            return ParseAppId(trimmed[(embeddedPrefixIndex + AppIdPathPrefix.Length)..]);
        }

        return null;
    }

    public static string? NormalizeVirtualPath(string? path)
    {
        var appId = GetAppId(path);
        return string.IsNullOrWhiteSpace(appId) ? null : Build(appId);
    }

    private static string? ParseAppId(string value)
    {
        var appId = value.Trim();
        return appId.Length > 0 && appId.All(char.IsDigit) ? appId : null;
    }
}
