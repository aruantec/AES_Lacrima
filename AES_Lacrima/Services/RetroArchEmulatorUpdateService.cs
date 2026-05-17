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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;
using AES_Core.IO;
using log4net;

namespace AES_Lacrima.Services;

public sealed record RetroArchUpdateState(
    string Repository,
    string? CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    IReadOnlyList<string> AvailableVersions,
    string StatusMessage,
    string EmulatorDirectory,
    string UpdateDirectory,
    string? ResolvedLauncherPath);

public readonly record struct RetroArchUpdateProgress(double Percent, string? StatusMessage = null);

[AutoRegister]
public partial class RetroArchEmulatorUpdateService
{
    private const string CacheFileName = "retroarch-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "retroarch_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<RetroArchEmulatorUpdateService>();
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

    private sealed record RepoResolution(
        string DisplayValue,
        string CacheKey,
        string ReleasesEndpoint,
        bool IsNightly);

    private sealed record ReleaseInfo(
        string Tag,
        bool IsPrerelease,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    private static readonly Regex NightlyAssetRegex = new(
        "<a\\s+href=\"(?<href>[^\"]+)\"[^>]*>(?<name>[^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NightlyDatedAssetNameRegex = new(
        "^(?<date>\\d{4}-\\d{2}-\\d{2})_(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<RetroArchUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? repositoryOverride,
        bool includeCores,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var repository = ResolveRepository(repositoryOverride);
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);

        try
        {
            var releases = await GetReleasesAsync(repository, forceRefresh, cancellationToken).ConfigureAwait(false);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New RetroArch version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "RetroArch is not installed in this section yet."
                    : $"RetroArch is up to date ({currentVersion}).";

            if (repository.IsNightly)
                status += " (Nightly feed)";

            return new RetroArchUpdateState(
                repository.DisplayValue,
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
            Log.Warn("Failed to fetch RetroArch update info; returning local status only.", ex);
            return new RetroArchUpdateState(
                repository.DisplayValue,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check RetroArch updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<RetroArchUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? repositoryOverride,
        bool includeCores,
        string? requestedVersion,
        Action<RetroArchUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ReportProgress(progress, 1, "Checking RetroArch builds...");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repository = ResolveRepository(repositoryOverride);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var releases = await GetReleasesAsync(repository, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new RetroArchUpdateState(
                    repository.DisplayValue,
                    GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath),
                    null,
                    false,
                    Array.Empty<string>(),
                    "No RetroArch builds found.",
                    emulatorDirectory,
                    updateDirectory,
                    noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new RetroArchUpdateState(
                    repository.DisplayValue,
                    GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(20).ToList(),
                    $"Version '{requestedVersion}' was not found.",
                    emulatorDirectory,
                    updateDirectory,
                    unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new RetroArchUpdateState(
                    repository.DisplayValue,
                    GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(20).ToList(),
                    "No compatible RetroArch build found for this OS.",
                    emulatorDirectory,
                    updateDirectory,
                    missingAssetLauncherPath);
            }

            var coreStartProgress = includeCores && OperatingSystem.IsWindows() ? 70d : 100d;

            PrepareUpdateDirectory(updateDirectory);
            var downloadedAssetPath = Path.Combine(updateDirectory, selectedAsset.Name);
            ReportProgress(progress, 4, $"Downloading RetroArch {targetRelease.Tag}...");
            await DownloadAssetAsync(
                selectedAsset.DownloadUrl,
                downloadedAssetPath,
                cancellationToken,
                p => ReportProgress(progress, MapProgress(4, 55, p), $"Downloading RetroArch {targetRelease.Tag}...")).ConfigureAwait(false);

            if (downloadedAssetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                downloadedAssetPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                var extractDirectory = Path.Combine(updateDirectory, "extracted");
                Directory.CreateDirectory(extractDirectory);
                ReportProgress(progress, MapProgress(55, coreStartProgress, 0.35), "Extracting RetroArch archive...");
                ExtractArchive(downloadedAssetPath, extractDirectory);
                var sourceDirectory = NormalizeExtractionRoot(extractDirectory);
                ReportProgress(progress, MapProgress(55, coreStartProgress, 0.65), "Installing RetroArch files...");
                CopyDirectoryContents(sourceDirectory, emulatorDirectory);
            }
            else
            {
                var destinationPath = Path.Combine(emulatorDirectory, Path.GetFileName(downloadedAssetPath));
                File.Copy(downloadedAssetPath, destinationPath, overwrite: true);
            }

            PrepareUpdateDirectory(updateDirectory);
            SaveInstalledVersionMarker(emulatorDirectory, targetRelease.Tag);
            ReportProgress(progress, coreStartProgress, includeCores && OperatingSystem.IsWindows() ? "RetroArch updated. Downloading cores..." : "RetroArch updated.");

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);

            if (includeCores && OperatingSystem.IsWindows())
            {
                var emuRoot = ResolveEmulatorRootDirectory(emulatorDirectory, resolvedLauncherPath);
                var coresStatus = await DownloadAndInstallWindowsNightlyCoresAsync(
                    emuRoot,
                    updateDirectory,
                    cancellationToken,
                    p => ReportProgress(progress, MapProgress(coreStartProgress, 100, p), "Downloading and installing RetroArch cores...")).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(coresStatus))
                    Log.Info(coresStatus);
            }

            ReportProgress(progress, 100, "RetroArch update completed.");

            return new RetroArchUpdateState(
                repository.DisplayValue,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                includeCores && OperatingSystem.IsWindows()
                    ? $"RetroArch {targetRelease.Tag} downloaded and updated. Cores were refreshed from nightly/latest into cores/."
                    : $"RetroArch {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("RetroArch update failed.", ex);
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
            return new RetroArchUpdateState(
                repository.DisplayValue,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"RetroArch download/update failed: {ex.Message}",
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
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, "Retroarch");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(RepoResolution repository, bool forceRefresh, CancellationToken cancellationToken)
    {
        return repository.IsNightly
            ? await GetNightlyReleasesAsync(repository, forceRefresh, cancellationToken).ConfigureAwait(false)
            : await GetGitHubReleasesAsync(repository, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetGitHubReleasesAsync(RepoResolution repository, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath)!;
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, repository.CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseGitHubReleases(cache.ReleasesJson!);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RetroArchUpdater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, repository.ReleasesEndpoint);
        if (!string.IsNullOrWhiteSpace(cache!.ETag) &&
            string.Equals(cache!.Repository, repository.CacheKey, StringComparison.OrdinalIgnoreCase))
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
            Log.Warn("Rate limit reached for RetroArch updates; using cached releases.");
            json = cache!.ReleasesJson;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new ReleaseCache
            {
                Repository = repository.CacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

        return ParseGitHubReleases(json);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetNightlyReleasesAsync(RepoResolution repository, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath)!;
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, repository.CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseNightlyReleases(cache.ReleasesJson!, repository.ReleasesEndpoint);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RetroArchNightlyUpdater/1.0");

        string? html;
        using var response = await Client.GetAsync(repository.ReleasesEndpoint, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            Log.Warn("Nightly endpoint blocked temporarily; using cached RetroArch nightly listing.");
            html = cache!.ReleasesJson;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new ReleaseCache
            {
                Repository = repository.CacheKey,
                ETag = null,
                ReleasesJson = html,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<ReleaseInfo>();

        return ParseNightlyReleases(html, repository.ReleasesEndpoint);
    }

    private static IReadOnlyList<ReleaseInfo> ParseGitHubReleases(string json)
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
            .Where(static r => !r.IsPrerelease)
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ReleaseInfo> ParseNightlyReleases(string html, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Array.Empty<ReleaseInfo>();

        var endpointUri = new Uri(endpoint, UriKind.Absolute);
        var releasesByTag = new Dictionary<string, List<ReleaseAsset>>(StringComparer.OrdinalIgnoreCase);
        var releaseDates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var latestAssets = new List<ReleaseAsset>();

        foreach (Match match in NightlyAssetRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name))
                continue;

            var lowerName = name.ToLowerInvariant();
            if (!lowerName.Contains("retroarch"))
                continue;

            if (!(lowerName.EndsWith(".zip") || lowerName.EndsWith(".7z") || lowerName.EndsWith(".exe")))
                continue;

            var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : new Uri(endpointUri, href).ToString();

            var asset = new ReleaseAsset(name, absolute);
            if (name.Equals("RetroArch.7z", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RetroArch.zip", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RetroArch-Win64-setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                latestAssets.Add(asset);
            }

            var dated = NightlyDatedAssetNameRegex.Match(name);
            if (!dated.Success)
                continue;

            var dateToken = dated.Groups["date"].Value;
            var tag = $"nightly-{dateToken}";
            if (!releasesByTag.TryGetValue(tag, out var assets))
            {
                assets = [];
                releasesByTag[tag] = assets;
            }

            assets.Add(asset);
            if (DateTimeOffset.TryParse(dateToken, out var parsedDate))
                releaseDates[tag] = parsedDate;
        }

        var releases = releasesByTag
            .Select(kvp => new ReleaseInfo(
                kvp.Key,
                IsPrerelease: true,
                releaseDates.TryGetValue(kvp.Key, out var published) ? published : null,
                kvp.Value))
            .OrderByDescending(static release => release.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static release => release.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (latestAssets.Count > 0)
        {
            releases.Insert(0, new ReleaseInfo(
                "nightly-latest",
                IsPrerelease: true,
                DateTimeOffset.UtcNow,
                latestAssets));
        }

        return releases;
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
            return assets.FirstOrDefault(asset => asset.Name.Equals("RetroArch.7z", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith("_RetroArch.7z", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            return assets.FirstOrDefault(asset => asset.Name.Contains("osx", StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault(asset => asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)))
               ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DownloadAssetAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<double>? onProgress = null)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);

        var contentLength = response.Content.Headers.ContentLength;
        var totalRead = 0L;
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            if (contentLength.HasValue && contentLength.Value > 0)
            {
                var ratio = Math.Clamp((double)totalRead / contentLength.Value, 0, 1);
                onProgress?.Invoke(ratio);
            }
        }

        onProgress?.Invoke(1d);
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

        throw new InvalidOperationException($"Unsupported archive format: {Path.GetExtension(archivePath)}");
    }

    private static void TryExtract7zWithSystemTool(string archivePath, string extractDirectory)
    {
        Directory.CreateDirectory(extractDirectory);

        // Prefer bsdtar/tar (available on recent Windows), fallback to 7z.exe when installed.
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "tar.exe", "7z.exe" }
            : new[] { "7z", "7zz", "tar" };

        foreach (var tool in candidates)
        {
            var args = tool.StartsWith("tar", StringComparison.OrdinalIgnoreCase)
                ? $"-xf \"{archivePath}\" -C \"{extractDirectory}\""
                : $"x -y \"{archivePath}\" -o\"{extractDirectory}\"";

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
                    continue;

                process.WaitForExit();
                if (process.ExitCode == 0)
                    return;
            }
            catch
            {
                // try next tool
            }
        }

        throw new InvalidOperationException("Unable to extract .7z archive. Install 7-Zip (7z.exe) or ensure tar supports 7z extraction.");
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
            ? ResolveDefaultNightlyEndpoint()
            : overrideValue.Trim();

        if (value.Contains("buildbot.libretro.com", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("/nightly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "libretro/nightly", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ResolveNightlyEndpoint(value);
            return new RepoResolution(endpoint, $"nightly:{endpoint}", endpoint, IsNightly: true);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var ownerRepo = value.Trim('/');
            return new RepoResolution(
                ownerRepo,
                $"github:{ownerRepo}",
                $"https://api.github.com/repos/{ownerRepo}/releases?per_page=20",
                IsNightly: false);
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            var ownerRepoPath = "libretro/RetroArch";
            return new RepoResolution(
                ownerRepoPath,
                $"github:{ownerRepoPath}",
                $"https://api.github.com/repos/{ownerRepoPath}/releases?per_page=20",
                IsNightly: false);
        }

        var path = $"{segments[0]}/{segments[1]}";
        if (uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new RepoResolution(
                path,
                $"github:{path}",
                $"https://api.github.com/repos/{path}/releases?per_page=20",
                IsNightly: false);
        }

        return new RepoResolution(
            $"{uri.Scheme}://{uri.Host}/{path}",
            $"{uri.Host}:{path}",
            $"{uri.Scheme}://{uri.Host}/api/v1/repos/{path}/releases?page=1&limit=20",
            IsNightly: false);
    }

    private static string ResolveDefaultNightlyEndpoint()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "https://buildbot.libretro.com/nightly/windows/x86_64/";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "https://buildbot.libretro.com/nightly/apple/osx/x86_64/";

        return "https://buildbot.libretro.com/nightly/linux/x86_64/";
    }

    private static string ResolveNightlyEndpoint(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("buildbot.libretro.com", StringComparison.OrdinalIgnoreCase))
        {
            return value.EndsWith('/') ? value : value + "/";
        }

        return ResolveDefaultNightlyEndpoint();
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
            var win = Directory.EnumerateFiles(emulatorDirectory, "retroarch.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(win))
                return win;
        }

        if (OperatingSystem.IsMacOS())
        {
            var app = Directory.EnumerateDirectories(emulatorDirectory, "RetroArch.app", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(app))
            {
                var candidate = Path.Combine(app, "Contents", "MacOS", "RetroArch");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var executableCandidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path).Contains("retroarch", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var localCandidate = executableCandidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localCandidate))
            return localCandidate;

        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
            return launcherPath;

        return null;
    }

    private static string ResolveEmulatorRootDirectory(string emulatorDirectory, string? resolvedLauncherPath)
    {
        if (!string.IsNullOrWhiteSpace(resolvedLauncherPath) && File.Exists(resolvedLauncherPath))
        {
            var folder = Path.GetDirectoryName(resolvedLauncherPath);
            if (!string.IsNullOrWhiteSpace(folder))
                return folder;
        }

        return emulatorDirectory;
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

    private static bool IsUpdateAvailable(string? currentVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
            return false;

        if (string.IsNullOrWhiteSpace(currentVersion))
            return true;

        var normalizedCurrent = NormalizeVersion(currentVersion);
        var normalizedLatest = NormalizeVersion(latestVersion);

        if (normalizedCurrent.Equals(normalizedLatest, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Version.TryParse(normalizedCurrent, out var current) &&
            Version.TryParse(normalizedLatest, out var latest))
        {
            return latest > current;
        }

        return string.Compare(normalizedLatest, normalizedCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private async Task<string?> DownloadAndInstallWindowsNightlyCoresAsync(
        string emulatorDirectory,
        string updateDirectory,
        CancellationToken cancellationToken,
        Action<double>? onProgress = null)
    {
        var coresListingUrl = "https://buildbot.libretro.com/nightly/windows/x86_64/latest/";
        var coresFolder = Path.Combine(emulatorDirectory, "cores");
        Directory.CreateDirectory(coresFolder);

        onProgress?.Invoke(0d);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-RetroArchCoresUpdater/1.0");

        using var response = await Client.GetAsync(coresListingUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
            return "RetroArch cores listing was empty.";

        var endpointUri = new Uri(coresListingUrl, UriKind.Absolute);
        var coreAssets = new List<ReleaseAsset>();
        foreach (Match match in NightlyAssetRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!name.EndsWith("_libretro.dll.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : new Uri(endpointUri, href).ToString();
            coreAssets.Add(new ReleaseAsset(name, absolute));
        }

        if (coreAssets.Count == 0)
            return "No RetroArch core packages found in nightly/latest.";

        var tempCoresDir = Path.Combine(updateDirectory, "cores_download");
        PrepareUpdateDirectory(tempCoresDir);

        try
        {
            var installedCount = 0;
            for (var index = 0; index < coreAssets.Count; index++)
            {
                var asset = coreAssets[index];
                cancellationToken.ThrowIfCancellationRequested();

                var assetStart = (double)index / coreAssets.Count;
                var assetEnd = (double)(index + 1) / coreAssets.Count;

                var localZip = Path.Combine(tempCoresDir, asset.Name);
                await DownloadAssetAsync(
                    asset.DownloadUrl,
                    localZip,
                    cancellationToken,
                    p => onProgress?.Invoke(MapProgress(assetStart, assetEnd, p * 0.75))).ConfigureAwait(false);

                var extractDir = Path.Combine(tempCoresDir, Path.GetFileNameWithoutExtension(asset.Name));
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(localZip, extractDir, overwriteFiles: true);

                var dlls = Directory.EnumerateFiles(extractDir, "*.dll", SearchOption.AllDirectories).ToList();
                for (var dllIndex = 0; dllIndex < dlls.Count; dllIndex++)
                {
                    var dll = dlls[dllIndex];
                    var destination = Path.Combine(coresFolder, Path.GetFileName(dll));
                    File.Copy(dll, destination, overwrite: true);
                    installedCount++;

                    var copyProgress = dlls.Count == 0 ? 1d : (double)(dllIndex + 1) / dlls.Count;
                    onProgress?.Invoke(MapProgress(assetStart, assetEnd, 0.75 + (copyProgress * 0.25)));
                }

                onProgress?.Invoke(assetEnd);
            }

            onProgress?.Invoke(1d);
            return $"RetroArch cores updated: {installedCount} core DLL(s) into '{coresFolder}'.";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempCoresDir))
                    Directory.Delete(tempCoresDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static double MapProgress(double start, double end, double ratio)
    {
        var boundedRatio = Math.Clamp(ratio, 0, 1);
        return start + ((end - start) * boundedRatio);
    }

    private static void ReportProgress(Action<RetroArchUpdateProgress>? progress, double percent, string? status = null)
    {
        progress?.Invoke(new RetroArchUpdateProgress(Math.Clamp(percent, 0, 100), status));
    }

    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return string.Empty;

        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        return trimmed;
    }

    private static string? GetFileVersionSafe(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            return null;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(launcherPath);
            return string.IsNullOrWhiteSpace(versionInfo.FileVersion)
                ? versionInfo.ProductVersion
                : versionInfo.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    private static ReleaseCache? LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ReleaseCache>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string path, ReleaseCache cache)
    {
        try
        {
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }
}
