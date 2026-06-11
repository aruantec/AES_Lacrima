using AES_Core.DI;
using AES_Core.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using log4net;

using AES_Core.Logging;

namespace AES_Controls.Helpers;

/// <summary>
/// Detects and manages the gamescope compositor used to launch Linux emulators.
/// </summary>
[AutoRegister]
public partial class GamescopeManager : ObservableObject
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<GamescopeManager>();
    private static string? _resolvedPathCache;
    private int _lastExitCode;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private bool _isBusy;

    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    public static bool IsSupported => OperatingSystem.IsLinux();

    public static bool IsInstalled => !string.IsNullOrWhiteSpace(ResolveExecutablePath());

    public static void InvalidateResolvedPathCache() => _resolvedPathCache = null;

    public static string? ResolveExecutablePath()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        if (!string.IsNullOrWhiteSpace(_resolvedPathCache) && File.Exists(_resolvedPathCache))
            return _resolvedPathCache;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "gamescope"),
            ApplicationPaths.GetToolFile("gamescope"),
            "/usr/bin/gamescope",
            "/usr/local/bin/gamescope",
            "/usr/games/gamescope",
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    _resolvedPathCache = candidate;
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to probe gamescope candidate path '{candidate}'.", ex);
            }
        }

        var fromPath = ResolveFromPath("gamescope");
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            _resolvedPathCache = fromPath;
            return fromPath;
        }

        _resolvedPathCache = null;
        return null;
    }

    public bool IsAvailable() => IsInstalled;

    public async Task<string?> GetCurrentVersionAsync()
    {
        var path = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var output = await ExecuteCommandCaptureAsync(path, "--version").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            output = await ExecuteCommandCaptureAsync(path, "-h").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output))
            return null;

        var firstLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return ExtractVersionFromText(firstLine ?? output);
    }

    public async Task<bool> EnsureInstalledAsync()
    {
        if (IsAvailable())
        {
            Status = "gamescope is already installed.";
            return true;
        }

        return await InstallAsync().ConfigureAwait(false);
    }

    public async Task<bool> InstallAsync()
    {
        if (!IsSupported)
        {
            Status = "gamescope is only available on Linux.";
            return false;
        }

        Log.Info("gamescope install requested.");
        IsBusy = true;
        Status = "gamescope not found. Starting installation...";

        var success = await RunLinuxInstallerAsync().ConfigureAwait(false);
        if (success)
            InvalidateResolvedPathCache();

        Status = success
            ? "gamescope installation successful."
            : $"gamescope installation failed (Exit code: {(_lastExitCode == 0 ? "Unknown" : _lastExitCode)}).";
        IsBusy = false;

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    public async Task<bool> UpgradeAsync()
    {
        if (!IsSupported)
        {
            Status = "gamescope is only available on Linux.";
            return false;
        }

        IsBusy = true;
        Status = "Starting gamescope upgrade...";
        var success = await RunLinuxUpgradeAsync().ConfigureAwait(false);
        if (success)
            InvalidateResolvedPathCache();

        Status = success
            ? "gamescope upgrade completed."
            : $"gamescope upgrade failed (Exit code: {(_lastExitCode == 0 ? "Unknown" : _lastExitCode)}).";
        IsBusy = false;

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    public async Task<bool> UninstallAsync()
    {
        if (!IsSupported)
        {
            Status = "gamescope is only available on Linux.";
            return false;
        }

        IsBusy = true;
        Status = "Uninstalling gamescope...";
        var success = await RunLinuxUninstallAsync().ConfigureAwait(false);
        if (success)
            InvalidateResolvedPathCache();

        Status = success
            ? "gamescope uninstalled."
            : $"gamescope uninstall failed (Exit code: {(_lastExitCode == 0 ? "Unknown" : _lastExitCode)}).";
        IsBusy = false;

        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    private static string? ResolveFromPath(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"command -v {executable}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve gamescope from PATH.", ex);
            return null;
        }
    }

    private async Task<bool> RunLinuxInstallerAsync()
    {
        if (CommandExists("pacman"))
            return await ExecuteCommandAsync("sudo", "pacman -S --needed --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await ExecuteCommandAsync("sudo", "apt-get install -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await ExecuteCommandAsync("sudo", "dnf install -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await ExecuteCommandAsync("sudo", "zypper install -y gamescope").ConfigureAwait(false);

        Log.Warn("No supported Linux package manager found for gamescope installation.");
        return false;
    }

    private async Task<bool> RunLinuxUpgradeAsync()
    {
        if (CommandExists("pacman"))
            return await ExecuteCommandAsync("sudo", "pacman -S --needed --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await ExecuteCommandAsync("sudo", "apt-get install -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await ExecuteCommandAsync("sudo", "dnf upgrade -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await ExecuteCommandAsync("sudo", "zypper update -y gamescope").ConfigureAwait(false);

        return false;
    }

    private async Task<bool> RunLinuxUninstallAsync()
    {
        if (CommandExists("pacman"))
            return await ExecuteCommandAsync("sudo", "pacman -R --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await ExecuteCommandAsync("sudo", "apt-get remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await ExecuteCommandAsync("sudo", "dnf remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await ExecuteCommandAsync("sudo", "zypper remove -y gamescope").ConfigureAwait(false);

        return false;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"command -v {command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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

    private async Task<bool> ExecuteCommandAsync(string fileName, string args)
    {
        try
        {
            Log.Info($"Starting external command: {fileName} {args}");
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync()).ConfigureAwait(false);
            _lastExitCode = process.ExitCode;
            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"External command failed: {fileName} {args}", ex);
            return false;
        }
    }

    private static async Task<string?> ExecuteCommandCaptureAsync(string fileName, string args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync()).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            var output = outputTask.Result;
            return string.IsNullOrWhiteSpace(output) ? errorTask.Result : output;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to capture output from {fileName} {args}.", ex);
            return null;
        }
    }

    private static string? ExtractVersionFromText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = Regex.Match(input, @"\d+(?:\.\d+)+");
        return match.Success ? match.Value : null;
    }
}
