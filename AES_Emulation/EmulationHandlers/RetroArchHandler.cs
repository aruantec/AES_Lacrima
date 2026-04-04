using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(RetroArchHandler));

    public static RetroArchHandler Instance { get; } = new();

    private RetroArchHandler()
    {
    }

    public override string HandlerId => "retroarch";

    public override string SectionKey => "ARCADE";

    public override string SectionTitle => "Arcade";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Arcade Machine", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Arcade Machines", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "MAME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FinalBurn Neo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FBNeo", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen);
        var arcadeCorePath = FindRetroArchArcadeCore(launcherPath);
        var logFilePath = GetRetroArchLogFilePath(launcherPath);

        if (!string.IsNullOrWhiteSpace(arcadeCorePath))
        {
            Log.Debug($"RetroArch arcade core selected: {arcadeCorePath}");
            startInfo.ArgumentList.Clear();

            if (startFullscreen)
                startInfo.ArgumentList.Add("--fullscreen");

            var focusConfigPath = GetRetroArchFocusOverrideConfigPath();
            if (!string.IsNullOrWhiteSpace(focusConfigPath))
            {
                startInfo.ArgumentList.Add("--appendconfig");
                startInfo.ArgumentList.Add(focusConfigPath);
            }

            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                startInfo.ArgumentList.Add("--verbose");
                startInfo.ArgumentList.Add("--log-file");
                startInfo.ArgumentList.Add(logFilePath);
            }

            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add(arcadeCorePath);
            startInfo.ArgumentList.Add(romPath);
            return startInfo;
        }

        Log.Warn($"RetroArch arcade core not found for launcher path '{launcherPath}'. Falling back to raw ROM launch.");
        if (startFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            startInfo.ArgumentList.Add("--verbose");
            startInfo.ArgumentList.Add("--log-file");
            startInfo.ArgumentList.Add(logFilePath);
        }

        return startInfo;
    }

    public static string? GetRetroArchLogFilePath(string? launcherPath)
    {
        try
        {
            var directory = string.IsNullOrWhiteSpace(launcherPath)
                ? Path.GetTempPath()
                : Path.GetDirectoryName(launcherPath) ?? Path.GetTempPath();

            var logFilePath = Path.Combine(directory, "retroarch-launch.log");
            return logFilePath;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetRetroArchFocusOverrideConfigPath()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "aes-lacrima-retroarch-focus.cfg");
            if (!File.Exists(path))
            {
                File.WriteAllLines(path, new[]
                {
                    "pause_nonactive = \"false\"",
                    "input_auto_game_focus = \"0\""
                });
            }

            return path;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryGetRetroArchErrorDetails(string? launcherPath, out string summary, out string details)
    {
        summary = string.Empty;
        details = string.Empty;

        var logFilePath = GetRetroArchLogFilePath(launcherPath);
        if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
            return false;

        try
        {
            var lines = File.ReadAllLines(logFilePath);
            var errorLines = new List<string>();

            foreach (var line in lines)
            {
                if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("libretro ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Required files are missing", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Failed to load", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Cannot initialize", StringComparison.OrdinalIgnoreCase))
                {
                    errorLines.Add(line.Trim());
                }
            }

            if (errorLines.Count > 0)
            {
                details = string.Join(Environment.NewLine, errorLines);
                summary = errorLines.Last();
                return true;
            }

            var tail = lines.Skip(Math.Max(0, lines.Length - 80)).Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l));
            details = string.Join(Environment.NewLine, tail);
            summary = details.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(details);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryExtractRetroArchErrorDetails(string[] lines, out string summary, out string details)
    {
        summary = string.Empty;
        details = string.Empty;

        var errorLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("libretro ERROR", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Required files are missing", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to load", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Cannot initialize", StringComparison.OrdinalIgnoreCase))
            {
                errorLines.Add(line.Trim());
            }
        }

        if (errorLines.Count > 0)
        {
            details = string.Join(Environment.NewLine, errorLines);
            summary = errorLines.Last();
            return true;
        }

        return false;
    }

    private static string? FindRetroArchArcadeCore(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var baseDir = Path.GetDirectoryName(launcherPath);
        if (string.IsNullOrWhiteSpace(baseDir))
            return null;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var candidateDirectories = new[]
        {
            baseDir,
            Path.Combine(baseDir, "cores"),
            Path.Combine(baseDir, "libretro"),
            Path.Combine(baseDir, "cores", "32bit"),
            Path.Combine(baseDir, "cores", "64bit"),
            Path.Combine(baseDir, "..", "cores"),
            Path.Combine(appData, "RetroArch", "cores"),
            Path.Combine(commonAppData, "RetroArch", "cores")
        };

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var fileName in GetRetroArchArcadeCoreFileNames())
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }

            var fallbackCore = FindRetroArchCoreByKeyword(directory);
            if (!string.IsNullOrWhiteSpace(fallbackCore))
                return fallbackCore;
        }

        Log.Warn($"No arcade-specific RetroArch core found in candidate directories for launcher '{launcherPath}'.");
        return null;
    }

    private static string? FindRetroArchCoreByKeyword(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*libretro*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file)?.ToLowerInvariant() ?? string.Empty;
                if (fileName.Contains("fbneo") ||
                    fileName.Contains("mame") ||
                    fileName.Contains("fbalpha") ||
                    fileName.Contains("neogeo") ||
                    fileName.Contains("finalburn"))
                {
                    if (OperatingSystem.IsWindows() && fileName.EndsWith(".dll"))
                        return file;
                    if (OperatingSystem.IsLinux() && fileName.EndsWith(".so"))
                        return file;
                    if (OperatingSystem.IsMacOS() && fileName.EndsWith(".dylib"))
                        return file;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed keyword search for RetroArch cores in '{directory}': {ex.Message}", ex);
        }

        return null;
    }

    private static IEnumerable<string> GetRetroArchArcadeCoreFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                "fbneo_libretro.dll",
                "mame2003_plus_libretro.dll",
                "mame2003_libretro.dll",
                "mame_libretro.dll",
                "fbalpha_libretro.dll",
                "neogeo_libretro.dll"
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return new[]
            {
                "fbneo_libretro.so",
                "mame2003_plus_libretro.so",
                "mame_libretro.so",
                "fbalpha_libretro.so",
                "neogeo_libretro.so"
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return new[]
            {
                "fbneo_libretro.dylib",
                "mame2003_plus_libretro.dylib",
                "mame_libretro.dylib",
                "fbalpha_libretro.dylib",
                "neogeo_libretro.dylib"
            };
        }

        return [];
    }

    public override void PrepareProcessForCapture(Process process) => HideProcessWindowsForCapture(process);

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyRetroArchRenderWindow);

    private static bool IsLikelyRetroArchRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;
        var normalizedTitle = title;

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            var lowerTitle = normalizedTitle.ToLowerInvariant();

            if (lowerTitle.Contains("retroarch") &&
                !lowerTitle.Contains("menu") &&
                !lowerTitle.Contains("settings") &&
                !lowerTitle.Contains("audio") &&
                !lowerTitle.Contains("video") &&
                !lowerTitle.Contains("input") &&
                !lowerTitle.Contains("controller") &&
                !lowerTitle.Contains("cheat") &&
                !lowerTitle.Contains("shader") &&
                !lowerTitle.Contains("playlist") &&
                !lowerTitle.Contains("load content") &&
                !lowerTitle.Contains("options") &&
                !lowerTitle.Contains("config"))
            {
                looksLikePrimaryUi = false;
            }

            if (lowerTitle.Contains("menu") ||
                lowerTitle.Contains("settings") ||
                lowerTitle.Contains("audio") ||
                lowerTitle.Contains("video") ||
                lowerTitle.Contains("input") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("cheat") ||
                lowerTitle.Contains("shader") ||
                lowerTitle.Contains("playlist") ||
                lowerTitle.Contains("load content") ||
                lowerTitle.Contains("options") ||
                lowerTitle.Contains("config"))
            {
                looksLikePrimaryUi = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.Contains("sdl") ||
                lowerClass.Contains("glfw") ||
                lowerClass.Contains("retroarch"))
            {
                looksLikePrimaryUi |= hasCaption && hasThickFrame;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(normalizedTitle));
    }
}
