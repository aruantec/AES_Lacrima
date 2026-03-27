using System.Reflection;
using System.Runtime.CompilerServices;
using AES_Controls.Helpers;
using TagLib;

namespace AES_Controls.Tests;

public sealed class MetadataScrapperTests
{
    [Fact]
    public void SelectEmbeddedImages_PrefersFrontCover_EvenWhenOverMaxBytes()
    {
        var front = new byte[] { 1, 2, 3, 4, 5, 6 };
        var other = new byte[] { 9, 9 };
        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.Other, "", other),
            new FakePicture(PictureType.FrontCover, "", front)
        };

        var (cover, wallpaper) = InvokeSelectEmbeddedImages(pictures, maxBytes: 3, includeCover: true, includeWallpaper: true);

        Assert.Equal(front, cover);
        Assert.Null(wallpaper);
    }

    [Fact]
    public void SelectEmbeddedImages_UsesCoverDescription_WhenPictureTypeIsOtherAndOverMaxBytes()
    {
        var largeCover = new byte[] { 1, 2, 3, 4, 5, 6 };
        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.Other, "cover (front)", largeCover)
        };

        var (cover, _) = InvokeSelectEmbeddedImages(pictures, maxBytes: 3, includeCover: true, includeWallpaper: false);

        Assert.Equal(largeCover, cover);
    }

    [Fact]
    public void SelectEmbeddedImages_UsesOversizedFallback_WhenNoBetterCandidateExists()
    {
        var largeOther = new byte[] { 9, 8, 7, 6, 5, 4 };
        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.Other, string.Empty, largeOther)
        };

        var (cover, _) = InvokeSelectEmbeddedImages(pictures, maxBytes: 3, includeCover: true, includeWallpaper: false);

        Assert.Equal(largeOther, cover);
    }

    [Fact]
    public void SelectEmbeddedImages_SkipsWallpaperAndBackCover_WhenChoosingFallbackCover()
    {
        var wallpaperBytes = new byte[] { 7, 7 };
        var backCoverBytes = new byte[] { 8, 8 };
        var validCoverBytes = new byte[] { 3, 3, 3 };

        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.Other, "wallpaper", wallpaperBytes),
            new FakePicture(PictureType.BackCover, "", backCoverBytes),
            new FakePicture(PictureType.Other, "", validCoverBytes)
        };

        var (cover, _) = InvokeSelectEmbeddedImages(pictures, maxBytes: 10, includeCover: true, includeWallpaper: false);

        Assert.Equal(validCoverBytes, cover);
    }

    [Fact]
    public void SelectEmbeddedImages_PicksIllustrationForWallpaper()
    {
        var illustrationBytes = new byte[] { 4, 4, 4 };
        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.Other, "", new byte[] { 1 }),
            new FakePicture(PictureType.Illustration, "", illustrationBytes)
        };

        var (_, wallpaper) = InvokeSelectEmbeddedImages(pictures, maxBytes: 10, includeCover: false, includeWallpaper: true);

        Assert.Equal(illustrationBytes, wallpaper);
    }

    [Fact]
    public void SelectEmbeddedImages_RespectsIncludeFlags()
    {
        var pictures = new IPicture[]
        {
            new FakePicture(PictureType.FrontCover, "", new byte[] { 1, 2 }),
            new FakePicture(PictureType.Illustration, "wallpaper", new byte[] { 3, 4 })
        };

        var (cover, wallpaper) = InvokeSelectEmbeddedImages(pictures, maxBytes: 10, includeCover: false, includeWallpaper: false);

        Assert.Null(cover);
        Assert.Null(wallpaper);
    }

    [Theory]
    [InlineData("My Song (Official Video) [HD]", "My Song")]
    [InlineData("Artist - Track", "Artist - Track")]
    [InlineData("Track (lyrics)", "Track")]
    public void CleanSearchQuery_RemovesCommonVideoNoise(string input, string expected)
    {
        var instance = RuntimeHelpers.GetUninitializedObject(typeof(MetadataScrapper));
        var method = typeof(MetadataScrapper).GetMethod("CleanSearchQuery", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var result = Assert.IsType<string>(method!.Invoke(instance, new object[] { input }));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindLocalArtworkPath_PrefersConventionalCoverNames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mediaPath = Path.Combine(tempDir, "track.mp3");
            System.IO.File.WriteAllText(mediaPath, string.Empty);
            var fallbackArt = Path.Combine(tempDir, "random.png");
            var preferredArt = Path.Combine(tempDir, "cover.jpg");
            System.IO.File.WriteAllBytes(fallbackArt, [1, 2, 3]);
            System.IO.File.WriteAllBytes(preferredArt, [4, 5, 6]);

            var result = InvokeFindLocalArtworkPath(mediaPath);

            Assert.Equal(preferredArt, result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindLocalArtworkPath_FallsBackToAlphabeticalImageWhenNoConventionalNameExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mediaPath = Path.Combine(tempDir, "track.flac");
            System.IO.File.WriteAllText(mediaPath, string.Empty);
            var firstArt = Path.Combine(tempDir, "a.png");
            var secondArt = Path.Combine(tempDir, "z.jpg");
            System.IO.File.WriteAllBytes(secondArt, [1, 2, 3]);
            System.IO.File.WriteAllBytes(firstArt, [4, 5, 6]);

            var result = InvokeFindLocalArtworkPath(mediaPath);

            Assert.Equal(firstArt, result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static (byte[]? cover, byte[]? wallpaper) InvokeSelectEmbeddedImages(IPicture[] pictures, int maxBytes, bool includeCover, bool includeWallpaper)
    {
        var method = typeof(MetadataScrapper).GetMethod("SelectEmbeddedImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { pictures, maxBytes, includeCover, includeWallpaper, null, null };
        method!.Invoke(null, args);

        return ((byte[]?)args[4], (byte[]?)args[5]);
    }

    private static string? InvokeFindLocalArtworkPath(string mediaPath)
    {
        var method = typeof(MetadataScrapper).GetMethod("FindLocalArtworkPath", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return method!.Invoke(null, new object[] { mediaPath }) as string;
    }

    private sealed class FakePicture : IPicture
    {
        public FakePicture(PictureType type, string description, byte[] data)
        {
            Type = type;
            Description = description;
            Data = new ByteVector(data);
            MimeType = "image/png";
        }

        public string MimeType { get; set; }
        public string Filename { get; set; } = string.Empty;
        public PictureType Type { get; set; }
        public string Description { get; set; }
        public ByteVector Data { get; set; }
    }
}
