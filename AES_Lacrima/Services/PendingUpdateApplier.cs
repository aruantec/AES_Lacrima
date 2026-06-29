using AES_Core.IO;
using log4net;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        PendingUpdateManualApplyScripts.EnsureWritten();

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

    public static bool TryScheduleExternalApply(PendingUpdateManifest manifest, int waitProcessId)
    {
        if (OperatingSystem.IsWindows())
            return WindowsPendingUpdateHelper.TryScheduleApply(manifest, waitProcessId);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return UnixPendingUpdateHelper.TryScheduleApply(manifest, waitProcessId);

        return false;
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

        if (!IsManifestForCurrentInstallation(manifest))
        {
            WriteDiagnosticLog(
                "Pending update targets a different installation; skipping apply at startup.",
                $"CurrentBaseDirectory={AppContext.BaseDirectory}",
                $"ManifestTargetPath={manifest.TargetPath}",
                $"ManifestRestartPath={manifest.RestartPath}");
            PendingUpdateManualApplyScripts.EnsureWritten();
            return false;
        }

        PendingUpdateManualApplyScripts.EnsureWritten();

        if (TryScheduleExternalApply(manifest, Environment.ProcessId))
        {
            WriteDiagnosticLog(
                "Exiting so the external update helper can replace locked files.",
                $"ProcessId={Environment.ProcessId}");
            Environment.Exit(0);
            return true;
        }

        WriteDiagnosticLog("Failed to schedule external update apply; leaving pending update manifest in place.");
        Log.Error("Failed to schedule external update apply at startup");
        return false;
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

    private static bool IsManifestForCurrentInstallation(PendingUpdateManifest manifest)
    {
        if (manifest.TargetKind == PendingUpdateTargetKind.DirectoryContents)
        {
            return string.Equals(
                NormalizeDirectoryPath(AppContext.BaseDirectory),
                NormalizeDirectoryPath(manifest.TargetPath),
                StringComparison.OrdinalIgnoreCase);
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(manifest.RestartPath))
            return false;

        return string.Equals(
            Path.GetFullPath(processPath),
            Path.GetFullPath(manifest.RestartPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathExists(string path, PendingUpdateTargetKind targetKind) =>
        targetKind switch
        {
            PendingUpdateTargetKind.DirectoryContents => Directory.Exists(path),
            PendingUpdateTargetKind.MacBundle => Directory.Exists(path),
            PendingUpdateTargetKind.LinuxAppImage => File.Exists(path),
            _ => false
        };

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
