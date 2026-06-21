using AES_Lacrima.Services.Emulation;
using System.IO;

namespace AES_Lacrima.Tests;

public sealed class EmulationHashTitleResolverTests
{
    [Fact]
    public void NesDatabase_ResolvesKnownMd5()
    {
        var database = EmulationHashTitleDatabase.Load("nes.json", "NES No-Intro title database");
        var romInfo = new RomInfo { Md5 = "c03268b57e753fbd1d5e86a31c17c549" };
        var title = database.TryResolve(romInfo);
        Assert.NotNull(title);
        Assert.Contains("!Clik!", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GbaDatabase_ResolvesKnownMd5()
    {
        var database = EmulationHashTitleDatabase.Load("gba.json", "GBA No-Intro title database");
        var romInfo = new RomInfo { Md5 = "27f322f5cd535297ab21bc4a41cbfc12" };
        var title = database.TryResolve(romInfo);
        Assert.NotNull(title);
        Assert.Contains("Advance Wars", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsSupportedAlbum_MatchesNesGbaPspGenesis()
    {
        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("Sega Genesis", out var genesis));
        Assert.Equal("GENESIS", genesis.ConsoleKey);

        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("Nintendo Entertainment System", out var nes));
        Assert.Equal("NES", nes.ConsoleKey);

        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("Game Boy Advance", out var gba));
        Assert.Equal("GBA", gba.ConsoleKey);

        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("PlayStation Portable", out var psp));
        Assert.Equal("PSP", psp.ConsoleKey);
    }

    [Fact]
    public void PspParamSfoReader_ParsesSampleSfo()
    {
        using var stream = new MemoryStream(CreateSampleSfo("ULUS12345", "Test Game"));
        Assert.True(PspParamSfoReader.TryReadSfo(stream, out var discId, out var title));
        Assert.Equal("ULUS12345", discId);
        Assert.Equal("Test Game", title);
    }

    [Fact]
    public void PspIso_ReadsRealDiscMetadata_WhenAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PSP_TEST_ISO")
            ?? "/run/media/aruan/Data/Gaming/Games/Consoles/Playstation/PSP/em-ff7cc.iso";
        if (!File.Exists(path))
            return;

        var romInfo = RomInspector.Inspect(path, DiscSection.PSP);
        Assert.False(string.IsNullOrWhiteSpace(romInfo.GameId));
        Assert.Contains("ULUS-10336", romInfo.GameId, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(romInfo.InternalTitle));
        Assert.Contains("CRISIS CORE", romInfo.InternalTitle, StringComparison.OrdinalIgnoreCase);

        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("PlayStation Portable", out var platform));
        Assert.NotNull(platform);
        var title = EmulationHashTitleResolver.TryResolveOffline(path, platform!);
        Assert.NotNull(title);
        Assert.Contains("Crisis Core", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PspRedumpDatabase_ResolvesKnownSerial()
    {
        var database = EmulationHashTitleDatabase.Load("psp_redump.json", "PSP Redump title database");
        var romInfo = new RomInfo { GameId = "ULUS-10336" };
        var title = database.TryResolve(romInfo);
        Assert.NotNull(title);
        Assert.Contains("Crisis Core", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PspRedumpDatabase_ResolvesKnownMd5()
    {
        var database = EmulationHashTitleDatabase.Load("psp_redump.json", "PSP Redump title database");
        var romInfo = new RomInfo { Md5 = "a36a1884647146c607215134e1836228" };
        var title = database.TryResolve(romInfo);
        Assert.NotNull(title);
        Assert.Contains("Crisis Core", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenesisTitleResolver_RemainsCompatibleWrapper()
    {
        Assert.True(GenesisTitleResolver.IsGenesisAlbum("Sega Genesis"));
        Assert.False(GenesisTitleResolver.IsGenesisAlbum("NES"));
    }

    [Theory]
    [InlineData("AEROACR2.ZIP", "ACROBAT")]
    [InlineData("AEROBLST.ZIP", "BLAST")]
    [InlineData("AFLASH.ZIP", "FLASH")]
    public void GenesisSmdZip_ResolvesInternalTitle_WhenAvailable(string fileName, string expectedFragment)
    {
        var path = $"/run/media/aruan/Data/Gaming/Games/Consoles/Sega/Genesis/{fileName}";
        if (!File.Exists(path))
            return;

        var romInfo = RomInspector.Inspect(path);
        Assert.Equal(RomFormat.Genesis, romInfo.Format);
        Assert.False(string.IsNullOrWhiteSpace(romInfo.InternalTitle));
        Assert.Contains(expectedFragment, romInfo.InternalTitle, StringComparison.OrdinalIgnoreCase);

        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum("Sega Genesis", out var platform));
        Assert.NotNull(platform);
        var title = EmulationHashTitleResolver.TryResolveOffline(path, platform!);
        Assert.NotNull(title);
        Assert.Contains(expectedFragment, title, StringComparison.OrdinalIgnoreCase);
    }

    private static EmulationHashTitlePlatform GetPlatform(string key)
    {
        Assert.True(EmulationHashTitleResolver.IsSupportedAlbum(key, out var platform));
        Assert.NotNull(platform);
        return platform;
    }

    private static byte[] CreateSampleSfo(string discId, string title)
    {
        var keys = new[] { "DISC_ID", "TITLE" };
        var values = new[] { discId, title };
        var keyData = new MemoryStream();
        var valueData = new MemoryStream();
        var keyOffsets = new ushort[keys.Length];
        var valueOffsets = new uint[keys.Length];
        var valueLengths = new uint[keys.Length];

        for (int i = 0; i < keys.Length; i++)
        {
            keyOffsets[i] = (ushort)keyData.Length;
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(keys[i] + "\0");
            keyData.Write(keyBytes, 0, keyBytes.Length);

            valueOffsets[i] = (uint)valueData.Length;
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(values[i] + "\0");
            valueLengths[i] = (uint)valueBytes.Length;
            valueData.Write(valueBytes, 0, valueBytes.Length);
        }

        var keyTableOffset = 0x14 + keys.Length * 16;
        var dataTableOffset = keyTableOffset + (int)keyData.Length;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(unchecked((int)0x46535000));
        writer.Write(0x00000101);
        writer.Write(keyTableOffset);
        writer.Write(dataTableOffset);
        writer.Write(keys.Length);

        for (int i = 0; i < keys.Length; i++)
        {
            writer.Write(keyOffsets[i]);
            writer.Write(keys[i] == "DISC_ID" || keys[i] == "TITLE" ? (ushort)0x0204 : (ushort)0x0404);
            writer.Write(valueLengths[i]);
            writer.Write(valueLengths[i]);
            writer.Write(valueOffsets[i]);
        }

        writer.Write(keyData.ToArray());
        writer.Write(valueData.ToArray());
        return output.ToArray();
    }
}
