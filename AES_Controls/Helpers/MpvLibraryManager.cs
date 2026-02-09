using CommunityToolkit.Mvvm.ComponentModel;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AES_Controls.Helpers;

/// <summary>
/// Responsible for ensuring a compatible libmpv binary is available to the
/// application. Handles downloading Windows builds and locating system
/// libraries on Unix-like platforms.
/// </summary>
public partial class MpvLibraryManager : ObservableObject
{
    private const string Repo = "zhongfly/mpv-winbuild";
    private readonly string _destFolder = AppContext.BaseDirectory;
    private static readonly HttpClient _client = new();

    /// <summary>
    /// Percentage (0-100) representing the current download progress when
    /// retrieving a libmpv package from a remote release.
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
    /// Ensures that the appropriate libmpv library is present in the
    /// application's directory. On Windows this may download a prebuilt
    /// package; on Linux/macOS the method will attempt to locate a system-installed
    /// library and copy it into the app folder.
    /// </summary>
    /// <returns>A task that completes when the check and any installation are finished.</returns>
    public async Task EnsureLibraryInstalledAsync()
    {
        string libName = GetPlatformLibName();
        if (File.Exists(Path.Combine(_destFolder, libName))) return;

        IsDownloading = true;
        DownloadProgress = 0;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                await DownloadWindowsLgplAsync(libName);
            else
                CopySystemLibrary(libName);
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 100; // Ensure it hits 100% on completion
        }
    }

    /// <summary>
    /// Download the LGPL Windows build of mpv from the configured GitHub
    /// repository releases and extract the requested library name.
    /// </summary>
    /// <param name="libName">The library filename to extract (for example "libmpv-2.dll").</param>
    private async Task DownloadWindowsLgplAsync(string libName)
    {
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; MpvDownloader)");
        var response = await _client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(response);

        JsonElement? found = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (a.TryGetProperty("name", out var nameProp))
            {
                var name = nameProp.GetString();
                if (!string.IsNullOrEmpty(name) && name.Contains("mpv-dev-lgpl-x86_64") && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    found = a;
                    break;
                }
            }
        }

        if (found == null)
        {
            Debug.WriteLine("No suitable LGPL mpv build found in release assets.");
            return;
        }

        if (!found.Value.TryGetProperty("browser_download_url", out var urlProp))
        {
            Debug.WriteLine("Release asset missing browser_download_url.");
            return;
        }

        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url))
        {
            Debug.WriteLine("Download URL is empty.");
            return;
        }

        await DownloadWithProgressAsync(url, libName);
    }

    /// <summary>
    /// Downloads the file at <paramref name="url"/> to a temporary location
    /// while reporting progress to <see cref="DownloadProgress"/>, then
    /// extracts <paramref name="libName"/> into the application folder.
    /// </summary>
    /// <param name="url">The download URL pointing to a .7z archive.</param>
    /// <param name="libName">The library filename to extract.</param>
    private async Task DownloadWithProgressAsync(string url, string libName)
    {
        if (string.IsNullOrEmpty(url)) return;

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
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
                    Debug.WriteLine($"Download progress: {DownloadProgress:F2}%");
                }
            }
        }

        // Extraction Logic
        using (var archive = SevenZipArchive.Open(tempFile))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Key?.EndsWith(libName, StringComparison.OrdinalIgnoreCase) == true);
            if (entry != null)
            {
                entry.WriteToDirectory(_destFolder, new SharpCompress.Common.ExtractionOptions { ExtractFullPath = false, Overwrite = true });
            }
        }

        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
    }

    /// <summary>
    /// Returns the expected library filename for the current platform.
    /// </summary>
    private string GetPlatformLibName() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "libmpv-2.dll" :
                                           RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libmpv.dylib" : "libmpv.so";

    /// <summary>
    /// Attempts to locate an installed libmpv on the host system and copy it
    /// into the application's directory. If not found, logs an instruction
    /// for the user on how to install mpv/mpv-dev.
    /// </summary>
    /// <param name="libName">The library filename to search for and copy.</param>
    private void CopySystemLibrary(string libName)
    {
        // Common search paths where package managers install shared libraries
        string[] searchPaths = {
        "/usr/lib",
        "/usr/local/lib",
        "/opt/homebrew/lib",                      // macOS (Homebrew ARM)
        "/usr/lib/x86_64-linux-gnu",              // Ubuntu/Debian x64
        "/usr/lib/aarch64-linux-gnu",             // Ubuntu/Debian ARM
        "/Applications/IINA.app/Contents/Frameworks" // macOS (Fallback if IINA is installed)
    };

        bool found = false;

        foreach (var path in searchPaths)
        {
            string fullPath = Path.Combine(path, libName);
            if (File.Exists(fullPath))
            {
                try
                {
                    string destination = Path.Combine(_destFolder, libName);
                    // 'true' allows overwriting if an old version exists
                    File.Copy(fullPath, destination, true);

                    Console.WriteLine($"Successfully localized {libName} from {path}");
                    found = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Found library at {path} but could not copy: {ex.Message}");
                }
            }
        }

        if (!found)
        {
            // On Linux/macOS, we don't auto-download from GitHub usually 
            // because of architecture complexity (ARM vs x64).
            string installCmd = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "brew install mpv"
                : "sudo apt install libmpv-dev";

            Console.WriteLine($"CRITICAL: {libName} not found on system.");
            Console.WriteLine($"Please run: {installCmd}");
        }
    }
}