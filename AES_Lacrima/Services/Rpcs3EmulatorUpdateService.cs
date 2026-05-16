using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;
using AES_Core.IO;
using log4net;

namespace AES_Lacrima.Services;

public sealed record Rpcs3UpdateState(
    string Repository,
    string? CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    IReadOnlyList<string> AvailableVersions,
    string StatusMessage,
    string EmulatorDirectory,
    string UpdateDirectory,
    string? ResolvedLauncherPath);

[AutoRegister]
public partial class Rpcs3EmulatorUpdateService
{
    private const string Repository = "https://emulationking.com/sony/ps3/emulator/rpcs3";
    private const string EmulationKingRpcs3PageEndpoint = "https://emulationking.com/sony/ps3/emulator/rpcs3";
    private const string ReleasesRepository = "https://github.com/RPCS3/rpcs3";
    private const string NightlyRepository = "https://rpcs3.net/download";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/RPCS3/rpcs3/releases?per_page=100";
    private const string CompatibilityBuildsEndpoint = "https://rpcs3.net/compatibility?b&p=1";
    private const string CompatibilityBuildsApiFallbackEndpoint = "https://rpcs3.net/compatibility?b&p=1&api=v1";
    private const string UpdateApiEndpointTemplate = "https://update.rpcs3.net/?api=v3&c={0}&os_type=windows&os_arch=64&os_version=10.0.0";
    private const string UpdateLatestEndpoint = "https://update.rpcs3.net/?api=v3&os_type=windows&os_arch=64&os_version=10.0.0";
    private const string BinaryReleasesApiEndpoint = "https://api.github.com/repos/RPCS3/rpcs3-binaries-win/releases?per_page=100";
    private const string StableCacheKey = "github:RPCS3/rpcs3";
    private const string NightlyCacheKey = "rpcs3:compatibility-builds:p1";
    private const string CacheFileName = "rpcs3-releases-cache.json";
    private const string NightlyCacheFileName = "rpcs3-nightly-cache.json";
    private const string InstalledVersionMarkerFileName = "rpcs3_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<Rpcs3EmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class ReleaseCache
    {
        public string? Repository { get; set; }
        public string? ETag { get; set; }
        public string? ReleasesJson { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
    }

    private sealed record ReleaseInfo(
        string Tag,
        bool IsPrerelease,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record CompatibilityBuildEntry(string Commit, DateTimeOffset? Date);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<Rpcs3UpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includePrereleases,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);

        try
        {
            var releases = await GetReleasesAsync(includePrereleases, forceRefresh, cancellationToken).ConfigureAwait(false);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New RPCS3 version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "RPCS3 is not installed in this section yet."
                    : $"RPCS3 is up to date ({currentVersion}).";

            return new Rpcs3UpdateState(
                Repository,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                status,
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to fetch RPCS3 update info; returning local status only.", ex);
            return new Rpcs3UpdateState(
                Repository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check RPCS3 updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    private async Task<ReleaseInfo?> TryGetLatestNightlyFromUpdateEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(UpdateLatestEndpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(json) as JsonObject;
            var version = root?["latest_build"]?["version"]?.GetValue<string>()?.Trim();
            var datetime = root?["latest_build"]?["datetime"]?.GetValue<string>()?.Trim();

            if (string.IsNullOrWhiteSpace(version))
                return null;

            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(datetime, out var parsedDate))
                publishedAt = parsedDate;

            return new ReleaseInfo(version, true, publishedAt, Array.Empty<ReleaseAsset>());
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve latest RPCS3 nightly build from update endpoint.", ex);
            return null;
        }
    }

    private static IReadOnlyList<ReleaseInfo> MergeNightlyReleases(IReadOnlyList<ReleaseInfo> primary, ReleaseInfo? latest)
    {
        if (latest == null)
            return primary.Take(10).ToList();

        var merged = new List<ReleaseInfo> { latest };
        foreach (var release in primary)
        {
            if (merged.Any(existing => string.Equals(existing.Tag, release.Tag, StringComparison.OrdinalIgnoreCase)))
                continue;

            merged.Add(release);
            if (merged.Count >= 10)
                break;
        }

        return merged.Take(10).ToList();
    }

    private async Task<IReadOnlyList<ReleaseInfo>> TryGetNightlyReleasesFromBinaryFeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(BinaryReleasesApiEndpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseBinaryReleases(json);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to fetch RPCS3 nightly builds via binaries feed fallback.", ex);
            return Array.Empty<ReleaseInfo>();
        }
    }

    private static IReadOnlyList<ReleaseInfo> PickNewestNightlyList(IReadOnlyList<ReleaseInfo> first, IReadOnlyList<ReleaseInfo> second)
    {
        if (first.Count == 0)
            return second;

        if (second.Count == 0)
            return first;

        static int GetTopBuild(IReadOnlyList<ReleaseInfo> releases)
        {
            var build = ExtractBuildNumber(releases[0].Tag);
            return int.TryParse(build, out var parsedBuild) ? parsedBuild : int.MinValue;
        }

        return GetTopBuild(second) > GetTopBuild(first) ? second : first;
    }

    public async Task<Rpcs3UpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includePrereleases,
        string? requestedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var releases = await GetReleasesAsync(includePrereleases, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new Rpcs3UpdateState(
                    Repository,
                    GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath),
                    null,
                    false,
                    Array.Empty<string>(),
                    "No RPCS3 versions found.",
                    emulatorDirectory,
                    updateDirectory,
                    noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new Rpcs3UpdateState(
                    Repository,
                    GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(10).ToList(),
                    $"Version '{requestedVersion}' was not found.",
                    emulatorDirectory,
                    updateDirectory,
                    unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);

            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new Rpcs3UpdateState(
                    Repository,
                    GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(10).ToList(),
                    "No compatible RPCS3 asset found for this OS.",
                    emulatorDirectory,
                    updateDirectory,
                    missingAssetLauncherPath);
            }

            PrepareUpdateDirectory(updateDirectory);
            var downloadedAssetPath = Path.Combine(updateDirectory, selectedAsset.Name);
            await DownloadAssetAsync(selectedAsset.DownloadUrl, downloadedAssetPath, cancellationToken).ConfigureAwait(false);

            if (IsArchive(downloadedAssetPath))
            {
                var extractDirectory = Path.Combine(updateDirectory, "extracted");
                Directory.CreateDirectory(extractDirectory);
                ExtractArchive(downloadedAssetPath, extractDirectory);
                var sourceDirectory = NormalizeExtractionRoot(extractDirectory);
                CopyDirectoryContents(sourceDirectory, emulatorDirectory);
            }
            else
            {
                var destinationPath = Path.Combine(emulatorDirectory, Path.GetFileName(downloadedAssetPath));
                File.Copy(downloadedAssetPath, destinationPath, overwrite: true);
            }

            PrepareUpdateDirectory(updateDirectory);
            SaveInstalledVersionMarker(emulatorDirectory, targetRelease.Tag);

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);

