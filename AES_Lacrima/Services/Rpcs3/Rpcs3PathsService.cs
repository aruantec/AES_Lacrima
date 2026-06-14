using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Resolves the RPCS3 user-data root used for configs, patches, and cheats.
/// AES stores data beside the managed AppImage and redirects RPCS3 via <c>RPCS3_CONFIG_DIR</c>.
/// </summary>
public static class Rpcs3PathsService
{
    private static readonly ILog Log = LogHelper.For(typeof(Rpcs3PathsService));

    public static string GetDefaultEmulatorDirectory() =>
        Rpcs3CustomConfigService.GetDefaultEmulatorDirectory();

    public static string ResolveEmulatorDirectory(string? preferredDirectory, string? launcherPath)
    {
        foreach (var candidate in EnumerateEmulatorDirectoryCandidates(preferredDirectory, launcherPath))
        {
            if (HasEmulatorDirectoryMarkers(candidate))
                return EnsureManagedDirectories(candidate);
        }

        foreach (var candidate in EnumerateEmulatorDirectoryCandidates(preferredDirectory, launcherPath))
            return EnsureManagedDirectories(candidate);

        var managed = GetDefaultEmulatorDirectory();
        return EnsureManagedDirectories(managed);
    }

    public static IEnumerable<string> EnumerateEmulatorDirectoryCandidates(string? preferredDirectory, string? launcherPath)
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
                Log.Debug($"Skipping invalid RPCS3 emulator directory candidate '{path}'.", ex);
            }

            return [];
        }

        foreach (var candidate in Add(preferredDirectory))
            yield return candidate;

        var normalizedLauncher = NormalizeLauncherPath(launcherPath);
        if (!string.IsNullOrWhiteSpace(normalizedLauncher))
        {
            foreach (var launcherCandidate in EnumerateLauncherParentDirectories(normalizedLauncher))
            {
                foreach (var candidate in Add(launcherCandidate))
                    yield return candidate;
            }
        }

        foreach (var candidate in Add(GetDefaultEmulatorDirectory()))
            yield return candidate;

        if (OperatingSystem.IsLinux())
        {
            foreach (var candidate in Add(ResolveLinuxUserConfigDirectory()))
                yield return candidate;
        }
    }

    public static bool HasEmulatorDirectoryMarkers(string emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return false;

        if (Rpcs3PatchesService.PatchFileExists(emulatorDirectory))
            return true;

        if (File.Exists(Path.Combine(emulatorDirectory, "rpcs3_version.txt")))
            return true;

        if (Directory.Exists(Path.Combine(emulatorDirectory, "config")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(emulatorDirectory, "config")).Any())
        {
            return true;
        }

        if (Directory.Exists(Path.Combine(emulatorDirectory, "patches")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(emulatorDirectory, "patches")).Any())
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(emulatorDirectory, "*.AppImage", SearchOption.TopDirectoryOnly)
                .Any(path => Path.GetFileName(path).Contains("rpcs3", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static string EnsureManagedDirectories(string emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return emulatorDirectory;

        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(Path.Combine(emulatorDirectory, "config"));
        Directory.CreateDirectory(Path.Combine(emulatorDirectory, "config", "custom_configs"));
        Directory.CreateDirectory(Path.Combine(emulatorDirectory, "patches"));
        return emulatorDirectory;
    }

    public static string ResolveLinuxUserConfigDirectory()
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return Path.Combine(xdgConfigHome, "rpcs3");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".config", "rpcs3");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", "rpcs3");
    }

    private static IEnumerable<string> EnumerateLauncherParentDirectories(string normalizedLauncher)
    {
        var directories = new List<string>();
        try
        {
            var current = Path.GetDirectoryName(normalizedLauncher);
            while (!string.IsNullOrWhiteSpace(current))
            {
                directories.Add(current);

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to enumerate RPCS3 launcher parent directories for '{normalizedLauncher}'.", ex);
        }

        return directories;
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
}
