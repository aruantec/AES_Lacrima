using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        var corePath = FindRetroArchCore(launcherPath, romPath, sectionTitle, selectedRetroArchCore);
        var logFilePath = GetRetroArchLogFilePath(launcherPath);

        if (!string.IsNullOrWhiteSpace(corePath))
        {
            Log.Debug($"RetroArch core selected: {corePath}");
            startInfo.ArgumentList.Clear();

            if (startFullscreen)
            {
                startInfo.ArgumentList.Add("--fullscreen");
            }

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

            Log.Info($"RetroArch launch: launcher={launcherPath}, rom={romPath}, startFullscreen={startFullscreen}, selectedCore={selectedRetroArchCore}, args={string.Join(' ', startInfo.ArgumentList)}");
            return startInfo;
        }

        Log.Warn($"RetroArch core not found for launcher path '{launcherPath}'. Launching RetroArch without a core path to avoid init_libretro_symbols() errors.");
        startInfo.ArgumentList.Clear();

        if (startFullscreen)
            startInfo.ArgumentList.Add("--fullscreen");

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            startInfo.ArgumentList.Add("--verbose");
            startInfo.ArgumentList.Add("--log-file");
            startInfo.ArgumentList.Add(logFilePath);
        }

        Log.Info($"RetroArch launch (no core): launcher={launcherPath}, rom={romPath}, startFullscreen={startFullscreen}, args={string.Join(' ', startInfo.ArgumentList)}");
        // Avoid passing the ROM directly when no core is available, as RetroArch will fail with
        // "Frontend is built for dynamic libretro cores, but path is not set" when launched this way.
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
                    "input_auto_game_focus = \"0\"",
                    "video_fullscreen = \"false\""
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
                foreach (var file in Directory.EnumerateFiles(directory, "*libretro*", SearchOption.AllDirectories))
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

    private static string? FindRetroArchCore(string? launcherPath, string? romPath, string? sectionTitle, string? selectedRetroArchCore)
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
        var installDir = Path.GetDirectoryName(launcherPath);
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
