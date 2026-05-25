using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services.Cemu;

public sealed record CemuGraphicPacksDownloadResult(bool Success, string Message, bool WasAlreadyUpToDate);

public static class CemuGraphicPacksDownloadService
{
    private static readonly ILog Log = LogHelper.For(typeof(CemuGraphicPacksDownloadService));
    private const string QueryUrlTemplate =
        "https://cemu.info/api2/query_graphicpack_url.php?version={0}&t={1}";

    private const string GitHubLatestReleaseApi =
        "https://api.github.com/repos/cemu-project/cemu_graphic_packs/releases/latest";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    static CemuGraphicPacksDownloadService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AES_Lacrima-CemuGraphicPacks/1.0");
    }

    public static async Task<CemuGraphicPacksDownloadResult> DownloadLatestAsync(
        string? emulatorDirectory,
        string? launcherPath,
        CancellationToken cancellationToken = default)
    {
        if (!CemuPathsService.TryResolveUserDataDirectory(emulatorDirectory, launcherPath, out var userDataDirectory))
            return new CemuGraphicPacksDownloadResult(false, "Cemu user data directory was not configured.", false);

        var downloadedDirectory = CemuPathsService.GetDownloadedGraphicPacksDirectory(userDataDirectory);
        var versionPath = CemuPathsService.GetDownloadedVersionPath(userDataDirectory);
        Directory.CreateDirectory(downloadedDirectory);

        string releaseJson;
        try
        {
            var queryUrl = string.Format(
                QueryUrlTemplate,
                Uri.EscapeDataString("2.6.0"),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var githubApiUrl = await TryResolveGithubApiUrlAsync(queryUrl, cancellationToken).ConfigureAwait(false)
                               ?? GitHubLatestReleaseApi;

            releaseJson = await Client.GetStringAsync(new Uri(githubApiUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CemuGraphicPacksDownloadResult(false, $"Failed to query graphic pack release: {ex.Message}", false);
        }

        using var document = JsonDocument.Parse(releaseJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            return new CemuGraphicPacksDownloadResult(false, "Graphic pack release response did not include a name.", false);

        var releaseName = nameElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(releaseName))
            return new CemuGraphicPacksDownloadResult(false, "Graphic pack release name was empty.", false);

        if (File.Exists(versionPath))
        {
            var installedVersion = (await File.ReadAllTextAsync(versionPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.Equals(installedVersion, releaseName, StringComparison.OrdinalIgnoreCase))
                return new CemuGraphicPacksDownloadResult(true, "Graphic packs are already up to date.", true);
        }

        if (!root.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array ||
            assetsElement.GetArrayLength() == 0)
        {
            return new CemuGraphicPacksDownloadResult(false, "Graphic pack release did not include a download asset.", false);
        }

        var asset = assetsElement[0];
        if (!asset.TryGetProperty("browser_download_url", out var downloadUrlElement) ||
            downloadUrlElement.ValueKind != JsonValueKind.String)
        {
            return new CemuGraphicPacksDownloadResult(false, "Graphic pack release asset did not include a download URL.", false);
        }

        var downloadUrl = downloadUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            return new CemuGraphicPacksDownloadResult(false, "Graphic pack download URL was empty.", false);

        try
        {
            var zipBytes = await Client.GetByteArrayAsync(new Uri(downloadUrl), cancellationToken).ConfigureAwait(false);
            ClearDownloadedGraphicPacks(downloadedDirectory);
            ExtractZipToDirectory(zipBytes, downloadedDirectory);
            await File.WriteAllTextAsync(versionPath, releaseName, cancellationToken).ConfigureAwait(false);
            return new CemuGraphicPacksDownloadResult(true, $"Downloaded graphic packs ({releaseName}).", false);
        }
        catch (Exception ex)
        {
            return new CemuGraphicPacksDownloadResult(false, $"Failed to download graphic packs: {ex.Message}", false);
        }
    }

    private static async Task<string?> TryResolveGithubApiUrlAsync(string queryUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Client.GetStringAsync(new Uri(queryUrl), cancellationToken).ConfigureAwait(false);
            var trimmed = response.Trim();
            return trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearDownloadedGraphicPacks(string downloadedDirectory)
    {
        if (!Directory.Exists(downloadedDirectory))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(downloadedDirectory))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
        }
    }

    private static void ExtractZipToDirectory(byte[] zipBytes, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        using var memoryStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            if (entry.FullName.Contains("..", StringComparison.Ordinal))
                continue;

            var targetPath = Path.Combine(destinationDirectory, entry.FullName);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            if (entry.Length == 0)
                continue;

            if (entry.Length > 128 * 1024 * 1024)
                continue;

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }
}
