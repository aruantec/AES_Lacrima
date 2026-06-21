using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using AES_Emulation.Services;

namespace AES_Emulation.Linux;

/// <summary>
/// Resolves PipeWire/Pulse monitor names for gameplay recording.
/// Pulse "default" often does not match the WirePlumber default sink (e.g. Bluetooth vs USB).
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxFfmpegPulseAudio
{
    public static bool IsPwRecordAvailable()
        => ResolvePwRecordExecutable() != null;

    public static string? ResolvePwRecordExecutable()
        => LinuxAudioEnvironmentHelper.ResolvePwRecordExecutable();

    public static bool IsPulseCaptureAvailable(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            return false;

        if (!TryResolveDefaultSinkMonitor(out var monitor))
            return false;

        try
        {
            var quoted = monitor.Contains(' ') ? $"\"{monitor}\"" : monitor;
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -f pulse -i {quoted} -t 0.1 -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            LinuxAudioEnvironmentHelper.Apply(startInfo, includeSdlDriver: false);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pulse monitor name for FFmpeg <c>-f pulse</c> (ends with <c>.monitor</c>).
    /// </summary>
    public static string ResolvePulseMonitor(
        GameplayRecordingAudioSource source,
        int processId,
        int compositorLaunchPid,
        string? deviceId)
    {
        if (source == GameplayRecordingAudioSource.OutputDevice)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId.EndsWith(".monitor", StringComparison.Ordinal)
                    ? deviceId
                    : deviceId + ".monitor";
            }

            return ResolveDefaultSinkMonitor();
        }

        if (source is GameplayRecordingAudioSource.Application or GameplayRecordingAudioSource.EmulatorProcess)
        {
            var targets = LinuxGameplayAudioTargetResolver.ResolveTargets(
                source,
                processId,
                compositorLaunchPid,
                deviceId);

            foreach (var target in targets)
            {
                if (target.Target.EndsWith(".monitor", StringComparison.Ordinal))
                    return target.Target;
            }
        }

        return ResolveDefaultSinkMonitor();
    }

    /// <summary>
    /// PipeWire node name for <c>pw-record --target</c> (sink name, no <c>.monitor</c> suffix).
    /// </summary>
    public static string ResolvePwRecordTarget(
        GameplayRecordingAudioSource source,
        int processId,
        int compositorLaunchPid,
        string? deviceId)
    {
        var monitor = ResolvePulseMonitor(source, processId, compositorLaunchPid, deviceId);
        return monitor.EndsWith(".monitor", StringComparison.Ordinal)
            ? monitor[..^".monitor".Length]
            : monitor;
    }

    public static string ResolveDefaultSinkMonitor()
    {
        if (TryResolveDefaultSinkMonitor(out var monitor))
            return monitor;

        return "default";
    }

    public static bool TryResolveDefaultSinkMonitor(out string monitor)
    {
        monitor = string.Empty;
        if (!LinuxGameplayAudioTargetResolver.TryResolveDefaultSinkMonitor(out var sinkMonitor))
            return false;

        monitor = sinkMonitor;
        return !string.IsNullOrWhiteSpace(monitor);
    }

    [Obsolete("Use ResolvePulseMonitor")]
    public static string ResolvePulseInput(
        GameplayRecordingAudioSource source,
        int processId,
        int compositorLaunchPid,
        string? deviceId)
        => ResolvePulseMonitor(source, processId, compositorLaunchPid, deviceId);

    [Obsolete("Use IsPwRecordAvailable or IsPulseCaptureAvailable")]
    public static bool IsCaptureAvailable(string? ffmpegPath) => IsPwRecordAvailable() || IsPulseCaptureAvailable(ffmpegPath);
}
