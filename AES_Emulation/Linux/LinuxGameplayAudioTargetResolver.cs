using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using AES_Emulation.Services;

namespace AES_Emulation.Linux;

/// <summary>
/// Resolves PipeWire pw-record targets for gameplay recording using the same stream
/// matching strategy as <see cref="LinuxEmulatorAudioVolumeController"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxGameplayAudioTargetResolver
{
    private static readonly HashSet<string> IgnoredApplicationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "plasmashell",
        "brave",
        "brave-browser",
        "chrome",
        "chromium",
        "firefox",
        "spotify",
        "discord",
        "obs",
        "vlc",
        "system sounds",
        "cursor",
    };

    public readonly record struct RecordTarget(string Target, string Description, int Score);

    public static IReadOnlyList<RecordTarget> ResolveTargets(
        GameplayRecordingAudioSource source,
        int primaryPid,
        int compositorLaunchPid,
        string? deviceId)
    {
        return source switch
        {
            GameplayRecordingAudioSource.OutputDevice when compositorLaunchPid > 0 && string.IsNullOrWhiteSpace(deviceId) =>
                MergeTargets(
                    ResolveEmulatorStreamTargets(primaryPid, compositorLaunchPid),
                    ResolveMonitorTargets(deviceId)),
            GameplayRecordingAudioSource.OutputDevice when compositorLaunchPid > 0 =>
                MergeTargets(
                    ResolveMonitorTargets(deviceId),
                    LowerPriority(ResolveEmulatorStreamTargets(primaryPid, compositorLaunchPid), 20).ToList()),
            GameplayRecordingAudioSource.OutputDevice => ResolveMonitorTargets(deviceId),
            GameplayRecordingAudioSource.Application or GameplayRecordingAudioSource.EmulatorProcess =>
                ResolveEmulatorStreamTargets(primaryPid, compositorLaunchPid),
            _ => Array.Empty<RecordTarget>(),
        };
    }

    private static IEnumerable<RecordTarget> LowerPriority(IEnumerable<RecordTarget> targets, int penalty)
    {
        foreach (var target in targets)
            yield return target with { Score = Math.Max(0, target.Score - penalty) };
    }

    private static IReadOnlyList<RecordTarget> MergeTargets(
        IReadOnlyList<RecordTarget> primary,
        IReadOnlyList<RecordTarget> secondary)
    {
        var merged = new List<RecordTarget>(primary.Count + secondary.Count);
        foreach (var target in primary)
            AddUniqueTarget(merged, target.Target, target.Description, target.Score);

        foreach (var target in secondary)
            AddUniqueTarget(merged, target.Target, target.Description, target.Score);

        merged.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        return merged;
    }

    public static IReadOnlyList<RecordTarget> ResolveEmulatorStreamTargets(int primaryPid, int compositorLaunchPid)
    {
        var targets = new List<RecordTarget>();
        var candidatePids = new HashSet<int>();
        var processNameHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPid(int pid)
        {
            if (pid > 0)
                candidatePids.Add(pid);
        }

        AddPid(primaryPid);

        if (compositorLaunchPid > 0)
        {
            var compositorRoot = LinuxCompositorProcessHelper.ResolveCompositorRootPid(compositorLaunchPid);
            AddPid(compositorRoot);
            AddPid(compositorLaunchPid);

            var tree = new HashSet<int>();
            LinuxCompositorProcessHelper.CollectCompositorTreePids(compositorRoot, tree);
            foreach (var pid in tree)
                AddPid(pid);

            var primaryEmulatorPid = LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(compositorRoot);
            AddPid(primaryEmulatorPid);
            if (primaryEmulatorPid > 0)
                LinuxCompositorProcessHelper.CollectProcessIdentityHints(primaryEmulatorPid, processNameHints);
        }

        foreach (var pid in candidatePids)
            LinuxCompositorProcessHelper.CollectProcessIdentityHints(pid, processNameHints);

        if (!LinuxPipeWireAudioBackend.TryListSinkInputsJson(out var json) || string.IsNullOrWhiteSpace(json))
            return targets;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return targets;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                if (!element.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryScoreStream(properties, candidatePids, processNameHints, out var score))
                    continue;

                var nodeId = indexElement.GetInt32();
                var description = DescribeStream(properties, nodeId);
                AddUniqueTarget(targets, nodeId.ToString(), description, score);

                if (TryGetPropertyString(properties, "node.name", out var nodeName) &&
                    !string.IsNullOrWhiteSpace(nodeName))
                {
                    AddUniqueTarget(targets, nodeName, $"{description} ({nodeName})", score - 1);
                }

                if (TryResolveMonitorTargetForStream(properties, out var monitor) &&
                    !string.IsNullOrWhiteSpace(monitor))
                {
                    AddUniqueTarget(targets, monitor, $"Monitor for {description}", Math.Max(40, score - 10));
                }
            }
        }
        catch
        {
            // ignored
        }

        targets.Sort(static (left, right) => right.Score.CompareTo(left.Score));

        foreach (var monitor in ResolveMonitorTargets(null))
        {
            if (monitor.Score >= 80)
                AddUniqueTarget(targets, monitor.Target, monitor.Description, 60);
        }

        targets.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        return targets;
    }

    public static IReadOnlyList<RecordTarget> ResolveMonitorTargets(string? deviceId)
    {
        var targets = new List<RecordTarget>();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            AddUniqueTarget(targets, deviceId, deviceId, 100);
            if (!deviceId.EndsWith(".monitor", StringComparison.Ordinal))
            {
                AddUniqueTarget(targets, deviceId + ".monitor", deviceId + " monitor", 95);
            }
            else
            {
                var sinkName = deviceId[..^".monitor".Length];
                AddUniqueTarget(targets, sinkName, $"{sinkName} (sink)", 98);
            }

            return targets;
        }

        AddUniqueTarget(targets, "@DEFAULT_MONITOR@", "Default monitor (Pulse compat)", 80);

        if (TryResolveDefaultSinkMonitor(out var monitor))
            AddUniqueTarget(targets, monitor, $"Default sink monitor ({monitor})", 90);

        return targets;
    }

    private static bool TryScoreStream(
        JsonElement properties,
        HashSet<int> candidatePids,
        HashSet<string> processNameHints,
        out int score)
    {
        score = 0;

        if (TryGetPropertyString(properties, "application.name", out var applicationName) &&
            IsIgnoredApplicationName(applicationName))
        {
            return false;
        }

        TryGetProcessId(properties, out var pid);
        if (pid > 0 && candidatePids.Contains(pid))
            score = Math.Max(score, 100);

        if (MatchesProcessNameHints(properties, processNameHints, out var hintScore))
            score = Math.Max(score, hintScore);

        if (score <= 0 && pid > 0 &&
            TryMatchFromProcessIdentity(properties, pid, out hintScore))
        {
            score = hintScore;
        }

        if (score <= 0 && IsLikelyCompositorStream(properties))
            score = 70;

        return score > 0;
    }

    private static bool TryMatchFromProcessIdentity(JsonElement properties, int pid, out int score)
    {
        score = 0;
        var identityHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LinuxCompositorProcessHelper.CollectProcessIdentityHints(pid, identityHints);
        if (identityHints.Count == 0)
            return false;

        foreach (var key in new[]
                 {
                     "application.process.binary",
                     "application.name",
                     "application.process.command",
                     "node.name",
                     "media.name",
                 })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.ToLowerInvariant();
            foreach (var hint in identityHints)
            {
                if (hint.Length < 2 || !normalized.Contains(hint, StringComparison.Ordinal))
                    continue;

                score = key switch
                {
                    "application.process.binary" => 85,
                    "application.name" => 75,
                    "node.name" => 65,
                    _ => 55,
                };
                return true;
            }
        }

        return false;
    }

    private static bool MatchesProcessNameHints(JsonElement properties, HashSet<string> hints, out int score)
    {
        score = 0;
        if (hints.Count == 0)
            return false;

        foreach (var key in new[] { "application.process.binary", "application.name", "node.name", "media.name" })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.ToLowerInvariant();
            foreach (var hint in hints)
            {
                if (hint.Length < 2 || !normalized.Contains(hint, StringComparison.Ordinal))
                    continue;

                score = Math.Max(score, key switch
                {
                    "application.process.binary" => 90,
                    "application.name" => 80,
                    "node.name" => 70,
                    _ => 60,
                });
            }
        }

        return score > 0;
    }

    private static bool IsLikelyCompositorStream(JsonElement properties)
    {
        foreach (var key in new[] { "application.process.binary", "application.name", "application.process.command", "node.name" })
        {
            if (!TryGetPropertyString(properties, key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Contains("gamescope", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("gamescopereaper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveMonitorTargetForStream(JsonElement properties, out string monitor)
    {
        monitor = string.Empty;

        if (TryGetPropertyString(properties, "target.object", out var targetObject) &&
            !string.IsNullOrWhiteSpace(targetObject))
        {
            monitor = targetObject.EndsWith(".monitor", StringComparison.Ordinal)
                ? targetObject
                : targetObject + ".monitor";
            return true;
        }

        if (TryGetPropertyString(properties, "node.target", out var nodeTarget) &&
            !string.IsNullOrWhiteSpace(nodeTarget))
        {
            monitor = nodeTarget.EndsWith(".monitor", StringComparison.Ordinal)
                ? nodeTarget
                : nodeTarget + ".monitor";
            return true;
        }

        return false;
    }

    public static bool TryResolveDefaultSinkMonitor(out string monitor)
    {
        if (TryResolveDefaultSinkMonitorFromWpctl(out monitor))
            return true;

        return TryResolveDefaultSinkMonitorFromPwDump(out monitor);
    }

    private static bool TryResolveDefaultSinkMonitorFromWpctl(out string monitor)
    {
        monitor = string.Empty;
        var wpctl = LinuxAudioEnvironmentHelper.ResolveWpctlExecutable();
        if (wpctl == null)
            return false;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = wpctl,
                Arguments = "inspect @DEFAULT_AUDIO_SINK@",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.Contains("node.name", StringComparison.Ordinal))
                    continue;

                var equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                    continue;

                var value = line[(equalsIndex + 1)..].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                monitor = value.EndsWith(".monitor", StringComparison.Ordinal)
                    ? value
                    : value + ".monitor";
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryResolveDefaultSinkMonitorFromPwDump(out string monitor)
    {
        monitor = string.Empty;
        var pwDump = LinuxAudioEnvironmentHelper.ResolvePwDumpExecutable();
        if (pwDump == null)
            return false;

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pwDump,
                Arguments = "Node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            string? defaultSinkName = null;
            string? highestPrioritySink = null;
            var highestPriority = int.MinValue;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("info", out var infoElement) ||
                    !infoElement.TryGetProperty("props", out var propsElement))
                {
                    continue;
                }

                if (!TryGetPropertyString(propsElement, "media.class", out var mediaClass) ||
                    !string.Equals(mediaClass, "Audio/Sink", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryGetPropertyString(propsElement, "node.name", out var nodeName) ||
                    string.IsNullOrWhiteSpace(nodeName))
                {
                    continue;
                }

                var priority = 0;
                if (propsElement.TryGetProperty("priority.session", out var priorityElement) &&
                    priorityElement.ValueKind == JsonValueKind.Number)
                {
                    priority = priorityElement.GetInt32();
                }

                if (propsElement.TryGetProperty("state", out var stateElement) &&
                    stateElement.ValueKind == JsonValueKind.String &&
                    string.Equals(stateElement.GetString(), "running", StringComparison.OrdinalIgnoreCase))
                {
                    defaultSinkName = nodeName;
                }

                if (priority > highestPriority)
                {
                    highestPriority = priority;
                    highestPrioritySink = nodeName;
                }
            }

            var sinkName = defaultSinkName ?? highestPrioritySink;
            if (string.IsNullOrWhiteSpace(sinkName))
                return false;

            monitor = sinkName.EndsWith(".monitor", StringComparison.Ordinal)
                ? sinkName
                : sinkName + ".monitor";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeStream(JsonElement properties, int nodeId)
    {
        foreach (var key in new[] { "application.name", "application.process.binary", "media.name", "node.name" })
        {
            if (TryGetPropertyString(properties, key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                if (TryGetProcessId(properties, out var pid) && pid > 0)
                    return $"{value} (PID {pid}, node {nodeId})";

                return $"{value} (node {nodeId})";
            }
        }

        return $"Audio stream (node {nodeId})";
    }

    private static void AddUniqueTarget(List<RecordTarget> targets, string target, string description, int score)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (targets.Exists(t => string.Equals(t.Target, target, StringComparison.Ordinal)))
            return;

        targets.Add(new RecordTarget(target, description, score));
    }

    private static bool IsIgnoredApplicationName(string? applicationName) =>
        !string.IsNullOrWhiteSpace(applicationName) && IgnoredApplicationNames.Contains(applicationName);

    private static bool TryGetProcessId(JsonElement properties, out int pid)
    {
        pid = 0;
        foreach (var key in new[] { "application.process.id", "application.process.pid", "module-stream-restore.process.id" })
        {
            if (!properties.TryGetProperty(key, out var pidProperty))
                continue;

            if (pidProperty.ValueKind == JsonValueKind.String &&
                int.TryParse(pidProperty.GetString(), out pid))
            {
                return true;
            }

            if (pidProperty.ValueKind == JsonValueKind.Number &&
                pidProperty.TryGetInt32(out pid))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPropertyString(JsonElement properties, string key, out string? value)
    {
        value = null;
        if (!properties.TryGetProperty(key, out var property))
            return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };

        return !string.IsNullOrWhiteSpace(value);
    }
}
