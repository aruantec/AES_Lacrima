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
    /// <returns>A task returning true if yt-dlp is locally available; otherwise false.</returns>
    public async Task<bool> EnsureInstalledAsync()
    {
        string binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe" : "yt-dlp";
        if (File.Exists(Path.Combine(_destFolder, binName))) 
        {
            Status = "yt-dlp is already installed.";
            return true;
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
            return true;
        }
        catch (Exception ex)
        {
            Status = $"yt-dlp installation failed: {ex.Message}";
            Log.Error(Status, ex);
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
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
        LoadCache();

        if (!string.IsNullOrEmpty(_cache?.LatestVersion) && (DateTime.Now - _cache.LastUpdated).TotalMinutes < 15)
        {
            return _cache.LatestVersion;
        }

        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; YtDlpDownloader; AES_Lacrima)");

            string apiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            if (!string.IsNullOrEmpty(_cache?.ETag))
            {
                request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(_cache.ETag));
            }

            using var response = await Client.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                if (_cache != null) _cache.LastUpdated = DateTime.Now;
                return _cache?.LatestVersion;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Remaining"))
            {
                Log.Warn("GitHub API rate limit exceeded while checking yt-dlp version.");
                return _cache?.LatestVersion;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
            {
                var ver = tagProp.GetString();
                if (_cache != null)
                {
                    _cache.LatestVersion = ver;
                    _cache.ETag = response.Headers.ETag?.Tag;
                    _cache.LatestReleaseJson = json;
                    _cache.LastUpdated = DateTime.Now;
                    SaveCache();
                }
                return ver;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch latest yt-dlp version", ex);
        }

        return _cache?.LatestVersion;
    }

    private static YtDlpCacheEntry? _cache;
    private static readonly string _cachePath = Path.Combine(AppContext.BaseDirectory, "ytdlp_cache.json");

    private sealed class YtDlpCacheEntry
    {
        public string? ETag { get; set; }
        public string? LatestVersion { get; set; }
        public string? LatestReleaseJson { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    private void SaveCache()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex) { Log.Warn("Failed to save yt-dlp cache to disk", ex); }
    }

    private void LoadCache()
    {
        if (_cache != null) return;
        if (!File.Exists(_cachePath))
        {
            _cache = new YtDlpCacheEntry();
            return;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            _cache = JsonSerializer.Deserialize<YtDlpCacheEntry>(json) ?? new YtDlpCacheEntry();
        }
        catch (Exception ex) 
        { 
            Log.Warn("Failed to load yt-dlp cache from disk", ex); 
            _cache = new YtDlpCacheEntry();
        }
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
        LoadCache();

        Client.DefaultRequestHeaders.UserAgent.Clear();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; YtDlpDownloader; AES_Lacrima)");

        string apiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
        Log.Debug($"Fetching latest yt-dlp release info from {apiUrl}");

        string? json = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            if (!string.IsNullOrEmpty(_cache?.ETag))
            {
                request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(_cache.ETag));
            }

            using var responseMessage = await Client.SendAsync(request);

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                Log.Info("yt-dlp latest release info not modified (304), using cache.");
                json = _cache?.LatestReleaseJson;
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.Forbidden && responseMessage.Headers.Contains("X-RateLimit-Remaining"))
            {
                Log.Warn("GitHub API rate limit exceeded during yt-dlp release fetch.");
                json = _cache?.LatestReleaseJson;
            }
            else
            {
                responseMessage.EnsureSuccessStatusCode();
                json = await responseMessage.Content.ReadAsStringAsync();

                if (_cache != null)
                {
                    _cache.LatestReleaseJson = json;
                    _cache.ETag = responseMessage.Headers.ETag?.Tag;
                    _cache.LastUpdated = DateTime.Now;
                    
                    if (JsonDocument.Parse(json).RootElement.TryGetProperty("tag_name", out var tagProp))
                        _cache.LatestVersion = tagProp.GetString();
                        
                    SaveCache();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch latest yt-dlp release info", ex);
            json = _cache?.LatestReleaseJson;
        }

        if (string.IsNullOrEmpty(json))
        {
            throw new InvalidOperationException("Could not retrieve yt-dlp release information (API limit or connection error).");
        }

        using var doc = JsonDocument.Parse(json);

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
