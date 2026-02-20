using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using log4net;

namespace AES_Controls.Helpers;

/// <summary>
/// Information about a libmpv release on GitHub.
/// </summary>
public sealed class MpvReleaseInfo
{
    public string Tag { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public override string ToString() => string.IsNullOrEmpty(Title) ? Tag : Title;
}

/// <summary>
/// Responsible for ensuring a compatible libmpv binary is available to the
/// application. Handles downloading Windows builds and locating system
/// libraries on Unix-like platforms.
/// </summary>
[AutoRegister]
public partial class MpvLibraryManager : ObservableObject
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MpvLibraryManager));
    private const string Repo = "zhongfly/mpv-winbuild";
    private readonly string _destFolder = AppContext.BaseDirectory;
    private static readonly HttpClient Client = new();

    /// <summary>
    /// Event raised when libmpv usage should be terminated (e.g., before uninstallation).
    /// </summary>
    public event Action? RequestMpvTermination;

    /// <summary>
    /// Attempts to stop libmpv usage across the application.
    /// Broadcasts <see cref="RequestMpvTermination"/>.
    /// </summary>
    public void KillAllMpvActivity()
    {
        RequestMpvTermination?.Invoke();
    }

    /// <summary>
    /// Raised when an installation/upgrade/uninstall operation completes.
    /// </summary>
    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    /// <summary>
    /// Human readable status text for display in the UI.
    /// </summary>
    [ObservableProperty]
    private string _status = "Idle";

    private int _activeTaskCount;

    /// <summary>
    /// Indicates an ongoing operation (download or installation in progress).
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        if (!value) UpdateStatusInternal();
    }

    /// <summary>
    /// Reports libmpv activity to update the status label.
    /// </summary>
    /// <param name="isActive">True if background libmpv activity is starting; false if it has stopped.</param>
    public void ReportActivity(bool isActive)
    {
        if (isActive) Interlocked.Increment(ref _activeTaskCount);
        else Interlocked.Decrement(ref _activeTaskCount);

        // Ensure count doesn't drop below zero due to race conditions or mismatched calls
        if (_activeTaskCount < 0) Interlocked.Exchange(ref _activeTaskCount, 0);

        UpdateStatusInternal();
    }

    private void UpdateStatusInternal()
    {
        if (IsBusy) return;

        if (_activeTaskCount > 0)
        {
            Status = $"libmpv is active ({_activeTaskCount} task(s))";
        }
        else if (string.IsNullOrEmpty(Status) || Status == "Idle" || Status.StartsWith("libmpv is active"))
        {
            Status = "Idle";
        }
    }

    /// <summary>
    /// True while a download is in progress.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    /// Percentage (0-100) representing the current download progress.
    /// </summary>
    [ObservableProperty]
    private double _downloadProgress;

    /// <summary>
    /// Indicates if a library update or removal requires an application restart to complete.
    /// </summary>
    [ObservableProperty]
    private bool _isPendingRestart;

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

        // Clear user-requested uninstallation marker if manual install is triggered
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string markerPath = Path.Combine(_destFolder, libName + ".delete");
            try 
            { 
                if (File.Exists(markerPath)) File.Delete(markerPath); 
            } catch (Exception ex) { Log.Warn($"Failed to clear uninstall marker {markerPath}", ex); }
        }

        if (File.Exists(Path.Combine(_destFolder, libName)))
        {
            Status = "libmpv is already installed.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
            return;
        }

        IsBusy = true;
        Status = "libmpv not found. Starting automatic setup...";
        DownloadProgress = 0;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                await DownloadWindowsLgplAsync(libName);
            else
                CopySystemLibrary(libName);

            Status = "libmpv installation successful.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
        }
        catch (Exception ex)
        {
            Status = $"libmpv installation failed: {ex.Message}";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            DownloadProgress = 100; // Ensure it hits 100% on completion
        }
    }

    /// <summary>
    /// Attempts to uninstall libmpv from the application directory.
    /// </summary>
    public async Task<bool> UninstallAsync()
    {
        KillAllMpvActivity();
        await Task.Delay(1000); // Give player more time to fully release the library

        IsBusy = true;
        Status = "Uninstalling libmpv...";

        try
        {
            string libName = GetPlatformLibName();
            string fullPath = Path.Combine(_destFolder, libName);

            // ALWAYS create the persistent marker for uninstallation state on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string markerPath = fullPath + ".delete";
                try 
                { 
                    if (!File.Exists(markerPath)) File.WriteAllText(markerPath, string.Empty); 
                } catch (Exception ex) { Log.Error($"Failed to create uninstall marker {markerPath}", ex); }
            }

            if (File.Exists(fullPath))
            {
                if (!TryDeleteFile(fullPath))
                {
                    // Marker logic is already inside TryDeleteFile, but we provide specific feedback here
                    Status = "libmpv is currently in use. It will be removed after the application restarts.";
                    InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
                    return true;
                }

                // Cleanup macOS alternate name
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string altPath = Path.Combine(_destFolder, "libmpv.2.dylib");
                    if (File.Exists(altPath)) File.Delete(altPath);
                }
                
                Status = "libmpv uninstalled.";
                InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
                return true;
            }
            
            Status = "libmpv not found, nothing to uninstall.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"libmpv uninstall failed: {ex.Message}";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Checks whether libmpv is locally installed.
    /// </summary>
    public bool IsLibraryInstalled()
    {
        return File.Exists(Path.Combine(_destFolder, GetPlatformLibName()));
    }

    /// <summary>
    /// Checks if a new version is waiting to be applied at restart.
    /// </summary>
    public bool IsNewVersionPending()
    {
        return File.Exists(Path.Combine(_destFolder, GetPlatformLibName() + ".update"));
    }

    /// <summary>
    /// Gets the current version of the locally installed libmpv.
    /// On Windows, this reads the FileVersion from the DLL.
    /// </summary>
    /// <returns>The version string or null if not found.</returns>
    public async Task<string?> GetCurrentVersionAsync()
    {
        string libPath = Path.Combine(_destFolder, GetPlatformLibName());
        if (!File.Exists(libPath)) return null;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var info = FileVersionInfo.GetVersionInfo(libPath);
                return info.ProductVersion ?? info.FileVersion;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // On Unix platforms, we could try running 'mpv --version' if it's in the PATH,
                // but since we are managing the library specifically, we might look for a version file
                // or just leave it for now if we can't easily extract it from the .so/.dylib itself.
                // However, often 'mpv' command is available if the library is installed via package manager.
                return await GetUnixMpvVersionAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error getting libmpv version", ex);
        }

        return null;
    }

    private async Task<string?> GetUnixMpvVersionAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "mpv",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(firstLine)) return null;

            // Typically starts with "mpv 0.35.0-unknown ..."
            var match = Regex.Match(firstLine, @"mpv\s+([^\s]+)");
            return match.Success ? match.Groups[1].Value : firstLine;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not determine unix mpv version via CLI", ex);
            return null;
        }
    }

    /// <summary>
    /// Fetches all available versions for Windows from the GitHub repository.
    /// </summary>
    public async Task<List<MpvReleaseInfo>> GetAvailableVersionsAsync()
    {
        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; MpvDownloader)");

            var response = await Client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases");
            using var doc = JsonDocument.Parse(response);

            var versions = new List<MpvReleaseInfo>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                string? tag = null;
                string? name = null;

                if (release.TryGetProperty("tag_name", out var tagProp))
                    tag = tagProp.GetString();

                if (release.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString();

                if (!string.IsNullOrEmpty(tag))
                {
                    versions.Add(new MpvReleaseInfo { Tag = tag, Title = name ?? tag });
                }
            }
            return versions;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to fetch versions from GitHub", ex);
            return new List<MpvReleaseInfo>();
        }
    }

    /// <summary>
    /// Downloads and installs a specific version of libmpv for Windows.
    /// </summary>
    public async Task<bool> InstallVersionAsync(string tagName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Status = "Version selection is only supporting Windows builds currently.";
            return false;
        }

        KillAllMpvActivity();
        await Task.Delay(1000); // Give player time to fully release the library

        IsBusy = true;
        IsDownloading = true;
        Status = $"Installing version {tagName}...";
        DownloadProgress = 0;

        try
        {
            Client.DefaultRequestHeaders.UserAgent.Clear();
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; MpvDownloader)");
            
            var response = await Client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/{tagName}");
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

            if (found == null || !found.Value.TryGetProperty("browser_download_url", out var urlProp))
            {
                Status = $"No suitable build found for version {tagName}.";
                InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(false, Status));
                return false;
            }

            await DownloadWithProgressAsync(urlProp.GetString()!, GetPlatformLibName());
            Status = $"Version {tagName} installed successfully.";
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(true, Status));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Failed to install version {tagName}: {ex.Message}";
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
    /// Download the LGPL Windows build of mpv from the configured GitHub
    /// repository releases and extract the requested library name.
    /// </summary>
    /// <param name="libName">The library filename to extract (for example "libmpv-2.dll").</param>
    private async Task DownloadWindowsLgplAsync(string libName)
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Compatible; MpvDownloader)");
        var response = await Client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
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
            Log.Warn("No suitable LGPL mpv build in release assets.");
            return;
        }

        if (!found.Value.TryGetProperty("browser_download_url", out var urlProp))
        {
            Log.Warn("Release asset missing browser_download_url.");
            return;
        }

        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url))
        {
            Log.Warn("Download URL is empty.");
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
                    Log.Debug($"Download progress: {DownloadProgress:F2}%");
                }
            }
        }

        // Extraction Logic
        using (var archive = SevenZipArchive.Open(tempFile))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Key?.EndsWith(libName, StringComparison.OrdinalIgnoreCase) == true);
            if (entry != null)
            {
                // Verify if we need to write to a temporary name if target was locked
                string targetPath = Path.Combine(_destFolder, libName);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(targetPath))
                {
                    // Attempt the rename trick to replace the locked DLL immediately
                    if (!TryDeleteFile(targetPath))
                    {
                        // If rename failed (permissions? volume boundary?), fallback to .update staging
                        string updatePath = targetPath + ".update";
                        try
                        {
                            if (File.Exists(updatePath)) File.Delete(updatePath);
                            using (var fs = File.Create(updatePath))
                            {
                                entry.WriteTo(fs);
                            }
                            _isPendingRestart = true;
                            Status = "The update is staged as .update and will be applied on the next application restart.";
                            return;
                        }
                        catch (Exception ex)
                        {
                            throw new IOException($"Could not prepare update: {ex.Message}. Please try restarting the app first.", ex);
                        }
                    }
                }

                entry.WriteToDirectory(_destFolder, new SharpCompress.Common.ExtractionOptions { ExtractFullPath = false, Overwrite = true });
            }
        }

        // On macOS some consumers expect the SONAME/lib name to be 'libmpv.2.dylib'.
        // If we just extracted 'libmpv.dylib', create a copy named 'libmpv.2.dylib' so
        // the runtime can find the expected filename.
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && string.Equals(libName, "libmpv.dylib", StringComparison.OrdinalIgnoreCase))
            {
                var extracted = Path.Combine(_destFolder, "libmpv.dylib");
                var alt = Path.Combine(_destFolder, "libmpv.2.dylib");
                if (File.Exists(extracted) && !File.Exists(alt))
                {
                    File.Copy(extracted, alt, true);
                    Log.Debug($"Created macOS alternate lib name: {alt}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to create alternate macOS lib name", ex);
        }

        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception ex) { Log.Warn($"Failed to delete temporary mpv archive: {ex.Message}"); }
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

                    Log.Info($"Successfully localized {libName} from {path}");
                    // On macOS ensure alternate SONAME filename is also present
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && string.Equals(libName, "libmpv.dylib", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var altDest = Path.Combine(_destFolder, "libmpv.2.dylib");
                            if (!File.Exists(altDest)) File.Copy(destination, altDest, true);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Could not create libmpv.2.dylib copy: {ex.Message}");
                        }
                    }
                    found = true;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Found library at {path} but could not copy", ex);
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

            Log.Error($"CRITICAL: {libName} not found on system. Please run: {installCmd}");
        }
    }

    private bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            try
            {
                // If it's Windows and it's locked, attempt to move it to a unique .delete file
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // 1. Create a zero-byte marker for the specific library name to skip auto-setup on restart
                    string markerPath = path + ".delete";
                    try 
                    { 
                        if (!File.Exists(markerPath)) File.WriteAllText(markerPath, string.Empty); 
                    } catch (Exception ex) { Log.Warn($"Failed to create delete marker {markerPath}", ex); }

                    // 2. Rename the locked file to a unique .delete name so the directory entry is freed.
                    // This allows a new file with the SAME name to be created in the same directory.
                    string uniqueDelPath = path + "." + Guid.NewGuid().ToString("N") + ".delete";
                    File.Move(path, uniqueDelPath);
                    Log.Info($"Renamed locked file {path} to {uniqueDelPath} for startup cleanup.");

                    _isPendingRestart = true; 
                    return true; 
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to rename locked file {path}", ex);
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error while trying to delete {path}", ex);
            return false;
        }
    }

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

    private static string? ExtractVersionFromText(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var m = Regex.Match(input, @"\d+:?\d+(?:[.\-_]\d+)+");
        if (m.Success) return m.Value;

        m = Regex.Match(input, @"\d+(?:\.\d+)+");
        return m.Success ? m.Value : null;
    }
}