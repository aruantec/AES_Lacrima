using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AES_Controls.Helpers;

/// <summary>
/// Manages detection and installation of FFmpeg for the running platform.
/// Exposes a simple status and busy indicator and provides an async method
/// to ensure FFmpeg is installed (using platform-specific installers).
/// </summary>
public partial class FFmpegManager : ObservableObject
{
    /// <summary>
    /// Human readable status text for display in the UI.
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private string _status = "Idle";

    /// <summary>
    /// Indicates an ongoing operation (installation in progress).
    /// This backing field is populated by the <see cref="ObservablePropertyAttribute"/> source generator.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Ensures that FFmpeg is available on the system. If FFmpeg cannot be
    /// found in PATH, this method will attempt to run a platform-specific
    /// installer and update <see cref="Status"/> and <see cref="IsBusy"/> accordingly.
    /// </summary>
    /// <returns>A task that completes when the check/installation finishes.</returns>
    public async Task EnsureFFmpegInstalledAsync()
    {
        if (IsFFmpegAvailable())
        {
            Status = "FFmpeg is already installed.";
            return;
        }

        IsBusy = true;
        Status = "FFmpeg not found. Starting installation...";

        bool success = await RunPlatformInstaller();

        Status = success ? "FFmpeg installation successful." : "FFmpeg installation failed.";
        IsBusy = false;
    }

    /// <summary>
    /// Checks if FFmpeg is accessible via the system PATH.
    /// </summary>
    /// <summary>
    /// Checks whether FFmpeg is accessible via the system PATH by attempting
    /// to start the process with the <c>-version</c> argument.
    /// </summary>
    /// <returns><c>true</c> when FFmpeg appears available; otherwise <c>false</c>.</returns>
    private bool IsFFmpegAvailable()
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
            // If we can start the process, ffmpeg exists in PATH
            return true;
        }
        catch
        {
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
            // Using -e (exact) and gyan builds which are the standard for Windows
            return await ExecuteCommandAsync("winget", "install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await ExecuteCommandAsync("brew", "install ffmpeg");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Requires sudo; user will likely see a password prompt in the terminal
            return await ExecuteCommandAsync("sudo", "apt-get install -y ffmpeg");
        }

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
    private Task<bool> ExecuteCommandAsync(string fileName, string args)
    {
        var tcs = new TaskCompletionSource<bool>();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            // UseShellExecute is TRUE so that winget/brew/sudo can 
            // open their own window for license agreements or passwords.
            UseShellExecute = true,
            CreateNoWindow = false
        };

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => tcs.SetResult(process.ExitCode == 0);
            process.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Installer error: {ex.Message}");
            tcs.SetResult(false);
        }

        return tcs.Task;
    }
}