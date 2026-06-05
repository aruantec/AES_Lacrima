using AES_Core.IO;
using log4net;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal enum PendingUpdateTargetKind
{
    DirectoryContents,
    MacBundle,
    LinuxAppImage
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(PendingUpdateManifest))]
internal partial class PendingUpdateJsonContext : JsonSerializerContext
{
}

internal sealed record PendingUpdateManifest(
    PendingUpdateTargetKind TargetKind,
    string PreparedSourcePath,
    string TargetPath,
    string RestartPath,
    string StagingRoot,
    string? ReleaseVersion,
    int PreviousProcessId,
    DateTimeOffset CreatedAtUtc);

internal static class PendingUpdateApplier
{
    private const string ManifestFileName = "pending-update.json";
    private const string UpdaterLogFileName = "updater.log";
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For(typeof(PendingUpdateApplier));

    public static string ManifestPath => Path.Combine(ApplicationPaths.UpdatesDirectory, ManifestFileName);

    public static bool HasPendingUpdate => File.Exists(ManifestPath);

    public static PendingUpdateManifest? TryReadPendingManifest()
    {
        if (!File.Exists(ManifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(ManifestPath);
            return JsonSerializer.Deserialize(json, PendingUpdateJsonContext.Default.PendingUpdateManifest);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to read pending update manifest", ex);
            return null;
        }
    }

    public static void StageForNextLaunch(
        PendingUpdateTargetKind targetKind,
        string preparedSourcePath,
        string targetPath,
        string restartPath,
        string stagingRoot,
        string? releaseVersion,
        int previousProcessId)
    {
        Directory.CreateDirectory(ApplicationPaths.UpdatesDirectory);

        var manifest = new PendingUpdateManifest(
            targetKind,
            preparedSourcePath,
            targetPath,
            restartPath,
            stagingRoot,
            releaseVersion,
            previousProcessId,
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(manifest, PendingUpdateJsonContext.Default.PendingUpdateManifest);
        var tempPath = ManifestPath + ".tmp";
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, ManifestPath, overwrite: true);

        WriteDiagnosticLog(
            "Staged pending update for next launch",
            $"TargetKind={targetKind}",
            $"PreparedSourcePath={preparedSourcePath}",
            $"TargetPath={targetPath}",
            $"RestartPath={restartPath}",
            $"StagingRoot={stagingRoot}",
            $"ReleaseVersion={releaseVersion ?? "<none>"}",
            $"PreviousProcessId={previousProcessId}");
    }

    public static void LaunchRelaunch(string restartPath)
    {
        if (string.IsNullOrWhiteSpace(restartPath))
            throw new InvalidOperationException("Unable to relaunch because the restart path is missing.");

        WriteDiagnosticLog("Launching relaunch process", $"RestartPath={restartPath}");

        if (OperatingSystem.IsMacOS() && restartPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(restartPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch the macOS relaunch helper.");
            return;
        }

        var executableStartInfo = new ProcessStartInfo
        {
            FileName = restartPath,
            UseShellExecute = OperatingSystem.IsWindows(),
            CreateNoWindow = !OperatingSystem.IsWindows()
        };

        if (OperatingSystem.IsWindows())
        {
            var workingDirectory = Path.GetDirectoryName(restartPath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                executableStartInfo.WorkingDirectory = workingDirectory;
        }

        using var relaunch = Process.Start(executableStartInfo)
            ?? throw new InvalidOperationException("Failed to launch the relaunch process.");
    }

    public static bool TryApplyAtStartup()
    {
        if (!File.Exists(ManifestPath))
            return false;

        PendingUpdateManifest? manifest;
        try
        {
            var json = File.ReadAllText(ManifestPath);
            manifest = JsonSerializer.Deserialize(json, PendingUpdateJsonContext.Default.PendingUpdateManifest);
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog($"Failed to read pending update manifest: {ex}");
            Log.Warn("Failed to read pending update manifest", ex);
            return false;
        }

        if (manifest == null)
        {
            WriteDiagnosticLog("Pending update manifest was empty.");
            return false;
        }

        WriteDiagnosticLog(
            "Applying pending update at startup",
            $"TargetKind={manifest.TargetKind}",
            $"PreparedSourcePath={manifest.PreparedSourcePath}",
            $"TargetPath={manifest.TargetPath}",
            $"RestartPath={manifest.RestartPath}",
            $"StagingRoot={manifest.StagingRoot}",
            $"ReleaseVersion={manifest.ReleaseVersion ?? "<none>"}",
            $"PreviousProcessId={manifest.PreviousProcessId}");

        if (!ValidateManifest(manifest))
            return false;

        WaitForProcessExit(manifest.PreviousProcessId, TimeSpan.FromMinutes(2));

        try
        {
            ApplyManifest(manifest);
            CleanupAfterSuccessfulApply(manifest);
            WriteDiagnosticLog(
                "Pending update applied successfully. Relaunching updated build.",
                $"RestartPath={manifest.RestartPath}");
            LaunchRelaunch(manifest.RestartPath);
            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnosticLog($"Pending update apply failed: {ex}");
            Log.Error("Failed to apply pending update at startup", ex);
            return false;
        }
    }

    private static bool ValidateManifest(PendingUpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PreparedSourcePath) || !PathExists(manifest.PreparedSourcePath, manifest.TargetKind))
        {
            WriteDiagnosticLog("Pending update source payload is missing; clearing manifest.");
            TryDeleteManifest();
            TryDeleteDirectory(manifest.StagingRoot);
            return false;
        }

        if (string.IsNullOrWhiteSpace(manifest.TargetPath))
        {
            WriteDiagnosticLog("Pending update target path is missing; clearing manifest.");
            TryDeleteManifest();
            return false;
        }

        return true;
    }

    private static bool PathExists(string path, PendingUpdateTargetKind targetKind) =>
        targetKind switch
        {
            PendingUpdateTargetKind.DirectoryContents => Directory.Exists(path),
            PendingUpdateTargetKind.MacBundle => Directory.Exists(path),
            PendingUpdateTargetKind.LinuxAppImage => File.Exists(path),
            _ => false
        };

    private static void ApplyManifest(PendingUpdateManifest manifest)
    {
        switch (manifest.TargetKind)
        {
            case PendingUpdateTargetKind.DirectoryContents:
                ApplyDirectoryContentsUpdate(manifest.PreparedSourcePath, manifest.TargetPath);
                break;
            case PendingUpdateTargetKind.MacBundle:
                ApplyMacBundleUpdate(manifest.PreparedSourcePath, manifest.TargetPath);
                break;
            case PendingUpdateTargetKind.LinuxAppImage:
                ApplyLinuxAppImageUpdate(manifest.PreparedSourcePath, manifest.TargetPath);
                break;
            default:
                throw new InvalidOperationException($"Unsupported pending update target kind '{manifest.TargetKind}'.");
        }
    }

    private static void ApplyDirectoryContentsUpdate(string sourceDirectory, string targetDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Directory-content self-update is only supported on Windows.");

        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(targetDirectory, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            CopyFileWithRetries(sourceFile, destinationFile);
        }
    }

    private static void ApplyMacBundleUpdate(string sourceAppBundle, string targetAppBundle)
    {
        var temporaryBundle = $"{targetAppBundle}.new";
        TryDeleteDirectory(temporaryBundle);
        CopyDirectoryRecursive(sourceAppBundle, temporaryBundle);
        TryDeleteDirectory(targetAppBundle);
        Directory.Move(temporaryBundle, targetAppBundle);
    }

    private static void ApplyLinuxAppImageUpdate(string sourceFile, string targetFile)
    {
        TrySetUnixExecutable(sourceFile);
        var temporaryFile = $"{targetFile}.new";
        TryDeleteFile(temporaryFile);
        CopyFileWithRetries(sourceFile, temporaryFile);
        TrySetUnixExecutable(temporaryFile);

        try
        {
            File.Move(temporaryFile, targetFile, overwrite: true);
        }
        catch (IOException)
        {
            CopyFileWithRetries(temporaryFile, targetFile);
            TryDeleteFile(temporaryFile);
        }
    }

    private static void CleanupAfterSuccessfulApply(PendingUpdateManifest manifest)
    {
        TryDeleteManifest();
        TryDeleteDirectory(manifest.StagingRoot);
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0)
            return;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            Thread.Sleep(500);
        }

