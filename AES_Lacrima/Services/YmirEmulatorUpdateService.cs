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
using AES_Lacrima.Serialization;
using log4net;

using AES_Core.Logging;
namespace AES_Lacrima.Services;

public sealed record YmirUpdateState(
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
public partial class YmirEmulatorUpdateService
{
    private const string Repository = "https://github.com/StrikerX3/Ymir";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/StrikerX3/Ymir/releases?per_page=100";
    private const string CacheKey = "github:StrikerX3/Ymir";
    private const string CacheFileName = "ymir-releases-cache.json";
    private const string InstalledVersionMarkerFileName = "ymir_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<YmirEmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record ReleaseInfo(
        string Tag,
        bool IsPrerelease,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets,
        string? ReleaseNotes = null);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<YmirUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includeNightlies = false,
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
                ? $"New Ymir version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "Ymir is not installed in this section yet."
                    : $"Ymir is up to date ({currentVersion}).";

            if (includeNightlies)
                status += " (Including nightlies)";

            return new YmirUpdateState(
                Repository,
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
            Log.Warn("Failed to fetch Ymir update info; returning local status only.", ex);
            return new YmirUpdateState(
                Repository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check Ymir updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<YmirUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool includeNightlies = false,
        string? requestedVersion = null,
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
                return new YmirUpdateState(Repository, GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath), null, false, Array.Empty<string>(), "No Ymir releases found.", emulatorDirectory, updateDirectory, noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new YmirUpdateState(Repository, GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), $"Version '{requestedVersion}' was not found.", emulatorDirectory, updateDirectory, unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new YmirUpdateState(Repository, GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), "No compatible Ymir asset found for this OS.", emulatorDirectory, updateDirectory, missingAssetLauncherPath);
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
                TryMarkLinuxExecutable(destinationPath);
            }

            TryMarkYmirLinuxExecutables(emulatorDirectory);
            YmirHandler.EnsurePortableProfile(emulatorDirectory);
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

            return new YmirUpdateState(
                Repository,
                currentVersion,
                latest,
                updateAvailable,
                versions,
                $"Ymir {targetRelease.Tag} downloaded and updated.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        catch (Exception ex)
        {
            Log.Error("Ymir update failed.", ex);
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            try
            {
                PrepareUpdateDirectory(updateDirectory);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            return new YmirUpdateState(
                Repository,
                GetInstalledVersion(emulatorDirectory, resolvedLauncherPath),
                null,
                false,
                Array.Empty<string>(),
                $"Ymir download/update failed: {ex.Message}",
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
        var sectionDirectory = EmulatorSectionDirectoryHelper.GetEmulatorSectionDirectory(sectionKey, sectionTitle);
        var emulatorDirectory = Path.Combine(sectionDirectory, "Ymir");
        var updateDirectory = Path.Combine(emulatorDirectory, "Emu_Update");
        Directory.CreateDirectory(emulatorDirectory);
        Directory.CreateDirectory(updateDirectory);
        return (emulatorDirectory, updateDirectory);
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(bool includeNightlies, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(ApplicationPaths.CacheDirectory, CacheFileName);
        var cache = LoadCache(cachePath) ?? new EmulatorReleaseCache();
        if (!forceRefresh &&
            cache.Repository != null &&
            string.Equals(cache.Repository, CacheKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cache.ReleasesJson) &&
            (DateTimeOffset.UtcNow - cache.FetchedAtUtc) <= CacheTtl)
        {
            return ParseReleases(cache.ReleasesJson!, includeNightlies);
        }

        Directory.CreateDirectory(ApplicationPaths.CacheDirectory);

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-YmirUpdater/1.0");

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
            Log.Warn("Rate limit reached for Ymir updates; using cached releases.");
            json = cache!.ReleasesJson;
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
            return Array.Empty<ReleaseInfo>();

        return ParseReleases(json, includeNightlies);
    }

    private static IReadOnlyList<ReleaseInfo> ParseReleases(string json, bool includeNightlies)
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

            if (item["draft"]?.GetValue<bool>() == true)
                continue;

            var prerelease = item["prerelease"]?.GetValue<bool>() == true;
            if (prerelease && !includeNightlies)
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

            if (SelectAssetForPlatform(assets) == null)
                continue;

            results.Add(new ReleaseInfo(tag, prerelease, publishedAt, assets, EmulatorReleaseNotesHelper.ParseGitHubReleaseBody(item)));
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

        static bool IsYmirAsset(ReleaseAsset asset)
            => asset.Name.StartsWith("ymir-", StringComparison.OrdinalIgnoreCase);

        static bool IsAvx2Asset(ReleaseAsset asset)
            => asset.Name.Contains("AVX2", StringComparison.OrdinalIgnoreCase);

        static bool IsSse2Asset(ReleaseAsset asset)
            => asset.Name.Contains("SSE2", StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            return EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && IsAvx2Asset(asset))
                   ?? EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && IsSse2Asset(asset))
                   ?? EmulatorReleaseAssetSelection.SelectFirstWindowsAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("windows", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsLinux())
        {
            return EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && IsAvx2Asset(asset))
                   ?? EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) && IsSse2Asset(asset))
                   ?? EmulatorReleaseAssetSelection.SelectFirstLinuxAsset(
                       assets,
                       static asset => asset.Name,
                       asset => IsYmirAsset(asset) && asset.Name.Contains("linux", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            var architecture = EmulatorReleaseAssetSelection.ResolveHostArchitecture();
            if (architecture == System.Runtime.InteropServices.Architecture.Arm64)
            {
                return assets.FirstOrDefault(asset =>
                           IsYmirAsset(asset) &&
                           asset.Name.Contains("macos-arm64", StringComparison.OrdinalIgnoreCase))
                       ?? assets.FirstOrDefault(asset =>
                           IsYmirAsset(asset) &&
                           asset.Name.Contains("macos", StringComparison.OrdinalIgnoreCase));
            }

            return assets.FirstOrDefault(asset =>
                       IsYmirAsset(asset) &&
                       asset.Name.Contains("macos-x64", StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(asset =>
                       IsYmirAsset(asset) &&
                       asset.Name.Contains("macos", StringComparison.OrdinalIgnoreCase));
        }

        return assets.FirstOrDefault(IsYmirAsset);
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

    private static string? ResolveLauncherPath(string? launcherPath, string emulatorDirectory)
    {
        var prioritized = new[] { "ymir-sdl3.exe", "ymir-sdl3" };
        foreach (var executableName in prioritized)
        {
            var candidate = Directory.EnumerateFiles(emulatorDirectory, executableName, SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        if (OperatingSystem.IsMacOS())
        {
            var appBundleCandidate = Directory.EnumerateDirectories(emulatorDirectory, "*.app", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("ymir", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(appBundleCandidate))
                return appBundleCandidate;
        }

        var candidates = Directory.EnumerateFiles(emulatorDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileName(path).Contains("ymir", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var localCandidate = candidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(localCandidate))
            return localCandidate;

        if (!string.IsNullOrWhiteSpace(launcherPath) && (File.Exists(launcherPath) || Directory.Exists(launcherPath)))
            return launcherPath;

        return null;
    }

    private static void TryMarkYmirLinuxExecutables(string emulatorDirectory)
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var candidate in Directory.EnumerateFiles(emulatorDirectory, "ymir-sdl3", SearchOption.AllDirectories))
            TryMarkLinuxExecutable(candidate);
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
            Log.Debug($"Failed to mark Ymir Linux file as executable: '{path}'.", ex);
        }
    }

    private static string? GetInstalledVersion(string emulatorDirectory, string? launcherPath)
    {
        var markerPath = Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName);
        var markerVersion = ReadInstalledVersionMarker(markerPath);
        if (!string.IsNullOrWhiteSpace(markerVersion))
            return markerVersion;

        return GetFileVersionSafe(launcherPath);
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

    private static EmulatorReleaseCache? LoadCache(string cachePath) =>
        EmulatorReleaseCachePersistence.Load(cachePath);

    private static void SaveCache(string cachePath, EmulatorReleaseCache cache) =>
        EmulatorReleaseCachePersistence.Save(cachePath, cache);
}
