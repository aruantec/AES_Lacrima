using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public static class FlatpakLaunchHelper
{
    private static readonly HashSet<string> PathArgumentFlags = new(StringComparer.Ordinal)
    {
        "-e",
        "--exec",
        "-u",
        "--user",
        "-m",
        "--movie",
        "-s",
        "--save_state",
        "-l",
        "--load_state",
        "-C",
        "--config",
    };

    public static bool IsFlatpakAvailable() => LinuxFlatpakApplicationService.IsFlatpakAvailable();

    public static void Apply(ProcessStartInfo startInfo, string flatpakAppId, string? contentPath = null)
    {
        var flatpakPath = LinuxFlatpakApplicationService.FindFlatpakExecutable();
        if (flatpakPath == null || string.IsNullOrWhiteSpace(flatpakAppId))
            return;

        var forwardedArgs = new List<string>();
        foreach (var arg in startInfo.ArgumentList)
            forwardedArgs.Add(arg);

        startInfo.ArgumentList.Clear();
        startInfo.FileName = flatpakPath;
        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add("run");

        foreach (var grant in CollectFilesystemGrants(forwardedArgs, contentPath, startInfo.WorkingDirectory))
            startInfo.ArgumentList.Add(grant);

        startInfo.ArgumentList.Add(flatpakAppId);

        // Arguments after the app id are forwarded to the application. Do not insert `--` here:
        // flatpak would pass it through and break option parsing in apps like Dolphin (`-b` becomes a file path).
        foreach (var arg in forwardedArgs)
            startInfo.ArgumentList.Add(arg);
    }

    public static IEnumerable<string> CollectFilesystemGrants(
        IReadOnlyList<string> forwardedArgs,
        string? contentPath,
        string? workingDirectory)
    {
        var grants = new HashSet<string>(StringComparer.Ordinal);
        AddFilesystemGrant(grants, contentPath, readOnly: true);

        for (var i = 0; i < forwardedArgs.Count; i++)
        {
            var arg = forwardedArgs[i];
            if (TryExtractFlagValue(arg, out var flag, out var inlineValue))
            {
                AddFilesystemGrant(grants, inlineValue, readOnly: IsReadOnlyPathFlag(flag));
                continue;
            }

            if (!PathArgumentFlags.Contains(arg))
                continue;

            if (i + 1 >= forwardedArgs.Count)
                continue;

            AddFilesystemGrant(grants, forwardedArgs[++i], readOnly: IsReadOnlyPathFlag(arg));
        }

        AddFilesystemGrant(grants, workingDirectory, readOnly: false);
        return grants;
    }

    private static bool TryExtractFlagValue(string arg, out string flag, out string value)
    {
        foreach (var pathFlag in PathArgumentFlags)
        {
            var prefix = pathFlag + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                flag = pathFlag;
                value = arg[prefix.Length..];
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        flag = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool IsReadOnlyPathFlag(string flag)
        => string.Equals(flag, "-e", StringComparison.Ordinal) ||
           string.Equals(flag, "--exec", StringComparison.Ordinal) ||
           string.Equals(flag, "-m", StringComparison.Ordinal) ||
           string.Equals(flag, "--movie", StringComparison.Ordinal) ||
           string.Equals(flag, "-s", StringComparison.Ordinal) ||
           string.Equals(flag, "--save_state", StringComparison.Ordinal) ||
           string.Equals(flag, "-l", StringComparison.Ordinal) ||
           string.Equals(flag, "--load_state", StringComparison.Ordinal);

    private static void AddFilesystemGrant(HashSet<string> grants, string? path, bool readOnly)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalizedPath = path.Trim();
        if (!Path.IsPathRooted(normalizedPath))
            return;

        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch
        {
            return;
        }

        var grantPath = File.Exists(normalizedPath)
            ? Path.GetDirectoryName(normalizedPath)
            : normalizedPath;

        if (string.IsNullOrWhiteSpace(grantPath))
            return;

        try
        {
            grantPath = Path.GetFullPath(grantPath);
        }
        catch
        {
            return;
        }

        if (!Directory.Exists(grantPath) && !File.Exists(grantPath))
            return;

        grants.Add($"--filesystem={grantPath}{(readOnly ? ":ro" : string.Empty)}");
    }
}
