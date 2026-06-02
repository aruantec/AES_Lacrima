using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Reads GameCube / Wii disc metadata the same way Dolphin does:
/// CreateBlobReader → read volume header at disc offset 0 → game id @ 0, magic @ 0x18 / 0x1C (big-endian).
/// </summary>
public static class DolphinDiscMetadataReader
{
    private const uint GczMagic = 0xB10BC001;
    private const uint CisoMagic = 0x4F534943; // "CISO" on disc (LE u32)
    private const uint WbfsMagic = 0x53464257; // "WBFS" as little-endian u32 (Dolphin WBFS_MAGIC)
    private const uint WiiDiscMagic = 0x5D1C9EA3;
    private const uint GameCubeDiscMagic = 0xC2339F3D;
    private const int DiscHeaderSize = 0x80;
    private const int InternalNameLength = 0x60;
    private const int GczHeaderSize = 32;

    public static DolphinDiscMetadataResult TryRead(string? filePath)
    {
        filePath = NintendoDiscMetadataHelper.NormalizeRomPath(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return DolphinDiscMetadataResult.Empty;

        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".wbfs", StringComparison.OrdinalIgnoreCase))
            return TryReadWbfs(filePath);

        if (string.Equals(extension, ".wad", StringComparison.OrdinalIgnoreCase))
            return TryReadWad(filePath);

        if (!TryOpenDiscBlobReader(filePath, out var reader) || reader == null)
            return DolphinDiscMetadataResult.Empty;

