using System;
using System.Collections.Generic;
using System.IO;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Locates Eden / Yuzu / Ryujinx style prod.keys and title.keys directories.
/// </summary>
internal static class SwitchKeysHelper
{
    public static string? ResolveEdenKeysDirectory()
    {
        foreach (var candidate in EnumerateKeyDirectoryCandidates())
        {
            if (HasRequiredKeys(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateKeyDirectoryCandidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "eden", "keys");
            yield return Path.Combine(appData, "yuzu", "keys");
            yield return Path.Combine(appData, "Ryujinx", "system");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "share", "eden", "keys");
            yield return Path.Combine(userProfile, ".local", "share", "yuzu", "keys");
        }
    }

    private static bool HasRequiredKeys(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        return File.Exists(Path.Combine(directory, "prod.keys")) ||
               File.Exists(Path.Combine(directory, "title.keys"));
    }
}
