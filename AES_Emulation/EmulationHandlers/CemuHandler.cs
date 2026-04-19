using AES_Emulation.Windows.API;
using AES_Emulation.Controls;
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

    public override bool HideUntilCaptured => true;

    public override bool ForceUseTargetClientAreaCapture => true;

    public override int CaptureStartupDelayMs => 2000;

    public override EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

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
        return await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public override void PrepareWindowForCapture(IntPtr hwnd) => HideWindowForCapture(hwnd);

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        return FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: null);
    }

}
