using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Windows.API;

[SupportedOSPlatform("windows")]
public static class InjectionBridgeApi
{
    private const int PROCESS_CREATE_THREAD = 0x0002;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_VM_READ = 0x0010;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    private const int INFINITE = -1;
    private const int WAIT_OBJECT_0 = 0;
    private const int WAIT_TIMEOUT = 0x102;

    public const long InjectionSharedMemorySize = (128L * 1024L * 1024L) + 64;

    private static string GetRequestEventName(int processId) => $"Local\\AES_Lacrima_Injector_Request_{processId}";
    private static string GetReadyEventName(int processId) => $"Local\\AES_Lacrima_Injector_Ready_{processId}";
    public static string GetSharedMemoryName(int processId) => $"Local\\AES_Lacrima_Injector_Shared_{processId}";

    public static bool TryInjectProcess(int processId, TimeSpan timeout, out MemoryMappedFile? mmf, out MemoryMappedViewAccessor? accessor)
    {
        mmf = null;
        accessor = null;

        Debug.WriteLine($"[INJECTION] TryInjectProcess(pid={processId}, timeout={timeout.TotalSeconds}s)");

        if (!TryGetProcess(processId, out var process))
        {
            Debug.WriteLine($"[INJECTION] Target process not found or exited: pid={processId}");
            return false;
        }

        if (!ValidateArchitecture(process))
        {
            Debug.WriteLine($"[INJECTION] Architecture mismatch for pid={processId}");
            return false;
        }

        var dllPath = Path.Combine(AppContext.BaseDirectory, "WgcBridge.dll");
        if (!File.Exists(dllPath))
        {
            Debug.WriteLine($"[INJECTION] WgcBridge.dll not found at '{dllPath}'");
            return false;
        }

        var requestEventName = GetRequestEventName(processId);
        var readyEventName = GetReadyEventName(processId);

        IntPtr requestEvent = CreateEvent(IntPtr.Zero, true, false, requestEventName);
        if (requestEvent == IntPtr.Zero)
        {
            Debug.WriteLine($"[INJECTION] CreateEvent failed for requestEvent '{requestEventName}'");
            return false;
        }

        IntPtr readyEvent = CreateEvent(IntPtr.Zero, true, false, readyEventName);
        if (readyEvent == IntPtr.Zero)
        {
            Debug.WriteLine($"[INJECTION] CreateEvent failed for readyEvent '{readyEventName}'");
            CloseHandle(requestEvent);
            return false;
        }

        try
        {
            IntPtr processHandle = OpenProcess(
                PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                false,
                processId);
            if (processHandle == IntPtr.Zero)
                return false;

            try
            {
                if (!TryInjectDll(processHandle, dllPath))
                {
                    Debug.WriteLine($"[INJECTION] DLL injection failed for pid={processId}");
                    return false;
                }

                if (!SetEvent(requestEvent))
                {
                    Debug.WriteLine($"[INJECTION] SetEvent failed for requestEvent '{requestEventName}'");
                    return false;
                }

                Debug.WriteLine($"[INJECTION] Request event signaled '{requestEventName}'");
            }
            finally
            {
                CloseHandle(processHandle);
            }

            Debug.WriteLine($"[INJECTION] Waiting for ready event '{readyEventName}'");
            var remaining = (int)timeout.TotalMilliseconds;
            var result = WaitForSingleObject(readyEvent, remaining);
            Debug.WriteLine($"[INJECTION] WaitForSingleObject result={result}");
            if (result != WAIT_OBJECT_0)
            {
                Debug.WriteLine($"[INJECTION] WaitForInjectionReady timed out for '{readyEventName}'");
                return false;
            }

            try
            {
                // Try to open with ReadWrite if possible, or just Read. 
                // Using OpenExisting ensures we are looking for the exact same name.
                mmf = MemoryMappedFile.OpenExisting(GetSharedMemoryName(processId), MemoryMappedFileRights.ReadWrite);
                accessor = mmf.CreateViewAccessor(0, InjectionSharedMemorySize, MemoryMappedFileAccess.ReadWrite);
                
                // Set initialized flag via C# if bridge was slow
                unsafe
                {
                    byte* ptr = null;
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    try {
                        uint* magicPtr = (uint*)ptr; // Magic is now first
                        if (*magicPtr != 0x41534145) { // InjectionMagic
                            Debug.WriteLine("[INJECTION] Shared memory found but uninitialized, setting magic");
                            *magicPtr = 0x41534145;
                            // Initialize sequence
                            *(uint*)(ptr + 4) = 1;      // Sequence1
                            *(uint*)(ptr + 8) = 1;      // Sequence2
                        }
                    } finally {
                        accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[INJECTION] Failed to open injected shared memory (trying Read only): {ex.Message}");
                try {
                    mmf = MemoryMappedFile.OpenExisting(GetSharedMemoryName(processId), MemoryMappedFileRights.Read);
                    accessor = mmf.CreateViewAccessor(0, InjectionSharedMemorySize, MemoryMappedFileAccess.Read);
                    return true;
                } catch (Exception ex2) {
                    Debug.WriteLine($"[INJECTION] Final fallback failed: {ex2.Message}");
                    accessor?.Dispose();
                    mmf?.Dispose();
                    return false;
                }
            }
        }
        finally
        {
            CloseHandle(readyEvent);
            CloseHandle(requestEvent);
        }
    }

    private static bool TryGetProcess(int processId, out Process process)
    {
        process = null!;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateArchitecture(Process process)
    {
        if (!Environment.Is64BitOperatingSystem)
            return true;

        if (!IsWow64Process(process.Handle, out bool isWow64))
            return false;

        if (Environment.Is64BitProcess && isWow64)
            return false;

        if (!Environment.Is64BitProcess && !isWow64)
            return false;

        return true;
    }

    private static bool TryInjectDll(IntPtr processHandle, string dllPath)
    {
        Debug.WriteLine($"[INJECTION] TryInjectDll(dllPath='{dllPath}')");
        IntPtr loadLibraryAddress = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        if (loadLibraryAddress == IntPtr.Zero)
        {
            Debug.WriteLine("[INJECTION] GetProcAddress(LoadLibraryW) failed");
            return false;
        }

        var bytes = (dllPath.Length + 1) * 2;
        IntPtr remoteString = VirtualAllocEx(processHandle, IntPtr.Zero, (uint)bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteString == IntPtr.Zero)
        {
            Debug.WriteLine("[INJECTION] VirtualAllocEx failed");
            return false;
        }

        try
        {
            var buffer = Encoding.Unicode.GetBytes(dllPath + "\0");
            if (!WriteProcessMemory(processHandle, remoteString, buffer, buffer.Length, out _))
                return false;

            IntPtr threadHandle = CreateRemoteThread(processHandle, IntPtr.Zero, 0, loadLibraryAddress, remoteString, 0, IntPtr.Zero);
            if (threadHandle == IntPtr.Zero)
                return false;

            try
            {
                var result = WaitForSingleObject(threadHandle, 10000);
                Debug.WriteLine($"[INJECTION] CreateRemoteThread wait result={result}");

                if (result != WAIT_OBJECT_0)
                {
                    Debug.WriteLine("[INJECTION] CreateRemoteThread did not complete normally");
                    return false;
                }

                if (!GetExitCodeThread(threadHandle, out uint exitCode))
                {
                    Debug.WriteLine($"[INJECTION] GetExitCodeThread failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                Debug.WriteLine($"[INJECTION] Remote thread exit code={exitCode}");
                return exitCode != 0;
            }
            finally
            {
                CloseHandle(threadHandle);
            }
        }
        finally
        {
            VirtualFreeEx(processHandle, remoteString, 0, 0x8000);
        }
    }

    private static bool WaitForInjectionReady(string readyEventName, TimeSpan timeout)
    {
        Debug.WriteLine($"[INJECTION] Waiting for ready event '{readyEventName}'");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            IntPtr readyEvent = OpenEvent(SYNCHRONIZE, false, readyEventName);
            if (readyEvent != IntPtr.Zero)
            {
                try
                {
                    Debug.WriteLine($"[INJECTION] Ready event opened '{readyEventName}'");
                    var result = WaitForSingleObject(readyEvent, (int)(timeout - sw.Elapsed).TotalMilliseconds);
                    Debug.WriteLine($"[INJECTION] WaitForSingleObject result={result}");
                    return result == WAIT_OBJECT_0;
                }
                finally
                {
                    CloseHandle(readyEvent);
                }
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[INJECTION] OpenEvent failed for readyEvent '{readyEventName}', error={error}");
            }

            Thread.Sleep(50);
        }

        Debug.WriteLine($"[INJECTION] WaitForInjectionReady timed out waiting for '{readyEventName}'");
        return false;
    }

    private const int SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEvent(int dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);
}
