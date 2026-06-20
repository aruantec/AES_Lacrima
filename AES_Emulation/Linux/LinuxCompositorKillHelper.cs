using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

        SLog.Info($"Force-killing emulator process tree (pid={pid}).");
        KillProcessTreeSinglePass(pid);
    }

    /// <summary>
    /// Sends SIGKILL to a process tree. Uses one /proc scan; does not block on Process.WaitForExit.
    /// </summary>
    public static void ForceKillProcessTree(int rootPid, bool waitForFallback = false)
    {
        if (!OperatingSystem.IsLinux() || rootPid <= 0)
            return;

        SLog.Info($"Force-killing gamescope/emulator process tree (root pid={rootPid}).");
        KillProcessTreeSinglePass(rootPid);
        RunShellKillFallback(rootPid, waitForFallback);
    }

    /// <summary>
    /// Signals a process tree on a background thread so the UI thread never waits on gamescope teardown.
    /// </summary>
    public static void ScheduleKillProcessTree(int rootPid)
    {
        if (!OperatingSystem.IsLinux() || rootPid <= 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                SLog.Info($"Scheduling background kill for gamescope/emulator tree (root pid={rootPid}).");
                KillProcessTreeSinglePass(rootPid);
            }
            catch (Exception ex)
            {
                SLog.Debug($"Background kill failed for pid={rootPid}.", ex);
            }
        });
    }

    private static void KillProcessTreeSinglePass(int rootPid)
    {
        var childrenByParent = BuildChildrenByParentMap();

        void KillDepthFirst(int pid)
        {
            if (childrenByParent.TryGetValue(pid, out var children))
            {
                foreach (var childPid in children)
                    KillDepthFirst(childPid);
            }

            TryKill(pid, SigKill);
        }

        KillDepthFirst(rootPid);
    }

    private static Dictionary<int, List<int>> BuildChildrenByParentMap()
    {
        var childrenByParent = new Dictionary<int, List<int>>();

        if (!Directory.Exists("/proc"))
            return childrenByParent;

        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out var pid) || pid <= 1)
                continue;

            if (!TryReadParentProcessId(entry, out var parentPid))
                continue;

            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                children = new List<int>();
                childrenByParent[parentPid] = children;
            }

            children.Add(pid);
        }

        return childrenByParent;
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

    private static void RunShellKillFallback(int rootPid, bool waitForExit)
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

            if (shell == null || !waitForExit)
                return;

            shell.WaitForExit(250);
        }
        catch (Exception ex)
        {
            SLog.Debug($"Shell fallback kill failed for pid={rootPid}.", ex);
        }
    }

    /// <summary>
    /// Kills leftover gamescope/gamescopereaper sessions from prior launches.
    /// </summary>
    public static void KillOrphanedGamescopeSessions(int exceptRootPid = 0)
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var pid in EnumerateLiveCompositorRootPids())
        {
            if (pid == exceptRootPid)
                continue;

            SLog.Info($"Killing orphaned gamescope session (root pid={pid}).");
            ForceKillProcessTree(pid, waitForFallback: false);
        }
    }

    private static IEnumerable<int> EnumerateLiveCompositorRootPids()
    {
        if (!Directory.Exists("/proc"))
            yield break;

        foreach (var entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out var pid) || pid <= 1)
                continue;

            string comm;
            try
            {
                comm = File.ReadAllText(Path.Combine(entry, "comm")).Trim();
            }
            catch
            {
                continue;
            }

            if (comm.StartsWith("gamescope", StringComparison.OrdinalIgnoreCase))
                yield return pid;
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
