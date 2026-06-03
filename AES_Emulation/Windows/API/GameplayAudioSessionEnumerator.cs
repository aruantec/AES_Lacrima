using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Emulation.Windows.API;

[SupportedOSPlatform("windows")]
public static class GameplayAudioSessionEnumerator
{
    private const int EDataFlowRender = 0;
    private const int DeviceStateActive = 1;

    public static IReadOnlyList<GameplayRecordingAudioSessionItem> EnumerateActiveSessions()
    {
        var results = new List<GameplayRecordingAudioSessionItem>();
        var seen = new HashSet<int>();
        if (!OperatingSystem.IsWindows())
            return results;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
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
                    CollectSessionsFromDevice(device, results, seen);
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

        results.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static void CollectSessionsFromDevice(
        IMMDevice device,
        List<GameplayRecordingAudioSessionItem> results,
        HashSet<int> seen)
    {
        var sessionManager = EmulatorAudioVolumeController.TryActivateSessionManager(device);
        if (sessionManager == null)
            return;

        sessionManager.GetAudioSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        sessionEnum.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            sessionEnum.GetSession(i, out IAudioSessionControl sessionControl);
            if (sessionControl is not IAudioSessionControl2 session2)
                continue;

            try
            {
                session2.GetProcessId(out uint pidRaw);
                var pid = (int)pidRaw;
                if (pid <= 0 || !seen.Add(pid))
                    continue;

                string displayName;
                try
                {
                    sessionControl.GetDisplayName(out string? sessionName);
                    displayName = !string.IsNullOrWhiteSpace(sessionName)
                        ? sessionName
                        : TryProcessName(pid);
                }
                catch
                {
                    displayName = TryProcessName(pid);
                }

                results.Add(new GameplayRecordingAudioSessionItem(pid, $"{displayName} (PID {pid})"));
            }
            catch (COMException)
            {
            }
        }
    }

    private static string TryProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return $"Process {pid}";
        }
    }

    [ComImport]
    [Guid("0BD7A1BE-7A7A-4D66-9B64-6A7B8E8D4D8B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }
}
