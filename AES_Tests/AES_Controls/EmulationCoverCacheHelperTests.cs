using AES_Code.Models;
using AES_Controls.Helpers;

namespace AES_Controls.Tests;

public sealed class EmulationCoverCacheHelperTests : IDisposable
{
    private readonly string _tempDirectory;

    public EmulationCoverCacheHelperTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aes_emulation_cover_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void WriteCoverFromBytes_CreatesSidecarFile()
    {
        var romPath = Path.Combine(_tempDirectory, "game.iso");
        File.WriteAllText(romPath, "rom");

        var png = CreateSolidPng(320, 320);
        Assert.True(EmulationCoverCacheHelper.WriteCoverFromBytes(romPath, png));
        Assert.True(EmulationCoverCacheHelper.HasCover(romPath));

        var written = EmulationCoverCacheHelper.TryReadCoverBytes(romPath);
        Assert.NotNull(written);
        Assert.True(written!.Length > 0);
    }

    [Fact]
    public void TryMigrateCoverFromMetadata_MovesCoverOutOfMeta()
    {
        var romPath = Path.Combine(_tempDirectory, "migrate.iso");
        File.WriteAllText(romPath, "rom");

        var metaPath = EmulationCoverCacheHelper.GetMetadataCachePath(romPath);
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);

        var png = CreateSolidPng(256, 256);
        var metadata = new CustomMetadata
        {
            Title = "Test Game",
            Images =
            [
                new ImageData
                {
                    Kind = TagImageKind.Cover,
                    MimeType = "image/png",
                    Data = png
                }
            ]
        };
        BinaryMetadataHelper.SaveMetadata(metaPath, metadata);

        Assert.True(EmulationCoverCacheHelper.TryMigrateCoverFromMetadata(romPath));
        Assert.True(EmulationCoverCacheHelper.HasCover(romPath));

        var reloaded = BinaryMetadataHelper.LoadMetadata(metaPath);
        Assert.NotNull(reloaded);
        Assert.DoesNotContain(reloaded!.Images, image => image.Kind == TagImageKind.Cover && image.Data.Length > 0);
    }

    private static byte[] CreateSolidPng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.Coral);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data!.ToArray();
    }
}
