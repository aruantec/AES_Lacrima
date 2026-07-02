using AES_Controls.Helpers.Windows;
using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using log4net;
using AES_Core.Logging;

namespace AES_Controls.Helpers;

/// <summary>
/// Detects and installs the Virtual Display Driver used for Windows emulator capture,
/// similar to gamescope on Linux.
/// </summary>
[AutoRegister]
public partial class VirtualDisplayDriverManager : ObservableObject
{
    private static readonly ILog Log = LogHelper.For<VirtualDisplayDriverManager>();

    public const string WingetPackageId = "VirtualDrivers.Virtual-Display-Driver";
    public const string DefaultSettingsPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    public const string ProjectUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver";

    public const string CaptureRequiredUserMessage =
        "The Virtual Display Driver is required for reliable game capture on Windows (the same role gamescope plays on Linux). " +
        "Install it from Settings → Tools to enable fullscreen emulator and Steam game capture.";

    public const string InstallRequiresAdminMessage =
        "One click installs everything AES needs. Windows will ask for administrator approval (UAC) once or twice — no manual downloads, folders, or control apps.";

    public const string PackageWithoutKernelMessage =
        "Finishing Virtual Display Driver setup...";

    private static readonly Regex VersionRegex = new(@"\d+(?:\.\d+)+", RegexOptions.CultureInvariant);
    private readonly VddPipeClient _pipeClient = new();
    private int _lastExitCode;

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private bool _isBusy;

    public event EventHandler<InstallationCompletedEventArgs>? InstallationCompleted;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsInstalled => IsDriverActive();

