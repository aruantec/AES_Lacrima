using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using AES_Emulation.Windows.API;

namespace AES_Emulation.Linux;

/// <summary>
/// Enumerates PipeWire/Pulse monitor sources and active playback sessions for gameplay recording settings.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxGameplayAudioEnumerator
{
    private const string DefaultMonitorLabel = "Default output (monitor)";

    public static IReadOnlyList<GameplayRecordingAudioDeviceItem> EnumerateMonitorDevices()
    {
        var defaultMonitor = LinuxFfmpegPulseAudio.ResolveDefaultSinkMonitor();
        var defaultLabel = string.Equals(defaultMonitor, "default", StringComparison.Ordinal)
            ? DefaultMonitorLabel
            : $"Default output ({defaultMonitor})";

        var results = new List<GameplayRecordingAudioDeviceItem>
        {
            new(defaultMonitor, defaultLabel, true),
        };

        AppendMonitorDevicesFromPactl(results);

        if (results.Count <= 1)
            AppendMonitorDevicesFromPwDump(results);

        return results;
    }

    private static void AppendMonitorDevicesFromPactl(List<GameplayRecordingAudioDeviceItem> results)
    {
        var pactl = LinuxAudioEnvironmentHelper.ResolvePactlExecutable();
        if (pactl == null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pactl,
                Arguments = "-f json list sources",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".monitor", StringComparison.Ordinal))
                    continue;

                var description = name;
                if (element.TryGetProperty("description", out var descriptionElement) &&
                    descriptionElement.ValueKind == JsonValueKind.String)
                {
                    var parsed = descriptionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                        description = parsed;
                }

                AddMonitorDevice(results, name, description, isDefault: false);
            }
        }
        catch
        {
            // Fall through to pw-dump.
        }
    }

    private static void AppendMonitorDevicesFromPwDump(List<GameplayRecordingAudioDeviceItem> results)
    {
        var pwDump = LinuxAudioEnvironmentHelper.ResolvePwDumpExecutable();
        if (pwDump == null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pwDump,
                Arguments = "Node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("info", out var infoElement) ||
                    !infoElement.TryGetProperty("props", out var propsElement))
                {
                    continue;
                }

                if (!propsElement.TryGetProperty("media.class", out var mediaClass) ||
                    mediaClass.ValueKind != JsonValueKind.String ||
                    !string.Equals(mediaClass.GetString(), "Audio/Sink", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!propsElement.TryGetProperty("node.name", out var nodeNameElement) ||
                    nodeNameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var nodeName = nodeNameElement.GetString();
                if (string.IsNullOrWhiteSpace(nodeName))
                    continue;

                var monitorId = nodeName.EndsWith(".monitor", StringComparison.Ordinal)
                    ? nodeName
                    : nodeName + ".monitor";

                var description = monitorId;
                if (propsElement.TryGetProperty("node.description", out var descriptionElement) &&
                    descriptionElement.ValueKind == JsonValueKind.String)
                {
                    var parsed = descriptionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                        description = parsed + " (monitor)";
                }

                AddMonitorDevice(results, monitorId, description, isDefault: false);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void AddMonitorDevice(
        List<GameplayRecordingAudioDeviceItem> results,
        string monitorId,
        string description,
        bool isDefault)
    {
        if (results.Exists(d => string.Equals(d.Id, monitorId, StringComparison.Ordinal)))
            return;

        results.Add(new GameplayRecordingAudioDeviceItem(monitorId, description, isDefault));
    }

    public static IReadOnlyList<GameplayRecordingAudioDeviceItem> EnumerateMonitorDevicesFromPwDump()
    {
        var defaultMonitor = LinuxFfmpegPulseAudio.ResolveDefaultSinkMonitor();
        var defaultLabel = string.Equals(defaultMonitor, "default", StringComparison.Ordinal)
            ? DefaultMonitorLabel
            : $"Default output ({defaultMonitor})";

        var results = new List<GameplayRecordingAudioDeviceItem>
        {
            new(defaultMonitor, defaultLabel, true),
        };

        AppendMonitorDevicesFromPwDump(results);
        return results;
    }

    public static IReadOnlyList<GameplayRecordingAudioSessionItem> EnumerateActiveSessions()
    {
        var results = new List<GameplayRecordingAudioSessionItem>();
        var pactl = LinuxAudioEnvironmentHelper.ResolvePactlExecutable();
        if (pactl != null)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pactl,
                    Arguments = "-f json list sink-inputs",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                LinuxAudioEnvironmentHelper.Apply(startInfo);

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        AppendSessionsFromSinkInputsJson(output, results);
                }
            }
            catch
            {
                // Fall through to pw-dump.
            }
        }

        if (results.Count == 0)
            AppendSessionsFromPwDump(results);

        results.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static void AppendSessionsFromSinkInputsJson(string output, List<GameplayRecordingAudioSessionItem> results)
    {
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("properties", out var properties))
                continue;

            if (!TryGetProcessId(properties, out var pid) || pid <= 0)
                continue;

            if (results.Exists(s => s.ProcessId == pid))
                continue;

            var displayName = ResolveDisplayName(properties, pid);
            results.Add(new GameplayRecordingAudioSessionItem(pid, displayName));
        }
    }

    private static void AppendSessionsFromPwDump(List<GameplayRecordingAudioSessionItem> results)
    {
        var pwDump = LinuxAudioEnvironmentHelper.ResolvePwDumpExecutable();
        if (pwDump == null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pwDump,
                Arguments = "Node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("info", out var infoElement) ||
                    !infoElement.TryGetProperty("props", out var properties))
                {
                    continue;
                }

                if (!properties.TryGetProperty("media.class", out var mediaClass) ||
                    mediaClass.ValueKind != JsonValueKind.String ||
                    !string.Equals(mediaClass.GetString(), "Stream/Output/Audio", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryGetProcessId(properties, out var pid) || pid <= 0)
                    continue;

                if (results.Exists(s => s.ProcessId == pid))
                    continue;

                results.Add(new GameplayRecordingAudioSessionItem(pid, ResolveDisplayName(properties, pid)));
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string ResolveDisplayName(JsonElement properties, int pid)
    {
        foreach (var key in new[] { "application.name", "media.name", "node.name" })
        {
            if (properties.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return $"{text} (PID {pid})";
            }
        }

        return $"Application (PID {pid})";
    }

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
}
