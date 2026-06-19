using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Serialization;
using AES_Lacrima.Services.Xenia;
using log4net;

using AES_Core.Logging;
namespace AES_Lacrima.Services;

public sealed record XeniaUpdateState(
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
public partial class XeniaEmulatorUpdateService
{
    private const string Repository = "https://github.com/xenia-canary/xenia-canary";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/xenia-canary/xenia-canary/releases?per_page=20";
    private const string OfficialAtomFeedUrl = "https://github.com/xenia-canary/xenia-canary/releases.atom";
    private const string LinuxAppImageRepository = "pkgforge-dev/xenia-canary-AppImage";
    private const string LinuxAppImageRepositoryUrl = "https://github.com/pkgforge-dev/xenia-canary-AppImage";
    private const string LinuxAppImageReleasesApiEndpoint = "https://api.github.com/repos/pkgforge-dev/xenia-canary-AppImage/releases?per_page=10";
    private const string LinuxAppImageAtomFeedUrl = "https://github.com/pkgforge-dev/xenia-canary-AppImage/releases.atom";
    private const string GamePatchesZipUrl = "https://github.com/xenia-canary/game-patches/releases/latest/download/game-patches.zip";
    private const string CacheKey = "github:xenia-canary/xenia-canary";
    private const string CacheFileName = "xenia-canary-releases-cache.json";
    private const string LinuxAppImageCacheKey = "github:pkgforge-dev/xenia-canary-AppImage";
    private const string LinuxAppImageCacheFileName = "xenia-canary-appimage-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "xenia_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<XeniaEmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record ReleaseInfo(
        string Tag,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets,
        string? ReleaseNotes = null);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<XeniaUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
        var writableWarning = EmulatorInstallDirectoryHelper.GetWritableDirectoryWarning(emulatorDirectory);
        var activeRepository = GetActiveRepositoryUrl();

        try
        {
            await EnsureGamePatchesAsync(emulatorDirectory, updateDirectory, resolvedLauncherPath, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            var releases = await GetActiveReleasesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
            var latestRelease = releases.FirstOrDefault();
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            var latest = latestRelease?.Tag ?? versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            if (string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(latest))
                updateAvailable = true;

            var status = !string.IsNullOrWhiteSpace(writableWarning)
                ? $"{writableWarning} Download is required before Xenia can be installed."
                : updateAvailable
                ? string.IsNullOrWhiteSpace(currentVersion)
                    ? OperatingSystem.IsLinux()
                        ? $"Xenia Canary Linux AppImage is not installed. Latest version: {latest}"
                        : $"Xenia Canary is not installed. Latest version: {latest}"
                    : $"New Xenia Canary version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? OperatingSystem.IsLinux()
                        ? "Xenia Canary Linux AppImage is not installed in this section yet."
                        : "Xenia Canary is not installed in this section yet."
                    : $"Xenia Canary is up to date ({currentVersion}).";

            return new XeniaUpdateState(
                activeRepository,
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
            Log.Warn("Failed to fetch Xenia Canary update info; returning local status only.", ex);
            return new XeniaUpdateState(
                activeRepository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check Xenia Canary updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<XeniaUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? requestedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Log.Info("Starting Xenia Canary download/update.");
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var (writable, writeError) = await EmulatorInstallDirectoryHelper
                .EnsureWritableAsync(emulatorDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (!writable)
            {
                Log.Warn($"Xenia download blocked because emulator directory is not writable: '{emulatorDirectory}'.");
                var blockedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new XeniaUpdateState(
                    GetActiveRepositoryUrl(),
                    GetInstalledVersion(emulatorDirectory, blockedLauncherPath),
                    null,
                    false,
                    Array.Empty<string>(),
                    writeError ?? "Xenia emulator directory is not writable.",
                    emulatorDirectory,
                    updateDirectory,
                    blockedLauncherPath);
            }

            var releases = await GetActiveReleasesAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new XeniaUpdateState(GetActiveRepositoryUrl(), GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath), null, false, Array.Empty<string>(), "No Xenia Canary releases found.", emulatorDirectory, updateDirectory, noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new XeniaUpdateState(GetActiveRepositoryUrl(), GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(10).ToList(), $"Version '{requestedVersion}' was not found.", emulatorDirectory, updateDirectory, unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new XeniaUpdateState(GetActiveRepositoryUrl(), GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(10).ToList(), "No compatible Xenia Canary asset found for this OS.", emulatorDirectory, updateDirectory, missingAssetLauncherPath);
            }

            Log.Info($"Downloading Xenia asset '{selectedAsset.Name}' for release '{targetRelease.Tag}'.");
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
                TryMarkLinuxExecutable(destinationPath);
            }

            PrepareUpdateDirectory(updateDirectory);
            SaveInstalledVersionMarker(emulatorDirectory, targetRelease.Tag);

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            await EnsureGamePatchesAsync(emulatorDirectory, updateDirectory, resolvedLauncherPath, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            PrepareUpdateDirectory(updateDirectory);
            var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);

            return new XeniaUpdateState(
                GetActiveRepositoryUrl(),
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"Xenia Canary {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("Xenia Canary update failed.", ex);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new XeniaUpdateState(
                GetActiveRepositoryUrl(),
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"Xenia Canary download/update failed: {ex.Message}",
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
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, safeSectionKey, "Xenia");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private static string GetActiveRepositoryUrl()
        => OperatingSystem.IsLinux() ? LinuxAppImageRepositoryUrl : Repository;

    private Task<IReadOnlyList<ReleaseInfo>> GetActiveReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
        => OperatingSystem.IsLinux()
            ? GetLinuxAppImageReleasesAsync(forceRefresh, cancellationToken)
            : GetReleasesAsync(forceRefresh, cancellationToken);

    private async Task<IReadOnlyList<ReleaseInfo>> GetLinuxAppImageReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, LinuxAppImageCacheFileName);
        var cache = LoadCache(cachePath) ?? new EmulatorReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, LinuxAppImageCacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseReleases(cache.ReleasesJson!);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);
        ConfigureGitHubClientHeaders("AES_Lacrima-XeniaLinuxUpdater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, LinuxAppImageReleasesApiEndpoint);
        if (!string.IsNullOrWhiteSpace(cache.ETag) &&
            string.Equals(cache.Repository, LinuxAppImageCacheKey, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        string? json;
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            json = cache!.ReleasesJson;
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Warn("Rate limit reached for Xenia Linux AppImage updates; using cached or atom feed releases.");
            json = !string.IsNullOrWhiteSpace(cache?.ReleasesJson) ? cache!.ReleasesJson : null;
            if (string.IsNullOrWhiteSpace(json))
            {
                var atomReleases = await BuildLinuxAppImageReleasesFromAtomAsync(cancellationToken).ConfigureAwait(false);
                return atomReleases;
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new EmulatorReleaseCache
            {
                Repository = LinuxAppImageCacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            var atomReleases = await BuildLinuxAppImageReleasesFromAtomAsync(cancellationToken).ConfigureAwait(false);
            return atomReleases;
        }

        var parsed = ParseReleases(json);
        if (parsed.Count == 0)
        {
            var atomReleases = await BuildLinuxAppImageReleasesFromAtomAsync(cancellationToken).ConfigureAwait(false);
            return atomReleases;
        }

        return parsed;
    }

    private async Task<IReadOnlyList<ReleaseInfo>> BuildLinuxAppImageReleasesFromAtomAsync(CancellationToken cancellationToken)
    {
        var atomEntries = await GitHubAtomReleaseFeedReader.FetchReleasesAsync(
            Client,
            LinuxAppImageAtomFeedUrl,
            maxEntries: 10,
            cancellationToken).ConfigureAwait(false);

        return BuildLinuxAppImageReleasesFromAtomEntries(atomEntries);
    }

    private static IReadOnlyList<ReleaseInfo> BuildLinuxAppImageReleasesFromAtomEntries(
        IReadOnlyList<GitHubAtomReleaseFeedReader.AtomReleaseEntry> atomEntries)
    {
        var results = new List<ReleaseInfo>();
        foreach (var entry in atomEntries)
        {
            var assetName = BuildPkgforgeLinuxAssetName(entry.Title);
            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(entry.Tag))
                continue;

            var downloadUrl =
                $"https://github.com/{LinuxAppImageRepository}/releases/download/{entry.Tag}/{assetName}";
            results.Add(new ReleaseInfo(
                entry.Tag,
                entry.PublishedAt,
                new[] { new ReleaseAsset(assetName, downloadUrl) }));
        }

        return results;
    }

    internal static string? BuildPkgforgeLinuxAssetName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var separatorIndex = title.IndexOf(':');
        if (separatorIndex < 0 || separatorIndex >= title.Length - 1)
            return null;

        var hash = title[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(hash))
            return null;

        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        return $"Xenia_Canary-{hash}-anylinux-{arch}.AppImage";
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new EmulatorReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseReleases(cache.ReleasesJson!);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        ConfigureGitHubClientHeaders("AES_Lacrima-XeniaUpdater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiEndpoint);
        if (!string.IsNullOrWhiteSpace(cache.ETag) &&
            string.Equals(cache.Repository, CacheKey, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));
        }

        string? json;
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            json = cache!.ReleasesJson;
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Log.Warn("Rate limit reached for Xenia Canary updates; using cached or atom feed releases.");
            json = !string.IsNullOrWhiteSpace(cache?.ReleasesJson) ? cache!.ReleasesJson : null;
            if (string.IsNullOrWhiteSpace(json))
            {
                var atomReleases = await BuildOfficialReleasesFromAtomAsync(cancellationToken).ConfigureAwait(false);
                return atomReleases;
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new EmulatorReleaseCache
            {
                Repository = CacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            var atomReleases = await BuildOfficialReleasesFromAtomAsync(cancellationToken).ConfigureAwait(false);
            return atomReleases;
        }

        return ParseReleases(json);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> BuildOfficialReleasesFromAtomAsync(CancellationToken cancellationToken)
    {
        var atomEntries = await GitHubAtomReleaseFeedReader.FetchReleasesAsync(
            Client,
            OfficialAtomFeedUrl,
            maxEntries: 10,
            cancellationToken).ConfigureAwait(false);

        var results = new List<ReleaseInfo>();
        foreach (var entry in atomEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Tag))
                continue;

            var assetName = "xenia_canary_windows.zip";
            var downloadUrl = $"https://github.com/xenia-canary/xenia-canary/releases/download/{entry.Tag}/{assetName}";
            results.Add(new ReleaseInfo(
                entry.Title,
                entry.PublishedAt,
                new[] { new ReleaseAsset(assetName, downloadUrl) },
                entry.Title));
        }

        return results;
    }

    private static void ConfigureGitHubClientHeaders(string userAgent)
    {
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        Client.DefaultRequestHeaders.Accept.Clear();
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
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

            var prerelease = item["prerelease"]?.GetValue<bool>() == true;
            if (prerelease)
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

            results.Add(new ReleaseInfo(tag, publishedAt, assets, EmulatorReleaseNotesHelper.ParseGitHubReleaseBody(item)));
        }

        return results
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            return EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
                       assets,
                       static asset => asset.Name,
                       static asset =>
                           asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.Contains("canary", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
                       assets,
                       static asset => asset.Name,
                       static asset =>
                           asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsLinux())
        {
            return EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       static asset =>
                           asset.Name.Contains("anylinux", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                   ?? EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       static asset =>
                           asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                   ?? EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       static asset =>
                           asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                           asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
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
                try { File.Delete(file); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            }

            foreach (var directory in Directory.EnumerateDirectories(updateDirectory, "*", SearchOption.AllDirectories).OrderByDescending(static path => path.Length))
            {
                try { Directory.Delete(directory, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
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

    private async Task EnsureGamePatchesAsync(string emulatorDirectory, string updateDirectory, string? resolvedLauncherPath, bool forceRefresh, CancellationToken cancellationToken)
    {
        var patchesDirectory = ResolvePatchesDirectory(emulatorDirectory, resolvedLauncherPath);
        if (!forceRefresh && Directory.Exists(patchesDirectory) && Directory.EnumerateFileSystemEntries(patchesDirectory).Any())
            return;

        try
        {
            Directory.CreateDirectory(updateDirectory);
            var zipPath = Path.Combine(updateDirectory, "game-patches.zip");
            var extractDirectory = Path.Combine(updateDirectory, "patches_extracted");

            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            }

            if (Directory.Exists(extractDirectory))
            {
                try { Directory.Delete(extractDirectory, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            }

            await DownloadAssetAsync(GamePatchesZipUrl, zipPath, cancellationToken).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);

            var sourcePatchesDirectory = ResolveExtractedPatchesDirectory(extractDirectory);
            if (string.IsNullOrWhiteSpace(sourcePatchesDirectory) || !Directory.Exists(sourcePatchesDirectory))
                return;

            ReplaceDirectoryContents(sourcePatchesDirectory, patchesDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to refresh Xenia Canary game patches.", ex);
        }
    }

    private static string ResolvePatchesDirectory(string emulatorDirectory, string? resolvedLauncherPath) =>
        XeniaPathsService.ResolvePatchesDirectory(emulatorDirectory, resolvedLauncherPath);

    private static string? ResolveExtractedPatchesDirectory(string extractDirectory)
    {
        var direct = Path.Combine(extractDirectory, "patches");
        if (Directory.Exists(direct))
            return direct;

        foreach (var directory in Directory.EnumerateDirectories(extractDirectory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(directory), "patches", StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    private static void ReplaceDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory))
        {
            try { Directory.Delete(destinationDirectory, true); } catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
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
            var prioritized = new[] { "xenia_canary.exe", "xenia.exe" };
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
                .FirstOrDefault(path => Path.GetFileName(path).Contains("xenia", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appImageCandidate))
                return appImageCandidate;
        }

        var candidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path =>
            {
                var fileName = Path.GetFileName(path);
                return fileName.Contains("xenia", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        var localCandidate = candidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localCandidate))
            return localCandidate;

        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static bool VersionsEquivalent(string? currentVersion, string? releaseVersion)
    {
        return string.Equals(NormalizeVersion(currentVersion), NormalizeVersion(releaseVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpdateAvailable(string? currentVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;

        if (string.IsNullOrWhiteSpace(currentVersion))
            return true;

        var compareResult = CompareVersionNumbers(currentVersion, latestVersion);
        if (compareResult.HasValue)
            return compareResult.Value < 0;

        return !VersionsEquivalent(currentVersion, latestVersion);
    }

    private static void TryMarkLinuxExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to mark Xenia Linux file as executable: '{path}'.", ex);
        }
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

    private static string SanitizePathPart(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private static EmulatorReleaseCache? LoadCache(string cachePath) =>
        EmulatorReleaseCachePersistence.Load(cachePath);

    private static void SaveCache(string cachePath, EmulatorReleaseCache cache) =>
        EmulatorReleaseCachePersistence.Save(cachePath, cache);
}
