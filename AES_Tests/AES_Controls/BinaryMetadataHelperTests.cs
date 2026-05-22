using AES_Code.Models;
using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class BinaryMetadataHelperTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"aes-lacrima-tests-{Guid.NewGuid():N}");

    public BinaryMetadataHelperTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void SaveMetadata_ThenLoadMetadata_RoundTripsContent()
    {
        var cachePath = Path.Combine(_tempDirectory, "metadata.json");
        var metadata = new CustomMetadata
        {
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Track = 7,
            Year = 2025,
            Genre = "Synthwave",
            Images =
            [
                new ImageData
                {
                    MimeType = "image/jpeg",
                    Data = [1, 2, 3],
                    Kind = TagImageKind.Cover
                }
            ]
        };

        BinaryMetadataHelper.SaveMetadata(cachePath, metadata);
        var loaded = BinaryMetadataHelper.LoadMetadata(cachePath);

        Assert.NotNull(loaded);
        Assert.Equal(metadata.Title, loaded.Title);
        Assert.Equal(metadata.Artist, loaded.Artist);
        Assert.Equal(metadata.Track, loaded.Track);
        Assert.Single(loaded.Images);
        Assert.Equal("image/jpeg", loaded.Images[0].MimeType);
        Assert.Equal([1, 2, 3], loaded.Images[0].Data);
    }

    [Fact]
    public void LoadMetadata_ReturnsNullForMissingFile()
    {
        var result = BinaryMetadataHelper.LoadMetadata(Path.Combine(_tempDirectory, "missing.json"));

        Assert.Null(result);
    }

    [Fact]
    public void ReadMetadataImages_DeduplicatesLegacyLiveWallpaperStoredInBothLists()
    {
        var sharedBytes = new byte[] { 9, 8, 7, 6 };
        var metadata = new CustomMetadata
        {
            Images =
            [
                new ImageData { Kind = TagImageKind.Cover, MimeType = "image/png", Data = [1, 2, 3] },
                new ImageData { Kind = TagImageKind.LiveWallpaper, MimeType = "video/mp4", Data = sharedBytes }
            ],
            Videos =
            [
                new VideoData { Kind = TagImageKind.LiveWallpaper, MimeType = "video/mp4", Data = sharedBytes }
            ]
        };

        var loaded = BinaryMetadataHelper.ReadMetadataImages(metadata);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(TagImageKind.Cover, loaded[0].Kind);
        Assert.Equal(TagImageKind.LiveWallpaper, loaded[1].Kind);
    }

    [Fact]
    public void ReadMetadataImages_CollapsesDuplicateBytesAcrossKindsPreferringBoxArt()
    {
        var sharedBytes = new byte[] { 4, 5, 6, 7 };
        var metadata = new CustomMetadata
        {
            Images =
            [
                new ImageData { Kind = TagImageKind.Cover, MimeType = "image/png", Data = sharedBytes },
                new ImageData { Kind = TagImageKind.BoxArt, MimeType = "image/png", Data = sharedBytes },
                new ImageData { Kind = TagImageKind.Cover, MimeType = "image/png", Data = sharedBytes },
                new ImageData { Kind = TagImageKind.BoxArt, MimeType = "image/png", Data = sharedBytes }
            ]
        };

        var loaded = BinaryMetadataHelper.ReadMetadataImages(metadata);

        Assert.Single(loaded);
        Assert.Equal(TagImageKind.BoxArt, loaded[0].Kind);
    }

    [Fact]
    public void WriteMetadataImages_StoresLiveWallpaperOnlyInVideos()
    {
        var metadata = new CustomMetadata();
        BinaryMetadataHelper.WriteMetadataImages(metadata,
        [
            new MetadataImageEntry(TagImageKind.Cover, [1, 2], "image/png"),
            new MetadataImageEntry(TagImageKind.Other, [3, 4], "image/jpeg"),
            new MetadataImageEntry(TagImageKind.LiveWallpaper, [5, 6], "video/mp4")
        ]);

        Assert.Equal(2, metadata.Images.Count);
        Assert.Single(metadata.Videos);
        Assert.DoesNotContain(metadata.Images, image => image.Kind == TagImageKind.LiveWallpaper);
        Assert.Equal(TagImageKind.LiveWallpaper, metadata.Videos[0].Kind);
    }

    [Fact]
    public void GetCacheId_ReturnsStableHashAndUnknownForEmptyInput()
    {
        var first = BinaryMetadataHelper.GetCacheId("/music/track.mp3");
        var second = BinaryMetadataHelper.GetCacheId("/music/track.mp3");

        Assert.Equal(first, second);
        Assert.Equal(40, first.Length);
        Assert.Equal("unknown", BinaryMetadataHelper.GetCacheId(string.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
