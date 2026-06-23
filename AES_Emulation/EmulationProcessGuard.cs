using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AES_Core.IO;
using AES_Core.Logging;
using AES_Emulation.Linux;
using log4net;

namespace AES_Emulation;

/// <summary>
/// Tracks the active emulator / gamescope session so it can be torn down when AES exits,
/// crashes, or is terminated without running the normal UI shutdown path.
/// </summary>
public static class EmulationProcessGuard
{
    private static readonly ILog SLog = LogHelper.For(typeof(EmulationProcessGuard));

    private static int _compositorRootPid;
    private static int _emulatorPid;
    private static int _emergencyInProgress;

    private static string SessionMarkerPath =>
        Path.Combine(ApplicationPaths.CacheDirectory, "active-emulation-session.pid");

    public static void RegisterLinuxCompositor(int compositorRootPid)
    {
        if (!OperatingSystem.IsLinux() || compositorRootPid <= 0)
            return;

        Interlocked.Exchange(ref _compositorRootPid, compositorRootPid);
        PersistMarker();
        SLog.Info($"EmulationProcessGuard registered gamescope root pid={compositorRootPid}.");
    }

    public static void RegisterEmulator(int emulatorPid)
    {
        if (emulatorPid <= 0)
            return;

        Interlocked.Exchange(ref _emulatorPid, emulatorPid);
        PersistMarker();
        SLog.Info($"EmulationProcessGuard registered emulator pid={emulatorPid}.");
    }

    public static void Clear()
    {
        Interlocked.Exchange(ref _compositorRootPid, 0);
        Interlocked.Exchange(ref _emulatorPid, 0);
        DeleteMarker();
    }

    /// <summary>
    /// Kills a stale gamescope/emulator tree left behind when a prior AES process died abruptly.
    /// </summary>
    public static void RecoverStaleSessionFromMarker()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(SessionMarkerPath))
            return;

        var compositorPid = 0;
        var emulatorPid = 0;
        try
        {
            var lines = File.ReadAllLines(SessionMarkerPath);
            if (lines.Length > 0)
                int.TryParse(lines[0].Trim(), out compositorPid);
            if (lines.Length > 1)
                int.TryParse(lines[1].Trim(), out emulatorPid);
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to read stale emulation session marker.", ex);
        }
        finally
        {
            DeleteMarker();
        }

        if (compositorPid > 0)
        {
            SLog.Warn($"Recovering stale gamescope session from marker (root pid={compositorPid}).");
            LinuxCompositorKillHelper.ForceKillProcessTree(compositorPid, waitForFallback: false);
            return;
        }

        if (emulatorPid > 0)
        {
            SLog.Warn($"Recovering stale emulator session from marker (pid={emulatorPid}).");
            LinuxCompositorKillHelper.ForceKillProcessTree(emulatorPid, waitForFallback: false);
        }
    }

    public static void EmergencyShutdown()
    {
        if (Interlocked.CompareExchange(ref _emergencyInProgress, 1, 0) != 0)
            return;

        try
        {
            var compositorPid = Interlocked.Exchange(ref _compositorRootPid, 0);
            var emulatorPid = Interlocked.Exchange(ref _emulatorPid, 0);
            DeleteMarker();

            if (OperatingSystem.IsLinux())
            {
                if (compositorPid > 0)
                {
                    SLog.Warn($"EmergencyShutdown: force-killing gamescope tree (root pid={compositorPid}).");
                    LinuxCompositorKillHelper.ForceKillProcessTree(compositorPid, waitForFallback: false);
                    return;
                }

                if (emulatorPid > 0)
                {
                    SLog.Warn($"EmergencyShutdown: force-killing emulator tree (pid={emulatorPid}).");
                    LinuxCompositorKillHelper.ForceKillProcessTree(emulatorPid, waitForFallback: false);
                }

                return;
            }

            if (OperatingSystem.IsWindows() && emulatorPid > 0)
                KillWindowsProcessTree(emulatorPid);
        }
        catch (Exception ex)
        {
            SLog.Debug("EmergencyShutdown failed.", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _emergencyInProgress, 0);
        }
    }

    private static void KillWindowsProcessTree(int emulatorPid)
    {
        try
        {
            using var process = Process.GetProcessById(emulatorPid);
            if (!process.HasExited)
            {
                SLog.Warn($"EmergencyShutdown: force-killing Windows emulator tree (pid={emulatorPid}).");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            SLog.Debug($"EmergencyShutdown failed to kill Windows emulator pid={emulatorPid}.", ex);
        }
    }

    private static void PersistMarker()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            Directory.CreateDirectory(ApplicationPaths.CacheDirectory);
            File.WriteAllText(SessionMarkerPath, $"{Volatile.Read(ref _compositorRootPid)}\n{Volatile.Read(ref _emulatorPid)}");
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to persist emulation session marker.", ex);
        }
    }

    private static void DeleteMarker()
    {
        try
        {
            if (File.Exists(SessionMarkerPath))
                File.Delete(SessionMarkerPath);
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to delete emulation session marker.", ex);
        }
    }
}
