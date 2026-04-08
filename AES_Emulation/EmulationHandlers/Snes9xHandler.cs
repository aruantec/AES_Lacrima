using System;
using System.Diagnostics;
using AES_Emulation.Windows.API;

namespace AES_Emulation.EmulationHandlers;

public sealed class Snes9xHandler : EmulatorHandlerBase
{
    public static Snes9xHandler Instance { get; } = new();

    private Snes9xHandler()
    {
    }

    public override string HandlerId => "snes9x";

    public override string SectionKey => "SNES";

    public override string SectionTitle => "Super Nintendo";

    public override string DisplayName => "Snes9x";

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Snes9x", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Super Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        
        // Snes9x should run windowed for better capture integration.
        // If we needed fullscreen, we would use startInfo.ArgumentList.Add("-fullscreen");

        startInfo.ArgumentList.Add(romPath);

        return startInfo;
    }

    // Snes9x usually starts quickly but needs a moment for the window to settle.
    public override int CaptureStartupDelayMs => 500;

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        // Snes9x typically uses a single main window for emulation.
        // We can use the default implementation or refine it if needed.
        return FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: IsLikelySnes9xWindow);
    }

    private static bool IsLikelySnes9xWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd);
        // Snes9x window title typically contains "Snes9x"
        return title.Contains("Snes9x", StringComparison.OrdinalIgnoreCase);
    }
}
