using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

using log4net;
using AES_Core.Logging;
namespace AES_Lacrima.Services.ShadPs4;

[SupportedOSPlatform("windows")]
internal static class ShadPs4WinMmAudioEnumeration
{
    private static readonly ILog Log = LogHelper.For(typeof(ShadPs4WinMmAudioEnumeration));
    private const uint MmsyserrNoerror = 0;
    private const ushort VtLpwstr = 0x001F;
    private const string DeviceFriendlyNamePropertyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved1;
        public uint Support;
        public ushort Reserved2;
        public ushort Reserved3;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved1;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveOutGetDevCapsW(uint deviceId, ref WaveOutCaps caps, uint capsSize);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveInGetDevCapsW(uint deviceId, ref WaveInCaps caps, uint capsSize);

    public static void AddDevices(HashSet<string> devices)
    {
        AddWaveOutDevices(devices);
        AddWaveInDevices(devices);
        AddMmDeviceRegistryNames(devices);
    }

    private static void AddWaveOutDevices(HashSet<string> devices)
    {
        var count = waveOutGetNumDevs();
        var capsSize = (uint)Marshal.SizeOf<WaveOutCaps>();

        for (uint index = 0; index < count; index++)
        {
            var caps = default(WaveOutCaps);
            if (waveOutGetDevCapsW(index, ref caps, capsSize) != MmsyserrNoerror)
                continue;

            AddName(devices, caps.DeviceName);
        }
    }

    private static void AddWaveInDevices(HashSet<string> devices)
    {
        var count = waveInGetNumDevs();
        var capsSize = (uint)Marshal.SizeOf<WaveInCaps>();

        for (uint index = 0; index < count; index++)
        {
            var caps = default(WaveInCaps);
            if (waveInGetDevCapsW(index, ref caps, capsSize) != MmsyserrNoerror)
                continue;

            AddName(devices, caps.DeviceName);
        }
    }

    private static void AddMmDeviceRegistryNames(HashSet<string> devices)
    {
        AddMmDeviceRegistryNames(devices, @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
        AddMmDeviceRegistryNames(devices, @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
    }

    private static void AddMmDeviceRegistryNames(HashSet<string> devices, string registryPath)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(registryPath);
            if (root == null)
                return;

            foreach (var deviceId in root.GetSubKeyNames())
            {
                try
                {
                    using var properties = root.OpenSubKey($"{deviceId}\\Properties");
                    if (properties == null)
                        continue;

                    var propertyBytes = properties.GetValue(DeviceFriendlyNamePropertyKey) as byte[];
                    AddName(devices, ParsePropVariantString(propertyBytes));
                }
                catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            }
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
    }

    private static string? ParsePropVariantString(byte[]? data)
    {
        if (data == null || data.Length < 4)
            return null;

        var variantType = BitConverter.ToUInt16(data, 0);
        if (variantType == VtLpwstr)
            return ReadUnicodeString(data, 2);

        foreach (var offset in new[] { 4, 8, 12 })
        {
            if (data.Length <= offset + 2)
                continue;

            var candidate = ReadUnicodeString(data, offset);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return ReadFirstUnicodeString(data);
    }

    private static string? ReadUnicodeString(byte[] data, int offset)
    {
        if (offset >= data.Length)
            return null;

        try
        {
            var length = data.Length - offset;
            var end = offset;
            while (end + 1 < data.Length)
            {
                if (data[end] == 0 && data[end + 1] == 0)
                    break;

                end += 2;
            }

            if (end <= offset)
                return null;

            return Encoding.Unicode.GetString(data, offset, end - offset).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadFirstUnicodeString(byte[] data)
    {
        for (var offset = 0; offset < data.Length - 3; offset += 2)
        {
            if (data[offset] == 0x1F && data[offset + 1] == 0)
                continue;

            if (data[offset] >= 0x20 || data[offset + 1] >= 0x20)
            {
                var candidate = ReadUnicodeString(data, offset);
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length >= 3)
                    return candidate;
            }
        }

        return null;
    }

    private static void AddName(HashSet<string> devices, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return;

        devices.Add(trimmed);
    }
}
