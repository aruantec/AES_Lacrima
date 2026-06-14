using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.IO;
using log4net;

using AES_Core.Logging;

namespace AES_Lacrima.Services;

internal static class EmulatorInstallDirectoryHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(EmulatorInstallDirectoryHelper));
    private static readonly string[] PolkitEnvironmentVariables =
    [
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "XAUTHORITY",
        "DBUS_SESSION_BUS_ADDRESS",
        "XDG_RUNTIME_DIR",
        "DESKTOP_SESSION",
        "HOME",
        "USER",
        "LOGNAME",
    ];

    public static async Task<(bool Success, string? ErrorMessage)> EnsureWritableAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);

        if (ApplicationPaths.IsDirectoryWritable(directory))
            return (true, null);

        if (!OperatingSystem.IsLinux())
        {
            return (false,
                $"Cannot write to emulator directory '{directory}'. Check folder permissions.");
        }

        var emulatorsRoot = ApplicationPaths.EmulatorsDirectory;
        Log.Warn($"Emulator directory is not writable: '{directory}'. Attempting ownership repair via pkexec.");

        if (await TryRepairLinuxOwnershipAsync(emulatorsRoot, cancellationToken).ConfigureAwait(false) &&
            ApplicationPaths.IsDirectoryWritable(directory))
        {
            Log.Info($"Emulator directory ownership repair succeeded for '{emulatorsRoot}'.");
            return (true, null);
        }

        if (!string.Equals(directory, emulatorsRoot, StringComparison.Ordinal) &&
            await TryRepairLinuxOwnershipAsync(directory, cancellationToken).ConfigureAwait(false) &&
            ApplicationPaths.IsDirectoryWritable(directory))
        {
            Log.Info($"Emulator directory ownership repair succeeded for '{directory}'.");
            return (true, null);
        }

        return (false,
            $"Cannot write to emulator directory '{directory}' (often caused by root-owned folders under Emulators). " +
            $"Approve the system permission prompt when downloading, or run: sudo chown -R \"$USER\":\"$USER\" \"{emulatorsRoot}\"");
    }

    public static string? GetWritableDirectoryWarning(string directory)
    {
        if (ApplicationPaths.IsDirectoryWritable(directory))
            return null;

        return $"Emulator folder is not writable: {directory}";
    }

    private static async Task<bool> TryRepairLinuxOwnershipAsync(string directory, CancellationToken cancellationToken)
    {
        var pkexec = ResolveSystemExecutable("pkexec");
        var bash = ResolveSystemExecutable("bash") ?? "/bin/bash";
        if (string.IsNullOrWhiteSpace(pkexec))
        {
            Log.Warn("pkexec was not found on PATH; cannot repair emulator directory ownership automatically.");
            return false;
        }

        var userName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(userName))
        {
            Log.Warn("Environment.UserName is empty; cannot repair emulator directory ownership.");
            return false;
        }

        var groupName = await GetLinuxPrimaryGroupNameAsync(cancellationToken).ConfigureAwait(false) ?? userName;
        var escapedDirectory = directory.Replace("'", "'\\''");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"aes-emulator-chown-{Guid.NewGuid():N}.sh");

        try
        {
            await File.WriteAllTextAsync(
                scriptPath,
                $"""
                #!/usr/bin/env bash
                set -euo pipefail
                chown -R '{userName.Replace("'", "'\\''")}':'{groupName.Replace("'", "'\\''")}' '{escapedDirectory}'
                """,
                cancellationToken).ConfigureAwait(false);

            try
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to chmod emulator ownership repair script '{scriptPath}'.", ex);
            }

            Log.Info($"Starting emulator ownership repair via pkexec: {pkexec} {bash} {scriptPath}");
            var startInfo = new ProcessStartInfo
            {
                FileName = pkexec,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(bash);
            startInfo.ArgumentList.Add(scriptPath);
            CopyPolkitEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                Log.Warn("Failed to start pkexec for emulator directory ownership repair.");
                return false;
            }

            var exitedTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            var completedTask = await Task.WhenAny(exitedTask, timeoutTask).ConfigureAwait(false);
            if (completedTask != exitedTask)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception killEx)
                {
                    Log.Debug("Failed to kill timed-out emulator ownership repair command.", killEx);
                }

                Log.Warn($"Emulator ownership repair timed out for '{directory}'.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                Log.Warn(
                    $"Emulator ownership repair failed for '{directory}' (exit code {process.ExitCode}). " +
                    "Authentication may have been cancelled or denied.");
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to repair ownership for emulator directory '{directory}'.", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete emulator ownership repair script '{scriptPath}'.", ex);
            }
        }
    }

    private static async Task<string?> GetLinuxPrimaryGroupNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveSystemExecutable("id") ?? "/usr/bin/id",
                Arguments = "-gn",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            var group = output.Trim();
            return string.IsNullOrWhiteSpace(group) ? null : group;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to resolve Linux primary group name.", ex);
            return null;
        }
    }

    private static string? ResolveSystemExecutable(string command)
    {
        if (command.Contains('/', StringComparison.Ordinal))
            return File.Exists(command) ? command : null;

        var resolved = ResolveFromPath(command);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        foreach (var candidate in GetDefaultExecutableCandidates(command))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> GetDefaultExecutableCandidates(string command)
    {
        if (string.Equals(command, "pkexec", StringComparison.Ordinal))
        {
            yield return "/usr/bin/pkexec";
            yield return "/sbin/pkexec";
            yield break;
        }

        if (string.Equals(command, "bash", StringComparison.Ordinal))
        {
            yield return "/bin/bash";
            yield return "/usr/bin/bash";
            yield break;
        }

        if (string.Equals(command, "id", StringComparison.Ordinal))
        {
            yield return "/usr/bin/id";
            yield return "/bin/id";
        }
    }

    private static string? ResolveFromPath(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveSystemExecutable("bash") ?? "/bin/bash",
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
            Log.Debug($"Failed to resolve '{executable}' from PATH.", ex);
            return null;
        }
    }

    private static void CopyPolkitEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var variable in PolkitEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[variable] = value;
        }
    }
}
