using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchSaturnHandler : EmulatorHandlerBase
{
    public static RetroArchSaturnHandler Instance { get; } = new();

    private RetroArchSaturnHandler()
    {
    }

    public override string HandlerId => "retroarch-saturn";

    public override string SectionKey => "SATURN";

    public override string SectionTitle => "Sega Saturn";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;


    public override bool UsesRetroArchCores => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Saturn", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
        => RetroArchHandler.BuildRetroArchStartInfo(launcherPath, romPath, startFullscreen, sectionTitle ?? SectionTitle, selectedRetroArchCore);
}
