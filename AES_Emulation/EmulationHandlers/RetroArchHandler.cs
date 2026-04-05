using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
               string.Equals(albumTitle, "FBNeo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "3DS", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo 3DS", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        var corePath = FindRetroArchCore(launcherPath, sectionTitle, selectedRetroArchCore);
        var logFilePath = GetRetroArchLogFilePath(launcherPath);

        if (!string.IsNullOrWhiteSpace(corePath))
        {
            Log.Debug($"RetroArch core selected: {corePath}");
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
            startInfo.ArgumentList.Add(corePath);
            startInfo.ArgumentList.Add(romPath);
            return startInfo;
        }

        Log.Warn($"RetroArch core not found for launcher path '{launcherPath}'. Falling back to raw ROM launch.");
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
            // Always use a writable temp location for RetroArch logs.
            // Launcher directory may be protected on Windows (Program Files), which can cause first-run failures.
            var directory = Path.GetTempPath();
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

    public static IReadOnlyList<string> GetRetroArchCores(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return Array.Empty<string>();

        var baseDir = Path.GetDirectoryName(launcherPath);
        if (string.IsNullOrWhiteSpace(baseDir))
            return Array.Empty<string>();

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

        var extensions = GetSupportedRetroArchCoreExtensions();
        var foundCores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*libretro*", SearchOption.TopDirectoryOnly))
                {
                    if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;

                    var fileName = Path.GetFileName(file);
                    if (!string.IsNullOrWhiteSpace(fileName))
                        foundCores.Add(fileName);
                }
            }
            catch
            {
                // ignore invalid directories
            }
        }

        return foundCores.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindRetroArchCore(string? launcherPath, string? sectionTitle, string? selectedRetroArchCore)
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

        if (!string.IsNullOrWhiteSpace(selectedRetroArchCore))
        {
            var explicitPath = ResolveRetroArchCorePath(candidateDirectories, selectedRetroArchCore);
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return explicitPath;
        }

        var is3Ds = IsRetroArch3DSSection(sectionTitle);

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            if (is3Ds)
            {
                foreach (var fileName in GetRetroArch3DSCoreFileNames())
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }

                var fallback3DsCore = FindRetroArchCoreByKeyword(directory, is3Ds);
                if (!string.IsNullOrWhiteSpace(fallback3DsCore))
                    return fallback3DsCore;

                continue;
            }

            foreach (var fileName in GetRetroArchArcadeCoreFileNames())
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }

            var fallbackCore = FindRetroArchCoreByKeyword(directory, is3Ds);
            if (!string.IsNullOrWhiteSpace(fallbackCore))
                return fallbackCore;
        }

        Log.Warn($"No RetroArch core found in candidate directories for launcher '{launcherPath}' and section '{sectionTitle}'.");
        return null;
    }

    private static string? ResolveRetroArchCorePath(string[] candidateDirectories, string selectedRetroArchCore)
    {
        if (Path.IsPathRooted(selectedRetroArchCore))
        {
            return File.Exists(selectedRetroArchCore) ? selectedRetroArchCore : null;
        }

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            var candidate = Path.Combine(directory, selectedRetroArchCore);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsRetroArch3DSSection(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return false;

        return sectionTitle.IndexOf("3ds", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? FindRetroArchCoreByKeyword(string directory, bool is3Ds)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*libretro*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file)?.ToLowerInvariant() ?? string.Empty;
                if (is3Ds)
                {
                    if (fileName.Contains("citra"))
                    {
                        if (OperatingSystem.IsWindows() && fileName.EndsWith(".dll"))
                            return file;
                        if (OperatingSystem.IsLinux() && fileName.EndsWith(".so"))
                            return file;
                        if (OperatingSystem.IsMacOS() && fileName.EndsWith(".dylib"))
                            return file;
                    }

                    continue;
                }

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

    private static bool IsRetroArch3DSRom(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return false;

        var extension = Path.GetExtension(romPath).ToLowerInvariant();
        if (extension == ".3ds" ||
            extension == ".3dsx" ||
            extension == ".cci" ||
            extension == ".cxi" ||
            extension == ".cia")
        {
            return true;
        }

        if (extension == ".zip")
        {
            try
            {
                using var archive = ZipFile.OpenRead(romPath);
                foreach (var entry in archive.Entries)
                {
                    var entryExt = Path.GetExtension(entry.FullName).ToLowerInvariant();
                    if (entryExt == ".3ds" ||
                        entryExt == ".3dsx" ||
                        entryExt == ".cci" ||
                        entryExt == ".cxi" ||
                        entryExt == ".cia")
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore invalid archives
            }
        }

        return false;
    }

    private static IEnumerable<string> GetRetroArch3DSCoreFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return new[]
            {
                "citra_libretro.dll"
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return new[]
            {
                "citra_libretro.so"
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return new[]
            {
                "citra_libretro.dylib"
            };
        }

        return [];
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

    private static IReadOnlyList<string> GetSupportedRetroArchCoreExtensions()
    {
        if (OperatingSystem.IsWindows())
            return [".dll"];

        if (OperatingSystem.IsLinux())
            return [".so"];

        if (OperatingSystem.IsMacOS())
            return [".dylib"];

        return [];
    }

    public override void PrepareProcessForCapture(Process process) => HideProcessWindowsForCapture(process);

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        var preferredHandle = FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyRetroArchRenderWindow);
        if (preferredHandle != IntPtr.Zero)
            return preferredHandle;

        return FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: true, isPreferredRenderWindow: null);
    }

    private static bool IsLikelyRetroArchRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).ToLowerInvariant();
        var style = GetWindowStyle(hwnd);

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();
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
                return false;
            }

            if (lowerTitle.Contains("retroarch") || lowerTitle.Contains("citra") || lowerTitle.Contains("3ds"))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.Contains("retroarch") || lowerClass.Contains("sdl") || lowerClass.Contains("glfw") ||
                lowerClass.Contains("azahar") || lowerClass.Contains("citra"))
                return true;
        }

        if (hwnd == mainWindowHandle)
            return true;

        return false;
    }
}
