using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AES_Lacrima.Services.Emulation.Switch;

/// <summary>
/// Extracts Nintendo Switch title IDs and display names from ROM containers (NSP, XCI, NCA, etc.).
/// Uses Eden-compatible keys when available for NACP application titles.
/// </summary>
public static class SwitchRomMetadataReader
{
    private static readonly string[] SwitchExtensions =
    [
        ".nsp",
        ".xci",
        ".nca",
        ".nsz",
        ".xcz",
        ".nspd"
    ];

    private static readonly Regex TitleIdInNameRegex = new(
        @"\[(?<id>[0-9A-Fa-f]{16})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EmbeddedTitleIdRegex = new(
        @"(?<id>01[0-9A-Fa-f]{14})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSwitchFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (Directory.Exists(filePath))
            return true;

        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(extension) &&
               SwitchExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static SwitchRomMetadataResult TryRead(string? filePath)
    {
        filePath = NormalizePath(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            return SwitchRomMetadataResult.Empty;

        if (Directory.Exists(filePath))
        {
            var installed = TryReadInstalledDirectory(filePath);
            if (installed.HasTitleId || !string.IsNullOrWhiteSpace(installed.DisplayTitle))
                return installed;
        }
        else if (!File.Exists(filePath))
        {
            return SwitchRomMetadataResult.Empty;
        }

        var fromContainers = TryReadFromContainers(filePath);
        var fromName = TryReadFromFileName(filePath);
        var merged = MergeResults(fromContainers, fromName, filePath);

        if (!string.IsNullOrWhiteSpace(merged.DisplayTitle))
            return merged;

        var officialTitle = SwitchLibHacMetadataReader.TryReadApplicationTitle(filePath);
        if (string.IsNullOrWhiteSpace(officialTitle))
            return merged;

        return BuildResult(merged.TitleId, officialTitle, filePath);
    }

    private static SwitchRomMetadataResult MergeResults(
        SwitchRomMetadataResult primary,
        SwitchRomMetadataResult secondary,
        string filePath)
    {
        var titleId = !string.IsNullOrWhiteSpace(primary.TitleId) ? primary.TitleId : secondary.TitleId;
        var displayTitle = !string.IsNullOrWhiteSpace(primary.DisplayTitle)
            ? primary.DisplayTitle
            : secondary.DisplayTitle;

        return BuildResult(titleId, displayTitle, filePath);
    }

    private static SwitchRomMetadataResult TryReadInstalledDirectory(string directoryPath)
    {
        try
        {
            var nacp = Directory.EnumerateFiles(directoryPath, "*.nacp", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (nacp != null)
            {
                var nacpTitle = SwitchNacpReader.TryReadTitleFromFile(nacp);
                var titleId = ExtractTitleIdFromText(Path.GetFileNameWithoutExtension(directoryPath))
                              ?? ExtractTitleIdFromText(nacp);
                if (!string.IsNullOrWhiteSpace(nacpTitle) || !string.IsNullOrWhiteSpace(titleId))
                    return BuildResult(titleId, nacpTitle, directoryPath);
            }

            var cnmt = Directory.EnumerateFiles(directoryPath, "*.cnmt.nca", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directoryPath, "*.nca", SearchOption.TopDirectoryOnly))
                .FirstOrDefault();
            if (cnmt != null)
                return TryRead(cnmt);
        }
        catch
        {
            // ignore
        }

        return SwitchRomMetadataResult.Empty;
    }

    private static SwitchRomMetadataResult TryReadFromContainers(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".nca" => TryReadSingleNca(filePath),
            ".xci" => TryReadXci(filePath),
            ".nsp" or ".nsz" or ".xcz" => TryReadPartitionedPackage(filePath),
            _ => TryReadPartitionedPackage(filePath)
        };
    }

    private static SwitchRomMetadataResult TryReadSingleNca(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!SwitchNcaHeaderReader.TryReadAt(stream, 0, out var header) || !header.IsValid)
            return SwitchRomMetadataResult.Empty;

        return BuildResult(header.TitleId, null, filePath);
    }

