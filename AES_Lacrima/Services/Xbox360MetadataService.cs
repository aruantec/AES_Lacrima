using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AES_Core.DI;

namespace AES_Lacrima.Services;

public sealed record Xbox360GameMetadata(
    string? TitleId,
    string? MediaId,
    string SourcePath);

[AutoRegister]
public partial class Xbox360MetadataService
{
    private const uint Xex2Magic = 0x58455832;
    private const uint ExecutionInfoSearchId = 0x00040006;
    private const int MaxXexHeaderDirectoryEntries = 4096;
    private const int IsoSectorSize = 0x800;
    private const int MaxDirectoryBytes = 32 * 1024 * 1024;
    private const int MaxExtractedFileBytes = 64 * 1024 * 1024;
    private const int IsoBaseSector = 0x20;
    private const int IsoScanChunkSize = 1024 * 1024;
    private const int IsoScanWindowSize = 512 * 1024;
    private const long MaxIsoScanBytes = 512L * 1024 * 1024;
    private static readonly int[] XgdMagicSectors = [0x20, 0x4120, 0x1FB40, 0x30620];
    private static readonly byte[] XgdMagic = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    public Xbox360GameMetadata? TryReadGameMetadata(string? gamePath, ISet<string>? knownTitleIds = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return null;

        var normalizedKnownIds = NormalizeKnownTitleIds(knownTitleIds);

        try
        {
            var fullPath = Path.GetFullPath(gamePath);

            if (File.Exists(fullPath))
            {
                var ext = Path.GetExtension(fullPath);
                if (ext.Equals(".xex", StringComparison.OrdinalIgnoreCase))
                {
                    var fromXex = TryReadFromXexFile(fullPath, normalizedKnownIds);
                    if (fromXex != null)
                        return fromXex;
                }
                else if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xiso", StringComparison.OrdinalIgnoreCase))
                {
                    var fromIso = TryReadFromIsoFile(fullPath, normalizedKnownIds);
                    if (fromIso != null)
                        return fromIso;
                }
            }
            else if (Directory.Exists(fullPath))
            {
                var defaultXex = Directory
                    .EnumerateFiles(fullPath, "default.xex", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(defaultXex))
                {
                    var fromDirectoryXex = TryReadFromXexFile(defaultXex, normalizedKnownIds);
                    if (fromDirectoryXex != null)
                        return fromDirectoryXex;
                }
            }

            var pathTitleId = TryGetTitleIdFromPath(fullPath, normalizedKnownIds);
            if (!string.IsNullOrWhiteSpace(pathTitleId))
                return new Xbox360GameMetadata(pathTitleId, null, fullPath);
        }
        catch
        {
        }

        return null;
    }

    private static Xbox360GameMetadata? TryReadFromXexFile(string xexPath, HashSet<string> knownTitleIds)
    {
        try
        {
            var bytes = File.ReadAllBytes(xexPath);
            return TryBuildMetadataFromExecutionInfo(TryReadExecutionInfo(bytes, 0), xexPath, knownTitleIds);
        }
        catch
        {
            return null;
        }
    }

    private static Xbox360GameMetadata? TryReadFromIsoFile(string isoPath, HashSet<string> knownTitleIds)
    {
        try
        {
            using var stream = File.OpenRead(isoPath);
            if (!TryReadXgdLayout(stream, out var layout))
                return null;

            var xexData = TryExtractFileFromXdvdfs(stream, layout, "default.xex");
            if (xexData is { Length: >= 0x20 })
            {
                var fromExtracted = TryBuildMetadataFromExecutionInfo(TryReadExecutionInfo(xexData, 0), isoPath, knownTitleIds);
                if (fromExtracted != null)
                    return fromExtracted;
            }

            stream.Position = 0;
            return TryScanIsoForXexExecutionInfo(stream, isoPath, knownTitleIds);
        }
        catch
        {
            return null;
        }
    }

    private static Xbox360GameMetadata? TryScanIsoForXexExecutionInfo(Stream stream, string sourcePath, HashSet<string> knownTitleIds)
    {
        var scanLimit = Math.Min(stream.Length, MaxIsoScanBytes);
        if (scanLimit <= 0)
            return null;

        var buffer = new byte[IsoScanChunkSize + 3];
        var carry = 0;
        long scanned = 0;

        while (scanned < scanLimit)
        {
            var remaining = scanLimit - scanned;
            var toRead = (int)Math.Min(IsoScanChunkSize, remaining);
            var read = stream.Read(buffer, carry, toRead);
            if (read <= 0)
                break;

            var total = carry + read;
            var chunkStart = stream.Position - read;

            for (var i = 0; i <= total - 4; i++)
            {
                if (buffer[i] != (byte)'X' || buffer[i + 1] != (byte)'E' || buffer[i + 2] != (byte)'X' || buffer[i + 3] != (byte)'2')
                    continue;

                var absoluteOffset = chunkStart - carry + i;
                var candidate = TryReadExecutionInfoFromStreamWindow(stream, absoluteOffset);
                var metadata = TryBuildMetadataFromExecutionInfo(candidate, sourcePath, knownTitleIds);
                if (metadata != null)
                    return metadata;
            }

            scanned += read;
            carry = Math.Min(3, total);
            if (carry > 0)
                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }

        return null;
    }

