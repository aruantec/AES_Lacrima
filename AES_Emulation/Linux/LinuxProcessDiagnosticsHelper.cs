using System;
using System.Diagnostics;

namespace AES_Emulation.Linux;

public static class LinuxProcessDiagnosticsHelper
{
    public static bool TryGetHasExited(Process? process, out bool hasExited)
    {
        hasExited = false;
        if (process == null)
            return false;

        try
        {
            hasExited = process.HasExited;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Describe(Process? process)
    {
        if (process == null)
            return "pid=0";

        try
        {
            if (process.HasExited)
                return $"pid={process.Id}, hasExited=true, exitCode={TryGetExitCode(process)}";

            var name = TryGetProcessName(process);
            return string.IsNullOrWhiteSpace(name)
                ? $"pid={process.Id}, hasExited=false"
                : $"pid={process.Id}, name={name}, hasExited=false";
        }
        catch (Exception ex)
        {
            return $"pid={TryGetProcessId(process)}, describeError={ex.Message}";
        }
    }

    public static int TryGetExitCode(Process? process)
    {
        if (process == null)
            return -1;

        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static int TryGetProcessId(Process? process)
    {
        if (process == null)
            return 0;

        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
