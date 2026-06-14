using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AES_Emulation.EmulationHandlers;

namespace AES_Emulation.Linux;

/// <summary>
/// Shared helpers for launching Linux AppImages outside gamescope (setup/GUI mode).
/// </summary>
public static class LinuxAppImageLaunchHelper
{
    public static void ApplyExtractAndRunEnvironment(ProcessStartInfo startInfo, string? launcherPath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var executablePath = EmulatorHandlerBase.ResolveLauncherExecutablePath(launcherPath) ?? startInfo.FileName;
        if (!IsAppImagePath(executablePath))
            return;

        if (!HasLikelyFuseSupport())
            startInfo.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
    }

    public static void PrepareDirectGamescopeLaunch(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var appImagePath = TryUnwrapEnvAppImageLaunch(startInfo);
        if (string.IsNullOrWhiteSpace(appImagePath))
            return;

        startInfo.FileName = appImagePath;
        LinuxAppImageLaunchHelper.ApplyExtractAndRunEnvironment(startInfo, appImagePath);
    }

    private static string? TryUnwrapEnvAppImageLaunch(ProcessStartInfo startInfo)
    {
        if (IsAppImagePath(startInfo.FileName))
            return startInfo.FileName;

        if (!string.Equals(startInfo.FileName, "env", StringComparison.OrdinalIgnoreCase) ||
            startInfo.ArgumentList.Count == 0)
        {
            return null;
        }

        var index = 0;
        while (index < startInfo.ArgumentList.Count && startInfo.ArgumentList[index].Contains('='))
            index++;

        if (index >= startInfo.ArgumentList.Count ||
            !IsAppImagePath(startInfo.ArgumentList[index]))
        {
            return null;
        }

        var appImagePath = startInfo.ArgumentList[index];
        index++;

        if (index < startInfo.ArgumentList.Count &&
            string.Equals(startInfo.ArgumentList[index], "--appimage-extract-and-run", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var remainingArgs = startInfo.ArgumentList.Skip(index).ToList();
        startInfo.ArgumentList.Clear();
        foreach (var arg in remainingArgs)
            startInfo.ArgumentList.Add(arg);

        return appImagePath;
    }

    public static void PrepareDirectExtractAndRunLaunch(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (string.IsNullOrWhiteSpace(startInfo.FileName) ||
            !IsAppImagePath(startInfo.FileName))
        {
            return;
        }

        var appImagePath = startInfo.FileName;
        var originalArgs = startInfo.ArgumentList.ToArray();

        startInfo.FileName = "env";
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("APPIMAGE_EXTRACT_AND_RUN=1");
        startInfo.ArgumentList.Add(appImagePath);
        startInfo.ArgumentList.Add("--appimage-extract-and-run");

        foreach (var arg in originalArgs)
            startInfo.ArgumentList.Add(arg);
    }

    /// <summary>
    /// Wraps an AppImage with env APPIMAGE=... on the command line (needed when APPIMAGE must reach the payload).
    /// </summary>
    public static void WrapWithAppImageEnvironment(ProcessStartInfo startInfo, string appImagePath)
    {
        if (!OperatingSystem.IsLinux() || !IsAppImagePath(appImagePath))
            return;

        UnwrapEnvWrapperIfNeeded(startInfo, ref appImagePath);

        var useExtractAndRun = startInfo.Environment.ContainsKey("APPIMAGE_EXTRACT_AND_RUN");
        startInfo.Environment.Remove("APPIMAGE");
        startInfo.Environment.Remove("APPIMAGE_EXTRACT_AND_RUN");

        var originalArgs = startInfo.ArgumentList.ToArray();
        startInfo.FileName = "env";
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add($"APPIMAGE={appImagePath}");
        if (useExtractAndRun)
            startInfo.ArgumentList.Add("APPIMAGE_EXTRACT_AND_RUN=1");
        startInfo.ArgumentList.Add(appImagePath);

        foreach (var arg in originalArgs)
            startInfo.ArgumentList.Add(arg);
    }

    private static void UnwrapEnvWrapperIfNeeded(ProcessStartInfo startInfo, ref string appImagePath)
    {
        if (!string.Equals(startInfo.FileName, "env", StringComparison.OrdinalIgnoreCase) ||
            startInfo.ArgumentList.Count == 0)
        {
            return;
        }

        var index = 0;
        while (index < startInfo.ArgumentList.Count && startInfo.ArgumentList[index].Contains('='))
            index++;

        if (index >= startInfo.ArgumentList.Count ||
            !IsAppImagePath(startInfo.ArgumentList[index]))
        {
            return;
        }

        appImagePath = startInfo.ArgumentList[index];
        index++;

        if (index < startInfo.ArgumentList.Count &&
            string.Equals(startInfo.ArgumentList[index], "--appimage-extract-and-run", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }

        var remainingArgs = startInfo.ArgumentList.Skip(index).ToList();
        startInfo.ArgumentList.Clear();
        foreach (var arg in remainingArgs)
            startInfo.ArgumentList.Add(arg);
    }

    private static bool IsAppImagePath(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath) &&
        executablePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);

    private static bool HasLikelyFuseSupport()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (!File.Exists("/dev/fuse"))
                return false;

            return IsCommandAvailable("fusermount3") || IsCommandAvailable("fusermount");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, command);
                if (File.Exists(candidate))
                    return true;
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }
}
