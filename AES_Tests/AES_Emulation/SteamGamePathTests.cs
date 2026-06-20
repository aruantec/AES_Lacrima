using AES_Emulation.Steam;

namespace AES_Tests.AES_Emulation;

public sealed class SteamGamePathTests
{
    [Fact]
    public void Build_and_GetAppId_roundtrip()
    {
        var path = SteamGamePath.Build("570");
        Assert.Equal("%STEAM_APPID%:570", path);
        Assert.Equal("570", SteamGamePath.GetAppId(path));
        Assert.True(SteamGamePath.IsSteamGamePath(path));
    }

    [Fact]
    public void GetAppId_rejects_invalid_paths()
    {
        Assert.Null(SteamGamePath.GetAppId(null));
        Assert.Null(SteamGamePath.GetAppId("/game/bin"));
        Assert.Null(SteamGamePath.GetAppId("%STEAM_APPID%:abc"));
    }

    [Fact]
    public void GetAppId_reads_embedded_virtual_path_after_GetFullPath()
    {
        var corrupted = Path.Combine("/tmp/aes", "%STEAM_APPID%:2235020");
        Assert.Equal("2235020", SteamGamePath.GetAppId(corrupted));
        Assert.True(SteamGamePath.IsSteamGamePath(corrupted));
        Assert.Equal("%STEAM_APPID%:2235020", SteamGamePath.NormalizeVirtualPath(corrupted));
    }
}
