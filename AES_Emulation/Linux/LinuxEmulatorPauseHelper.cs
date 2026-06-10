using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Pauses and resumes emulator processes inside a gamescope session using SIGSTOP/SIGCONT.
/// gamescope itself keeps running so PipeWire capture can hold the last frame.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxEmulatorPauseHelper
{
    private const int SigStop = 19;
    private const int SigCont = 18;

    private static readonly ILog Log = LogHelper.For(typeof(LinuxEmulatorPauseHelper));

    public static bool TryResolveEmulatorPids(int trackedPid, int compositorPid, out HashSet<int> emulatorPids)
    {
        emulatorPids = new HashSet<int>();
        if (!OperatingSystem.IsLinux())
            return false;

        return TryCollectEmulatorPids(trackedPid, compositorPid, emulatorPids);
    }

    public static bool TrySuspendEmulatorTree(int trackedPid, int compositorPid, out HashSet<int> suspendedPids)
    {
        suspendedPids = new HashSet<int>();
        if (!OperatingSystem.IsLinux())
            return false;

        if (!TryCollectEmulatorPids(trackedPid, compositorPid, suspendedPids))
            return false;

        var appliedAny = false;
        foreach (var pid in suspendedPids)
            appliedAny |= TrySendSignal(pid, SigStop);

        if (appliedAny)
        {
            Log.Info(
                $"EmulatorPause: suspended {suspendedPids.Count} process(es) " +
                $"[trackedPid={trackedPid}, compositorPid={compositorPid}].");
        }

        return appliedAny;
    }

    public static bool TryResumeEmulatorTree(IEnumerable<int> suspendedPids)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var resumedAny = false;
        var count = 0;
        foreach (var pid in suspendedPids)
        {
            if (pid <= 0)
                continue;

            count++;
            resumedAny |= TrySendSignal(pid, SigCont);
        }

        if (resumedAny)
            Log.Info($"EmulatorPause: resumed {count} process(es).");

        return resumedAny;
    }

    private static bool TryCollectEmulatorPids(int trackedPid, int compositorPid, HashSet<int> targetPids)
    {
        var seeds = new HashSet<int>();
        if (trackedPid > 0)
            seeds.Add(trackedPid);

        if (compositorPid > 0)
            seeds.Add(compositorPid);

        var compositorRoot = compositorPid > 0
            ? LinuxCompositorProcessHelper.ResolveCompositorRootPid(compositorPid)
            : 0;
        if (compositorRoot > 0)
            seeds.Add(compositorRoot);

        var sessionPids = new HashSet<int>();
        LinuxCompositorProcessHelper.CollectSessionProcessTrees(seeds, sessionPids);

        foreach (var pid in sessionPids)
        {
            if (pid <= 0 || !IsProcessAlive(pid))
                continue;

            if (LinuxCompositorProcessHelper.IsCompositorProcess(pid))
                continue;

            targetPids.Add(pid);
        }

        if (targetPids.Count > 0)
            return true;

        var primaryEmulatorPid = compositorRoot > 0
            ? LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(compositorRoot)
            : 0;
        if (primaryEmulatorPid > 0 && IsProcessAlive(primaryEmulatorPid))
        {
            targetPids.Add(primaryEmulatorPid);
            CollectProcessTreePids(primaryEmulatorPid, targetPids);
            return targetPids.Count > 0;
        }

        if (trackedPid > 0 &&
            IsProcessAlive(trackedPid) &&
            !LinuxCompositorProcessHelper.IsCompositorProcess(trackedPid))
        {
            targetPids.Add(trackedPid);
            CollectProcessTreePids(trackedPid, targetPids);
        }

        return targetPids.Count > 0;
    }

    private static void CollectProcessTreePids(int rootPid, HashSet<int> targetPids)
    {
        var tree = new HashSet<int>();
        LinuxCompositorProcessHelper.CollectCompositorTreePids(rootPid, tree);
        foreach (var pid in tree)
        {
            if (pid <= 0 || !IsProcessAlive(pid))
                continue;

            if (LinuxCompositorProcessHelper.IsCompositorProcess(pid))
                continue;

            targetPids.Add(pid);
        }
    }

    private static bool TrySendSignal(int pid, int signal)
    {
        try
        {
            if (!IsProcessAlive(pid))
                return false;

            if (kill(pid, signal) == 0)
                return true;

            Log.Debug($"EmulatorPause: kill({pid}, {signal}) failed with errno={Marshal.GetLastWin32Error()}.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug($"EmulatorPause: failed to send signal {signal} to pid={pid}.", ex);
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
        => pid > 0 && System.IO.Directory.Exists($"/proc/{pid}");

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
