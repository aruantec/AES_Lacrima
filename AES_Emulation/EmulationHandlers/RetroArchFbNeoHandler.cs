using System;
using System.Diagnostics;

namespace AES_Emulation.EmulationHandlers;

public sealed class RetroArchFbNeoHandler : EmulatorHandlerBase
{
    public static RetroArchFbNeoHandler Instance { get; } = new();

    private RetroArchFbNeoHandler()
    {
    }

    public override string HandlerId => "retroarch-fbn";

    public override string SectionKey => "FBN";

    public override string SectionTitle => "Final Burn Neo";

    public override string DisplayName => "RetroArch";

    public override bool HideUntilCaptured => true;


    public override bool UsesRetroArchCores => true;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FBNeo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Final Burn Neo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "FBN", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
        => RetroArchHandler.BuildRetroArchStartInfo(launcherPath, romPath, startFullscreen, sectionTitle ?? SectionTitle, selectedRetroArchCore, FlatpakAppId);
}
