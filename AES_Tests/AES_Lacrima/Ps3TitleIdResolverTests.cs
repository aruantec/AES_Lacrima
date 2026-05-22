using AES_Lacrima.Services;

namespace AES_Tests.AES_Lacrima;

public sealed class Ps3TitleIdResolverTests
{
    [Fact]
    public void ResolveTitleId_ReadsTitleIdFromGameIdBootPath()
    {
        var titleId = Ps3InstalledGameHelper.ResolveTitleId("%RPCS3_GAMEID%:BLES01227");

        Assert.Equal("BLES01227", titleId);
    }

    [Fact]
    public void ResolveTitleId_ReadsTitleIdFromFolderName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(tempRoot, "Asura's Wrath [BLES01227]");
        Directory.CreateDirectory(gameDir);
        try
        {
            var titleId = Ps3InstalledGameHelper.ResolveTitleId(gameDir);

            Assert.Equal("BLES01227", titleId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
