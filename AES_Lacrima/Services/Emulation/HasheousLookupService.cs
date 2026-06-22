using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;

namespace AES_Lacrima.Services.Emulation;

internal sealed class HasheousMatch
{
    public required string Name { get; init; }
    public string? PlatformName { get; init; }
    public string? TheGamesDbId { get; init; }
    public string? IgdbId { get; init; }
}

internal static class HasheousLookupService
{
    private static readonly ILog Log = LogHelper.For(typeof(HasheousLookupService));
    private const string LookupUrl = "https://hasheous.org/api/v1/Lookup/ByHash";
    private const string UserAgent = "Mozilla/5.0 (compatible; AES_Lacrima/1.0; +https://github.com/AES-Team/AES_Lacrima)";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<HasheousMatch?> TryLookupAsync(RomInfo romInfo, CancellationToken cancellationToken)
    {
        var payload = BuildLookupPayload(romInfo);
        if (payload == null)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, LookupUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"Hasheous lookup returned {(int)response.StatusCode} for '{romInfo.FilePath}'.");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonSerializer.DeserializeAsync<HasheousLookupResponse>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document == null || string.IsNullOrWhiteSpace(document.Name))
                return null;

            var tgdbId = FindMappedGameId(document.Metadata, "TheGamesDb");
            var igdbId = FindMappedGameId(document.Metadata, "IGDB");

            return new HasheousMatch
            {
                Name = document.Name.Trim(),
                PlatformName = document.Platform?.Name?.Trim(),
                TheGamesDbId = tgdbId,
                IgdbId = igdbId
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("Hasheous lookup failed.", ex);
            return null;
        }
    }

    internal static string? BuildLookupPayload(RomInfo romInfo)
    {
        var entries = new List<Dictionary<string, string>>();

        var romEntry = CreateHashEntry(romInfo.Md5, romInfo.Sha1, romInfo.Crc32);
        if (romEntry.Count > 0)
            entries.Add(romEntry);

        var archiveEntry = CreateHashEntry(romInfo.ArchiveMd5, romInfo.ArchiveSha1, romInfo.ArchiveCrc32);
        if (archiveEntry.Count > 0 && !HashEntriesEquivalent(romEntry, archiveEntry))
            entries.Add(archiveEntry);

        if (!string.IsNullOrWhiteSpace(romInfo.NormSha1) &&
            (romInfo.Format == RomFormat.N64 || romInfo.Format == RomFormat.Z64 || romInfo.Format == RomFormat.V64))
        {
            var normEntry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sha1"] = NormalizeHash(romInfo.NormSha1)
            };
            if (!entries.Any(entry => entry.TryGetValue("sha1", out var sha1) &&
                                      string.Equals(sha1, normEntry["sha1"], StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(normEntry);
            }
        }

        return entries.Count > 0 ? JsonSerializer.Serialize(entries) : null;
    }

    private static Dictionary<string, string> CreateHashEntry(string? md5, string? sha1, string? crc32)
    {
        var entry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(md5))
            entry["md5"] = NormalizeHash(md5);
        if (!string.IsNullOrWhiteSpace(sha1))
            entry["sha1"] = NormalizeHash(sha1);
        if (!string.IsNullOrWhiteSpace(crc32))
            entry["crc"] = NormalizeHash(crc32);

        return entry;
    }

    private static bool HashEntriesEquivalent(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return false;

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other))
                return false;

            if (!string.Equals(pair.Value, other, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string? FindMappedGameId(IReadOnlyList<HasheousMetadataEntry>? metadata, string source)
    {
        if (metadata == null || metadata.Count == 0)
            return null;

        foreach (var entry in metadata)
        {
            if (!string.Equals(entry.ObjectType, "Game", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(entry.Status, "Mapped", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            return entry.Id.Trim();
        }

        return null;
    }

    private static string NormalizeHash(string value)
        => new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

    private sealed class HasheousLookupResponse
    {
        public string? Name { get; set; }
        public HasheousPlatformResponse? Platform { get; set; }
        public List<HasheousMetadataEntry>? Metadata { get; set; }
    }

    private sealed class HasheousPlatformResponse
    {
        public string? Name { get; set; }
    }

    private sealed class HasheousMetadataEntry
    {
        [JsonPropertyName("objectType")]
        public string? ObjectType { get; set; }

        public string? Source { get; set; }
        public string? Status { get; set; }
        public string? Id { get; set; }
    }
}

internal static class TheGamesDbCoverService
{
    private const string BoxArtBaseUrl = "https://cdn.thegamesdb.net/images/original/boxart/front/";

    public static IEnumerable<string> BuildCoverUrls(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
            yield break;

        var normalized = gameId.Trim();
        yield return $"{BoxArtBaseUrl}{normalized}-1.jpg";
        yield return $"{BoxArtBaseUrl}{normalized}-2.jpg";
    }

    public static async Task<byte[]?> TryDownloadCoverAsync(string gameId, CancellationToken cancellationToken)
    {
        foreach (var url in BuildCoverUrls(gameId))
        {
            var bytes = await EmulationCoverImageDownload.TryDownloadValidatedCoverAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (bytes != null)
                return bytes;
        }

        return null;
    }
}
