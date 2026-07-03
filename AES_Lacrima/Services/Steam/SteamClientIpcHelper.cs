using System;
using System.Diagnostics;
using System.IO;
using AES_Core.Logging;
using log4net;

namespace AES_Lacrima.Services.Steam;

/// <summary>
/// Ensures Proton can reach the running Steam client's IPC pipe when Steam is installed
/// outside the usual ~/.steam layout (Snap/Flatpak).
/// </summary>
internal static class SteamClientIpcHelper
{
    private const string FlatpakSteamAppId = "com.valvesoftware.Steam";

    private static readonly ILog Log = LogHelper.For(typeof(SteamClientIpcHelper));

    public static void EnsureUserSteamDirectory(string libraryRoot)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(libraryRoot))
            return;

        if (!IsSteamClientRunning())
            Log.Warn("Steam client is not running. Some Steam games may fail to launch until Steam is started.");

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var targetDirectory = ResolveSteamDotSteamDirectory(home, libraryRoot);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            Log.Debug($"Steam IPC bootstrap skipped: no .steam directory resolved for '{libraryRoot}'.");
            return;
        }

        var userSteamPath = Path.Combine(home, ".steam");
        if (Path.Exists(userSteamPath))
        {
            if (PathsEquivalent(userSteamPath, targetDirectory) ||
                (IsSymbolicLink(userSteamPath) && PathsEquivalent(ResolveSymbolicLink(userSteamPath), targetDirectory)))
            {
                Log.Debug($"Steam IPC bootstrap OK: '{userSteamPath}' already points to '{targetDirectory}'.");
                return;
            }

            Log.Warn(
                $"Steam IPC bootstrap skipped: '{userSteamPath}' already exists and does not point to '{targetDirectory}'.");
            return;
        }

        try
        {
            Directory.CreateSymbolicLink(userSteamPath, targetDirectory);
            Log.Info($"Linked '{userSteamPath}' -> '{targetDirectory}' for Proton Steam client IPC.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to link '{userSteamPath}' to '{targetDirectory}' for Proton Steam client IPC.", ex);
        }
    }

    public static bool IsSteamClientRunning()
    {
        if (OperatingSystem.IsWindows())
            return SteamWindowsLaunchHelper.IsSteamClientRunning();

        if (!OperatingSystem.IsLinux())
            return false;

        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    if (process.ProcessName.Contains("steam", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed while probing for a running Steam client.", ex);
        }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // ignored
                }
            }
        }

        return false;
    }

    internal static string? ResolveSteamDotSteamDirectory(string homeDirectory, string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || string.IsNullOrWhiteSpace(libraryRoot))
            return null;

        try
        {
            var normalizedLibraryRoot = SteamLibraryPathHelper.NormalizeLibraryRoot(libraryRoot);
            var normalizedHome = Path.GetFullPath(homeDirectory);

            if (normalizedLibraryRoot.Contains($"{Path.DirectorySeparatorChar}snap{Path.DirectorySeparatorChar}steam{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                var snapDotSteam = Path.Combine(normalizedHome, "snap", "steam", "common", ".steam");
                if (Directory.Exists(snapDotSteam))
                    return snapDotSteam;
            }

            var userSteamPath = Path.Combine(normalizedHome, ".steam");
            if (Directory.Exists(userSteamPath))
            {
                var resolvedUserSteam = Directory.ResolveLinkTarget(userSteamPath, returnFinalTarget: true)?.FullName ?? userSteamPath;
                if (Directory.Exists(resolvedUserSteam))
                    return resolvedUserSteam;
            }

            var flatpakDotSteam = Path.Combine(
                normalizedHome,
                ".var",
                "app",
                FlatpakSteamAppId,
                "data",
                ".steam");
            if (normalizedLibraryRoot.Contains($"{Path.DirectorySeparatorChar}.var{Path.DirectorySeparatorChar}app{Path.DirectorySeparatorChar}{FlatpakSteamAppId}{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                Directory.Exists(flatpakDotSteam))
            {
                return flatpakDotSteam;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to resolve Steam .steam directory for '{libraryRoot}'.", ex);
        }

        return null;
    }

    private static bool PathsEquivalent(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: false) != null ||
                   File.ResolveLinkTarget(path, returnFinalTarget: false) != null;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSymbolicLink(string path)
    {
        var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
        return target?.FullName ?? path;
    }
}
