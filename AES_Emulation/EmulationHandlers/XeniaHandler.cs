using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class XeniaHandler : EmulatorHandlerBase
{
    public static XeniaHandler Instance { get; } = new();

    private XeniaHandler()
    {
    }

    public override string HandlerId => "xenia";

    public override string SectionKey => "XBOX360";

    public override string SectionTitle => "Xbox 360";

    public override string DisplayName => "Xenia";

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "XBOX 360", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "XBOX360", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "X360", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Xbox 360", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);

        if (startFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        return startInfo;
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyXeniaRenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyXeniaRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyXeniaRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).ToLowerInvariant();
        var style = GetWindowStyle(hwnd);

        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        bool looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();

            if (lowerTitle.Contains("console") ||
                lowerTitle.Contains("debug") ||
                lowerTitle.Contains("log") ||
                lowerTitle.Contains("settings") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("tools") ||
                lowerTitle.Contains("about") ||
                lowerTitle.Contains("help"))
            {
                return false;
            }

            if (lowerTitle.Contains("xenia"))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            if (className.Contains("sdl") || className.Contains("xenia"))
                return true;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }
}