        WriteDiagnosticLog($"Timed out waiting for previous process {processId} to exit; attempting apply anyway.");
    }

    private static void CopyDirectoryRecursive(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var destinationFile = Path.Combine(targetDirectory, relativePath);
            CopyFileWithRetries(sourceFile, destinationFile);
        }
    }

    private static void CopyFileWithRetries(string sourceFile, string destinationFile, int maxAttempts = 10)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationFile);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                WriteDiagnosticLog($"Copy attempt {attempt} failed for '{destinationFile}': {ex.Message}");
                Thread.Sleep(500);
            }
        }

        File.Copy(sourceFile, destinationFile, overwrite: true);
    }

    private static void TrySetUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to set executable permissions on staged update payload", ex);
        }
    }

    private static void TryDeleteManifest()
    {
        try
        {
            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to delete pending update manifest", ex);
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to delete directory '{path}'", ex);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to delete file '{path}'", ex);
        }
    }

    private static void WriteDiagnosticLog(string message, params string[] details)
    {
        try
        {
            var logPath = Path.Combine(ApplicationPaths.LogsDirectory, UpdaterLogFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var writer = new StreamWriter(logPath, append: true, Encoding.UTF8);
            writer.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
            foreach (var detail in details)
                writer.WriteLine($"  {detail}");
        }
        catch (Exception ex)
        {
            Log.Warn("Diagnostics should never interfere with app behavior.", ex);
        }
    }
}
