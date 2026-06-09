using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// PCSX2 path helpers on Linux. Game launches prefer portable data beside the AppImage when
/// it exists; otherwise they use XDG (~/.config/PCSX2). Setup always uses portable beside AppImage.
/// </summary>
public static class Pcsx2PathsService
{
    private static readonly ILog Log = LogHelper.For(typeof(Pcsx2PathsService));

    public const string SettingsFolderName = "inis";
    public const string SettingsFileName = "PCSX2.ini";
    private const string DataSubfolderName = "PCSX2";

    public static string GetDefaultEmulatorDirectory() =>
        Path.Combine(ApplicationPaths.EmulatorsDirectory, "PS2", "PCSX2");

    public static string ResolveDataPath(string? preferredDirectory, string? launcherPath)
    {
        foreach (var candidate in EnumerateDataPathCandidates(preferredDirectory, launcherPath))
        {
            if (HasDataPathMarkers(candidate))
                return candidate;

            var modernRoot = GetModernDataRoot(candidate);
            if (HasDataPathMarkers(modernRoot))
                return modernRoot;
        }

        return ResolveLinuxUserConfigDirectory();
    }

    public static string GetSettingsFilePath(string dataPath) =>
        Path.Combine(dataPath, SettingsFolderName, SettingsFileName);

    /// <summary>
    /// Portable PCSX2 setup launch beside the AppImage (setup launcher only).
    /// </summary>
    public static void PrepareLinuxPortableSetupLaunch(ProcessStartInfo startInfo, string? launcherPath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        EnsureArgument(startInfo, "-portable");
        ApplyLinuxPortableAppImageLaunch(startInfo, launcherPath, "setup");
    }

    /// <summary>
    /// When portable PCSX2 data exists beside the AppImage, game launches must use the same
    /// -portable + APPIMAGE path as the setup launcher. Returns true when the env wrapper was applied.
    /// </summary>
    public static bool TryApplyLinuxPortableGameLaunch(ProcessStartInfo startInfo, string? launcherPath)
    {
        if (!OperatingSystem.IsLinux() || !HasPortableConfigBesideAppImage(launcherPath))
            return false;

        EnsureArgument(startInfo, "-portable");
        ApplyLinuxPortableAppImageLaunch(startInfo, launcherPath, "game");
        return true;
    }

    public static bool HasPortableConfigBesideAppImage(string? launcherPath)
    {
        var appImagePath = NormalizeLauncherPath(launcherPath);
        if (string.IsNullOrWhiteSpace(appImagePath) ||
            !appImagePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var appImageDirectory = Path.GetDirectoryName(appImagePath);
        if (string.IsNullOrWhiteSpace(appImageDirectory))
            return false;

        EnsureModernAppImageLayout(appImageDirectory);
        return HasDataPathMarkers(GetModernDataRoot(appImageDirectory)) ||
               HasDataPathMarkers(appImageDirectory);
    }

    private static void ApplyLinuxPortableAppImageLaunch(
        ProcessStartInfo startInfo,
        string? launcherPath,
        string launchKind)
    {
        var appImagePath = NormalizeLauncherPath(launcherPath) ?? startInfo.FileName;
        if (string.IsNullOrWhiteSpace(appImagePath) ||
            !appImagePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            LinuxAppImageLaunchHelper.ApplyExtractAndRunEnvironment(startInfo, launcherPath);
            LinuxAppImageLaunchHelper.PrepareDirectExtractAndRunLaunch(startInfo);
            return;
        }

        var appImageDirectory = Path.GetDirectoryName(appImagePath);
        if (string.IsNullOrWhiteSpace(appImageDirectory))
            return;

        EnsureModernAppImageLayout(appImageDirectory);
        LinuxAppImageLaunchHelper.ApplyExtractAndRunEnvironment(startInfo, launcherPath);
        LinuxAppImageLaunchHelper.WrapWithAppImageEnvironment(startInfo, appImagePath);

        Log.Info($"PCSX2 Linux {launchKind} launch will use -portable with APPIMAGE='{appImagePath}'.");
    }

    public static bool HasDataPathMarkers(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
            return false;

        if (File.Exists(GetSettingsFilePath(dataPath)))
            return true;

        var inisDirectory = Path.Combine(dataPath, SettingsFolderName);
        if (Directory.Exists(inisDirectory) &&
            Directory.EnumerateFileSystemEntries(inisDirectory).Any())
        {
            return true;
        }

        foreach (var folder in new[] { "bios", "memcards", "sstates", "cache", "covers", "textures" })
        {
            var path = Path.Combine(dataPath, folder);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                return true;
        }

        return File.Exists(Path.Combine(dataPath, "portable.txt"));
    }

    public static IEnumerable<string> EnumerateDataPathCandidates(string? preferredDirectory, string? launcherPath)
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
                Log.Debug($"Skipping invalid PCSX2 data path candidate '{path}'.", ex);
            }

            return [];
        }

        if (!OperatingSystem.IsLinux())
        {
            foreach (var candidate in Add(preferredDirectory))
                yield return candidate;
            yield break;
        }

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

        foreach (var candidate in Add(ResolveLinuxUserConfigDirectory()))
            yield return candidate;
    }

    private static string GetModernDataRoot(string baseDirectory) =>
        Path.Combine(baseDirectory, DataSubfolderName);

    private static void EnsureModernAppImageLayout(string appImageDirectory)
    {
        var modernRoot = GetModernDataRoot(appImageDirectory);
        if (HasDataPathMarkers(modernRoot))
            return;

        var legacyMarkers = new[] { SettingsFolderName, "bios", "memcards", "sstates", "cache", "covers", "textures" };
        var hasLegacyLayout = legacyMarkers.Any(folder =>
        {
            var path = Path.Combine(appImageDirectory, folder);
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        });

        if (!hasLegacyLayout)
            return;

        try
        {
            Directory.CreateDirectory(modernRoot);

            foreach (var folder in legacyMarkers)
            {
                var legacyPath = Path.Combine(appImageDirectory, folder);
                var modernPath = Path.Combine(modernRoot, folder);
                if (!Directory.Exists(legacyPath) ||
                    !Directory.EnumerateFileSystemEntries(legacyPath).Any() ||
                    Directory.Exists(modernPath))
                {
                    continue;
                }

                Directory.CreateSymbolicLink(modernPath, legacyPath);
                Log.Info($"Linked legacy PCSX2 folder '{legacyPath}' to '{modernPath}'.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to link legacy PCSX2 folders under '{appImageDirectory}'.", ex);
        }
    }

    public static string ResolveLinuxUserConfigDirectory()
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            foreach (var name in new[] { "PCSX2", "pcsx2" })
            {
                var candidate = Path.Combine(xdgConfigHome, name);
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(xdgConfigHome, "PCSX2");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            foreach (var name in new[] { "PCSX2", "pcsx2" })
            {
                var candidate = Path.Combine(home, ".config", name);
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(home, ".config", "PCSX2");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", "PCSX2");
    }

    private static string? NormalizeLauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        try
        {
            var executable = EmulatorHandlerBase.ResolveLauncherExecutablePath(launcherPath) ?? launcherPath;
            return Path.GetFullPath(executable.Trim());
        }
        catch
        {
            return launcherPath.Trim();
        }
    }

    private static void EnsureArgument(ProcessStartInfo startInfo, string argument)
    {
        foreach (var existing in startInfo.ArgumentList)
        {
            if (string.Equals(existing, argument, StringComparison.OrdinalIgnoreCase))
                return;
        }

        startInfo.ArgumentList.Insert(0, argument);
    }
}
