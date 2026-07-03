using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class XeniaEmulatorUpdateServiceTests
{
    [Fact]
    public void BuildPkgforgeLinuxAssetName_builds_x86_64_appimage_from_atom_title()
    {
        var assetName = XeniaEmulatorUpdateService.BuildPkgforgeLinuxAssetName("Xenia_Canary: 02d2cb5");
        Assert.Equal("Xenia_Canary-02d2cb5-anylinux-x86_64.AppImage", assetName);
    }

    [Fact]
    public void IsXeniaWindowsDesktopAssetName_accepts_current_7z_release_asset()
    {
        Assert.True(XeniaEmulatorUpdateService.IsXeniaWindowsDesktopAssetName("xenia_canary_windows.7z"));
        Assert.True(XeniaEmulatorUpdateService.IsXeniaWindowsDesktopAssetName("xenia_canary_windows.zip"));
        Assert.False(XeniaEmulatorUpdateService.IsXeniaWindowsDesktopAssetName("xenia_canary_linux.AppImage"));
    }

    [Fact]
    public void GetOfficialWindowsAssetCandidates_includes_7z_and_legacy_zip()
    {
        var candidates = XeniaEmulatorUpdateService.GetOfficialWindowsAssetCandidates();
        Assert.Contains("xenia_canary_windows.7z", candidates);
        Assert.Contains("xenia_canary_windows.zip", candidates);
    }

    [Fact]
    public void GitHubAtomReleaseFeedReader_ParseReleases_reads_pkgforge_feed_entry()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/1046600556/02d2cb5@2026-06-08_1780919707</id>
                <updated>2026-06-08T11:55:12Z</updated>
                <title>Xenia_Canary: 02d2cb5</title>
                <link rel="alternate" href="https://github.com/pkgforge-dev/xenia-canary-AppImage/releases/tag/02d2cb5%402026-06-08_1780919707"/>
              </entry>
            </feed>
            """;

        var releases = GitHubAtomReleaseFeedReader.ParseReleases(xml, maxEntries: 5);

        Assert.Single(releases);
        Assert.Equal("02d2cb5@2026-06-08_1780919707", releases[0].Tag);
        Assert.Equal("Xenia_Canary: 02d2cb5", releases[0].Title);
    }
}
