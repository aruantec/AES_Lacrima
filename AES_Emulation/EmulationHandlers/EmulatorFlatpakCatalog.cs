using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Emulation.EmulationHandlers;

public static class EmulatorFlatpakCatalog
{
    private sealed record HandlerProfile(string[] KnownApplicationIds, string[] Keywords);

    private static readonly Dictionary<string, HandlerProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dolphin"] = new(["org.DolphinEmu.dolphin-emu"], ["dolphin"]),
        ["pcsx2"] = new(["net.pcsx2.PCSX2"], ["pcsx2"]),
        ["rpcs3"] = new(["net.rpcs3.RPCS3"], ["rpcs3"]),
        ["duckstation"] = new([], ["duckstation"]),
        ["flycast"] = new(["org.flycast.Flycast"], ["flycast"]),
        ["retroarch"] = new(["org.libretro.RetroArch"], ["retroarch", "libretro"]),
        ["retroarch-gba"] = new(["org.libretro.RetroArch"], ["retroarch", "libretro"]),
        ["retroarch-genesis"] = new(["org.libretro.RetroArch"], ["retroarch", "libretro"]),
        ["retroarch-saturn"] = new(["org.libretro.RetroArch"], ["retroarch", "libretro"]),
        ["eden"] = new([], ["eden", "yuzu"]),
        ["shadps4-qtlauncher"] = new(["net.shadps4.shadPS4"], ["shadps4", "shadps4"]),
        ["xenia"] = new([], ["xenia"]),
        ["xemu"] = new([], ["xemu"]),
        ["cemu"] = new(["info.cemu.Cemu"], ["cemu"]),
        ["ares"] = new(["dev.ares.ares"], ["ares"]),
        ["snes9x"] = new(["com.snes9x.Snes9x"], ["snes9x"]),
        ["steam"] = new(["com.valvesoftware.Steam"], ["steam", "valve"]),
        ["redream"] = new([], ["redream"]),
        ["ymir"] = new([], ["ymir", "ymir-sdl3"]),
        ["fbneo"] = new([], ["fbneo", "fightcade", "finalburn"]),
        ["default"] = new([], []),
    };

    public static bool IsCompatibleApplicationId(string handlerId, string? applicationId)
    {
        if (string.IsNullOrWhiteSpace(handlerId) || string.IsNullOrWhiteSpace(applicationId))
            return false;

        if (!Profiles.TryGetValue(handlerId, out var profile))
            return false;

        var knownIds = new HashSet<string>(profile.KnownApplicationIds, StringComparer.OrdinalIgnoreCase);
        var app = new FlatpakApplicationItem(applicationId, applicationId, null);
        return MatchesProfile(app, profile, knownIds);
    }

    public static IReadOnlyList<FlatpakApplicationItem> BuildSelectionList(
        string handlerId,
        IReadOnlyList<FlatpakApplicationItem> installedApplications)
    {
        var filtered = FilterInstalledApplications(handlerId, installedApplications);
        var results = new List<FlatpakApplicationItem> { FlatpakApplicationItem.Empty };
        results.AddRange(filtered);
        return results;
    }

    public static IReadOnlyList<FlatpakApplicationItem> FilterInstalledApplications(
        string handlerId,
        IReadOnlyList<FlatpakApplicationItem> installedApplications)
    {
        if (installedApplications.Count == 0)
            return [];

        if (!Profiles.TryGetValue(handlerId, out var profile))
            return [];

        var knownIds = new HashSet<string>(profile.KnownApplicationIds, StringComparer.OrdinalIgnoreCase);
        var matches = installedApplications
            .Where(app => MatchesProfile(app, profile, knownIds))
            .OrderBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matches;
    }

    private static bool MatchesProfile(
        FlatpakApplicationItem app,
        HandlerProfile profile,
        HashSet<string> knownIds)
    {
        if (knownIds.Contains(app.ApplicationId))
            return true;

        if (profile.Keywords.Length == 0)
            return false;

        var haystack = $"{app.ApplicationId} {app.DisplayName}";
        return profile.Keywords.Any(keyword =>
            haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
