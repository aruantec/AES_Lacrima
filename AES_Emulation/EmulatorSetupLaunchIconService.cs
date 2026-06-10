using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Linux;

namespace AES_Emulation;

/// <summary>
/// Loads setup-launcher icons from emulator executables when possible.
/// </summary>
public static class EmulatorSetupLaunchIconService
{
    public static Bitmap? TryLoadSetupLaunchIcon(string? launcherPath)
    {
        var executablePath = EmulatorHandlerBase.ResolveSimpleLaunchExecutablePath(launcherPath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        if (OperatingSystem.IsWindows())
            return TryLoadWindowsExecutableIcon(executablePath);

        if (OperatingSystem.IsLinux())
            return TryLoadLinuxExecutableIcon(executablePath);

        if (OperatingSystem.IsMacOS())
            return TryLoadMacExecutableIcon(executablePath);

        return null;
    }

    public static Bitmap? TryLoadFlatpakSetupLaunchIcon(string? flatpakAppId)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(flatpakAppId))
            return null;

        return LinuxFlatpakIconService.TryLoadApplicationIcon(flatpakAppId);
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap? TryLoadWindowsExecutableIcon(string executablePath)
    {
#pragma warning disable CA1416
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (icon == null)
                return null;

            using var drawingBitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            drawingBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
#pragma warning restore CA1416
    }

