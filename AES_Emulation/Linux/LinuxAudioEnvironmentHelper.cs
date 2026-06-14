using System;
using System.Diagnostics;
using System.IO;
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

    public static void Apply(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var key in KeysToPropagate)
        {
            var value = Resolve(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }

        if (!startInfo.Environment.ContainsKey("SDL_AUDIODRIVER"))
            startInfo.Environment["SDL_AUDIODRIVER"] = "pipewire";
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

    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();
}
