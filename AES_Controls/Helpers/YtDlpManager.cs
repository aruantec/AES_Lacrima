using CommunityToolkit.Mvvm.ComponentModel;
using SharpCompress.Archives;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AES_Controls.Helpers;

/// <summary>
/// Responsible for ensuring a compatible yt-dlp binary is available.
/// Handles downloading the platform-specific version and unpacking 
/// zip bundles on Windows to include the _internal folder.
/// </summary>
public partial class YtDlpManager : ObservableObject
{
    private const string Repo = "yt-dlp/yt-dlp";
    private readonly string _destFolder = AppContext.BaseDirectory;
    private static readonly HttpClient Client = new();

    /// <summary>
    /// Gets a value indicating whether yt-dlp is locally installed in the application directory.
    /// </summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp"));

    /// <summary>
    /// Percentage (0-100) representing the current download progress.
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private double _downloadProgress;

    /// <summary>
    /// True while a download is in progress.
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    /// Ensures that yt-dlp is present in the application's directory.
    /// Downloads the latest release from GitHub if missing.
    /// </summary>
    /// <returns>A task that represents the asynchronous installation operation.</returns>
    public async Task EnsureInstalledAsync()
    {
        string binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        if (File.Exists(Path.Combine(_destFolder, binName))) return;

        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            await DownloadLatestAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to install yt-dlp: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 100;
        }
    }

    /// <summary>
    /// Locates and downloads the latest yt-dlp release asset for the current platform.
    /// </summary>
    private async Task DownloadLatestAsync()
    {
        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; YtDlpDownloader)");
        
        var response = await Client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(response);

        string targetAsset = GetPlatformAssetName();
        JsonElement? found = null;

        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString()?.Equals(targetAsset, StringComparison.OrdinalIgnoreCase) == true)
            {
                found = a;
                break;
            }
        }

        if (found == null)
        {
            Debug.WriteLine($"No suitable yt-dlp build found for {targetAsset}.");
            return;
        }

        var url = found.Value.GetProperty("browser_download_url").GetString();
        if (string.IsNullOrEmpty(url)) return;

        await DownloadWithProgressAsync(url, targetAsset);
    }

    /// <summary>
    /// Downloads the file with progress reporting and extracts it if necessary.
    /// </summary>
    private async Task DownloadWithProgressAsync(string url, string assetName)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        string tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        using (var destinationStream = File.Create(tempFile))
        using (var sourceStream = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) != 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes != -1)
                {
                    DownloadProgress = (double)totalRead / totalBytes * 100;
                }
            }
        }

        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ArchiveFactory.Open(tempFile);
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                // Preserve folder structure (important for _internal)
                entry.WriteToDirectory(_destFolder, new SharpCompress.Common.ExtractionOptions 
                { 
                    ExtractFullPath = true, 
                    Overwrite = true 
                });
            }
        }
        else
        {
            // For single-file binary downloads (Linux/macOS)
            string destPath = Path.Combine(_destFolder, assetName.Contains("macos") ? "yt-dlp" : assetName);
            
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tempFile, destPath);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Set executable permissions on Unix-like systems
                try
                {
                    Process.Start(new ProcessStartInfo("chmod", $"+x \"{destPath}\"") { CreateNoWindow = true })?.WaitForExit();
                }
                catch
                {
                    // ignored
                }
            }
        }

        try { if (File.Exists(tempFile)) File.Delete(tempFile); }
        catch
        {
            // ignored
        }
    }

    private string GetPlatformAssetName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "yt-dlp_win.zip";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "yt-dlp_macos";
        return "yt-dlp"; // Standard Linux binary
    }
}
