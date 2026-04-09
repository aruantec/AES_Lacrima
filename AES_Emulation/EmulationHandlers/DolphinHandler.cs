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

    public override int ClientAreaCropRightInset => 4;

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
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var lowerTitle = title.ToLowerInvariant();
            var lowerClass = className.ToLowerInvariant();

            // Exclude helper/system windows that can appear in Dolphin's process tree.
            if (lowerTitle.Contains("diemwin") ||
                lowerTitle.Contains("temp window") ||
                lowerTitle.Contains("msctfime ui") ||
                lowerTitle.Contains("default ime") ||
                lowerClass.StartsWith("temp_d3d_window", StringComparison.Ordinal) ||
                lowerClass.Contains("diemwin") ||
                lowerClass.Contains("ime") ||
                lowerClass.Contains("screenchangeobserver") ||
                lowerClass.Contains("themechangeobserver"))
            {
                return false;
            }

            // Typical Dolphin game window title:
            // "Dolphin 2603a | JTT64 SC | Vulkan | HLE | <Game Title>"
            // Prefer this aggressively for capture.
            var looksLikeGameTitle = lowerTitle.Contains("dolphin") && lowerTitle.Contains(" | ");

            if (looksLikeGameTitle)
                return true;

            var isClearlyUiTitle =
                lowerTitle.Contains("dolphin") ||
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

            // A plain "Dolphin <version>" title is usually the main UI shell, not the game surface.
            if (lowerTitle.StartsWith("dolphin ", StringComparison.Ordinal) && !lowerTitle.Contains(" | "))
                return false;

            if (!isClearlyUiTitle && lowerTitle.Length >= 3)
            {
                // Keep fallback permissive only for plausible app windows.
                return lowerClass.Contains("qt") || lowerClass.Contains("wx") || lowerClass.Contains("dolphin");
            }

            if (isClearlyUiTitle)
                looksLikePrimaryUi = true;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            var lowerClass = className.ToLowerInvariant();
            if (lowerClass.StartsWith("temp_d3d_window", StringComparison.Ordinal) ||
                lowerClass.Contains("diemwin") ||
                lowerClass.Contains("screenchangeobserver") ||
                lowerClass.Contains("themechangeobserver"))
            {
                return false;
            }

            if (lowerClass.Contains("dolphin") && hasCaption && hasThickFrame)
                looksLikePrimaryUi |= hasCaption && hasThickFrame;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
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

            // Never touch IME/helper windows (these caused taskbar artifacts).
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
}
