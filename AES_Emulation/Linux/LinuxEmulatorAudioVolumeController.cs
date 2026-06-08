using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Adjusts PipeWire/PulseAudio sink-input volume for the active emulator in a gamescope session.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxEmulatorAudioVolumeController : IDisposable
{
    private const int PulseVolumeNominal = 65536;

    private static readonly ILog Log = LogHelper.For<LinuxEmulatorAudioVolumeController>();
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string[] AudioEnvironmentKeys =
    [
        "PATH",
        "XDG_RUNTIME_DIR",
        "PULSE_RUNTIME_PATH",
        "PULSE_SERVER",
        "DBUS_SESSION_BUS_ADDRESS",
        "WAYLAND_DISPLAY",
        "DISPLAY",
        "HOME",
        "USER",
    ];

    private int _launchedCompositorPid;
    private float _volume = 1.0f;
    private bool _disposed;
    private bool _pushPending;
    private Timer? _syncTimer;
    private int _activeSinkInputIndex;
    private DateTime _lastUserPushUtc = DateTime.MinValue;
    private static readonly TimeSpan SyncGracePeriod = TimeSpan.FromSeconds(1.5);

    public bool IsAttached => _launchedCompositorPid > 0;

    public event Action<float>? SystemVolumeChanged;

    public void Attach(int launchedCompositorPid, params int[] additionalPids)
    {
        Detach();
        if (launchedCompositorPid <= 0)
            return;

        _launchedCompositorPid = LinuxCompositorProcessHelper.ResolveCompositorRootPid(launchedCompositorPid);
        if (_launchedCompositorPid <= 0)
            _launchedCompositorPid = launchedCompositorPid;

        Log.Info(
            $"EmulatorVolume: attached launchedPid={launchedCompositorPid}, compositorRoot={_launchedCompositorPid}.");

        _pushPending = true;
        StartSyncTimer();
        TrySyncAndApply(forcePush: true);
    }

    public void Detach()
    {
        StopSyncTimer();
        _launchedCompositorPid = 0;
        _activeSinkInputIndex = 0;
        _pushPending = false;
    }

    public void EnsureSession()
    {
        if (_launchedCompositorPid <= 0)
            return;

        _pushPending = true;
        TrySyncAndApply(forcePush: true);
    }

    public float Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0f, 1.0f);
            _volume = clamped;
            _pushPending = true;
            TrySyncAndApply(forcePush: true);
        }
    }

    private void StartSyncTimer()
    {
        StopSyncTimer();
        _syncTimer = new Timer(_ => TrySyncAndApply(forcePush: false), null, SyncInterval, SyncInterval);
    }

    private void StopSyncTimer()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
    }

    private void TrySyncAndApply(bool forcePush)
    {
        if (_launchedCompositorPid <= 0)
            return;

        if (!TryResolveActiveSinkInput(out var sinkIndex, out var sinkVolume, out var muted))
        {
            if (_pushPending)
                Log.Debug("EmulatorVolume: active sink input not found yet, will retry.");
            return;
        }

        _activeSinkInputIndex = sinkIndex;

        if (_pushPending || forcePush)
        {
            if (TryApplyVolumeToSink(sinkIndex, _volume))
            {
                _pushPending = false;
                _lastUserPushUtc = DateTime.UtcNow;
                Log.Info(
                    $"EmulatorVolume: pushed {FormatVolumePercent(_volume)} to sink-input #{sinkIndex}.");
            }
            else
            {
                Log.Warn(
                    $"EmulatorVolume: failed to push {FormatVolumePercent(_volume)} to sink-input #{sinkIndex}.");
            }

            return;
        }

        if (DateTime.UtcNow - _lastUserPushUtc < SyncGracePeriod)
            return;

        var systemVolume = muted ? 0.0f : sinkVolume;
        if (Math.Abs(systemVolume - _volume) > 0.01f)
        {
            _volume = systemVolume;
            SystemVolumeChanged?.Invoke(_volume);
        }
    }

    private bool TryApplyVolumeToSink(int sinkIndex, float normalizedVolume)
    {
        var clamped = Math.Clamp(normalizedVolume, 0.0f, 1.0f);
        var pulseVolume = Math.Clamp(
            (int)Math.Round(clamped * PulseVolumeNominal, MidpointRounding.AwayFromZero),
            0,
            PulseVolumeNominal);

        // Unmute before setting level so intermediate values work after hitting 0.
        if (clamped > 0.001f &&
            !TryRunPactl($"set-sink-input-mute {sinkIndex} 0"))
        {
            return false;
        }

        var pulseArg = pulseVolume.ToString(CultureInfo.InvariantCulture);
        if (!TryRunPactl($"set-sink-input-volume {sinkIndex} {pulseArg}"))
            return false;

        if (clamped <= 0.001f &&
            !TryRunPactl($"set-sink-input-mute {sinkIndex} 1"))
            return false;

        return true;
    }

    private static string FormatVolumePercent(float normalizedVolume)
        => $"{Math.Round(normalizedVolume * 100.0, 1).ToString(CultureInfo.InvariantCulture)}%";

    private bool TryResolveActiveSinkInput(out int sinkIndex, out float volumeNormalized, out bool muted)
    {
        sinkIndex = 0;
        volumeNormalized = _volume;
        muted = false;

        var treePids = new HashSet<int>();
        LinuxCompositorProcessHelper.CollectCompositorTreePids(_launchedCompositorPid, treePids);
        if (treePids.Count == 0)
            return false;

        if (!TryRunPactl("list sink-inputs", out var output) || string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var bestIndex = 0;
            var bestSerial = long.MinValue;
            var found = false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number)
                    continue;

                if (!element.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryGetProcessId(properties, out var pid) || !treePids.Contains(pid))
                    continue;

                if (!IsProcessAlive(pid))
                    continue;

                var serial = TryGetObjectSerial(properties);
                if (serial >= bestSerial)
                {
                    bestSerial = serial;
                    bestIndex = indexElement.GetInt32();
                    volumeNormalized = TryReadVolumeNormalized(element);
                    muted = element.TryGetProperty("mute", out var muteElement) &&
                            muteElement.ValueKind == JsonValueKind.True;
                    found = true;
                }
            }

            if (!found)
            {
                Log.Debug(
                    $"EmulatorVolume: no sink input matched compositor tree pids=[{string.Join(", ", treePids)}].");
                return false;
            }

            sinkIndex = bestIndex;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("EmulatorVolume: failed to parse pactl sink-input JSON.", ex);
            return false;
        }
    }

    private static float TryReadVolumeNormalized(JsonElement sinkInput)
    {
        if (!sinkInput.TryGetProperty("volume", out var volumeElement) ||
            volumeElement.ValueKind != JsonValueKind.Object)
            return 1.0f;

        foreach (var channel in volumeElement.EnumerateObject())
        {
            if (!channel.Value.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Number)
                continue;

            return Math.Clamp(valueElement.GetInt32() / (float)PulseVolumeNominal, 0.0f, 1.0f);
        }

        return 1.0f;
    }

    private static long TryGetObjectSerial(JsonElement properties)
    {
        if (!properties.TryGetProperty("object.serial", out var serialProperty))
            return 0;

        return serialProperty.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(serialProperty.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when serialProperty.TryGetInt64(out var number) => number,
            _ => 0,
        };
    }

    private static bool IsProcessAlive(int pid)
        => pid > 0 && Directory.Exists($"/proc/{pid}");

    private static bool TryGetProcessId(JsonElement properties, out int pid)
    {
        pid = 0;
        if (!properties.TryGetProperty("application.process.id", out var pidProperty))
            return false;

        return pidProperty.ValueKind switch
        {
            JsonValueKind.String => int.TryParse(pidProperty.GetString(), out pid),
            JsonValueKind.Number => pidProperty.TryGetInt32(out pid),
            _ => false,
        };
    }

    private static bool TryRunPactl(string arguments)
        => TryRunPactl(arguments, out _);

    private static bool TryRunPactl(string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/pactl",
                Arguments = $"-f json {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            CopyAudioEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Log.Warn($"EmulatorVolume: pactl '{arguments}' failed: {error.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"EmulatorVolume: pactl '{arguments}' failed.", ex);
            return false;
        }
    }

    private static void CopyAudioEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in AudioEnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Detach();
    }
}
