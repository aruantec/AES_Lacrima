using System;
using System.Diagnostics;
using System.IO;
using AES_Core.Logging;
using log4net;
using AES_Emulation.Controls;
using AES_Emulation.Windows.API;

namespace AES_Emulation.EmulationHandlers;

public sealed class EdenHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<EdenHandler>();

    public static EdenHandler Instance { get; } = new();

    private EdenHandler()
    {
    }

    public override string HandlerId => "eden";

    public override string SectionKey => "SWITCH";

    public override string SectionTitle => "Nintendo Switch";

    public override string DisplayName => "Eden";

    public override bool HideUntilCaptured => false;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            Log.Warn($"Eden launcher path does not exist: '{launcherPath}'");

        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
            Log.Warn($"Eden ROM path does not exist: '{romPath}'");

        var launcherName = Path.GetFileNameWithoutExtension(launcherPath)?.ToLowerInvariant() ?? string.Empty;
        var isCli = launcherName.Contains("eden-cli") || launcherName.Contains("edencli");

        if (isCli)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        if (isCli)
        {
            if (startFullscreen)
                startInfo.ArgumentList.Add("--fullscreen");

            startInfo.ArgumentList.Add("--game");
            startInfo.ArgumentList.Add(romPath);
        }
        else
        {
            if (startFullscreen)
                startInfo.ArgumentList.Add("-f");

            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(romPath);
        }

        Log.Debug($"Eden start info: FileName='{startInfo.FileName}', WorkingDirectory='{startInfo.WorkingDirectory}', Arguments='{string.Join(' ', startInfo.ArgumentList)}', UseShellExecute={startInfo.UseShellExecute}");
        return startInfo;
    }

    public override int CaptureStartupDelayMs => 0;

    public override void PrepareProcessForCapture(Process process)
    {
        // Intentionally no-op for Eden because hiding/moving the window can destabilize it.
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        // Intentionally no-op for Eden.
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: true, allowHiddenWindows: true, isPreferredRenderWindow: IsLikelyEdenRenderWindow, fallbackTitleHint: DisplayName);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyEdenRenderWindow(hwnd, mainWindowHandle);

    private static bool IsLikelyEdenRenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
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
            if (lowerTitle.Contains("eden") && !lowerTitle.Contains("settings") && !lowerTitle.Contains("audio") && !lowerTitle.Contains("video") && !lowerTitle.Contains("input") && !lowerTitle.Contains("controller") && !lowerTitle.Contains("cheat") && !lowerTitle.Contains("shader") && !lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = false;
            }

            if (lowerTitle.Contains("settings") || lowerTitle.Contains("audio") || lowerTitle.Contains("video") || lowerTitle.Contains("input") || lowerTitle.Contains("controller") || lowerTitle.Contains("cheat") || lowerTitle.Contains("shader") || lowerTitle.Contains("about"))
            {
                looksLikePrimaryUi = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(lowerClass))
        {
            if ((lowerClass.Contains("sdl") || lowerClass.Contains("glfw") || lowerClass.Contains("qt") || lowerClass.Contains("eden")) && hasCaption && hasThickFrame)
            {
                looksLikePrimaryUi |= true;
            }
        }

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(lowerTitle));
    }
}
