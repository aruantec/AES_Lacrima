using AES_Lacrima.Helpers;
using AES_Lacrima.Serialization;
using log4net;
using System;
using System.Collections.Generic;
using System.Text.Json;
using AES_Core.Logging;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// In-memory hash and serial indexes built from embedded <c>Database/*.json</c> title lists.
/// </summary>
internal sealed class EmulationHashTitleDatabase
{
    private static readonly ILog Log = LogHelper.For(typeof(EmulationHashTitleDatabase));

    private readonly Dictionary<string, string> _byMd5;
    private readonly Dictionary<string, string> _bySha1;
    private readonly Dictionary<string, string> _byCrc;
    private readonly Dictionary<string, string> _bySerial;

    private EmulationHashTitleDatabase(
        Dictionary<string, string> byMd5,
        Dictionary<string, string> bySha1,
        Dictionary<string, string> byCrc,
        Dictionary<string, string> bySerial)
    {
        _byMd5 = byMd5;
        _bySha1 = bySha1;
        _byCrc = byCrc;
        _bySerial = bySerial;
    }

    public static EmulationHashTitleDatabase Load(string fileName, string logLabel)
    {
        var byMd5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySha1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byCrc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySerial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = EmbeddedDatabaseResource.ReadText(fileName);
        if (string.IsNullOrWhiteSpace(json))
        {
            Log.Warn($"{logLabel} ({fileName}) was not found.");
            return new EmulationHashTitleDatabase(byMd5, bySha1, byCrc, bySerial);
        }

        try
        {
            var entries = JsonSerializer.Deserialize(json, RomHashTitleDatabaseJsonContext.Default.ListRomHashTitleEntry) ?? [];
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry?.Title))
                    continue;

                var title = entry.Title.Trim();
                Register(byMd5, NormalizeHash(entry.Md5), title);
                Register(bySha1, NormalizeHash(entry.Sha1), title);
                Register(byCrc, NormalizeCrc(entry.Crc), title);
                Register(bySerial, NormalizeSerial(entry.Serial), title);
            }

            Log.Info($"{logLabel} loaded {entries.Count} rows from {fileName}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to parse {logLabel} ({fileName}).", ex);
        }

        return new EmulationHashTitleDatabase(byMd5, bySha1, byCrc, bySerial);
    }

    public string? TryResolve(RomInfo romInfo)
    {
        if (TryResolveSerial(romInfo.GameId, out var serialTitle))
            return serialTitle;

        foreach (var hashes in EnumerateHashCandidates(romInfo))
        {
            if (TryResolveHashes(hashes.Md5, hashes.Sha1, hashes.Crc32, out var hashTitle))
                return hashTitle;
        }

        return null;
    }

    private static IEnumerable<(string? Md5, string? Sha1, string? Crc32)> EnumerateHashCandidates(RomInfo romInfo)
    {
        yield return (romInfo.Md5, romInfo.Sha1, romInfo.Crc32);
        yield return (romInfo.AltMd5, romInfo.AltSha1, romInfo.AltCrc32);
        yield return (romInfo.ArchiveMd5, romInfo.ArchiveSha1, romInfo.ArchiveCrc32);
    }

    public bool TryResolveSerial(string? serial, out string? title)
    {
        title = null;
        var normalized = NormalizeSerial(serial);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (_bySerial.TryGetValue(normalized, out title))
            return true;

        // Redump sometimes appends "/P" or "GH" suffixes to serial numbers.
        var trimmed = normalized;
        int slash = trimmed.IndexOf('/');
        if (slash > 0)
            trimmed = trimmed[..slash];

        while (trimmed.Length > 4 && char.IsLetter(trimmed[^1]))
            trimmed = trimmed[..^1];

        return _bySerial.TryGetValue(trimmed, out title);
    }

    private bool TryResolveHashes(string? md5, string? sha1, string? crc32, out string? title)
    {
        title = null;
        if (!string.IsNullOrWhiteSpace(md5) &&
            _byMd5.TryGetValue(NormalizeHash(md5)!, out var fromMd5))
        {
            title = fromMd5;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sha1) &&
            _bySha1.TryGetValue(NormalizeHash(sha1)!, out var fromSha1))
        {
            title = fromSha1;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(crc32) &&
            _byCrc.TryGetValue(NormalizeCrc(crc32)!, out var fromCrc))
        {
            title = fromCrc;
            return true;
        }

        return false;
    }

    private static void Register(Dictionary<string, string> map, string? key, string title)
    {
        if (string.IsNullOrWhiteSpace(key) || map.ContainsKey(key))
            return;

        map[key] = title;
    }

    internal static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    internal static string? NormalizeCrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        return trimmed.ToUpperInvariant();
    }

    internal static string? NormalizeSerial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim()
            .Replace('_', '-')
            .Replace('.', '-')
            .ToUpperInvariant();
    }
}
