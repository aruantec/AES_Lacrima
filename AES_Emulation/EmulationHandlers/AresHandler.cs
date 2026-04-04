using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class AresHandler : EmulatorHandlerBase
{
    public static AresHandler Instance { get; } = new();

    private AresHandler()
    {
    }

    public override string HandlerId => "ares";

    public override string SectionKey => "ARES";

    public override string SectionTitle => "Ares";

    public override string DisplayName => "Ares";

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "SNES", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Super Nintendo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Super Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "NES", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "N64", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo 64", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen);

        if (startFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        return startInfo;
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        return await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }
}
