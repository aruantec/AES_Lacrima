using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Extracts the icon embedded in Linux AppImages (same assets used for desktop integration).
/// </summary>
public static class LinuxAppImageIconExtractor
{
    private static readonly ILog Log = LogHelper.For(typeof(LinuxAppImageIconExtractor));

    private static readonly ConcurrentDictionary<string, string?> IconPathCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string IconCacheRoot =
        Path.Combine(Path.GetTempPath(), "aes-appimage-icons");

    private static readonly string[] EmbeddedIconPatterns =
    [
        "usr/share/icons/hicolor/512x512/apps/*.png",
        "usr/share/icons/hicolor/256x256/apps/*.png",
        "usr/share/icons/hicolor/128x128/apps/*.png",
        "usr/share/icons/hicolor/64x64/apps/*.png",
        "usr/bin/resources/icons/AppIconLarge.png",
        "PCSX2.png",
        ".DirIcon",
    ];

    public static Bitmap? TryLoadIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !executablePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            var cacheKey = BuildCacheKey(executablePath);
            if (!IconPathCache.TryGetValue(cacheKey, out var iconPath))
            {
                iconPath = ResolveIconPath(executablePath, cacheKey);
                IconPathCache[cacheKey] = iconPath;
            }

            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
                return null;

            return new Bitmap(iconPath);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to load AppImage icon from '{executablePath}'.", ex);
            return null;
        }
    }

    private static string BuildCacheKey(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var mtime = File.GetLastWriteTimeUtc(fullPath).Ticks;
        return $"{fullPath}|{mtime}";
    }

    private static string? ResolveIconPath(string appImagePath, string cacheKey)
    {
        var besideIcon = TryResolveBesideAppImageIconPath(appImagePath);
        if (!string.IsNullOrWhiteSpace(besideIcon))
            return besideIcon;

        var extractTool = ResolveExtractTool();
        if (extractTool == null)
            return null;

        var cacheDirectory = Path.Combine(IconCacheRoot, HashCacheKey(cacheKey));
        Directory.CreateDirectory(cacheDirectory);
        var cachedIconPath = Path.Combine(cacheDirectory, "icon.png");
        if (File.Exists(cachedIconPath) && new FileInfo(cachedIconPath).Length >= 128)
            return cachedIconPath;

        var tempDirectory = Path.Combine(cacheDirectory, "extract");
        if (Directory.Exists(tempDirectory))
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to reset AppImage icon extract directory '{tempDirectory}'.", ex);
            }
        }

        Directory.CreateDirectory(tempDirectory);

        try
        {
            foreach (var pattern in EmbeddedIconPatterns)
            {
                var extractedPath = TryExtractFirstMatch(extractTool.Value, appImagePath, tempDirectory, pattern);
                if (string.IsNullOrWhiteSpace(extractedPath))
                    continue;

                try
                {
                    var fileInfo = new FileInfo(extractedPath);
                    if (fileInfo.Length < 128)
                        continue;

                    File.Copy(extractedPath, cachedIconPath, overwrite: true);
                    return cachedIconPath;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Failed to cache extracted AppImage icon '{extractedPath}'.", ex);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete temporary AppImage icon directory '{tempDirectory}'.", ex);
            }
        }

        return null;
    }

    private static string HashCacheKey(string cacheKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        return Convert.ToHexString(hash);
    }

    private static string? TryResolveBesideAppImageIconPath(string appImagePath)
    {
        var directory = Path.GetDirectoryName(appImagePath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, ".DirIcon"),
                     Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(appImagePath)}.png"),
                 })
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;

                if (new FileInfo(candidate).Length >= 128)
                    return candidate;
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static string? TryExtractFirstMatch(
        ExtractTool extractTool,
        string appImagePath,
        string outputDirectory,
        string archivePathPattern)
    {
        try
        {
            if (extractTool == ExtractTool.SevenZip)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "7z",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = outputDirectory,
                };
                startInfo.ArgumentList.Add("e");
                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add($"-o{outputDirectory}");
                startInfo.ArgumentList.Add(appImagePath);
                startInfo.ArgumentList.Add(archivePathPattern);

                using var process = Process.Start(startInfo);
                if (process == null)
                    return null;

                process.WaitForExit(10_000);
                if (process.ExitCode != 0)
                    return null;
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "bsdtar",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = outputDirectory,
                };
                startInfo.ArgumentList.Add("-xf");
                startInfo.ArgumentList.Add(appImagePath);
                startInfo.ArgumentList.Add("-C");
                startInfo.ArgumentList.Add(outputDirectory);
                startInfo.ArgumentList.Add(archivePathPattern);

                using var process = Process.Start(startInfo);
                if (process == null)
                    return null;

                process.WaitForExit(10_000);
                if (process.ExitCode != 0)
                    return null;
            }

            return Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to extract '{archivePathPattern}' from '{appImagePath}'.", ex);
            return null;
        }
    }

    private static ExtractTool? ResolveExtractTool()
    {
        if (IsCommandAvailable("7z"))
            return ExtractTool.SevenZip;

        if (IsCommandAvailable("bsdtar"))
            return ExtractTool.BsdTar;

        return null;
    }

    private static bool IsCommandAvailable(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(entry, command)))
                    return true;
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }

    private enum ExtractTool
    {
        SevenZip,
        BsdTar,
    }
}
