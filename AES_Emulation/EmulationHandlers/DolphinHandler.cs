using System;
using System.Diagnostics;
using AES_Emulation.Windows.API;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class DolphinHandler : EmulatorHandlerBase
{
    public static DolphinHandler Instance { get; } = new();

    private DolphinHandler()
    {
    }

    public override string HandlerId => "dolphin";

    public override string SectionKey => "GC";

    public override string SectionTitle => "GameCube";

    public override string DisplayName => "Dolphin";

    public override bool HideUntilCaptured => false;

    public override bool ForceUseTargetClientAreaCapture => true;

    /// <summary>
    /// Dolphin's render window often includes thin invisible borders or padding that can cause
    /// capture artifacts (like a vertical line on the right) when using WGC. 
    /// Increasing the right inset slightly clips these artifacts.
    /// </summary>
    public override int ClientAreaCropRightInset => 16;

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

        // Dolphin CLI: -b batch, -e executable/content path, -f fullscreen.
        startInfo.ArgumentList.Add("-b");
        if (startFullscreen)
            startInfo.ArgumentList.Add("-f");

        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public override int CaptureStartupDelayMs => 650;

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

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyDolphinRenderWindow);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        var targetHwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (targetHwnd == IntPtr.Zero)
        {
            targetHwnd = FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: true, isPreferredRenderWindow: null);
        }

        if (targetHwnd != IntPtr.Zero)
            HideNonTargetDolphinWindows(process, targetHwnd);

        return targetHwnd;
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
            catch
            {
                // ignore transient window races
            }
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
}
