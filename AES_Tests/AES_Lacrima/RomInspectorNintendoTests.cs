using System.Text;
using AES_Lacrima.Services.Emulation;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Tests;

public sealed class RomInspectorNintendoTests
{
    private static readonly ILog Log = LogHelper.For<RomInspectorNintendoTests>();
    [Fact]
    public void Inspect_GameCubeIso_ExtractsGameIdAndInternalTitle()
    {
        using var tempFile = new TempRomFile(".iso");
        WriteGameCubeDiscHeader(tempFile.Path, "GM8E01", "SUPER MARIO SUNSHINE     ");

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.GameCube);

        Assert.Equal("GM8E01", romInfo.GameId);
        Assert.Contains("SUPER MARIO SUNSHINE", romInfo.InternalTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_WiiIso_ExtractsGameId()
    {
        using var tempFile = new TempRomFile(".iso");
        WriteWiiDiscHeader(tempFile.Path, "RMGE01", "NEW SUPER MARIO BROS. WII");

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.Wii);

        Assert.Equal("RMGE01", romInfo.GameId);
        Assert.Contains("NEW SUPER MARIO BROS", romInfo.InternalTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_Wbfs_ExtractsGameIdFromHeader()
    {
        using var tempFile = new TempRomFile(".wbfs");
        var header = new byte[64];
        Encoding.ASCII.GetBytes("WBFS").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("RZDE01").CopyTo(header, 8);
        File.WriteAllBytes(tempFile.Path, header);

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.Wii);

        Assert.Equal("RZDE01", romInfo.GameId);
    }

    [Fact]
    public void Inspect_GameCubeFilename_ExtractsGameIdWhenHeaderMissing()
    {
        using var tempFile = new TempRomFile(".gcm", "GM8E01 - Mario.iso");
        File.WriteAllBytes(tempFile.Path, new byte[0x100]);

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.GameCube);

        Assert.Equal("GM8E01", romInfo.GameId);
    }

    [Fact]
    public void Inspect_GameCubeFilename_DoesNotTreatDoubleDashTitleAsGameId()
    {
        using var tempFile = new TempRomFile(".iso", "Mario Kart - Double Dash.iso");
        WriteGameCubeDiscHeader(tempFile.Path, "GALE01", "MARIO KART DOUBLE DASH!!      ");

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.GameCube);

        Assert.Equal("GALE01", romInfo.GameId);
        Assert.Contains("MARIO KART DOUBLE DASH", romInfo.InternalTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_WiiUPackage_ExtractsTitleIdAndLongName()
    {
        using var tempDir = new TempRomDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "code"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "content"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "meta"));
        File.WriteAllText(
            Path.Combine(tempDir.Path, "meta", "meta.xml"),
            """
            <menu>
              <title_id>0005000010113100</title_id>
              <longname_en>Super Mario 3D World</longname_en>
            </menu>
            """);

        var romInfo = RomInspector.Inspect(tempDir.Path, DiscSection.WiiU);

        Assert.Equal("00050000-10113100", romInfo.GameId);
        Assert.Equal("Super Mario 3D World", romInfo.InternalTitle);
    }

    private static void WriteGameCubeDiscHeader(string path, string gameId, string title)
    {
        var header = new byte[0x100];
        Encoding.ASCII.GetBytes(gameId).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(title).CopyTo(header, 0x20);
        BitConverter.GetBytes(0xC2339F3Du).CopyTo(header, 0x1C);
        File.WriteAllBytes(path, header);
    }

    private static void WriteWiiDiscHeader(string path, string gameId, string title)
    {
        var header = new byte[0x100];
        Encoding.ASCII.GetBytes(gameId).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(title).CopyTo(header, 0x20);
        BitConverter.GetBytes(0x5D1C9EA3u).CopyTo(header, 0x60);
        File.WriteAllBytes(path, header);
    }

    private sealed class TempRomDirectory : IDisposable
    {
        public string Path { get; }

        public TempRomDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, true);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    private sealed class TempRomFile : IDisposable
    {
        public string Path { get; }

        public TempRomFile(string extension, string? fileName = null)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                fileName ?? $"{System.IO.Path.GetRandomFileName()}{extension}");
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }
}
