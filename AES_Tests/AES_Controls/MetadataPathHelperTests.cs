using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class MetadataPathHelperTests
{
    [Fact]
    public void TryExtractStreamUrl_RecoversUrlFromFilesystemPrefixedPath()
    {
        var corrupted = "/home/user/app/bin/Debug/net10.0/https:/www.youtube.com/watch?v=kspTon3WOok";
        var extracted = MetadataPathHelper.TryExtractStreamUrl(corrupted);

        Assert.Equal("https://www.youtube.com/watch?v=kspTon3WOok", extracted);
    }

    [Fact]
    public void NormalizeMetadataPath_CanonicalizesYouTubeUrls()
    {
        var normalized = MetadataPathHelper.NormalizeMetadataPath(
            "/home/user/app/https:/www.youtube.com/watch?v=kspTon3WOok");

        Assert.Equal("https://www.youtube.com/watch?v=kspTon3WOok", normalized);
    }

    [Fact]
    public void IsOnlineMediaPath_DetectsEmbeddedStreamUrls()
    {
        Assert.True(MetadataPathHelper.IsOnlineMediaPath(
            "/home/user/app/bin/https:/www.youtube.com/watch?v=abc12345678"));
    }
}
