using System;
using System.Diagnostics;

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

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS1", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen);
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

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, IsLikelyDuckStationRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyDuckStationRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyDuckStationRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd);
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;
        var normalizedTitle = title.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            if (normalizedTitle.Contains("DuckStation", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("settings", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("game list", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("memory card", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("cheat", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("achievement", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("tools", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("view", StringComparison.OrdinalIgnoreCase) ||
                normalizedTitle.Contains("help", StringComparison.OrdinalIgnoreCase))
            {
                looksLikePrimaryUi = true;
            }

            if (!normalizedTitle.Contains("DuckStation", StringComparison.OrdinalIgnoreCase) &&
                normalizedTitle.Length >= 3)
            {
                looksLikePrimaryUi = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(className) &&
            (className.Contains("QWindow", StringComparison.OrdinalIgnoreCase) ||
             className.Contains("Qt", StringComparison.OrdinalIgnoreCase)))
        {
            looksLikePrimaryUi |= hasCaption && hasThickFrame;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(normalizedTitle));
    }
}
