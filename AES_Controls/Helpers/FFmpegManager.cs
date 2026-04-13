using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;

namespace AES_Controls.Helpers;

/// <summary>
/// Manages detection and installation of FFmpeg for the running platform.
/// Exposes a simple status and busy indicator and provides an async method
/// to ensure FFmpeg is installed (using platform-specific installers).
/// </summary>
[AutoRegister]
public partial class FFmpegManager : ObservableObject
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<FFmpegManager>();
    /// <summary>
    /// Event raised when FFmpeg processes should be terminated (e.g., before uninstallation).
    /// </summary>
    public event Action? RequestFfmpegTermination;

    /// <summary>
    /// Attempts to stop all active FFmpeg processes across the application.
    /// Broadcasts <see cref="RequestFfmpegTermination"/> and kills lingering ffmpeg processes.
    /// </summary>
    public void KillAllFfmpegActivity()
    {
        // 1. Notify listeners to cancel operations
        RequestFfmpegTermination?.Invoke();

        // 2. Kill all ffmpeg child processes (best effort)
        try
        {
            var processes = Process.GetProcessesByName("ffmpeg");
            foreach (var p in processes)
            {
                try { p.Kill(true); }
                catch (Exception ex) { Log.Warn($"Failed to kill ffmpeg process {p.Id}", ex); }
            }
        }
        catch (Exception ex) { Log.Error("Failed to list ffmpeg processes for killing", ex); }
    }

    /// <summary>
    /// Human readable status text for display in the UI.
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private string _status = "Idle";

    private int _activeTaskCount;
    private int _lastExitCode;

    /// <summary>
    /// Indicates an ongoing operation (installation in progress).
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        if (!value) UpdateStatusInternal();
    }

    /// <summary>
    /// Reports FFmpeg activity (e.g. background analysis or decoding) to updating the status label.
    /// </summary>
    /// <param name="isActive">True if background FFmpeg activity is starting; false if it has stopped.</param>
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
            Status = $"FFmpeg is active ({_activeTaskCount} task(s))";
        }
        else if (string.IsNullOrEmpty(Status) || Status == "Idle" || Status.StartsWith("FFmpeg is active"))
        {
            Status = "Idle";
        }
    }

    /// <summary>
    /// Ensures that FFmpeg is available on the system. If FFmpeg cannot be
    /// found in PATH, this method will attempt to run a platform-specific
    /// installer and update <see cref="Status"/> and <see cref="IsBusy"/> accordingly.
    /// </summary>
    /// <returns>True if FFmpeg is available; otherwise false.</returns>
    public async Task<bool> EnsureFFmpegInstalledAsync()
    {
        if (IsFFmpegAvailable())
        {
            Status = "FFmpeg is already installed.";
            return true;
        }

        return await InstallAsync();
    }

    /// <summary>
    /// Raised when an installation/upgrade/uninstall operation completes.
    /// </summary>
    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    /// <summary>
    /// Installs FFmpeg using the platform-specific installer and raises <see cref="InstallationCompleted"/>.
    /// </summary>
    /// <returns>True if successful; otherwise false.</returns>
    public async Task<bool> InstallAsync()
    {
        Log.Info("FFmpeg install requested.");
        IsBusy = true;
        Status = "FFmpeg not found. Starting installation...";

        bool success = await RunPlatformInstaller();

        Status = success ? "FFmpeg installation successful." : $"FFmpeg installation failed (Exit code: {_lastExitCode}).";
        Log.Info($"FFmpeg install completed. Success={success}, ExitCode={_lastExitCode}");
        IsBusy = false;

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    /// <summary>
    /// Attempts to upgrade FFmpeg using the platform-specific package manager.
    /// </summary>
    public async Task<bool> UpgradeAsync()
    {
        Log.Info("FFmpeg upgrade requested.");
        IsBusy = true;
        Status = "Starting FFmpeg upgrade...";

        bool result = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Info("Using winget to upgrade FFmpeg.");
            result = await ExecuteCommandAsync("winget", "upgrade --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Log.Info("Using brew to upgrade FFmpeg.");
            result = await ExecuteCommandAsync("brew", "upgrade ffmpeg");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Log.Info("Using apt-get to upgrade FFmpeg.");
            result = await ExecuteCommandAsync("sudo", "apt-get install -y ffmpeg");
        }

        IsBusy = false;
        Status = result ? "FFmpeg upgrade completed." : $"FFmpeg upgrade failed (Exit code: {_lastExitCode}).";
        Log.Info($"FFmpeg upgrade completed. Result={result}, ExitCode={_lastExitCode}");

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(result, Status));

        return result;
    }

    /// <summary>
    /// Checks whether an update is available for FFmpeg through the platform package manager.
    /// This performs a best-effort check and may return false when detection is inconclusive.
    /// </summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        var details = await CheckForUpdateDetailsAsync();
        return details?.UpdateAvailable ?? false;
    }

    /// <summary>
    /// Returns detailed information about an available FFmpeg update (best-effort).
    /// </summary>
    public async Task<CheckUpdateResult?> CheckForUpdateDetailsAsync()
    {
        Log.Info("Checking FFmpeg update details.");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Try JSON output first
                var json = await ExecuteCommandCaptureAsync("winget", "upgrade --id Gyan.FFmpeg --output json");
                if (!string.IsNullOrEmpty(json) && json.TrimStart().StartsWith("["))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            foreach (var prop in el.EnumerateObject())
                            {
                                var name = prop.Name;
                                if (name.IndexOf("available", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(name, "AvailableVersion", StringComparison.OrdinalIgnoreCase))
                                {
                                    var ver = prop.Value.GetString();
                                    if (!string.IsNullOrEmpty(ver))
                                        return new CheckUpdateResult(true, ver, json);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Failed to parse winget JSON output for FFmpeg update check", ex);
                    }
                }

                // Fallback: text parsing
                var output = await ExecuteCommandCaptureAsync("winget", "upgrade --id Gyan.FFmpeg");
                if (string.IsNullOrWhiteSpace(output)) return new CheckUpdateResult(false, null, output);

                if (output.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase) || output.Contains("No applicable upgrades", StringComparison.OrdinalIgnoreCase))
                    return new CheckUpdateResult(false, null, output);

                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.IndexOf("Gyan.FFmpeg", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var ver = ExtractVersionFromText(line);
                        if (!string.IsNullOrEmpty(ver)) return new CheckUpdateResult(true, ver, output);
                    }
                }

                return new CheckUpdateResult(false, null, output);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var infoJson = await ExecuteCommandCaptureAsync("brew", "info --json=v2 ffmpeg");
                var installed = await ExecuteCommandCaptureAsync("brew", "list --versions ffmpeg");

                string? latest = null;
                if (!string.IsNullOrEmpty(infoJson) && infoJson.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(infoJson);
                        if (doc.RootElement.TryGetProperty("formulae", out var arr) && arr.GetArrayLength() > 0)
                        {
                            var f = arr[0];
                            if (f.TryGetProperty("versions", out var versions) && versions.TryGetProperty("stable", out var stable))
                            {
                                latest = stable.GetString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Failed to parse brew info JSON output for FFmpeg update check", ex);
                    }
                }

                var installedVersion = ExtractVersionFromText(installed ?? string.Empty);
                if (!string.IsNullOrEmpty(latest) && !string.Equals(latest, installedVersion, StringComparison.OrdinalIgnoreCase))
                    return new CheckUpdateResult(true, latest, infoJson + "\n" + (installed ?? string.Empty));

                return new CheckUpdateResult(false, null, infoJson + "\n" + (installed ?? string.Empty));
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var output = await ExecuteCommandCaptureAsync("bash", "-lc \"apt-cache policy ffmpeg\"");
                if (string.IsNullOrWhiteSpace(output)) return new CheckUpdateResult(false, null, output);

                string? candidate = null;
                string? installed = null;
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Candidate:", StringComparison.OrdinalIgnoreCase))
                        candidate = trimmed.Substring("Candidate:".Length).Trim();
                    if (trimmed.StartsWith("Installed:", StringComparison.OrdinalIgnoreCase))
                        installed = trimmed.Substring("Installed:".Length).Trim();
                }

                if (!string.IsNullOrEmpty(candidate) && !string.Equals(candidate, installed, StringComparison.OrdinalIgnoreCase) && candidate != "(none)")
                    return new CheckUpdateResult(true, candidate, output);

                return new CheckUpdateResult(false, null, output);
            }

            return new CheckUpdateResult(false, null, null);
        }
        catch (Exception ex)
        {
            Log.Error("An error occurred during FFmpeg update check details retrieval", ex);
            return new CheckUpdateResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Result of a detailed update check.
    /// </summary>
    public sealed class CheckUpdateResult
    {
        public CheckUpdateResult(bool updateAvailable, string? newVersion, string? rawOutput)
        {
            UpdateAvailable = updateAvailable;
            NewVersion = newVersion;
            RawOutput = rawOutput;
        }

        public bool UpdateAvailable { get; }
        public string? NewVersion { get; }
        public string? RawOutput { get; }
    }

    private static string? ExtractVersionFromText(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var m = Regex.Match(input, @"\d+:?\d+(?:[.\-_]\d+)+");
        if (m.Success) return m.Value;

        m = Regex.Match(input, @"\d+(?:\.\d+)+");
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Uninstalls FFmpeg via the platform-specific package manager.
    /// </summary>
    public async Task<bool> UninstallAsync()
    {
        Log.Info("FFmpeg uninstall requested.");
        KillAllFfmpegActivity();
        await Task.Delay(500); // Give processes a moment to terminate

        IsBusy = true;
        Status = "Uninstalling FFmpeg...";

        bool result = false;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Info("Using winget to uninstall FFmpeg.");
            result = await ExecuteCommandAsync("winget", "uninstall --id Gyan.FFmpeg -e --silent");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Log.Info("Using brew to uninstall FFmpeg.");
            result = await ExecuteCommandAsync("brew", "uninstall ffmpeg");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Log.Info("Using apt-get to uninstall FFmpeg.");
            result = await ExecuteCommandAsync("sudo", "apt-get remove -y ffmpeg");
        }

        IsBusy = false;

        Status = result ? "FFmpeg uninstalled." : $"FFmpeg uninstall failed (Exit code: {(_lastExitCode == 0 ? "Unknown" : _lastExitCode)}).";
        Log.Info($"FFmpeg uninstall completed. Result={result}, ExitCode={_lastExitCode}");

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(result, Status));

        return result;
    }

    /// <summary>
    /// Gets the current version of FFmpeg installed in the system PATH.
    /// </summary>
    public async Task<string?> GetCurrentVersionAsync()
    {
        Log.Debug("Querying current FFmpeg version.");
        var output = await ExecuteCommandCaptureAsync("ffmpeg", "-version");
        if (string.IsNullOrEmpty(output))
        {
            Log.Debug("FFmpeg version query returned no output.");
            return null;
        }
        var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var version = ExtractVersionFromText(firstLine ?? string.Empty);
        Log.Debug($"FFmpeg version detected: {version}");
        return version;
    }

    /// <summary>
    /// Checks whether FFmpeg is accessible via the system PATH by attempting
    /// to start the process with the <c>-version</c> argument.
    /// </summary>
    /// <returns><c>true</c> when FFmpeg appears available; otherwise <c>false</c>.</returns>
    public bool IsFFmpegAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                Log.Debug("FFmpeg executable detected in PATH.");
                return true;
            }

            Log.Debug("FFmpeg executable not found in PATH.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug("FFmpeg availability check failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Attempts to run a platform-specific package manager command to install FFmpeg.
    /// </summary>
    /// <returns><c>true</c> if the installer reported success; otherwise <c>false</c>.</returns>
    private async Task<bool> RunPlatformInstaller()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Log.Info("Running FFmpeg installer via winget.");
            // Using -e (exact) and gyan builds which are the standard for Windows
            return await ExecuteCommandAsync("winget", "install --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Log.Info("Running FFmpeg installer via brew.");
            return await ExecuteCommandAsync("brew", "install ffmpeg");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Log.Info("Running FFmpeg installer via apt-get.");
            // Requires sudo; user will likely see a password prompt in the terminal
            return await ExecuteCommandAsync("sudo", "apt-get install -y ffmpeg");
        }

        Log.Warn("FFmpeg installer is not supported on this platform.");
        return false;
    }

    /// <summary>
    /// Executes the specified command using <see cref="Process"/> and returns
    /// a task that completes when the process exits. The task result is
    /// <c>true</c> when the exit code is zero.
    /// </summary>
    /// <param name="fileName">The executable or command to run (for example, "winget" or "brew").</param>
    /// <param name="args">The arguments to pass to the command.</param>
    /// <returns>A task that resolves to <c>true</c> when the command succeeded.</returns>
    private async Task<bool> ExecuteCommandAsync(string fileName, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.Verb = "runas";
        }

        try
        {
            Log.Info($"Starting external command: {fileName} {args}");
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                Log.Warn($"Failed to start external command: {fileName} {args}");
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitedTask = Task.Run(() => process.WaitForExit());
            var timeout = Task.Delay(TimeSpan.FromMinutes(5));

            var completed = await Task.WhenAny(exitedTask, timeout).ConfigureAwait(false);
            if (completed != exitedTask)
            {
                try { process.Kill(true); } catch { }
                Log.Warn($"External command timed out: {fileName} {args}");
                return false;
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            _lastExitCode = process.ExitCode;
            if (_lastExitCode != 0)
            {
                Log.Warn($"External command failed: {fileName} {args} ExitCode={_lastExitCode} StdErr={errorTask.Result.Trim()} StdOut={outputTask.Result.Trim()}");
            }

            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"FFmpeg command failed to start: {fileName} {args}", ex);
            return false;
        }
    }

    /// <summary>
    /// Executes a command and captures its standard output. Returns the captured output (may be empty) or null on error.
    /// </summary>
    private async Task<string?> ExecuteCommandCaptureAsync(string fileName, string args)
    {
        try
        {
            Log.Debug($"Executing command capture: {fileName} {args}");
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            Log.Debug($"Command capture finished: {fileName} {args} ExitCode={process.ExitCode}");
            if (!string.IsNullOrEmpty(output))
                Log.Debug($"Command output: {output.Trim()}");
            if (!string.IsNullOrEmpty(error))
                Log.Debug($"Command error output: {error.Trim()}");

            if (!string.IsNullOrEmpty(error) && string.IsNullOrEmpty(output))
            {
                return error;
            }

            return output;
        }
        catch (Exception ex)
        {
            Log.Error($"FFmpeg command capture failed: {fileName} {args}", ex);
            return null;
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
}
