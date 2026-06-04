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
            int hr = enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out IMMDeviceCollection collection);
            if (hr < 0 || collection == null)
                return results;

            if (collection.GetCount(out uint count) < 0)
                return results;

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice device) < 0 || device == null)
                    continue;

                CollectSessionsFromDevice(device, results, seen);
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
}
