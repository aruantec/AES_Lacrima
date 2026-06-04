using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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

    private static readonly Regex BackgroundInputCaptureLineRegex = new(
        @"^\s*background_input_capture\s*=.*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex InputSectionHeaderRegex = new(
        @"^\s*\[input\]\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static void EnsureBackgroundInputCaptureEnabled(string? launcherPath)
    {
        var configPath = ResolveLauncherConfigPath(launcherPath);
        if (string.IsNullOrWhiteSpace(configPath))
            return;

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

    private static string? ResolveLauncherConfigPath(string? launcherPath)
    {
        var executablePath = ResolveLauncherExecutablePath(launcherPath) ?? launcherPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var launcherDirectory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(launcherDirectory)
            ? null
            : Path.Combine(launcherDirectory, "xemu.toml");
    }

    private static bool TryEnableBackgroundInputCapture(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            return false;

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (!File.Exists(configPath))
        {
            File.WriteAllText(
                configPath,
                "[input]" + Environment.NewLine + "background_input_capture = true" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }

        var original = File.ReadAllText(configPath);
        var updated = PatchBackgroundInputCapture(original);
        if (string.Equals(original, updated, StringComparison.Ordinal))
            return false;

        File.WriteAllText(configPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static string PatchBackgroundInputCapture(string content)
    {
        if (BackgroundInputCaptureLineRegex.IsMatch(content))
            return BackgroundInputCaptureLineRegex.Replace(content, "background_input_capture = true");

        var sectionMatch = InputSectionHeaderRegex.Match(content);
        if (sectionMatch.Success)
        {
            var insertAt = sectionMatch.Index + sectionMatch.Length;
            return content.Insert(insertAt, Environment.NewLine + "background_input_capture = true");
        }

        if (content.Length == 0)
            return "[input]" + Environment.NewLine + "background_input_capture = true" + Environment.NewLine;

        var suffix = content.EndsWith('\n') ? string.Empty : Environment.NewLine;
        return content + suffix + Environment.NewLine + "[input]" + Environment.NewLine + "background_input_capture = true" + Environment.NewLine;
    }
}
