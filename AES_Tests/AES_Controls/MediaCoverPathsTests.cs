using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class MediaCoverPathsTests
{
    [Fact]
    public void UsesEmulationCoverSidecar_ReturnsTrue_ForMissingIsoPath()
    {
        var missingIso = "/run/media/user/SSD/Games/Blue Dragon [RF][DVD1].iso";

        Assert.False(File.Exists(missingIso));
        Assert.True(MediaCoverPaths.UsesEmulationCoverSidecar(missingIso));
        Assert.False(MediaCoverPaths.UsesMetadataImageCache(missingIso));
    }

    [Fact]
    public void UsesMetadataImageCache_ReturnsTrue_ForMissingNonRomPath()
    {
        var missingVideo = "/tmp/missing/video.mkv";

        Assert.False(File.Exists(missingVideo));
        Assert.True(MediaCoverPaths.UsesMetadataImageCache(missingVideo));
        Assert.False(MediaCoverPaths.UsesEmulationCoverSidecar(missingVideo));
    }

    [Fact]
    public void UsesMetadataImageCache_ReturnsTrue_ForAudioFiles()
    {
        Assert.True(MediaCoverPaths.UsesMetadataImageCache("/music/track.flac"));
        Assert.False(MediaCoverPaths.UsesEmulationCoverSidecar("/music/track.flac"));
    }

    [Fact]
    public void UsesMetadataImageCache_ReturnsTrue_ForHttpStreams()
    {
        Assert.True(MediaCoverPaths.UsesMetadataImageCache("https://www.youtube.com/watch?v=abc12345678"));
        Assert.False(MediaCoverPaths.UsesEmulationCoverSidecar("https://www.youtube.com/watch?v=abc12345678"));
    }
}
