using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AES_Lacrima.Serialization;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services.ShadPs4;

public sealed record ShadPs4GpuOption(int GpuId, string Label);

public static class ShadPs4CustomConfigService
{
    private static readonly ILog Log = LogHelper.For(typeof(ShadPs4CustomConfigService));
    public static readonly IReadOnlyList<string> ResolutionPresets =
    [
        "960 x 540",
        "1280 x 720",
        "1366 x 768",
        "1600 x 900",
        "1920 x 1080",
        "2560 x 1440",
        "3840 x 2160"
    ];

    public static readonly IReadOnlyList<string> AudioBackendLabels = ["SDL", "OpenAL"];

    public static readonly IReadOnlyList<string> FullScreenModeLabels =
    [
        "Windowed",
        "Fullscreen",
        "Fullscreen (Borderless)"
    ];

    public static readonly IReadOnlyList<string> PresentModeLabels =
    [
        "Mailbox (Vsync)",
        "Fifo (Vsync)",
        "Immediate (No Vsync)"
    ];

    public static readonly IReadOnlyList<string> PresentModeValues = ["Mailbox", "Fifo", "Immediate"];

    public static readonly IReadOnlyList<string> ReadbacksModeLabels = ["Disabled", "Relaxed", "Precise"];

    public static readonly IReadOnlyList<string> CursorStateLabels = ["Never", "Idle", "Always"];

    public static readonly IReadOnlyList<string> UsbDeviceBackendLabels =
    [
        "Real USB Device",
        "Skylander Portal",
        "Infinity Base",
        "Dimensions Toypad"
    ];

    public static readonly IReadOnlyList<string> TrophyNotificationSideLabels = ["Left", "Right", "Top", "Bottom"];

    public static readonly IReadOnlyList<string> TrophyNotificationSideValues = ["left", "right", "top", "bottom"];

    public static readonly IReadOnlyList<string> LogTypeLabels = ["wincolor", "msvc"];

    public static string GetCustomConfigsDirectory(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "user", "custom_configs");
    }

    public static string GetConfigFilePath(string? emulatorDirectory, string titleId)
    {
        var directory = GetCustomConfigsDirectory(emulatorDirectory);
        return Path.Combine(directory, $"{titleId.ToUpperInvariant()}.json");
    }

    public static string GetGlobalConfigPath(string? emulatorDirectory)
    {
        if (string.IsNullOrWhiteSpace(emulatorDirectory))
            return string.Empty;

        return Path.Combine(emulatorDirectory, "user", "config.json");
    }

    public static ShadPs4CustomConfigDocument CreateDefault() => new();

    public static ShadPs4CustomConfigDocument LoadOrDefault(string? emulatorDirectory, string titleId)
    {
        var path = GetConfigFilePath(emulatorDirectory, titleId);
        if (!File.Exists(path))
            return CreateDefault();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.ShadPs4CustomConfigDocument) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static void Save(string? emulatorDirectory, string titleId, ShadPs4CustomConfigDocument document)
    {
        var path = GetConfigFilePath(emulatorDirectory, titleId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(document, ShadPs4JsonContext.Default.ShadPs4CustomConfigDocument);
        File.WriteAllText(path, json);
    }

    public static string FormatResolution(int width, int height) => $"{width} x {height}";

    public static bool TryParseResolution(string? preset, out int width, out int height)
    {
        width = 1280;
        height = 720;

        if (string.IsNullOrWhiteSpace(preset))
            return false;

        var parts = preset.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }

    public static string FindMatchingResolutionPreset(int width, int height)
    {
        var formatted = FormatResolution(width, height);
        return ResolutionPresets.FirstOrDefault(preset =>
            string.Equals(preset, formatted, StringComparison.OrdinalIgnoreCase)) ?? formatted;
    }

    public static string PresentModeLabelForValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return PresentModeValues[2];

        var index = PresentModeValues
            .Select((mode, idx) => (mode, idx))
            .FirstOrDefault(entry => string.Equals(entry.mode, value, StringComparison.OrdinalIgnoreCase))
            .idx;

        return index >= 0 && index < PresentModeLabels.Count
            ? PresentModeLabels[index]
            : value;
    }

    public static string PresentModeValueForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return PresentModeValues[2];

        var index = PresentModeLabels
            .Select((mode, idx) => (mode, idx))
            .FirstOrDefault(entry => string.Equals(entry.mode, label, StringComparison.OrdinalIgnoreCase))
            .idx;

        return index >= 0 && index < PresentModeValues.Count
            ? PresentModeValues[index]
            : label;
    }

    public static string TrophySideLabelForValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TrophyNotificationSideLabels[1];

        var index = TrophyNotificationSideValues
            .Select((side, idx) => (side, idx))
            .FirstOrDefault(entry => string.Equals(entry.side, value, StringComparison.OrdinalIgnoreCase))
            .idx;

        return index >= 0 && index < TrophyNotificationSideLabels.Count
            ? TrophyNotificationSideLabels[index]
            : value;
    }

    public static string TrophySideValueForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return TrophyNotificationSideValues[1];

        var index = TrophyNotificationSideLabels
            .Select((side, idx) => (side, idx))
            .FirstOrDefault(entry => string.Equals(entry.side, label, StringComparison.OrdinalIgnoreCase))
            .idx;

        return index >= 0 && index < TrophyNotificationSideValues.Count
            ? TrophyNotificationSideValues[index]
            : label.ToLowerInvariant();
    }

    public static IReadOnlyList<string> GetAudioDeviceNames(string? emulatorDirectory)
    {
        var devices = new HashSet<string>(EnumerateAudioDevicesWithConfigFallback(emulatorDirectory), StringComparer.OrdinalIgnoreCase);
        var ordered = devices
            .Where(static name => !string.Equals(name, ShadPs4HardwareEnumeration.DefaultAudioDeviceLabel, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ordered.Insert(0, ShadPs4HardwareEnumeration.DefaultAudioDeviceLabel);
        return ordered;
    }

    public static IReadOnlyList<ShadPs4GpuOption> GetGpuOptions() => ShadPs4HardwareEnumeration.EnumerateGpuAdapters();

    private static IEnumerable<string> EnumerateAudioDevicesWithConfigFallback(string? emulatorDirectory)
    {
        var devices = new HashSet<string>(ShadPs4HardwareEnumeration.EnumerateAudioDevices(), StringComparer.OrdinalIgnoreCase);

        try
        {
            var globalConfigPath = GetGlobalConfigPath(emulatorDirectory);
            if (!File.Exists(globalConfigPath))
                return devices;

            var root = JsonNode.Parse(File.ReadAllText(globalConfigPath)) as JsonObject;
            var audio = root?["Audio"] as JsonObject;
            if (audio == null)
                return devices;

            foreach (var property in audio)
            {
                if (property.Value is not JsonValue value)
                    continue;

                var text = value.GetValue<string?>();
                if (!string.IsNullOrWhiteSpace(text))
                    devices.Add(text);
            }
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return devices;
    }
}
