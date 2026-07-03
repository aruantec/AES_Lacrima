using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Controls.Helpers;
using AES_Core.Logging;
using AES_Emulation.Linux;
using AES_Emulation.Steam;
using AES_Emulation.Windows.API;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class SteamHandler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<SteamHandler>();
    public const string DefaultFlatpakAppId = "com.valvesoftware.Steam";

    public static SteamHandler Instance { get; } = new();

    private SteamHandler()
    {
    }

    public override string HandlerId => "steam";

    public override string SectionKey => "STEAM";

    public override string SectionTitle => "Steam";

    public override string DisplayName => "Steam";

    public override bool HideUntilCaptured => true;

    public override int CaptureStartupDelayMs => OperatingSystem.IsLinux() ? 1500 : 8000;

    public override double? CaptureWindowAspectRatio => 16.0 / 9.0;

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Steam.png", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    public override bool HasLauncherPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return IsLauncherPathValid(LauncherPath) || TryResolveNativeSteamExecutable(LauncherPath) != null;

            return HasConfiguredFlatpakLauncher() ||
                   IsLauncherPathValid(LauncherPath) ||
                   ResolveAutoFlatpakAppId() != null ||
                   TryResolveNativeSteamExecutable(LauncherPath) != null;
        }
    }

    protected override bool HasConfiguredFlatpakLauncher()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        if (base.HasConfiguredFlatpakLauncher())
            return true;

        return ResolveAutoFlatpakAppId() != null;
    }

    public string? ResolveAutoFlatpakAppId()
    {
        if (!OperatingSystem.IsLinux() ||
            !string.IsNullOrWhiteSpace(FlatpakAppId) ||
            !LinuxFlatpakApplicationService.IsFlatpakAvailable() ||
            !LinuxFlatpakApplicationService.IsApplicationInstalled(DefaultFlatpakAppId))
        {
            return null;
        }

        return DefaultFlatpakAppId;
    }

    public string? ResolveLaunchFlatpakAppId()
        => !string.IsNullOrWhiteSpace(FlatpakAppId) ? FlatpakAppId : ResolveAutoFlatpakAppId();

    public override ProcessStartInfo BuildStartInfo(
        string launcherPath,
        string romPath,
        bool startFullscreen,
        string? sectionTitle = null,
        string? selectedRetroArchCore = null)
    {
        var appId = SteamGamePath.GetAppId(romPath);
        if (string.IsNullOrWhiteSpace(appId))
            throw new InvalidOperationException($"Invalid Steam game path: '{romPath}'.");

        var executablePath = TryResolveNativeSteamExecutable(launcherPath);
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Could not locate the Steam executable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        startInfo.ArgumentList.Add("-silent");
        startInfo.ArgumentList.Add("-applaunch");
        startInfo.ArgumentList.Add(appId);

        Log.Debug(
            $"Steam start info: FileName='{startInfo.FileName}', WorkingDirectory='{startInfo.WorkingDirectory}', AppId='{appId}'.");
        return startInfo;
    }

    public override void PrepareStartInfoForVirtualDisplay(
        ProcessStartInfo startInfo,
        int monitorIndex,
        ParsecVirtualDisplayMonitor monitor)
    {
        base.PrepareStartInfoForVirtualDisplay(startInfo, monitorIndex, monitor);
        // Steam games are positioned on the virtual display after launch; monitor capture is used once ready.
    }

    public override async Task<Process?> ResolveRuntimeProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (process == null)
            return null;

        var maxAttempts = OperatingSystem.IsWindows() ? 120 : 40;
        var delayMs = OperatingSystem.IsWindows() ? 500 : 250;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryResolveGameProcess(process, out var gameProcess))
                return gameProcess;

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return process;
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyGameWindow,
            fallbackTitleHint: null);

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyGameWindow(hwnd, mainWindowHandle);

    public static string? TryResolveNativeSteamExecutable(string? launcherPath = null)
    {
        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
            return launcherPath;

        if (OperatingSystem.IsWindows())
            return TryResolveWindowsSteamExecutable();

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in pathEntries)
        {
            var candidate = Path.Combine(entry, "steam");
            if (File.Exists(candidate))
                return candidate;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;

        var candidates = new[]
        {
            Path.Combine(home, ".steam", "root", "ubuntu12_64", "steam"),
            Path.Combine(home, ".steam", "steam", "ubuntu12_64", "steam"),
            Path.Combine(home, ".local", "share", "Steam", "ubuntu12_64", "steam"),
            Path.Combine(home, ".var", "app", DefaultFlatpakAppId, ".local", "share", "Steam", "ubuntu12_64", "steam"),
            "/usr/bin/steam",
            "/usr/games/steam",
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryResolveWindowsSteamExecutable()
    {
        try
        {
            var steamPath = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Valve\Steam")
                ?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                var registryExe = Path.Combine(steamPath.TrimEnd('\\', '/'), "steam.exe");
                if (File.Exists(registryExe))
                    return registryExe;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to read Steam install path from registry.", ex);
        }

        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Steam\steam.exe",
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool TryResolveGameProcess(Process rootProcess, out Process? gameProcess)
    {
        gameProcess = null;
        if (rootProcess == null)
            return false;

        try
        {
            if (rootProcess.HasExited)
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            var descendants = rootProcess.GetProcessTree().Where(entry => entry.Id != rootProcess.Id);
            foreach (var candidate in descendants.OrderByDescending(entry => entry.Id))
            {
                if (IsSteamClientProcess(candidate) || IsWineInfrastructureProcess(candidate))
                    continue;

                if (IsLikelyGameProcess(candidate))
                {
                    gameProcess = candidate;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed while resolving Steam game process.", ex);
        }

        return false;
    }

    private static bool IsLikelyGameProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            if (OperatingSystem.IsWindows())
            {
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !IsSteamClientProcess(process) &&
                    !name.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("gameoverlay", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return process.MainWindowHandle != IntPtr.Zero &&
                       !string.IsNullOrWhiteSpace(process.MainWindowTitle) &&
                       !process.MainWindowTitle.Contains("Steam", StringComparison.OrdinalIgnoreCase);
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("steam", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var cmdline = TryReadProcCmdline(process.Id);
            if (!string.IsNullOrWhiteSpace(cmdline) &&
                cmdline.Contains(".exe", StringComparison.OrdinalIgnoreCase) &&
                !cmdline.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                !cmdline.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (process.MainWindowHandle != IntPtr.Zero || !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                return true;
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool IsWineInfrastructureProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.Contains("wineserver", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("wine-preloader", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("wine64-preloader", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("winedevice.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("plugplay.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("rpcss.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("tabtip.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("xalia.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadProcCmdline(int processId)
    {
        try
        {
            var cmdlinePath = $"/proc/{processId}/cmdline";
            if (!File.Exists(cmdlinePath))
                return null;

            var raw = File.ReadAllBytes(cmdlinePath);
            if (raw.Length == 0)
                return null;

            return string.Join(' ', Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSteamClientProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (OperatingSystem.IsWindows())
            {
                return name.Equals("steam", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("steamservice", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("gameoverlay", StringComparison.OrdinalIgnoreCase);
            }

            return name.Contains("steam", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("steamservice", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyGameWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return false;

        if (title.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

internal static class ProcessTreeExtensions
{
    public static IEnumerable<Process> GetProcessTree(this Process root)
    {
        var visited = new HashSet<int>();
        var queue = new Queue<Process>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Id))
                continue;

            yield return current;

            Process[] children;
            try
            {
                children = current.GetChildProcesses();
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
                queue.Enqueue(child);
        }
    }

    private static Process[] GetChildProcesses(this Process process)
    {
        if (OperatingSystem.IsLinux())
            return GetLinuxChildProcesses(process);

        if (OperatingSystem.IsWindows())
            return GetWindowsChildProcesses(process.Id);

        return [];
    }

    private static Process[] GetLinuxChildProcesses(Process process)
    {
        var children = new List<Process>();
        var procRoot = $"/proc/{process.Id}/task/{process.Id}/children";
        if (!File.Exists(procRoot))
            return [];

        var childIds = File.ReadAllText(procRoot)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var childIdText in childIds)
        {
            if (!int.TryParse(childIdText, out var childId) || childId <= 0)
                continue;

            try
            {
                children.Add(Process.GetProcessById(childId));
            }
            catch
            {
                // ignored
            }
        }

        return children.ToArray();
    }

    private static Process[] GetWindowsChildProcesses(int parentProcessId)
    {
        var children = new List<Process>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return [];

        try
        {
            var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return [];

            do
            {
                if ((int)entry.Th32ParentProcessID == parentProcessId && entry.Th32ProcessID != 0)
                {
                    try
                    {
                        children.Add(Process.GetProcessById((int)entry.Th32ProcessID));
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return children.ToArray();
    }

    private const uint Th32CsSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint DwSize;
        public uint CntUsage;
        public uint Th32ProcessID;
        public IntPtr Th32DefaultHeapID;
        public uint Th32ModuleID;
        public uint CntThreads;
        public uint Th32ParentProcessID;
        public int PcPriClassBase;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
