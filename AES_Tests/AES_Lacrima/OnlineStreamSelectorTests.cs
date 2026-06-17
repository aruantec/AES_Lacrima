using AES_Controls.Helpers;
using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class OnlineStreamSelectorTests
{
    [Fact]
    public void Select_StandardVideoMode_UsesMuxedStreamWhenAvailable()
    {
        var info = CreateMediaInfo(
            videoUrl: "https://example.com/video-only",
            audioUrl: "https://example.com/audio-only",
            muxedUrl: "https://example.com/muxed");

        var resolved = OnlineStreamSelector.Select(info, preferVideo: true, useHighQualityStream: false);

        Assert.NotNull(resolved);
        Assert.Equal("https://example.com/muxed", resolved.VideoUrl);
        Assert.Equal("https://example.com/muxed", resolved.AudioUrl);
    }

    [Fact]
    public void Select_HighQualityVideoMode_RejectsMuxedOnlyFormats()
    {
        var info = new MediaInfo
        {
            VideoFormats = [],
            AudioFormats = [],
            MuxedFormats =
            [
                new MuxedFormat { Url = "https://example.com/muxed", Height = 360, Width = 640 }
            ]
        };

        var resolved = OnlineStreamSelector.Select(info, preferVideo: true, useHighQualityStream: true);

        Assert.Null(resolved);
    }

    [Fact]
    public void Select_HighQualityVideoMode_UsesSeparateStreams()
    {
        var info = CreateMediaInfo(
            videoUrl: "https://example.com/video-only",
            audioUrl: "https://example.com/audio-only",
            muxedUrl: "https://example.com/muxed");

        var resolved = OnlineStreamSelector.Select(info, preferVideo: true, useHighQualityStream: true);

        Assert.NotNull(resolved);
        Assert.Equal("https://example.com/video-only", resolved.VideoUrl);
        Assert.Equal("https://example.com/audio-only", resolved.AudioUrl);
    }

    [Fact]
    public void SelectBestVideo_CapsAtTargetHeight()
    {
        var formats = new[]
        {
            new VideoFormat { Url = "https://example.com/4k", Height = 2160 },
            new VideoFormat { Url = "https://example.com/1080", Height = 1080 },
            new VideoFormat { Url = "https://example.com/720", Height = 720 }
        };

        var best = OnlineStreamSelector.SelectBestVideo(formats, targetHeight: 1080);

        Assert.NotNull(best);
        Assert.Equal("https://example.com/1080", best.Url);
    }

    private static MediaInfo CreateMediaInfo(string videoUrl, string audioUrl, string muxedUrl) =>
        new()
        {
            VideoFormats =
            [
                new VideoFormat { Url = videoUrl, Height = 1080, Width = 1920 }
            ],
            AudioFormats =
            [
                new AudioFormat { Url = audioUrl, Bitrate = 256 }
            ],
            MuxedFormats =
            [
                new MuxedFormat { Url = muxedUrl, Height = 360, Width = 640 }
            ]
        };
}
