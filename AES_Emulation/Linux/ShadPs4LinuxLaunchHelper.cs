using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.Linux;

[SupportedOSPlatform("linux")]
internal static class ShadPs4LinuxLaunchHelper
{
    private static readonly ILog Log = LogHelper.For(typeof(ShadPs4LinuxLaunchHelper));

    private const string DefaultDevice = "Default Device";

    /// <summary>
    /// Forces OpenAL on Linux. SDL audio fails inside gamescope headless, and shadPS4 prefers
    /// cwd/user over XDG when a portable user folder exists beside the AppImage.
    /// </summary>
    public static void EnsureLinuxAudioSettings(string? userDirectory, string? emulatorDirectory = null)
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (!string.IsNullOrWhiteSpace(userDirectory))
            TryPatchConfigInDirectory(userDirectory);

        if (!string.IsNullOrWhiteSpace(emulatorDirectory))
        {
            var portableUserDirectory = Path.Combine(emulatorDirectory, ShadPs4UserDirectoryHelper.PortableUserFolderName);
            if (Directory.Exists(portableUserDirectory))
                TryPatchConfigInDirectory(portableUserDirectory);
        }
    }

    private static void TryPatchConfigInDirectory(string userDirectory)
    {
        var configJsonPath = Path.Combine(userDirectory, "config.json");
        if (!File.Exists(configJsonPath))
            return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configJsonPath)) as JsonObject ?? new JsonObject();
            if (root["Audio"] is not JsonObject audio)
            {
                audio = new JsonObject();
                root["Audio"] = audio;
            }

            var modified = false;
            if (audio["audio_backend"]?.GetValue<int>() != 1)
            {
                audio["audio_backend"] = 1;
                modified = true;
            }

            foreach (var key in new[]
                     {
                         "openal_main_output_device",
                         "openal_mic_device",
                         "openal_padSpk_output_device",
                     })
            {
                if (!string.Equals(audio[key]?.GetValue<string>(), DefaultDevice, StringComparison.Ordinal))
                {
                    audio[key] = DefaultDevice;
                    modified = true;
                }
            }

            if (!modified)
                return;

            File.WriteAllText(configJsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"Patched shadPS4 Linux OpenAL audio settings in '{configJsonPath}'.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to patch shadPS4 Linux audio settings in '{configJsonPath}'.", ex);
        }
    }
}
