using System;
using System.Runtime.InteropServices;

namespace AES_Emulation.Windows.API
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        void RegisterEndpointNotificationCallback(IntPtr pClient);
        void UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        void OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        void QueryHardwareSupport(out uint pdwHardwareSupportMask);
    }

    [ComImport]
    [Guid("BFA951F0-2FFE-48E6-91C9-472604E8FE34")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager
    {
        void GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        void GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC8-D2E655D726D5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        void GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
        void GetAudioSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        void RegisterSessionNotification(IntPtr sessionNotification);
        void UnregisterSessionNotification(IntPtr sessionNotification);
        void RegisterDuckingNotification(IntPtr notification);
        void UnregisterDuckingNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);
        void GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        void GetState(out uint state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pDisplayName);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, Guid eventContext);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pIconPath);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, Guid eventContext);
        void GetGroupingParam(out Guid pGroupingParam);
        void SetGroupingParam(Guid groupingParam, Guid eventContext);
    }

    [ComImport]
    [Guid("BFB7FF88-6799-4560-8BE7-ED3A2C7B4381")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2 : IAudioSessionControl
    {
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        void GetProcessId(out uint pRetVal);
        void IsSystemSoundsSession(out uint pRetVal);
        void SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        void SetMasterVolume(float levelNorm, ref Guid eventContext);
        void GetMasterVolume(out float levelNorm);
        void SetMute(bool isMuted, ref Guid eventContext);
        void GetMute(out bool isMuted);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        void Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        void GetBufferSize(out uint bufferSize);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint currentPadding);
        void IsFormatSupported(uint shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        void GetMixFormat(out IntPtr ppFormat);
        void GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        [PreserveSig]
        int GetService(ref Guid iid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr pNotify);
        void UnregisterControlChangeNotify(IntPtr pNotify);
        void GetChannelCount(out uint channelCount);
        void SetMasterVolumeLevel(float level, ref Guid eventContext);
        void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolume(uint channel, float level, ref Guid eventContext);
        void SetChannelVolumeScalar(uint channel, float level, ref Guid eventContext);
        void GetChannelVolume(uint channel, out float level);
        void GetChannelVolumeScalar(uint channel, out float level);
        void SetMute(bool isMuted, ref Guid eventContext);
        void GetMute(out bool isMuted);
        void GetVolumeRange(out float minLevel, out float maxLevel, out float increment);
        void GetVolumeStepInfo(out uint step, out uint stepCount);
        void VolumeStepUp(ref Guid eventContext);
        void VolumeStepDown(ref Guid eventContext);
        void QueryHardwareSupport(out uint hardwareSupportMask);
        void GetVolumeRangeChannel(uint channel, out float minLevel, out float maxLevel, out float increment);
    }
}
