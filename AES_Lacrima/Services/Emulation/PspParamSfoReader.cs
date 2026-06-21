using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Reads PSP <c>PARAM.SFO</c> metadata (DISC_ID, TITLE) from PBP packages and ISO images.
/// </summary>
internal static class PspParamSfoReader
{
    private const int SfoMagic = unchecked((int)0x46535000); // "\0PSF"
    private const int SfoMagicAlt = 0x00465350; // "PSF\0" (some homebrew/tools)
    private const int IsoSectorSize = 2048;
    private const int MaxScanBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding?> ShiftJis = new(TryLoadShiftJis);

    public static bool TryReadFromPbp(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        if (!fs.CanSeek || fs.Length < 0x20)
            return false;

        fs.Seek(0, SeekOrigin.Begin);
        var header = new byte[0x20];
        if (fs.Read(header, 0, header.Length) != header.Length)
            return false;

        if (header[0] != 0x00 || header[1] != (byte)'P' || header[2] != (byte)'B' || header[3] != (byte)'P')
            return false;

        uint paramOffset = BitConverter.ToUInt32(header, 0x08);
        if (paramOffset == 0 || paramOffset >= fs.Length)
            return false;

        fs.Seek(paramOffset, SeekOrigin.Begin);
        return TryReadSfo(fs, out discId, out title);
    }

    public static bool TryReadFromIso(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        if (!fs.CanSeek || fs.Length < 0x100)
            return false;

        if (TryReadFromIso9660Path(fs, out discId, out title))
            return true;

        return TryScanForSfo(fs, out discId, out title);
    }

    public static bool TryReadFromCso(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        if (!fs.CanSeek || fs.Length < 0x20)
            return false;

        fs.Seek(0, SeekOrigin.Begin);
        var header = new byte[0x20];
        if (fs.Read(header, 0, header.Length) != header.Length)
            return false;

        if (header[0] != (byte)'C' || header[1] != (byte)'I' || header[2] != (byte)'S' || header[3] != (byte)'O')
            return false;

        int headerSize = BitConverter.ToInt32(header, 0x04);
        int blockSize = BitConverter.ToInt32(header, 0x0C);
        if (headerSize <= 0 || blockSize <= 0 || headerSize + 4 > fs.Length)
            return false;

        fs.Seek(headerSize, SeekOrigin.Begin);
        var indexBytes = new byte[4];
        if (fs.Read(indexBytes, 0, 4) != 4)
            return false;

        int blockOffset = (int)(BitConverter.ToUInt32(indexBytes, 0) & 0x7FFFFFFF);
        if (blockOffset <= 0 || blockOffset >= fs.Length)
            return false;

        int toRead = (int)Math.Min(blockSize, fs.Length - blockOffset);
        var block = new byte[toRead];
        fs.Seek(blockOffset, SeekOrigin.Begin);
        if (fs.Read(block, 0, toRead) < 0x100)
            return false;

        using var blockStream = new MemoryStream(block, writable: false);
        if (TryReadFromIso9660Path(blockStream, out discId, out title))
            return true;

        return TryScanBufferForSfo(block, out discId, out title);
    }

    public static bool TryReadSfo(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        if (!fs.CanSeek || fs.Length < 0x14)
            return false;

        long startPosition = fs.Position;
        var header = new byte[0x14];
        if (fs.Read(header, 0, header.Length) != header.Length)
            return false;

        if (!IsSfoMagic(BitConverter.ToInt32(header, 0)))
            return false;

        uint keyTableOffset = BitConverter.ToUInt32(header, 0x08);
        uint dataTableOffset = BitConverter.ToUInt32(header, 0x0C);
        uint indexCount = BitConverter.ToUInt32(header, 0x10);
        if (indexCount == 0 || indexCount > 128)
            return false;

        long indexTableOffset = startPosition + 0x14;
        if (indexTableOffset + indexCount * 16L > fs.Length)
            return false;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var indexBuffer = new byte[16];

        for (uint i = 0; i < indexCount; i++)
        {
            fs.Seek(indexTableOffset + i * 16, SeekOrigin.Begin);
            if (fs.Read(indexBuffer, 0, 16) != 16)
                break;

            ushort keyOffset = BitConverter.ToUInt16(indexBuffer, 0);
            ushort dataFormat = BitConverter.ToUInt16(indexBuffer, 2);
            uint dataLength = BitConverter.ToUInt32(indexBuffer, 4);
            uint dataOffset = BitConverter.ToUInt32(indexBuffer, 12);

            var key = ReadTableString(fs, startPosition + keyTableOffset + keyOffset);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (dataFormat is 0x0404 or 0x0204 && dataLength > 0)
            {
                fs.Seek(startPosition + dataTableOffset + dataOffset, SeekOrigin.Begin);
                var raw = new byte[Math.Min(dataLength, 512)];
                if (fs.Read(raw, 0, raw.Length) == raw.Length)
                {
                    var value = DecodeSfoString(raw);
                    if (!string.IsNullOrWhiteSpace(value))
                        values[key] = value;
                }
            }
            else if (dataFormat == 0x0402 && dataLength >= 4)
            {
                var raw = new byte[4];
                fs.Seek(startPosition + dataTableOffset + dataOffset, SeekOrigin.Begin);
                if (fs.Read(raw, 0, 4) == 4)
                    values[key] = BitConverter.ToUInt32(raw, 0).ToString();
            }
        }

        if (values.TryGetValue("DISC_ID", out var id))
            discId = id;

        if (values.TryGetValue("TITLE", out var sfoTitle))
            title = sfoTitle;

        return !string.IsNullOrWhiteSpace(discId) || !string.IsNullOrWhiteSpace(title);
    }

