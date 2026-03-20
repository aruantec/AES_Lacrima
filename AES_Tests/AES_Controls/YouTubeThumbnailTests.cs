using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class YouTubeThumbnailTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=5", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void ExtractVideoIdWithRegex_ReturnsVideoId(string input, string expected)
    {
        var result = YouTubeThumbnail.ExtractVideoIdWithRegex(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetCleanVideoLink_NormalizesVideoUrl()
    {
        var result = YouTubeThumbnail.GetCleanVideoLink("https://youtu.be/dQw4w9WgXcQ?t=5");

        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", result);
    }

    [Fact]
    public void GetAllThumbnailUrls_PopulatesThumbnailCollections()
    {
        var result = YouTubeThumbnail.GetAllThumbnailUrls("dQw4w9WgXcQ", "channel123");

        Assert.Equal("dQw4w9WgXcQ", result.VideoId);
        Assert.Equal("https://img.youtube.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", result.Thumbnails["Max Resolution (1280x720)"]);
        Assert.Equal("https://img.youtube.com/vi/dQw4w9WgXcQ/2.jpg", result.VideoFrames["Frame 2 (Middle)"]);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", result.PlayerBackground);
        Assert.Equal("https://yt3.googleusercontent.com/ytc/channel123=s88-c-k-c0x00ffffff-no-rj", result.ChannelImages["Profile Picture (88x88)"]);
    }
}
