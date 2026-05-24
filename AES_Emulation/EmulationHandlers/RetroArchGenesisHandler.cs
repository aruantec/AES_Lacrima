using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchGenesisHandler : EmulatorHandlerBase
{
    public static RetroArchGenesisHandler Instance { get; } = new();

    private RetroArchGenesisHandler()
    {
    }

    public override string HandlerId => "retroarch-genesis";

    public override string SectionKey => "GENESIS";

    public override string SectionTitle => "Sega Genesis";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;


    public override bool UsesRetroArchCores => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Genesis", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Mega Drive", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "MegaDrive", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "MD", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
        => RetroArchHandler.BuildRetroArchStartInfo(launcherPath, romPath, startFullscreen, sectionTitle ?? SectionTitle, selectedRetroArchCore);
}