    private static (uint TitleId, uint MediaId)? TryReadExecutionInfoFromStreamWindow(Stream stream, long absoluteOffset)
    {
        if (absoluteOffset < 0 || absoluteOffset >= stream.Length)
            return null;

        var originalPosition = stream.Position;
        try
        {
            stream.Position = absoluteOffset;
            var bytesToRead = (int)Math.Min(IsoScanWindowSize, stream.Length - absoluteOffset);
            if (bytesToRead < 0x20)
                return null;

            var window = new byte[bytesToRead];
            var totalRead = 0;
            while (totalRead < bytesToRead)
            {
                var read = stream.Read(window, totalRead, bytesToRead - totalRead);
                if (read <= 0)
                    break;

                totalRead += read;
            }

            if (totalRead < 0x20)
                return null;

            if (totalRead != window.Length)
                Array.Resize(ref window, totalRead);

            return TryReadExecutionInfo(window, 0);
        }
        catch
        {
            return null;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static Xbox360GameMetadata? TryBuildMetadataFromExecutionInfo((uint TitleId, uint MediaId)? executionInfo, string sourcePath, HashSet<string> knownTitleIds)
    {
        if (executionInfo == null)
            return null;

        var titleIdValue = executionInfo.Value.TitleId;
        if (!LooksLikeXbox360TitleId(titleIdValue))
            return null;

        var titleId = titleIdValue.ToString("X8", CultureInfo.InvariantCulture);
        if (knownTitleIds.Count > 0 && !knownTitleIds.Contains(titleId))
            return null;

        var mediaId = executionInfo.Value.MediaId.ToString("X8", CultureInfo.InvariantCulture);
        return new Xbox360GameMetadata(titleId, mediaId, sourcePath);
    }

    private static bool TryReadXgdLayout(Stream stream, out (long BaseSector, uint RootDirSector, uint RootDirSize) layout)
    {
        layout = default;

        foreach (var magicSector in XgdMagicSectors)
        {
            if (!TryReadSector(stream, magicSector, out var sector))
                continue;

            if (!HasXgdMagic(sector))
                continue;

            var rootDirSector = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(20, 4));
            var rootDirSize = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(24, 4));

            if (rootDirSize == 0 || rootDirSize > MaxDirectoryBytes)
                continue;

            var baseSector = magicSector - IsoBaseSector;
            if (baseSector < 0)
                continue;

            layout = (baseSector, rootDirSector, rootDirSize);
            return true;
        }

        return false;
    }

    private static bool HasXgdMagic(byte[] sector)
    {
        if (sector.Length < IsoSectorSize)
            return false;

        if (!sector.AsSpan(0, XgdMagic.Length).SequenceEqual(XgdMagic))
            return false;

        var tailOffset = IsoSectorSize - XgdMagic.Length;
        return sector.AsSpan(tailOffset, XgdMagic.Length).SequenceEqual(XgdMagic);
    }

    private static byte[]? TryExtractFileFromXdvdfs(Stream stream, (long BaseSector, uint RootDirSector, uint RootDirSize) layout, string fileName)
    {
        var rootDirectoryData = ReadSectorRegion(stream, layout.BaseSector + layout.RootDirSector, layout.RootDirSize);
        if (rootDirectoryData == null || rootDirectoryData.Length == 0)
            return null;

        var stack = new Stack<(byte[] Data, uint Offset)>();
        stack.Push((rootDirectoryData, 0));

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!TryReadDirectoryEntry(node.Data, node.Offset, out var entry))
                continue;

            if ((entry.Attributes & 0x10) == 0 &&
                entry.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Size == 0 || entry.Size > MaxExtractedFileBytes)
                    return null;

