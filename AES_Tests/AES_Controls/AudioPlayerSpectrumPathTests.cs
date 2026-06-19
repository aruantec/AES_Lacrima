using AES_Controls.Player;

namespace AES_Controls.Tests;

public sealed class AudioPlayerSpectrumPathTests
{
    [Fact]
    public void ResolveSpectrumAnalysisPath_uses_external_audio_for_split_video_streams()
    {
        var resolved = AudioPlayer.ResolveSpectrumAnalysisPath(
            playbackPath: "https://example.com/video-only.webm",
            video: true,
            externalAudioUrl: "https://example.com/audio-only.webm",
            muxedFallbackUrl: "https://example.com/muxed.mp4",
            usedMuxFallback: false,
            externalAudioActive: true);

        Assert.Equal("https://example.com/audio-only.webm", resolved);
    }

    [Fact]
    public void ResolveSpectrumAnalysisPath_uses_muxed_fallback_when_external_audio_failed()
    {
        var resolved = AudioPlayer.ResolveSpectrumAnalysisPath(
            playbackPath: "https://example.com/video-only.webm",
            video: true,
            externalAudioUrl: "https://example.com/audio-only.webm",
            muxedFallbackUrl: "https://example.com/muxed.mp4",
            usedMuxFallback: true,
            externalAudioActive: false);

        Assert.Equal("https://example.com/muxed.mp4", resolved);
    }

    [Fact]
    public void ResolveSpectrumAnalysisPath_uses_playback_path_for_local_video()
    {
        var resolved = AudioPlayer.ResolveSpectrumAnalysisPath(
            playbackPath: "/media/movie.mkv",
            video: true,
            externalAudioUrl: null,
            muxedFallbackUrl: null,
            usedMuxFallback: false,
            externalAudioActive: false);

        Assert.Equal("/media/movie.mkv", resolved);
    }

    [Fact]
    public void ResolveSpectrumAnalysisPath_uses_playback_path_for_audio_only()
    {
        var resolved = AudioPlayer.ResolveSpectrumAnalysisPath(
            playbackPath: "/media/track.flac",
            video: false,
            externalAudioUrl: null,
            muxedFallbackUrl: null,
            usedMuxFallback: false,
            externalAudioActive: false);

        Assert.Equal("/media/track.flac", resolved);
    }
}
