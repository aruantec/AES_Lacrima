using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    [SupportedOSPlatform("windows")]
    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        var topLevel = FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyXeniaRenderWindow);

        var childRenderWindow = FindBestXeniaRenderChildWindow(process, topLevel);
        return childRenderWindow != IntPtr.Zero ? childRenderWindow : topLevel;
    }

    [SupportedOSPlatform("windows")]
    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyXeniaRenderWindow(hwnd, mainWindowHandle);

    [SupportedOSPlatform("windows")]
    private static bool IsLikelyXeniaRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).ToLowerInvariant();
        var style = GetWindowStyle(hwnd);

        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
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

            if (className.StartsWith("temp_d3d_window", StringComparison.Ordinal) ||
                (className.Contains("d3d") && className.Contains("window")))
            {
                return true;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr FindBestXeniaRenderChildWindow(Process process, IntPtr primaryWindow)
    {
        uint processId;
        try
        {
            process.Refresh();
            processId = (uint)process.Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        IntPtr bestHwnd = IntPtr.Zero;
        long bestScore = long.MinValue;

        void ProbeChildren(IntPtr parent)
        {
            if (parent == IntPtr.Zero)
                return;

            EnumChildWindows(parent, (child, _) =>
            {
                var score = ScoreXeniaRenderChildCandidate(child, processId);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = child;
                }

                return true;
            }, IntPtr.Zero);
        }

        ProbeChildren(primaryWindow);

        foreach (var topLevel in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
            ProbeChildren(topLevel);

        return bestScore > long.MinValue ? bestHwnd : IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private static long ScoreXeniaRenderChildCandidate(IntPtr hwnd, uint processId)
    {
        if (hwnd == IntPtr.Zero)
            return long.MinValue;

        if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
            return long.MinValue;

        if (!GetWindowRect(hwnd, out RECT rect))
            return long.MinValue;

        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width < 320 || height < 180)
            return long.MinValue;

        var className = GetWindowClassName(hwnd).Trim().ToLowerInvariant();
        var title = GetWindowTitle(hwnd).Trim().ToLowerInvariant();
        if (className.Length == 0 && title.Length == 0)
            return long.MinValue;

        if (className.Contains("ime") ||
            className.Contains("observerwindow") ||
            className.Contains("titlebar") ||
            className.Contains("tooltip"))
        {
            return long.MinValue;
        }

        long score = (long)width * height * 10;
        if (IsWindowVisible(hwnd))
            score += 500_000;

        if (className.StartsWith("temp_d3d_window", StringComparison.Ordinal))
            score += 50_000_000;

        if (className.Contains("d3d") && className.Contains("window"))
            score += 20_000_000;

        if (className.Contains("render") || className.Contains("swapchain") || className.Contains("viewport"))
            score += 5_000_000;

        if (title.Contains("temp window"))
            score += 10_000_000;

        return score;
    }

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