    private static bool TryReadFromIso9660Path(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        if (!TryLocateIso9660File(fs, ["PSP_GAME", "PARAM.SFO"], out long fileOffset, out int fileLength))
            return false;

        var buffer = new byte[Math.Min(fileLength, 64 * 1024)];
        fs.Seek(fileOffset, SeekOrigin.Begin);
        if (fs.Read(buffer, 0, buffer.Length) < 0x14)
            return false;

        using var sfoStream = new MemoryStream(buffer, writable: false);
        return TryReadSfo(sfoStream, out discId, out title);
    }

    private static bool TryLocateIso9660File(Stream fs, IReadOnlyList<string> pathParts, out long fileOffset, out int fileLength)
    {
        fileOffset = 0;
        fileLength = 0;

        var pvd = ReadBytes(fs, 16L * IsoSectorSize, IsoSectorSize);
        if (pvd == null || pvd.Length < 156 + 34 || pvd[0] != 0x01)
            return false;

        if (!TryParseDirectoryRecord(pvd, 156, out uint extent, out uint length))
            return false;

        for (int partIndex = 0; partIndex < pathParts.Count; partIndex++)
        {
            var dirBytes = ReadBytes(fs, extent * (uint)IsoSectorSize, (int)length);
            if (dirBytes == null)
                return false;

            if (partIndex == pathParts.Count - 1)
                return TryFindFileEntry(dirBytes, pathParts[partIndex], extent, out fileOffset, out fileLength);

            if (!TryFindDirectoryEntry(dirBytes, pathParts[partIndex], out extent, out length))
                return false;
        }

        return false;
    }

    private static bool TryFindDirectoryEntry(byte[] dirBytes, string name, out uint extent, out uint length)
    {
        extent = 0;
        length = 0;
        return TryFindEntry(dirBytes, name, out extent, out length, out _, out bool isDirectory) && isDirectory;
    }

    private static bool TryFindFileEntry(
        byte[] dirBytes,
        string name,
        uint parentExtent,
        out long fileOffset,
        out int fileLength)
    {
        fileOffset = 0;
        fileLength = 0;

        if (!TryFindEntry(dirBytes, name, out uint extent, out uint length, out _, out bool isDirectory) || isDirectory)
            return false;

        fileOffset = (long)extent * IsoSectorSize;
        fileLength = (int)Math.Min(length, int.MaxValue);
        return fileLength > 0;
    }

    private static bool TryFindEntry(
        byte[] dirBytes,
        string name,
        out uint extent,
        out uint length,
        out int recordOffset,
        out bool isDirectory)
    {
        extent = 0;
        length = 0;
        recordOffset = 0;
        isDirectory = false;

        int idx = 0;
        while (idx + 33 <= dirBytes.Length)
        {
            int recLen = dirBytes[idx];
            if (recLen == 0)
            {
                int nextSector = ((idx / IsoSectorSize) + 1) * IsoSectorSize;
                if (nextSector <= idx)
                    break;

                idx = nextSector;
                continue;
            }

            if (idx + recLen > dirBytes.Length)
                break;

            int fileIdLen = dirBytes[idx + 32];
            if (fileIdLen > 0 && idx + 33 + fileIdLen <= dirBytes.Length)
            {
                var identifier = Encoding.ASCII.GetString(dirBytes, idx + 33, fileIdLen).Trim('\0', ' ');
                var identifierNoVersion = identifier;
                int sep = identifier.IndexOf(';', StringComparison.Ordinal);
                if (sep >= 0)
                    identifierNoVersion = identifier[..sep];

                if (string.Equals(identifierNoVersion, name, StringComparison.OrdinalIgnoreCase))
                {
                    extent = BitConverter.ToUInt32(dirBytes, idx + 2);
                    length = BitConverter.ToUInt32(dirBytes, idx + 10);
                    isDirectory = (dirBytes[idx + 25] & 0x02) != 0;
                    recordOffset = idx;
                    return true;
                }
            }

            idx += recLen;
        }

        return false;
    }

