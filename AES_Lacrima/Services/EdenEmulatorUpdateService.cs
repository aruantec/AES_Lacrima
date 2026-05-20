using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;
using AES_Core.IO;
using log4net;

namespace AES_Lacrima.Services;

public sealed record EdenUpdateState(
    string Repository,
    string? CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    IReadOnlyList<string> AvailableVersions,
    string StatusMessage,
    string EmulatorDirectory,
    string UpdateDirectory,
    string? ResolvedLauncherPath,
    string? LatestReleaseNotes = null);

[AutoRegister]
public partial class EdenEmulatorUpdateService
{
    private const string DefaultRepo = "https://git.eden-emu.dev/eden-emu/eden";
    private const string CacheFileName = "eden-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "eden_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<EdenEmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class EdenReleaseCache
    {
        public string? Repository { get; set; }
        public string? ETag { get; set; }
        public string? ReleasesJson { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
    }

    private sealed record RepoResolution(
        string DisplayValue,
        string CacheKey,
        string ReleasesApiEndpoint,
        bool IsGitHub);

    private sealed record EdenRelease(
        string Tag,
        string Name,
        bool IsPrerelease,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<EdenAsset> Assets,
        string? ReleaseNotes = null);

    private sealed record EdenAsset(string Name, string DownloadUrl);

    public async Task<EdenUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? repositoryOverride,
        bool includePrereleases,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var repository = ResolveRepository(repositoryOverride);
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);

