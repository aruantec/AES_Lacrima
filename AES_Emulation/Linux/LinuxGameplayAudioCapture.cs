using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using AES_Core.Logging;
using AES_Emulation.Services;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Captures PCM audio for gameplay recording via PipeWire/PulseAudio CLI tools.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGameplayAudioCapture : IDisposable
{
    private static readonly ILog Log = LogHelper.For<LinuxGameplayAudioCapture>();
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

    private Process? _captureProcess;
    private Stream? _stdout;
    private bool _isCapturing;

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Resolves a PulseAudio/PipeWire source name for FFmpeg's pulse input.
    /// </summary>
    public static string? ResolvePulseInputForRecording(
        GameplayRecordingAudioSource source,
        int processId,
        string? deviceId)
    {
        if (source == GameplayRecordingAudioSource.None)
            return null;

        if (source == GameplayRecordingAudioSource.OutputDevice)
            return string.IsNullOrWhiteSpace(deviceId) ? "@DEFAULT_MONITOR@" : deviceId;

        if (processId > 0 && TryResolveMonitorSourceForProcess(processId, out var monitor))
            return monitor;

        return "@DEFAULT_MONITOR@";
    }

    public static void ApplyAudioEnvironment(ProcessStartInfo startInfo) => CopyAudioEnvironment(startInfo);

    public static bool CanCapturePulse()
    {
        if (!IsSupported)
            return false;

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/pactl",
                Arguments = "info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            CopyAudioEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            process.WaitForExit(1500);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture pulse availability check failed.", ex);
            return false;
        }
    }

    public int SampleRate { get; private set; } = 48_000;
    public int Channels { get; private set; } = 2;
    public int BitsPerSample { get; private set; } = 16;
    public int BytesPerSample => Channels * BitsPerSample / 8;

    public bool TryStart(GameplayRecordingAudioSource source, int processId, string? deviceId)
    {
        if (!IsSupported || source == GameplayRecordingAudioSource.None)
            return false;

        Stop();

        ProcessStartInfo? startInfo = source switch
        {
            GameplayRecordingAudioSource.OutputDevice => BuildMonitorCaptureStartInfo(deviceId),
            GameplayRecordingAudioSource.Application or GameplayRecordingAudioSource.EmulatorProcess =>
                BuildProcessCaptureStartInfo(processId),
            _ => null,
        };

        if (startInfo == null)
            return false;

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
                return false;

            _stdout = process.StandardOutput.BaseStream;
            _captureProcess = process;
            _isCapturing = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("LinuxGameplayAudioCapture failed to start.", ex);
            Stop();
            return false;
        }
    }

    public int Read(byte[] buffer)
    {
        if (!_isCapturing || _stdout == null || buffer.Length == 0)
            return 0;

        try
        {
            return _stdout.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture read failed.", ex);
            return 0;
        }
    }

    public void Stop()
    {
        _isCapturing = false;

        try
        {
            _stdout?.Dispose();
        }
        catch
        {
            // ignored
        }

        _stdout = null;

        if (_captureProcess == null)
            return;

        try
        {
            if (!_captureProcess.HasExited)
                _captureProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Debug("LinuxGameplayAudioCapture failed to stop capture process.", ex);
        }
        finally
        {
            try { _captureProcess.Dispose(); } catch { /* ignored */ }
            _captureProcess = null;
        }
    }

    public void Dispose() => Stop();

    private static ProcessStartInfo? BuildMonitorCaptureStartInfo(string? deviceId)
    {
        var monitor = string.IsNullOrWhiteSpace(deviceId) ? "@DEFAULT_MONITOR@" : deviceId;
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/parec",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--device=" + monitor);
        startInfo.ArgumentList.Add("--format=s16le");
        startInfo.ArgumentList.Add("--rate=48000");
        startInfo.ArgumentList.Add("--channels=2");
        CopyAudioEnvironment(startInfo);
        return startInfo;
    }

    private static ProcessStartInfo? BuildProcessCaptureStartInfo(int processId)
    {
        if (processId <= 0)
            return null;

        if (!TryResolveSinkInputTarget(processId, out var target))
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/pw-record",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("--rate=48000");
        startInfo.ArgumentList.Add("--channels=2");
        startInfo.ArgumentList.Add("-");
        CopyAudioEnvironment(startInfo);
        return startInfo;
    }

    private static bool TryResolveSinkInputTarget(int processId, out string target)
    {
        target = string.Empty;
        if (processId <= 0)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/pactl",
                Arguments = "-f json list sink-inputs",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            CopyAudioEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("properties", out var properties))
                    continue;

                if (!TryGetProcessId(properties, out var pid) || pid != processId)
                    continue;

                if (properties.TryGetProperty("node.name", out var nodeName) &&
                    nodeName.ValueKind == JsonValueKind.String)
                {
                    var name = nodeName.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        target = name;
                        return true;
                    }
                }

                if (element.TryGetProperty("index", out var indexElement) &&
                    indexElement.ValueKind == JsonValueKind.Number)
                {
                    target = indexElement.GetInt32().ToString();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"LinuxGameplayAudioCapture failed to resolve sink input for pid={processId}.", ex);
        }

        return false;
    }

    private static bool TryResolveMonitorSourceForProcess(int processId, out string monitorSource)
    {
        monitorSource = string.Empty;
        if (processId <= 0)
            return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/pactl",
                Arguments = "-f json list sink-inputs",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            CopyAudioEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("properties", out var properties))
                    continue;

                if (!TryGetProcessId(properties, out var pid) || pid != processId)
                    continue;

                if (!element.TryGetProperty("sink", out var sinkElement))
                    continue;

                var sinkIndex = sinkElement.ValueKind == JsonValueKind.Number
                    ? sinkElement.GetInt32()
                    : -1;

                if (sinkIndex < 0)
                    continue;

                return TryResolveMonitorForSinkIndex(sinkIndex, out monitorSource);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"LinuxGameplayAudioCapture failed to resolve monitor for pid={processId}.", ex);
        }

        return false;
    }

    private static bool TryResolveMonitorForSinkIndex(int sinkIndex, out string monitorSource)
    {
        monitorSource = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/pactl",
                Arguments = "-f json list sinks",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            CopyAudioEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return false;

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number ||
                    indexElement.GetInt32() != sinkIndex)
                {
                    continue;
                }

                if (element.TryGetProperty("monitor_source", out var monitorElement) &&
                    monitorElement.ValueKind == JsonValueKind.String)
                {
                    var name = monitorElement.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        monitorSource = name;
                        return true;
                    }
                }

                if (element.TryGetProperty("name", out var sinkNameElement) &&
                    sinkNameElement.ValueKind == JsonValueKind.String)
                {
                    var sinkName = sinkNameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(sinkName))
                    {
                        monitorSource = sinkName + ".monitor";
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"LinuxGameplayAudioCapture failed to resolve monitor for sink={sinkIndex}.", ex);
        }

        return false;
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

    private static void CopyAudioEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in AudioEnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[key] = value;
        }
    }
}
