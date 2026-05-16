using AES_Core.DI;
using AES_Core.IO;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services;

public sealed record CemuUpdateState(
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
public partial class CemuEmulatorUpdateService
{
    private const string Repository = "https://github.com/cemu-project/Cemu";
    private const string ReleasesApiEndpoint = "https://api.github.com/repos/cemu-project/Cemu/releases?per_page=100";
    private const string InstalledVersionMarkerFileName = "cemu_version.txt";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<CemuEmulatorUpdateService>();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record ReleaseInfo(
        string Tag,
        bool IsPrerelease,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl);

    public async Task<CemuUpdateState> GetUpdateInfoAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
        var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
        var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);

        try
        {
            var releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            var versions = releases
                .Select(static r => r.Tag)
                .Where(static v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
            var latest = versions.FirstOrDefault();
            var updateAvailable = IsUpdateAvailable(currentVersion, latest);
            var status = updateAvailable
                ? $"New Cemu version available: {latest}"
                : string.IsNullOrWhiteSpace(currentVersion)
                    ? "Cemu is not installed in this section yet."
                    : $"Cemu is up to date ({currentVersion}).";

            return new CemuUpdateState(
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
            Log.Warn("Failed to fetch Cemu update info; returning local status only.", ex);
            return new CemuUpdateState(
                Repository,
                currentVersion,
                null,
                false,
                Array.Empty<string>(),
                $"Failed to check Cemu updates: {ex.Message}",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
    }

    public async Task<CemuUpdateState> DownloadOrUpdateAsync(
        string sectionKey,
        string sectionTitle,
        string? launcherPath,
        string? requestedVersion,
        Action<UpdateProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (emulatorDirectory, updateDirectory) = EnsureDirectories(sectionKey, sectionTitle);
            var releases = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
            if (releases.Count == 0)
            {
                var noReleaseLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new CemuUpdateState(Repository, GetInstalledVersion(emulatorDirectory, noReleaseLauncherPath), null, false, Array.Empty<string>(), "No Cemu releases found.", emulatorDirectory, updateDirectory, noReleaseLauncherPath);
            }

            var targetRelease = ResolveTargetRelease(releases, requestedVersion);
            if (targetRelease == null)
            {
                var unresolvedVersionLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new CemuUpdateState(Repository, GetInstalledVersion(emulatorDirectory, unresolvedVersionLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), $"Version '{requestedVersion}' was not found.", emulatorDirectory, updateDirectory, unresolvedVersionLauncherPath);
            }

            var selectedAsset = SelectAssetForPlatform(targetRelease.Assets);
            if (selectedAsset == null)
            {
                var missingAssetLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
                return new CemuUpdateState(Repository, GetInstalledVersion(emulatorDirectory, missingAssetLauncherPath), releases[0].Tag, false, releases.Select(static r => r.Tag).Take(12).ToList(), "No compatible Cemu asset found for this OS.", emulatorDirectory, updateDirectory, missingAssetLauncherPath);
            }

            PrepareUpdateDirectory(updateDirectory);
            var downloadedAssetPath = Path.Combine(updateDirectory, selectedAsset.Name);

            onProgress?.Invoke(new UpdateProgress(10, $"Downloading Cemu {targetRelease.Tag}..."));
            await DownloadAssetAsync(selectedAsset.DownloadUrl, downloadedAssetPath, onProgress, cancellationToken).ConfigureAwait(false);

            onProgress?.Invoke(new UpdateProgress(90, "Extracting Cemu..."));
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

            PrepareUpdateDirectory(updateDirectory);
            SaveInstalledVersionMarker(emulatorDirectory, targetRelease.Tag);

            var resolvedLauncherPath = ResolveLauncherPath(launcherPath, emulatorDirectory);
            var currentVersion = GetInstalledVersion(emulatorDirectory, resolvedLauncherPath);
            var versions = releases.Select(static r => r.Tag).Take(12).ToList();
            var latest = versions.FirstOrDefault();

            return new CemuUpdateState(
                Repository,
                currentVersion,
                latest,
                IsUpdateAvailable(currentVersion, latest),
                versions,
                $"Cemu {targetRelease.Tag} updated successfully.",
                emulatorDirectory,
                updateDirectory,
                resolvedLauncherPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        if (Client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-CemuUpdater/1.0");
        }

        var response = await Client.GetAsync(ReleasesApiEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonArray;

        return root?.OfType<JsonObject>()
            .Select(item => new ReleaseInfo(
                item["tag_name"]?.GetValue<string>() ?? "",
                item["prerelease"]?.GetValue<bool>() ?? false,
                item["published_at"]?.GetValue<DateTimeOffset>(),
                (item["assets"] as JsonArray)?
                    .OfType<JsonObject>()
                    .Select(a => new ReleaseAsset(
                        a["name"]?.GetValue<string>() ?? "",
                        a["browser_download_url"]?.GetValue<string>() ?? ""))
                    .ToList() ?? []))
            .ToList() ?? [];
    }

    private static (string EmulatorDirectory, string UpdateDirectory) EnsureDirectories(string sectionKey, string sectionTitle)
    {
        var emuDir = Path.Combine(ApplicationPaths.EmulatorsDirectory, "WIIU", "Cemu");
        var updateDir = Path.Combine(emuDir, "Emu_Update");
        Directory.CreateDirectory(emuDir);
        Directory.CreateDirectory(updateDir);
        return (emuDir, updateDir);
    }

    private static string? ResolveLauncherPath(string? launcherPath, string emulatorDirectory)
    {
        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
            return Path.GetFullPath(launcherPath);

        var exeName = "Cemu.exe";
        var candidate = Path.Combine(emulatorDirectory, exeName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : Path.GetFullPath(candidate);
    }

    private static string? GetInstalledVersion(string emulatorDirectory, string? resolvedLauncherPath)
    {
        var markerPath = Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName);
        if (File.Exists(markerPath))
            return File.ReadAllText(markerPath).Trim();

        return resolvedLauncherPath != null ? "Installed" : null;
    }

    private static void SaveInstalledVersionMarker(string emulatorDirectory, string version)
    {
        var markerPath = Path.Combine(emulatorDirectory, InstalledVersionMarkerFileName);
        File.WriteAllText(markerPath, version);
    }

    private static bool IsUpdateAvailable(string? current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latest))
            return false;
        if (current == "Installed")
            return true;
        return !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
    }

    private static ReleaseInfo? ResolveTargetRelease(IReadOnlyList<ReleaseInfo> releases, string? requestedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion))
            return releases.FirstOrDefault();
        return releases.FirstOrDefault(r => string.Equals(r.Tag, requestedVersion, StringComparison.OrdinalIgnoreCase));
    }

    private static ReleaseAsset? SelectAssetForPlatform(IReadOnlyList<ReleaseAsset> assets)
    {
        return assets.FirstOrDefault(a => 
            a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) && 
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    private static void PrepareUpdateDirectory(string updateDirectory)
    {
        if (Directory.Exists(updateDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(updateDirectory)) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(updateDirectory)) Directory.Delete(dir, true);
        }
        else
        {
            Directory.CreateDirectory(updateDirectory);
        }
    }

    private async Task DownloadAssetAsync(string url, string destinationPath, Action<UpdateProgress>? onProgress, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;
            if (totalBytes.HasValue)
            {
                var percent = (double)totalRead / totalBytes.Value * 80 + 10;
                onProgress?.Invoke(new UpdateProgress(percent));
            }
        }
    }

    private static string NormalizeExtractionRoot(string extractDirectory)
    {
        var subDirs = Directory.GetDirectories(extractDirectory);
        if (subDirs.Length == 1 && Directory.GetFiles(extractDirectory).Length == 0)
            return subDirs[0];
        return extractDirectory;
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, destination));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), overwrite: true);
    }
}

public record UpdateProgress(double Percent, string? StatusMessage = null);
