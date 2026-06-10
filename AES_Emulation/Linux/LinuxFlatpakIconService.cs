using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public static class LinuxFlatpakIconService
{
    private static readonly ConcurrentDictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? TryLoadApplicationIcon(string? applicationId)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(applicationId))
            return null;

        return IconCache.GetOrAdd(applicationId, LoadApplicationIconInternal);
    }

    public static void InvalidateCache()
    {
        IconCache.Clear();
    }

    private static Bitmap? LoadApplicationIconInternal(string applicationId)
    {
        foreach (var iconPath in EnumerateIconPaths(applicationId))
        {
            try
            {
                if (!File.Exists(iconPath))
                    continue;

                if (new FileInfo(iconPath).Length < 128)
                    continue;

                return new Bitmap(iconPath);
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateIconPaths(string applicationId)
    {
        var iconName = TryReadFlatpakIconName(applicationId) ?? applicationId;

        foreach (var exportRoot in EnumerateFlatpakExportRoots(applicationId))
        {
            var iconsRoot = Path.Combine(exportRoot, "share", "icons", "hicolor");
            if (Directory.Exists(iconsRoot))
            {
                foreach (var size in new[] { 128, 64, 48, 256, 512, 32 })
                {
                    yield return Path.Combine(iconsRoot, $"{size}x{size}", "apps", $"{iconName}.png");
                    yield return Path.Combine(iconsRoot, $"{size}x{size}", "apps", $"{iconName}.svg");
                }

                yield return Path.Combine(iconsRoot, "scalable", "apps", $"{iconName}.svg");
                yield return Path.Combine(iconsRoot, "scalable", "apps", $"{iconName}.png");
            }

            var flatExportIcon = Path.Combine(exportRoot, "share", "icons", $"{iconName}.png");
            yield return flatExportIcon;
        }
    }

    private static IEnumerable<string> EnumerateFlatpakExportRoots(string applicationId)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var userExport = Path.Combine(home, ".local", "share", "flatpak", "app", applicationId, "current", "active", "export");
            if (Directory.Exists(userExport))
                yield return userExport;
        }

        var systemExport = Path.Combine("/var/lib/flatpak/app", applicationId, "current", "active", "export");
        if (Directory.Exists(systemExport))
            yield return systemExport;
    }

    private static string? TryReadFlatpakIconName(string applicationId)
    {
        var flatpakPath = LinuxFlatpakApplicationService.FindFlatpakExecutable();
        if (flatpakPath == null)
            return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = flatpakPath,
                Arguments = $"info --show-metadata {applicationId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            foreach (var line in output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var iconName = line["Icon=".Length..].Trim();
                return string.IsNullOrWhiteSpace(iconName) ? null : iconName;
            }
        }
        catch
        {
            // Ignore metadata lookup failures.
        }

        return null;
    }
}
