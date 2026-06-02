using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Reads cleartext fields from an NCA header (Switchbrew layout).
/// </summary>
internal static class SwitchNcaHeaderReader
{
    internal const int HeaderStart = 0x200;
    private const int TitleIdOffset = 0x210;
    private const int ContentTypeOffset = 0x205;

    internal const byte ContentTypeProgram = 0x00;
    internal const byte ContentTypeMeta = 0x01;
    internal const byte ContentTypeControl = 0x02;

    internal readonly record struct NcaHeaderInfo(
        string? TitleId,
        byte ContentType,
        bool IsValid);

    internal static bool TryReadAt(Stream stream, long ncaOffset, out NcaHeaderInfo info)
    {
        info = default;
        if (!stream.CanSeek || ncaOffset < 0 || ncaOffset + 0x400 > stream.Length)
            return false;

        try
        {
            Span<byte> header = stackalloc byte[0x220];
            stream.Position = ncaOffset + HeaderStart;
            if (stream.Read(header[..4]) != 4)
                return false;

            var magic = Encoding.ASCII.GetString(header[..4]);
            if (magic is not ("NCA3" or "NCA2" or "NCA1" or "NCA0"))
                return false;

            stream.Position = ncaOffset + ContentTypeOffset;
            var contentType = (byte)stream.ReadByte();
            if (contentType > 0x05)
                return false;

            stream.Position = ncaOffset + TitleIdOffset;
            Span<byte> titleBytes = stackalloc byte[8];
            if (stream.Read(titleBytes) != 8)
                return false;

            var titleId = FormatTitleId(titleBytes);
            if (string.IsNullOrWhiteSpace(titleId))
                return false;

            info = new NcaHeaderInfo(titleId, contentType, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? FormatTitleId(ReadOnlySpan<byte> titleBytes)
    {
        if (titleBytes.Length != 8)
            return null;

        var value = BinaryPrimitives.ReadUInt64LittleEndian(titleBytes);
        if (value is 0 or ulong.MaxValue)
            return null;

        return value.ToString("X16");
    }

    internal static bool IsValidTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId) || titleId.Length != 16)
            return false;

        foreach (var ch in titleId)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        // Application, patch, add-on, delta, and related content IDs start with 01.
        return titleId.StartsWith("01", StringComparison.OrdinalIgnoreCase);
    }
}
