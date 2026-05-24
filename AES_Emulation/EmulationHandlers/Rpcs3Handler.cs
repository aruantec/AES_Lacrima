using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Controls;
using AES_Emulation.Windows.API;

namespace AES_Emulation.EmulationHandlers;

public sealed class Rpcs3Handler : EmulatorHandlerBase
{
    public const string GameIdBootPrefix = "%RPCS3_GAMEID%:";

    private const uint WS_CHILD = 0x40000000;

    public static Rpcs3Handler Instance { get; } = new();

    private Rpcs3Handler()
    {
    }

    public override string HandlerId => "rpcs3";

    public override string SectionKey => "PS3";

    public override string SectionTitle => "PlayStation 3";

    public override string DisplayName => "RPCS3";

    public override bool HideUntilCaptured => true;

    public override bool IsWindowEmbeddingSupported => true;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override int CaptureStartupDelayMs => 200;

    public override bool IsLauncherPathValid(string? launcherPath)
        => !string.IsNullOrWhiteSpace(ResolveRpcs3LauncherPath(launcherPath));

    public override string? NormalizeLauncherPath(string? launcherPath)
        => ResolveRpcs3LauncherPath(launcherPath) ?? base.NormalizeLauncherPath(launcherPath);

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Playstation 3", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS3", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        // RPCS3 supports direct CLI boot. Using no-GUI mode avoids the launcher/game-list shell
        // and gets us to the actual render window faster.
        startInfo.ArgumentList.Add("--no-gui");

        var bootTarget = romPath;
        if (!string.IsNullOrWhiteSpace(bootTarget) &&
            bootTarget.StartsWith(GameIdBootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            bootTarget = bootTarget[GameIdBootPrefix.Length..].Trim();
        }

        startInfo.ArgumentList.Add(bootTarget);
        return startInfo;
    }

    public static string BuildGameIdBootPath(string titleId)
        => GameIdBootPrefix + titleId;

    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for RPCS3.
        // Hiding or resizing during target resolution can race with window construction
        // and produce stale aspect ratios on relaunch.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for RPCS3; see PrepareProcessForCapture.
    }

    public override void PrepareWindowForCaptureAttach(IntPtr hwnd)
    {
        // Geometry is applied once the render window is fully constructed during resolve.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        return FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyRpcs3RenderWindow,
            fallbackTitleHint: DisplayName);
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyRpcs3RenderWindow(hwnd, mainWindowHandle);

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 160;
        const int delayMs = 40;
        const int stableAttemptsBeforeAssign = 8;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        var lastStableWidth = 0;
        var lastStableHeight = 0;

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

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
                KeepWindowHiddenDuringResolve(hwnd);

            if (hwnd != IntPtr.Zero &&
                IsStableCaptureCandidate(hwnd, mainWindowHandle) &&
                Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            {
                var dimensionsStable = width == lastStableWidth && height == lastStableHeight;
                if (hwnd == observedHwnd && dimensionsStable)
                    observedStableAttempts++;
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                    lastStableWidth = width;
                    lastStableHeight = height;
                }

                if (observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    ApplyCaptureGeometryOnce(hwnd);
                    KeepWindowHiddenDuringResolve(hwnd);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    return hwnd;
                }
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
                lastStableWidth = 0;
                lastStableHeight = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var fallback = FindPreferredWindowHandle(process);
        if (fallback != IntPtr.Zero)
        {
            ApplyCaptureGeometryOnce(fallback);
            KeepWindowHiddenDuringResolve(fallback);
        }

        return fallback;
    }

    private static void KeepWindowHiddenDuringResolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.SetWindowCloaked(hwnd, cloaked: true);
            Win32API.MoveAway(hwnd, useCloak: true);
            Win32API.EnsureRenderActiveForCapture(hwnd, bringOnScreen: false);
        }
        catch
        {
        }
    }

    private void ApplyCaptureGeometryOnce(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            Win32API.TryExitFullscreenWindow(hwnd);

            if (Win32API.HasWindowCaption(hwnd))
                Win32API.RemoveWindowDecorations(hwnd);

            var aspect = CaptureWindowAspectRatio;
            if (aspect is > 0)
                Win32API.ResizeWindowToAspectRatioInPlace(hwnd, aspect.Value);
        }
        catch
        {
        }
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyRpcs3RenderWindow(hwnd, mainWindowHandle))
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.HasWindowCaption(hwnd))
            return false;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return false;

        return width >= 640 && height >= 360;
    }

    private static string? ResolveRpcs3LauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var normalizedPath = launcherPath.Trim();
        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch
        {
        }

        if (File.Exists(normalizedPath))
            return normalizedPath;

        if (!Directory.Exists(normalizedPath))
            return null;

        var executableNames = new[]
        {
            "rpcs3.exe",
            "RPCS3.exe",
            "rpcs3",
            "RPCS3"
        };

        foreach (var executableName in executableNames)
        {
            var candidate = Path.Combine(normalizedPath, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var launcherCandidate = Directory.EnumerateFiles(normalizedPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    return string.Equals(fileName, "rpcs3", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Contains("rpcs3", StringComparison.OrdinalIgnoreCase);
                });

            if (launcherCandidate != null)
                return launcherCandidate;

            var files = Directory.EnumerateFiles(normalizedPath, "*", SearchOption.AllDirectories).ToArray();
            if (files.Length == 1)
                return files[0];
        }
        catch
        {
        }

        return null;
    }

    private static bool IsLikelyRpcs3RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).Trim();
        var lowerTitle = title.ToLowerInvariant();
        var style = GetWindowStyle(hwnd);
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasChildStyle = (style & WS_CHILD) == WS_CHILD;
        var parent = GetParent(hwnd);

        if (hasChildStyle || parent != IntPtr.Zero)
            return false;

        if (IsRpcs3UiWindow(lowerTitle))
            return false;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var clientWidth, out var clientHeight))
            return false;

        if (hwnd == mainWindowHandle && clientWidth >= 640 && clientHeight >= 360)
            return true;

        if (lowerTitle.StartsWith("fps:", StringComparison.OrdinalIgnoreCase) ||
            lowerTitle.Contains("vulkan") ||
            lowerTitle.Contains("opengl") ||
            lowerTitle.Contains("render") ||
            lowerTitle.Contains("gpu") ||
            lowerTitle.Contains("swapchain"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(title) && clientWidth >= 480 && clientHeight >= 270)
            return true;

        if (lowerTitle.Contains("rpcs3") && !hasCaption)
            return true;

        return false;
    }

    private static bool IsRpcs3UiWindow(string lowerTitle)
    {
        if (string.IsNullOrWhiteSpace(lowerTitle))
            return false;

        return lowerTitle.Contains("settings") ||
               lowerTitle.Contains("configuration") ||
               lowerTitle.Contains("controller") ||
               lowerTitle.Contains("input") ||
               lowerTitle.Contains("audio") ||
               lowerTitle.Contains("video") ||
               lowerTitle.Contains("about") ||
               lowerTitle.Contains("help") ||
               lowerTitle.Contains("log") ||
               lowerTitle.Contains("debug") ||
               lowerTitle.Contains("game list") ||
               lowerTitle.Contains("shader") ||
               lowerTitle.Contains("launcher") ||
               lowerTitle.StartsWith("rpcs3 ", StringComparison.OrdinalIgnoreCase) ||
               lowerTitle.Contains("master") ||
               lowerTitle.Contains("alpha");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static new extern bool IsIconic(IntPtr hWnd);
}
