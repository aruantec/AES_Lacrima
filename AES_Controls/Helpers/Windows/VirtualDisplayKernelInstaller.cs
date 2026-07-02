using AES_Core.IO;
using AES_Core.Logging;
using log4net;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Installs the Virtual Display Driver kernel component. Winget only deploys the control app
/// and signed driver files; Lacrima registers the display adapter automatically.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VirtualDisplayKernelInstaller
{
    private static readonly ILog Log = LogHelper.For(typeof(VirtualDisplayKernelInstaller));
    private const string NefConDownloadUrl =
        "https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip";
    private const string DriverOnlyZipUrl =
        "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.26/VirtualDisplayDriver-x86.Driver.Only.zip";

    public static bool IsKernelDriverPresent()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return TryQueryDriverDevices().Count > 0;
    }

    public static bool IsKernelDriverHealthy()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return TryQueryDriverDevices().Any(device =>
            string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetDriverProblemUserMessage()
    {
        var devices = TryQueryDriverDevices();
        if (devices.Count == 0)
            return null;

        if (devices.Any(device => string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase)))
            return null;

        var errorDevice = devices.FirstOrDefault(device =>
            string.Equals(device.Status, "Error", StringComparison.OrdinalIgnoreCase));

        if (errorDevice == null)
        {
            return "Virtual Display Driver is registered in Windows, but it is not running yet. " +
                   "Reboot once, then click Refresh Driver Info in Settings → Tools.";
        }

        return errorDevice.Problem switch
        {
            "CM_PROB_FAILED_POST_START" =>
                "Virtual Display Driver failed to start (Device Manager shows Error). " +
                "Reboot your PC once. If it still fails, open Device Manager → Display adapters, " +
                "disable and re-enable Virtual Display Driver, or click Install again in Settings → Tools.",
            "CM_PROB_DISABLED" =>
                "Virtual Display Driver is disabled in Device Manager. Enable it under Display adapters, then refresh.",
            _ =>
                $"Virtual Display Driver is registered but not working ({errorDevice.Problem}). " +
                "Reboot once, then refresh. If it persists, reinstall from Settings → Tools.",
        };
    }

    public static async Task<(bool Success, string Message)> TryRestartDriverDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Virtual Display Driver is only supported on Windows.");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-vdd-restart-{Guid.NewGuid():N}.ps1");
        var content = """
            $ErrorActionPreference = 'Stop'
            $devices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -match 'Virtual Display|IddSample|MttVDD' }
            if (-not $devices) { exit 2 }
            foreach ($device in $devices) {
                if ($device.Status -eq 'Error' -or $device.Status -eq 'Degraded') {
                    Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
                }
            }
            Start-Sleep -Seconds 2
            foreach ($device in $devices) {
                Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds 3
            $healthy = (Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -match 'Virtual Display|IddSample|MttVDD' -and $_.Status -eq 'OK' }).Count
            if ($healthy -le 0) { exit 1 }
            """;

        try
        {
            File.WriteAllText(scriptPath, content);
            var success = await ExecuteElevatedProcessAsync(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    Path.GetTempPath(),
                    cancellationToken)
                .ConfigureAwait(false);

            try { File.Delete(scriptPath); } catch { /* best effort */ }

            if (!success)
            {
                return (false,
                    GetDriverProblemUserMessage() ??
                    "Virtual Display Driver could not be restarted. Reboot once, then try Install again.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return IsKernelDriverHealthy()
                ? (true, "Virtual Display Driver restarted successfully.")
                : (false, GetDriverProblemUserMessage() ?? "Virtual Display Driver restart did not recover the device.");
        }
        catch (Exception ex)
        {
            Log.Error("Virtual Display Driver restart failed.", ex);
            return (false, $"Driver restart failed: {ex.Message}");
        }
    }

    private static IReadOnlyList<VirtualDisplayDriverDeviceInfo> TryQueryDriverDevices()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<VirtualDisplayDriverDeviceInfo>();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -Command " +
                    "\"Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.FriendlyName -match 'Virtual Display|IddSample|MttVDD' } | " +
                    "Select-Object FriendlyName, Status, Problem, ConfigManagerErrorCode | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return Array.Empty<VirtualDisplayDriverDeviceInfo>();

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (string.IsNullOrWhiteSpace(output))
                return Array.Empty<VirtualDisplayDriverDeviceInfo>();

            if (output.StartsWith("[", StringComparison.Ordinal))
            {
                var devices = System.Text.Json.JsonSerializer
                    .Deserialize<List<VirtualDisplayDriverDeviceInfo>>(output);
                return devices == null
                    ? Array.Empty<VirtualDisplayDriverDeviceInfo>()
                    : devices;
            }

            var single = System.Text.Json.JsonSerializer.Deserialize<VirtualDisplayDriverDeviceInfo>(output);
            return single == null
                ? Array.Empty<VirtualDisplayDriverDeviceInfo>()
                : new[] { single };
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to query Virtual Display Driver PnP devices.", ex);
            return Array.Empty<VirtualDisplayDriverDeviceInfo>();
        }
    }

    private sealed class VirtualDisplayDriverDeviceInfo
    {
        public string? FriendlyName { get; set; }
        public string? Status { get; set; }
        public string? Problem { get; set; }
        public int ConfigManagerErrorCode { get; set; }
    }

    public static string? TryResolveWingetPackageDirectory()
    {
        try
        {
            var packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Packages");

            if (!Directory.Exists(packagesRoot))
                return null;

            return Directory
                .GetDirectories(packagesRoot, "VirtualDrivers.Virtual-Display-Driver_*")
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve Virtual Display Driver winget package directory.", ex);
            return null;
        }
    }

    public static string? TryResolveBundledDriverDirectory(string? packageDirectory = null)
    {
        packageDirectory ??= TryResolveWingetPackageDirectory();
        if (string.IsNullOrWhiteSpace(packageDirectory))
            return null;

        var candidates = new[]
        {
            Path.Combine(packageDirectory, "SignedDrivers", "x86", "VDD"),
            Path.Combine(packageDirectory, "SignedDrivers", "ARM64", "VDD"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "MttVDD.inf")))
                return candidate;
        }

        return null;
    }

    public static string? TryResolveBundledDevconPath(string? packageDirectory = null)
    {
        packageDirectory ??= TryResolveWingetPackageDirectory();
        if (string.IsNullOrWhiteSpace(packageDirectory))
            return null;

        var devconPath = Path.Combine(packageDirectory, "Dependencies", "devcon.exe");
        return File.Exists(devconPath) ? devconPath : null;
    }

    public static async Task<string?> EnsureDriverPayloadDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var bundled = TryResolveBundledDriverDirectory();
        if (!string.IsNullOrWhiteSpace(bundled))
            return bundled;

        var toolsDirectory = Path.Combine(ApplicationPaths.ToolsDirectory, "virtual-display-driver");
        Directory.CreateDirectory(toolsDirectory);

        var driverDirectory = Path.Combine(toolsDirectory, "VirtualDisplayDriver");
        if (File.Exists(Path.Combine(driverDirectory, "MttVDD.inf")))
            return driverDirectory;

        var zipPath = Path.Combine(toolsDirectory, "driver.zip");
        try
        {
            Log.Info("Downloading Virtual Display Driver payload for kernel installation.");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using var stream = await client
                .GetStreamAsync(DriverOnlyZipUrl, cancellationToken)
                .ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(driverDirectory))
                Directory.Delete(driverDirectory, recursive: true);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolsDirectory, overwriteFiles: true);
            return File.Exists(Path.Combine(driverDirectory, "MttVDD.inf")) ? driverDirectory : null;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to download Virtual Display Driver payload.", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    public static async Task<(bool Success, string Message)> InstallKernelDriverAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Virtual Display Driver is only supported on Windows.");

        var driverDirectory = await EnsureDriverPayloadDirectoryAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(driverDirectory))
        {
            return (false,
                "AES could not download the Virtual Display Driver files. Check your internet connection and retry.");
        }

        try
        {
            TrySeedSettingsDirectory(driverDirectory);

            var infPath = Path.Combine(driverDirectory, "MttVDD.inf");
            var catalogPath = Path.Combine(driverDirectory, "mttvdd.cat");
            var devconPath = TryResolveBundledDevconPath();
            var nefconPath = await EnsureNefConAsync(cancellationToken).ConfigureAwait(false);
            var scriptPath = WriteInstallScript(devconPath, nefconPath, infPath, catalogPath, driverDirectory);

            Log.Info($"Installing Virtual Display Driver kernel component via elevated script: {infPath}");
            var success = await ExecuteElevatedProcessAsync(
                    "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    driverDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            try { File.Delete(scriptPath); } catch { /* best effort */ }

            if (!success)
            {
                return (false,
                    "Driver installation was cancelled or failed. Approve the Windows administrator (UAC) prompt and retry.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            if (!IsKernelDriverPresent())
            {
                return (false,
                    "AES installed the driver package, but Windows has not activated it yet. Reboot once, then click Install again.");
            }

            return (true, "Virtual Display Driver is ready for capture.");
        }
        catch (Exception ex)
        {
            Log.Error("Virtual Display Driver kernel installation failed.", ex);
            return (false, $"Driver installation failed: {ex.Message}");
        }
    }

    private static void TrySeedSettingsDirectory(string driverDirectory)
    {
        try
        {
            const string targetDirectory = @"C:\VirtualDisplayDriver";
            Directory.CreateDirectory(targetDirectory);

            var bundledSettings = Path.Combine(driverDirectory, "vdd_settings.xml");
            var targetSettings = Path.Combine(targetDirectory, "vdd_settings.xml");
            if (File.Exists(bundledSettings) && !File.Exists(targetSettings))
                File.Copy(bundledSettings, targetSettings);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to seed Virtual Display Driver settings directory.", ex);
        }
    }

    private static string WriteInstallScript(
        string? devconPath,
        string? nefconPath,
        string infPath,
        string catalogPath,
        string workingDirectory)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-vdd-install-{Guid.NewGuid():N}.ps1");
        var devconLiteral = string.IsNullOrWhiteSpace(devconPath) ? string.Empty : devconPath.Replace("'", "''");
        var nefconLiteral = string.IsNullOrWhiteSpace(nefconPath) ? string.Empty : nefconPath.Replace("'", "''");
        var content = $$"""
            $ErrorActionPreference = 'Stop'

            $catalogPath = '{{catalogPath.Replace("'", "''")}}'
            if (Test-Path -LiteralPath $catalogPath) {
                $certificates = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
                $certificates.Import($catalogPath)
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine')
                $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                foreach ($cert in $certificates) { $store.Add($cert) }
                $store.Close()
            }

            Set-Location -LiteralPath '{{workingDirectory.Replace("'", "''")}}'
            $infPath = '{{infPath.Replace("'", "''")}}'
            $installed = $false

            if ('{{devconLiteral}}' -ne '') {
                & '{{devconLiteral}}' install $infPath 'Root\MttVDD'
                if ($LASTEXITCODE -eq 0) { $installed = $true }
            }

            if (-not $installed -and '{{nefconLiteral}}' -ne '') {
                & '{{nefconLiteral}}' install $infPath 'Root\MttVDD'
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
            } elseif (-not $installed) {
                throw 'No driver installer tool was available.'
            }
            """;

        File.WriteAllText(scriptPath, content);
        return scriptPath;
    }

    private static async Task<string?> EnsureNefConAsync(CancellationToken cancellationToken)
    {
        var toolsDirectory = Path.Combine(ApplicationPaths.ToolsDirectory, "nefcon");
        Directory.CreateDirectory(toolsDirectory);

        var nefconPath = Path.Combine(toolsDirectory, "x64", "nefconw.exe");
        if (File.Exists(nefconPath))
            return nefconPath;

        var zipPath = Path.Combine(toolsDirectory, "nefcon.zip");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using var stream = await client
                .GetStreamAsync(NefConDownloadUrl, cancellationToken)
                .ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolsDirectory, overwriteFiles: true);
            return File.Exists(nefconPath) ? nefconPath : null;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to download NefCon.", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    private static async Task<bool> ExecuteElevatedProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Verb = "runas",
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
                Log.Info($"Virtual Display Driver install output: {output.Trim()}");
            if (!string.IsNullOrWhiteSpace(error))
                Log.Warn($"Virtual Display Driver install error output: {error.Trim()}");

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start elevated process: {fileName} {arguments}", ex);
            return false;
        }
    }
}
