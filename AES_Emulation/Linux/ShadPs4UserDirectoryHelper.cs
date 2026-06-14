using System;
using System.IO;

namespace AES_Emulation.Linux;

/// <summary>
/// Resolves shadPS4 user data locations. shadPS4 uses cwd/user when that folder
/// exists, otherwise XDG on Linux (~/.local/share/shadPS4) or portable user/ on Windows.
/// </summary>
public static class ShadPs4UserDirectoryHelper
{
    public const string LinuxUserFolderName = "shadPS4";
    public const string PortableUserFolderName = "user";

    public static string? ResolveLaunchRootFromPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var normalized = launcherPath.Trim();
        try
        {
            if (File.Exists(normalized))
                return Path.GetDirectoryName(Path.GetFullPath(normalized));

            if (Directory.Exists(normalized))
                return Path.GetFullPath(normalized);
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static string? ResolveContentRootDirectory(string? launcherPath, string? fallbackEmulatorDirectory)
    {
        var launchRoot = ResolveLaunchRootFromPath(launcherPath);
        if (!string.IsNullOrWhiteSpace(launchRoot))
            return launchRoot;

        return string.IsNullOrWhiteSpace(fallbackEmulatorDirectory)
            ? null
            : fallbackEmulatorDirectory;
    }

    public static string ResolveUserDirectory(string? launchRootDirectory)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!string.IsNullOrWhiteSpace(launchRootDirectory))
            {
                var portableUserDirectory = Path.Combine(launchRootDirectory, PortableUserFolderName);
                if (Directory.Exists(portableUserDirectory))
                    return portableUserDirectory;
            }

            return ResolveLinuxDefaultUserDirectory();
        }

        if (string.IsNullOrWhiteSpace(launchRootDirectory))
            return string.Empty;

        return Path.Combine(launchRootDirectory, PortableUserFolderName);
    }

    public static string ResolveLinuxDefaultUserDirectory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
            return Path.Combine(dataHome, LinuxUserFolderName);

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return Path.Combine(home, ".local", "share", LinuxUserFolderName);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal),
            ".local",
            "share",
            LinuxUserFolderName);
    }

    public static string GetUserSubdirectory(string? launchRootDirectory, string subdirectory)
    {
        var userDirectory = ResolveUserDirectory(launchRootDirectory);
        return string.IsNullOrWhiteSpace(userDirectory)
            ? string.Empty
            : Path.Combine(userDirectory, subdirectory);
    }

    /// <summary>
    /// Copies missing files from the XDG user tree into portable user/ when shadPS4 will read portable.
    /// Helps recover content saved to the wrong location before launch-root resolution was fixed.
    /// </summary>
    public static void TryMirrorLinuxPortableSubtreeFromXdg(string? launchRootDirectory, string subdirectory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(launchRootDirectory))
            return;

        var portableUserDirectory = Path.Combine(launchRootDirectory, PortableUserFolderName);
        if (!Directory.Exists(portableUserDirectory))
            return;

        var portableSubtree = Path.Combine(portableUserDirectory, subdirectory);
        var xdgSubtree = Path.Combine(ResolveLinuxDefaultUserDirectory(), subdirectory);
        if (!Directory.Exists(xdgSubtree))
            return;

        Directory.CreateDirectory(portableSubtree);
        MirrorMissingDirectoryContents(xdgSubtree, portableSubtree);
    }

    private static void MirrorMissingDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var name = Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var destinationPath = Path.Combine(destinationDirectory, name);
            if (Directory.Exists(sourcePath))
            {
                Directory.CreateDirectory(destinationPath);
                MirrorMissingDirectoryContents(sourcePath, destinationPath);
                continue;
            }

            if (!File.Exists(destinationPath))
                File.Copy(sourcePath, destinationPath);
        }
    }
}
