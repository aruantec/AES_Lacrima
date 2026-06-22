using AES_Lacrima.Services.Emulation;
using AES_Controls.Player.Models;

namespace AES_Lacrima.Tests;

public sealed class EmulationOnlineCoverResolverTests
{
    [Fact]
    public void BuildLookupPayload_IncludesAvailableHashes()
    {
        var romInfo = new RomInfo
        {
            Md5 = "7A61D6A9BD7AC1A3249EF167AE136AF7",
            Sha1 = "0123456789ABCDEF0123456789ABCDEF01234567",
            Crc32 = "B19ED489"
        };

        var payload = HasheousLookupService.BuildLookupPayload(romInfo);

        Assert.NotNull(payload);
        Assert.Contains("\"md5\":\"7a61d6a9bd7ac1a3249ef167ae136af7\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"sha1\":\"0123456789abcdef0123456789abcdef01234567\"", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"crc\":\"b19ed489\"", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLookupPayload_ReturnsNullWhenNoHashes()
    {
        var payload = HasheousLookupService.BuildLookupPayload(new RomInfo());
        Assert.Null(payload);
    }

    [Fact]
    public void LibRetroPlatformCatalog_ResolvesSnesAlbum()
    {
        Assert.True(LibRetroPlatformCatalog.TryResolveFolder("Super Nintendo", null, out var folder));
        Assert.Equal("Nintendo - Super Nintendo Entertainment System", folder);
    }

    [Fact]
    public void LibRetroPlatformCatalog_ResolvesGbaAlbum()
    {
        Assert.True(LibRetroPlatformCatalog.TryResolveFolder("GBA", null, out var folder));
        Assert.Equal("Nintendo - Game Boy Advance", folder);
    }

    [Fact]
    public void LibRetroThumbnailCoverService_BuildsRegionalVariants()
    {
        var urls = LibRetroThumbnailCoverService
            .BuildCoverUrls("Nintendo - Super Nintendo Entertainment System", ["Super Mario World"])
            .Take(4)
            .ToList();

        Assert.Contains(urls, url => url.Contains("Super%20Mario%20World%20(USA).png", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("Super%20Mario%20World.png", StringComparison.Ordinal));
    }

    [Fact]
    public void LibRetroThumbnailCoverService_BuildsGbaCoverUrl()
    {
        var urls = LibRetroThumbnailCoverService
            .BuildCoverUrls("Nintendo - Game Boy Advance", ["Pokemon Emerald (USA, Europe)"])
            .Take(4)
            .ToList();

        Assert.Contains(urls, url => url.Contains("Pokemon%20Emerald%20(USA%2C%20Europe).png", StringComparison.Ordinal));
        Assert.Contains(urls, url => url.Contains("Pokemon%20Emerald.png", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildTitleCandidates_PrefersHasheousName()
    {
        var item = new MediaItem
        {
            Title = "smw",
            FileName = "/roms/Super Mario World (USA).sfc"
        };
        var romInfo = new RomInfo { InternalTitle = "SUPER MARIOWORLD" };

        var candidates = EmulationOnlineCoverResolver.BuildTitleCandidates(item, romInfo, "Super Mario World");

        Assert.Equal("Super Mario World", candidates[0]);
        Assert.Contains(candidates, candidate => candidate.Contains("Super Mario World (USA)", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldApplyResolvedTitle_WhenTitleMatchesFilename()
    {
        var item = new MediaItem
        {
            Title = "Super Mario World (USA)",
            FileName = "/roms/Super Mario World (USA).sfc"
        };

        Assert.True(EmulationOnlineCoverResolver.ShouldApplyResolvedTitle(item, "Super Mario World"));
    }

    [Fact]
    public void TheGamesDbCoverService_BuildsExpectedUrls()
    {
        var urls = TheGamesDbCoverService.BuildCoverUrls("9577").ToList();

        Assert.Equal("https://cdn.thegamesdb.net/images/original/boxart/front/9577-1.jpg", urls[0]);
        Assert.Equal("https://cdn.thegamesdb.net/images/original/boxart/front/9577-2.jpg", urls[1]);
    }
}