        using (reader)
        {
            var header = new byte[DiscHeaderSize];
            if (!reader.Read(0, header))
                return DolphinDiscMetadataResult.Empty;

            return ParseDiscHeader(header);
        }
    }

    public static DiscSection SniffDiscSection(string? filePath)
    {
        var result = TryRead(filePath);
        return result.Section != DiscSection.Auto ? result.Section : DiscSection.Auto;
    }

    private static DolphinDiscMetadataResult TryReadWbfs(string filePath)
    {
        try
        {
            using var file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < WbfsHeaderSize)
                return DolphinDiscMetadataResult.Empty;

            var header = new byte[WbfsHeaderSize];
            file.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != WbfsMagic || header[12] == 0)
                return DolphinDiscMetadataResult.Empty;

            var hdSectorShift = header[8];
            var wbfsShift = header[9];
            if (hdSectorShift > 31 || wbfsShift > 31)
                return DolphinDiscMetadataResult.Empty;

            var hdSectorSize = 1UL << hdSectorShift;
            var wbfsSectorSize = 1UL << wbfsShift;
            if (wbfsSectorSize < WbfsWiiSectorSize)
                return DolphinDiscMetadataResult.Empty;

            var blocksPerDisc = (WbfsWiiSectorCount * WbfsWiiSectorSize + wbfsSectorSize - 1) / wbfsSectorSize;
            var wlbaOffset = hdSectorSize + WbfsWiiDiscHeaderSize;
            var wlbaBytes = (int)(blocksPerDisc * sizeof(ushort));
            if (file.Length < (long)(wlbaOffset + (ulong)wlbaBytes))
                return DolphinDiscMetadataResult.Empty;

            file.Seek((long)wlbaOffset, SeekOrigin.Begin);
            var wlbaRaw = new byte[wlbaBytes];
            file.ReadExactly(wlbaRaw);

            var clusterIndex = BinaryPrimitives.ReadUInt16BigEndian(wlbaRaw);
            var clusterAddress = wbfsSectorSize * clusterIndex;
            if (clusterAddress + DiscHeaderSize > (ulong)file.Length)
                return DolphinDiscMetadataResult.Empty;

            file.Seek((long)clusterAddress, SeekOrigin.Begin);
            var discHeader = new byte[DiscHeaderSize];
            file.ReadExactly(discHeader);
            return ParseDiscHeader(discHeader);
        }
        catch
        {
            return DolphinDiscMetadataResult.Empty;
        }
    }

    private const ulong WbfsWiiSectorSize = 0x8000;
    private const ulong WbfsWiiSectorCount = 143432UL * 2;
    private const int WbfsWiiDiscHeaderSize = 256;
    private const int WbfsHeaderSize = 512;

    private static DolphinDiscMetadataResult TryReadWad(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 0x20)
                return DolphinDiscMetadataResult.Empty;

            var header = new byte[0x20];
            if (stream.Read(header, 0, header.Length) != header.Length)
                return DolphinDiscMetadataResult.Empty;

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x00204973u)
                return DolphinDiscMetadataResult.Empty;

            var certSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x0C));
            var crlSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x10));
            var ticketSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x14));
            var tmdOffset = AlignWadSize(0x20 + certSize) + AlignWadSize(crlSize) + AlignWadSize(ticketSize);

            if (stream.Length < tmdOffset + 8)
                return DolphinDiscMetadataResult.Empty;

            var titleId = new byte[8];
            stream.Seek(tmdOffset, SeekOrigin.Begin);
            if (stream.Read(titleId, 0, titleId.Length) != titleId.Length)
                return DolphinDiscMetadataResult.Empty;

            var gameId = ConvertWadTitleIdToGameId(titleId);
            return string.IsNullOrEmpty(gameId)
                ? DolphinDiscMetadataResult.Empty
                : new DolphinDiscMetadataResult(gameId, null, DiscSection.Wii);
        }
        catch
        {
            return DolphinDiscMetadataResult.Empty;
        }
    }

    private static DolphinDiscMetadataResult ParseDiscHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 6)
            return DolphinDiscMetadataResult.Empty;

        var gameId = FilterGameId(Encoding.ASCII.GetString(header[..6]));
        var section = DetectDiscSection(header);

        string? title = null;
        if (header.Length >= 0x20 + InternalNameLength)
        {
            var rawTitle = Encoding.ASCII.GetString(header.Slice(0x20, InternalNameLength));
            title = CleanTitle(rawTitle);
        }

        if (string.IsNullOrEmpty(gameId) && section == DiscSection.Auto)
            return DolphinDiscMetadataResult.Empty;

        if (string.IsNullOrEmpty(gameId))
            return new DolphinDiscMetadataResult(null, title, section);

        if (section == DiscSection.Auto)
            section = InferSectionFromGameId(gameId);

        return new DolphinDiscMetadataResult(gameId, title, section);
    }

    /// <summary>Matches Dolphin Volume.cpp TryCreateDisc (big-endian magic at 0x18 then 0x1C).</summary>
    public static DiscSection DetectDiscSection(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 0x18 + 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x18, 4)) == WiiDiscMagic)
            return DiscSection.Wii;

        if (header.Length >= 0x1C + 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x1C, 4)) == GameCubeDiscMagic)
            return DiscSection.GameCube;

        return DiscSection.Auto;
    }

    private static DiscSection InferSectionFromGameId(string gameId)
    {
        if (gameId.Length < 3)
            return DiscSection.Auto;

        // Wii product codes often use R (Revolution) in the third character.
        return gameId[2] is 'R' or 'W' ? DiscSection.Wii : DiscSection.GameCube;
    }

    /// <summary>Matches Dolphin VolumeDisc.cpp FilterGameID.</summary>
    public static string FilterGameId(string rawId)
    {
        if (string.IsNullOrEmpty(rawId))
            return string.Empty;

        Span<char> filtered = stackalloc char[rawId.Length];
        var length = 0;
        foreach (var character in rawId)
        {
            if (char.IsAsciiLetterOrDigit(character))
                filtered[length++] = character;
        }

        return length == 0 ? string.Empty : new string(filtered[..length]).ToUpperInvariant();
    }

    private static bool IsValidGameId(string gameId)
    {
        if (gameId.Length != 6)
            return false;

        for (var i = 0; i < 6; i++)
        {
            if (!char.IsAsciiLetterOrDigit(gameId[i]))
                return false;
        }

        return char.IsAsciiDigit(gameId[4]) || char.IsAsciiDigit(gameId[5]);
    }

    private static string ConvertWadTitleIdToGameId(ReadOnlySpan<byte> titleId)
    {
        if (titleId.Length < 8)
            return string.Empty;

        var code = Encoding.ASCII.GetString(titleId.Slice(4, 4));
        if (code.Length != 4 || !code.All(static c => char.IsAsciiLetterOrDigit(c)))
            return string.Empty;

        var region = titleId[3].ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        if (region == "00")
            region = titleId[1].ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

        var gameId = FilterGameId(code + region);
        return IsValidGameId(gameId) ? gameId : string.Empty;
    }

    private static int AlignWadSize(int size) => (size + 63) & ~63;

    private static string? CleanTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static bool TryOpenDiscBlobReader(string filePath, out IDiscBlobReader? reader)
    {
        reader = null;
        try
        {
            using var probe = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (probe.Length < 4)
                return false;

            var magicBytes = new byte[4];
            if (probe.Read(magicBytes, 0, 4) != 4)
                return false;

            var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
            if (magicLe == GczMagic)
            {
                reader = new GczDiscBlobReader(filePath);
                return reader.IsValid;
            }

            if (magicLe == CisoMagic)
            {
                reader = new CisoDiscBlobReader(filePath);
                return reader.IsValid;
            }

            if (magicLe == WbfsMagic)
            {
                reader = null;
                return false;
            }

            reader = new PlainDiscBlobReader(filePath);
            return reader.IsValid;
        }
        catch
        {
            reader?.Dispose();
            reader = null;
            return false;
        }
    }

    private interface IDiscBlobReader : IDisposable
    {
        bool IsValid { get; }
        bool Read(ulong discOffset, Span<byte> destination);
    }

    private sealed class PlainDiscBlobReader : IDiscBlobReader
    {
        private readonly FileStream _stream;

        public PlainDiscBlobReader(string path)
        {
            _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public bool IsValid => _stream.CanRead;

        public bool Read(ulong discOffset, Span<byte> destination)
        {
            if ((long)discOffset < 0 || discOffset + (ulong)destination.Length > (ulong)_stream.Length)
                return false;

            _stream.Seek((long)discOffset, SeekOrigin.Begin);
            return _stream.Read(destination) == destination.Length;
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class GczDiscBlobReader : IDiscBlobReader
    {
        private readonly FileStream _file;
        private GczHeader _header;
        private readonly ulong[] _blockPointers;
        private readonly int _dataOffset;
        private byte[]? _cachedBlock;
        private int _cachedBlockIndex = -1;

        public GczDiscBlobReader(string path)
        {
            _file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            IsValid = TryParseHeader();
            _blockPointers = IsValid ? new ulong[_header.NumBlocks] : [];
            _dataOffset = IsValid
                ? GczHeaderSize + ((int)_header.NumBlocks * (sizeof(ulong) + sizeof(uint)))
                : 0;

            if (IsValid && !ReadBlockPointers())
                IsValid = false;
        }

        public bool IsValid { get; private set; }

        public bool Read(ulong discOffset, Span<byte> destination)
        {
            if (!IsValid || destination.IsEmpty)
                return false;

            var blockSize = (ulong)_header.BlockSize;
            var blockIndex = (int)(discOffset / blockSize);
            var blockOffset = (int)(discOffset % blockSize);

            if (blockIndex < 0 || blockIndex >= _header.NumBlocks)
                return false;

            if (!TryGetDecompressedBlock(blockIndex, out var blockData))
                return false;

            if (blockOffset + destination.Length > blockData.Length)
                return false;

            blockData.AsSpan(blockOffset, destination.Length).CopyTo(destination);
            return true;
        }

        public void Dispose() => _file.Dispose();

        private bool TryParseHeader()
        {
            if (_file.Length < GczHeaderSize)
                return false;

            Span<byte> raw = stackalloc byte[GczHeaderSize];
            _file.Seek(0, SeekOrigin.Begin);
            if (_file.Read(raw) != raw.Length)
                return false;

            _header.Magic = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (_header.Magic != GczMagic)
                return false;

            _header.SubType = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4));
            _header.CompressedDataSize = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(8));
            _header.DataSize = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(16));
            _header.BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(24));
            _header.NumBlocks = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(28));

            return _header.BlockSize > 0 && _header.NumBlocks > 0;
        }

        private bool ReadBlockPointers()
        {
            _file.Seek(GczHeaderSize, SeekOrigin.Begin);
            var pointerBytes = new byte[_header.NumBlocks * sizeof(ulong)];
            if (_file.Read(pointerBytes) != pointerBytes.Length)
                return false;

            for (var i = 0; i < _header.NumBlocks; i++)
                _blockPointers[i] = BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes.AsSpan(i * sizeof(ulong), sizeof(ulong)));

            // Skip Adler32 hashes.
            _file.Seek(_header.NumBlocks * sizeof(uint), SeekOrigin.Current);
            return true;
        }

        private bool TryGetDecompressedBlock(int blockIndex, out byte[] blockData)
        {
            blockData = [];
            if (_cachedBlockIndex == blockIndex && _cachedBlock != null)
            {
                blockData = _cachedBlock;
                return true;
            }

            if (!TryReadCompressedBlock(blockIndex, out var compressed, out var storedUncompressed))
                return false;

            var output = new byte[_header.BlockSize];
            if (storedUncompressed)
            {
                compressed.AsSpan(0, Math.Min(compressed.Length, output.Length)).CopyTo(output);
            }
            else
            {
                try
                {
                    using var input = new MemoryStream(compressed);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    var totalRead = 0;
                    while (totalRead < output.Length)
                    {
                        var read = zlib.Read(output, totalRead, output.Length - totalRead);
                        if (read == 0)
                            break;
                        totalRead += read;
                    }
                }
                catch
                {
                    return false;
                }
            }

            _cachedBlockIndex = blockIndex;
            _cachedBlock = output;
            blockData = output;
            return true;
        }

        private bool TryReadCompressedBlock(int blockIndex, out byte[] compressed, out bool storedUncompressed)
        {
            compressed = [];
            storedUncompressed = false;

            var start = _blockPointers[blockIndex];
            storedUncompressed = (start & (1UL << 63)) != 0;
            if (storedUncompressed)
                start &= ~(1UL << 63);

            ulong end;
            if (blockIndex < _header.NumBlocks - 1)
                end = _blockPointers[blockIndex + 1] & ~(1UL << 63);
            else
                end = _header.CompressedDataSize;

            var size = (int)(end - start);
            if (size <= 0)
                return false;

            compressed = new byte[size];
            _file.Seek(_dataOffset + (long)start, SeekOrigin.Begin);
            return _file.Read(compressed, 0, size) == size;
        }

        private struct GczHeader
        {
            public uint Magic;
            public uint SubType;
            public ulong CompressedDataSize;
            public ulong DataSize;
            public uint BlockSize;
            public uint NumBlocks;
        }
    }

    private sealed class CisoDiscBlobReader : IDiscBlobReader
    {
        private const int CisoHeaderSize = 0x8000;
        private const int CisoMapSize = CisoHeaderSize - sizeof(uint) - 4;
        private const ushort UnusedBlockId = ushort.MaxValue;

        private readonly FileStream _file;

        public CisoDiscBlobReader(string path)
        {
            _file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            IsValid = TryParseHeader(out _blockSize, out _map);
        }

        public bool IsValid { get; }
        private readonly uint _blockSize;
        private readonly ushort[] _map;

        public bool Read(ulong discOffset, Span<byte> destination)
        {
            if (!IsValid || destination.IsEmpty)
                return false;

            var written = 0;
            while (written < destination.Length)
            {
                var offset = discOffset + (ulong)written;
                var block = offset / _blockSize;
                var blockOffset = (int)(offset % _blockSize);
                var bytesInBlock = (int)Math.Min(_blockSize - (uint)blockOffset, (uint)(destination.Length - written));

                if (block >= (ulong)_map.Length)
                    return false;

                var mapEntry = _map[(int)block];
                if (mapEntry == UnusedBlockId)
                    destination.Slice(written, bytesInBlock).Clear();
                else
                {
                    var fileOffset = CisoHeaderSize + (ulong)mapEntry * _blockSize + (ulong)blockOffset;
                    _file.Seek((long)fileOffset, SeekOrigin.Begin);
                    if (_file.Read(destination.Slice(written, bytesInBlock)) != bytesInBlock)
                        return false;
                }

                written += bytesInBlock;
            }

            return true;
        }

        public void Dispose() => _file.Dispose();

        private bool TryParseHeader(out uint blockSize, out ushort[] map)
        {
            blockSize = 0;
            map = [];
            if (_file.Length < CisoHeaderSize)
                return false;

            var header = new byte[CisoHeaderSize];
            _file.Seek(0, SeekOrigin.Begin);
            if (_file.Read(header, 0, header.Length) != header.Length)
                return false;

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CisoMagic)
                return false;

            blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (blockSize == 0)
                return false;

            map = new ushort[CisoMapSize];
            ushort used = 0;
            for (var i = 0; i < CisoMapSize; i++)
            {
                map[i] = header[8 + i] switch
                {
                    1 => used++,
                    _ => UnusedBlockId
                };
            }

            return true;
        }
    }

}

public readonly record struct DolphinDiscMetadataResult(string? GameId, string? Title, DiscSection Section)
{
    public static DolphinDiscMetadataResult Empty { get; } = new(null, null, DiscSection.Auto);
}