    private static Bitmap? TryLoadLinuxExecutableIcon(string executablePath)
    {
        if (executablePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            var appImageIcon = LinuxAppImageIconExtractor.TryLoadIcon(executablePath);
            if (appImageIcon != null)
                return appImageIcon;
        }

        foreach (var candidate in EnumerateLinuxIconCandidates(executablePath))
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;

                if (new FileInfo(candidate).Length < 128)
                    continue;

                return new Bitmap(candidate);
            }
            catch
            {
                // Try the next candidate.
            }
        }

        var themeIcon = TryLoadLinuxThemeIcon(executablePath);
        if (themeIcon != null)
            return themeIcon;

        return null;
    }

    private static Bitmap? TryLoadLinuxThemeIcon(string executablePath)
    {
        foreach (var iconName in EnumerateLinuxIconNames(executablePath))
        {
            foreach (var candidate in EnumerateThemeIconCandidates(iconName))
            {
                try
                {
                    if (File.Exists(candidate) && new FileInfo(candidate).Length >= 128)
                        return new Bitmap(candidate);
                }
                catch
                {
                    // Try the next candidate.
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLinuxIconNames(string executablePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [];

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                return [trimmed];

            return [];
        }

        var directory = Path.GetDirectoryName(executablePath);
        var baseName = Path.GetFileNameWithoutExtension(executablePath);

        foreach (var name in Add(baseName))
            yield return name;

        if (string.IsNullOrWhiteSpace(directory))
            yield break;

        foreach (var desktopPath in Directory.EnumerateFiles(directory, "*.desktop", SearchOption.TopDirectoryOnly))
        {
            var iconName = TryReadDesktopIconName(desktopPath);
            foreach (var name in Add(iconName))
                yield return name;
        }

        foreach (var desktopPath in EnumerateInstalledDesktopFiles(executablePath))
        {
            var iconName = TryReadDesktopIconName(desktopPath);
            foreach (var name in Add(iconName))
                yield return name;
        }
    }

    private static IEnumerable<string> EnumerateInstalledDesktopFiles(string executablePath)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        var searchRoots = new List<string>();
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            searchRoots.Add(Path.Combine(home, ".local/share/applications"));
            searchRoots.Add(Path.Combine(home, ".local/share/applications/icons"));
        }

        searchRoots.Add("/usr/share/applications");

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> desktopFiles;
            try
            {
                desktopFiles = Directory.EnumerateFiles(root, "*.desktop", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var desktopPath in desktopFiles)
            {
                if (DesktopFileReferencesExecutable(desktopPath, fullExecutablePath))
                    yield return desktopPath;
            }
        }
    }

    private static bool DesktopFileReferencesExecutable(string desktopPath, string executablePath)
    {
        try
        {
            foreach (var line in File.ReadLines(desktopPath))
            {
                if (!line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                    continue;

                return line.Contains(executablePath, StringComparison.OrdinalIgnoreCase) ||
                       line.Contains(Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Ignore unreadable desktop entries.
        }

        return false;
    }

    private static string? TryReadDesktopIconName(string desktopPath)
    {
        try
        {
            foreach (var line in File.ReadLines(desktopPath))
            {
                if (!line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var iconValue = line["Icon=".Length..].Trim();
                return string.IsNullOrWhiteSpace(iconValue) ? null : iconValue;
            }
        }
        catch
        {
            // Ignore desktop parsing failures.
        }

        return null;
    }

    private static IEnumerable<string> EnumerateThemeIconCandidates(string iconName)
    {
        if (iconName.Contains('/', StringComparison.Ordinal) || iconName.Contains('\\', StringComparison.Ordinal))
        {
            if (Path.IsPathRooted(iconName))
                yield return iconName;

            yield break;
        }

        var sizes = new[] { 512, 256, 128, 64, 48, 32 };
        var roots = new List<string>();
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            roots.Add(Path.Combine(home, ".local/share/icons/hicolor"));

        roots.Add("/usr/share/icons/hicolor");
        roots.Add("/usr/share/pixmaps");

        foreach (var root in roots)
        {
            foreach (var size in sizes)
            {
                yield return Path.Combine(root, $"{size}x{size}", "apps", $"{iconName}.png");
                yield return Path.Combine(root, $"{size}x{size}", "apps", $"{iconName}.svg");
            }

            yield return Path.Combine(root, "scalable", "apps", $"{iconName}.svg");
            yield return Path.Combine(root, "scalable", "apps", $"{iconName}.png");
        }

        yield return Path.Combine("/usr/share/pixmaps", $"{iconName}.png");
        yield return Path.Combine("/usr/share/pixmaps", $"{iconName}.svg");
    }

    private static Bitmap? TryLoadMacExecutableIcon(string executablePath)
    {
        try
        {
            if (!executablePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return null;

            var resourcesDirectory = Path.Combine(executablePath, "Contents", "Resources");
            if (!Directory.Exists(resourcesDirectory))
                return null;

            var icnsPath = Directory.EnumerateFiles(resourcesDirectory, "*.icns", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(icnsPath))
                return null;

            return new Bitmap(icnsPath);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateLinuxIconCandidates(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
            yield break;

        var fileName = Path.GetFileName(executablePath);
        var baseName = Path.GetFileNameWithoutExtension(executablePath);

        yield return Path.Combine(directory, ".DirIcon");
        yield return Path.Combine(directory, $"{baseName}.png");
        yield return Path.Combine(directory, $"{baseName}.svg");

        if (executablePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(directory, $"{fileName}.dir", $"{baseName}.png");
            yield return Path.Combine(directory, $"{fileName}.dir", ".DirIcon");

            var desktopIcon = TryResolveIconFromDesktopFile(directory, baseName);
            if (!string.IsNullOrWhiteSpace(desktopIcon))
                yield return desktopIcon;
        }
    }

    private static string? TryResolveIconFromDesktopFile(string directory, string baseName)
    {
        try
        {
            var desktopPath = Directory.EnumerateFiles(directory, "*.desktop", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Contains(baseName, StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(directory, "*.desktop", SearchOption.TopDirectoryOnly).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(desktopPath) || !File.Exists(desktopPath))
                return null;

            foreach (var line in File.ReadLines(desktopPath))
            {
                if (!line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var iconValue = line["Icon=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(iconValue))
                    return null;

                if (Path.IsPathRooted(iconValue) && File.Exists(iconValue))
                    return iconValue;

                var besideExecutable = Path.Combine(directory, iconValue);
                if (File.Exists(besideExecutable))
                    return besideExecutable;

                var pngBesideExecutable = Path.Combine(directory, $"{iconValue}.png");
                if (File.Exists(pngBesideExecutable))
                    return pngBesideExecutable;
            }
        }
        catch
        {
            // Ignore desktop parsing failures.
        }

        return null;
    }
}