    private static SwitchRomMetadataResult TryReadPartitionedPackage(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (SwitchPartitionReader.TryRead(stream, 0, out var rootEntries))
        {
            var result = TryReadFromPartitionEntries(stream, rootEntries, filePath);
            if (result.HasTitleId)
                return result;
        }

        if (TryReadPartitionAtPfs0Offset(stream, filePath, out var nestedResult))
            return nestedResult;

        return TryScanForNcaHeaders(stream, filePath);
    }

    private static bool TryReadPartitionAtPfs0Offset(Stream stream, string filePath, out SwitchRomMetadataResult result)
    {
        result = SwitchRomMetadataResult.Empty;
        if (!TryFindPartitionMagicOffset(stream, 0x30534650, out var pfs0Offset))
            return false;

        if (!SwitchPartitionReader.TryRead(stream, pfs0Offset, out var entries))
            return false;

        result = TryReadFromPartitionEntries(stream, entries, filePath);
        return result.HasTitleId;
    }

    private static bool TryFindPartitionMagicOffset(Stream stream, uint magic, out long offset)
    {
        offset = 0;
        const int scanSize = 32 * 1024 * 1024;
        var length = Math.Min(stream.Length, scanSize);
        if (length < 0x10)
            return false;

        var buffer = new byte[Math.Min(length, 4 * 1024 * 1024)];
        stream.Position = 0;
        var totalRead = 0L;

        while (totalRead < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - totalRead);
            var read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
                break;

            for (var i = 0; i <= read - 4; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i, 4)) != magic)
                    continue;

                offset = totalRead + i;
                return true;
            }

            totalRead += read;
            if (read < toRead)
                break;
        }

        return false;
    }

    private static SwitchRomMetadataResult TryReadXci(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long[] hfsOffsets = [0xF000, 0x10000, 0xE000, 0x1000];

        foreach (var offset in hfsOffsets)
        {
            if (offset >= stream.Length)
                continue;

            if (!SwitchPartitionReader.TryRead(stream, offset, out var entries))
                continue;

            var result = TryReadFromPartitionEntries(stream, entries, filePath);
            if (result.HasTitleId)
                return result;
        }

        return TryScanForNcaHeaders(stream, filePath);
    }

    private static SwitchRomMetadataResult TryReadFromPartitionEntries(
        Stream stream,
        IReadOnlyList<SwitchPartitionReader.PartitionFileEntry> entries,
        string filePath)
    {
        string? programTitleId = null;
        string? controlTitleId = null;
        string? metaTitleId = null;
        string? anyTitleId = null;

        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Size < 0x400 || entry.DataOffset < 0 || entry.DataOffset + 0x400 > stream.Length)
                continue;

            if (!SwitchNcaHeaderReader.TryReadAt(stream, entry.DataOffset, out var header) || !header.IsValid)
                continue;

            anyTitleId ??= header.TitleId;
            switch (header.ContentType)
            {
                case SwitchNcaHeaderReader.ContentTypeProgram:
                    programTitleId ??= header.TitleId;
                    break;
                case SwitchNcaHeaderReader.ContentTypeControl:
                    controlTitleId ??= header.TitleId;
                    break;
                case SwitchNcaHeaderReader.ContentTypeMeta:
                    metaTitleId ??= header.TitleId;
                    break;
            }
        }

        var titleId = programTitleId ?? controlTitleId ?? metaTitleId ?? anyTitleId;
        if (string.IsNullOrWhiteSpace(titleId))
            return SwitchRomMetadataResult.Empty;

        return BuildResult(titleId, null, filePath);
    }

    private static SwitchRomMetadataResult TryScanForNcaHeaders(Stream stream, string filePath)
    {
        const int scanSize = 64 * 1024 * 1024;
        var length = Math.Min(stream.Length, scanSize);
        if (length < 0x400)
            return SwitchRomMetadataResult.Empty;

        var buffer = new byte[Math.Min(length, 4 * 1024 * 1024)];
        stream.Position = 0;
        var totalRead = 0;
        string? bestTitleId = null;

        while (totalRead < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - totalRead);
            var read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
                break;

            for (var i = 0; i <= read - 4; i++)
            {
                if (buffer[i] != (byte)'N' || buffer[i + 1] != (byte)'C' || buffer[i + 2] != (byte)'A')
                    continue;

                if (buffer[i + 3] is not ((byte)'0' or (byte)'1' or (byte)'2' or (byte)'3'))
                    continue;

                var ncaStart = totalRead + i - SwitchNcaHeaderReader.HeaderStart;
                if (ncaStart < 0)
                    continue;

                if (!SwitchNcaHeaderReader.TryReadAt(stream, ncaStart, out var header) || !header.IsValid)
                    continue;

                if (header.ContentType == SwitchNcaHeaderReader.ContentTypeProgram)
                    return BuildResult(header.TitleId, null, filePath);

                bestTitleId ??= header.TitleId;
            }

            totalRead += read;
            if (read < toRead)
                break;
        }

        return string.IsNullOrWhiteSpace(bestTitleId)
            ? SwitchRomMetadataResult.Empty
            : BuildResult(bestTitleId, null, filePath);
    }

    private static SwitchRomMetadataResult TryReadFromFileName(string filePath)
    {
        string? titleId = null;
        string? displaySource = null;

        foreach (var segment in EnumeratePathSegmentsForTitleId(filePath))
        {
            var fromSegment = ExtractTitleIdFromText(segment);
            if (string.IsNullOrWhiteSpace(fromSegment))
                continue;

            titleId = fromSegment;
            displaySource = segment;
            break;
        }

        if (string.IsNullOrWhiteSpace(titleId))
            return SwitchRomMetadataResult.Empty;

        var display = CleanDisplayTitleFromFileName(displaySource ?? Path.GetFileNameWithoutExtension(filePath), titleId);
        return BuildResult(titleId, display, filePath);
    }

    private static IEnumerable<string> EnumeratePathSegmentsForTitleId(string filePath)
    {
        yield return Path.GetFileNameWithoutExtension(filePath);

        List<string>? directories = null;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrWhiteSpace(directory))
            {
                directories ??= [];
                directories.Add(Path.GetFileName(directory));
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch
        {
            // ignore invalid paths
        }

        if (directories != null)
        {
            foreach (var name in directories)
                yield return name;
        }

        yield return filePath;
    }

    internal static string? ExtractTitleIdFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var bracket = TitleIdInNameRegex.Match(text);
        if (bracket.Success)
        {
            var candidate = bracket.Groups["id"].Value.ToUpperInvariant();
            if (SwitchNcaHeaderReader.IsValidTitleId(candidate))
                return candidate;
        }

        var embedded = EmbeddedTitleIdRegex.Match(text);
        if (!embedded.Success)
            return null;

        var embeddedId = embedded.Groups["id"].Value.ToUpperInvariant();
        return SwitchNcaHeaderReader.IsValidTitleId(embeddedId) ? embeddedId : null;
    }

    internal static string? CleanDisplayTitleFromFileName(string fileName, string? titleId)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var cleaned = fileName;
        if (!string.IsNullOrWhiteSpace(titleId))
        {
            cleaned = cleaned.Replace($"[{titleId}]", string.Empty, StringComparison.OrdinalIgnoreCase);
            cleaned = cleaned.Replace(titleId, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        cleaned = Regex.Replace(cleaned, @"\[v\d+\]", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\[.*?\]", string.Empty);
        cleaned = cleaned.Replace('_', ' ').Replace('.', ' ');
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(" -".ToCharArray());
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static SwitchRomMetadataResult BuildResult(string? titleId, string? displayTitle, string filePath)
    {
        titleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim().ToUpperInvariant();
        if (!SwitchNcaHeaderReader.IsValidTitleId(titleId))
            titleId = null;

        displayTitle = string.IsNullOrWhiteSpace(displayTitle)
            ? CleanDisplayTitleFromFileName(Path.GetFileNameWithoutExtension(filePath), titleId)
            : displayTitle.Trim();

        if (string.IsNullOrWhiteSpace(titleId) && string.IsNullOrWhiteSpace(displayTitle))
            return SwitchRomMetadataResult.Empty;

        return new SwitchRomMetadataResult(titleId, displayTitle);
    }

    private static string? NormalizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        try
        {
            return Path.GetFullPath(filePath.Trim());
        }
        catch
        {
            return filePath.Trim();
        }
    }
}

public readonly record struct SwitchRomMetadataResult(string? TitleId, string? DisplayTitle)
{
    public bool HasTitleId => !string.IsNullOrWhiteSpace(TitleId);

    public static SwitchRomMetadataResult Empty { get; } = new(null, null);
}
