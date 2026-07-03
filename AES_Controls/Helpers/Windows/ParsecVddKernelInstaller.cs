using AES_Core.IO;
using System.Diagnostics;
using System.Runtime.Versioning;
using log4net;
using AES_Core.Logging;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Installs and removes the Parsec Virtual Display Driver kernel package.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ParsecVddKernelInstaller
{
    private static readonly ILog Log = LogHelper.For(typeof(ParsecVddKernelInstaller));

    public static string DriverBundleDirectory =>
        Path.Combine(ApplicationPaths.ToolsDirectory, "parsec-vdd");

    public static string DriverInfPath =>
        Path.Combine(DriverBundleDirectory, "driver", "mm.inf");

    public static string NefconPath =>
        Path.Combine(DriverBundleDirectory, "nefconw.exe");

    public const string SystemInstallDirectoryName = "Parsec Virtual Display Driver";

    public static bool IsKernelDriverPresent() =>
        ParsecVddNative.QueryDeviceStatus() is not ParsecVddNative.DeviceStatus.NotInstalled
            and not ParsecVddNative.DeviceStatus.Inaccessible;

    public static bool IsKernelDriverHealthy() =>
        ParsecVddNative.QueryDeviceStatus() == ParsecVddNative.DeviceStatus.Ok;

    public static bool HasDriverPayload() =>
        HasDriverRegistrationPayload(DriverBundleDirectory);

    public static bool HasExtractedDriverFiles() =>
        FindDriverRegistrationDirectory() != null;

    public static bool HasDriverRegistrationPayload(string directory) =>
        File.Exists(Path.Combine(directory, "nefconw.exe")) &&
        File.Exists(Path.Combine(directory, "driver", "mm.inf"));

    public static string? FindSystemInstallDirectory()
    {
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in programFiles.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            var candidate = Path.Combine(root, SystemInstallDirectoryName);
            if (HasDriverRegistrationPayload(candidate))
                return candidate;
        }

        return null;
    }

    public static string? FindDriverRegistrationDirectory()
    {
        if (FindSystemInstallDirectory() is { } systemDirectory)
            return systemDirectory;

        return HasDriverPayload() ? DriverBundleDirectory : null;
    }

    /// <summary>
    /// Registers the Parsec kernel driver after the setup wizard has extracted files.
    /// </summary>
    public static async Task<bool> RegisterKernelDriverAsync(CancellationToken cancellationToken = default)
    {
        var installDirectory = ResolveRegistrationDirectory();
        if (installDirectory == null)
        {
            throw new FileNotFoundException(
                "Parsec driver files were not found. Run the setup wizard first, then click Register Driver.");
        }

        Log.Info($"Registering Parsec VDD via nefconw in {installDirectory}");
        var registered = await RegisterWithNefconAsync(installDirectory, cancellationToken).ConfigureAwait(false);
        if (!registered)
            return false;

        return await WaitForDriverActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveRegistrationDirectory()
    {
        if (FindDriverRegistrationDirectory() is { } existingDirectory)
            return existingDirectory;

        try
        {
            EnsureDriverPayloadExtracted();
            return HasDriverPayload() ? DriverBundleDirectory : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not prepare Parsec driver payload for registration.", ex);
            return null;
        }
    }

    private static async Task<bool> RegisterWithNefconAsync(string installDirectory, CancellationToken cancellationToken)
    {
        var nefconPath = Path.Combine(installDirectory, "nefconw.exe");
        var infPath = Path.Combine(installDirectory, "driver", "mm.inf");
        if (!File.Exists(nefconPath) || !File.Exists(infPath))
        {
            Log.Warn($"Parsec registration payload missing in {installDirectory}.");
            return false;
        }

        var classGuid = ParsecVddNative.ClassGuid.ToString("D").ToUpperInvariant();
        var tempScriptPath = Path.Combine(Path.GetTempPath(), $"aes-parsec-vdd-{Guid.NewGuid():N}.cmd");
        var script = $"""
            @echo off
            echo Installing Parsec Virtual Display Driver...
            cd /d "{installDirectory}"
            echo [1/3] Removing any existing Parsec device node...
            start /wait "" "{nefconPath}" --remove-device-node --hardware-id {ParsecVddNative.HardwareId} --class-guid "{classGuid}"
            if errorlevel 1 echo Previous device node was not present.
            echo [2/3] Creating Parsec display device node...
            start /wait "" "{nefconPath}" --create-device-node --class-name Display --class-guid "{classGuid}" --hardware-id {ParsecVddNative.HardwareId}
            if errorlevel 1 exit /b 1
            echo [3/3] Installing Parsec driver...
            start /wait "" "{nefconPath}" --install-driver --inf-path "{infPath}"
            if errorlevel 1 exit /b 1
            echo Parsec driver registration finished successfully.
            exit /b 0
            """;

        try
        {
            await File.WriteAllTextAsync(tempScriptPath, script, cancellationToken).ConfigureAwait(false);
            var exitCode = await RunElevatedAndWaitForExitCodeAsync(
                    "cmd.exe",
                    installDirectory,
                    $"/c \"\"{tempScriptPath}\"\"",
                    cancellationToken)
                .ConfigureAwait(false);

            if (exitCode == 1223)
            {
                Log.Warn("Parsec driver registration cancelled at UAC prompt.");
                return false;
            }

            if (exitCode != 0)
            {
                Log.Warn($"Parsec driver registration script failed with exit code {exitCode}.");
                return false;
            }

            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(tempScriptPath))
                    File.Delete(tempScriptPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete temporary Parsec registration script '{tempScriptPath}'.", ex);
            }
        }
    }

    private static async Task<bool> WaitForDriverActiveAsync(CancellationToken cancellationToken, int timeoutMs = 30000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsKernelDriverHealthy() || ParsecVddNative.TryQuickProbe())
                return true;

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return IsKernelDriverHealthy() || ParsecVddNative.TryQuickProbe();
    }

    private static Task<int> RunElevatedAndWaitForExitCodeAsync(
        string fileName,
        string workingDirectory,
        string? arguments,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Log.Info($"Launching elevated process: {fileName} {arguments} (cwd={workingDirectory})");

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    Log.Warn($"Elevated process did not start: {fileName}");
                    return -1;
                }

                if (!process.WaitForExit(180_000))
                {
                    try { process.Kill(); }
                    catch (Exception ex) { Log.Debug("Failed to kill timed-out Parsec registration process.", ex); }
                    Log.Warn($"Elevated process timed out: {fileName}");
                    return -1;
                }

                Log.Info($"Elevated process exited with code {process.ExitCode}: {fileName}");
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log.Warn("Parsec driver registration cancelled at UAC prompt.", ex);
                return 1223;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to run elevated Parsec registration process '{fileName}'.", ex);
                return -1;
            }
        }, cancellationToken);
    }

    public static string? FindDriverInstallerExe(string? searchDirectory = null)
    {
        searchDirectory ??= DriverBundleDirectory;
        if (!Directory.Exists(searchDirectory))
            return null;

        return Directory
            .EnumerateFiles(searchDirectory, "parsec-vdd-*.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string? FindParsecDisplayExe(string? searchDirectory = null)
    {
        searchDirectory ??= DriverBundleDirectory;
        if (!Directory.Exists(searchDirectory))
            return null;

        return Directory
            .EnumerateFiles(searchDirectory, "ParsecVDisplay.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    /// <summary>
    /// Opens the official Parsec VDD setup wizard so the user can install manually.
    /// </summary>
    public static void LaunchDriverInstallerUi()
    {
        var installerExe = FindDriverInstallerExe()
                           ?? throw new FileNotFoundException(
                               "Parsec VDD installer not found. Click Install again to re-download the driver package.");

        var workingDirectory = Path.GetDirectoryName(installerExe)
                               ?? DriverBundleDirectory;

        Log.Info($"Launching Parsec VDD installer UI: {installerExe}");

        if (TryStartProcess(installerExe, workingDirectory, useRunAs: true))
            return;

        if (TryStartProcess(installerExe, workingDirectory, useRunAs: false))
            return;

        throw new InvalidOperationException(
            "Could not open the Parsec driver installer. Try opening the driver folder and running parsec-vdd-*.exe manually.");
    }

    public static void LaunchParsecDisplayApp()
    {
        var displayExe = FindParsecDisplayExe()
                         ?? throw new FileNotFoundException(
                             "ParsecVDisplay.exe was not found. Click Install again to re-download the package.");

        var workingDirectory = Path.GetDirectoryName(displayExe) ?? DriverBundleDirectory;
        if (!TryStartProcess(displayExe, workingDirectory, useRunAs: false))
        {
            throw new InvalidOperationException(
                "Could not open ParsecVDisplay.exe. Try opening the driver folder and running it manually.");
        }
    }

    public static void OpenDriverBundleDirectory()
    {
        Directory.CreateDirectory(DriverBundleDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = DriverBundleDirectory,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Unpack nefconw.exe and driver/mm.inf from the NSIS installer (used for uninstall helpers).
    /// </summary>
    public static void EnsureDriverPayloadExtracted()
    {
        if (HasDriverPayload())
            return;

        var installerExe = FindDriverInstallerExe();
        if (installerExe == null)
            throw new FileNotFoundException(
                "Parsec VDD driver installer was not found in the downloaded package. Re-download from Settings.");

        Directory.CreateDirectory(DriverBundleDirectory);
        var payloadDirectory = Path.Combine(DriverBundleDirectory, "_payload");
        if (Directory.Exists(payloadDirectory))
            Directory.Delete(payloadDirectory, true);
        Directory.CreateDirectory(payloadDirectory);

        if (!ArchiveExtractionHelper.TryExtractWithSystemTool(installerExe, payloadDirectory))
        {
            throw new InvalidOperationException(
                "Could not unpack the Parsec VDD driver installer. Install 7-Zip (7z.exe) or use the setup wizard instead.");
        }

        var discoveredInf = Directory.GetFiles(payloadDirectory, "mm.inf", SearchOption.AllDirectories).FirstOrDefault();
        var discoveredNefcon = Directory.GetFiles(payloadDirectory, "nefconw.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (discoveredInf == null || discoveredNefcon == null)
        {
            throw new FileNotFoundException(
                "Parsec VDD driver files were not found inside the installer package after extraction.");
        }

        var driverSourceDir = Path.GetDirectoryName(discoveredInf)!;
        var driverDestDir = Path.Combine(DriverBundleDirectory, "driver");
        Directory.CreateDirectory(driverDestDir);
        foreach (var file in Directory.GetFiles(driverSourceDir))
            File.Copy(file, Path.Combine(driverDestDir, Path.GetFileName(file)), true);

        File.Copy(discoveredNefcon, NefconPath, true);

        try { Directory.Delete(payloadDirectory, true); }
        catch (Exception ex) { Log.Debug("Failed to delete temporary Parsec VDD payload directory.", ex); }

        if (!HasDriverPayload())
            throw new FileNotFoundException("Parsec VDD driver payload extraction completed but required files are still missing.");
    }

    public static Task<bool> UninstallDriverAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(NefconPath))
        {
            var classGuid = ParsecVddNative.ClassGuid.ToString("D").ToUpperInvariant();
            var args = $"--remove-device-node --hardware-id {ParsecVddNative.HardwareId} --class-guid {classGuid}";
            if (TryStartProcess(NefconPath, DriverBundleDirectory, useRunAs: true, arguments: args))
                return Task.FromResult(true);
        }

        LaunchDriverInstallerUi();
        return Task.FromResult(false);
    }

    private static bool TryStartProcess(
        string fileName,
        string workingDirectory,
        bool useRunAs,
        string? arguments = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            if (useRunAs)
                startInfo.Verb = "runas";

            return Process.Start(startInfo) != null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn($"Process launch cancelled at UAC prompt: {fileName}", ex);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to start process '{fileName}'.", ex);
            return false;
        }
    }
}
