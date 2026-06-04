using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.Logging;
using log4net;
using Microsoft.Win32;

namespace AES_Emulation.Windows.API;

[SupportedOSPlatform("windows")]
public static class GameplayAudioDeviceEnumerator
{
    private static readonly ILog Log = LogHelper.For(typeof(GameplayAudioDeviceEnumerator));

    private const int EDataFlowRender = 0;
    private const int DeviceStateActive = 1;
    private const int RoleConsole = 0;
    private const string DefaultDeviceLabel = "Default output device";
    private const string RenderDevicesRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string DeviceFriendlyNamePropertyKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";
    private const string DeviceFriendlyNamePropertyKeyAlt = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MMDeviceEnumerator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IMMDeviceEnumerator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IMMDeviceCollection))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IMMDevice))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPropertyStore))]
    public static IReadOnlyList<GameplayRecordingAudioDeviceItem> EnumerateRenderDevices()
    {
        var results = new List<GameplayRecordingAudioDeviceItem>();
        if (!OperatingSystem.IsWindows())
            return results;

        results.Add(new GameplayRecordingAudioDeviceItem(string.Empty, DefaultDeviceLabel, true));

        if (TryEnumerateViaCoreAudio(results))
            return results;

        Log.Warn("Core Audio device enumeration failed; falling back to registry names.");
        TryEnumerateViaRegistry(results);
        return results;
    }

    private static bool TryEnumerateViaCoreAudio(List<GameplayRecordingAudioDeviceItem> results)
    {
        try
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
            string? defaultId = null;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlowRender, RoleConsole, out IMMDevice defaultDevice);
                defaultDevice.GetId(out defaultId);
            }
            catch (Exception ex)
            {
                Log.Debug("Default audio endpoint lookup failed.", ex);
            }

            int hr = enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out IMMDeviceCollection collection);
            if (hr < 0 || collection == null)
                return false;

            if (collection.GetCount(out uint count) < 0)
                return false;

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) < 0 || device == null)
                    continue;

                device.GetId(out string id);
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                    continue;

                var isDefault = !string.IsNullOrEmpty(defaultId) &&
                                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                var name = TryGetDeviceFriendlyName(device) ?? ShortDeviceId(id);
                results.Add(new GameplayRecordingAudioDeviceItem(id, name, isDefault));
            }

            return results.Count > 1;
        }
        catch (Exception ex)
        {
            Log.Warn("Core Audio render device enumeration failed.", ex);
            return false;
        }
    }

    private static void TryEnumerateViaRegistry(List<GameplayRecordingAudioDeviceItem> results)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RenderDevicesRegistryPath);
            if (root == null)
                return;

            foreach (var deviceKey in root.GetSubKeyNames())
            {
                if (string.IsNullOrWhiteSpace(deviceKey))
                    continue;

                using var properties = root.OpenSubKey($"{deviceKey}\\Properties");
                if (properties == null)
                    continue;

                var name = ReadRegistryFriendlyName(properties);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var id = BuildEndpointId(deviceKey);
                if (results.Exists(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                results.Add(new GameplayRecordingAudioDeviceItem(id, name, false));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Registry audio device enumeration failed.", ex);
        }
    }

    private static string? ReadRegistryFriendlyName(RegistryKey properties)
    {
        foreach (var key in new[] { DeviceFriendlyNamePropertyKey, DeviceFriendlyNamePropertyKeyAlt })
        {
            if (properties.GetValue(key) is not byte[] data || data.Length < 4)
                continue;

            var name = ParsePropVariantString(data);
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }

        return null;
    }

    private static string? ParsePropVariantString(byte[] data)
    {
        if (data.Length < 4)
            return null;

        if (BitConverter.ToUInt16(data, 0) == 31)
            return ReadUnicodeString(data, 2);

        foreach (var offset in new[] { 4, 8, 12 })
        {
            if (data.Length <= offset + 2)
                continue;

            var candidate = ReadUnicodeString(data, offset);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private static string? ReadUnicodeString(byte[] data, int offset)
    {
        if (offset >= data.Length)
            return null;

        var end = offset;
        while (end + 1 < data.Length)
        {
            if (data[end] == 0 && data[end + 1] == 0)
                break;
            end += 2;
        }

        if (end <= offset)
            return null;

        return System.Text.Encoding.Unicode.GetString(data, offset, end - offset).Trim();
    }

    private static string BuildEndpointId(string registryDeviceKey) =>
        $"{{0.0.0.00000000}}.{registryDeviceKey}";

    private static string? TryGetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0, out IPropertyStore store);
            if (store == null)
                return null;

            var key = CoreAudioConstants.DeviceFriendlyNameKey;
            store.GetValue(ref key, out PropVariant value);
            try
            {
                return value.GetString();
            }
            finally
            {
                PropVariantHelper.PropVariantClear(ref value);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string ShortDeviceId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Unknown device";

        var brace = id.IndexOf('{');
        return brace >= 0 ? id[brace..] : id;
    }
}
