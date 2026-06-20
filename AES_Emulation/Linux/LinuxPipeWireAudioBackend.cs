using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

/// <summary>
/// Lists and controls PipeWire playback streams via pactl (Pulse compat) or native pw-dump/wpctl.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxPipeWireAudioBackend
{
    private const int PulseVolumeNominal = 65536;

    private static readonly ILog Log = LogHelper.For(typeof(LinuxPipeWireAudioBackend));
    private static string? _activeListBackend;

    internal static bool TryListSinkInputsJson(out string json)
    {
        json = string.Empty;

        if (TryRunPactl("list sink-inputs", out json) && !string.IsNullOrWhiteSpace(json))
        {
            NoteListBackend("pactl");
            return true;
        }

        if (TryBuildSinkInputsJsonFromPwDump(out json))
        {
            NoteListBackend("pw-dump/wpctl");
            return true;
        }

        if (_activeListBackend == null)
            Log.Warn("EmulatorVolume: no audio backend available (install pulseaudio-utils/pactl or wireplumber wpctl).");

        return false;
    }

    internal static bool TryApplyStreamVolume(int streamId, float normalizedVolume)
    {
        if (TryApplyVolumeViaPactl(streamId, normalizedVolume))
            return true;

        return TryApplyVolumeViaWpctl(streamId, normalizedVolume);
    }

    private static void NoteListBackend(string backend)
    {
        if (string.Equals(_activeListBackend, backend, StringComparison.Ordinal))
            return;

        _activeListBackend = backend;
        Log.Info($"EmulatorVolume: using {backend} audio backend.");
    }

    private static bool TryBuildSinkInputsJsonFromPwDump(out string json)
    {
        json = string.Empty;
        if (!TryRunPwDump(out var output) || string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var streams = new List<Dictionary<string, object?>>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                if (!element.TryGetProperty("info", out var infoElement) ||
                    !infoElement.TryGetProperty("props", out var propsElement) ||
                    propsElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetPropString(propsElement, "media.class", out var mediaClass) ||
                    !string.Equals(mediaClass, "Stream/Output/Audio", StringComparison.Ordinal))
                {
                    continue;
                }

                var nodeId = idElement.GetInt32();
                var properties = ExtractStreamProperties(propsElement);
                TryGetStreamVolumeViaWpctl(nodeId, out var volumeNormalized, out var muted);

                streams.Add(new Dictionary<string, object?>
                {
                    ["index"] = nodeId,
                    ["mute"] = muted,
                    ["volume"] = new Dictionary<string, object>
                    {
                        ["mono"] = new Dictionary<string, object>
                        {
                            ["value"] = Math.Clamp(
                                (int)Math.Round(volumeNormalized * PulseVolumeNominal, MidpointRounding.AwayFromZero),
                                0,
                                PulseVolumeNominal),
                        },
                    },
                    ["properties"] = properties,
                });
            }

            json = JsonSerializer.Serialize(streams);
            return streams.Count > 0 || json == "[]";
        }
        catch (Exception ex)
        {
            Log.Warn("EmulatorVolume: failed to parse pw-dump Node output.", ex);
            return false;
        }
    }

    private static Dictionary<string, string> ExtractStreamProperties(JsonElement propsElement)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in propsElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(value))
                properties[property.Name] = value;
        }

        return properties;
    }

    private static bool TryGetPropString(JsonElement propsElement, string key, out string? value)
    {
        value = null;
        if (!propsElement.TryGetProperty(key, out var property))
            return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryApplyVolumeViaPactl(int streamId, float normalizedVolume)
    {
        var pactlPath = LinuxAudioEnvironmentHelper.ResolvePactlExecutable();
        if (string.IsNullOrWhiteSpace(pactlPath))
            return false;

        var clamped = Math.Clamp(normalizedVolume, 0.0f, 1.0f);
        var pulseVolume = Math.Clamp(
            (int)Math.Round(clamped * PulseVolumeNominal, MidpointRounding.AwayFromZero),
            0,
            PulseVolumeNominal);

        if (clamped > 0.001f &&
            !TryRunPactl($"set-sink-input-mute {streamId} 0"))
        {
            return false;
        }

        var pulseArg = pulseVolume.ToString(CultureInfo.InvariantCulture);
        if (!TryRunPactl($"set-sink-input-volume {streamId} {pulseArg}"))
            return false;

        if (clamped <= 0.001f &&
            !TryRunPactl($"set-sink-input-mute {streamId} 1"))
        {
            return false;
        }

        return true;
    }

    private static bool TryApplyVolumeViaWpctl(int streamId, float normalizedVolume)
    {
        var wpctlPath = LinuxAudioEnvironmentHelper.ResolveWpctlExecutable();
        if (string.IsNullOrWhiteSpace(wpctlPath))
            return false;

        var clamped = Math.Clamp(normalizedVolume, 0.0f, 1.0f);
        var volumeArg = clamped.ToString("0.####", CultureInfo.InvariantCulture);

        if (clamped > 0.001f)
        {
            if (!TryRunWpctl($"set-mute {streamId} 0"))
                return false;
        }

        if (!TryRunWpctl($"set-volume {streamId} {volumeArg}"))
            return false;

        if (clamped <= 0.001f &&
            !TryRunWpctl($"set-mute {streamId} 1"))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetStreamVolumeViaWpctl(int streamId, out float normalizedVolume, out bool muted)
    {
        normalizedVolume = 1.0f;
        muted = false;

        if (!TryRunWpctl($"get-volume {streamId}", out var output) || string.IsNullOrWhiteSpace(output))
            return false;

        return TryParseWpctlVolume(output, out normalizedVolume, out muted);
    }

    internal static bool TryParseWpctlVolume(string output, out float normalizedVolume, out bool muted)
    {
        normalizedVolume = 1.0f;
        muted = false;

        var trimmed = output.Trim();
        if (trimmed.Length == 0)
            return false;

        muted = trimmed.Contains("[MUTED]", StringComparison.OrdinalIgnoreCase);

        var volumePrefix = "Volume:";
        var start = trimmed.IndexOf(volumePrefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        start += volumePrefix.Length;
        var end = trimmed.IndexOf('[', start);
        var volumeText = (end >= 0 ? trimmed[start..end] : trimmed[start..]).Trim();

        if (!float.TryParse(volumeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            !float.TryParse(volumeText, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        normalizedVolume = Math.Clamp(parsed, 0.0f, 1.0f);
        return true;
    }

    private static bool TryRunPactl(string arguments)
        => TryRunPactl(arguments, out _);

    private static bool TryRunPactl(string arguments, out string output)
    {
        output = string.Empty;

        var pactlPath = LinuxAudioEnvironmentHelper.ResolvePactlExecutable();
        if (string.IsNullOrWhiteSpace(pactlPath))
            return false;

        return TryRunExecutable(pactlPath, $"-f json {arguments}", out output);
    }

    private static bool TryRunPwDump(out string output)
    {
        output = string.Empty;

        var pwDumpPath = LinuxAudioEnvironmentHelper.ResolvePwDumpExecutable();
        if (string.IsNullOrWhiteSpace(pwDumpPath))
            return false;

        return TryRunExecutable(pwDumpPath, "Node", out output);
    }

    private static bool TryRunWpctl(string arguments)
        => TryRunWpctl(arguments, out _);

    private static bool TryRunWpctl(string arguments, out string output)
    {
        output = string.Empty;

        var wpctlPath = LinuxAudioEnvironmentHelper.ResolveWpctlExecutable();
        if (string.IsNullOrWhiteSpace(wpctlPath))
            return false;

        return TryRunExecutable(wpctlPath, arguments, out output);
    }

    private static bool TryRunExecutable(string executablePath, string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            LinuxAudioEnvironmentHelper.Apply(startInfo);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    Log.Debug($"EmulatorVolume: '{Path.GetFileName(executablePath)} {arguments}' failed: {error.Trim()}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"EmulatorVolume: '{Path.GetFileName(executablePath)} {arguments}' failed.", ex);
            return false;
        }
    }
}