    private static bool TryParseDirectoryRecord(byte[] buffer, int offset, out uint extent, out uint length)
    {
        extent = 0;
        length = 0;
        if (offset + 34 > buffer.Length || buffer[offset] == 0)
            return false;

        extent = BitConverter.ToUInt32(buffer, offset + 2);
        length = BitConverter.ToUInt32(buffer, offset + 10);
        return extent > 0 && length > 0;
    }

    private static byte[]? ReadBytes(Stream fs, long offset, int length)
    {
        if (offset < 0 || offset >= fs.Length || length <= 0)
            return null;

        int toRead = (int)Math.Min(length, fs.Length - offset);
        var buffer = new byte[toRead];
        fs.Seek(offset, SeekOrigin.Begin);
        return fs.Read(buffer, 0, toRead) == toRead ? buffer : null;
    }

    private static bool TryScanForSfo(Stream fs, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        int scanSize = (int)Math.Min(fs.Length, MaxScanBytes);
        var buffer = new byte[scanSize];
        fs.Seek(0, SeekOrigin.Begin);
        if (fs.Read(buffer, 0, scanSize) < 0x100)
            return false;

        return TryScanBufferForSfo(buffer, out discId, out title);
    }

    private static bool TryScanBufferForSfo(byte[] buffer, out string? discId, out string? title)
    {
        discId = null;
        title = null;

        foreach (var offset in FindSfoOffsets(buffer))
        {
            using var slice = new MemoryStream(buffer, offset, buffer.Length - offset, writable: false);
            if (TryReadSfo(slice, out discId, out title))
                return true;
        }

        return false;
    }

    private static bool IsSfoMagic(int value) => value == SfoMagic || value == SfoMagicAlt;

    private static IEnumerable<int> FindSfoOffsets(byte[] buffer)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i + 0x14 <= buffer.Length; i++)
        {
            if (!IsSfoMagic(BitConverter.ToInt32(buffer, i)))
                continue;

            if (seen.Add(i))
                yield return i;
        }
    }

    private static string ReadTableString(Stream fs, long offset, int maxLength = 64)
    {
        if (offset < 0 || offset >= fs.Length)
            return string.Empty;

        fs.Seek(offset, SeekOrigin.Begin);
        var bytes = new List<byte>(maxLength);
        for (int i = 0; i < maxLength && fs.Position < fs.Length; i++)
        {
            int value = fs.ReadByte();
            if (value <= 0)
                break;

            bytes.Add((byte)value);
        }

        return bytes.Count == 0 ? string.Empty : Encoding.UTF8.GetString(bytes.ToArray());
    }

    internal static string DecodeSfoString(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0)
            return string.Empty;

        int length = raw.IndexOf((byte)0);
        if (length < 0)
            length = raw.Length;
        if (length == 0)
            return string.Empty;

        var span = raw[..length];

        try
        {
            return SanitizePspTitle(StrictUtf8.GetString(span));
        }
        catch (DecoderFallbackException)
        {
            var shiftJis = ShiftJis.Value;
            if (shiftJis != null)
            {
                try
                {
                    return SanitizePspTitle(shiftJis.GetString(span));
                }
                catch (DecoderFallbackException)
                {
                }
            }

            return SanitizePspTitle(ExtractAsciiPrefix(span));
        }
    }

    internal static string SanitizePspTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim().TrimEnd('\0', ' ');
        value = Regex.Replace(value, @"[\?\uFFFD]+$", string.Empty).TrimEnd();
        return value;
    }

    private static string ExtractAsciiPrefix(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (byte b in raw)
        {
            if (b == 0)
                break;

            if (b is >= 32 and <= 126)
                sb.Append((char)b);
            else
                break;
        }

        return sb.ToString().Trim();
    }

    private static Encoding? TryLoadShiftJis()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch
        {
            return null;
        }
    }
}
