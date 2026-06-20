using AES_Lacrima.Services;
using AES_Lacrima.Services.Steam;

namespace AES_Tests.AES_Lacrima;

public sealed class SteamInstalledGameHelperTests
{
    [Fact]
    public void GetInstalledGames_returns_empty_on_non_linux()
    {
        if (OperatingSystem.IsLinux())
            return;

        Assert.Empty(SteamInstalledGameHelper.GetInstalledGames());
    }

    [Fact]
    public void IsSteamSection_matches_steam_assets_and_titles()
    {
        Assert.True(EmulationConsoleCatalog.IsSteamSection("Steam"));
        Assert.True(EmulationConsoleCatalog.IsSteamSection("Steam.png"));
        Assert.True(EmulationConsoleCatalog.UsesAutoLibrarySync("Steam"));
        Assert.True(EmulationConsoleCatalog.IsLinuxOnlySection("Steam"));
        Assert.False(EmulationConsoleCatalog.IsSteamSection("PlayStation 2"));
    }

    [Fact]
    public void IsConsoleAssetAvailableOnCurrentPlatform_hides_steam_off_linux()
    {
        var steamAsset = "/tmp/Assets/Consoles/Steam.png";
        if (OperatingSystem.IsLinux())
            Assert.True(EmulationConsoleCatalog.IsConsoleAssetAvailableOnCurrentPlatform(steamAsset));
        else
            Assert.False(EmulationConsoleCatalog.IsConsoleAssetAvailableOnCurrentPlatform(steamAsset));
    }

    [Fact]
    public void BuildSteamRootCandidates_includes_snap_steam_path()
    {
        var home = "/home/testuser";
        var candidates = SteamInstalledGameHelper.BuildSteamRootCandidates(home).ToList();

        Assert.Contains(
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
            candidates,
            StringComparer.Ordinal);
    }

    [Fact]
    public void ParseFile_reads_libraryfolders_root_wrapper()
    {
        using var tempDir = new TempDirectory();
        var vdfPath = Path.Combine(tempDir.Path, "libraryfolders.vdf");
        File.WriteAllText(vdfPath,
            """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"/steam/library"
            	}
            }
            """);

        var parsed = SteamVdfParser.ParseFile(vdfPath);
        Assert.NotNull(parsed);

        var paths = SteamVdfParser.CollectStringValues(parsed, "path").ToList();
        Assert.Contains("/steam/library", paths);
    }

    [Theory]
    [InlineData("Proton Hotfix", "Proton Hotfix", true)]
    [InlineData("Proton - Experimental", "Proton Experimental", true)]
    [InlineData("SteamLinuxRuntime_4", "Steam Linux Runtime 4.0", true)]
    [InlineData("Steamworks Shared", "Steamworks Common Redistributables", true)]
    [InlineData("BASEBALL STARS 2", "BASEBALL STARS 2", false)]
    [InlineData("RE3", "Resident Evil 3", false)]
    public void IsIgnoredSteamToolInstall_filters_steam_tools_but_not_games(
        string installDir,
        string name,
        bool expectedIgnored)
    {
        Assert.Equal(expectedIgnored, SteamInstalledGameHelper.IsIgnoredSteamToolInstall(installDir, name));
    }

    [Fact]
    public void ResolveIconPath_finds_modern_appcache_directory_layout()
    {
        using var tempDir = new TempDirectory();
        var appId = "1887840";
        var cacheDir = Path.Combine(tempDir.Path, "appcache", "librarycache", appId);
        Directory.CreateDirectory(cacheDir);
        var iconPath = Path.Combine(cacheDir, "library_600x900.jpg");
        File.WriteAllBytes(iconPath, [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00]);

        var resolved = SteamInstalledGameHelper.ResolveIconPath(tempDir.Path, appId);

        Assert.Equal(iconPath, resolved);
    }

    [Fact]
    public void ResolveIconPath_finds_nested_library_header_layout()
    {
        using var tempDir = new TempDirectory();
        var appId = "3357650";
        var nestedDir = Path.Combine(tempDir.Path, "appcache", "librarycache", appId, "abc123");
        Directory.CreateDirectory(nestedDir);
        var iconPath = Path.Combine(nestedDir, "library_header.jpg");
        File.WriteAllBytes(iconPath, [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00]);

        var resolved = SteamInstalledGameHelper.ResolveIconPath(tempDir.Path, appId);

        Assert.Equal(iconPath, resolved);
    }

    [Fact]
    public void GetPreferredIconPath_finds_requiem_icon_when_installed()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var iconPath = SteamInstalledGameHelper.GetPreferredIconPath(
            SteamInstalledGameHelper.BuildGamePath("3764200"));

        if (string.IsNullOrWhiteSpace(iconPath))
            return;

        Assert.True(File.Exists(iconPath!));
    }

    [Fact]
    public void GetPreferredIconPath_finds_another_crabs_treasure_icon_when_installed()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var manifestPath = Path.Combine(
            home,
            "snap",
            "steam",
            "common",
            ".local",
            "share",
            "Steam",
            "steamapps",
            "appmanifest_1887840.acf");
        if (!File.Exists(manifestPath))
            return;

        var iconPath = SteamInstalledGameHelper.GetPreferredIconPath(SteamInstalledGameHelper.BuildGamePath("1887840"));

        Assert.False(string.IsNullOrWhiteSpace(iconPath));
        Assert.True(File.Exists(iconPath!));
    }

    [Fact]
    public void GetInstalledGames_finds_snap_steam_game_when_present()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return;

        var snapSteamApps = Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam", "steamapps");
        if (!Directory.Exists(snapSteamApps))
            return;

        var games = SteamInstalledGameHelper.GetInstalledGames();
        Assert.Contains(games, game => string.Equals(game.AppId, "2235020", StringComparison.Ordinal));
        Assert.Contains(games, game => game.Name.Contains("Contra", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(games, game => game.Name.Contains("Proton", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(games, game => game.Name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
