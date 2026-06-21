using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux;

/// <summary>
/// Ensures child emulator processes can reach the user's PipeWire/PulseAudio session.
/// GUI apps launched from some contexts (IDE debuggers, detached services) may not inherit
/// XDG_RUNTIME_DIR, which prevents SDL/ALSA clients from opening an audio device.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxAudioEnvironmentHelper
{
    private static readonly string[] KeysToPropagate =
    [
        "PATH",
        "HOME",
        "USER",
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "XDG_RUNTIME_DIR",
        "PULSE_RUNTIME_PATH",
        "PULSE_SERVER",
        "DBUS_SESSION_BUS_ADDRESS",
    ];

    public static void Apply(ProcessStartInfo startInfo) => Apply(startInfo, includeSdlDriver: true);

    public static void Apply(ProcessStartInfo startInfo, bool includeSdlDriver, int compositorLaunchPid)
    {
        Apply(startInfo, includeSdlDriver);
        ApplyCompositorSessionEnvironment(startInfo, compositorLaunchPid);
    }

    /// <summary>
    /// Child processes must inherit the full session environment; assigning only a few keys
    /// replaces the entire environment in .NET and breaks Pulse/FFmpeg under gamescope.
    /// </summary>
    public static void Apply(ProcessStartInfo startInfo, bool includeSdlDriver)
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrEmpty(key))
                continue;

            startInfo.Environment[key] = entry.Value?.ToString() ?? string.Empty;
        }

        foreach (var key in KeysToPropagate)
        {
            var value = Resolve(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }

        if (includeSdlDriver && !startInfo.Environment.ContainsKey("SDL_AUDIODRIVER"))
            startInfo.Environment["SDL_AUDIODRIVER"] = "pipewire";
    }

    /// <summary>
    /// Prefer the gamescope/emulator PipeWire session when the UI process was started without
    /// a full desktop session environment (IDE debuggers, detached launches).
    /// </summary>
    public static void ApplyCompositorSessionEnvironment(ProcessStartInfo startInfo, int compositorLaunchPid)
    {
        if (!OperatingSystem.IsLinux() || compositorLaunchPid <= 0)
            return;

        var candidatePids = new List<int> { compositorLaunchPid };
        var compositorRoot = LinuxCompositorProcessHelper.ResolveCompositorRootPid(compositorLaunchPid);
        if (compositorRoot > 0)
            candidatePids.Add(compositorRoot);

        var emulatorPid = LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(compositorRoot > 0 ? compositorRoot : compositorLaunchPid);
        if (emulatorPid > 0)
            candidatePids.Add(emulatorPid);

        foreach (var key in new[]
                 {
                     "XDG_RUNTIME_DIR",
                     "PULSE_SERVER",
                     "PULSE_RUNTIME_PATH",
                     "PIPEWIRE_RUNTIME_DIR",
                     "PIPEWIRE_KEY",
                     "DBUS_SESSION_BUS_ADDRESS",
                     "WAYLAND_DISPLAY",
                     "DISPLAY",
                 })
        {
            foreach (var pid in candidatePids.Distinct())
            {
                if (!LinuxGamescopeEnvironmentHelper.TryReadProcessEnvironment(pid, out var environment))
                    continue;

                if (!environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                startInfo.Environment[key] = value;
                break;
            }
        }
    }

    internal static string? ResolveRuntimeDir()
    {
        var existing = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var uid = GetUserId();
        if (uid < 0)
            return null;

        var candidate = $"/run/user/{uid}";
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? Resolve(string key)
    {
        var existing = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var runtimeDir = ResolveRuntimeDir();
        if (string.IsNullOrWhiteSpace(runtimeDir))
            return null;

        return key switch
        {
            "XDG_RUNTIME_DIR" => runtimeDir,
            "PULSE_RUNTIME_PATH" => Directory.Exists(Path.Combine(runtimeDir, "pulse"))
                ? Path.Combine(runtimeDir, "pulse")
                : null,
            "PULSE_SERVER" => File.Exists(Path.Combine(runtimeDir, "pulse", "native"))
                ? $"unix:{Path.Combine(runtimeDir, "pulse", "native")}"
                : null,
            "DBUS_SESSION_BUS_ADDRESS" => File.Exists(Path.Combine(runtimeDir, "bus"))
                ? $"unix:path={Path.Combine(runtimeDir, "bus")}"
                : null,
            _ => null,
        };
    }

    private static int GetUserId()
    {
        try
        {
            return (int)getuid();
        }
        catch
        {
            return -1;
        }
    }

    internal static string? ResolvePactlExecutable()
    {
        foreach (var candidate in new[]
                 {
                     "pactl",
                     "/usr/bin/pactl",
                     "/usr/local/bin/pactl",
                     "/bin/pactl",
                 })
        {
            if (string.Equals(candidate, "pactl", StringComparison.Ordinal))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathEnv))
                {
                    foreach (var entry in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var resolved = Path.Combine(entry, "pactl");
                        if (File.Exists(resolved))
                            return resolved;
                    }
                }

                continue;
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static string? ResolveWpctlExecutable()
        => ResolveExecutable("wpctl", "/usr/bin/wpctl", "/bin/wpctl");

    internal static string? ResolvePwRecordExecutable()
        => ResolveExecutable("pw-record", "/usr/bin/pw-record", "/bin/pw-record");

    internal static string? ResolveParecExecutable()
        => ResolveExecutable("parec", "/usr/bin/parec", "/bin/parec");

    internal static string? ResolvePwDumpExecutable()
        => ResolveExecutable("pw-dump", "/usr/bin/pw-dump", "/bin/pw-dump");

    private static string? ResolveExecutable(string name, params string[] absoluteCandidates)
    {
        foreach (var candidate in absoluteCandidates.Prepend(name))
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathEnv))
                {
                    foreach (var entry in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var resolved = Path.Combine(entry, name);
                        if (File.Exists(resolved))
                            return resolved;
                    }
                }

                continue;
            }

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();
}
