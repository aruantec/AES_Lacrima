using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Windows.API;

[SupportedOSPlatform("windows")]
public static class GameplayAudioDeviceEnumerator
{
    private const int EDataFlowRender = 0;
    private const int DeviceStateActive = 1;
    private const int RoleConsole = 0;

    public static IReadOnlyList<GameplayRecordingAudioDeviceItem> EnumerateRenderDevices()
    {
        var results = new List<GameplayRecordingAudioDeviceItem>();
        if (!OperatingSystem.IsWindows())
            return results;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlowRender, RoleConsole, out IMMDevice defaultDevice);
                defaultDevice.GetId(out defaultId);
            }
            catch
            {
            }

            results.Add(new GameplayRecordingAudioDeviceItem(
                string.Empty,
                "Default output device",
                true));

            int hr = enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out IntPtr collectionPtr);
            if (hr < 0 || collectionPtr == IntPtr.Zero)
                return results;

            try
            {
                var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr);
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out IMMDevice device);
                    device.GetId(out string id);
                    var isDefault = !string.IsNullOrEmpty(defaultId) &&
                                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                    var name = TryGetDeviceFriendlyName(device) ?? ShortDeviceId(id);
                    results.Add(new GameplayRecordingAudioDeviceItem(id, name, isDefault));
                }
            }
            finally
            {
                Marshal.Release(collectionPtr);
            }
        }
        catch
        {
        }

        return results;
    }

    private static string? TryGetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0, out IntPtr storePtr);
            if (storePtr == IntPtr.Zero)
                return null;

            try
            {
                var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
                var key = new PropertyKey
                {
                    fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                    pid = 14
                };
                store.GetValue(ref key, out PropVariant value);
                try
                {
                    return value.Value as string;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.Release(storePtr);
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

    [ComImport]
    [Guid("0BD7A1BE-7A7A-4D66-9B64-6A7B8E8D4D8B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;

        public object? Value
        {
            get
            {
                if (vt == 31 && pointerValue != IntPtr.Zero) // VT_LPWSTR
                    return Marshal.PtrToStringUni(pointerValue);
                return null;
            }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
