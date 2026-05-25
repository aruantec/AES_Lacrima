using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AES_Emulation.Windows.API;

using log4net;
using AES_Core.Logging;
namespace AES_Emulation.EmulationHandlers;

public sealed class Pcsx2Handler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<Pcsx2Handler>();
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

        EnsurePauseOnFocusLossDisabled(startInfo.FileName, startInfo.WorkingDirectory);

        // PCSX2 Qt supports batch mode and optional fullscreen startup.
        // `-nogui` reduces chances of capturing the full shell window instead of the render surface.
        // `-portable` keeps settings/config within the emulator folder.
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-nogui");
        startInfo.ArgumentList.Add("-portable");
        if (startFullscreen)
            startInfo.ArgumentList.Add("-fullscreen");

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }


    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for PCSX2.
        // Aggressively hiding/moving windows during target resolution can race with
        // Qt window creation and make capture assignment inconsistent.
        // The capture control hides/restores the selected target once session creation begins.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for PCSX2; see PrepareProcessForCapture.
    }

    public override int CaptureStartupDelayMs => 200;

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 120;
        const int delayMs = 40;
        const int stableAttemptsBeforeAssign = 6;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;

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

            var hwnd = FindFallbackQtGameWindow(process);
            if (hwnd == IntPtr.Zero)
                hwnd = FindPreferredWindowHandle(process);

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
                    return hwnd;
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
            return targetHwnd;

        // Fallback for recent Qt builds where MainWindowHandle/title stabilization can lag.
        return FindFallbackQtGameWindow(process);
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyPcsx2RenderWindow);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyPcsx2RenderWindow(hwnd, mainWindowHandle);

    private static bool IsStableCaptureCandidate(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (!IsLikelyPcsx2RenderWindow(hwnd, mainWindowHandle))
            return false;

        if (IsIconic(hwnd))
            return false;

        if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
            return false;

        if (width < 640 || height < 360)
            return false;

        return true;
    }

    private static bool IsLikelyPcsx2RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd);
        var style = GetWindowStyle(hwnd);
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        if (lowerTitle.Contains("_q_titlebar") ||
            lowerTitle.Contains("msctfime ui") ||
            lowerTitle.Contains("default ime") ||
            lowerClass.Contains("screenchangeobserver") ||
            lowerClass.Contains("themechangeobserver") ||
            lowerClass.Contains("ime"))
        {
            return false;
        }

        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var hasThickFrame = (style & WS_THICKFRAME) == WS_THICKFRAME;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        if (!string.IsNullOrWhiteSpace(title))
        {
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
            // Typical game windows have their own title (e.g. ROM/game name).
            if (!isClearlyUiTitle && lowerTitle.Length >= 2)
                return true;
        }

        if (!string.IsNullOrWhiteSpace(className))
        {
            if (lowerClass.Contains("pcsx2") && hasCaption && hasThickFrame)
                looksLikePrimaryUi |= hasCaption && hasThickFrame;

            // PCSX2 Qt window class names often look like "Qt6110QWindowIcon".
            // If we have a Qt top-level window that is not clearly the UI shell,
            // accept it as a candidate.
            if (!looksLikePrimaryUi && lowerClass.Contains("qt") && !lowerTitle.Contains("pcsx2"))
                return true;
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
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

            if (!IsLikelyPcsx2RenderWindow(hwnd, mainWindowHandle))
                continue;

            if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var width, out var height))
                continue;

            if (width < 480 || height < 270)
                continue;

            var title = GetWindowTitle(hwnd).Trim();
            var score = (long)width * height;

            if (!string.IsNullOrWhiteSpace(title))
                score += 500_000;

            if (!title.Contains("pcsx2", StringComparison.OrdinalIgnoreCase))
                score += 350_000;

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
        }

        return best;
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

            var settingsPath = Path.Combine(baseDirectory, "inis", "PCSX2.ini");
            if (!File.Exists(settingsPath))
                return;

            var lines = File.ReadAllLines(settingsPath);
            var modified = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("PauseOnFocusLoss", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Contains('='))
                {
                    var newLine = "PauseOnFocusLoss = false";
                    if (!string.Equals(lines[i].Trim(), newLine, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = newLine;
                        modified = true;
                    }
                }
            }

            if (modified)
                File.WriteAllLines(settingsPath, lines);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    [DllImport("user32.dll")]
    private static new extern bool IsIconic(IntPtr hWnd);
}
