using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services.Xenia;

/// <summary>
/// Resolves Xenia Canary storage paths to match xenia-canary layout:
/// storage_root/patches, storage_root/xenia-canary.config.toml, storage_root/custom_configs.
/// On Linux, portable mode defaults on and AppImage runs extract to a temp mount, so the
/// external AppImage directory must be passed via --storage_root at launch.
/// </summary>
public static class XeniaPathsService
{
    private static readonly ILog Log = LogHelper.For(typeof(XeniaPathsService));

    public const string PatchesFolderName = "patches";
    public const string CustomConfigsFolderName = "custom_configs";
    public const string PortableMarkerFileName = "portable.txt";

    public static string GetDefaultEmulatorDirectory() =>
        Path.Combine(ApplicationPaths.EmulatorsDirectory, XeniaHandler.Instance.SectionKey, "Xenia");

    /// <summary>
    /// Primary storage root used for config preparation and --storage_root on Linux.
    /// </summary>
    public static string ResolveStorageRoot(string? preferredDirectory, string? launcherPath)
    {
        foreach (var candidate in EnumerateStorageRootCandidates(preferredDirectory, launcherPath))
        {
            if (HasStorageRootMarkers(candidate))
                return candidate;
        }

        foreach (var candidate in EnumerateStorageRootCandidates(preferredDirectory, launcherPath))
            return candidate;

        var managed = GetDefaultEmulatorDirectory();
        Directory.CreateDirectory(managed);
        return managed;
    }

    /// <summary>
    /// Directory where patch downloads should be written (same as xenia-canary storage_root/patches).
    /// </summary>
    public static string ResolvePatchesDirectory(string? preferredDirectory, string? launcherPath) =>
        GetPatchesDirectory(ResolveStorageRoot(preferredDirectory, launcherPath));

    public static string GetPatchesDirectory(string storageRoot) =>
        Path.Combine(storageRoot, PatchesFolderName);

    public static IEnumerable<string> EnumeratePatchesDirectories(string? preferredDirectory, string? launcherPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var storageRoot in EnumerateStorageRootCandidates(preferredDirectory, launcherPath))
        {
            var patchesDirectory = GetPatchesDirectory(storageRoot);
            if (seen.Add(patchesDirectory))
                yield return patchesDirectory;
        }
    }

    /// <summary>
    /// Ensures xenia-canary reads patches/config from the resolved folder when running as an AppImage.
    /// </summary>
    public static void ApplyLinuxStorageRootLaunchArguments(ProcessStartInfo startInfo, string? preferredDirectory, string? launcherPath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var storageRoot = ResolveStorageRoot(preferredDirectory, launcherPath);
        if (string.IsNullOrWhiteSpace(storageRoot))
            return;

        Directory.CreateDirectory(storageRoot);
        Directory.CreateDirectory(GetPatchesDirectory(storageRoot));
        Directory.CreateDirectory(Path.Combine(storageRoot, CustomConfigsFolderName));

        XeniaCustomConfigService.EnsureGamescopeLaunchSettings(storageRoot);

        startInfo.ArgumentList.Add($"--storage_root={storageRoot}");

        var activeConfigPath = XeniaCustomConfigService.GetActiveConfigPath(storageRoot);
        if (File.Exists(activeConfigPath))
            startInfo.ArgumentList.Add($"--config={activeConfigPath}");

        Log.Info($"Xenia Linux launch will use storage_root '{storageRoot}'.");
    }

    public static bool HasStorageRootMarkers(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            return false;

        if (Directory.Exists(GetPatchesDirectory(storageRoot)) &&
            Directory.EnumerateFileSystemEntries(GetPatchesDirectory(storageRoot)).Any())
        {
            return true;
        }

        if (File.Exists(Path.Combine(storageRoot, XeniaCustomConfigService.ActiveConfigFileName)) ||
            File.Exists(Path.Combine(storageRoot, XeniaCustomConfigService.DefaultTemplateFileName)))
        {
            return true;
        }

        var customConfigs = Path.Combine(storageRoot, CustomConfigsFolderName);
        if (Directory.Exists(customConfigs) && Directory.EnumerateFileSystemEntries(customConfigs).Any())
            return true;

        if (File.Exists(Path.Combine(storageRoot, PortableMarkerFileName)))
            return true;

        return Directory.Exists(Path.Combine(storageRoot, "content")) ||
               Directory.Exists(Path.Combine(storageRoot, "cache"));
    }

    public static IEnumerable<string> EnumerateStorageRootCandidates(string? preferredDirectory, string? launcherPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return [];

            try
            {
                var fullPath = Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (seen.Add(fullPath))
                    return [fullPath];
            }
            catch (Exception ex)
            {
                Log.Debug($"Skipping invalid Xenia storage root candidate '{path}'.", ex);
            }

            return [];
        }

        if (OperatingSystem.IsLinux())
        {
            var normalizedLauncher = NormalizeLauncherPath(launcherPath);
            if (!string.IsNullOrWhiteSpace(normalizedLauncher) &&
                normalizedLauncher.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                var appImageDirectory = Path.GetDirectoryName(normalizedLauncher);
                if (!string.IsNullOrWhiteSpace(appImageDirectory))
                {
                    foreach (var candidate in Add(appImageDirectory))
                        yield return candidate;
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedLauncher))
            {
                var launcherDirectory = Path.GetDirectoryName(normalizedLauncher);
                if (!string.IsNullOrWhiteSpace(launcherDirectory))
                {
                    foreach (var candidate in Add(launcherDirectory))
                        yield return candidate;
                }
            }

            foreach (var candidate in Add(preferredDirectory))
                yield return candidate;
            foreach (var candidate in Add(GetDefaultEmulatorDirectory()))
                yield return candidate;
            foreach (var candidate in Add(ResolveLinuxUserStorageRoot()))
                yield return candidate;
            yield break;
        }

        foreach (var candidate in Add(preferredDirectory))
            yield return candidate;
        foreach (var candidate in Add(GetDefaultEmulatorDirectory()))
            yield return candidate;

        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            var launcherDirectory = Path.GetDirectoryName(NormalizeLauncherPath(launcherPath) ?? launcherPath.Trim());
            if (!string.IsNullOrWhiteSpace(launcherDirectory))
            {
                foreach (var candidate in Add(launcherDirectory))
                    yield return candidate;
            }
        }
    }

    private static string? NormalizeLauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        try
        {
            return Path.GetFullPath(launcherPath.Trim());
        }
        catch
        {
            return launcherPath.Trim();
        }
    }

    /// <summary>
    /// Matches xenia-canary non-portable fallback: ~/.local/share/Xenia
    /// </summary>
    private static string ResolveLinuxUserStorageRoot()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
            return Path.Combine(dataHome, "Xenia");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "share", "Xenia");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share", "Xenia");
    }
}
