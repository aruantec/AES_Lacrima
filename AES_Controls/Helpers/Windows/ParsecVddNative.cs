using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AES_Controls.Helpers.Windows;

/// <summary>
/// Minimal C# port of nomi-san/parsec-vdd core/parsec-vdd.h for Parsec Virtual Display Driver control.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ParsecVddNative
{
    public const string DisplayName = "ParsecVDA";
    public const string HardwareId = "Root\\Parsec\\VDA";
    public const string AdapterName = "Parsec Virtual Display Adapter";

    // {00b41627-04c4-429e-a26e-0265cf50c8fa}
    public static readonly Guid AdapterGuid = new(0x00b41627, 0x04c4, 0x429e, 0xa2, 0x6e, 0x02, 0x65, 0xcf, 0x50, 0xc8, 0xfa);

    // {4d36e968-e325-11ce-bfc1-08002be10318}
    public static readonly Guid ClassGuid = new(0x4d36e968, 0xe325, 0x11ce, 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18);

    public const int MaxDisplays = 8;

    public enum DeviceStatus
    {
        Ok = 0,
        Inaccessible,
        Unknown,
        UnknownProblem,
        Disabled,
        DriverError,
        RestartRequired,
        DisabledService,
        NotInstalled
    }

    private enum VddCtlCode : uint
    {
        Add = 0x0022e004,
        Remove = 0x0022a008,
        Update = 0x0022a00c,
        Version = 0x0022e010
    }

    public static DeviceStatus QueryDeviceStatus()
    {
        var devInfo = SetupDiGetClassDevs(ClassGuid, null, IntPtr.Zero, DiGetClassDevsPresent);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
            return DeviceStatus.NotInstalled;

        try
        {
            var devInfoData = new SpDevinfoData { CbSize = Marshal.SizeOf<SpDevinfoData>() };
            for (uint deviceIndex = 0; SetupDiEnumDeviceInfo(devInfo, deviceIndex, ref devInfoData); deviceIndex++)
            {
                if (!TryReadHardwareId(devInfo, ref devInfoData, out var hardwareId))
                    continue;

                if (!string.Equals(hardwareId, HardwareId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CmGetDevNodeStatus(out var devStatus, out var devProblem, devInfoData.DevInst, 0) != CrSuccess)
                    return DeviceStatus.NotInstalled;

                if ((devStatus & (DnDriverLoaded | DnStarted)) != 0)
                    return DeviceStatus.Ok;

                if ((devStatus & DnHasProblem) != 0)
                {
                    return devProblem switch
                    {
                        CmProbNeedRestart => DeviceStatus.RestartRequired,
                        CmProbDisabled or CmProbHardwareDisabled => DeviceStatus.Disabled,
                        CmProbDisabledService => DeviceStatus.DisabledService,
                        CmProbFailedPostStart => DeviceStatus.DriverError,
                        _ => DeviceStatus.UnknownProblem
                    };
                }

                // Root-enumerated virtual display adapters may not set DN_STARTED even when healthy.
                return DeviceStatus.Ok;
            }

            return DeviceStatus.NotInstalled;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
    }

    public static IntPtr OpenDevice()
    {
        var devInfo = SetupDiGetClassDevs(AdapterGuid, null, IntPtr.Zero, DiGetClassDevsPresent | DiGetClassDevsDeviceInterface);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
            return IntPtr.Zero;

        try
        {
            var adapterGuid = AdapterGuid;
            var devInterface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref adapterGuid, i, ref devInterface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(devInfo, ref devInterface, IntPtr.Zero, 0, out var detailSize, IntPtr.Zero);
                var detailPtr = Marshal.AllocHGlobal((int)detailSize);
                try
                {
                    Marshal.WriteInt32(detailPtr, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref devInterface, detailPtr, detailSize, out _, IntPtr.Zero))
                        continue;

                    var devicePathOffset = IntPtr.Size == 8 ? 8 : 4;
                    var devicePathPtr = IntPtr.Add(detailPtr, devicePathOffset);
                    var devicePath = NormalizeDevicePath(Marshal.PtrToStringUni(devicePathPtr));
                    if (string.IsNullOrWhiteSpace(devicePath))
                        continue;

                    var handle = CreateFile(
                        devicePath,
                        FileAccessRead | FileAccessWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        FileAttributeNormal | FileFlagNoBuffering | FileFlagOverlapped | FileFlagWriteThrough,
                        IntPtr.Zero);

                    if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                        return handle;
                }
                finally
                {
                    Marshal.FreeHGlobal(detailPtr);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return IntPtr.Zero;
    }

    public static void CloseDevice(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            CloseHandle(handle);
    }

    public static int GetVersion(IntPtr handle) => (int)VddIoControl(handle, VddCtlCode.Version, null, 0);

    public static bool TryQuickProbe(int timeoutMs = 750)
    {
        var handle = OpenDevice();
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            return VddIoControl(handle, VddCtlCode.Version, null, 0, timeoutMs) != uint.MaxValue;
        }
        finally
        {
            CloseDevice(handle);
        }
    }

    public static void Update(IntPtr handle) => VddIoControl(handle, VddCtlCode.Update, null, 0);

    public static int AddDisplay(IntPtr handle)
    {
        var idx = (int)VddIoControl(handle, VddCtlCode.Add, null, 0);
        Update(handle);
        return idx;
    }

    public static void RemoveDisplay(IntPtr handle, int index)
    {
        ushort indexData = (ushort)(((index & 0xFF) << 8) | ((index >> 8) & 0xFF));
        var bytes = BitConverter.GetBytes(indexData);
        VddIoControl(handle, VddCtlCode.Remove, bytes, bytes.Length);
        Update(handle);
    }

    private static uint VddIoControl(IntPtr handle, VddCtlCode code, byte[]? data, int size, int timeoutMs = 5000)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return uint.MaxValue;

        var inBuffer = new byte[32];
        if (data is { Length: > 0 })
            Buffer.BlockCopy(data, 0, inBuffer, 0, Math.Min(size, inBuffer.Length));

        var overlapped = new NativeOverlapped();
        var eventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        overlapped.EventHandle = eventHandle;

        try
        {
            var outBuffer = 0;
            if (!DeviceIoControl(handle, (uint)code, inBuffer, inBuffer.Length, ref outBuffer, sizeof(int), out _, ref overlapped))
            {
                if (Marshal.GetLastWin32Error() != ErrorIoPending)
                    return uint.MaxValue;
            }

            if (!GetOverlappedResultEx(handle, ref overlapped, out var transferred, (uint)timeoutMs, false))
                return uint.MaxValue;

            return (uint)outBuffer;
        }
        finally
        {
            if (eventHandle != IntPtr.Zero)
                CloseHandle(eventHandle);
        }
    }

    private static bool TryReadHardwareId(IntPtr devInfo, ref SpDevinfoData devInfoData, out string hardwareId)
    {
        hardwareId = string.Empty;
        SetupDiGetDeviceRegistryProperty(devInfo, ref devInfoData, SpdrpHardwareId, out _, IntPtr.Zero, 0, out var requiredSize);
        if (requiredSize == 0)
            return false;

        var buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (!SetupDiGetDeviceRegistryProperty(devInfo, ref devInfoData, SpdrpHardwareId, out var regType, buffer, requiredSize, out _))
                return false;

            if (regType is RegSz or RegMultiSz)
            {
                hardwareId = ReadMultiSzString(buffer, requiredSize);
                return !string.IsNullOrWhiteSpace(hardwareId);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    private static string ReadMultiSzString(IntPtr buffer, uint bufferSize)
    {
        var strings = new List<string>();
        var offset = 0;
        while (offset < bufferSize)
        {
            var value = Marshal.PtrToStringAuto(IntPtr.Add(buffer, offset));
            if (string.IsNullOrEmpty(value))
                break;

            strings.Add(value);
            offset += (value.Length + 1) * Marshal.SystemDefaultCharSize;
        }

        return strings.FirstOrDefault() ?? string.Empty;
    }

    private static string? NormalizeDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return devicePath;

        if (devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return devicePath;

        if (devicePath.StartsWith(@"?\", StringComparison.Ordinal))
            return @"\\?\" + devicePath[2..];

        if (devicePath.StartsWith(@"\?\", StringComparison.Ordinal))
            return @"\\" + devicePath;

        return devicePath;
    }

    private const uint DiGetClassDevsPresent = 0x00000002;
    private const uint DiGetClassDevsDeviceInterface = 0x00000010;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint RegSz = 1;
    private const uint RegMultiSz = 7;
    private const uint DnDriverLoaded = 0x00000002;
    private const uint DnStarted = 0x00000008;
    private const uint DnHasProblem = 0x00000400;
    private const int CrSuccess = 0x00000000;
    private const int CmProbNeedRestart = 48;
    private const int CmProbDisabled = 22;
    private const int CmProbHardwareDisabled = 29;
    private const int CmProbDisabledService = 42;
    private const int CmProbFailedPostStart = 10;
    private const uint FileAccessRead = 0x80000000;
    private const uint FileAccessWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int ErrorIoPending = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(
        [MarshalAs(UnmanagedType.LPStruct)] Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("cfgmgr32.dll", SetLastError = true, EntryPoint = "CM_Get_DevNode_Status")]
    private static extern int CmGetDevNodeStatus(out uint status, out int problemNumber, int devInst, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        ref int lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResultEx(
        IntPtr hFile,
        ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        uint dwMilliseconds,
        bool bAlertable);
}
