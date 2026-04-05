using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class Pcsx2Handler : EmulatorHandlerBase
{
    public static Pcsx2Handler Instance { get; } = new();

    private Pcsx2Handler()
    {
    }

    public override string HandlerId => "pcsx2";

    public override string SectionKey => "PS2";

    public override string SectionTitle => "PlayStation 2";

    public override string DisplayName => "PCSX2";

    public override bool HideUntilCaptured => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Playstation 2", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        // PCSX2 Qt supports batch mode and optional fullscreen startup.
        // `-nogui` reduces chances of capturing the full shell window instead of the render surface.
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-nogui");
        if (startFullscreen)
            startInfo.ArgumentList.Add("-fullscreen");

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public override int CaptureStartupDelayMs => 900;

    public override void PrepareProcessForCapture(Process process) => HideProcessWindowsForCapture(process);

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyPcsx2RenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyPcsx2RenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyPcsx2RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();

            var isClearlyUiTitle =
                lowerTitle.Contains("pcsx2") ||
                lowerTitle.Contains("settings") ||
                lowerTitle.Contains("graphics") ||
                lowerTitle.Contains("audio") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("memory card") ||
                lowerTitle.Contains("cheat") ||
                lowerTitle.Contains("tools") ||
                lowerTitle.Contains("about") ||
                lowerTitle.Contains("emulation settings") ||
                lowerTitle.Contains("game properties");

            if (isClearlyUiTitle)
                looksLikePrimaryUi = true;
            else if (lowerTitle.Length >= 3)
                looksLikePrimaryUi = false;

            // For PCSX2, game windows often use Qt classes and still have captions.
            // If the title looks like actual game/content, prefer it as render window.
            if (!isClearlyUiTitle && lowerTitle.Length >= 3)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.Contains("pcsx2") && hasCaption && hasThickFrame)
                looksLikePrimaryUi |= hasCaption && hasThickFrame;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }
}