            return new Rpcs3UpdateState(
                Repository,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"RPCS3 {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("RPCS3 update failed.", ex);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            IReadOnlyList<string> fallbackVersions = Array.Empty<string>();
            string? fallbackLatest = null;

            try
            {
                var releases = await GetReleasesAsync(includePrereleases, forceRefresh: false, cancellationToken).ConfigureAwait(false);
                fallbackVersions = releases
                    .Select(static r => r.Tag)
                    .Where(static v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();
                fallbackLatest = fallbackVersions.FirstOrDefault();
            }
            catch (Exception fallbackEx)
            {
                Log.Debug("Failed to recover RPCS3 version list after update error.", fallbackEx);
            }

            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch
            {
            }

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new Rpcs3UpdateState(
                Repository,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                fallbackLatest,
                false,
                fallbackVersions,
                $"RPCS3 download/update failed: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (string EmulatorDirectory, string UpdateDirectory) EnsureDirectories(string sectionKey, string sectionTitle)
    {
        var safeSectionKey = SanitizePathPart(NormalizeSectionFolderName(string.IsNullOrWhiteSpace(sectionKey) ? "Unknown" : sectionKey));
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, safeSectionKey, "RPCS3");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(bool includePrereleases, bool forceRefresh, CancellationToken cancellationToken)
    {
        _ = includePrereleases;
        _ = forceRefresh;
        return await GetEmulationKingReleasesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetEmulationKingReleasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RPCS3Updater/1.0");

            using var response = await Client.GetAsync(EmulationKingRpcs3PageEndpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseEmulationKingReleases(html);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to fetch RPCS3 versions from EmulationKing.", ex);
            return Array.Empty<ReleaseInfo>();
        }
    }

    private static IReadOnlyList<ReleaseInfo> ParseEmulationKingReleases(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<ReleaseInfo>();

        var releases = new List<ReleaseInfo>();
        var anchorRegex = new Regex("<a[^>]*href=\"(?<href>[^\"]+)\"[^>]*>(?<text>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match match in anchorRegex.Matches(html))
        {
            var hrefRaw = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (string.IsNullOrWhiteSpace(hrefRaw))
                continue;

            if (!Uri.TryCreate(hrefRaw, UriKind.Absolute, out var downloadUri))
            {
                if (!Uri.TryCreate(new Uri(EmulationKingRpcs3PageEndpoint), hrefRaw, out downloadUri))
                    continue;
            }

            var text = Regex.Replace(match.Groups["text"].Value, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, "\\s+", " ").Trim();

            if (!text.Contains("RPCS3", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("for Windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(downloadUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                (!fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                 !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                 !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (fileName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("aarch64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = ExtractVersionFromBinaryAssetName(fileName);
            if (string.IsNullOrWhiteSpace(tag))
            {
                var nightlyFromText = Regex.Match(text, @"\b(?<v>\d+\.\d+\.\d+-\d+)\b", RegexOptions.IgnoreCase);
                if (nightlyFromText.Success)
                    tag = nightlyFromText.Groups["v"].Value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                var normalizedTagMatch = Regex.Match(tag, @"\b(?<v>\d+\.\d+\.\d+-\d+)\b", RegexOptions.IgnoreCase);
                tag = normalizedTagMatch.Success ? normalizedTagMatch.Groups["v"].Value.Trim() : null;
            }

            if (string.IsNullOrWhiteSpace(tag))
                continue;

            // Keep only nightly-style builds to avoid very old non-nightly entries.
            if (!Regex.IsMatch(tag, @"^\d+\.\d+\.\d+-\d+$", RegexOptions.IgnoreCase))
                continue;

            DateTimeOffset? publishedAt = null;
            var dateMatch = Regex.Match(text, @"\b(?<d>\d{1,2}/\d{1,2}/\d{4})\b");
            if (dateMatch.Success && DateTimeOffset.TryParseExact(dateMatch.Groups["d"].Value, new[] { "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy", "M/d/yyyy" }, null, System.Globalization.DateTimeStyles.AssumeLocal, out var parsedDate))
                publishedAt = parsedDate;

            releases.Add(new ReleaseInfo(tag, false, publishedAt, new[] { new ReleaseAsset(fileName, downloadUri.ToString()) }));
        }

        return releases
            .GroupBy(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r =>
            {
                var build = ExtractBuildNumber(r.Tag);
                return int.TryParse(build, out var parsedBuild) ? parsedBuild : int.MinValue;
            })
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetStableReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath);
        if (!forceRefresh &&
            cache?.Repository != null &&
            string.Equals(cache.Repository, StableCacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseGitHubReleases(cache.ReleasesJson!, includePrereleases: false);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RPCS3Updater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiEndpoint);
        if (!string.IsNullOrWhiteSpace(cache?.ETag) &&
            string.Equals(cache?.Repository, StableCacheKey, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        string? json;
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            json = cache!.ReleasesJson;
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            Log.Warn("Rate limit reached for RPCS3 releases; using cached releases.");
            json = cache!.ReleasesJson;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new ReleaseCache
            {
                Repository = StableCacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

        return ParseGitHubReleases(json, includePrereleases: false)
            .Take(10)
            .ToList();
    }

    private static IReadOnlyList<ReleaseInfo> ParseCompatibilityBuildReleases(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<ReleaseInfo>();

        var matches = Regex.Matches(content, @"\b\d+\.\d+\.\d+-\d+\b");
        if (matches.Count == 0)
            return Array.Empty<ReleaseInfo>();

        var versions = matches
            .Select(static match => match.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static value =>
            {
                var build = ExtractBuildNumber(value);
                return int.TryParse(build, out var parsedBuild) ? parsedBuild : int.MinValue;
            })
            .ThenByDescending(static value => value, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return versions
            .Select(static version => new ReleaseInfo(version, true, null, Array.Empty<ReleaseAsset>()))
            .ToList();
    }

    private async Task<ReleaseInfo?> TryGetLatestWindowsNightlyFromDownloadPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync("https://rpcs3.net/download", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var assets = ParseWindowsNightlyAssetsFromDownloadHtml(html);
            var latest = assets
                .OrderByDescending(static asset =>
                {
                    var build = ExtractBuildNumber(asset.Version);
                    return int.TryParse(build, out var parsedBuild) ? parsedBuild : int.MinValue;
                })
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(latest.Version) || latest.Asset == null)
                return null;

            return new ReleaseInfo(latest.Version, true, DateTimeOffset.UtcNow, new[] { latest.Asset });
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve latest RPCS3 Windows x64 nightly from download page.", ex);
            return null;
        }
    }

    private static IReadOnlyList<(string Version, ReleaseAsset Asset)> ParseWindowsNightlyAssetsFromDownloadHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<(string Version, ReleaseAsset Asset)>();

        var matches = Regex.Matches(
            html,
            @"https?:\\?/\\?/github\.com/RPCS3/rpcs3-binaries-win/releases/download/[^\""""'\s>]+/[^\""""'\s>]+\.(?:7z|zip|exe)",
            RegexOptions.IgnoreCase);

        var results = new List<(string Version, ReleaseAsset Asset)>();
        foreach (Match match in matches)
        {
            if (!match.Success)
                continue;

            var rawUrl = WebUtility.HtmlDecode(match.Value)?.Replace("\\/", "/");
            if (string.IsNullOrWhiteSpace(rawUrl) ||
                !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var fileName = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                !fileName.Contains("win64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = ExtractVersionFromBinaryAssetName(fileName);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            results.Add((version, new ReleaseAsset(fileName, rawUrl)));
        }

        return results
            .GroupBy(static item => item.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private async Task<ReleaseAsset?> TryResolveNightlyAssetFromDownloadPageAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync("https://rpcs3.net/download", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var assets = ParseWindowsNightlyAssetsFromDownloadHtml(html);
            if (assets.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(requestedVersion))
            {
                var versionMatch = assets.FirstOrDefault(item => string.Equals(item.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));
                if (versionMatch.Asset != null)
                    return versionMatch.Asset;
            }

            return assets
                .OrderByDescending(static item =>
                {
                    var build = ExtractBuildNumber(item.Version);
                    return int.TryParse(build, out var parsedBuild) ? parsedBuild : int.MinValue;
                })
                .Select(static item => item.Asset)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve RPCS3 nightly asset from download page.", ex);
            return null;
        }
    }

    private static IReadOnlyList<CompatibilityBuildEntry> ParseCompatibilityBuildEntriesFromApi(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<CompatibilityBuildEntry>();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return Array.Empty<CompatibilityBuildEntry>();
        }

        var results = root?["results"] as JsonObject;
        if (results == null)
            return Array.Empty<CompatibilityBuildEntry>();

        var entries = new List<CompatibilityBuildEntry>();
        foreach (var pair in results)
        {
            if (pair.Value is not JsonObject item)
                continue;

            var commit = item["commit"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(commit))
                continue;

            DateTimeOffset? date = null;
            var dateRaw = item["date"]?.GetValue<string>()?.Trim();
            if (DateTimeOffset.TryParse(dateRaw, out var parsedDate))
                date = parsedDate;

            entries.Add(new CompatibilityBuildEntry(commit, date));
        }

        return entries
            .GroupBy(static e => e.Commit, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .OrderByDescending(static e => e.Date ?? DateTimeOffset.MinValue)
            .Take(80)
            .ToList();
    }

    private async Task<IReadOnlyList<ReleaseInfo>> ResolveCompatibilityBuildReleasesFromApiAsync(string json, CancellationToken cancellationToken)
    {
        var entries = ParseCompatibilityBuildEntriesFromApi(json);
        if (entries.Count == 0)
            return Array.Empty<ReleaseInfo>();

        var releases = new List<ReleaseInfo>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (releases.Count >= 10)
                break;

            var version = await ResolveVersionFromCommitAsync(entry.Commit, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(version) ||
                releases.Any(r => string.Equals(r.Tag, version, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            releases.Add(new ReleaseInfo(version, true, entry.Date, Array.Empty<ReleaseAsset>()));
        }

        return releases;
    }

    private async Task<string?> ResolveVersionFromCommitAsync(string commit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commit))
            return null;

        try
        {
            var endpoint = string.Format(UpdateApiEndpointTemplate, Uri.EscapeDataString(commit));
            using var response = await Client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var root = JsonNode.Parse(json) as JsonObject;
            var current = root?["current_build"]?["version"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(current))
                return current;

            var latest = root?["latest_build"]?["version"]?.GetValue<string>()?.Trim();
            return string.IsNullOrWhiteSpace(latest) ? null : latest;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ReleaseInfo>> TryGetNightlyReleasesFromApiFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(CompatibilityBuildsApiFallbackEndpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return await ResolveCompatibilityBuildReleasesFromApiAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to fetch RPCS3 nightly builds via compatibility API fallback.", ex);
            return Array.Empty<ReleaseInfo>();
        }
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetNightlyReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RPCS3Updater/1.0");

        var latestFromDownloadPage = await TryGetLatestWindowsNightlyFromDownloadPageAsync(cancellationToken).ConfigureAwait(false);
        if (latestFromDownloadPage != null)
            return new[] { latestFromDownloadPage };

        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, NightlyCacheFileName);
        var cache = LoadCache(cachePath);

        if (!forceRefresh &&
            cache?.Repository != null &&
            string.Equals(cache.Repository, NightlyCacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            var cachedReleases = ParseCompatibilityBuildReleases(cache.ReleasesJson!);
            var cachedBinaryFallback = await TryGetNightlyReleasesFromBinaryFeedAsync(cancellationToken).ConfigureAwait(false);
            var preferredCached = PickNewestNightlyList(cachedReleases, cachedBinaryFallback);
            var latestFromUpdate = await TryGetLatestNightlyFromUpdateEndpointAsync(cancellationToken).ConfigureAwait(false);
            preferredCached = MergeNightlyReleases(preferredCached, latestFromUpdate);
            if (preferredCached.Count > 0)
                return preferredCached;

            var cachedApiFallback = await ResolveCompatibilityBuildReleasesFromApiAsync(cache.ReleasesJson!, cancellationToken).ConfigureAwait(false);
            var preferredApiCached = PickNewestNightlyList(cachedApiFallback, cachedBinaryFallback);
            preferredApiCached = MergeNightlyReleases(preferredApiCached, latestFromUpdate);
            if (preferredApiCached.Count > 0)
                return preferredApiCached;
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        using var request = new HttpRequestMessage(HttpMethod.Get, CompatibilityBuildsEndpoint);
        if (!string.IsNullOrWhiteSpace(cache?.ETag) &&
            string.Equals(cache?.Repository, NightlyCacheKey, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        string? json;
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            json = cache!.ReleasesJson;
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            Log.Warn("Rate limit reached for RPCS3 nightly metadata; using cached entries.");
            json = cache!.ReleasesJson;
        }
        else
        {
            if (!response.IsSuccessStatusCode)
            {
                var fallbackReleases = await TryGetNightlyReleasesFromApiFallbackAsync(cancellationToken).ConfigureAwait(false);
                if (fallbackReleases.Count > 0)
                    return fallbackReleases;

                response.EnsureSuccessStatusCode();
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new ReleaseCache
            {
                Repository = NightlyCacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

        var releases = ParseCompatibilityBuildReleases(json);
        var binaryFallbackReleases = await TryGetNightlyReleasesFromBinaryFeedAsync(cancellationToken).ConfigureAwait(false);
        var preferredReleases = PickNewestNightlyList(releases, binaryFallbackReleases);
        var latestNightly = await TryGetLatestNightlyFromUpdateEndpointAsync(cancellationToken).ConfigureAwait(false);
        preferredReleases = MergeNightlyReleases(preferredReleases, latestNightly);
        if (preferredReleases.Count > 0)
            return preferredReleases;

        var apiFallbackReleases = await TryGetNightlyReleasesFromApiFallbackAsync(cancellationToken).ConfigureAwait(false);
        var preferredApiReleases = PickNewestNightlyList(apiFallbackReleases, binaryFallbackReleases);
        preferredApiReleases = MergeNightlyReleases(preferredApiReleases, latestNightly);
        if (preferredApiReleases.Count > 0)
            return preferredApiReleases;

        return Array.Empty<ReleaseInfo>();
    }

    private static IReadOnlyList<ReleaseInfo> ParseGitHubReleases(string json, bool includePrereleases)
    {
        var root = JsonNode.Parse(json) as JsonArray;
        if (root == null)
            return Array.Empty<ReleaseInfo>();

        var results = new List<ReleaseInfo>();
        foreach (var node in root)
        {
            if (node is not JsonObject item)
                continue;

            var tag = item["tag_name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var draft = item["draft"]?.GetValue<bool>() == true;
            if (draft)
                continue;

            var prerelease = item["prerelease"]?.GetValue<bool>() == true;
            if (prerelease && !includePrereleases)
                continue;

            var published = item["published_at"]?.GetValue<string>();
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(published, out var parsedPublished))
                publishedAt = parsedPublished;

            var assets = new List<ReleaseAsset>();
            if (item["assets"] is JsonArray assetsNode)
            {
                foreach (var assetNode in assetsNode)
                {
                    if (assetNode is not JsonObject assetObj)
                        continue;

                    var name = assetObj["name"]?.GetValue<string>()?.Trim();
                    var url = assetObj["browser_download_url"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new ReleaseAsset(name, url));
                }
            }

            results.Add(new ReleaseInfo(tag, prerelease, publishedAt, assets));
        }

        return results
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ReleaseInfo> ParseBinaryReleases(string json)
    {
        var root = JsonNode.Parse(json) as JsonArray;
        if (root == null)
            return Array.Empty<ReleaseInfo>();

        var results = new List<ReleaseInfo>();
        foreach (var node in root)
        {
            if (node is not JsonObject item)
                continue;

            var published = item["published_at"]?.GetValue<string>();
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(published, out var parsedPublished))
                publishedAt = parsedPublished;

            var assets = new List<ReleaseAsset>();
            if (item["assets"] is not JsonArray assetsNode)
                continue;

            foreach (var assetNode in assetsNode)
            {
                if (assetNode is not JsonObject assetObj)
                    continue;

                var name = assetObj["name"]?.GetValue<string>()?.Trim();
                var url = assetObj["browser_download_url"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                if (!name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assets.Add(new ReleaseAsset(name, url));
            }

            if (assets.Count == 0)
                continue;

            var version = ExtractVersionFromBinaryAssetName(assets[0].Name);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            results.Add(new ReleaseInfo(version, true, publishedAt, assets));
        }

        return results
            .GroupBy(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private async Task<ReleaseAsset?> TryResolveAssetFromBinaryReleasesAsync(string stableVersionTag, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stableVersionTag))
            return null;

        var normalizedStable = NormalizeVersion(stableVersionTag) ?? stableVersionTag;
        if (normalizedStable.StartsWith("nightly-", StringComparison.OrdinalIgnoreCase))
            return null;

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RPCS3Updater/1.0");

        var endpoints = new[] { BinaryReleasesApiEndpoint };
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await Client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var binaryReleases = ParseBinaryReleases(json);

                var match = binaryReleases
                    .Where(release => release.Tag.StartsWith(normalizedStable + "-", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(release.Tag, normalizedStable, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();

                if (match == null)
                    continue;

                var selected = SelectAssetForPlatform(match.Assets, includePrereleases: true);
                if (selected != null)
                    return selected;
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to resolve RPCS3 binary asset from '{endpoint}' for version '{stableVersionTag}'.", ex);
            }
        }

        return null;
    }

    private static string? ExtractBuildNumber(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var trimmed = version.Trim();
        var dashIndex = trimmed.LastIndexOf('-');
        if (dashIndex < 0 || dashIndex >= trimmed.Length - 1)
            return null;

        var suffix = trimmed[(dashIndex + 1)..];
        return suffix.All(char.IsDigit) ? suffix : null;
    }

    private static string? ExtractVersionFromBinaryAssetName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return null;

        var fileName = Path.GetFileNameWithoutExtension(assetName.Trim());
        const string prefix = "rpcs3-v";
        var prefixIndex = fileName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
            return null;

        var start = prefixIndex + prefix.Length;
        var tail = fileName[start..];
        var firstSeparator = tail.IndexOf('_');
        if (firstSeparator > 0)
            tail = tail[..firstSeparator];

        var parts = tail.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        if (!parts[0].Contains('.'))
            return null;

        if (!parts[1].All(char.IsDigit))
            return null;

        return $"{parts[0]}-{parts[1]}";
    }

    private static ReleaseInfo? ResolveTargetRelease(IReadOnlyList<ReleaseInfo> releases, string? requestedVersion)
    {
        if (releases.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(requestedVersion))
            return releases[0];

        return releases.FirstOrDefault(release =>
            string.Equals(release.Tag, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeVersion(release.Tag), NormalizeVersion(requestedVersion), StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseAsset? SelectAssetForPlatform(IReadOnlyList<ReleaseAsset> assets)
    {
        if (assets.Count == 0)
            return null;

        if (OperatingSystem.IsWindows())
        {
            static bool IsWindowsArchive(ReleaseAsset asset)
                => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            static bool IsNonWindowsBuild(ReleaseAsset asset)
                => asset.Name.Contains("android", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
                   asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase);

            return assets.FirstOrDefault(asset =>
                        asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                        !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                        !asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
                        !asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                        IsWindowsArchive(asset))
                   ?? assets.FirstOrDefault(asset =>
                        !IsNonWindowsBuild(asset) &&
                        !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                        !asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
                        !asset.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase) &&
                        IsWindowsArchive(asset));
        }

        if (OperatingSystem.IsLinux())
        {
            return assets.FirstOrDefault(asset =>
                        asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                        asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                        asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                        (asset.Name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) || asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            return assets.FirstOrDefault(asset =>
                asset.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) &&
                (asset.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) ||
                 asset.Name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
                 asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
        }

        return assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseAsset? SelectAssetForPlatform(IReadOnlyList<ReleaseAsset> assets, bool includePrereleases)
    {
        var selected = SelectAssetForPlatform(assets);
        if (selected != null || !includePrereleases)
            return selected;

        if (OperatingSystem.IsWindows())
        {
            return assets.FirstOrDefault(asset =>
                !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                !asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                !asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                !asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                !asset.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault();
    }

    private static bool IsArchive(string filePath)
        => filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
           filePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
           filePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
           filePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
           filePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static async Task DownloadAssetAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static void ExtractArchive(string archivePath, string extractDirectory)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
        {
            TryExtract7zWithSystemTool(archivePath, extractDirectory);
            return;
        }

        if (archivePath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            TryExtractTarWithSystemTool(archivePath, extractDirectory);
            return;
        }

        throw new InvalidOperationException($"Unsupported archive format: {Path.GetExtension(archivePath)}");
    }

    private static void TryExtract7zWithSystemTool(string archivePath, string extractDirectory)
    {
        Directory.CreateDirectory(extractDirectory);

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "tar.exe", "7z.exe" }
            : new[] { "7z", "7zz", "tar" };

        foreach (var tool in candidates)
        {
            var args = tool.StartsWith("tar", StringComparison.OrdinalIgnoreCase)
                ? $"-xf \"{archivePath}\" -C \"{extractDirectory}\""
                : $"x -y \"{archivePath}\" -o\"{extractDirectory}\"";

            if (TryRunExtractionTool(tool, args))
                return;
        }

        throw new InvalidOperationException("Unable to extract .7z archive. Install 7-Zip (7z.exe) or ensure tar supports 7z extraction.");
    }

    private static void TryExtractTarWithSystemTool(string archivePath, string extractDirectory)
    {
        Directory.CreateDirectory(extractDirectory);

        var candidates = OperatingSystem.IsWindows()
            ? new[] { "tar.exe", "7z.exe" }
            : new[] { "tar", "7z", "7zz" };

        foreach (var tool in candidates)
        {
            var args = tool.StartsWith("tar", StringComparison.OrdinalIgnoreCase)
                ? $"-xf \"{archivePath}\" -C \"{extractDirectory}\""
                : $"x -y \"{archivePath}\" -o\"{extractDirectory}\"";

            if (TryRunExtractionTool(tool, args))
                return;
        }

        throw new InvalidOperationException("Unable to extract tar archive. Ensure tar is available on PATH.");
    }

    private static bool TryRunExtractionTool(string tool, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void PrepareUpdateDirectory(string updateDirectory)
    {
        if (Directory.Exists(updateDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(updateDirectory, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }

            foreach (var directory in Directory.EnumerateDirectories(updateDirectory, "*", SearchOption.AllDirectories).OrderByDescending(static path => path.Length))
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        Directory.CreateDirectory(updateDirectory);
    }

    private static string NormalizeExtractionRoot(string extractDirectory)
    {
        var entries = Directory.EnumerateDirectories(extractDirectory).ToList();
        if (entries.Count == 1 && !Directory.EnumerateFiles(extractDirectory).Any())
            return entries[0];

        return extractDirectory;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            if (relative.StartsWith("Emu_Update", StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            if (relative.StartsWith("Emu_Update", StringComparison.OrdinalIgnoreCase))
                continue;

            var destinationPath = Path.Combine(destinationDirectory, relative);
            var destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static string? ResolveLauncherPath(string? launcherPath, string emulatorDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var prioritized = new[] { "rpcs3.exe", "RPCS3.exe" };
            foreach (var executableName in prioritized)
            {
                var candidate = Directory.EnumerateFiles(emulatorDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var appImageCandidate = Directory.EnumerateFiles(emulatorDirectory, "*.AppImage", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("rpcs3", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appImageCandidate))
                return appImageCandidate;
        }

        if (OperatingSystem.IsMacOS())
        {
            var appBundleCandidate = Directory.EnumerateDirectories(emulatorDirectory, "*.app", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("rpcs3", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appBundleCandidate))
                return appBundleCandidate;
        }

        var candidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path).Contains("rpcs3", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var localCandidate = candidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localCandidate))
            return localCandidate;

        if (!string.IsNullOrWhiteSpace(launcherPath) && (File.Exists(launcherPath) || Directory.Exists(launcherPath)))
            return launcherPath;

        return null;
    }

    private static string? GetInstalledVersion(string emulatorDirectory, string? launcherPath)
    {
        var markerPath = Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName);
        var markerVersion = ReadInstalledVersionMarker(markerPath);
        if (!string.IsNullOrWhiteSpace(markerVersion))
            return markerVersion;

        var fileVersion = GetFileVersionSafe(launcherPath);
        if (!string.IsNullOrWhiteSpace(fileVersion))
            return fileVersion;

        return null;
    }

    private static string? ReadInstalledVersionMarker(string markerPath)
    {
        if (!File.Exists(markerPath))
            return null;

        try
        {
            var markerVersion = File.ReadAllText(markerPath).Trim();
            return string.IsNullOrWhiteSpace(markerVersion) ? null : markerVersion;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveInstalledVersionMarker(string emulatorDirectory, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        try
        {
            Directory.CreateDirectory(emulatorDirectory);
            File.WriteAllText(Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName), version.Trim());
        }
        catch
        {
        }
    }

    private static string? GetFileVersionSafe(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            return null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(launcherPath).FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return fileVersion;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool VersionsEquivalent(string? currentVersion, string? releaseVersion)
        => string.Equals(NormalizeVersion(currentVersion), NormalizeVersion(releaseVersion), StringComparison.OrdinalIgnoreCase);

    private static bool IsUpdateAvailable(string? currentVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(latestVersion))
            return false;

        var compareResult = CompareVersionNumbers(currentVersion, latestVersion);
        if (compareResult.HasValue)
            return compareResult.Value < 0;

        return !VersionsEquivalent(currentVersion, latestVersion);
    }

    private static int? CompareVersionNumbers(string left, string right)
    {
        var leftParts = ExtractVersionNumberParts(left);
        var rightParts = ExtractVersionNumberParts(right);

        if (leftParts.Count == 0 || rightParts.Count == 0)
            return null;

        TrimTrailingZeros(leftParts);
        TrimTrailingZeros(rightParts);

        var max = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < max; i++)
        {
            var leftValue = i < leftParts.Count ? leftParts[i] : 0;
            var rightValue = i < rightParts.Count ? rightParts[i] : 0;
            var compare = leftValue.CompareTo(rightValue);
            if (compare != 0)
                return compare;
        }

        return 0;
    }

    private static List<int> ExtractVersionNumberParts(string value)
    {
        var normalized = NormalizeVersion(value) ?? string.Empty;
        var parts = new List<int>();
        var current = 0;
        var inNumber = false;

        foreach (var ch in normalized)
        {
            if (char.IsDigit(ch))
            {
                inNumber = true;
                current = (current * 10) + (ch - '0');
                continue;
            }

            if (!inNumber)
                continue;

            parts.Add(current);
            current = 0;
            inNumber = false;
        }

        if (inNumber)
            parts.Add(current);

        return parts;
    }

    private static void TrimTrailingZeros(List<int> values)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            if (values[i] != 0)
                break;

            values.RemoveAt(i);
        }
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimStart('v', 'V');
    }

    private static ReleaseCache? LoadCache(string cachePath)
    {
        if (!File.Exists(cachePath))
            return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject;
            if (node == null)
                return null;

            var repository = node["repository"]?.GetValue<string>();
            var etag = node["etag"]?.GetValue<string>();
            var releasesJson = node["releasesJson"]?.GetValue<string>();
            var fetchedRaw = node["fetchedAtUtc"]?.GetValue<string>();
            var fetchedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(fetchedRaw) && DateTimeOffset.TryParse(fetchedRaw, out var parsedFetched))
                fetchedAt = parsedFetched;

            return new ReleaseCache
            {
                Repository = repository,
                ETag = etag,
                ReleasesJson = releasesJson,
                FetchedAtUtc = fetchedAt
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string cachePath, ReleaseCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ApplicationPaths.CacheDirectory);
            var payload = new JsonObject
            {
                ["repository"] = cache.Repository,
                ["etag"] = cache.ETag,
                ["releasesJson"] = cache.ReleasesJson,
                ["fetchedAtUtc"] = cache.FetchedAtUtc.ToString("O")
            };
            File.WriteAllText(cachePath, payload.ToJsonString());
        }
        catch
        {
        }
    }

    private static string NormalizeSectionFolderName(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return "Unknown";

        var trimmed = sectionName.Trim();
        var extension = Path.GetExtension(trimmed);
        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = Path.GetFileNameWithoutExtension(trimmed).Trim();
        }

        return trimmed.ToUpperInvariant() switch
        {
            "PLAYSTATION 3" => "PS3",
            "PS3" => "PS3",
            _ => trimmed
        };
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedChars = value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        var sanitized = new string(sanitizedChars)
            .Replace(" ", "_")
            .Trim('_');

        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }
}
