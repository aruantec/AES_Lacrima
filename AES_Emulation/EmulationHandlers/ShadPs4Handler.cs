using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class ShadPs4Handler : EmulatorHandlerBase
{
    private const string CoreProcessName = "shadPS4";
    private const string QtLauncherTitleToken = "qtlauncher";

    public static ShadPs4Handler Instance { get; } = new();

    private ShadPs4Handler()
    {
    }

    public override string HandlerId => "shadps4-qtlauncher";

    public override string SectionKey => "PS4";

    public override string SectionTitle => "PlayStation 4";

    public override string DisplayName => "shadPS4 QtLauncher";

    public override bool ForceUseTargetClientAreaCapture => true;

    public override int CaptureStartupDelayMs => 3500;

    public override bool IsWindowEmbeddingSupported => true;

    public override bool IsLauncherPathValid(string? launcherPath)
        => !string.IsNullOrWhiteSpace(ResolveShadPs4LauncherPath(launcherPath));

    public override string? NormalizeLauncherPath(string? launcherPath)
        => ResolveShadPs4LauncherPath(launcherPath) ?? base.NormalizeLauncherPath(launcherPath);

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "PS4", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Playstation 4", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);
        startInfo.ArgumentList.Clear();

        var resolvedGamePath = ResolveShadPs4GamePath(romPath);
        var executableName = Path.GetFileNameWithoutExtension(startInfo.FileName);
        var useQtLauncherArguments = executableName.Contains("qtlauncher", StringComparison.OrdinalIgnoreCase);

        if (useQtLauncherArguments)
        {
            startInfo.ArgumentList.Add("-d");
        }

        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(resolvedGamePath);

        if (startFullscreen)
        {
            if (useQtLauncherArguments)
            {
                startInfo.ArgumentList.Add("--");
            }

            startInfo.ArgumentList.Add("--fullscreen");
            startInfo.ArgumentList.Add("true");
        }

        return startInfo;
    }

    private static string? ResolveShadPs4LauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        var normalizedPath = launcherPath.Trim();
        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch
        {
        }

        if (File.Exists(normalizedPath))
            return normalizedPath;

        if (!Directory.Exists(normalizedPath))
            return null;

        var executableNames = new[]
        {
            "shadPS4.exe",
            "shadps4.exe",
            "shadPS4QtLauncher.exe",
            "shadps4qtlauncher.exe"
        };

        foreach (var executableName in executableNames)
        {
            var candidate = Path.Combine(normalizedPath, executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var coreCandidate = Directory.EnumerateFiles(normalizedPath, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    return string.Equals(fileName, CoreProcessName, StringComparison.OrdinalIgnoreCase);
                });

            if (coreCandidate != null)
                return coreCandidate;

            var launcherCandidate = Directory.EnumerateFiles(normalizedPath, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(path);
                    return fileName.Contains("qtlauncher", StringComparison.OrdinalIgnoreCase) ||
                           fileName.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(fileName, CoreProcessName, StringComparison.OrdinalIgnoreCase);
                });

            if (launcherCandidate != null)
                return launcherCandidate;

            var exeFiles = Directory.EnumerateFiles(normalizedPath, "*.exe", SearchOption.AllDirectories).ToArray();
            if (exeFiles.Length == 1)
                return exeFiles[0];
        }
        catch
        {
        }

        return null;
    }

    private static string ResolveShadPs4GamePath(string romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
            return romPath;

        var normalizedPath = romPath.Trim();
        try
        {
            normalizedPath = Path.GetFullPath(normalizedPath);
        }
        catch
        {
        }

        if (File.Exists(Path.Combine(normalizedPath, "sce_sys", "param.sfo")))
            return normalizedPath;

        if (!Directory.Exists(normalizedPath))
            return romPath;

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(Path.Combine(candidate, "sce_sys", "param.sfo")))
                    return candidate;
            }

            foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(candidate, "sce_sys", "param.sfo")))
                    return candidate;
            }
        }
        catch
        {
        }

        return romPath;
    }

    public override void PrepareProcessForCapture(Process process)
    {
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        var preferred = FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyShadPs4RenderWindow,
            fallbackTitleHint: CoreProcessName);

        return preferred != IntPtr.Zero
            ? preferred
            : FindBestProcessWindowHandle(
                process,
                preferSpecificRenderWindow: false,
                allowHiddenWindows: true,
                isPreferredRenderWindow: null,
                fallbackTitleHint: CoreProcessName);
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyShadPs4RenderWindow(hwnd, mainWindowHandle);

    public override async Task<Process?> ResolveRuntimeProcessAsync(Process process, CancellationToken cancellationToken)
    {
        var launcherStartTimeUtc = GetProcessStartTimeUtc(process);
        var launcherProcessId = TryGetProcessId(process);

        for (var attempt = 0; attempt < 160; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var coreProcess = TryResolveCoreProcess(launcherProcessId, launcherStartTimeUtc);
            if (coreProcess != null)
                return coreProcess;

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return await base.ResolveRuntimeProcessAsync(process, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        if (CaptureStartupDelayMs > 0)
            await Task.Delay(CaptureStartupDelayMs, cancellationToken).ConfigureAwait(false);

        const int maxAttempts = 180;
        const int delayMs = 100;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preferredHwnd = FindPreferredWindowHandle(process);
            if (preferredHwnd != IntPtr.Zero)
                return preferredHwnd;

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        var fallbackHwnd = FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: false,
            allowHiddenWindows: true,
            isPreferredRenderWindow: null,
            fallbackTitleHint: CoreProcessName);

        return fallbackHwnd != IntPtr.Zero
            ? fallbackHwnd
            : await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLikelyShadPs4RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).Trim();
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(lowerTitle) && string.IsNullOrWhiteSpace(lowerClass))
            return false;

        if (lowerTitle.Contains(QtLauncherTitleToken, StringComparison.Ordinal) ||
            lowerTitle.Contains("settings", StringComparison.Ordinal) ||
            lowerTitle.Contains("controller", StringComparison.Ordinal) ||
            lowerTitle.Contains("about", StringComparison.Ordinal) ||
            lowerTitle.Contains("compatibility", StringComparison.Ordinal) ||
            lowerTitle.Contains("version manager", StringComparison.Ordinal) ||
            lowerTitle.Contains("game install", StringComparison.Ordinal) ||
            lowerClass.Contains("ime") ||
            lowerClass.Contains("tooltip") ||
            lowerClass.Contains("observer"))
        {
            return false;
        }

        if (lowerTitle.Contains("shadps4", StringComparison.Ordinal))
            return true;

        if (lowerClass.Contains("sdl") || lowerClass.Contains("render") || lowerClass.Contains("vulkan"))
            return true;

        return hwnd != mainWindowHandle && !string.IsNullOrWhiteSpace(title);
    }

    private static Process? TryResolveCoreProcess(int launcherProcessId, DateTime launcherStartTimeUtc)
    {
        if (launcherProcessId > 0 &&
            OperatingSystem.IsWindows() &&
            TryResolveCoreProcessFromChildren(launcherProcessId, out var coreProcess))
        {
            return coreProcess;
        }

        return TryResolveCoreProcessByName(launcherProcessId, launcherStartTimeUtc);
    }

    private static Process? TryResolveCoreProcessByName(int launcherProcessId, DateTime launcherStartTimeUtc)
    {
        Process? bestCandidate = null;
        DateTime bestStartTime = DateTime.MinValue;

        foreach (var candidate in Process.GetProcesses())
        {
            try
            {
                if (candidate.Id == launcherProcessId || !IsLikelyCoreProcessName(candidate.ProcessName))
                    continue;

                var startTime = candidate.StartTime.ToUniversalTime();
                if (startTime < launcherStartTimeUtc.AddSeconds(-2))
                    continue;

                if (startTime > bestStartTime)
                {
                    bestStartTime = startTime;
                    bestCandidate = candidate;
                }
            }
            catch
            {
            }
        }

        return bestCandidate;
    }

    private static bool TryResolveCoreProcessFromChildren(int launcherProcessId, out Process? process)
    {
        process = null;

        if (!OperatingSystem.IsWindows())
            return false;

        var descendants = EnumerateDescendantProcesses(launcherProcessId);
        if (descendants.Count == 0)
            return false;

        var bestCandidate = descendants
            .Where(entry => IsLikelyCoreProcessName(entry.ExeName))
            .OrderByDescending(entry => entry.ProcessId)
            .FirstOrDefault();

        if (bestCandidate.ProcessId == 0)
            return false;

        try
        {
            process = Process.GetProcessById((int)bestCandidate.ProcessId);
            return !process.HasExited;
        }
        catch
        {
            process = null;
            return false;
        }
    }

    private static List<ProcessEntry> EnumerateDescendantProcesses(int rootProcessId)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        IntPtr snapshot = IntPtr.Zero;
        try
        {
            snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
                return [];

            var entries = new List<ProcessEntry>();
            var current = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref current))
                return [];

            do
            {
                entries.Add(new ProcessEntry(current.th32ProcessID, current.th32ParentProcessID, current.szExeFile));
                current.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32Next(snapshot, ref current));

            var byParent = entries
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var pending = new Queue<uint>();
            var visited = new HashSet<uint>();
            var results = new List<ProcessEntry>();
            pending.Enqueue((uint)rootProcessId);
            visited.Add((uint)rootProcessId);

            while (pending.Count > 0)
            {
                var parentId = pending.Dequeue();
                if (!byParent.TryGetValue(parentId, out var children))
                    continue;

                foreach (var child in children)
                {
                    if (!visited.Add(child.ProcessId))
                        continue;

                    results.Add(child);
                    pending.Enqueue(child.ProcessId);
                }
            }

            return results;
        }
        finally
        {
            if (snapshot != IntPtr.Zero && snapshot != INVALID_HANDLE_VALUE)
                CloseHandle(snapshot);
        }
    }

    private static bool IsLikelyCoreProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(processName.Trim());
        return string.Equals(fileName, CoreProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetProcessStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return 0;
        }
    }

    private readonly record struct ProcessEntry(uint ProcessId, uint ParentProcessId, string ExeName);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}