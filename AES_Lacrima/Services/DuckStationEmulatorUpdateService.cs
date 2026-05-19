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
using AES_Emulation.EmulationHandlers;
using log4net;

namespace AES_Lacrima.Services;

public sealed record DuckStationUpdateState(
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
public partial class DuckStationEmulatorUpdateService
{
    private const string Repository = "https://github.com/stenzek/duckstation";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/stenzek/duckstation/releases?per_page=100";
    private const string CacheKey = "github:stenzek/duckstation";
    private const string CacheFileName = "duckstation-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "duckstation_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<DuckStationEmulatorUpdateService>();
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

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<DuckStationUpdateState> GetUpdateInfoAsync(
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
                .Take(12)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New DuckStation version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "DuckStation is not installed in this section yet."
                    : $"DuckStation is up to date ({currentVersion}).";

            return new DuckStationUpdateState(
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
            Log.Warn("Failed to fetch DuckStation update info; returning local status only.", ex);
            return new DuckStationUpdateState(
                Repository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check DuckStation updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<DuckStationUpdateState> DownloadOrUpdateAsync(
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
                return new DuckStationUpdateState(Repository, GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath), null, false, Array.Empty<string>(), "No DuckStation releases found.", emulatorDirectory, updateDirectory, noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new DuckStationUpdateState(Repository, GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), $"Version '{requestedVersion}' was not found.", emulatorDirectory, updateDirectory, unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new DuckStationUpdateState(Repository, GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), "No compatible DuckStation asset found for this OS.", emulatorDirectory, updateDirectory, missingAssetLauncherPath);
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

            var portableExecutablePath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            DuckStationHandler.EnsurePortableModeMarker(portableExecutablePath, emulatorDirectory);

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

            return new DuckStationUpdateState(
                Repository,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"DuckStation {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("DuckStation update failed.", ex);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch
            {
            }

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new DuckStationUpdateState(
                Repository,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"DuckStation download/update failed: {ex.Message}",
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
        var emulatorDirectory = Path.Combine(ApplicationPaths.EmulatorsDirectory, safeSectionKey, "DuckStation");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(bool includePrereleases, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new ReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseReleases(cache.ReleasesJson!, includePrereleases);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-DuckStationUpdater/1.0");

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
        else if (response.StatusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(cache?.ReleasesJson))
        {
            Log.Warn("Rate limit reached for DuckStation updates; using cached releases.");
            json = cache!.ReleasesJson;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            cache = new ReleaseCache
            {
                Repository = CacheKey,
                ETag = response.Headers.ETag?.Tag,
                ReleasesJson = json,
                FetchedAtUtc = DateTimeOffset.UtcNow
            };
            SaveCache(cachePath, cache);
        }

        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ReleaseInfo>();

        return ParseReleases(json, includePrereleases);
    }

    private static IReadOnlyList<ReleaseInfo> ParseReleases(string json, bool includePrereleases)
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
            return assets.FirstOrDefault(asset =>
                       asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("release", StringComparison.OrdinalIgnoreCase) &&
                       !asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                       !asset.Name.Contains("sse2", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                       asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("release", StringComparison.OrdinalIgnoreCase) &&
                       !asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                       asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                       !asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsLinux())
        {
            return assets.FirstOrDefault(asset =>
                       asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                       asset.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            return assets.FirstOrDefault(asset =>
                       asset.Name.Contains("mac", StringComparison.OrdinalIgnoreCase) &&
                       asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
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
            var prioritized = new[]
            {
                "duckstation-qt-x64-ReleaseLTCG.exe",
                "duckstation-qt-x64-Release.exe",
                "duckstation-qt.exe",
                "duckstation.exe"
            };
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
                .FirstOrDefault(path => Path.GetFileName(path).Contains("duckstation", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appImageCandidate))
                return appImageCandidate;
        }

        if (OperatingSystem.IsMacOS())
        {
            var appBundleCandidate = Directory.EnumerateDirectories(emulatorDirectory, "*.app", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("duckstation", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appBundleCandidate))
                return appBundleCandidate;
        }

        var candidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path).Contains("duckstation", StringComparison.OrdinalIgnoreCase))
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

    private static ReleaseCache? LoadCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;

            var json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize<ReleaseCache>(json);
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
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
        }
    }
}
