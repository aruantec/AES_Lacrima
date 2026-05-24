using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Windows.API;

namespace AES_Emulation.EmulationHandlers;

public sealed class DuckStationHandler : EmulatorHandlerBase
{
    public static DuckStationHandler Instance { get; } = new();

    private DuckStationHandler()
    {
    }

    public override string HandlerId => "duckstation";

    public override string SectionKey => "PSX";

    public override string SectionTitle => "PlayStation";

    public override string DisplayName => "DuckStation";

    public override bool HideUntilCaptured => true;

    public override double? CaptureWindowAspectRatio => 4.0 / 3.0;

    public override int CaptureStartupDelayMs => 120;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PlayStation", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PlayStation 1", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        EnsurePortableModeMarker(startInfo.FileName, startInfo.WorkingDirectory);

        // DuckStation expects command switches before `--`, with the image path
        // after `--` so the ROM filename is not parsed as an option.
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-nofullscreen");
        startInfo.ArgumentList.Add("-nogui");

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public static void EnsurePortableModeMarker(string? executablePath, string? workingDirectory)
    {
        try
        {
            var baseDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(executablePath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                return;

            var portableMarkerPath = Path.Combine(baseDirectory, "portable.txt");
            if (!File.Exists(portableMarkerPath))
                File.WriteAllText(portableMarkerPath, string.Empty);
        }
        catch
        {
        }
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 120;
        const int delayMs = 40;
        const int stableAttemptsBeforeAssign = 6;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                mainWindowHandle = process.MainWindowHandle;
            }
            catch
            {
            }

            var hwnd = FindFallbackQtGameWindow(process);
            if (hwnd == IntPtr.Zero)
                hwnd = FindPreferredWindowHandle(process);

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
                    return hwnd;
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var targetHwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (targetHwnd != IntPtr.Zero)
            return targetHwnd;

        return FindFallbackQtGameWindow(process);
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, IsLikelyDuckStationRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyDuckStationRenderWindow(hwnd, mainWindowHandle);

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyDuckStationRenderWindow(hwnd, mainWindowHandle))
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
            return false;

        if (width < 640 || height < 360)
            return false;

        return true;
    }

    private static bool IsLikelyDuckStationRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        if (lowerTitle.Contains("_q_titlebar") ||
            lowerTitle.Contains("msctfime ui") ||
            lowerTitle.Contains("default ime") ||
            lowerClass.Contains("screenchangeobserver") ||
            lowerClass.Contains("themechangeobserver") ||
            lowerClass.Contains("ime"))
        {
            return false;
        }

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        // DuckStation can render directly in its main Qt window with an empty title
        // while loading/starting a game. Allow that window shape as a capture target.
        if (looksLikePrimaryUi && string.IsNullOrWhiteSpace(lowerTitle) && lowerClass.Contains("qt") && !hasCaption)
            return true;

        if (!string.IsNullOrWhiteSpace(lowerTitle))
        {
            var titleLooksLikeUi =
                lowerTitle == "duckstation" ||
                lowerTitle.Contains("settings") ||
                lowerTitle.Contains("audio") ||
                lowerTitle.Contains("video") ||
                lowerTitle.Contains("input") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("memory card") ||
                lowerTitle.Contains("cheat") ||
                lowerTitle.Contains("achievement") ||
                lowerTitle.Contains("tools") ||
                lowerTitle.Contains("view") ||
                lowerTitle.Contains("help") ||
                lowerTitle.Contains("shader") ||
                lowerTitle.Contains("about");

            if (titleLooksLikeUi)
                looksLikePrimaryUi = true;
            else if (lowerTitle.Length >= 2)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(lowerClass) && lowerClass.Contains("qt"))
        {
            if (!looksLikePrimaryUi && !lowerTitle.Contains("duckstation"))
                return true;

            if (!looksLikePrimaryUi && !hasCaption)
                return true;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }

    private static IntPtr FindFallbackQtGameWindow(Process process)
    {
        IntPtr best = IntPtr.Zero;
        long bestScore = long.MinValue;

        IntPtr mainWindowHandle;
        try
        {
            mainWindowHandle = process.MainWindowHandle;
        }
        catch
        {
            mainWindowHandle = IntPtr.Zero;
        }

        foreach (var hwnd in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
        {
            if (hwnd == IntPtr.Zero)
                continue;

            if (!IsLikelyDuckStationRenderWindow(hwnd, mainWindowHandle))
                continue;

            if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
                continue;

            if (width < 480 || height < 270)
                continue;

            var title = GetWindowTitle(hwnd).Trim();
            var score = (long)width * height;

            if (!string.IsNullOrWhiteSpace(title))
                score += 500_000;

            if (!title.Contains("duckstation", StringComparison.OrdinalIgnoreCase))
                score += 350_000;

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
        }

        return best;
    }

    [DllImport("user32.dll")]
    private static new extern bool IsIconic(IntPtr hWnd);
}
