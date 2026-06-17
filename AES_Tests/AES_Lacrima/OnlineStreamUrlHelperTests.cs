using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class OnlineStreamUrlHelperTests
{
    [Fact]
    public void IsVerifiedHighQualityStreamSet_RejectsSingleMuxedUrl()
    {
        const string muxed =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=18&mime=video%2Fmp4";

        Assert.False(OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet([muxed]));
    }

    [Fact]
    public void IsVerifiedHighQualityStreamSet_Rejects360pVideoOnlyPlusAudio()
    {
        const string video =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=134&mime=video%2Fmp4";
        const string audio =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=251&mime=audio%2Fwebm";

        Assert.False(OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet([video, audio]));
    }

    [Fact]
    public void IsVerifiedHighQualityStreamSet_AcceptsSeparate1080Streams()
    {
        const string video =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4";
        const string audio =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=251&mime=audio%2Fwebm";

        Assert.True(OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet([video, audio]));
    }

    [Fact]
    public void IsVerifiedHighQualityStreamSet_AcceptsUnknownHighItagWithoutMime()
    {
        const string video =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=399&expire=123";
        const string audio =
            "https://rr3---sn.test.googlevideo.com/videoplayback?itag=251&expire=123";

        Assert.True(OnlineStreamUrlHelper.IsVerifiedHighQualityStreamSet([video, audio]));
    }
}
