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
