using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AES_Emulation.Linux;

/// <summary>
/// Resolves live gamescope/gamescopereaper PIDs and emulator process trees.
/// </summary>
public static class LinuxCompositorProcessHelper
{
    public static int ResolveCompositorRootPid(int launchedPid)
    {
        if (launchedPid <= 0 || !IsProcessAlive(launchedPid))
            return launchedPid;

        var reaperChild = FindDirectChildByComm(launchedPid, "gamescopereaper");
        if (reaperChild > 0)
            return reaperChild;

        if (IsCompositorProcess(launchedPid))
            return launchedPid;

        return launchedPid;
    }

    public static int FindCompositorAncestor(int pid)
    {
        var current = pid;
        for (var depth = 0; depth < 16 && current > 1; depth++)
        {
            if (IsCompositorProcess(current))
                return current;

            if (!TryReadParentProcessId(current, out current))
                break;
        }

        return 0;
    }

    public static int FindPrimaryEmulatorPid(int compositorRootPid)
    {
        if (compositorRootPid <= 0 || !IsProcessAlive(compositorRootPid))
            return 0;

        var childrenByParent = BuildChildrenByParentMap();
        if (childrenByParent.TryGetValue(compositorRootPid, out var directChildren))
        {
            var directEmulators = new List<int>();
            foreach (var child in directChildren)
            {
                if (!IsProcessAlive(child) || IsCompositorProcess(child))
                    continue;

                directEmulators.Add(child);
            }

            if (directEmulators.Count > 0)
                return directEmulators.Max();
        }

        var tree = new HashSet<int>();
        AddProcessTreePids(compositorRootPid, tree);

        var fallback = 0;
        foreach (var pid in tree)
        {
            if (IsCompositorProcess(pid) || !IsProcessAlive(pid))
                continue;

            if (pid > fallback)
                fallback = pid;
        }

        return fallback;
    }

    public static void CollectCompositorTreePids(int compositorRootPid, HashSet<int> targetPids)
    {
        targetPids.Clear();
        if (compositorRootPid <= 0 || !IsProcessAlive(compositorRootPid))
            return;

        AddProcessTreePids(compositorRootPid, targetPids);
    }

    public static bool IsDescendantOf(int ancestorPid, int pid)
    {
        if (ancestorPid <= 0 || pid <= 0)
            return false;

        if (ancestorPid == pid)
            return true;

        var current = pid;
        for (var depth = 0; depth < 32 && current > 1; depth++)
        {
            if (!TryReadParentProcessId(current, out current))
                break;

            if (current == ancestorPid)
                return true;
        }

        return false;
    }

    public static bool IsCompositorProcess(int pid)
    {
        var comm = TryReadProcessComm(pid);
        if (string.IsNullOrWhiteSpace(comm))
            return false;

        return comm.StartsWith("gamescope", StringComparison.OrdinalIgnoreCase) ||
               comm.StartsWith("gamescopereaper", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindDirectChildByComm(int parentPid, string comm)
    {
        var childrenByParent = BuildChildrenByParentMap();
        if (!childrenByParent.TryGetValue(parentPid, out var children))
            return 0;

        foreach (var childPid in children)
        {
            if (string.Equals(TryReadProcessComm(childPid), comm, StringComparison.OrdinalIgnoreCase))
                return childPid;
        }

        return 0;
    }

    private static void AddProcessTreePids(int rootPid, HashSet<int> pids)
    {
        var childrenByParent = BuildChildrenByParentMap();

        void Collect(int pid)
        {
            if (pid <= 0 || !pids.Add(pid))
                return;

            if (!childrenByParent.TryGetValue(pid, out var children))
                return;

            foreach (var childPid in children)
                Collect(childPid);
        }

        Collect(rootPid);
    }

    private static bool IsProcessAlive(int pid)
        => pid > 0 && Directory.Exists($"/proc/{pid}");

    private static string? TryReadProcessComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadParentProcessId(int pid, out int parentPid)
    {
        parentPid = 0;
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("PPid:", StringComparison.Ordinal))
                    continue;

                var value = line["PPid:".Length..].Trim();
                return int.TryParse(value, out parentPid);
            }
        }
        catch
        {
            // ignored
        }

        return false;
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

            if (!TryReadParentProcessIdFromProcDir(entry, out var parentPid))
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

    private static bool TryReadParentProcessIdFromProcDir(string procDir, out int parentPid)
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
        catch
        {
            // ignored
        }

        return false;
    }
}
