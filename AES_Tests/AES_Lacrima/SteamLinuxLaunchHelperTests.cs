using System.Diagnostics;
using AES_Lacrima.Services.Steam;

namespace AES_Tests.AES_Lacrima;

public sealed class SteamLinuxLaunchHelperTests
{
    [Fact]
    public void TryResolveProtonDirectory_reads_config_info_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes-steam-launch-" + Guid.NewGuid().ToString("N"));
        var protonDirectory = Path.Combine(root, "steamapps", "common", "Proton - Experimental");
        var configDirectory = Path.Combine(root, "steamapps", "compatdata", "2235020");
        Directory.CreateDirectory(protonDirectory);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(protonDirectory, "proton"), string.Empty);
        File.WriteAllText(
            Path.Combine(configDirectory, "config_info"),
            "11.0-100\n" +
            $"{protonDirectory}/files/share/fonts/\n" +
            $"{protonDirectory}/files/lib/\n" +
            $"{root}\n");

        try
        {
            var resolved = SteamInstalledGameHelper.TryResolveProtonDirectory(root, "2235020");
            Assert.Equal(protonDirectory, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveGameExecutable_prefers_largest_root_exe()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes-steam-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "UnityCrashHandler64.exe"), new string('a', 32));
        File.WriteAllText(Path.Combine(root, "Game.exe"), new string('b', 64));

        try
        {
            Assert.True(
                SteamInstalledGameHelper.TryResolveGameExecutable(root, preferWindowsExecutable: true, out var executable));
            Assert.Equal(Path.Combine(root, "Game.exe"), executable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildLaunchPath_includes_core_system_directories()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var path = SteamLinuxLaunchHelper.BuildLaunchPath();
        Assert.Contains("/usr/bin", path, StringComparison.Ordinal);
        Assert.Contains("/bin", path, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSteamDotSteamDirectory_finds_snap_layout()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var resolved = SteamClientIpcHelper.ResolveSteamDotSteamDirectory(home, libraryRoot);
        Assert.NotNull(resolved);
        Assert.True(Directory.Exists(resolved!));
        Assert.EndsWith(".steam", resolved!, StringComparison.Ordinal);
    }

    [Fact]
    public void TryPrepareDirectLaunch_builds_proton_start_info()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var snapManifest = Path.Combine(
            home,
            "snap",
            "steam",
            "common",
            ".local",
            "share",
            "Steam",
            "steamapps",
            "appmanifest_2235020.acf");
        if (!File.Exists(snapManifest))
            return;

        var startInfo = new ProcessStartInfo();
        var prepared = SteamLinuxLaunchHelper.TryPrepareDirectLaunch(startInfo, "%STEAM_APPID%:2235020");
        Assert.True(prepared);
        Assert.EndsWith("env", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(startInfo.ArgumentList, arg =>
            arg.StartsWith("STEAM_COMPAT_DATA_PATH=", StringComparison.Ordinal));
        Assert.Contains(startInfo.ArgumentList, arg =>
            arg == "SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD=1");
        Assert.Contains(startInfo.ArgumentList, arg =>
            arg.StartsWith("EnableConfiguratorSupport=", StringComparison.Ordinal));
        Assert.DoesNotContain(startInfo.ArgumentList, arg =>
            arg.StartsWith("LD_PRELOAD=", StringComparison.Ordinal));
        Assert.DoesNotContain(startInfo.ArgumentList, arg =>
            arg == "ENABLE_GAMESCOPE_WSI=0");
        Assert.Contains(startInfo.ArgumentList, arg =>
            arg.StartsWith("PATH=", StringComparison.Ordinal) &&
            arg.Contains("/usr/bin", StringComparison.Ordinal));
        Assert.Contains(startInfo.ArgumentList, arg =>
            arg.StartsWith("ENABLE_VK_LAYER_VALVE_steam_fossilize_1=", StringComparison.Ordinal));

        var runtimeIndex = startInfo.ArgumentList.ToList().FindIndex(arg =>
            arg.EndsWith("_v2-entry-point", StringComparison.Ordinal));
        if (runtimeIndex >= 0)
        {
            Assert.Equal("--verb=waitforexitandrun", startInfo.ArgumentList[runtimeIndex + 1]);
            Assert.Equal("--", startInfo.ArgumentList[runtimeIndex + 2]);
            Assert.Contains("Proton", startInfo.ArgumentList[runtimeIndex + 3], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("waitforexitandrun", startInfo.ArgumentList[runtimeIndex + 4]);
            Assert.Contains("ContraOG.exe", startInfo.ArgumentList[runtimeIndex + 5], StringComparison.OrdinalIgnoreCase);
            return;
        }

        var pythonIndex = startInfo.ArgumentList.ToList().FindIndex(arg =>
            arg.EndsWith("python3", StringComparison.Ordinal));
        Assert.True(pythonIndex >= 0);
        Assert.Contains("Proton", startInfo.ArgumentList[pythonIndex + 1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("waitforexitandrun", startInfo.ArgumentList[pythonIndex + 2]);
        Assert.Contains("ContraOG.exe", startInfo.ArgumentList[pythonIndex + 3], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProtonDirectoryForGame_prefers_per_game_override()
    {
        var root = CreateSteamLibraryRoot(out var experimentalDirectory, out var overrideDirectory, "366230");
        var game = new SteamInstalledGame(
            "366230",
            "BASEBALL STARS 2",
            Path.Combine(root, "steamapps", "common", "Baseball Stars 2"),
            root,
            null,
            SteamInstalledGameHelper.BuildGamePath("366230"));

        try
        {
            var preferences = new SteamProtonLaunchPreferences(
                defaultProtonDirectory: experimentalDirectory,
                gameOverrides: new Dictionary<string, string>
                {
                    ["366230"] = overrideDirectory
                });

            var resolved = SteamInstalledGameHelper.ResolveProtonDirectoryForGame(game, preferences);
            Assert.Equal(overrideDirectory, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveProtonDirectoryForGame_uses_config_info_before_global_default()
    {
        var root = CreateSteamLibraryRoot(out var experimentalDirectory, out var hotfixDirectory, "366230");
        var game = new SteamInstalledGame(
            "366230",
            "BASEBALL STARS 2",
            Path.Combine(root, "steamapps", "common", "Baseball Stars 2"),
            root,
            null,
            SteamInstalledGameHelper.BuildGamePath("366230"));

        try
        {
            var preferences = new SteamProtonLaunchPreferences(
                defaultProtonDirectory: hotfixDirectory,
                gameOverrides: null);

            var resolved = SteamInstalledGameHelper.ResolveProtonDirectoryForGame(game, preferences);
            Assert.Equal(experimentalDirectory, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveProtonDirectoryForGame_uses_global_default_when_config_info_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes-steam-proton-" + Guid.NewGuid().ToString("N"));
        var hotfixDirectory = Path.Combine(root, "steamapps", "common", "Proton Hotfix");
        Directory.CreateDirectory(hotfixDirectory);
        File.WriteAllText(Path.Combine(hotfixDirectory, "proton"), string.Empty);

        var game = new SteamInstalledGame(
            "366230",
            "BASEBALL STARS 2",
            Path.Combine(root, "steamapps", "common", "Baseball Stars 2"),
            root,
            null,
            SteamInstalledGameHelper.BuildGamePath("366230"));

        try
        {
            var preferences = new SteamProtonLaunchPreferences(
                defaultProtonDirectory: hotfixDirectory,
                gameOverrides: null);

            var resolved = SteamInstalledGameHelper.ResolveProtonDirectoryForGame(game, preferences);
            Assert.Equal(hotfixDirectory, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSteamLibraryRoot(
        out string configProtonDirectory,
        out string alternateProtonDirectory,
        string appId)
    {
        var root = Path.Combine(Path.GetTempPath(), "aes-steam-proton-" + Guid.NewGuid().ToString("N"));
        configProtonDirectory = Path.Combine(root, "steamapps", "common", "Proton - Experimental");
        alternateProtonDirectory = Path.Combine(root, "steamapps", "common", "Proton Hotfix");
        Directory.CreateDirectory(configProtonDirectory);
        Directory.CreateDirectory(alternateProtonDirectory);
        Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Baseball Stars 2"));
        File.WriteAllText(Path.Combine(configProtonDirectory, "proton"), string.Empty);
        File.WriteAllText(Path.Combine(alternateProtonDirectory, "proton"), string.Empty);
        File.WriteAllText(Path.Combine(root, "steamapps", "common", "Baseball Stars 2", "Game.exe"), "game");

        var configDirectory = Path.Combine(root, "steamapps", "compatdata", appId);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "config_info"),
            "11.0-100\n" +
            $"{configProtonDirectory}/files/share/fonts/\n" +
            $"{configProtonDirectory}/files/lib/\n" +
            $"{root}\n");

        return root;
    }

    [Fact]
    public void EnsureSteamAppIdFile_writes_app_id_into_install_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "aes-steam-appid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            SteamLinuxLaunchHelper.EnsureSteamAppIdFile(root, "366230");
            var path = Path.Combine(root, "steam_appid.txt");
            Assert.True(File.Exists(path));
            Assert.Equal("366230", File.ReadAllText(path).Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveEntryPoint_finds_steam_linux_runtime_on_snap_steam()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var entryPoint = SteamRuntimeLaunchHelper.TryResolveEntryPoint(libraryRoot);
        Assert.NotNull(entryPoint);
        Assert.EndsWith("_v2-entry-point", entryPoint!, StringComparison.Ordinal);
        Assert.True(File.Exists(entryPoint!));
    }

    [Fact]
    public void TryResolveLaunchHome_uses_snap_common_home_for_snap_libraries()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var launchHome = SteamLinuxLaunchHelper.TryResolveLaunchHome(libraryRoot);
        Assert.NotNull(launchHome);
        Assert.Equal(Path.Combine(home, "snap", "steam", "common"), launchHome);
    }

    [Fact]
    public void BuildSnapLaunchEnvironmentAssignments_includes_gstreamer_for_snap_steam()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var assignments = SteamLinuxLaunchHelper.BuildSnapLaunchEnvironmentAssignments(libraryRoot).ToList();
        if (assignments.Count == 0)
            return;

        Assert.Contains(assignments, arg => arg == "DISABLE_WAYLAND=1");
        Assert.Contains(assignments, arg => arg.StartsWith("PRESSURE_VESSEL_APP_LD_LIBRARY_PATH=", StringComparison.Ordinal));
        Assert.Contains(assignments, arg => arg.StartsWith("STEAM_RUNTIME_LIBRARY_PATH=", StringComparison.Ordinal));
    }

    [Fact]
    public void TryResolveLaunchHome_uses_snap_common_home_for_external_libraries()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        if (!Directory.Exists(Path.Combine(home, "snap", "steam", "common")))
            return;

        var launchHome = SteamLinuxLaunchHelper.TryResolveLaunchHome("/run/media/example/Steam");
        Assert.NotNull(launchHome);
        Assert.Equal(Path.Combine(home, "snap", "steam", "common"), launchHome);
    }

    [Fact]
    public void HasLegacy32BitSteamApi_is_false_for_64bit_only_install()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "aes-steam-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "steam_api64.dll"), "stub");

        try
        {
            Assert.False(SteamReaperLaunchHelper.HasLegacy32BitSteamApi(installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void HasLegacy32BitSteamApi_is_true_for_32bit_only_install()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "aes-steam-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "steam_api.dll"), "stub");

        try
        {
            Assert.True(SteamReaperLaunchHelper.HasLegacy32BitSteamApi(installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void ShouldUseReaperLaunch_is_false_for_64bit_steam_api_games_on_snap()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var clientRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(clientRoot))
            return;

        if (!File.Exists(Path.Combine(clientRoot, "ubuntu12_32", "reaper")))
            return;

        var installDirectory = Path.Combine(Path.GetTempPath(), "aes-steam-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "steam_api64.dll"), "stub");

        try
        {
            Assert.False(SteamReaperLaunchHelper.ShouldUseReaperLaunch(clientRoot, installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void ShouldUseReaperLaunch_is_false_without_steam_api()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var clientRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(clientRoot))
            return;

        if (!File.Exists(Path.Combine(clientRoot, "ubuntu12_32", "reaper")))
            return;

        var installDirectory = Path.Combine(Path.GetTempPath(), "aes-steam-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);

        try
        {
            Assert.False(SteamReaperLaunchHelper.ShouldUseReaperLaunch(clientRoot, installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void ShouldUseReaperLaunch_is_true_for_32bit_steam_api_games_on_snap()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var clientRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(clientRoot))
            return;

        if (!File.Exists(Path.Combine(clientRoot, "ubuntu12_32", "reaper")))
            return;

        var installDirectory = Path.Combine(Path.GetTempPath(), "aes-steam-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(Path.Combine(installDirectory, "steam_api.dll"), "stub");

        try
        {
            Assert.True(SteamReaperLaunchHelper.ShouldUseReaperLaunch(clientRoot, installDirectory));
        }
        finally
        {
            Directory.Delete(installDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryAppendReaperLaunchArguments_builds_steam_client_wrapper_chain()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var libraryRoot = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        if (!Directory.Exists(libraryRoot))
            return;

        var runtimeEntryPoint = SteamRuntimeLaunchHelper.TryResolveEntryPoint(libraryRoot);
        if (string.IsNullOrWhiteSpace(runtimeEntryPoint))
            return;

        var args = new List<string>();
        var appended = SteamReaperLaunchHelper.TryAppendReaperLaunchArguments(
            args,
            libraryRoot,
            "366230",
            runtimeEntryPoint,
            "/tmp/proton",
            "/tmp/bstars2.exe");

        Assert.True(appended);
        Assert.Contains(args, arg => arg.EndsWith("steam-launch-wrapper", StringComparison.Ordinal));
        Assert.Contains(args, arg => arg.EndsWith("reaper", StringComparison.Ordinal));
        Assert.Contains(args, arg => arg == "SteamLaunch AppId=366230");
        Assert.Equal("--verb=waitforexitandrun", args[args.IndexOf(runtimeEntryPoint) + 1]);
    }
}
