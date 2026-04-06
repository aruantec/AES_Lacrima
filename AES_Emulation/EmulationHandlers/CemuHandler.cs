using AES_Emulation.Windows.API;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class CemuHandler : EmulatorHandlerBase
{
    public static CemuHandler Instance { get; } = new();

    private CemuHandler()
    {
    }

    public override string HandlerId => "cemu";

    public override string SectionKey => "WIIU";

    public override string SectionTitle => "Wii U";

    public override string DisplayName => "Cemu";

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Wii U", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "WiiU", StringComparison.OrdinalIgnoreCase);
    }

    public override bool HideUntilCaptured => false;

    public override int CaptureStartupDelayMs => 0;

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        if (startFullscreen)
            startInfo.ArgumentList.Add("--fullscreen");

        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(romPath);

        return startInfo;
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        var hwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            Win32API.SetWindowSize(hwnd, 1920, 1080);
        }
        catch
        {
            // best effort only
        }

        return hwnd;
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: null);

}
