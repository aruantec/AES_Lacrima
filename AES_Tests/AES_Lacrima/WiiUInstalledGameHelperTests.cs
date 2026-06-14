using AES_Lacrima.Services;
using AES_Lacrima.Services.Emulation;

namespace AES_Lacrima.Tests;

public sealed class WiiUInstalledGameHelperTests
{
    [Fact]
    public void IsInstalledGameFolder_CaseInsensitiveMetaXml_ReturnsTrue()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "code"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "content"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "meta"));
        File.WriteAllText(
            Path.Combine(tempDir.Path, "meta", "Meta.XML"),
            """
            <menu>
              <title_id>0005000010113100</title_id>
              <longname_en>Super Mario 3D World</longname_en>
            </menu>
            """);

        Assert.True(WiiUInstalledGameHelper.IsInstalledGameFolder(tempDir.Path));
        Assert.Equal("00050000-10113100", WiiUInstalledGameHelper.GetTitleId(tempDir.Path));
        Assert.Equal("Super Mario 3D World", WiiUInstalledGameHelper.GetTitleName(tempDir.Path));
    }

    [Fact]
    public void ResolveMetadata_WuxFilename_ExtractsTitleIdAndDisplayName()
    {
        using var tempDir = new TempDirectory();
        var romPath = Path.Combine(tempDir.Path, "Super Mario 3D World [00050000-10113100].wux");
        File.WriteAllBytes(romPath, new byte[64 * 1024]);

        var resolved = WiiUInstalledGameHelper.ResolveMetadata(romPath);

        Assert.Equal("00050000-10113100", resolved.TitleId);
        Assert.Equal("Super Mario 3D World", resolved.TitleName);
    }

    [Fact]
    public void Inspect_WuxFile_DoesNotScanEntireDiscImage()
    {
        using var tempDir = new TempDirectory();
        var romPath = Path.Combine(tempDir.Path, "Zelda [00050000101C9300].wux");
        using (var stream = File.Create(romPath))
        {
            stream.SetLength(32L * 1024 * 1024);
        }

        var started = Environment.TickCount64;
        var romInfo = RomInspector.Inspect(romPath, DiscSection.WiiU);
        var elapsedMs = Environment.TickCount64 - started;

        Assert.Equal("00050000-101C9300", romInfo.GameId);
        Assert.Contains("Zelda", romInfo.InternalTitle, StringComparison.Ordinal);
        Assert.True(elapsedMs < 2000, $"Wii U file inspection took too long: {elapsedMs}ms");
    }
}
