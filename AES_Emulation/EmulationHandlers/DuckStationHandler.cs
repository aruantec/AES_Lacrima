using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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

    public override int CaptureStartupDelayMs => 250;

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
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.ArgumentList.Clear();

        // DuckStation expects command switches before `--`, with the image path
        // after `--` so the ROM filename is not parsed as an option.
        startInfo.ArgumentList.Add("-batch");
        if (startFullscreen)
            startInfo.ArgumentList.Add("-fullscreen");

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public override void PrepareProcessForCapture(Process process) => HideProcessWindowsForCapture(process);

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 200;
        const int delayMs = 50;
        const int stableAttemptsBeforeAssign = 2;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareProcessForCapture(process);

            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                mainWindowHandle = process.MainWindowHandle;
            }
            catch
            {
            }

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero && CanAssignWindow(hwnd, mainWindowHandle))
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
                    PrepareWindowForCapture(hwnd);
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

        return await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, IsLikelyDuckStationRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyDuckStationRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyDuckStationRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

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
}
