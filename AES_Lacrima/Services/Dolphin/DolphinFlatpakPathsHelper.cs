using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.Linux;
using log4net;

namespace AES_Lacrima.Services.Dolphin;

public static class DolphinFlatpakPathsHelper
{
    private const string FlatpakDataFolderName = "dolphin-emu";
    private const string FlatpakBundledSysRoot = "/app/share/dolphin-emu/sys";
    private const string SyncMarkerFileName = ".flatpak-sys-sync";

    private static readonly ILog Log = LogHelper.For(typeof(DolphinFlatpakPathsHelper));

    public static bool IsFlatpakLaunch(string? flatpakAppId)
        => OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(flatpakAppId);

    public static string? ResolveUserDirectory(string? flatpakAppId)
    {
        if (!IsFlatpakLaunch(flatpakAppId))
            return null;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        return Path.Combine(home, ".var", "app", flatpakAppId!.Trim(), "data", FlatpakDataFolderName);
    }

    public static async Task<string?> EnsureSysGameSettingsDirectoryAsync(
        string? emulatorDirectory,
        string? flatpakAppId,
        CancellationToken cancellationToken = default)
    {
        if (!IsFlatpakLaunch(flatpakAppId))
            return null;

        var cacheRoot = ResolveSysCacheRoot(emulatorDirectory);
        var gameSettingsDirectory = Path.Combine(cacheRoot, "Sys", "GameSettings");
        var markerPath = Path.Combine(gameSettingsDirectory, SyncMarkerFileName);
        if (File.Exists(markerPath))
            return gameSettingsDirectory;

        Directory.CreateDirectory(gameSettingsDirectory);

        if (!OperatingSystem.IsLinux())
            return null;

        var flatpakPath = LinuxFlatpakApplicationService.GetFlatpakExecutable();
        if (flatpakPath == null)
            return null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = flatpakPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--command=tar");
            startInfo.ArgumentList.Add(flatpakAppId!);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(FlatpakBundledSysRoot);
            startInfo.ArgumentList.Add("GameSettings");

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            await using var tarStream = process.StandardOutput.BaseStream;
            var reader = new TarReader(tarStream, leaveOpen: true);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: true)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType != TarEntryType.RegularFile || string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                var relativeName = entry.Name.Replace('\\', '/').TrimStart('/');
                const string prefix = "GameSettings/";
                if (relativeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    relativeName = relativeName[prefix.Length..];

                if (string.IsNullOrWhiteSpace(relativeName))
                    continue;

                var destinationPath = Path.Combine(gameSettingsDirectory, relativeName);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (string.IsNullOrWhiteSpace(destinationDirectory))
                    continue;

                Directory.CreateDirectory(destinationDirectory);
                await using var destination = File.Create(destinationPath);
                if (entry.DataStream != null)
                    await entry.DataStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                Log.Warn($"Failed to export Dolphin Flatpak GameSettings: {error}");
                return Directory.EnumerateFiles(gameSettingsDirectory).Any() ? gameSettingsDirectory : null;
            }

            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
            return gameSettingsDirectory;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to cache Dolphin Flatpak GameSettings.", ex);
            return Directory.Exists(gameSettingsDirectory) &&
                   Directory.EnumerateFiles(gameSettingsDirectory).Any()
                ? gameSettingsDirectory
                : null;
        }
    }

    private static string ResolveSysCacheRoot(string? emulatorDirectory)
    {
        if (!string.IsNullOrWhiteSpace(emulatorDirectory))
            return emulatorDirectory.Trim();

        return Path.Combine(ApplicationPaths.EmulatorsDirectory, "GC", "Dolphin");
    }
}
