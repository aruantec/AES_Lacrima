using System.Buffers.Binary;
using System.Text;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.Services.Emulation.Switch;

namespace AES_Lacrima.Tests;

public sealed class RomInspectorSwitchTests
{
    [Fact]
    public void Inspect_Nca_ExtractsTitleIdFromHeader()
    {
        using var tempFile = new TempRomFile(".nca");
        WriteNcaHeader(tempFile.Path, 0x0100F66015FB6000UL, SwitchNcaHeaderReader.ContentTypeProgram);

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.Switch);

        Assert.Equal("0100F66015FB6000", romInfo.GameId);
    }

    [Fact]
    public void Inspect_Nsp_ExtractsTitleIdFromEmbeddedNca()
    {
        using var tempFile = new TempRomFile(".nsp");
        WriteMinimalNsp(tempFile.Path, "0100F2C0115B6000", "Super Mario Odyssey");

        var romInfo = RomInspector.Inspect(tempFile.Path, DiscSection.Switch);

        Assert.Equal("0100F2C0115B6000", romInfo.GameId);
    }

    [Fact]
    public void SwitchRomMetadataReader_ExtractsTitleIdFromParentFolderName()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Bayonetta 3 [01004F5010BFA000]");
        Directory.CreateDirectory(folder);
        var romPath = Path.Combine(folder, "game.nsp");
        File.WriteAllBytes(romPath, [0x00]);

        try
        {
            var result = SwitchRomMetadataReader.TryRead(romPath);
            Assert.Equal("01004F5010BFA000", result.TitleId);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void SwitchRomMetadataReader_ExtractsTitleIdFromBracketedFilename()
    {
        using var tempFile = new TempRomFile(".nsp", "Game [01009BF0072D0000].nsp");
        File.WriteAllBytes(tempFile.Path, [0x00]);

        var result = SwitchRomMetadataReader.TryRead(tempFile.Path);

        Assert.Equal("01009BF0072D0000", result.TitleId);
        Assert.Contains("Game", result.DisplayTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteNcaHeader(string path, ulong titleId, byte contentType)
    {
        var data = new byte[0x400];
        Encoding.ASCII.GetBytes("NCA3").CopyTo(data, 0x200);
        data[0x205] = contentType;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x210), titleId);
        File.WriteAllBytes(path, data);
    }

    private static void WriteMinimalNsp(string path, string titleIdHex, string displayName)
    {
        var ncaBytes = new byte[0x400];
        Encoding.ASCII.GetBytes("NCA3").CopyTo(ncaBytes, 0x200);
        ncaBytes[0x205] = SwitchNcaHeaderReader.ContentTypeProgram;
        BinaryPrimitives.WriteUInt64LittleEndian(
            ncaBytes.AsSpan(0x210),
            ulong.Parse(titleIdHex, System.Globalization.NumberStyles.HexNumber));

        var ncaName = $"{displayName} [{titleIdHex}].nca";
        var ncaNameBytes = Encoding.UTF8.GetBytes(ncaName + "\0");
        const int headerSize = 0x10;
        var entrySize = 0x18;
        var stringTableOffset = headerSize + entrySize;
        var dataOffset = stringTableOffset + ncaNameBytes.Length;

        using var stream = File.Create(path);
        Span<byte> header = stackalloc byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x30534650);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)ncaNameBytes.Length);
        stream.Write(header.ToArray());

        var entry = new byte[entrySize];
        BinaryPrimitives.WriteUInt64LittleEndian(entry, 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), (ulong)ncaBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0x10), 0U);
        stream.Write(entry);
        stream.Write(ncaNameBytes);

        var padding = (int)((dataOffset - stream.Position) & 0xF);
        if (padding != 0)
            stream.Write(new byte[16 - padding]);

        stream.Write(ncaBytes);
    }

    private sealed class TempRomFile : IDisposable
    {
        public TempRomFile(string extension, string? fileName = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
            if (!string.IsNullOrWhiteSpace(fileName))
                Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, fileName);

            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }
}
