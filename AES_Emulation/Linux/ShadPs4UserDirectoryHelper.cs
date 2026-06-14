using System;
using System.IO;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux;

/// <summary>
/// Resolves shadPS4 user data locations. On Linux the emulator defaults to
/// $XDG_DATA_HOME/shadPS4 (~/.local/share/shadPS4). Forcing a portable user/
/// folder beside the AppImage triggers the save/trophy migration dialog.
/// </summary>
[SupportedOSPlatform("linux")]
public static class ShadPs4UserDirectoryHelper
{
    public const string LinuxUserFolderName = "shadPS4";
    public const string PortableUserFolderName = "user";

    public static string ResolveUserDirectory(string? emulatorDirectory)
    {
        if (OperatingSystem.IsLinux())
            return ResolveLinuxDefaultUserDirectory();

        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, PortableUserFolderName);
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

    public static string GetUserSubdirectory(string? emulatorDirectory, string subdirectory)
    {
        var userDirectory = ResolveUserDirectory(emulatorDirectory);
        return string.IsNullOrWhiteSpace(userDirectory)
            ? string.Empty
            : Path.Combine(userDirectory, subdirectory);
    }
}