        try
        {
            var releases = await GetReleasesAsync(repository, includePrereleases, forceRefresh, cancellationToken).ConfigureAwait(false);
            var latestRelease = releases.FirstOrDefault();
            var versions = releases.Select(static r => r.Tag).Where(static v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            var latest = latestRelease?.Tag ?? versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New Eden version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "Eden is not installed in this section yet."
                    : $"Eden is up to date ({currentVersion}).";

            return new EdenUpdateState(
                repository.DisplayValue,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                status,
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath,
                updateAvailable ? latestRelease?.ReleaseNotes : null);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to fetch Eden update info; returning local status only.", ex);
            return new EdenUpdateState(
                repository.DisplayValue,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check Eden updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<EdenUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? repositoryOverride,
        bool includePrereleases,
        string? requestedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repository = ResolveRepository(repositoryOverride);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var releases = await GetReleasesAsync(repository, includePrereleases, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new EdenUpdateState(repository.DisplayValue, GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath), null, false, Array.Empty<string>(), "No Eden releases found.", emulatorDirectory, updateDirectory, noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new EdenUpdateState(repository.DisplayValue, GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(10).ToList(), $"Version '{requestedVersion}' was not found.", emulatorDirectory, updateDirectory, unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new EdenUpdateState(repository.DisplayValue, GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(10).ToList(), "No compatible Eden asset found for this OS.", emulatorDirectory, updateDirectory, missingAssetLauncherPath);
            }

            PrepareUpdateDirectory(updateDirectory);
            var downloadedAssetPath = Path.Combine(updateDirectory, selectedAsset.Name);
            await DownloadAssetAsync(selectedAsset.DownloadUrl, downloadedAssetPath, cancellationToken).ConfigureAwait(false);

            if (downloadedAssetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDirectory = Path.Combine(updateDirectory, "extracted");
                Directory.CreateDirectory(extractDirectory);
                ZipFile.ExtractToDirectory(downloadedAssetPath, extractDirectory, overwriteFiles: true);
                var sourceDirectory = NormalizeExtractionRoot(extractDirectory);
                CopyDirectoryContents(sourceDirectory, emulatorDirectory);
            }
            else
            {
                var destinationPath = Path.Combine(emulatorDirectory, Path.GetFileName(downloadedAssetPath));
                File.Copy(downloadedAssetPath, destinationPath, overwrite: true);
            }

            // Keep Emu_Update as a pure temp staging area.
            PrepareUpdateDirectory(updateDirectory);
            SaveInstalledVersionMarker(emulatorDirectory, targetRelease.Tag);

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
            var versions = releases.Select(static r => r.Tag).Where(static v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);

            return new EdenUpdateState(
                repository.DisplayValue,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"Eden {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("Eden update failed.", ex);
            var repository = ResolveRepository(repositoryOverride);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch
            {
            }
            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new EdenUpdateState(
                repository.DisplayValue,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"Eden download/update failed: {ex.Message}",
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
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, safeSectionKey, "Eden");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<EdenRelease>> GetReleasesAsync(RepoResolution repository, bool includePrereleases, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new EdenReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, repository.CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseReleases(cache.ReleasesJson!, includePrereleases);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-EdenUpdater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, repository.ReleasesApiEndpoint);

        if (!string.IsNullOrWhiteSpace(cache.ETag) &&
            string.Equals(cache.Repository, repository.CacheKey, StringComparison.OrdinalIgnoreCase))
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
            Log.Warn("GitHub rate limit reached for Eden updates; using cached releases.");
            json = cache!.ReleasesJson;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new EdenReleaseCache
            {
                Repository = repository.CacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<EdenRelease>();

        return ParseReleases(json, includePrereleases);
    }

    private static IReadOnlyList<EdenRelease> ParseReleases(string json, bool includePrereleases)
    {
        var root = JsonNode.Parse(json) as JsonArray;
        if (root == null)
            return Array.Empty<EdenRelease>();

        var isGitHubFormat = root.Any(static node => node is JsonObject item && item.ContainsKey("tag_name"));
        if (!isGitHubFormat)
        {
            return ParseForgejoReleases(root, includePrereleases);
        }

        var results = new List<EdenRelease>();
        foreach (var node in root)
        {
            if (node is not JsonObject item)
                continue;

            var tag = item["tag_name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var prerelease = item["prerelease"]?.GetValue<bool>() == true;
            var published = item["published_at"]?.GetValue<string>();
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(published, out var parsedPublished))
                publishedAt = parsedPublished;

            var assets = new List<EdenAsset>();
            if (item["assets"] is JsonArray assetsNode)
            {
                foreach (var assetNode in assetsNode)
                {
                    if (assetNode is not JsonObject assetObj)
                        continue;

                    var name = assetObj["name"]?.GetValue<string>()?.Trim();
                    var url = assetObj["browser_download_url"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new EdenAsset(name, url));
                }
            }

            results.Add(new EdenRelease(tag, item["name"]?.GetValue<string>() ?? tag, prerelease, publishedAt, assets, EmulatorReleaseNotesHelper.ParseGitHubReleaseBody(item)));
        }

        return results
            .Where(r => includePrereleases ? r.IsPrerelease : !r.IsPrerelease)
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<EdenRelease> ParseForgejoReleases(JsonArray root, bool includePrereleases)
    {
        var results = new List<EdenRelease>();
        foreach (var node in root)
        {
            if (node is not JsonObject item)
                continue;

            var tag = item["tag_name"]?.GetValue<string>()?.Trim()
                      ?? item["tag"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var prerelease = item["prerelease"]?.GetValue<bool>() == true;
            var published = item["published_at"]?.GetValue<string>() ?? item["created_at"]?.GetValue<string>();
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(published, out var parsedPublished))
                publishedAt = parsedPublished;

            var assets = new List<EdenAsset>();
            if (item["assets"] is JsonArray assetsNode)
            {
                foreach (var assetNode in assetsNode)
                {
                    if (assetNode is not JsonObject assetObj)
                        continue;

                    var name = assetObj["name"]?.GetValue<string>()?.Trim();
                    var url = assetObj["browser_download_url"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(url) && assetObj["url"]?.GetValue<string>() is { } apiUrl)
                        url = apiUrl;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                        assets.Add(new EdenAsset(name, url));
                }
            }

            results.Add(new EdenRelease(tag, item["name"]?.GetValue<string>() ?? tag, prerelease, publishedAt, assets, EmulatorReleaseNotesHelper.ParseGitHubReleaseBody(item)));
        }

        return results
            .Where(r => includePrereleases ? r.IsPrerelease : !r.IsPrerelease)
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static EdenRelease? ResolveTargetRelease(IReadOnlyList<EdenRelease> releases, string? requestedVersion)
    {
        if (releases.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(requestedVersion))
            return releases[0];

        return releases.FirstOrDefault(release =>
            string.Equals(release.Tag, requestedVersion, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeVersion(release.Tag), NormalizeVersion(requestedVersion), StringComparison.OrdinalIgnoreCase));
    }

    private static EdenAsset? SelectAssetForPlatform(IReadOnlyList<EdenAsset> assets)
    {
        if (assets.Count == 0)
            return null;

        if (OperatingSystem.IsWindows())
        {
            return assets.FirstOrDefault(asset =>
                       asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                       asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            return assets.FirstOrDefault(asset =>
                       asset.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
                       asset.Name.Contains("osx", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault(asset =>
                   asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                   (asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ||
                    asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
               ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DownloadAssetAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
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

    private static RepoResolution ResolveRepository(string? overrideValue)
    {
        var value = string.IsNullOrWhiteSpace(overrideValue)
            ? DefaultRepo
            : overrideValue.Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var ownerRepo = value.Trim('/');
            return new RepoResolution(
                ownerRepo,
                $"github:{ownerRepo}",
                $"https://api.github.com/repos/{ownerRepo}/releases?per_page=20",
                true);
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return new RepoResolution(
                DefaultRepo,
                "forgejo:eden-emu/eden",
                "https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases?page=1&limit=20",
                false);
        }

        var ownerRepoPath = $"{segments[0]}/{segments[1]}";
        if (uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new RepoResolution(
                ownerRepoPath,
                $"github:{ownerRepoPath}",
                $"https://api.github.com/repos/{ownerRepoPath}/releases?per_page=20",
                true);
        }

        return new RepoResolution(
            $"{uri.Scheme}://{uri.Host}/{ownerRepoPath}",
            $"{uri.Host}:{ownerRepoPath}",
            $"{uri.Scheme}://{uri.Host}/api/v1/repos/{ownerRepoPath}/releases?page=1&limit=20",
            false);
    }

    private static string NormalizeSectionFolderName(string input)
    {
        var value = input.Trim();
        var extension = Path.GetExtension(value);
        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.GetFileNameWithoutExtension(value);
        }

        return value;
    }

    private static string? ResolveLauncherPath(string? launcherPath, string emulatorDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var cli = Directory.EnumerateFiles(emulatorDirectory, "eden-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cli))
                return cli;

            var gui = Directory.EnumerateFiles(emulatorDirectory, "eden.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(gui))
                return gui;
        }

        var executableCandidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path).Contains("eden", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var localCandidate = executableCandidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localCandidate))
            return localCandidate;

        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
            return launcherPath;

        return null;
    }

    private static string? GetInstalledVersion(string emulatorDirectory, string? launcherPath)
    {
        var fileVersion = GetFileVersionSafe(launcherPath);
        if (!string.IsNullOrWhiteSpace(fileVersion))
            return fileVersion;

        var markerPath = Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName);
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
    {
        return string.Equals(NormalizeVersion(currentVersion), NormalizeVersion(releaseVersion), StringComparison.OrdinalIgnoreCase);
    }

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

    private static string SanitizePathPart(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private static EdenReleaseCache? LoadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize<EdenReleaseCache>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string cachePath, EdenReleaseCache cache)
    {
        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
        }
    }
}
