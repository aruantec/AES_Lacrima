using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using AES_Emulation.Windows.API;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class FlyCastHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<FlyCastHandler>();

    public static FlyCastHandler Instance { get; } = new();

    private FlyCastHandler()
    {
    }

    public override string HandlerId => "flycast";

    public override string SectionKey => "DC";

    public override string SectionTitle => "Dreamcast";

    public override string DisplayName => "FlyCast";

    public override bool HideUntilCaptured => true;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override int ClientAreaCropTopInset => 28;

    public override int ClientAreaCropBottomInset => 14;

    public override int CaptureStartupDelayMs => 120;

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

    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for Flycast.
        // Avoid hiding windows too early; it can slow down capture target stabilization.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for Flycast.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyFlyCastRenderWindow, fallbackTitleHint: DisplayName);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyFlyCastRenderWindow(hwnd, mainWindowHandle);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 120;
        const int delayMs = 40;
        const int stableAttemptsBeforeAssign = 3;

        var captureStopwatch = Stopwatch.StartNew();
        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        var firstCandidateObservedMs = -1L;

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

            if (hwnd != IntPtr.Zero && firstCandidateObservedMs < 0)
            {
                firstCandidateObservedMs = captureStopwatch.ElapsedMilliseconds;
                Log.Info($"Flycast capture target candidate detected after {firstCandidateObservedMs} ms (attempt {attempt + 1}). hwnd=0x{hwnd.ToInt64():X}.");
            }

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
                    if (OperatingSystem.IsWindows() && hwnd != IntPtr.Zero)
                    {
                        try
                        {
                            if (Win32API.TryGetVirtualScreenBounds(out var x, out var y, out var width, out var height) && width > 0 && height > 0)
                            {
                                const int targetWidth = 1920;
                                const int targetHeight = 1080;
                                Win32API.SetWindowBounds(hwnd, x, y, Math.Min(targetWidth, width), Math.Min(targetHeight, height));
                            }
                            else
                            {
                                Win32API.SetWindowSize(hwnd, 1920, 1080);
                            }
                        }
                        catch
                        {
                        }
                    }
                    Log.Info(
                        $"Flycast capture target stabilized after {captureStopwatch.ElapsedMilliseconds} ms " +
                        $"(firstCandidateMs={(firstCandidateObservedMs >= 0 ? firstCandidateObservedMs : captureStopwatch.ElapsedMilliseconds)}, " +
                        $"stableAttempts={observedStableAttempts}, hwnd=0x{hwnd.ToInt64():X}).");
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

        var targetHwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (targetHwnd != IntPtr.Zero)
        {
            if (OperatingSystem.IsWindows() && targetHwnd != IntPtr.Zero)
            {
                try
                {
                    if (Win32API.TryGetVirtualScreenBounds(out var x, out var y, out var width, out var height) && width > 0 && height > 0)
                    {
                        const int targetWidth = 1920;
                        const int targetHeight = 1080;
                        Win32API.SetWindowBounds(targetHwnd, x, y, Math.Min(targetWidth, width), Math.Min(targetHeight, height));
                    }
                    else
                    {
                        Win32API.SetWindowSize(targetHwnd, 1920, 1080);
                    }
                }
                catch
                {
                }
            }
            Log.Info($"Flycast capture target fallback resolved after {captureStopwatch.ElapsedMilliseconds} ms. hwnd=0x{targetHwnd.ToInt64():X}.");
            return targetHwnd;
        }

        Log.Warn($"Flycast capture target resolution failed after {captureStopwatch.ElapsedMilliseconds} ms.");
        return FindFallbackQtGameWindow(process);
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyFlyCastRenderWindow(hwnd, mainWindowHandle))
            return false;

        if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
            return false;

        if (width < 640 || height < 360)
            return false;

        return true;
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

            if (!IsLikelyFlyCastRenderWindow(hwnd, mainWindowHandle))
                continue;

            if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
                continue;

            if (width < 480 || height < 270)
                continue;

            var title = GetWindowTitle(hwnd).Trim();
            var score = (long)width * height;

            if (!string.IsNullOrWhiteSpace(title))
                score += 500_000;

            if (!title.Contains("flycast", StringComparison.OrdinalIgnoreCase))
                score += 350_000;

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
        }

        return best;
    }

    private static bool IsLikelyFlyCastRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
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
            if (lowerTitle.Contains("flycast") &&
                !lowerTitle.Contains("settings") &&
                !lowerTitle.Contains("audio") &&
                !lowerTitle.Contains("video") &&
                !lowerTitle.Contains("input") &&
                !lowerTitle.Contains("controller") &&
                !lowerTitle.Contains("cheat") &&
                !lowerTitle.Contains("shader") &&
                !lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = false;
            }

            if (lowerTitle.Contains("settings") ||
                lowerTitle.Contains("audio") ||
                lowerTitle.Contains("video") ||
                lowerTitle.Contains("input") ||
                lowerTitle.Contains("controller") ||
                lowerTitle.Contains("cheat") ||
                lowerTitle.Contains("shader") ||
                lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = true;
            }

            if (!looksLikePrimaryUi && lowerTitle.Length >= 2)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(lowerClass))
        {
            if (lowerClass.Contains("sdl") ||
                lowerClass.Contains("glfw") ||
                lowerClass.Contains("qt") ||
                lowerClass.Contains("flycast"))
            {
                if (!looksLikePrimaryUi)
                    return true;

                if (!string.IsNullOrWhiteSpace(title) && !lowerTitle.Contains("settings") && !lowerTitle.Contains("audio") && !lowerTitle.Contains("video") && !lowerTitle.Contains("input") && !lowerTitle.Contains("controller") && !lowerTitle.Contains("cheat") && !lowerTitle.Contains("shader") && !lowerTitle.Contains("about"))
                    return true;

                looksLikePrimaryUi |= hasCaption && hasThickFrame;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }
}