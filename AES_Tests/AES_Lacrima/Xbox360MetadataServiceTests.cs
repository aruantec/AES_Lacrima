using System;
using System.Buffers.Binary;
using System.IO;
using AES_Lacrima.Services;

namespace AES_Lacrima.Tests;

public sealed class Xbox360MetadataServiceTests
{
    [Fact]
    public void TryReadGameMetadata_XexFile_ReadsTitleIdAndMediaId()
    {
        using var tempDir = new TempDirectory();
        var xexPath = Path.Combine(tempDir.Path, "default.xex");
        File.WriteAllBytes(xexPath, CreateTestXex(titleId: 0x43430817, mediaId: 0xD6ADDF11));

        var service = new Xbox360MetadataService();
        var metadata = service.TryReadGameMetadata(xexPath);

        Assert.NotNull(metadata);
        Assert.Equal("43430817", metadata!.TitleId);
        Assert.Equal("D6ADDF11", metadata.MediaId);
    }

    [Fact]
    public void TryReadGameMetadata_XexFile_WithKnownIds_RejectsUnknownTitle()
    {
        using var tempDir = new TempDirectory();
        var xexPath = Path.Combine(tempDir.Path, "default.xex");
        File.WriteAllBytes(xexPath, CreateTestXex(titleId: 0x43430817, mediaId: 0xD6ADDF11));

        var service = new Xbox360MetadataService();
        var metadata = service.TryReadGameMetadata(xexPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "4D5307DF" });

        Assert.Null(metadata);
    }

    private static byte[] CreateTestXex(uint titleId, uint mediaId)
    {
        var bytes = new byte[0x80];

        // Magic
        bytes[0] = (byte)'X';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'X';
        bytes[3] = (byte)'2';

        // Header directory entry count = 1
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 1);

        // Header directory entry 0 at 0x18
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18, 4), 0x00040006); // execution info search id
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x1C, 4), 0x40); // execution info offset

        // execution info @ 0x40
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x40, 4), mediaId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x44, 4), 0x00000000); // version
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x48, 4), 0x00000000); // base version
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x4C, 4), titleId);

        return bytes;
    }
}
