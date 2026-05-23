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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Serialization;
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services;

public sealed record FlycastUpdateState(
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
public partial class FlycastEmulatorUpdateService
{
    private const string Repository = "https://github.com/flyinghead/flycast";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/flyinghead/flycast/releases?per_page=100";
    private const string NightlyBuildsEndpoint = "https://flycast-builds.s3.fr-par.scw.cloud/";
    private const string CacheFileName = "flycast-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "flycast_version.txt";
    private const string ReleaseCacheKey = "github:flyinghead/flycast";
    private const string NightlyCacheKey = "flycast:nightly";

    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<FlycastEmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record ReleaseInfo(
        string Tag,
        bool IsNightly,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets,
        string? ReleaseNotes = null);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<FlycastUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includeNightlies,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);

        try
        {
            var releases = await GetReleasesAsync(includeNightlies, forceRefresh, cancellationToken).ConfigureAwait(false);
            var latestRelease = releases.FirstOrDefault();
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            var latest = latestRelease?.Tag ?? versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New Flycast version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "Flycast is not installed in this section yet."
                    : $"Flycast is up to date ({currentVersion}).";

            if (includeNightlies)
                status += " (Nightly feed)";

            return new FlycastUpdateState(
                includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
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
            Log.Warn("Failed to fetch Flycast update info; returning local status only.", ex);
            return new FlycastUpdateState(
                includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check Flycast updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<FlycastUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includeNightlies,
        string? requestedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var releases = await GetReleasesAsync(includeNightlies, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new FlycastUpdateState(
                    includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                    GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath),
                    null,
                    false,
                    Array.Empty<string>(),
                    "No Flycast versions found from update sources.",
                    emulatorDirectory,
                    updateDirectory,
                    noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new FlycastUpdateState(
                    includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                    GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(12).ToList(),
                    $"Version '{requestedVersion}' was not found.",
                    emulatorDirectory,
                    updateDirectory,
                    unresolvedVersionLauncherPath);
            }

            var selectedAsset = targetRelease.IsNightly
                ? targetRelease.Assets.FirstOrDefault()
                : SelectAssetForWindowsX64(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new FlycastUpdateState(
                    includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                    GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath),
                    releases[0].Tag,
                    false,
                    releases.Select(static r => r.Tag).Take(12).ToList(),
                    "No compatible Flycast Windows x64 asset found.",
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
                .Take(12)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);

            return new FlycastUpdateState(
                includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"Flycast {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("Flycast update failed.", ex);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch
            {
            }

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new FlycastUpdateState(
                includeNightlies ? "https://flyinghead.github.io/flycast-builds" : Repository,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"Flycast download/update failed: {ex.Message}",
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
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, safeSectionKey, "Flycast");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(bool includeNightlies, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (includeNightlies)
            return await GetNightlyReleasesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        return await GetGitHubReleasesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetGitHubReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new FlycastReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, ReleaseCacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.Payload) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseGitHubReleases(cache.Payload!);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-FlycastUpdater/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiEndpoint);
        if (!string.IsNullOrWhiteSpace(cache.ETag) && string.Equals(cache.Repository, ReleaseCacheKey, StringComparison.OrdinalIgnoreCase))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cache.ETag));

        string? json;
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && !string.IsNullOrWhiteSpace(cache?.Payload))
        {
            json = cache!.Payload;
        }
        else if (response.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(cache?.Payload))
        {
            Log.Warn("Rate limit reached for Flycast GitHub releases; using cached releases.");
            json = cache!.Payload;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new FlycastReleaseCache
            {
                Repository = ReleaseCacheKey,
                ETag = response.Headers.ETag?.Tag,
                Payload = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

        return ParseGitHubReleases(json);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetNightlyReleasesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new FlycastReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, NightlyCacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.Payload) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseNightlyReleases(cache.Payload!);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-FlycastUpdater/1.0");

        using var response = await Client.GetAsync(NightlyBuildsEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        cache = new FlycastReleaseCache
        {
            Repository = NightlyCacheKey,
            Payload = xml,
            FetchedAtUtc = DateTimeOffset.UtcNow
        };
        SaveCache(cachePath, cache);
        return ParseNightlyReleases(xml);
    }

    private static IReadOnlyList<ReleaseInfo> ParseGitHubReleases(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

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
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;

                    if (!IsWindowsX64AssetName(name))
                        continue;

                    assets.Add(new ReleaseAsset(name, url));
                }
            }

            if (assets.Count == 0)
                continue;

            results.Add(new ReleaseInfo(NormalizeVersionTag(tag)!, false, publishedAt, assets, EmulatorReleaseNotesHelper.ParseGitHubReleaseBody(item)));
        }

        return results
            .OrderByDescending(static r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ReleaseInfo> ParseNightlyReleases(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return Array.Empty<ReleaseInfo>();

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch
        {
            return Array.Empty<ReleaseInfo>();
        }

        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        var releases = new List<ReleaseInfo>();

        foreach (var content in document.Root?.Elements(ns + "Contents") ?? Enumerable.Empty<XElement>())
        {
            var key = content.Element(ns + "Key")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) ||
                !key.StartsWith("win/heads/", StringComparison.OrdinalIgnoreCase) ||
                !key.EndsWith("/flycast.zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastModifiedRaw = content.Element(ns + "LastModified")?.Value;
            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(lastModifiedRaw, out var parsedLastModified))
                publishedAt = parsedLastModified;

            var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;

            var branch = parts[2];
            var commitId = branch.Contains('-', StringComparison.OrdinalIgnoreCase)
                ? branch[(branch.LastIndexOf('-') + 1)..]
                : branch;
            var shortCommit = commitId.Length > 7 ? commitId[..7] : commitId;
            var tag = publishedAt.HasValue
                ? $"{publishedAt.Value:yyyy-MM-dd} {shortCommit}"
                : shortCommit;

            var downloadUrl = $"https://flycast-builds.s3.fr-par.scw.cloud/{key}";
            releases.Add(new ReleaseInfo(tag, true, publishedAt, new[] { new ReleaseAsset(Path.GetFileName(key), downloadUrl) }));
        }

        return releases
            .GroupBy(static r => r.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
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
            string.Equals(NormalizeVersionTag(release.Tag), NormalizeVersionTag(requestedVersion), StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseAsset? SelectAssetForWindowsX64(IReadOnlyList<ReleaseAsset> assets)
    {
        if (assets.Count == 0)
            return null;

        return assets.FirstOrDefault(asset => IsWindowsX64AssetName(asset.Name));
    }

    private static bool IsWindowsX64AssetName(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        var lower = assetName.ToLowerInvariant();
        if (!lower.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !lower.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
            !lower.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (lower.Contains("win64", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("win", StringComparison.OrdinalIgnoreCase)) &&
               !lower.Contains("symbol", StringComparison.OrdinalIgnoreCase) &&
               !lower.Contains("debug", StringComparison.OrdinalIgnoreCase) &&
               !lower.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
               !lower.Contains("arm", StringComparison.OrdinalIgnoreCase);
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
            var prioritized = new[] { "flycast.exe", "Flycast.exe", "flycast-windows-x64.exe" };
            foreach (var executableName in prioritized)
            {
                var candidate = Directory.EnumerateFiles(emulatorDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }
        }

        var candidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("flycast", StringComparison.OrdinalIgnoreCase))
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
        => string.Equals(NormalizeVersionTag(currentVersion), NormalizeVersionTag(releaseVersion), StringComparison.OrdinalIgnoreCase);

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
        var normalized = NormalizeVersionTag(value) ?? string.Empty;
        var parts = new List<int>();
        foreach (Match match in Regex.Matches(normalized, "\\d+"))
        {
            if (int.TryParse(match.Value, out var number))
                parts.Add(number);
        }

        return parts;
    }

    private static void TrimTrailingZeros(List<int> parts)
    {
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (parts[i] != 0)
                break;

            parts.RemoveAt(i);
        }
    }

    private static string? NormalizeVersionTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
            normalized = normalized[1..];

        return normalized;
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
            "GAMECUBE" => "GC",
            "NINTENDO GAMECUBE" => "GC",
            "GCN" => "GC",
            "GC" => "GC",
            "WII" => "WII",
            "NINTENDO WII" => "WII",
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

    private static FlycastReleaseCache? LoadCache(string cachePath) =>
        FlycastReleaseCachePersistence.Load(cachePath);

    private static void SaveCache(string cachePath, FlycastReleaseCache cache) =>
        FlycastReleaseCachePersistence.Save(cachePath, cache);
}