    public static bool IsDriverActive()
    {
        if (!IsSupported || !IsKernelDriverPresent())
            return false;

        try
        {
            return new VddPipeClient(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2))
                .PingAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Log.Debug("Virtual Display Driver ping failed.", ex);
            return false;
        }
    }

    public static bool IsKernelDriverPresent() =>
        OperatingSystem.IsWindows() && VirtualDisplayKernelInstaller.IsKernelDriverPresent();

    public static bool IsKernelDriverHealthy() =>
        OperatingSystem.IsWindows() && VirtualDisplayKernelInstaller.IsKernelDriverHealthy();

    public bool IsAvailable() => IsDriverActive();

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
        await _pipeClient.PingAsync(cancellationToken).ConfigureAwait(false);

    public async Task<bool> InstallKernelDriverAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        Status = "Registering the Virtual Display Driver with Windows (admin approval may be required)...";

        var (success, message) = await VirtualDisplayKernelInstaller
            .InstallKernelDriverAsync(cancellationToken)
            .ConfigureAwait(false);

        if (success)
            success = await PingAsync(cancellationToken).ConfigureAwait(false);

        Status = success
            ? "Virtual Display Driver is ready for capture."
            : message;

        IsBusy = false;
        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(success, Status));
        return success;
    }

    public async Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        return await InstallAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            Status = "Virtual Display Driver is only supported on Windows.";
            return false;
        }

        if (IsDriverActive())
        {
            Status = "Virtual Display Driver is ready for capture.";
            return true;
        }

        Log.Info("Virtual Display Driver install requested.");
        IsBusy = true;

        try
        {
            var packagePresent = await IsPackageInstalledAsync().ConfigureAwait(false);
            if (!packagePresent)
            {
                Status = "Step 1/2: Downloading Virtual Display Driver via winget...";
                packagePresent = await ExecuteElevatedCommandAsync(
                    "winget",
                    $"install --id {WingetPackageId} -e --silent --accept-source-agreements --accept-package-agreements")
                    .ConfigureAwait(false);

                if (packagePresent)
                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }

            if (!packagePresent)
            {
                Status = $"Virtual Display Driver download failed (exit code: {_lastExitCode}). Check your internet connection and retry.";
                return false;
            }

            if (await PingAsync(cancellationToken).ConfigureAwait(false))
            {
                Status = "Virtual Display Driver is ready for capture.";
                return true;
            }

            if (!IsKernelDriverPresent())
            {
                Status = "Step 2/2: Registering the Virtual Display Driver with Windows...";
                var (kernelInstalled, kernelMessage) = await VirtualDisplayKernelInstaller
                    .InstallKernelDriverAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (!kernelInstalled)
                {
                    Status = kernelMessage;
                    return false;
                }
            }

            if (await PingAsync(cancellationToken).ConfigureAwait(false))
            {
                Status = "Virtual Display Driver is ready for capture.";
                return true;
            }

            if (IsKernelDriverPresent() && !IsKernelDriverHealthy())
            {
                Status = "Virtual Display Driver failed to start. Trying to restart it (admin approval may be required)...";
                var (restarted, restartMessage) = await VirtualDisplayKernelInstaller
                    .TryRestartDriverDevicesAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (restarted && await PingAsync(cancellationToken).ConfigureAwait(false))
                {
                    Status = "Virtual Display Driver is ready for capture.";
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(restartMessage))
                    Status = restartMessage;
            }

            if (await PingAsync(cancellationToken).ConfigureAwait(false))
            {
                Status = "Virtual Display Driver is ready for capture.";
                return true;
            }

            Status = IsKernelDriverPresent()
                ? VirtualDisplayKernelInstaller.GetDriverProblemUserMessage()
                  ?? "Virtual Display Driver is installed, but capture is not responding yet. Reboot once, then click Install again."
                : "Virtual Display Driver setup did not finish. Reboot once, then click Install again.";

            return false;
        }
        finally
        {
            IsBusy = false;
            InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(IsDriverActive(), Status));
        }
    }

    public async Task<bool> UpgradeAsync()
    {
        if (!IsSupported)
            return false;

        Log.Info("Virtual Display Driver upgrade requested.");
        IsBusy = true;
        Status = "Starting Virtual Display Driver upgrade...";

        var result = await ExecuteElevatedCommandAsync(
            "winget",
            $"upgrade --id {WingetPackageId} -e --silent --accept-source-agreements --accept-package-agreements")
            .ConfigureAwait(false);

        if (result && !IsDriverActive())
            result = await InstallAsync().ConfigureAwait(false);

        IsBusy = false;
        Status = result
            ? "Virtual Display Driver upgrade completed."
            : $"Virtual Display Driver upgrade failed (exit code: {_lastExitCode}).";
        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(result, Status));
        return result;
    }

    public async Task<bool> UninstallAsync()
    {
        if (!IsSupported)
            return false;

        Log.Info("Virtual Display Driver uninstall requested.");
        IsBusy = true;
        Status = "Uninstalling Virtual Display Driver...";

        var result = await ExecuteElevatedCommandAsync(
            "winget",
            $"uninstall --id {WingetPackageId} -e --silent")
            .ConfigureAwait(false);

        IsBusy = false;
        Status = result
            ? "Virtual Display Driver uninstalled. Reboot is recommended."
            : $"Virtual Display Driver uninstall failed (exit code: {_lastExitCode}).";
        InstallationCompleted?.Invoke(this, new InstallationCompletedEventArgs(result, Status));
        return result;
    }

    public async Task<string?> GetCurrentVersionAsync()
    {
        var output = await ExecuteCommandCaptureAsync("winget", $"list --id {WingetPackageId} -e").ConfigureAwait(false);
        return ExtractVersionFromText(output ?? string.Empty);
    }

    public async Task<CheckUpdateResult?> CheckForUpdateDetailsAsync()
    {
        try
        {
            var json = await ExecuteCommandCaptureAsync(
                "winget",
                $"upgrade --id {WingetPackageId} -e --output json")
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith('['))
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Name.Contains("available", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(prop.Name, "AvailableVersion", StringComparison.OrdinalIgnoreCase))
                        {
                            var version = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(version))
                                return new CheckUpdateResult(true, version, json);
                        }
                    }
                }
            }

            var output = await ExecuteCommandCaptureAsync("winget", $"upgrade --id {WingetPackageId} -e").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
                return new CheckUpdateResult(false, null, output);

            if (output.Contains("No applicable upgrade", StringComparison.OrdinalIgnoreCase))
                return new CheckUpdateResult(false, null, output);

            var versionFromText = ExtractVersionFromText(output);
            return new CheckUpdateResult(!string.IsNullOrWhiteSpace(versionFromText), versionFromText, output);
        }
        catch (Exception ex)
        {
            Log.Error("Virtual Display Driver update check failed.", ex);
            return new CheckUpdateResult(false, null, ex.Message);
        }
    }

    public async Task<bool> IsPackageInstalledAsync()
    {
        var output = await ExecuteCommandCaptureAsync("winget", $"list --id {WingetPackageId} -e").ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(output) &&
               output.Contains(WingetPackageId, StringComparison.OrdinalIgnoreCase);
    }

    public static int TryReadConfiguredDisplayCount(string? settingsPath = null)
    {
        settingsPath ??= DefaultSettingsPath;
        try
        {
            if (!File.Exists(settingsPath))
                return 0;

            var document = XDocument.Load(settingsPath);
            var countText = document
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "count", StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(element.Parent?.Name.LocalName, "monitors", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            return int.TryParse(countText, out var count) ? Math.Max(0, count) : 0;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to read monitor count from '{settingsPath}'.", ex);
            return 0;
        }
    }

    public Task<bool> SetDisplayCountAsync(int count, CancellationToken cancellationToken = default) =>
        _pipeClient.SetDisplayCountAsync(count, cancellationToken);

    private static string? ExtractVersionFromText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = VersionRegex.Match(input);
        return match.Success ? match.Value : null;
    }

    private async Task<bool> ExecuteElevatedCommandAsync(string fileName, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas",
        };

        return await ExecuteProcessAsync(startInfo, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
    }

    private async Task<string?> ExecuteCommandCaptureAsync(string fileName, string args)
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

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (Exception ex)
        {
            Log.Debug($"Command capture failed: {fileName} {args}", ex);
            return null;
        }
    }

    private async Task<bool> ExecuteProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        try
        {
            Log.Info($"Starting external command: {startInfo.FileName} {startInfo.Arguments}");
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                Log.Warn($"Failed to start external command: {startInfo.FileName} {startInfo.Arguments}");
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitedTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(exitedTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != exitedTask)
            {
                try { process.Kill(true); } catch (Exception ex) { Log.Warn("Failed to kill timed-out process.", ex); }
                Log.Warn($"External command timed out: {startInfo.FileName} {startInfo.Arguments}");
                return false;
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            _lastExitCode = process.ExitCode;
            if (_lastExitCode != 0)
            {
                Log.Warn(
                    $"External command failed: {startInfo.FileName} {startInfo.Arguments} " +
                    $"ExitCode={_lastExitCode} StdErr={errorTask.Result.Trim()} StdOut={outputTask.Result.Trim()}");
            }

            return _lastExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"External command failed to start: {startInfo.FileName} {startInfo.Arguments}", ex);
            return false;
        }
    }

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
}
