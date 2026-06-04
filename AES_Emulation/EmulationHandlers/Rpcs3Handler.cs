using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Controls;
using AES_Emulation.Windows.API;

using log4net;
using AES_Core.Logging;
namespace AES_Emulation.EmulationHandlers;

public sealed class Rpcs3Handler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<Rpcs3Handler>();
    public const string GameIdBootPrefix = "%RPCS3_GAMEID%:";

    private const uint WS_CHILD = 0x40000000;

    /// <summary>Minimum client size before we treat an RPCS3 surface as a render candidate.</summary>
    private const int MinRenderCandidateWidth = 480;
    private const int MinRenderCandidateHeight = 270;

    /// <summary>Minimum stable client size while polling (gs_frame is usually constructed larger).</summary>
    private const int MinStableClientWidth = 960;
    private const int MinStableClientHeight = 540;

    /// <summary>Minimum client size after aspect-ratio resize before handing off to WGC.</summary>
    private const int MinConstructedClientWidth = 1280;
    private const int MinConstructedClientHeight = 720;

    public static Rpcs3Handler Instance { get; } = new();

    private Rpcs3Handler()
    {
    }

    public override string HandlerId => "rpcs3";

    public override string SectionKey => "PS3";

    public override string SectionTitle => "PlayStation 3";

    public override string DisplayName => "RPCS3";

    public override bool HideUntilCaptured => true;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override bool IsWindowEmbeddingSupported => true;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override int CaptureStartupDelayMs => 2500;

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

        EnsurePauseOnFocusLossDisabled(startInfo.FileName, startInfo.WorkingDirectory);

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
        ApplyCaptureGeometryOnce(hwnd);
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        IntPtr mainWindowHandle = IntPtr.Zero;
        try
        {
            process.Refresh();
            mainWindowHandle = process.MainWindowHandle;
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        var childRender = FindBestRpcs3RenderChildWindow(process, mainWindowHandle);
        if (childRender != IntPtr.Zero)
            return childRender;

        return FindBestRpcs3TopLevelRenderWindow(process, mainWindowHandle);
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => ScoreRpcs3CaptureWindow(hwnd, mainWindowHandle) > long.MinValue;

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 400;
        const int delayMs = 50;
        const int stableAttemptsBeforeAssign = 10;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        var lastStableWidth = 0;
        var lastStableHeight = 0;
        var geometryApplied = false;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr mainWindowHandle = IntPtr.Zero;
            try
            {
                process.Refresh();
                mainWindowHandle = process.MainWindowHandle;
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
                HideNonTargetRpcs3Windows(process, hwnd);

            if (hwnd != IntPtr.Zero &&
                IsStableCaptureCandidate(hwnd, mainWindowHandle) &&
                Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            {
                if (!geometryApplied && width >= MinStableClientWidth && height >= MinStableClientHeight)
                {
                    ApplyCaptureGeometryOnce(hwnd);
                    geometryApplied = true;
                    observedHwnd = IntPtr.Zero;
                    observedStableAttempts = 0;
                    lastStableWidth = 0;
                    lastStableHeight = 0;
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!geometryApplied)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

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

                if (observedStableAttempts >= stableAttemptsBeforeAssign &&
                    width >= MinConstructedClientWidth &&
                    height >= MinConstructedClientHeight)
                {
                    ApplyCaptureGeometryOnce(hwnd);
                    KeepWindowHiddenDuringResolve(hwnd);
                    HideNonTargetRpcs3Windows(process, hwnd);
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
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
        if (fallback != IntPtr.Zero && CanAssignWindow(fallback, process.MainWindowHandle))
        {
            ApplyCaptureGeometryOnce(fallback);
            KeepWindowHiddenDuringResolve(fallback);
            HideNonTargetRpcs3Windows(process, fallback);
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (ScoreRpcs3CaptureWindow(hwnd, mainWindowHandle) <= long.MinValue)
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return false;

        return width >= MinStableClientWidth && height >= MinStableClientHeight;
    }

    private static IntPtr FindBestRpcs3TopLevelRenderWindow(Process process, IntPtr mainWindowHandle)
    {
        IntPtr bestHandle = IntPtr.Zero;
        long bestScore = long.MinValue;

        foreach (var hwnd in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true, fallbackTitleHint: "RPCS3"))
        {
            var score = ScoreRpcs3CaptureWindow(hwnd, mainWindowHandle);
            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = hwnd;
            }
        }

        return bestHandle;
    }

    private static IntPtr FindBestRpcs3RenderChildWindow(Process process, IntPtr mainWindowHandle)
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

            Win32API.EnumChildWindows(parent, (child, _) =>
            {
                var score = ScoreRpcs3RenderChildWindow(child, processId, mainWindowHandle);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHwnd = child;
                }

                return true;
            }, IntPtr.Zero);
        }

        ProbeChildren(mainWindowHandle);

        foreach (var topLevel in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
            ProbeChildren(topLevel);

        return bestScore > long.MinValue ? bestHwnd : IntPtr.Zero;
    }

    private static long ScoreRpcs3RenderChildWindow(IntPtr hwnd, uint processId, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return long.MinValue;

        if (GetWindowThreadProcessId(hwnd, out var windowPid) == 0 || windowPid != processId)
            return long.MinValue;

        if (IsRpcs3DialogOrOverlayWindow(hwnd))
            return long.MinValue;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return long.MinValue;

        if (width < MinRenderCandidateWidth || height < MinRenderCandidateHeight)
            return long.MinValue;

        var className = GetWindowClassName(hwnd).Trim().ToLowerInvariant();
        if (className.Contains("ime") ||
            className.Contains("tooltip") ||
            className.Contains("titlebar"))
        {
            return long.MinValue;
        }

        long score = (long)width * height * 10;
        if (IsWindowVisible(hwnd))
            score += 500_000;

        if (className.Contains("qt") || className.Contains("vulkan") || className.Contains("opengl"))
            score += 5_000_000;

        if (width >= MinConstructedClientWidth && height >= MinConstructedClientHeight)
            score += 25_000_000;

        return score;
    }

    private static long ScoreRpcs3CaptureWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyRpcs3RenderWindow(hwnd, mainWindowHandle))
            return long.MinValue;

        if (IsRpcs3DialogOrOverlayWindow(hwnd))
            return long.MinValue;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return long.MinValue;

        long score = (long)width * height * 10;

        if (IsWindowVisible(hwnd))
            score += 500_000;

        if (width >= MinConstructedClientWidth && height >= MinConstructedClientHeight)
            score += 50_000_000;

        if (width >= 1920 && height >= 1080)
            score += 100_000_000;

        return score;
    }

    private static void HideNonTargetRpcs3Windows(Process process, IntPtr targetHwnd)
    {
        foreach (var hwnd in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
        {
            if (hwnd == IntPtr.Zero || hwnd == targetHwnd)
                continue;

            if (!ShouldHideAuxiliaryRpcs3Window(hwnd))
                continue;

            try
            {
                HideWindowForCapture(hwnd);
            }
            catch (Exception logEx) { Log.Warn("Non-critical error", logEx); }
        }
    }

    private static bool ShouldHideAuxiliaryRpcs3Window(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        if (IsRpcs3DialogOrOverlayWindow(hwnd))
            return true;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var width, out var height))
            return false;

        return width < MinStableClientWidth || height < MinStableClientHeight;
    }

    private static bool IsRpcs3DialogOrOverlayWindow(IntPtr hwnd)
    {
        var title = GetWindowTitle(hwnd).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var lowerTitle = title.ToLowerInvariant();

        return lowerTitle.Contains("trophy") ||
               lowerTitle.Contains("checking") ||
               lowerTitle.Contains("please wait") ||
               lowerTitle.Contains("please do not turn off") ||
               lowerTitle.Contains("do not turn off") ||
               lowerTitle.Contains("pipeline object") ||
               lowerTitle.Contains("compiling pipeline") ||
               lowerTitle.Contains("loading pipeline") ||
               lowerTitle.Contains("shader") ||
               lowerTitle.Contains("preloading") ||
               lowerTitle.Contains("installing");
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static bool IsLikelyRpcs3RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        if (IsRpcs3DialogOrOverlayWindow(hwnd))
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).Trim();
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();
        var style = GetWindowStyle(hwnd);
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasChildStyle = (style & WS_CHILD) == WS_CHILD;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (hasChildStyle)
            return false;

        if (IsRpcs3UiWindow(lowerTitle))
            looksLikePrimaryUi = true;

        if (!Win32API.TryGetWindowClientSize(hwnd, out var clientWidth, out var clientHeight))
            return false;

        if (clientWidth < MinRenderCandidateWidth || clientHeight < MinRenderCandidateHeight)
            return false;

        if (lowerTitle.StartsWith("fps:", StringComparison.OrdinalIgnoreCase) ||
            lowerTitle.Contains("vulkan") ||
            lowerTitle.Contains("opengl") ||
            lowerTitle.Contains("render") ||
            lowerTitle.Contains("gpu") ||
            lowerTitle.Contains("swapchain"))
        {
            return true;
        }

        if (lowerTitle.Contains("rpcs3") && !looksLikePrimaryUi && !hasCaption)
            return true;

        // gs_frame: separate QWindow with the game title (and optional FPS in formatted title).
        if (!looksLikePrimaryUi &&
            !string.IsNullOrWhiteSpace(title) &&
            (lowerClass.Contains("qt") || lowerClass.Contains("qwindow")))
        {
            return true;
        }

        if (!looksLikePrimaryUi &&
            clientWidth >= MinStableClientWidth &&
            clientHeight >= MinStableClientHeight &&
            !string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

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
               lowerTitle.Contains("launcher") ||
               lowerTitle.StartsWith("rpcs3 ", StringComparison.OrdinalIgnoreCase) ||
               lowerTitle.Contains("master") ||
               lowerTitle.Contains("alpha");
    }

    private static void EnsurePauseOnFocusLossDisabled(string? executablePath, string? workingDirectory)
    {
        try
        {
            var baseDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(executablePath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
                return;

            var configPath = Path.Combine(baseDirectory, "config.yml");
            if (!File.Exists(configPath))
                return;

            var lines = File.ReadAllLines(configPath);
            var modified = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("Pause emulation on RPCS3 focus loss", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Contains(':'))
                {
                    var newLine = "  Pause emulation on RPCS3 focus loss: false";
                    if (!string.Equals(lines[i], newLine, StringComparison.Ordinal))
                    {
                        lines[i] = newLine;
                        modified = true;
                    }
                }
            }

            if (modified)
                File.WriteAllLines(configPath, lines);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    [DllImport("user32.dll")]
    private static new extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
