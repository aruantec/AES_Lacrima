using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using AES_Emulation.EmulationHandlers;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
public static class LinuxFlatpakApplicationService
{
    private static readonly object CacheLock = new();
    private static IReadOnlyList<FlatpakApplicationItem>? _cachedInstalledApplications;
    private static DateTime _cachedAtUtc;

    public static bool IsFlatpakAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        return FindFlatpakExecutable() != null;
    }

    public static IReadOnlyList<FlatpakApplicationItem> GetInstalledApplications(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsLinux())
            return [];

        lock (CacheLock)
        {
            if (!forceRefresh &&
                _cachedInstalledApplications != null &&
                DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(2))
            {
                return _cachedInstalledApplications;
            }
        }

        var apps = Task.Run(() => ListInstalledApplicationsInternal(includeIcons: false)).GetAwaiter().GetResult();
        lock (CacheLock)
        {
            _cachedInstalledApplications = apps;
            _cachedAtUtc = DateTime.UtcNow;
            return _cachedInstalledApplications;
        }
    }

    public static IReadOnlyList<FlatpakApplicationItem> GetApplicationsForHandler(string handlerId, bool forceRefresh = false)
        => EmulatorFlatpakCatalog.BuildSelectionList(handlerId, GetInstalledApplications(forceRefresh));

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedInstalledApplications = null;
        }

        LinuxFlatpakIconService.InvalidateCache();
    }

    public static FlatpakApplicationItem WithIcon(FlatpakApplicationItem item)
    {
        if (item.IsEmpty || item.HasIcon)
            return item;

        return new FlatpakApplicationItem(
            item.ApplicationId,
            item.DisplayName,
            LinuxFlatpakIconService.TryLoadApplicationIcon(item.ApplicationId));
    }

    public static Task PopulateIconsAsync(IList<FlatpakApplicationItem> items)
    {
        if (!OperatingSystem.IsLinux() || items.Count == 0)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsEmpty || item.HasIcon)
                    continue;

                items[i] = WithIcon(item);
            }
        });
    }

    private static IReadOnlyList<FlatpakApplicationItem> ListInstalledApplicationsInternal(bool includeIcons)
    {
        var flatpakPath = FindFlatpakExecutable();
        if (flatpakPath == null)
            return [];

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = flatpakPath,
                Arguments = "list --app --columns=application,name",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return [];

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return [];

            var apps = new List<FlatpakApplicationItem>();
            foreach (var line in output.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
            {
                var tabIndex = line.IndexOf('\t');
                if (tabIndex <= 0)
                    continue;

                var applicationId = line[..tabIndex].Trim();
                var displayName = line[(tabIndex + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(applicationId))
                    continue;

                apps.Add(includeIcons
                    ? new FlatpakApplicationItem(
                        applicationId,
                        displayName,
                        LinuxFlatpakIconService.TryLoadApplicationIcon(applicationId))
                    : new FlatpakApplicationItem(applicationId, displayName));
            }

            return apps
                .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static string? FindFlatpakExecutable()
    {
        foreach (var candidate in new[] { "/usr/bin/flatpak", "/bin/flatpak" })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
            return null;

        foreach (var entry in pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(entry, "flatpak");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
