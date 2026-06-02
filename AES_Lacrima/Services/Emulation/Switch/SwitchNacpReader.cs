using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Reads application titles from plaintext NACP blobs (Switchbrew layout).
/// </summary>
internal static class SwitchNacpReader
{
    private const int NacpSize = 0x4000;
    private const int LanguageBlockSize = 0x300;
    private const int ApplicationNameOffset = 0x0;
    private const int ApplicationNameSize = 0x200;

    // AmericanEnglish is the first language block.
    private const int FirstLanguageBlockOffset = 0x300;

    public static string? TryReadTitleFromFile(string nacpPath)
    {
        if (string.IsNullOrWhiteSpace(nacpPath) || !File.Exists(nacpPath))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(nacpPath);
            return TryReadTitleFromBytes(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadTitleFromBytes(ReadOnlySpan<byte> nacp)
    {
        if (nacp.Length < FirstLanguageBlockOffset + ApplicationNameSize)
            return null;

        foreach (var blockOffset in EnumerateLanguageBlockOffsets(nacp.Length))
        {
            var nameOffset = blockOffset + ApplicationNameOffset;
            if (nameOffset + ApplicationNameSize > nacp.Length)
                continue;

            var title = ReadUtf8String(nacp.Slice(nameOffset, ApplicationNameSize));
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        return null;
    }

    private static IEnumerable<int> EnumerateLanguageBlockOffsets(int length)
    {
        yield return FirstLanguageBlockOffset;

        for (var offset = FirstLanguageBlockOffset + LanguageBlockSize;
             offset + ApplicationNameSize <= length && offset < NacpSize;
             offset += LanguageBlockSize)
        {
            yield return offset;
        }
    }

    private static string? ReadUtf8String(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end < 0)
            end = data.Length;
        if (end == 0)
            return null;

        var text = Encoding.UTF8.GetString(data[..end]).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
