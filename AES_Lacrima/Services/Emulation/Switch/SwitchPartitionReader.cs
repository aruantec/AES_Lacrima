using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Reads Nintendo Switch PFS0 / HFS0 partition layouts (NSP, XCI secure partition, etc.).
/// </summary>
internal static class SwitchPartitionReader
{
    private const uint Pfs0Magic = 0x30534650; // "PFS0"
    private const uint Hfs0Magic = 0x30534648; // "HFS0"

    internal readonly record struct PartitionFileEntry(string Name, long DataOffset, long Size);

    internal static bool TryRead(Stream stream, long baseOffset, out IReadOnlyList<PartitionFileEntry> entries)
    {
        entries = [];
        if (!stream.CanSeek)
            return false;

        try
        {
            Span<byte> header = stackalloc byte[0x10];
            if (!ReadAt(stream, baseOffset, header))
                return false;

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic != Pfs0Magic && magic != Hfs0Magic)
                return false;

            var fileCount = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var stringTableSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (fileCount <= 0 || fileCount > 512 || stringTableSize < 0 || stringTableSize > 0x400000)
                return false;

            var entryTableSize = fileCount * 0x18;
            var headerSize = 0x10 + entryTableSize + stringTableSize;
            var table = new byte[entryTableSize + stringTableSize];
            if (!ReadAt(stream, baseOffset + 0x10, table))
                return false;

            var stringTable = table.AsSpan(entryTableSize, stringTableSize);
            var list = new List<PartitionFileEntry>(fileCount);
            var dataBase = baseOffset + headerSize;

            for (var i = 0; i < fileCount; i++)
            {
                var entryOffset = i * 0x18;
                var fileOffset = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(entryOffset, 8));
                var fileSize = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(entryOffset + 8, 8));
                var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(entryOffset + 0x10, 4));
                if (nameOffset < 0 || nameOffset >= stringTableSize)
                    continue;

                var name = ReadNullTerminatedAscii(stringTable[nameOffset..]);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                list.Add(new PartitionFileEntry(name, dataBase + fileOffset, fileSize));
            }

            entries = list;
            return list.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadAtOffset(string filePath, long baseOffset, out IReadOnlyList<PartitionFileEntry> entries)
    {
        entries = [];
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryRead(stream, baseOffset, out entries);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end < 0)
            end = data.Length;
        return end == 0 ? string.Empty : Encoding.ASCII.GetString(data[..end]);
    }

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;

        stream.Position = offset;
        return stream.Read(buffer) == buffer.Length;
    }
}
