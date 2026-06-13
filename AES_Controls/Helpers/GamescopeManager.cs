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
    private string? _lastCommandError;

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
            : BuildFailureStatus("gamescope installation failed");
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
            : BuildFailureStatus("gamescope upgrade failed");
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
            : BuildFailureStatus("gamescope uninstall failed");
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
            return await RunPrivilegedPackageCommandAsync("pacman -S --needed --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await RunPrivilegedPackageCommandAsync("apt-get install -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await RunPrivilegedPackageCommandAsync("dnf install -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await RunPrivilegedPackageCommandAsync("zypper install -y gamescope").ConfigureAwait(false);

        Log.Warn("No supported Linux package manager found for gamescope installation.");
        Status = "No supported Linux package manager found for gamescope installation.";
        return false;
    }

    private async Task<bool> RunLinuxUpgradeAsync()
    {
        if (CommandExists("pacman"))
            return await RunPrivilegedPackageCommandAsync("pacman -S --needed --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await RunPrivilegedPackageCommandAsync("apt-get install -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await RunPrivilegedPackageCommandAsync("dnf upgrade -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await RunPrivilegedPackageCommandAsync("zypper update -y gamescope").ConfigureAwait(false);

        Status = "No supported Linux package manager found for gamescope upgrade.";
        return false;
    }

    private async Task<bool> RunLinuxUninstallAsync()
    {
        if (CommandExists("pacman"))
            return await RunPrivilegedPackageCommandAsync("pacman -R --noconfirm gamescope").ConfigureAwait(false);

        if (CommandExists("apt-get"))
            return await RunPrivilegedPackageCommandAsync("apt-get remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("dnf"))
            return await RunPrivilegedPackageCommandAsync("dnf remove -y gamescope").ConfigureAwait(false);

        if (CommandExists("zypper"))
            return await RunPrivilegedPackageCommandAsync("zypper remove -y gamescope").ConfigureAwait(false);

        Status = "No supported Linux package manager found for gamescope uninstall.";
        return false;
    }

    private async Task<bool> RunPrivilegedPackageCommandAsync(string arguments)
    {
        var attempts = new (string Command, string Label)[]
        {
            ("pkexec", "pkexec"),
            ("sudo", "sudo"),
        };

        foreach (var (command, label) in attempts)
        {
            if (!CommandExists(command))
                continue;

            Status = $"Running gamescope package command via {label}...";
            Log.Info($"Trying gamescope package command via {label}: {arguments}");

            if (await ExecuteCommandAsync(command, arguments).ConfigureAwait(false))
                return true;
        }

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

            CopyPolkitEnvironment(startInfo);

            var usePkexec = string.Equals(fileName, "pkexec", StringComparison.Ordinal);
            if (usePkexec)
            {
                startInfo.RedirectStandardOutput = false;
                startInfo.RedirectStandardError = false;
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;

            Task<string> outputTask = Task.FromResult(string.Empty);
            Task<string> errorTask = Task.FromResult(string.Empty);
            if (!usePkexec)
            {
                outputTask = process.StandardOutput.ReadToEndAsync();
                errorTask = process.StandardError.ReadToEndAsync();
            }

            var exitedTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
            var completedTask = await Task.WhenAny(exitedTask, timeoutTask).ConfigureAwait(false);

            if (completedTask != exitedTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception killEx)
                {
                    Log.Debug("Failed to kill timed-out gamescope package command.", killEx);
                }

                _lastExitCode = -1;
                _lastCommandError = "Installation timed out after 5 minutes.";
                Log.Warn($"External command timed out: {fileName} {args}");
                return false;
            }

            if (!usePkexec)
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

            _lastExitCode = process.ExitCode;
            _lastCommandError = usePkexec
                ? (_lastExitCode == 0 ? null : "Authentication was cancelled or denied.")
                : errorTask.Result.Trim();
            if (_lastExitCode != 0)
            {
                Log.Warn(
                    $"External command failed: {fileName} {args} ExitCode={_lastExitCode} StdErr={_lastCommandError} StdOut={outputTask.Result.Trim()}");
            }

            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            _lastCommandError = ex.Message;
            Log.Error($"External command failed: {fileName} {args}", ex);
            return false;
        }
    }

    private static void CopyPolkitEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "DESKTOP_SESSION" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }
    }

    private string BuildFailureStatus(string prefix)
    {
        var exitCode = _lastExitCode == 0 ? "Unknown" : _lastExitCode.ToString();
        if (string.IsNullOrWhiteSpace(_lastCommandError))
            return $"{prefix} (exit code: {exitCode}).";

        var detail = _lastCommandError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return $"{prefix} (exit code: {exitCode}): {detail}";
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
