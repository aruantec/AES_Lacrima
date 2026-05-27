using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Core.Logging;
using log4net;

namespace AES_Emulation.EmulationHandlers;

/// <summary>
/// Windows Job Object that automatically terminates all emulator processes when the
/// host application exits, crashes, or is killed. Uses JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
/// so the OS kernel handles cleanup -- no managed code needs to run.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EmulatorJobObject
{
    private static readonly ILog SLog = LogHelper.For(typeof(EmulatorJobObject));
    private static readonly IntPtr JobHandle;
    private static readonly object Lock = new();
    private static bool _isSupported;

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    static EmulatorJobObject()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            JobHandle = CreateJobObject(IntPtr.Zero, null);
            if (JobHandle == IntPtr.Zero)
            {
                SLog.Warn("EmulatorJobObject: CreateJobObject failed.");
                return;
            }

            var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr marshalPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limitInfo, marshalPtr, false);
                if (!SetInformationJobObject(JobHandle, JobObjectExtendedLimitInformation, marshalPtr, (uint)size))
                {
                    SLog.Warn("EmulatorJobObject: SetInformationJobObject failed.");
                    CloseHandle(JobHandle);
                    JobHandle = IntPtr.Zero;
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(marshalPtr);
            }

            _isSupported = true;
            SLog.Info("EmulatorJobObject: Job object created with KILL_ON_JOB_CLOSE.");
        }
        catch (Exception ex)
        {
            SLog.Warn("EmulatorJobObject: Initialization failed.", ex);
        }
    }

    /// <summary>
    /// Assigns a process to the job. Once assigned, the process (and any children it spawns)
    /// will be terminated when the host application exits, crashes, or is killed.
    /// </summary>
    public static bool AssignProcess(Process process)
    {
        if (!_isSupported || process == null || process.HasExited)
            return false;

        lock (Lock)
        {
            if (JobHandle == IntPtr.Zero)
                return false;

            try
            {
                bool result = AssignProcessToJobObject(JobHandle, process.Handle);
                if (!result)
                    SLog.Debug($"EmulatorJobObject: AssignProcessToJobObject failed for PID {process.Id}.");
                return result;
            }
            catch (Exception ex)
            {
                SLog.Debug($"EmulatorJobObject: AssignProcess threw for PID {process.Id}.", ex);
                return false;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public IntPtr PerProcessUserTimeLimit;
        public IntPtr PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }
}
