using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpCompress.Archives;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Threading.Tasks;
using log4net;

namespace AES_Controls.Helpers;

/// <summary>
/// Event args for completion events raised after install/upgrade/uninstall operations.
/// </summary>
public sealed class InstallationCompletedEventArgs : EventArgs
{
    public InstallationCompletedEventArgs(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success { get; }
    public string Message { get; }
}

/// <summary>
/// Responsible for ensuring a compatible yt-dlp binary is available.
/// Handles downloading the platform-specific version and unpacking 
/// zip bundles on Windows to include the _internal folder.
/// </summary>
[AutoRegister]
public partial class YtDlpManager : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(YtDlpManager));
    private const string Repo = "yt-dlp/yt-dlp";
    private readonly string _destFolder = AppContext.BaseDirectory;
    private static readonly HttpClient Client = new();

    /// <summary>
    /// Raised when an installation/upgrade/uninstall operation completes.
    /// </summary>
    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    /// <summary>
    /// Human readable status text for display in the UI.
    /// </summary>
    [ObservableProperty]
    private string _status = "Idle";

    /// <summary>
    /// Indicates an ongoing operation (download or installation in progress).
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Gets a value indicating whether yt-dlp is locally installed in the application directory.
    /// </summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp"));

    /// <summary>
    /// Percentage (0-100) representing the current download progress.
    /// </summary>
    [ObservableProperty]
    private double _downloadProgress;

    /// <summary>
    /// True while a download is in progress.
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
        if (File.Exists(Path.Combine(_destFolder, binName))) 
        {
            Status = "yt-dlp is already installed.";
            return;
        }

        IsBusy = true;
        Status = "yt-dlp not found. Starting automatic setup...";
        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            await DownloadLatestAsync();
            Status = "yt-dlp installation successful.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
        }
        catch (Exception ex)
        {
            Status = $"yt-dlp installation failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            DownloadProgress = 100;
        }
    }

    /// <summary>
    /// Attempts to uninstall yt-dlp from the application directory.
    /// </summary>
    public async Task<bool> UninstallAsync()
    {
        IsBusy = true;
        Status = "Uninstalling yt-dlp...";

        try
        {
            string binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
            string fullPath = Path.Combine(_destFolder, binName);

            if (File.Exists(fullPath))
            {
                // On Windows, yt-dlp might have an _internal folder
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    string internalFolder = Path.Combine(_destFolder, "_internal");
                    if (Directory.Exists(internalFolder))
                    {
                        try { Directory.Delete(internalFolder, true); }
                        catch (Exception ex) { Log.Warn($"Failed to delete _internal folder: {ex.Message}"); }
                    }
                }

                File.Delete(fullPath);
                Status = "yt-dlp uninstalled.";
                InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
                return true;
            }

            Status = "yt-dlp not found, nothing to uninstall.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"yt-dlp uninstall failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Gets the current version of the locally installed yt-dlp.
    /// </summary>
    public async Task<string?> GetCurrentVersionAsync()
    {
        string binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        string fullPath = Path.Combine(_destFolder, binName);
        if (!File.Exists(fullPath)) return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not determine yt-dlp version", ex);
            return null;
        }
    }

    /// <summary>
    /// Checks for a new version of yt-dlp on GitHub.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; YtDlpDownloader)");
            
            var response = await Client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(response);
            
            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                return tagProp.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch latest yt-dlp version", ex);
        }
        return null;
    }

    /// <summary>
    /// Forces an update to the latest version of yt-dlp.
    /// </summary>
    public async Task UpdateAsync()
    {
        IsBusy = true;
        Status = "Updating yt-dlp...";
        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            await DownloadLatestAsync();
            Status = "yt-dlp update successful.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
        }
        catch (Exception ex)
        {
            Status = $"yt-dlp update failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
        }
        finally
        {
            IsBusy = false;
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
                    // Update ObservableProperty - UI updates automatically
                    DownloadProgress = (double)totalRead / totalBytes * 100;
                }
            }
        }

        if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = SharpCompress.Archives.Zip.ZipArchive.Open(tempFile);
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
