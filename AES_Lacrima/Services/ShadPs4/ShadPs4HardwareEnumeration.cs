using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AES_Core.Logging;
using log4net;
using Vortice.DXGI;

namespace AES_Lacrima.Services.ShadPs4;

public static class ShadPs4HardwareEnumeration
{
    private static readonly ILog Log = LogHelper.For(typeof(ShadPs4HardwareEnumeration));

    /// <summary>
    /// shadPS4 qt-launcher uses <c>gpu_id = -1</c> for Auto Select
    /// (combo index 0 maps to <c>currentIndex() - 1</c> when saving).
    /// </summary>
    public const int AutoSelectGpuId = -1;

    public const string AutoSelectGpuLabel = "Auto Select";
    public const string DefaultAudioDeviceLabel = "Default Device";

    public static IReadOnlyList<ShadPs4GpuOption> EnumerateGpuAdapters()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [new ShadPs4GpuOption(AutoSelectGpuId, AutoSelectGpuLabel)];

        return ShadPs4WindowsInterop.RunOnStaThread(EnumerateWindowsGpuAdapters);
    }

    public static IReadOnlyList<string> EnumerateAudioDevices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [DefaultAudioDeviceLabel];

        return EnumerateWindowsAudioDevices();
    }

    private static IReadOnlyList<ShadPs4GpuOption> EnumerateWindowsGpuAdapters()
    {
        var options = new List<ShadPs4GpuOption> { new(AutoSelectGpuId, AutoSelectGpuLabel) };
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gpuIndex = 0;

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var result = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (result.Failure)
                    break;

                using (adapter)
                {
                    var description = adapter.Description1.Description.Trim();
                    if (string.IsNullOrWhiteSpace(description))
                        continue;

                    if (description.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase) ||
                        description.Contains("Microsoft Remote Display Adapter", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seenNames.Add(description))
                        continue;

                    options.Add(new ShadPs4GpuOption(gpuIndex++, description));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DXGI GPU enumeration failed; falling back to WMI.", ex);
            AppendWmiGpuAdapters(options, seenNames, ref gpuIndex);
        }

        if (options.Count == 1)
            AppendWmiGpuAdapters(options, seenNames, ref gpuIndex);

        return options;
    }

    private static void AppendWmiGpuAdapters(
        List<ShadPs4GpuOption> options,
        HashSet<string> seenNames,
        ref int gpuIndex)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController WHERE Name IS NOT NULL");

            foreach (var managementObject in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                using (managementObject)
                {
                    var name = managementObject["Name"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (name.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Microsoft Remote Display Adapter", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seenNames.Add(name))
                        continue;

                    options.Add(new ShadPs4GpuOption(gpuIndex++, name));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WMI GPU enumeration failed.", ex);
        }
    }

    private static IReadOnlyList<string> EnumerateWindowsAudioDevices()
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DefaultAudioDeviceLabel
        };

        try
        {
            ShadPs4WinMmAudioEnumeration.AddDevices(devices);
        }
        catch (Exception ex)
        {
            Log.Warn("Windows audio device enumeration failed.", ex);
        }

        if (devices.Count == 1)
            Log.Warn("No Windows audio endpoints were discovered; only the default entry is available.");

        return devices.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