                return ReadSectorRegion(stream, layout.BaseSector + entry.Sector, entry.Size);
            }

            if (entry.RightChild != 0 && entry.RightChild != ushort.MaxValue && entry.RightChild * 4U < node.Data.Length)
                stack.Push((node.Data, entry.RightChild));

            if (entry.LeftChild != 0 && entry.LeftChild != ushort.MaxValue && entry.LeftChild * 4U < node.Data.Length)
                stack.Push((node.Data, entry.LeftChild));

            if ((entry.Attributes & 0x10) != 0 && entry.Size > 0 && entry.Size <= MaxDirectoryBytes)
            {
                var subDirectoryData = ReadSectorRegion(stream, layout.BaseSector + entry.Sector, entry.Size);
                if (subDirectoryData is { Length: > 0 })
                    stack.Push((subDirectoryData, 0));
            }
        }

        return null;
    }

    private static byte[]? ReadSectorRegion(Stream stream, long startSector, uint length)
    {
        if (length == 0)
            return Array.Empty<byte>();

        if (length > int.MaxValue)
            return null;

        var output = new byte[length];
        var sectors = (length + IsoSectorSize - 1) / IsoSectorSize;
        uint copied = 0;

        for (uint i = 0; i < sectors; i++)
        {
            if (!TryReadSector(stream, startSector + i, out var sector))
                return null;

            var remaining = length - copied;
            var toCopy = Math.Min((uint)IsoSectorSize, remaining);
            Buffer.BlockCopy(sector, 0, output, (int)copied, (int)toCopy);
            copied += toCopy;
        }

        return output;
    }

    private static bool TryReadSector(Stream stream, long sector, out byte[] sectorData)
    {
        sectorData = new byte[IsoSectorSize];
        if (sector < 0)
            return false;

        var byteOffset = checked(sector * IsoSectorSize);
        if (byteOffset < 0 || byteOffset + IsoSectorSize > stream.Length)
            return false;

        stream.Position = byteOffset;
        var read = stream.Read(sectorData, 0, IsoSectorSize);
        return read == IsoSectorSize;
    }

    private static bool TryReadDirectoryEntry(byte[] directoryData, uint offset, out (ushort LeftChild, ushort RightChild, uint Sector, uint Size, byte Attributes, string Name) entry)
    {
        entry = default;

        var entryOffset = offset * 4U;
        if (entryOffset + 14 > directoryData.Length)
            return false;

        var span = directoryData.AsSpan((int)entryOffset);
        var left = BinaryPrimitives.ReadUInt16LittleEndian(span[..2]);
        var right = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
        var sector = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
        var attributes = span[12];
        var nameLength = span[13];

        if (nameLength == 0)
            return false;

        var nameOffset = entryOffset + 14;
        if (nameOffset + nameLength > directoryData.Length)
            return false;

        var name = System.Text.Encoding.ASCII.GetString(directoryData, (int)nameOffset, nameLength);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        entry = (left, right, sector, size, attributes, name);
        return true;
    }



    private static (uint TitleId, uint MediaId)? TryReadExecutionInfo(byte[] data, int xexOffset)
    {
        if (data.Length < xexOffset + 0x18)
            return null;

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(xexOffset, 4));
        if (magic != Xex2Magic)
            return null;

        var headerDirectoryEntryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(xexOffset + 20, 4));
        if (headerDirectoryEntryCount == 0 || headerDirectoryEntryCount > MaxXexHeaderDirectoryEntries)
            return null;

        var headerDirectoryOffset = xexOffset + 0x18;
        var maxEntriesByBuffer = (data.Length - headerDirectoryOffset) / 8;
        if (maxEntriesByBuffer <= 0)
            return null;

        var entriesToRead = (int)Math.Min(headerDirectoryEntryCount, (uint)maxEntriesByBuffer);

        for (var i = 0; i < entriesToRead; i++)
        {
            var entryOffset = headerDirectoryOffset + (i * 8);
            if (entryOffset + 8 > data.Length)
                break;

            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entryOffset, 4));
            var executionOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entryOffset + 4, 4));

            if (value != ExecutionInfoSearchId)
                continue;

            if (executionOffset == 0)
                continue;

            var absoluteExecutionOffset = checked(xexOffset + (int)executionOffset);
            if (absoluteExecutionOffset < 0 || absoluteExecutionOffset + 20 > data.Length)
                continue;

            var mediaId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(absoluteExecutionOffset, 4));
            var titleId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(absoluteExecutionOffset + 12, 4));
            return (titleId, mediaId);
        }

        return null;
    }

    private static HashSet<string> NormalizeKnownTitleIds(ISet<string>? knownTitleIds)
    {
        if (knownTitleIds == null || knownTitleIds.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return knownTitleIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? TryGetTitleIdFromPath(string fullPath, HashSet<string> knownTitleIds)
    {
        var contentMatch = Regex.Match(fullPath, @"[\\/]0000000000000000[\\/](?<id>[0-9A-Fa-f]{8})[\\/]", RegexOptions.IgnoreCase);
        if (contentMatch.Success)
        {
            var id = contentMatch.Groups["id"].Value.ToUpperInvariant();
            if (knownTitleIds.Count == 0 || knownTitleIds.Contains(id))
                return id;
        }

        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            var match = Regex.Match(segment, "\\b([0-9A-Fa-f]{8})\\b");
            if (!match.Success)
                continue;

            var id = match.Groups[1].Value.ToUpperInvariant();
            if (knownTitleIds.Count == 0 || knownTitleIds.Contains(id))
                return id;
        }

        return null;
    }

    private static bool LooksLikeXbox360TitleId(uint value)
    {
        if (value == 0 || value == uint.MaxValue)
            return false;

        // Common false positive observed when parsing invalid buffers.
        if (value == 0x524F4D20) // "ROM "
            return false;

        return true;
    }
}
