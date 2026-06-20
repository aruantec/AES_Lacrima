using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AES_Lacrima.Services.Steam;

public sealed record SteamProtonVersionItem(string DisplayName, string? DirectoryPath);

public static class SteamProtonCatalogHelper
{
    public const string AutomaticDisplayName = "Automatic (Steam compatibility, then global default)";

    public static SteamProtonVersionItem AutomaticOption { get; } = new(AutomaticDisplayName, null);

    public static IReadOnlyList<SteamProtonVersionItem> GetInstalledProtonVersions()
    {
        if (!OperatingSystem.IsLinux())
            return [];

        var versions = new Dictionary<string, SteamProtonVersionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryRoot in SteamInstalledGameHelper.GetLibraryRootsForCatalog())
        {
            var commonDirectory = Path.Combine(libraryRoot, "steamapps", "common");
            if (!Directory.Exists(commonDirectory))
                continue;

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(commonDirectory, "Proton*"))
                {
                    if (!File.Exists(Path.Combine(directory, "proton")))
                        continue;

                    var fullPath = Path.GetFullPath(directory);
                    if (versions.ContainsKey(fullPath))
                        continue;

                    versions[fullPath] = new SteamProtonVersionItem(Path.GetFileName(directory), fullPath);
                }
            }
            catch
            {
                // ignored
            }
        }

        return versions.Values
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? NormalizeProtonDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(directoryPath.Trim());
            return File.Exists(Path.Combine(fullPath, "proton")) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    public static SteamProtonVersionItem? FindMatchingInstalledVersion(
        string? directoryPath,
        IReadOnlyList<SteamProtonVersionItem>? installedVersions = null)
    {
        var normalized = NormalizeProtonDirectory(directoryPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        installedVersions ??= GetInstalledProtonVersions();
        return installedVersions.FirstOrDefault(item =>
            string.Equals(item.DirectoryPath, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
