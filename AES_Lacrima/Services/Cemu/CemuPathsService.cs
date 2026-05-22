using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AES_Core.IO;
using AES_Emulation.EmulationHandlers;

namespace AES_Lacrima.Services.Cemu;

public static class CemuPathsService
{
    public const string GraphicPacksFolderName = "graphicPacks";
    public const string DownloadedGraphicPacksFolderName = "downloadedGraphicPacks";
    public const string DownloadedVersionFileName = "version.txt";

    public static string GetDefaultEmulatorDirectory() =>
        Path.Combine(ApplicationPaths.EmulatorsDirectory, CemuHandler.Instance.SectionKey, "Cemu");

    public static string ResolveEmulatorDirectory(string? preferredDirectory, string? launcherPath)
    {
        var candidates = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var trimmed = path.Trim();
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                candidates.Add(trimmed);
        }

        Add(preferredDirectory);
        Add(GetDefaultEmulatorDirectory());

        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            try
            {
                var launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherPath.Trim()));
                if (!string.IsNullOrWhiteSpace(launcherDirectory))
                    Add(launcherDirectory);
            }
            catch
            {
            }
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "settings.xml")) ||
                Directory.Exists(Path.Combine(candidate, "portable")))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(preferredDirectory))
            return preferredDirectory.Trim();

        var managed = GetDefaultEmulatorDirectory();
        Directory.CreateDirectory(managed);
        return managed;
    }

    public static bool TryResolveUserDataDirectory(string? emulatorDirectory, string? launcherPath, out string userDataDirectory)
    {
        userDataDirectory = string.Empty;

        var resolvedLauncher = !string.IsNullOrWhiteSpace(launcherPath)
            ? CemuHandler.Instance.NormalizeLauncherPath(launcherPath)
            : Path.Combine(ResolveEmulatorDirectory(emulatorDirectory, launcherPath), "Cemu.exe");

        if (string.IsNullOrWhiteSpace(resolvedLauncher) || !File.Exists(resolvedLauncher))
        {
            var emuDir = ResolveEmulatorDirectory(emulatorDirectory, launcherPath);
            if (Directory.Exists(emuDir))
            {
                userDataDirectory = emuDir;
                return true;
            }

            return false;
        }

        var executableDirectory = Path.GetDirectoryName(resolvedLauncher);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return false;

        var portableDirectory = Path.Combine(executableDirectory, "portable");
        if (Directory.Exists(portableDirectory) || File.Exists(Path.Combine(portableDirectory, "settings.xml")))
        {
            userDataDirectory = portableDirectory;
            return true;
        }

        if (File.Exists(Path.Combine(executableDirectory, "settings.xml")))
        {
            userDataDirectory = executableDirectory;
            return true;
        }

        userDataDirectory = executableDirectory;
        return true;
    }

    public static bool TryResolveSettingsPath(string? emulatorDirectory, string? launcherPath, out string settingsPath)
    {
        settingsPath = string.Empty;
        if (!TryResolveUserDataDirectory(emulatorDirectory, launcherPath, out var userDataDirectory))
            return false;

        settingsPath = Path.Combine(userDataDirectory, "settings.xml");
        return true;
    }

    public static string GetGraphicPacksRoot(string userDataDirectory) =>
        Path.Combine(userDataDirectory, GraphicPacksFolderName);

    public static string GetDownloadedGraphicPacksDirectory(string userDataDirectory) =>
        Path.Combine(GetGraphicPacksRoot(userDataDirectory), DownloadedGraphicPacksFolderName);

    public static string GetDownloadedVersionPath(string userDataDirectory) =>
        Path.Combine(GetDownloadedGraphicPacksDirectory(userDataDirectory), DownloadedVersionFileName);

    public static string MakeRelativeRulesPath(string userDataDirectory, string rulesFilePath)
    {
        var userRoot = Path.GetFullPath(userDataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rulesFullPath = Path.GetFullPath(rulesFilePath);
        var relative = Path.GetRelativePath(userRoot, rulesFullPath);
        return relative.Replace('\\', '/');
    }
}
