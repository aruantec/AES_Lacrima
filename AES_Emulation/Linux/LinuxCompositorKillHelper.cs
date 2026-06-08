using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Force-kills a gamescope session and its emulator children.
/// </summary>
public static class LinuxCompositorKillHelper
{
    private const int SigKill = 9;

    private static readonly ILog SLog = LogHelper.For(typeof(LinuxCompositorKillHelper));

    public static void ForceKillEmulatorProcess(Process process)
    {
        if (!OperatingSystem.IsLinux() || process == null)
            return;

        try
        {
            process.Refresh();
            if (process.HasExited)
                return;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to inspect emulator process before termination.", ex);
            return;
        }

        var pid = 0;
        try
        {
            pid = process.Id;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to read emulator pid before termination.", ex);
            return;
        }

        SLog.Info($"Force-killing emulator process tree (pid={pid}), keeping gamescope alive.");

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            SLog.Debug($"Process.Kill(entireProcessTree) failed for emulator pid={pid}.", ex);
        }

        KillDescendantsRecursive(pid);
        TryKill(pid, SigKill);
    }

    public static void ForceKillProcessTree(int rootPid)
    {
        if (!OperatingSystem.IsLinux() || rootPid <= 0)
            return;

        SLog.Info($"Force-killing gamescope/emulator process tree (root pid={rootPid}).");

        try
        {
            using var process = Process.GetProcessById(rootPid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            SLog.Debug($"Process.Kill(entireProcessTree) failed for pid={rootPid}.", ex);
        }

        KillDescendantsRecursive(rootPid);
        TryKill(rootPid, SigKill);
        RunShellKillFallback(rootPid);
    }

    private static void KillDescendantsRecursive(int parentPid)
    {
        foreach (var childPid in EnumerateChildProcessIds(parentPid))
        {
            KillDescendantsRecursive(childPid);
            TryKill(childPid, SigKill);
        }
    }

    private static IEnumerable<int> EnumerateChildProcessIds(int parentPid)
    {
        if (!Directory.Exists("/proc"))
            yield break;

        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out var pid) || pid <= 1)
                continue;

            if (TryReadParentProcessId(entry, out var ppid) && ppid == parentPid)
                yield return pid;
        }
    }

    private static bool TryReadParentProcessId(string procDir, out int parentPid)
    {
        parentPid = 0;
        try
        {
            var statusPath = Path.Combine(procDir, "status");
            if (!File.Exists(statusPath))
                return false;

            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("PPid:", StringComparison.Ordinal))
                    continue;

                var value = line["PPid:".Length..].Trim();
                return int.TryParse(value, out parentPid);
            }
        }
        catch (Exception ex)
        {
            SLog.Debug($"Failed to read parent pid from '{procDir}'.", ex);
        }

        return false;
    }

    private static void RunShellKillFallback(int rootPid)
    {
        try
        {
            var script = new StringBuilder();
            script.Append($"pkill -9 -P {rootPid} 2>/dev/null; ");
            script.Append($"kill -9 {rootPid} 2>/dev/null; ");
            script.Append("true");

            using var shell = Process.Start(new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-lc \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            shell?.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            SLog.Debug($"Shell fallback kill failed for pid={rootPid}.", ex);
        }
    }

    private static void TryKill(int pid, int signal)
    {
        try
        {
            if (kill(pid, signal) != 0)
                SLog.Debug($"kill({pid}, {signal}) failed with errno={Marshal.GetLastWin32Error()}.");
        }
        catch (Exception ex)
        {
            SLog.Debug($"Failed to send signal {signal} to pid={pid}.", ex);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
