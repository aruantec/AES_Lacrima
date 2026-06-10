using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Windows.API;

using log4net;
using AES_Core.Logging;
namespace AES_Emulation.EmulationHandlers;

public sealed class DolphinHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<DolphinHandler>();
    private static readonly string[] DolphinExecutableNames =
    [
        "Dolphin.exe",
        "dolphin.exe",
        "DolphinQt2.exe",
        "dolphinqt2.exe",
        "DolphinWx.exe",
        "dolphinwx.exe",
        "Dolphin-emu.exe",
        "dolphin-emu.exe"
    ];

    public static DolphinHandler Instance { get; } = new();

    private DolphinHandler()
    {
    }

    public override string HandlerId => "dolphin";

    public override string SectionKey => "GC";

    public override string SectionTitle => "GameCube";

    public override string DisplayName => "Dolphin";

    public override bool HideUntilCaptured => true;

    public override bool IsLauncherPathValid(string? launcherPath)
        => !string.IsNullOrWhiteSpace(ResolveDolphinLauncherPath(launcherPath));

    public override string? NormalizeLauncherPath(string? launcherPath)
        => ResolveDolphinLauncherPath(launcherPath) ?? base.NormalizeLauncherPath(launcherPath);

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo GameCube", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "GCN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Wii", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Wii", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        var usingFlatpak = !string.IsNullOrWhiteSpace(FlatpakAppId);
        var dolphinUserDirectory = usingFlatpak
            ? null
            : ResolvePortableUserDirectory(startInfo.FileName, startInfo.WorkingDirectory);
        ApplyDolphinLaunchConfigOverrides(dolphinUserDirectory);

        // Dolphin CLI: -b batch, -e executable/content path, -f fullscreen.
        if (!string.IsNullOrWhiteSpace(dolphinUserDirectory))
        {
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add(dolphinUserDirectory);
        }

        startInfo.ArgumentList.Add("-b");
        if (startFullscreen)
            startInfo.ArgumentList.Add("-f");

        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public override ProcessStartInfo BuildSetupStartInfo(string? launcherPath, string? preferredEmulatorDirectory = null)
    {
        var startInfo = base.BuildSetupStartInfo(launcherPath, preferredEmulatorDirectory);
        if (!string.IsNullOrWhiteSpace(FlatpakAppId))
            return startInfo;

        var dolphinUserDirectory = ResolvePortableUserDirectory(startInfo.FileName, startInfo.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(dolphinUserDirectory))
        {
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add(dolphinUserDirectory);
        }

        return startInfo;
    }

    public override int CaptureStartupDelayMs => 1800;

    public override TimeSpan LaunchTopmostRestoreTimeout => TimeSpan.FromSeconds(60);

    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for Dolphin.
        // Minimizing/hiding before capture is established can stall Vulkan presentation
        // which results in audio-only playback and no captured frames.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for Dolphin; see PrepareProcessForCapture.
    }

    public override void PrepareWindowForCaptureAttach(IntPtr hwnd)
    {
        // Geometry is applied once the render window is fully constructed during resolve.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyDolphinRenderWindow);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 400;
        const int delayMs = 50;
        const int stableAttemptsBeforeAssign = 8;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        var lastStableWidth = 0;
        var lastStableHeight = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                mainWindowHandle = process.MainWindowHandle;
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
            {
                KeepWindowHiddenDuringResolve(hwnd);
                HideNonTargetDolphinWindows(process, hwnd);
            }

            if (hwnd != IntPtr.Zero &&
                IsStableCaptureCandidate(hwnd, mainWindowHandle) &&
                Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            {
                var dimensionsStable = width == lastStableWidth && height == lastStableHeight;
                if (hwnd == observedHwnd && dimensionsStable)
                    observedStableAttempts++;
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                    lastStableWidth = width;
                    lastStableHeight = height;
                }

                if (observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    ApplyCaptureGeometryOnce(hwnd);
                    KeepWindowHiddenDuringResolve(hwnd);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    return hwnd;
                }
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
                lastStableWidth = 0;
                lastStableHeight = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var fallback = FindPreferredWindowHandle(process);
        if (fallback == IntPtr.Zero)
        {
            fallback = FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: true, isPreferredRenderWindow: null);
        }

        if (fallback != IntPtr.Zero)
        {
            ApplyCaptureGeometryOnce(fallback);
            KeepWindowHiddenDuringResolve(fallback);
            HideNonTargetDolphinWindows(process, fallback);
        }

        return fallback;
    }

    private void ApplyCaptureGeometryOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.TryExitFullscreenWindow(hwnd);

            if (Win32API.HasWindowCaption(hwnd))
                Win32API.RemoveWindowDecorations(hwnd);

            var aspect = CaptureWindowAspectRatio;
            if (aspect is > 0)
                Win32API.ResizeWindowToAspectRatioInPlace(hwnd, aspect.Value);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static void KeepWindowHiddenDuringResolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.SetWindowCloaked(hwnd, cloaked: true);
            Win32API.MoveAway(hwnd, useCloak: true);
            Win32API.EnsureRenderActiveForCapture(hwnd, bringOnScreen: false);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyDolphinRenderWindow(hwnd, mainWindowHandle))
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.HasWindowCaption(hwnd))
            return false;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return false;

        return width >= 640 && height >= 360;
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyDolphinRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyDolphinRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).ToLowerInvariant();
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        var lowerTitle = string.IsNullOrWhiteSpace(title) ? string.Empty : title.ToLowerInvariant();

        // Exclude helper/system windows that can appear in Dolphin's process tree.
        if (lowerTitle.Contains("diemwin") ||
            lowerTitle.Contains("temp window") ||
            lowerTitle.Contains("msctfime ui") ||
            lowerTitle.Contains("default ime") ||
            className.StartsWith("temp_d3d_window", StringComparison.Ordinal) ||
            className.Contains("diemwin") ||
            className.Contains("ime") ||
            className.Contains("screenchangeobserver") ||
            className.Contains("themechangeobserver"))
        {
            return false;
        }

        var looksLikeGameTitle = lowerTitle.Contains("dolphin") && lowerTitle.Contains(" | ");
        if (looksLikeGameTitle)
            return true;

        var isClearlyUiTitle =
            lowerTitle.Contains("configuration") ||
            lowerTitle.Contains("graphics") ||
            lowerTitle.Contains("audio") ||
            lowerTitle.Contains("controller") ||
            lowerTitle.Contains("hotkey") ||
            lowerTitle.Contains("tools") ||
            lowerTitle.Contains("about") ||
            lowerTitle.Contains("fifo") ||
            lowerTitle.Contains("shader") ||
            lowerTitle.Contains("memory card") ||
            lowerTitle.Contains("netplay");

        if (lowerTitle.StartsWith("dolphin ", StringComparison.Ordinal) && !lowerTitle.Contains(" | "))
            return false;

        if (lowerTitle.Contains("dolphin") && !isClearlyUiTitle)
            return true;

        if (!isClearlyUiTitle && lowerTitle.Length >= 3)
        {
            if (className.Contains("qt") || className.Contains("wx") || className.Contains("dolphin"))
                return true;
        }

        if (string.IsNullOrWhiteSpace(lowerTitle))
        {
            if ((className.Contains("qt") || className.Contains("wx") || className.Contains("dolphin")) && !looksLikePrimaryUi)
                return true;
        }

        if (className.Contains("dolphin") && hasCaption && hasThickFrame)
            return !looksLikePrimaryUi;

        return false;
    }

    private static void HideNonTargetDolphinWindows(Process process, IntPtr targetHwnd)
    {
        foreach (var hwnd in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
        {
            if (hwnd == IntPtr.Zero || hwnd == targetHwnd)
                continue;

            if (!ShouldHideDolphinWindow(hwnd))
                continue;

            try
            {
                HideWindowForCapture(hwnd);
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }
        }
    }

    private static bool ShouldHideDolphinWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();

            if (lowerTitle.Contains("default ime") ||
                lowerTitle.Contains("msctfime ui") ||
                lowerTitle.Contains("temp window") ||
                lowerTitle.Contains("diemwin"))
            {
                return false;
            }

            if (lowerTitle.StartsWith("dolphin ", StringComparison.Ordinal) && !lowerTitle.Contains(" | "))
                return true;
        }

        if (className.Contains("ime") ||
            className.StartsWith("temp_d3d_window", StringComparison.Ordinal) ||
            className.Contains("screenchangeobserver") ||
            className.Contains("themechangeobserver") ||
            className.Contains("diemwin"))
        {
            return false;
        }

        return false;
    }

    private static string? ResolveDolphinLauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var normalizedPath = launcherPath.Trim();
        try
        {
            normalizedPath = System.IO.Path.GetFullPath(normalizedPath);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        if (System.IO.File.Exists(normalizedPath))
            return normalizedPath;

        if (!System.IO.Directory.Exists(normalizedPath))
            return null;

        foreach (var executableName in DolphinExecutableNames)
        {
            var candidate = System.IO.Path.Combine(normalizedPath, executableName);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }

        try
        {
            var launcherCandidate = System.IO.Directory.EnumerateFiles(normalizedPath, "*", System.IO.SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                    return string.Equals(fileName, "dolphin", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Contains("dolphin", StringComparison.OrdinalIgnoreCase);
                });

            if (launcherCandidate != null)
                return launcherCandidate;

            var files = System.IO.Directory.EnumerateFiles(normalizedPath, "*", System.IO.SearchOption.AllDirectories).ToArray();
            if (files.Length == 1)
                return files[0];
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static string? ResolvePortableUserDirectory(string? launcherExecutablePath, string? workingDirectory)
    {
        var executableDirectory = string.IsNullOrWhiteSpace(launcherExecutablePath)
            ? null
            : Path.GetDirectoryName(launcherExecutablePath);

        if (!string.IsNullOrWhiteSpace(executableDirectory))
            return Path.Combine(executableDirectory, "User");

        return string.IsNullOrWhiteSpace(workingDirectory) ? null : Path.Combine(workingDirectory, "User");
    }

    /// <summary>
    /// Applies launch-friendly Dolphin.ini overrides for both GameCube and Wii (same profile).
    /// </summary>
    private static void ApplyDolphinLaunchConfigOverrides(string? portableUserDirectory)
    {
        foreach (var userDirectory in EnumerateDolphinUserDirectories(portableUserDirectory))
        {
            TrySetDolphinIniValue(userDirectory, "PauseOnFocusLost", "False");
            // Single NKit dialog for all platforms; Wii is mentioned in the dialog text but uses this same flag.
            TrySetDolphinIniValue(userDirectory, "SkipNKitWarning", "True");
            TrySetDolphinCoreIniValue(userDirectory, "EnableCheats", "True");
        }
    }

    private static IEnumerable<string> EnumerateDolphinUserDirectories(string? portableUserDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(portableUserDirectory) && seen.Add(portableUserDirectory))
            yield return portableUserDirectory;

        foreach (var defaultUserDirectory in GetDefaultDolphinUserDirectories())
        {
            if (!string.IsNullOrWhiteSpace(defaultUserDirectory) && seen.Add(defaultUserDirectory))
                yield return defaultUserDirectory;
        }
    }

    private static IEnumerable<string> GetDefaultDolphinUserDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents))
                yield return Path.Combine(documents, "Dolphin Emulator");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!string.IsNullOrWhiteSpace(home))
            {
                yield return Path.Combine(home, ".local", "share", "dolphin-emu");
                yield return Path.Combine(home, ".dolphin-emu");
            }
        }
        else if (OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, "Library", "Application Support", "Dolphin");
        }
    }

    private static void TrySetDolphinCoreIniValue(string? userDirectory, string key, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userDirectory))
                return;

            var configDirectory = Path.Combine(userDirectory, "Config");
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "Dolphin.ini");

            List<string> lines;
            if (File.Exists(configPath))
            {
                lines = [.. File.ReadAllLines(configPath)];
            }
            else
            {
                lines = ["[Core]"];
            }

            var coreHeaderIndex = lines.FindIndex(line =>
                string.Equals(line.Trim(), "[Core]", StringComparison.OrdinalIgnoreCase));
            if (coreHeaderIndex < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                    lines.Add(string.Empty);
                lines.Add("[Core]");
                coreHeaderIndex = lines.Count - 1;
            }

            var keyFound = false;
            var newLine = $"{key} = {value}";
            for (var i = coreHeaderIndex + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith('['))
                    break;

                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase) || !lines[i].Contains('='))
                    continue;

                keyFound = true;
                if (!string.Equals(lines[i].Trim(), newLine, StringComparison.OrdinalIgnoreCase))
                    lines[i] = newLine;
                break;
            }

            if (!keyFound)
            {
                var insertAt = coreHeaderIndex + 1;
                while (insertAt < lines.Count && !lines[insertAt].TrimStart().StartsWith('['))
                    insertAt++;
                lines.Insert(insertAt, newLine);
            }

            File.WriteAllLines(configPath, lines);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static void TrySetDolphinIniValue(string? userDirectory, string key, string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userDirectory))
                return;

            var configDirectory = Path.Combine(userDirectory, "Config");
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "Dolphin.ini");

            List<string> lines;
            if (File.Exists(configPath))
            {
                lines = [.. File.ReadAllLines(configPath)];
            }
            else
            {
                lines = ["[Interface]"];
            }

            var modified = false;
            var keyFound = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase) || !lines[i].Contains('='))
                    continue;

                keyFound = true;
                var newLine = $"{key} = {value}";
                if (!string.Equals(lines[i].Trim(), newLine, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = newLine;
                    modified = true;
                }
            }

            if (!keyFound)
            {
                var interfaceHeaderIndex = lines.FindIndex(line =>
                    string.Equals(line.Trim(), "[Interface]", StringComparison.OrdinalIgnoreCase));
                if (interfaceHeaderIndex < 0)
                {
                    if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                        lines.Add(string.Empty);
                    lines.Add("[Interface]");
                    interfaceHeaderIndex = lines.Count - 1;
                }

                var insertAt = interfaceHeaderIndex + 1;
                while (insertAt < lines.Count && lines[insertAt].TrimStart().StartsWith('['))
                    insertAt++;

                lines.Insert(insertAt, $"{key} = {value}");
                modified = true;
            }

            if (modified)
                File.WriteAllLines(configPath, lines);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    [DllImport("user32.dll")]
    private static new extern bool IsIconic(IntPtr hWnd);
}
