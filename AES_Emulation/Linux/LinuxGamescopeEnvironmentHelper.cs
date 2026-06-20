using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AES_Emulation.Linux;

internal static class LinuxGamescopeEnvironmentHelper
{
    public static bool TryResolveGamescopeX11Display(int compositorPid, out string display)
    {
        display = string.Empty;
        if (!OperatingSystem.IsLinux() || compositorPid <= 0)
            return false;

        foreach (var pid in EnumerateProcessTree(compositorPid))
        {
            if (!TryReadProcessEnvironment(pid, out var environment))
                continue;

            if (environment.TryGetValue("DISPLAY", out var candidate) &&
                !string.IsNullOrWhiteSpace(candidate) &&
                !string.Equals(candidate, ":0", StringComparison.Ordinal))
            {
                display = candidate.Trim();
                return true;
            }
        }

        foreach (var pid in EnumerateProcessTree(compositorPid))
        {
            if (!TryReadProcessEnvironment(pid, out var environment))
                continue;

            if (environment.TryGetValue("DISPLAY", out var candidate) &&
                !string.IsNullOrWhiteSpace(candidate))
            {
                display = candidate.Trim();
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<int> EnumerateProcessTree(int rootPid)
    {
        if (rootPid <= 0)
            yield break;

        var queue = new Queue<int>();
        var visited = new HashSet<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!visited.Add(pid))
                continue;

            yield return pid;

            foreach (var child in ReadChildProcessIds(pid))
                queue.Enqueue(child);
        }
    }

    private static IEnumerable<int> ReadChildProcessIds(int pid)
    {
        var childrenPath = $"/proc/{pid}/task/{pid}/children";
        if (!File.Exists(childrenPath))
            yield break;

        string text;
        try
        {
            text = File.ReadAllText(childrenPath);
        }
        catch
        {
            yield break;
        }

        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var childPid) && childPid > 0)
                yield return childPid;
        }
    }

    internal static bool TryReadProcessEnvironment(int pid, out Dictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var environPath = $"/proc/{pid}/environ";
        if (!File.Exists(environPath))
            return false;

        try
        {
            var raw = File.ReadAllBytes(environPath);
            var start = 0;
            for (var i = 0; i <= raw.Length; i++)
            {
                if (i < raw.Length && raw[i] != 0)
                    continue;

                if (i <= start)
                {
                    start = i + 1;
                    continue;
                }

                var entry = System.Text.Encoding.UTF8.GetString(raw, start, i - start);
                var separator = entry.IndexOf('=');
                if (separator > 0)
                {
                    var key = entry[..separator];
                    var value = entry[(separator + 1)..];
                    environment[key] = value;
                }

                start = i + 1;
            }

            return environment.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
