using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<RetroArchHandler>();

    public static RetroArchHandler Instance { get; } = new();

    private RetroArchHandler()
    {
    }

    public override string HandlerId => "retroarch";

    public override string SectionKey => "ARCADE";

    public override string SectionTitle => "Arcade";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;

    public override int CaptureStartupDelayMs => 0;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        var normalized = NormalizeSectionString(albumTitle);
        return string.Equals(normalized, NormalizeSectionString(SectionTitle), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, NormalizeSectionString(SectionKey), StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("arcade", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("mame", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("finalburn neo", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("fbneo", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("3ds", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("nintendo 3ds", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gamecube", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gcn", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("gc", StringComparison.OrdinalIgnoreCase) ||
               (normalized.Contains("wii", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("wii u", StringComparison.OrdinalIgnoreCase)) ||
               (normalized.Contains("nintendo wii", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("nintendo wii u", StringComparison.OrdinalIgnoreCase)) ||
               normalized.Contains("n64", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("nintendo 64", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("snes", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("super nintendo", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("nes", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("nintendo entertainment system", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("dreamcast", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("playstation 2", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("ps2", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("playstation", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("ps1", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("psx", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        Log.Info($"[RetroArch] Building StartInfo: launcher={launcherPath}, rom={romPath}");
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        
        Log.Info($"[RetroArch] Base StartInfo: FileName={startInfo.FileName}, Args={string.Join(" ", startInfo.ArgumentList)}");
        
        RemoveQtPlatformOverride(startInfo);
        
        if (OperatingSystem.IsLinux())
        {
            // Aggressively force X11 at every level
            startInfo.EnvironmentVariables["SDL_VIDEODRIVER"] = "x11";
            startInfo.EnvironmentVariables["GDK_BACKEND"] = "x11";
            startInfo.EnvironmentVariables["XDG_SESSION_TYPE"] = "x11";
            startInfo.EnvironmentVariables["QT_QPA_PLATFORM"] = "xcb";
            
            // Some AppImages need this to not try to use the integrated display server
            startInfo.EnvironmentVariables["APPIMAGE_EXIT_AFTER_PULL"] = "no";
            
            // Ensure we don't accidentally use a flatpak or bubblewrap that hides windows
            startInfo.EnvironmentVariables["BWRAP_ARGS"] = "--share-net --dev-bind / / --setenv SDL_VIDEODRIVER x11";
        }

        var corePath = FindRetroArchCore(launcherPath, romPath, sectionTitle, selectedRetroArchCore);
        var logFilePath = GetRetroArchLogFilePath(launcherPath);

        if (!string.IsNullOrWhiteSpace(corePath))
        {
            Log.Debug($"RetroArch core selected: {corePath}");
            ConfigureRetroArchLaunchArguments(startInfo, startFullscreen, GetRetroArchFocusOverrideConfigPath(), logFilePath, corePath, romPath);

            Log.Info($"RetroArch launch: launcher={launcherPath}, rom={romPath}, startFullscreen={startFullscreen}, selectedCore={selectedRetroArchCore}, args={string.Join(' ', startInfo.ArgumentList)}");
            return startInfo;
        }

        Log.Warn($"RetroArch core not found for launcher path '{launcherPath}'. Launching RetroArch without a core path to avoid init_libretro_symbols() errors.");

        ConfigureRetroArchLaunchArguments(startInfo, startFullscreen, null, logFilePath, null, null);

        Log.Info($"RetroArch launch (no core): launcher={launcherPath}, rom={romPath}, startFullscreen={startFullscreen}, args={string.Join(' ', startInfo.ArgumentList)}");
        return startInfo;
    }

    private static void RemoveQtPlatformOverride(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove("QT_QPA_PLATFORM");

        if (startInfo.ArgumentList.Count == 0)
            return;

        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.ArgumentList.Clear();

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--env=QT_QPA_PLATFORM=xcb", StringComparison.OrdinalIgnoreCase))
                continue;

            startInfo.ArgumentList.Add(argument);
        }
    }

    private static void ConfigureRetroArchLaunchArguments(
        ProcessStartInfo startInfo,
        bool startFullscreen,
        string? focusConfigPath,
        string? logFilePath,
        string? corePath,
        string? romPath)
    {
        var existingArguments = startInfo.ArgumentList.ToArray();
        if (TryGetFlatpakLaunchPrefixLength(startInfo, existingArguments, out var launchPrefixLength))
        {
            startInfo.ArgumentList.Clear();
            for (var i = 0; i < launchPrefixLength; i++)
                startInfo.ArgumentList.Add(existingArguments[i]);
        }
        else
        {
            startInfo.ArgumentList.Clear();
        }

        if (startFullscreen)
            startInfo.ArgumentList.Add("--fullscreen");

        // Force X11/GL for embedding compatibility
        startInfo.EnvironmentVariables["SDL_VIDEODRIVER"] = "x11";
        startInfo.EnvironmentVariables["GDK_BACKEND"] = "x11";
        startInfo.EnvironmentVariables["XDG_SESSION_TYPE"] = "x11";
        startInfo.EnvironmentVariables["QT_QPA_PLATFORM"] = "xcb";

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

        if (!string.IsNullOrWhiteSpace(corePath))
        {
            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add(corePath);
        }

        if (!string.IsNullOrWhiteSpace(romPath))
            startInfo.ArgumentList.Add(romPath);
    }

    private static bool TryGetFlatpakLaunchPrefixLength(ProcessStartInfo startInfo, IReadOnlyList<string> existingArguments, out int launchPrefixLength)
    {
        launchPrefixLength = 0;

        if (existingArguments.Count == 0)
            return false;

        var executableName = Path.GetFileName(startInfo.FileName);
        var flatpakArgumentIndex = -1;

        if (!IsFlatpakLauncherExecutable(executableName))
        {
            for (var i = 0; i < existingArguments.Count; i++)
            {
                if (IsFlatpakLauncherExecutable(Path.GetFileName(existingArguments[i])))
                {
                    flatpakArgumentIndex = i;
                    break;
                }
            }

            if (flatpakArgumentIndex < 0)
                return false;
        }

        var runIndex = flatpakArgumentIndex < 0 ? 0 : flatpakArgumentIndex + 1;
        if (runIndex >= existingArguments.Count)
            return false;

        var runArgumentIndex = -1;
        for (var i = runIndex; i < existingArguments.Count; i++)
        {
            if (string.Equals(existingArguments[i], "run", StringComparison.OrdinalIgnoreCase))
            {
                runArgumentIndex = i;
                break;
            }
        }

        if (runArgumentIndex < 0)
            return false;

        for (var i = runArgumentIndex + 1; i < existingArguments.Count; i++)
        {
            if (existingArguments[i].StartsWith("-", StringComparison.Ordinal))
                continue;

            launchPrefixLength = i + 1;
            return true;
        }

        return false;
    }

    private static bool IsFlatpakLauncherExecutable(string? executableName)
    {
        return string.Equals(executableName, "flatpak", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(executableName, "flatpak-spawn", StringComparison.OrdinalIgnoreCase);
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
            // Always rewrite to ensure we have the latest X11-forcing settings
            File.WriteAllLines(path, new[]
            {
                "pause_nonactive = \"false\"",
                "input_auto_game_focus = \"0\"",
                "video_fullscreen = \"false\"",
                "video_driver = \"sdl2\"",
                "video_context_driver = \"x11\"",
                "menu_driver = \"xmb\""
            });

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

                if (summary.Contains("Post-processing shader not found", StringComparison.OrdinalIgnoreCase) ||
                    details.Contains("default_pre_post_process.glsl", StringComparison.OrdinalIgnoreCase))
                {
                    summary = "Dolphin core is missing required shader resources.";
                    details += Environment.NewLine +
                        "Ensure RetroArch has the Dolphin system files under system\\dolphin-emu\\Sys\\Shaders\\default_pre_post_process.glsl.";
                }

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

            if (summary.Contains("Post-processing shader not found", StringComparison.OrdinalIgnoreCase) ||
                details.Contains("default_pre_post_process.glsl", StringComparison.OrdinalIgnoreCase))
            {
                summary = "Dolphin core is missing required shader resources.";
                details += Environment.NewLine +
                    "Ensure RetroArch has the Dolphin system files under system\\dolphin-emu\\Sys\\Shaders\\default_pre_post_process.glsl.";
            }

            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> GetRetroArchCores(string? launcherPath)
    {
        var candidateDirectories = GetRetroArchSearchDirectories(launcherPath);

        var extensions = GetSupportedRetroArchCoreExtensions();
        var foundCores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add detailed logging to see where it searches
        Log.Info($"[RetroArch] Searching for cores in {candidateDirectories.Count} locations...");

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            try
            {
                Log.Debug($"[RetroArch] Enumerating directory: {directory}");
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file);
                    if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var fileName = Path.GetFileName(file);
                    // Standard core naming: *_libretro.so
                    if (fileName.Contains("libretro", StringComparison.OrdinalIgnoreCase))
                    {
                        if (foundCores.Add(fileName))
                        {
                            Log.Debug($"[RetroArch] Discovered core: {fileName} in {directory}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[RetroArch] Failed to scan directory {directory}: {ex.Message}");
            }
        }

        return foundCores.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> GetRetroArchSearchDirectories(string? launcherPath)
    {
        var searchDirectories = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !seenDirectories.Add(directory))
                return;

            searchDirectories.Add(directory);
        }

        void AddSearchRoot(string? rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                return;

            AddDirectory(rootDirectory);
            AddDirectory(Path.Combine(rootDirectory, "cores"));
            AddDirectory(Path.Combine(rootDirectory, "libretro"));
            AddDirectory(Path.Combine(rootDirectory, "cores", "32bit"));
            AddDirectory(Path.Combine(rootDirectory, "cores", "64bit"));
        }

        AddSearchRoot(ResolveLauncherWorkingDirectory(launcherPath));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        AddSearchRoot(Path.Combine(appData, "RetroArch"));
        AddSearchRoot(Path.Combine(commonAppData, "RetroArch"));

        if (OperatingSystem.IsLinux())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // ALWAYS add standard user directories first
            AddSearchRoot(Path.Combine(userProfile, ".config", "retroarch"));
            AddSearchRoot(Path.Combine(userProfile, ".config", "libretro"));
            AddSearchRoot(Path.Combine(userProfile, ".local", "share", "retroarch"));
            AddSearchRoot(Path.Combine(userProfile, ".local", "share", "libretro"));

            // If we are using a Flatpak, ALSO add Flatpak directories
            bool isFlatpak = launcherPath?.Contains("flatpak", StringComparison.OrdinalIgnoreCase) == true;
            if (isFlatpak)
            {
                foreach (var flatpakRoot in EnumerateLinuxFlatpakRetroArchRoots(userProfile))
                    AddSearchRoot(flatpakRoot);
            }

            AddSearchRoot("/usr/lib/libretro");
            AddSearchRoot("/usr/lib/retroarch");
            AddSearchRoot("/usr/lib64/libretro");
            AddSearchRoot("/usr/lib64/retroarch");
            AddSearchRoot("/usr/lib/x86_64-linux-gnu/libretro");
            AddSearchRoot("/usr/local/lib/libretro");
            AddSearchRoot("/usr/local/share/libretro");
            AddSearchRoot("/usr/share/libretro");
            AddSearchRoot("/usr/share/retroarch");
            AddSearchRoot("/lib/libretro");
            AddSearchRoot("/lib64/libretro");
        }

        return searchDirectories;
    }

    private static IReadOnlyList<string> EnumerateLinuxFlatpakRetroArchRoots(string? userProfile)
    {
        var roots = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfile))
            return roots;

        var flatpakApplicationsDirectory = Path.Combine(userProfile, ".var", "app");
        if (!Directory.Exists(flatpakApplicationsDirectory))
            return roots;

        try
        {
            foreach (var appDirectory in Directory.EnumerateDirectories(flatpakApplicationsDirectory))
            {
                roots.Add(Path.Combine(appDirectory, "config", "retroarch"));
                roots.Add(Path.Combine(appDirectory, "config", "libretro"));
                roots.Add(Path.Combine(appDirectory, "data", "retroarch"));
                roots.Add(Path.Combine(appDirectory, "data", "libretro"));
            }
        }
        catch
        {
        }

        return roots;
    }

    private static string? FindRetroArchCore(string? launcherPath, string? romPath, string? sectionTitle, string? selectedRetroArchCore)
    {
        var candidateDirectories = GetRetroArchSearchDirectories(launcherPath);

        var platform = GetRetroArchPlatform(sectionTitle, romPath);

        if (!string.IsNullOrWhiteSpace(selectedRetroArchCore))
        {
            var explicitPath = ResolveRetroArchCorePath(candidateDirectories, selectedRetroArchCore);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var explicitCoreName = Path.GetFileName(explicitPath);
                if (!IsRetroArchCoreCompatibleWithPlatform(explicitCoreName, platform))
                {
                    Log.Warn($"Selected RetroArch core '{selectedRetroArchCore}' is incompatible with platform '{platform}' (section='{sectionTitle}', rom='{romPath}'). Falling back to platform defaults.");
                }
                else
                {
                if (IsRetroArchCoreUsable(explicitPath, launcherPath))
                    return explicitPath;

                    var explicitCoreNameNormalized = explicitCoreName?.ToLowerInvariant() ?? string.Empty;
                    if (explicitCoreNameNormalized.Contains("pcsx2"))
                        return explicitPath;
                }
            }
        }

        foreach (var directory in candidateDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var fileName in GetRetroArchCoreFileNames(platform))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate) && IsRetroArchCoreUsable(candidate, launcherPath))
                    return candidate;
            }

            var fallbackCore = FindRetroArchCoreByKeyword(directory, platform);
            if (!string.IsNullOrWhiteSpace(fallbackCore) && IsRetroArchCoreUsable(fallbackCore, launcherPath))
                return fallbackCore;
        }

        Log.Warn($"No RetroArch core found in candidate directories for launcher '{launcherPath}' and section '{sectionTitle}'.");
        return null;
    }

    private static bool IsRetroArchCoreCompatibleWithPlatform(string? coreFileName, RetroArchPlatform platform)
    {
        if (platform == RetroArchPlatform.Unknown)
            return true;

        if (string.IsNullOrWhiteSpace(coreFileName))
            return false;

        var name = coreFileName.ToLowerInvariant();
        return platform switch
        {
            RetroArchPlatform._3DS => name.Contains("citra"),
            RetroArchPlatform.GameCube => name.Contains("dolphin"),
            RetroArchPlatform.Wii => name.Contains("dolphin"),
            RetroArchPlatform.N64 => name.Contains("mupen") || name.Contains("parallel_n64") || name.Contains("angrylion"),
            RetroArchPlatform.SNES => name.Contains("snes") || name.Contains("bsnes") || name.Contains("higan"),
            RetroArchPlatform.NES => name.Contains("nestopia") || name.Contains("fceumm") || name.Contains("quicknes"),
            RetroArchPlatform.Dreamcast => name.Contains("flycast") || name.Contains("nulldc"),
            RetroArchPlatform.PlayStation2 => name.Contains("pcsx2"),
            RetroArchPlatform.PlayStation => name.Contains("beetle_psx") || name.Contains("mednafen_psx") || name.Contains("pcsx_rearmed"),
            _ => name.Contains("fbneo") ||
                 name.Contains("mame") ||
                 name.Contains("fbalpha") ||
                 name.Contains("finalburn") ||
                 name.Contains("neogeo")
        };
    }

    private static string? ResolveRetroArchCorePath(IEnumerable<string> candidateDirectories, string selectedRetroArchCore)
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

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, selectedRetroArchCore, SearchOption.AllDirectories))
                {
                    if (File.Exists(file))
                        return file;
                }
            }
            catch
            {
                // ignore invalid directories
            }
        }

        return null;
    }

    private static bool IsRetroArchCoreUsable(string candidateCorePath, string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(candidateCorePath) || string.IsNullOrWhiteSpace(launcherPath))
            return true;

        var coreName = Path.GetFileName(candidateCorePath)?.ToLowerInvariant() ?? string.Empty;
        var installDir = ResolveLauncherWorkingDirectory(launcherPath);
        if (string.IsNullOrWhiteSpace(installDir))
            return true;

        bool directoryExists(string relativePath)
            => Directory.Exists(Path.Combine(installDir, relativePath));

        bool fileExists(string relativePath)
            => File.Exists(Path.Combine(installDir, relativePath));

        if (coreName.Contains("dolphin"))
        {
            var possibleRoots = new[]
            {
                installDir,
                Path.Combine(installDir, ".."),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RetroArch")
            };

            foreach (var root in possibleRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                var candidatePaths = new[]
                {
                    Path.Combine(root, "system", "dolphin-emu", "Sys", "Shaders", "default_pre_post_process.glsl"),
                    Path.Combine(root, "system", "dolphin-emu", "sys", "Shaders", "default_pre_post_process.glsl"),
                    Path.Combine(root, "dolphin-emu", "Sys", "Shaders", "default_pre_post_process.glsl"),
                    Path.Combine(root, "Sys", "Shaders", "default_pre_post_process.glsl")
                };

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                        return true;
                }
            }

            Log.Warn($"RetroArch Dolphin core found but expected shader path not found under any known RetroArch directories. Allowing core load and deferring failure to RetroArch.");
            return true;
        }

        if (coreName.Contains("pcsx2"))
        {
            return fileExists(Path.Combine("system", "pcsx2", "resources", "GameIndex.yaml")) ||
                   directoryExists(Path.Combine("system", "pcsx2", "resources")) ||
                   directoryExists(Path.Combine("system", "pcsx2"));
        }

        return true;
    }

    private static RetroArchPlatform GetRetroArchPlatform(string? sectionTitle, string? romPath)
    {
        // Section title is the most reliable signal because several platforms share
        // ambiguous extensions (notably .iso/.cue/.chd). Always prefer section first.
        if (IsRetroArch3DSSection(sectionTitle))
            return RetroArchPlatform._3DS;

        if (IsRetroArchSection(sectionTitle, "gamecube", "gcn", "gc"))
            return RetroArchPlatform.GameCube;

        if (IsRetroArchSection(sectionTitle, "wii"))
            return RetroArchPlatform.Wii;

        if (IsRetroArchSection(sectionTitle, "n64", "nintendo 64"))
            return RetroArchPlatform.N64;

        if (IsRetroArchSection(sectionTitle, "snes", "super nintendo"))
            return RetroArchPlatform.SNES;

        if (IsRetroArchSection(sectionTitle, "nes", "nintendo entertainment system"))
            return RetroArchPlatform.NES;

        if (IsRetroArchSection(sectionTitle, "playstation 2", "ps2"))
            return RetroArchPlatform.PlayStation2;

        if (IsRetroArchSection(sectionTitle, "playstation", "ps1", "psx"))
            return RetroArchPlatform.PlayStation;

        // Fallback to ROM-based detection only when section metadata is unavailable.
        if (IsRetroArch3DSRom(romPath))
            return RetroArchPlatform._3DS;

        if (IsRetroArchGameCubeRom(romPath))
            return RetroArchPlatform.GameCube;

        if (IsRetroArchWiiRom(romPath))
            return RetroArchPlatform.Wii;

        if (IsRetroArchN64Rom(romPath))
            return RetroArchPlatform.N64;

        if (IsRetroArchSnesRom(romPath))
            return RetroArchPlatform.SNES;

        if (IsRetroArchNesRom(romPath))
            return RetroArchPlatform.NES;

        // Keep PS2 before Dreamcast in ROM-only fallback due to shared .iso/.chd extensions.
        if (IsRetroArchPlayStation2Rom(romPath))
            return RetroArchPlatform.PlayStation2;

        if (IsRetroArchPlayStationRom(romPath))
            return RetroArchPlatform.PlayStation;

        if (IsRetroArchDreamcastRom(romPath))
            return RetroArchPlatform.Dreamcast;

        return RetroArchPlatform.Unknown;
    }

    private static bool IsRetroArchSection(string? sectionTitle, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return false;

        var normalized = NormalizeSectionString(sectionTitle);
        foreach (var value in values)
        {
            var normalizedValue = NormalizeSectionString(value);
            if (normalized.Contains(normalizedValue, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> GetRetroArchCoreFileNames(RetroArchPlatform platform)
    {
        if (platform == RetroArchPlatform._3DS)
        {
            return GetRetroArch3DSCoreFileNames();
        }

        if (platform == RetroArchPlatform.GameCube || platform == RetroArchPlatform.Wii)
        {
            if (OperatingSystem.IsWindows()) return new[] { "dolphin_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "dolphin_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "dolphin_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.N64)
        {
            if (OperatingSystem.IsWindows()) return new[] { "mupen64plus_next_libretro.dll", "mupen64plus_libretro.dll", "parallel_n64_libretro.dll", "angrylion_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "mupen64plus_next_libretro.so", "mupen64plus_libretro.so", "parallel_n64_libretro.so", "angrylion_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "mupen64plus_next_libretro.dylib", "mupen64plus_libretro.dylib", "parallel_n64_libretro.dylib", "angrylion_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.SNES)
        {
            if (OperatingSystem.IsWindows()) return new[] { "snes9x_libretro.dll", "snes9x_next_libretro.dll", "bsnes_libretro.dll", "higan_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "snes9x_libretro.so", "snes9x_next_libretro.so", "bsnes_libretro.so", "higan_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "snes9x_libretro.dylib", "snes9x_next_libretro.dylib", "bsnes_libretro.dylib", "higan_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.NES)
        {
            if (OperatingSystem.IsWindows()) return new[] { "nestopia_libretro.dll", "fceumm_libretro.dll", "quicknes_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "nestopia_libretro.so", "fceumm_libretro.so", "quicknes_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "nestopia_libretro.dylib", "fceumm_libretro.dylib", "quicknes_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.Dreamcast)
        {
            if (OperatingSystem.IsWindows()) return new[] { "flycast_libretro.dll", "nulldc_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "flycast_libretro.so", "nulldc_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "flycast_libretro.dylib", "nulldc_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.PlayStation2)
        {
            if (OperatingSystem.IsWindows()) return new[] { "pcsx2_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "pcsx2_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "pcsx2_libretro.dylib" };
        }

        if (platform == RetroArchPlatform.PlayStation)
        {
            if (OperatingSystem.IsWindows()) return new[] { "beetle_psx_libretro.dll", "pcsx_rearmed_libretro.dll", "mednafen_psx_libretro.dll" };
            if (OperatingSystem.IsLinux()) return new[] { "beetle_psx_libretro.so", "pcsx_rearmed_libretro.so", "mednafen_psx_libretro.so" };
            if (OperatingSystem.IsMacOS()) return new[] { "beetle_psx_libretro.dylib", "pcsx_rearmed_libretro.dylib", "mednafen_psx_libretro.dylib" };
        }

        return GetRetroArchArcadeCoreFileNames();
    }

    private static bool IsRetroArch3DSSection(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return false;

        var normalized = NormalizeSectionString(sectionTitle);
        return normalized.Contains("3ds", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("nintendo 3ds", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSectionString(string? sectionTitle)
    {
        if (string.IsNullOrWhiteSpace(sectionTitle))
            return string.Empty;

        var chars = sectionTitle
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return new string(chars).Replace("  ", " ").Trim();
    }

    private enum RetroArchPlatform
    {
        Unknown,
        _3DS,
        GameCube,
        N64,
        SNES,
        NES,
        Dreamcast,
        Wii,
        PlayStation,
        PlayStation2
    }

    private static string? FindRetroArchCoreByKeyword(string directory, RetroArchPlatform platform)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*libretro*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file)?.ToLowerInvariant() ?? string.Empty;
                if (platform == RetroArchPlatform._3DS)
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

                if (platform == RetroArchPlatform.GameCube || platform == RetroArchPlatform.Wii)
                {
                    if (fileName.Contains("dolphin"))
                        return file;
                }
                else if (platform == RetroArchPlatform.N64)
                {
                    if (fileName.Contains("mupen") || fileName.Contains("parallel_n64") || fileName.Contains("angrylion"))
                        return file;
                }
                else if (platform == RetroArchPlatform.SNES)
                {
                    if (fileName.Contains("snes") || fileName.Contains("bsnes") || fileName.Contains("higan"))
                        return file;
                }
                else if (platform == RetroArchPlatform.NES)
                {
                    if (fileName.Contains("nestopia") || fileName.Contains("fceumm") || fileName.Contains("quicknes"))
                        return file;
                }
                else if (platform == RetroArchPlatform.Dreamcast)
                {
                    if (fileName.Contains("flycast") || fileName.Contains("nulldc"))
                        return file;
                }
                else if (platform == RetroArchPlatform.PlayStation2)
                {
                    if (fileName.Contains("pcsx2"))
                        return file;
                }
                else if (platform == RetroArchPlatform.PlayStation)
                {
                    if (fileName.Contains("pcsx") || fileName.Contains("beetle_psx") || fileName.Contains("mednafen_psx"))
                        return file;
                }
                else
                {
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

    private static bool IsRetroArchRom(string? romPath, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return false;

        var extension = Path.GetExtension(romPath).ToLowerInvariant();
        if (extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return true;

        if (extension == ".zip")
        {
            try
            {
                using var archive = ZipFile.OpenRead(romPath);
                foreach (var entry in archive.Entries)
                {
                    var entryExt = Path.GetExtension(entry.FullName).ToLowerInvariant();
                    if (extensions.Contains(entryExt, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // ignore invalid archives
            }
        }

        return false;
    }

    private static bool IsRetroArchGameCubeRom(string? romPath)
        => IsRetroArchRom(romPath, ".gcm", ".ciso", ".gcz", ".rvz", ".wia", ".dol", ".elf", ".tgc");

    private static bool IsRetroArchWiiRom(string? romPath)
        => IsRetroArchRom(romPath, ".wbfs", ".wad");

    private static bool IsRetroArchN64Rom(string? romPath)
        => IsRetroArchRom(romPath, ".n64", ".z64", ".v64");

    private static bool IsRetroArchSnesRom(string? romPath)
        => IsRetroArchRom(romPath, ".sfc", ".smc", ".fig", ".swc");

    private static bool IsRetroArchNesRom(string? romPath)
        => IsRetroArchRom(romPath, ".nes", ".fds", ".unf", ".unif");

    private static bool IsRetroArchDreamcastRom(string? romPath)
        => IsRetroArchRom(romPath, ".cdi", ".gdi", ".chd", ".cue", ".iso");

    private static bool IsRetroArchPlayStation2Rom(string? romPath)
        => IsRetroArchRom(romPath, ".iso", ".bin", ".img", ".mdf", ".nrg", ".chd");

    private static bool IsRetroArchPlayStationRom(string? romPath)
        => IsRetroArchRom(romPath, ".cue", ".bin", ".img", ".iso", ".chd", ".pbp", ".m3u");

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

        return Array.Empty<string>();
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

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> GetSupportedRetroArchCoreExtensions()
    {
        if (OperatingSystem.IsWindows())
            return [".dll"];

        if (OperatingSystem.IsLinux())
            return [".so"];

        if (OperatingSystem.IsMacOS())
            return new[] { ".dylib" };

        return Array.Empty<string>();
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        Log.Info($"[RetroArch Linux] Entering ResolveCaptureTargetAsync for process {process?.Id}");

        if (OperatingSystem.IsLinux())
        {
            const int maxAttempts = 120; // 12 seconds total
            const int delayMs = 100;
            IntPtr observedHwnd = IntPtr.Zero;
            int stableCount = 0;

            Log.Info("[RetroArch Linux] Starting aggressive window resolution loop...");

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Force a global process scan for "retroarch"
                IntPtr hwnd = FindRetroArchWindowLinuxAggressive();

                if (hwnd != IntPtr.Zero)
                {
                    Log.Info($"[RetroArch Linux] HWND found: 0x{hwnd.ToInt64():X}. Attempt {attempt}.");
                    if (hwnd == observedHwnd)
                    {
                        stableCount++;
                        if (stableCount >= 2)
                        {
                            var title = GetWindowTitle(hwnd);
                            var className = GetWindowClassName(hwnd);
                            Log.Info($"[RetroArch Linux] Success! Returning HWND 0x{hwnd.ToInt64():X} (Title: '{title}', Class: '{className}')");
                            return hwnd;
                        }
                    }
                    else
                    {
                        var title = GetWindowTitle(hwnd);
                        var className = GetWindowClassName(hwnd);
                        Log.Info($"[RetroArch Linux] Found potential candidate 0x{hwnd.ToInt64():X} (Title: '{title}', Class: '{className}'). Waiting for stability...");
                        observedHwnd = hwnd;
                        stableCount = 1;
                    }
                }
                else
                {
                    if (attempt % 10 == 0) // Log more frequently
                        Log.Debug($"[RetroArch Linux] Still searching... (Attempt {attempt}/{maxAttempts})");
                    
                    observedHwnd = IntPtr.Zero;
                    stableCount = 0;
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            Log.Warn("[RetroArch Linux] TIMEOUT: Failed to find a suitable window handle.");
            return IntPtr.Zero;
        }

        Log.Warn("[RetroArch Linux] Not on Linux, falling back to base.");
        return await base.ResolveCaptureTargetAsync(process, cancellationToken);
    }

    private static IntPtr FindRetroArchWindowLinuxAggressive()
    {
        Log.Info("[RetroArch Linux] Performing global X11 window tree scan...");
        
        var discoveredWindows = new List<IntPtr>();
        
        // 1. Scan for Class/Title globally
        var classes = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByClass("retroarch");
        var libretroClasses = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByClass("libretro");
        var titles = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByTitle("RetroArch");

        discoveredWindows.AddRange(classes);
        discoveredWindows.AddRange(libretroClasses);
        discoveredWindows.AddRange(titles);

        foreach (var hwnd in discoveredWindows.Distinct())
        {
            var isVisible = AES_Emulation.Linux.API.LinuxWindowHelper.IsWindowVisible(hwnd);
            var title = GetWindowTitle(hwnd);
            var className = GetWindowClassName(hwnd);
            
            Log.Info($"[RetroArch Linux] Global Scan Found: 0x{hwnd.ToInt64():X} (Title='{title}', Class='{className}', Visible={isVisible})");

            if (isVisible)
            {
                Log.Info($"[RetroArch Linux] Selecting visible window 0x{hwnd.ToInt64():X}");
                return hwnd;
            }
        }

        // 3. Fallback: Log ALL windows in the tree to find the culprit
        Log.Debug("[RetroArch Linux] No matching window found yet. Listing ALL top-level windows...");
        var display = AES_Emulation.Linux.API.X11Interop.XOpenDisplay(null);
        if (display != IntPtr.Zero)
        {
            try {
                var root = AES_Emulation.Linux.API.X11Interop.XDefaultRootWindow(display);
                if (AES_Emulation.Linux.API.X11Interop.XQueryTree(display, root, out _, out _, out IntPtr children_ptr, out int nchildren) != 0)
                {
                    if (children_ptr != IntPtr.Zero)
                    {
                        var children = new IntPtr[nchildren];
                        Marshal.Copy(children_ptr, children, 0, nchildren);
                        AES_Emulation.Linux.API.X11Interop.XFree(children_ptr);
                        
                        foreach (var child in children)
                        {
                            var t = GetWindowTitle(child);
                            var c = GetWindowClassName(child);
                            if (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(c))
                                Log.Debug($"[RetroArch Linux]   Window 0x{child.ToInt64():X}: Title='{t}', Class='{c}'");
                        }
                    }
                }
            } finally { AES_Emulation.Linux.API.X11Interop.XCloseDisplay(display); }
        }

        // 2. Fallback: Systematic PID search
        var discoveredPids = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName.ToLowerInvariant();
                if (name.Contains("retroarch") || name.Contains("libretro"))
                {
                    Log.Info($"[RetroArch Linux] Found matching process: {p.ProcessName} (PID: {p.Id})");
                    discoveredPids.Add(p.Id);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        // If no named process, check /proc cmdline for hidden/flatpak names
        if (discoveredPids.Count == 0)
        {
            Log.Info("[RetroArch Linux] No direct process name match. Checking /proc/*/cmdline...");
            foreach (var procDir in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out var pid)) continue;
                try
                {
                    var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ').ToLowerInvariant();
                    if (cmdline.Contains("retroarch"))
                    {
                        Log.Info($"[RetroArch Linux] Found matching cmdline for PID {pid}: {cmdline}");
                        discoveredPids.Add(pid);
                    }
                }
                catch { }
            }
        }

        foreach (var pid in discoveredPids)
        {
            var windows = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByPid(pid);
            Log.Info($"[RetroArch Linux] PID {pid} has {windows.Count} windows.");
            
            foreach (var hwnd in windows)
            {
                var title = GetWindowTitle(hwnd);
                var className = GetWindowClassName(hwnd);
                var isVisible = AES_Emulation.Linux.API.LinuxWindowHelper.IsWindowVisible(hwnd);
                
                Log.Info($"[RetroArch Linux] Checking HWND 0x{hwnd.ToInt64():X}: Title='{title}', Class='{className}', Visible={isVisible}");

                // Be extremely permissive: if it's visible or has RetroArch in title/class, take it.
                if (isVisible || 
                    className.Contains("retroarch", StringComparison.OrdinalIgnoreCase) || 
                    title.Contains("RetroArch", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"[RetroArch Linux] HWND 0x{hwnd.ToInt64():X} matches criteria. Selecting it.");
                    return hwnd;
                }
            }
        }

        // Final fallback: Global Title Search
        Log.Info("[RetroArch Linux] No windows found for PIDs. Trying global title/class scan...");
        var allWindows = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByTitle("RetroArch");
        if (allWindows.Count > 0) return allWindows[0];

        return IntPtr.Zero;
    }

    public override void PrepareProcessForCapture(Process process)
    {
        // RetroArch uses Vulkan/DX and can fail to render if its window is hidden before capture.
        // Keep the window available to the capture session and let the window handler move it out of view instead.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Avoid hiding the RetroArch window prior to capture, as this can prevent Vulkan-based cores from producing a capture frame.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyRetroArchRenderWindow,
            fallbackTitleHint: DisplayName);

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
