using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AES_Emulation.Steam;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Steam;

internal sealed record SteamInstalledGame(
    string AppId,
    string Name,
    string InstallDirectory,
    string LibraryRoot,
    string? IconPath,
    string GamePath);

internal static class SteamInstalledGameHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(SteamInstalledGameHelper));

    public const string AppIdPathPrefix = SteamGamePath.AppIdPathPrefix;

    private const int FullyInstalledStateFlag = 4;

    private static readonly HashSet<string> IgnoredInstallDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steamworks Shared",
        "SteamLinuxRuntime",
        "SteamLinuxRuntime_soldier",
        "SteamLinuxRuntime_sniper",
        "SteamLinuxRuntime_4",
        "Proton",
        "Proton - Experimental",
    };

    private static readonly HashSet<string> IgnoredExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler64.exe",
        "UnityCrashHandler32.exe",
        "UnityCrashHandler.exe",
        "uninstall.exe",
        "setup.exe",
        "install.exe",
        "redist.exe",
        "EasyAntiCheat_EOS_Setup.exe",
        "EasyAntiCheat_Setup.exe",
    };

    private static readonly Regex AppManifestAppIdRegex = new(
        "\"appid\"\\s+\"(?<id>\\d+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AppManifestNameRegex = new(
        "\"name\"\\s+\"(?<name>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AppManifestInstallDirRegex = new(
        "\"installdir\"\\s+\"(?<dir>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AppManifestStateFlagsRegex = new(
        "\"StateFlags\"\\s+\"(?<flags>\\d+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsSteamGamePath(string? path) => SteamGamePath.IsSteamGamePath(path);

    public static string BuildGamePath(string appId) => SteamGamePath.Build(appId);

    public static string? GetAppId(string? path) => SteamGamePath.GetAppId(path);

    public static string? GetTitleName(string? path)
    {
        var appId = GetAppId(path);
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        return GetInstalledGames()
            .FirstOrDefault(game => string.Equals(game.AppId, appId, StringComparison.Ordinal))
            ?.Name;
    }

    public static string? GetPreferredIconPath(string? path)
    {
        var appId = GetAppId(path);
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        var installedIconPath = GetInstalledGame(appId)?.IconPath;
        if (!string.IsNullOrWhiteSpace(installedIconPath) && File.Exists(installedIconPath))
            return installedIconPath;

        return ResolveIconPathForApp(appId);
    }

    public static SteamInstalledGame? GetInstalledGame(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        return GetInstalledGames()
            .FirstOrDefault(game => string.Equals(game.AppId, appId, StringComparison.Ordinal));
    }

    public static bool HasProtonPrefix(string appId)
    {
        var game = GetInstalledGame(appId);
        if (game == null)
            return false;

        var prefixPath = Path.Combine(game.LibraryRoot, "steamapps", "compatdata", game.AppId, "pfx");
        return Directory.Exists(prefixPath);
    }

    public static bool TryResolveProtonLaunch(
        SteamInstalledGame game,
        out string protonPath,
        out string gameExecutable,
        SteamProtonLaunchPreferences? preferences = null)
    {
        protonPath = string.Empty;
        gameExecutable = string.Empty;

        if (!TryResolveGameExecutable(game.InstallDirectory, preferWindowsExecutable: true, out gameExecutable))
            return false;

        var protonDirectory = ResolveProtonDirectoryForGame(game, preferences);
        if (string.IsNullOrWhiteSpace(protonDirectory))
            return false;

        protonPath = Path.Combine(protonDirectory, "proton");
        return File.Exists(protonPath);
    }

    public static string? ResolveProtonDirectoryForGame(
        SteamInstalledGame game,
        SteamProtonLaunchPreferences? preferences = null)
    {
        if (preferences?.GameOverrides.TryGetValue(game.AppId, out var overrideDirectory) == true)
        {
            var normalizedOverride = SteamProtonCatalogHelper.NormalizeProtonDirectory(overrideDirectory);
            if (!string.IsNullOrWhiteSpace(normalizedOverride))
                return normalizedOverride;
        }

        var protonDirectory = TryResolveProtonDirectory(game.LibraryRoot, game.AppId);
        if (!string.IsNullOrWhiteSpace(protonDirectory))
            return protonDirectory;

        var normalizedDefault = SteamProtonCatalogHelper.NormalizeProtonDirectory(preferences?.DefaultProtonDirectory);
        if (!string.IsNullOrWhiteSpace(normalizedDefault))
            return normalizedDefault;

        return TryResolveDefaultProtonDirectory(game.LibraryRoot);
    }

    public static IEnumerable<string> GetLibraryRootsForCatalog() => GetLibraryRoots();

    public static bool TryResolveNativeLaunch(SteamInstalledGame game, out string nativeExecutable)
    {
        nativeExecutable = string.Empty;

        var compatDirectory = Path.Combine(game.LibraryRoot, "steamapps", "compatdata", game.AppId);
        if (Directory.Exists(compatDirectory) &&
            TryResolveGameExecutable(game.InstallDirectory, preferWindowsExecutable: true, out _))
        {
            return false;
        }

        return TryResolveGameExecutable(game.InstallDirectory, preferWindowsExecutable: false, out nativeExecutable);
    }

    internal static string? TryResolveProtonDirectory(string libraryRoot, string appId)
    {
        var configInfoPath = Path.Combine(libraryRoot, "steamapps", "compatdata", appId, "config_info");
        if (!File.Exists(configInfoPath))
            return null;

        try
        {
            foreach (var line in File.ReadAllLines(configInfoPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var filesIndex = line.IndexOf("/files/", StringComparison.Ordinal);
                if (filesIndex <= 0)
                    continue;

                var protonDirectory = line[..filesIndex].Trim();
                if (Directory.Exists(protonDirectory) && File.Exists(Path.Combine(protonDirectory, "proton")))
                    return protonDirectory;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to read Steam compat config '{configInfoPath}'.", ex);
        }

        return null;
    }

    internal static string? TryResolveDefaultProtonDirectory(string libraryRoot)
    {
        var commonDirectory = Path.Combine(libraryRoot, "steamapps", "common");
        if (!Directory.Exists(commonDirectory))
            return null;

        var preferredNames = new[]
        {
            "Proton - Experimental",
            "Proton Hotfix",
        };

        foreach (var preferredName in preferredNames)
        {
            var candidate = Path.Combine(commonDirectory, preferredName);
            if (File.Exists(Path.Combine(candidate, "proton")))
                return candidate;
        }

        try
        {
            return Directory.EnumerateDirectories(commonDirectory, "Proton*")
                .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "proton")));
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed while probing default Proton installs under '{commonDirectory}'.", ex);
            return null;
        }
    }

    internal static bool TryResolveGameExecutable(
        string installDirectory,
        bool preferWindowsExecutable,
        out string executablePath)
    {
        executablePath = string.Empty;
        if (!Directory.Exists(installDirectory))
            return false;

        if (preferWindowsExecutable)
        {
            var windowsCandidates = Directory.EnumerateFiles(installDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !IgnoredExecutableNames.Contains(Path.GetFileName(path)))
                .OrderByDescending(path => new FileInfo(path).Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (windowsCandidates.Length > 0)
            {
                executablePath = windowsCandidates[0];
                return true;
            }
        }

        var nativeCandidates = EnumerateNativeExecutables(installDirectory).ToArray();
        if (nativeCandidates.Length == 0)
            return false;

        executablePath = nativeCandidates[0];
        return true;
    }

    private static IEnumerable<string> EnumerateNativeExecutables(string installDirectory)
    {
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(installDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to enumerate native executables in '{installDirectory}'.", ex);
            return [];
        }

        var executables = new List<string>();
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                IgnoredExecutableNames.Contains(fileName))
            {
                continue;
            }

            try
            {
                if (File.Exists(path) && IsNativeExecutable(path))
                    executables.Add(path);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to inspect native executable candidate '{path}'.", ex);
            }
        }

        return executables;
    }

    private static bool IsNativeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & UnixFileMode.UserExecute) != 0 ||
                   (mode & UnixFileMode.GroupExecute) != 0 ||
                   (mode & UnixFileMode.OtherExecute) != 0;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to read unix file mode for '{path}'.", ex);
            return false;
        }
    }

    private static readonly object InstalledGamesCacheLock = new();
    private static IReadOnlyList<SteamInstalledGame>? _cachedInstalledGames;
    private static long _cachedInstalledGamesAtMs;

    public static void InvalidateInstalledGamesCache()
    {
        lock (InstalledGamesCacheLock)
            _cachedInstalledGames = null;
    }

    public static IReadOnlyList<SteamInstalledGame> GetInstalledGames()
    {
        if (!OperatingSystem.IsLinux())
            return [];

        var nowMs = Environment.TickCount64;
        lock (InstalledGamesCacheLock)
        {
            if (_cachedInstalledGames != null && nowMs - _cachedInstalledGamesAtMs < 30_000)
                return _cachedInstalledGames;
        }

        var games = EnumerateInstalledGamesUncached();

        lock (InstalledGamesCacheLock)
        {
            _cachedInstalledGames = games;
            _cachedInstalledGamesAtMs = nowMs;
        }

        return games;
    }

    private static IReadOnlyList<SteamInstalledGame> EnumerateInstalledGamesUncached()
    {
        var games = new Dictionary<string, SteamInstalledGame>(StringComparer.Ordinal);
        foreach (var libraryRoot in GetLibraryRoots())
        {
            var steamAppsDirectory = Path.Combine(libraryRoot, "steamapps");
            if (!Directory.Exists(steamAppsDirectory))
                continue;

            foreach (var manifestPath in Directory.EnumerateFiles(steamAppsDirectory, "appmanifest_*.acf"))
            {
                if (!TryParseAppManifest(manifestPath, libraryRoot, out var game))
                    continue;

                games[game.AppId] = game;
            }
        }

        return games.Values
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IEnumerable<string> GetWatchPaths()
    {
        if (!OperatingSystem.IsLinux())
            yield break;

        foreach (var libraryRoot in GetLibraryRoots())
        {
            var steamAppsDirectory = Path.Combine(libraryRoot, "steamapps");
            if (Directory.Exists(steamAppsDirectory))
                yield return steamAppsDirectory;
        }
    }

    public static IEnumerable<string> GetIconWatchPaths()
    {
        if (!OperatingSystem.IsLinux())
            yield break;

        foreach (var libraryRoot in GetLibraryRoots())
        {
            var legacyIconCacheDirectory = Path.Combine(libraryRoot, "steamapps", "librarycache");
            if (Directory.Exists(legacyIconCacheDirectory))
                yield return legacyIconCacheDirectory;

            var appCacheDirectory = Path.Combine(libraryRoot, "appcache", "librarycache");
            if (Directory.Exists(appCacheDirectory))
                yield return appCacheDirectory;
        }
    }

    internal static bool IsIgnoredSteamToolInstall(string installDir, string? name)
    {
        if (IgnoredInstallDirectories.Contains(installDir))
            return true;

        if (installDir.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
            installDir.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseAppManifest(string manifestPath, string libraryRoot, out SteamInstalledGame game)
    {
        game = default!;
        try
        {
            var text = File.ReadAllText(manifestPath);
            var appIdMatch = AppManifestAppIdRegex.Match(text);
            var nameMatch = AppManifestNameRegex.Match(text);
            var installDirMatch = AppManifestInstallDirRegex.Match(text);
            var stateFlagsMatch = AppManifestStateFlagsRegex.Match(text);

            if (!appIdMatch.Success || !nameMatch.Success || !installDirMatch.Success)
                return false;

            if (stateFlagsMatch.Success &&
                int.TryParse(stateFlagsMatch.Groups["flags"].Value, out var stateFlags) &&
                (stateFlags & FullyInstalledStateFlag) != FullyInstalledStateFlag)
            {
                return false;
            }

            var appId = appIdMatch.Groups["id"].Value.Trim();
            var installDir = UnescapeVdfString(installDirMatch.Groups["dir"].Value.Trim());
            var name = UnescapeVdfString(nameMatch.Groups["name"].Value.Trim());

            if (string.IsNullOrWhiteSpace(appId) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(installDir) ||
                IsIgnoredSteamToolInstall(installDir, name))
            {
                return false;
            }

            var commonDirectory = Path.Combine(libraryRoot, "steamapps", "common", installDir);
            if (!Directory.Exists(commonDirectory))
                return false;

            var iconPath = ResolveIconPathForApp(appId);
            var normalizedLibraryRoot = SteamLibraryPathHelper.NormalizeLibraryRoot(libraryRoot);
            game = new SteamInstalledGame(
                appId,
                name,
                commonDirectory,
                normalizedLibraryRoot,
                iconPath,
                BuildGamePath(appId));
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to parse Steam app manifest '{manifestPath}'.", ex);
            return false;
        }
    }

    internal static string? ResolveIconPathForApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        foreach (var libraryRoot in GetLibraryRoots())
        {
            var iconPath = ResolveIconPath(libraryRoot, appId);
            if (!string.IsNullOrWhiteSpace(iconPath))
                return iconPath;
        }

        return null;
    }

    internal static string? TryResolveSteamClientRoot()
    {
        foreach (var candidate in GetSteamInstallRoots())
        {
            var normalized = SteamLibraryPathHelper.NormalizeLibraryRoot(candidate);
            if (File.Exists(Path.Combine(normalized, "ubuntu12_64", "steam")) ||
                Directory.Exists(Path.Combine(normalized, "steamapps", "common", "SteamLinuxRuntime_4")))
            {
                return normalized;
            }
        }

        return GetSteamInstallRoots().FirstOrDefault();
    }

    internal static string? ResolveIconPath(string libraryRoot, string appId)
    {
        var cacheDirectory = Path.Combine(libraryRoot, "appcache", "librarycache", appId);
        var candidates = new[]
        {
            Path.Combine(cacheDirectory, "library_600x900.jpg"),
            Path.Combine(cacheDirectory, "library_header.jpg"),
            Path.Combine(libraryRoot, "steamapps", "librarycache", $"{appId}_icon.jpg"),
            Path.Combine(libraryRoot, "steamapps", "librarycache", $"{appId}_header.jpg"),
            Path.Combine(libraryRoot, "appcache", "librarycache", $"{appId}_library_600x900.jpg"),
            Path.Combine(libraryRoot, "appcache", "librarycache", $"{appId}_header.jpg"),
            Path.Combine(libraryRoot, "config", "grid", $"{appId}.jpg"),
            Path.Combine(libraryRoot, "config", "grid", $"{appId}.png"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return TryFindIconInCacheDirectory(cacheDirectory);
    }

    private static string? TryFindIconInCacheDirectory(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
            return null;

        foreach (var fileName in new[] { "library_600x900.jpg", "library_header.jpg", "library_capsule.jpg" })
        {
            try
            {
                var match = Directory.EnumerateFiles(cacheDirectory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(File.Exists);

                if (!string.IsNullOrWhiteSpace(match))
                    return match;
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to search Steam icon cache directory '{cacheDirectory}' for '{fileName}'.", ex);
            }
        }

        try
        {
            return Directory.EnumerateFiles(cacheDirectory, "*.jpg", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length > 4096;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .OrderByDescending(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to enumerate Steam icon cache directory '{cacheDirectory}'.", ex);
            return null;
        }
    }

    private static IEnumerable<string> GetLibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in GetSteamInstallRoots())
        {
            AddLibraryRoot(roots, steamRoot);

            var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            var libraryFolders = SteamVdfParser.ParseFile(libraryFoldersPath);
            foreach (var libraryPath in SteamVdfParser.CollectStringValues(libraryFolders, "path"))
            {
                try
                {
                    AddLibraryRoot(roots, SteamLibraryPathHelper.NormalizeLibraryRoot(libraryPath));
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to resolve Steam library path '{libraryPath}'.", ex);
                }
            }
        }

        return roots;
    }

    private static IEnumerable<string> GetSteamInstallRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return roots;

        foreach (var candidate in BuildSteamRootCandidates(home))
        {
            try
            {
                if (!Directory.Exists(candidate))
                    continue;

                var fullPath = SteamLibraryPathHelper.NormalizeLibraryRoot(candidate);
                if (Directory.Exists(Path.Combine(fullPath, "steamapps")))
                    roots.Add(fullPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to inspect Steam root candidate '{candidate}'.", ex);
            }
        }

        return roots;
    }

    internal static IEnumerable<string> BuildSteamRootCandidates(string homeDirectory)
    {
        yield return Path.Combine(homeDirectory, ".steam", "root");
        yield return Path.Combine(homeDirectory, ".steam", "steam");
        yield return Path.Combine(homeDirectory, ".local", "share", "Steam");
        yield return Path.Combine(homeDirectory, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(homeDirectory, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");

        // Snap-packaged Steam stores libraries under snap/steam/common.
        yield return Path.Combine(homeDirectory, "snap", "steam", "common", ".local", "share", "Steam");
        yield return Path.Combine(homeDirectory, "snap", "steam", "current", ".local", "share", "Steam");

        var snapSteamRoot = Path.Combine(homeDirectory, "snap", "steam");
        if (Directory.Exists(snapSteamRoot))
        {
            foreach (var revisionDirectory in Directory.EnumerateDirectories(snapSteamRoot))
            {
                var revisionName = Path.GetFileName(revisionDirectory);
                if (string.Equals(revisionName, "common", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(revisionName, "current", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return Path.Combine(revisionDirectory, ".local", "share", "Steam");
                yield return Path.Combine(revisionDirectory, "common", ".local", "share", "Steam");
            }
        }
    }

    private static void AddLibraryRoot(ISet<string> roots, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var fullPath = SteamLibraryPathHelper.NormalizeLibraryRoot(path);
            if (Directory.Exists(Path.Combine(fullPath, "steamapps")))
                roots.Add(fullPath);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to add Steam library root '{path}'.", ex);
        }
    }

    private static string UnescapeVdfString(string value)
        => value.Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
}
