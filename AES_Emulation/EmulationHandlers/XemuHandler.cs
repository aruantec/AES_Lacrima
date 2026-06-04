using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using AES_Emulation.Windows.API;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class XemuHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<XemuHandler>();

    public static XemuHandler Instance { get; } = new();

    private XemuHandler()
    {
    }

    public override string HandlerId => "xemu";

    public override string SectionKey => "XBOX";

    public override string SectionTitle => "Xbox";

    public override string DisplayName => "xemu";

    public override bool HideUntilCaptured => true;

    public override double? CaptureWindowAspectRatio => 4.0 / 3.0;

    public override int CaptureStartupDelayMs => 1500;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Original Xbox", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Microsoft Xbox", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        EnsureBackgroundInputCaptureEnabled(launcherPath);

        var startInfo = CreateBaseStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        if (startFullscreen)
            startInfo.ArgumentList.Add("-full-screen");

        if (IsDiscImagePath(romPath))
        {
            startInfo.ArgumentList.Add("-dvd_path");
            startInfo.ArgumentList.Add(romPath);
        }
        else
        {
            startInfo.ArgumentList.Add(romPath);
        }

        return startInfo;
    }

    public override void PrepareProcessForCapture(Process process)
    {
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyXemuRenderWindow, fallbackTitleHint: DisplayName);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyXemuRenderWindow(hwnd, mainWindowHandle);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 120;
        const int delayMs = 50;
        const int stableAttemptsBeforeAssign = 3;

        var captureStopwatch = Stopwatch.StartNew();
        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                if (process.HasExited)
                    return IntPtr.Zero;

                mainWindowHandle = process.MainWindowHandle;
            }
            catch (Exception logEx)
            {
                Log.Warn("Exception caught", logEx);
            }

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero && IsStableCaptureCandidate(hwnd, mainWindowHandle))
            {
                if (hwnd == observedHwnd)
                    observedStableAttempts++;
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                }

                if (observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    Log.Info(
                        $"xemu capture target stabilized after {captureStopwatch.ElapsedMilliseconds} ms " +
                        $"(stableAttempts={observedStableAttempts}, hwnd=0x{hwnd.ToInt64():X}).");
                    return hwnd;
                }
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var fallback = FindPreferredWindowHandle(process);
        if (fallback != IntPtr.Zero)
            Log.Info($"xemu capture target fallback after {captureStopwatch.ElapsedMilliseconds} ms. hwnd=0x{fallback.ToInt64():X}.");

        return fallback;
    }

    private static bool IsDiscImagePath(string romPath)
    {
        var extension = Path.GetExtension(romPath);
        return string.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xiso", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyXemuRenderWindow(hwnd, mainWindowHandle))
            return false;

        if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
            return false;

        return width >= 640 && height >= 360;
    }

    private static bool IsLikelyXemuRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(lowerTitle))
        {
            if (lowerTitle.Contains("settings") ||
                lowerTitle.Contains("monitor") ||
                lowerTitle.Contains("about") ||
                lowerTitle.Contains("input") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("network") ||
                lowerTitle.Contains("display"))
            {
                return false;
            }

            if (lowerTitle.Contains("xemu") || lowerTitle.Contains("xbox"))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(lowerClass) &&
            (lowerClass.Contains("sdl") || lowerClass.Contains("xemu")))
        {
            return hwnd != mainWindowHandle || !string.IsNullOrWhiteSpace(title);
        }

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle && hasCaption && hasThickFrame;

        return !looksLikePrimaryUi;
    }

    private static void EnsureBackgroundInputCaptureEnabled(string? launcherPath)
    {
        foreach (var configPath in ResolveXemuConfigPaths(launcherPath))
        {
            try
            {
                if (TryEnableBackgroundInputCapture(configPath))
                    Log.Info($"Enabled xemu background controller input capture in '{configPath}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to update xemu config at '{configPath}'.", ex);
            }
        }
    }

    private static IEnumerable<string> ResolveXemuConfigPaths(string? launcherPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var executablePath = ResolveLauncherExecutablePath(launcherPath) ?? launcherPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var launcherDirectory = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrWhiteSpace(launcherDirectory))
            {
                var nextToExecutable = Path.Combine(launcherDirectory, "xemu.toml");
                if (seen.Add(nextToExecutable))
                    yield return nextToExecutable;
            }
        }

        foreach (var root in GetXemuDataRoots())
        {
            var configPath = Path.Combine(root, "xemu.toml");
            if (seen.Add(configPath))
                yield return configPath;
        }
    }

    private static IEnumerable<string> GetXemuDataRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
                yield return Path.Combine(appData, "xemu", "xemu");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                yield return Path.Combine(localAppData, "xemu", "xemu");
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                yield return Path.Combine(home, ".local", "share", "xemu", "xemu");
                yield return Path.Combine(home, ".var", "app", "app.xemu.xemu", "data", "xemu", "xemu");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                yield return Path.Combine(home, "Library", "Application Support", "xemu", "xemu");
        }
    }

    private static bool TryEnableBackgroundInputCapture(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return false;

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        List<string> lines;
        if (File.Exists(configPath))
        {
            lines = File.ReadAllLines(configPath).ToList();
        }
        else
        {
            lines =
            [
                "[general]",
                "show_welcome = false",
                string.Empty,
                "[input]"
            ];
        }

        var modified = UpsertTomlBool(lines, "[input]", "background_input_capture", true);
        if (!modified && File.Exists(configPath))
            return false;

        File.WriteAllLines(configPath, lines, Encoding.UTF8);
        return true;
    }

    private static bool UpsertTomlBool(List<string> lines, string sectionHeader, string key, bool value)
    {
        var desiredLine = $"{key} = {(value ? "true" : "false")}";
        var sectionIndex = FindSectionIndex(lines, sectionHeader);
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add(sectionHeader);
            lines.Add(desiredLine);
            return true;
        }

        var sectionEnd = FindSectionEnd(lines, sectionIndex + 1);
        for (var i = sectionIndex + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase) || !trimmed.Contains('='))
                continue;

            if (string.Equals(lines[i].Trim(), desiredLine, StringComparison.Ordinal))
                return false;

            lines[i] = desiredLine;
            return true;
        }

        lines.Insert(sectionEnd, desiredLine);
        return true;
    }

    private static int FindSectionIndex(IReadOnlyList<string> lines, string sectionHeader)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindSectionEnd(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                return i;
        }

        return lines.Count;
    }
}
