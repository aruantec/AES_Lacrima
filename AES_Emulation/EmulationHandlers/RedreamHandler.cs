using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class RedreamHandler : EmulatorHandlerBase
{
    public static RedreamHandler Instance { get; } = new();

    private RedreamHandler()
    {
    }

    public override string HandlerId => "redream";

    public override string SectionKey => "DC";

    public override string SectionTitle => "Dreamcast";

    public override string DisplayName => "Redream";

    public override bool HideUntilCaptured => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Sega Dreamcast", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);

        if (startFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        return startInfo;
    }

    public override void PrepareProcessForCapture(Process process) => HideProcessWindowsForCapture(process);

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyRedreamRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyRedreamRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyRedreamRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;
        var normalizedTitle = title;

        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            var lowerTitle = normalizedTitle.ToLowerInvariant();

            if (lowerTitle.Contains("redream") && !lowerTitle.Contains("settings") && !lowerTitle.Contains("audio") && !lowerTitle.Contains("video") && !lowerTitle.Contains("input") && !lowerTitle.Contains("controller") && !lowerTitle.Contains("cheat") && !lowerTitle.Contains("shader") && !lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = false;
            }

            if (lowerTitle.Contains("settings") || lowerTitle.Contains("audio") || lowerTitle.Contains("video") || lowerTitle.Contains("input") || lowerTitle.Contains("controller") || lowerTitle.Contains("cheat") || lowerTitle.Contains("shader") || lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.Contains("sdl") || lowerClass.Contains("glfw") || lowerClass.Contains("qt") || lowerClass.Contains("redream"))
            {
                looksLikePrimaryUi |= hasCaption && hasThickFrame;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(normalizedTitle));
    }
}
