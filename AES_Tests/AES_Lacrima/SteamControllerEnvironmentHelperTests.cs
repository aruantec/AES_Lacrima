using AES_Lacrima.Services.Steam;

namespace AES_Tests.AES_Lacrima;

public sealed class SteamControllerEnvironmentHelperTests
{
    [Fact]
    public void BuildEnvironmentAssignments_includes_steam_input_vars_without_disabling_wsi()
    {
        var assignments = SteamControllerEnvironmentHelper.BuildEnvironmentAssignments(
            "/tmp/steam",
            "952060",
            "/tmp/steam/steamapps/common/RE3",
            "/tmp/steam/steamapps/common/Proton - Experimental").ToList();

        Assert.Contains(assignments, arg => arg == "EnableConfiguratorSupport=4097");
        Assert.Contains(assignments, arg => arg == "SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1");
        Assert.Contains(assignments, arg => arg == "STEAM_COMPAT_PROTON=1");
        Assert.Contains(assignments, arg => arg == "STEAM_COMPAT_FLAGS=search-cwd");
        Assert.Contains(assignments, arg => arg == "STEAM_COMPAT_INSTALL_PATH=/tmp/steam/steamapps/common/RE3");
        Assert.Contains(
            assignments,
            arg => arg == "STEAM_COMPAT_LIBRARY_PATHS=/tmp/steam/steamapps");
        Assert.Contains(assignments, arg => arg == "STEAM_BASE_FOLDER=/tmp/steam");
        Assert.Contains(
            assignments,
            arg => arg == "SRT_LAUNCHER_SERVICE_ALONGSIDE_STEAM=com.steampowered.PressureVessel.LaunchAlongsideSteam");
        Assert.DoesNotContain(assignments, arg => arg == "ENABLE_GAMESCOPE_WSI=0");
        Assert.DoesNotContain(assignments, arg => arg.StartsWith("SDL_GAMECONTROLLER_IGNORE_DEVICES=", StringComparison.Ordinal));
        Assert.DoesNotContain(assignments, arg => arg.StartsWith("LD_PRELOAD=", StringComparison.Ordinal));
    }

    [Fact]
    public void TryResolveGameOverlayRendererPreload_returns_steam_overlay_paths()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var preload = SteamControllerEnvironmentHelper.TryResolveGameOverlayRendererPreload(libraryRoot);
        Assert.NotNull(preload);
        Assert.Contains("gameoverlayrenderer.so", preload!, StringComparison.Ordinal);
        Assert.StartsWith(":", preload!);
    }

    [Fact]
    public void BuildEnvironmentAssignments_can_apply_steam_input_device_filters()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var assignments = SteamControllerEnvironmentHelper.BuildEnvironmentAssignments(
            libraryRoot,
            "1402120",
            Path.Combine(libraryRoot, "steamapps", "common", "9 Years of Shadows"),
            Path.Combine(libraryRoot, "steamapps", "common", "Proton - Experimental"),
            applySteamInputDeviceFilters: true).ToList();

        Assert.Contains(
            assignments,
            arg => arg.StartsWith("SDL_GAMECONTROLLER_IGNORE_DEVICES=", StringComparison.Ordinal));
        Assert.Contains(
            assignments,
            arg => arg.StartsWith("LD_PRELOAD=", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCompatMounts_includes_proton_and_runtime_directories()
    {
        var mounts = SteamControllerEnvironmentHelper.BuildCompatMounts(
            "/tmp/steam",
            "/tmp/steam/steamapps/common/Proton - Experimental");

        Assert.Equal(
            "/tmp/steam/steamapps/common/Proton - Experimental:/tmp/steam/steamapps/common/SteamLinuxRuntime_4",
            mounts);
    }

    [Fact]
    public void TryResolveSdlGameControllerIgnoreDevices_falls_back_to_playstation_devices()
    {
        var resolved = SteamControllerEnvironmentHelper.TryResolveSdlGameControllerIgnoreDevices("/tmp/no-steam", "123");
        Assert.Equal(SteamControllerEnvironmentHelper.DefaultSteamInputIgnoreDevices, resolved);
    }

    [Fact]
    public void ParseIgnoreDevicesFromRuntimeLog_reads_pressure_vessel_line()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "aes-steam-slr-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(
            logPath,
            "<7>01:00:15.043855: pressure-vessel-wrap[420403]: D: \t'SDL_GAMECONTROLLER_IGNORE_DEVICES=0x054c/0x0df2,0x28de/0x1205'\n");

        try
        {
            var parsed = SteamControllerEnvironmentHelper.ParseIgnoreDevicesFromRuntimeLog(logPath);
            Assert.Equal("0x054c/0x0df2,0x28de/0x1205", parsed);
        }
        finally
        {
            File.Delete(logPath);
        }
    }
}
