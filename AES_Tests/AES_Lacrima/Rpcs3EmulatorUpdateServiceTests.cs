using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class Rpcs3EmulatorUpdateServiceTests
{
    [Fact]
    public void IsRpcs3EmulationKingPlatformLink_AcceptsLinuxAndWindowsLinks()
    {
        Assert.True(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingPlatformLink(
            "RPCS3(0.0.41-19462 14/06/2026) [Nightly]for Linux"));
        Assert.True(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingPlatformLink(
            "RPCS3(0.0.41-19461 13/06/2026) [Nightly]for Windows"));
        Assert.False(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingPlatformLink(
            "Download RPCS3 from the official site"));
    }

    [Fact]
    public void IsRpcs3EmulationKingAssetName_AcceptsLinuxAppImageAndWindowsArchive()
    {
        Assert.True(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingAssetName(
            "rpcs3-v0.0.41-19462-23b414ac_linux64_nightly.AppImage"));
        Assert.True(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingAssetName(
            "rpcs3-v0.0.41-19461-5252f47a_win64_msvc_nightly.7z"));
        Assert.False(Rpcs3EmulatorUpdateService.IsRpcs3EmulationKingAssetName(
            "rpcs3-v0.0.41-19461-linux64_nightly.AppImage.zsync"));
    }

    [Fact]
    public void ParseEmulationKingReleaseSummaries_MergesLinuxAndWindowsAssetsPerVersion()
    {
        const string html = """
            <a href="https://files.emulationking.com/ps3/emulators/rpsc3/rpcs3-v0.0.41-19462-23b414ac_linux64_nightly.AppImage">
                RPCS3(0.0.41-19462 14/06/2026) <span>[Nightly]for Linux</span>
            </a>
            <a href="https://files.emulationking.com/ps3/emulators/rpsc3/rpcs3-v0.0.41-19462-aaaa_win64_msvc_nightly.7z">
                RPCS3(0.0.41-19462 14/06/2026) <span>[Nightly]for Windows</span>
            </a>
            <a href="https://files.emulationking.com/ps3/emulators/rpsc3/rpcs3-v0.0.41-19457-16a53dfe_linux64_nightly.AppImage">
                RPCS3(0.0.41-19457 13/06/2026) <span>[Nightly]for Linux</span>
            </a>
            """;

        var releases = Rpcs3EmulatorUpdateService.ParseEmulationKingReleaseSummaries(html);

        Assert.Equal(2, releases.Count);
        Assert.Equal("0.0.41-19462", releases[0].Tag);
        Assert.Contains(releases[0].AssetNames, name => name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(releases[0].AssetNames, name => name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("0.0.41-19457", releases[1].Tag);
        Assert.Single(releases[1].AssetNames);
        Assert.EndsWith(".AppImage", releases[1].AssetNames[0], StringComparison.OrdinalIgnoreCase);
    }
}
