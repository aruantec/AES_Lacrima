using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchGbaHandler : EmulatorHandlerBase
{
    public static RetroArchGbaHandler Instance { get; } = new();

    private RetroArchGbaHandler()
    {
    }

    public override string HandlerId => "retroarch-gba";

    public override string SectionKey => "GBA";

    public override string SectionTitle => "Game Boy Advance";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;


    public override bool UsesRetroArchCores => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "GBA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Game Boy Advance", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
        => RetroArchHandler.BuildRetroArchStartInfo(launcherPath, romPath, startFullscreen, sectionTitle ?? SectionTitle, selectedRetroArchCore);
}
