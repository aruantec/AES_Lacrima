using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.Logging;
using AES_Emulation.Windows.API;
using log4net;

namespace AES_Emulation.EmulationHandlers;

public sealed class ShadPs4Handler : EmulatorHandlerBase
{
    private static readonly ILog Log = LogHelper.For<ShadPs4Handler>();

    private const uint WS_CHILD = 0x40000000;

    private const string CoreProcessName = "shadPS4";
    private const string CoreLinkerOutputToken = "[Core.Linker]";
    private const string QtLauncherTitleToken = "qtlauncher";
    private const string CmdExecutableName = "cmd.exe";

    public static ShadPs4Handler Instance { get; } = new();

    private string? _launchTranscriptPath;

    public string? CurrentLaunchTranscriptPath => _launchTranscriptPath;

    /// <summary>
    /// When true, launches shadPS4 with SHADPS4_ENABLE_IPC and redirected stdin for live cheats (qt-launcher protocol).
    /// When false, uses a cmd wrapper without IPC (no live memory patches).
    /// </summary>
    public bool UseIpcForCheatsLaunch { get; set; } = true;

    private ShadPs4Handler()
    {
    }

    public override string HandlerId => "shadps4-qtlauncher";

    public override string SectionKey => "PS4";

    public override string SectionTitle => "PlayStation 4";

    public override string DisplayName => "shadPS4";


    public override double? CaptureWindowAspectRatio => 16.0 / 9.0;

    public override bool HideUntilCaptured => true;

    public override int CaptureStartupDelayMs => 0;

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
        var resolvedExecutablePath = ResolveShadPs4ExecutablePath(launcherPath);
        var resolvedGamePath = ResolveShadPs4GamePath(romPath);
        var launchTranscriptPath = CreateLaunchTranscriptPath(resolvedExecutablePath);
        _launchTranscriptPath = launchTranscriptPath;

        EnsureBackgroundInputEnabled(resolvedExecutablePath);

        if (UseIpcForCheatsLaunch)
        {
            TerminateOtherShadPs4Instances();
            return BuildIpcStartInfo(resolvedExecutablePath, resolvedGamePath, launchTranscriptPath);
        }

        return BuildScriptStartInfo(resolvedExecutablePath, resolvedGamePath, launchTranscriptPath);
    }

    private ProcessStartInfo BuildScriptStartInfo(string resolvedExecutablePath, string resolvedGamePath, string launchTranscriptPath)
    {
        var launchScriptPath = CreateLaunchScriptPath(resolvedExecutablePath, resolvedGamePath, launchTranscriptPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = CmdExecutableName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = ResolveShadPs4WorkingDirectory(resolvedExecutablePath)
        };

        ApplyShadPs4UserEnvironment(startInfo, resolvedExecutablePath);
        startInfo.Arguments = $"/d /c call \"{launchScriptPath}\"";

        Log.Debug($"shadPS4 start info: FileName='{startInfo.FileName}', WorkingDirectory='{startInfo.WorkingDirectory}', Arguments='{startInfo.Arguments}', UseShellExecute={startInfo.UseShellExecute}, TranscriptPath='{launchTranscriptPath}', ScriptPath='{launchScriptPath}', IpcEnabled=false");
        return startInfo;
    }

    private ProcessStartInfo BuildIpcStartInfo(string resolvedExecutablePath, string resolvedGamePath, string launchTranscriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = ResolveShadPs4WorkingDirectory(resolvedExecutablePath)
        };

        ApplyShadPs4UserEnvironment(startInfo, resolvedExecutablePath);
        startInfo.Environment["SHADPS4_ENABLE_IPC"] = "true";

        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(resolvedGamePath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("false");

        Log.Debug($"shadPS4 start info: FileName='{startInfo.FileName}', WorkingDirectory='{startInfo.WorkingDirectory}', UseShellExecute={startInfo.UseShellExecute}, TranscriptPath='{launchTranscriptPath}', IpcEnabled=true");
        return startInfo;
    }

    private static void ApplyShadPs4UserEnvironment(ProcessStartInfo startInfo, string resolvedExecutablePath)
    {
        var userDirectory = ResolveShadPs4UserDirectory(resolvedExecutablePath);
        if (string.IsNullOrWhiteSpace(userDirectory))
            return;

        startInfo.Environment["SHADPS4_USER_DIR"] = userDirectory;
        startInfo.Environment["APPDATA"] = userDirectory;
        startInfo.Environment["LOCALAPPDATA"] = userDirectory;
        startInfo.Environment["XDG_CONFIG_HOME"] = userDirectory;
        startInfo.Environment["XDG_DATA_HOME"] = userDirectory;
    }

    private static void TerminateOtherShadPs4Instances()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsLikelyCoreProcessName(process.ProcessName))
                    continue;

                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
            finally
            {
                process.Dispose();
            }
        }
    }

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        await WaitForCoreLinkerOutputAsync(process, _launchTranscriptPath, cancellationToken).ConfigureAwait(false);

        var hwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (hwnd != IntPtr.Zero)
            ResizeCaptureWindowToSixteenByNine(hwnd);

        return hwnd;
    }

    private static string CreateLaunchTranscriptPath(string executablePath)
    {
        var workingDirectory = ResolveShadPs4WorkingDirectory(executablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
            workingDirectory = Path.GetTempPath();

        var transcriptDirectory = Path.Combine(workingDirectory, "user", "log");
        Directory.CreateDirectory(transcriptDirectory);
        return Path.Combine(transcriptDirectory, $"shadps4_launch_{Guid.NewGuid():N}.log");
    }

    private static string CreateLaunchScriptPath(string executablePath, string gamePath, string transcriptPath)
    {
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "AES_Lacrima", "ShadPs4");
        Directory.CreateDirectory(scriptDirectory);

        var scriptPath = Path.Combine(scriptDirectory, $"launch_{Guid.NewGuid():N}.cmd");
        var escapedGamePath = gamePath.Replace("\"", "\"\"");
        var escapedTranscriptPath = transcriptPath.Replace("\"", "\"\"");
        var escapedExecutablePath = executablePath.Replace("\"", "\"\"");
        var escapedWorkingDirectory = ResolveShadPs4WorkingDirectory(executablePath).Replace("\"", "\"\"");

        var scriptContents = string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            $"set \"SHADPS4_TRANSCRIPT={escapedTranscriptPath}\"",
            $"cd /d \"{escapedWorkingDirectory}\"",
            $"\"{escapedExecutablePath}\" -g \"{escapedGamePath}\" -f false 1> \"%SHADPS4_TRANSCRIPT%\" 2>&1"
        });

        File.WriteAllText(scriptPath, scriptContents);
        return scriptPath;
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return null;
    }

    private static string ResolveShadPs4ExecutablePath(string? launcherPath)
    {
        var resolvedLauncherPath = ResolveShadPs4LauncherPath(launcherPath);
        if (string.IsNullOrWhiteSpace(resolvedLauncherPath))
            return launcherPath?.Trim() ?? string.Empty;

        var fileName = Path.GetFileNameWithoutExtension(resolvedLauncherPath);
        if (string.Equals(fileName, CoreProcessName, StringComparison.OrdinalIgnoreCase))
            return resolvedLauncherPath;

        var directory = Path.GetDirectoryName(resolvedLauncherPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            foreach (var executableName in new[] { "shadPS4.exe", "shadps4.exe" })
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return resolvedLauncherPath;
    }

    private static string ResolveShadPs4WorkingDirectory(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return string.Empty;

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(executablePath.Trim())) ?? string.Empty;
        }
        catch
        {
            return Path.GetDirectoryName(executablePath.Trim()) ?? string.Empty;
        }
    }

    private static string ResolveShadPs4UserDirectory(string? executablePath)
    {
        var workingDirectory = ResolveShadPs4WorkingDirectory(executablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return string.Empty;

        var userDirectory = Path.Combine(workingDirectory, "user");
        try
        {
            Directory.CreateDirectory(userDirectory);
            return userDirectory;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task WaitForCoreLinkerOutputAsync(Process process, string? transcriptPath, CancellationToken cancellationToken)
    {
        if (process == null)
            return;

        try
        {
            if (process.HasExited)
                return;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(transcriptPath))
            return;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        var coreLinkerToken = "Core.Linker";
        long lastObservedLength = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(transcriptPath))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < lastObservedLength)
                    lastObservedLength = 0;

                if (stream.Length == lastObservedLength)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                stream.Position = lastObservedLength;
                using var reader = new StreamReader(stream);
                var remainingText = await reader.ReadToEndAsync().ConfigureAwait(false);
                lastObservedLength = stream.Position;

                if (string.IsNullOrWhiteSpace(remainingText))
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var line in remainingText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Log.Info($"shadPS4 output: {line}");
                    if (line.Contains(CoreLinkerOutputToken, StringComparison.OrdinalIgnoreCase) ||
                        line.Contains(coreLinkerToken, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (IOException logEx) { Log.Warn("Exception caught", logEx); }

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }


        Log.Warn("Timed out waiting for shadPS4 to emit [Core.Linker]; continuing with capture target resolution.");
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
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        var directParamSfoPath = Path.Combine(normalizedPath, "sce_sys", "param.sfo");
        if (File.Exists(normalizedPath) || File.Exists(directParamSfoPath))
            return normalizedPath;

        if (!Directory.Exists(normalizedPath))
            return romPath;

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.TopDirectoryOnly))
            {
                var candidateParamSfoPath = Path.Combine(candidate, "sce_sys", "param.sfo");

                if (File.Exists(candidateParamSfoPath))
                    return candidate;
            }

            foreach (var candidate in Directory.EnumerateDirectories(normalizedPath, "*", SearchOption.AllDirectories))
            {
                var candidateParamSfoPath = Path.Combine(candidate, "sce_sys", "param.sfo");

                if (File.Exists(candidateParamSfoPath))
                    return candidate;
            }
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

        return romPath;
    }

    public override void PrepareProcessForCapture(Process process)
    {
    }

    public override void PrepareWindowForCapture(IntPtr hwnd)
    {
        PrepareWindowForCaptureAttach(hwnd);
    }

    public override IntPtr FindPreferredWindowHandle(Process process)
    {
        var topLevel = FindBestProcessWindowHandle(
            process,
            preferSpecificRenderWindow: true,
            allowHiddenWindows: true,
            isPreferredRenderWindow: IsLikelyShadPs4RenderWindow,
            fallbackTitleHint: CoreProcessName);

        if (topLevel == IntPtr.Zero)
            return IntPtr.Zero;

        if (OperatingSystem.IsWindows())
        {
            var renderChild = FindBestRenderChildWindow(process, topLevel);
            if (renderChild != IntPtr.Zero)
                return renderChild;
        }

        return topLevel;
    }

    public override bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle)
        => IsLikelyShadPs4RenderWindow(hwnd, mainWindowHandle);

    public override async Task<Process?> ResolveRuntimeProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited && IsLikelyCoreProcessName(process.ProcessName))
                return process;
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }

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

    private static bool IsLikelyShadPs4RenderWindow(IntPtr hwnd, IntPtr mainWindowHandle)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var title = GetWindowTitle(hwnd).Trim();
        var className = GetWindowClassName(hwnd).Trim();
        var lowerTitle = title.ToLowerInvariant();
        var lowerClass = className.ToLowerInvariant();
        var style = GetWindowStyle(hwnd);
        var hasChildStyle = (style & WS_CHILD) == WS_CHILD;
        var parent = GetParent(hwnd);
        var hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        var looksLikePrimaryUi = hwnd == mainWindowHandle;

        // WGC on child HWNDs (nested SDL/Qt surfaces) produces edge corruption on the right.
        if (hasChildStyle || parent != IntPtr.Zero)
            return false;

        if (lowerTitle.Contains(QtLauncherTitleToken, StringComparison.Ordinal) ||
            lowerTitle.Contains("settings", StringComparison.Ordinal) ||
            lowerTitle.Contains("controller", StringComparison.Ordinal) ||
            lowerTitle.Contains("about", StringComparison.Ordinal) ||
            lowerTitle.Contains("compatibility", StringComparison.Ordinal) ||
            lowerTitle.Contains("version manager", StringComparison.Ordinal) ||
            lowerTitle.Contains("game install", StringComparison.Ordinal) ||
            lowerTitle.Contains("launcher", StringComparison.Ordinal) ||
            lowerClass.Contains("ime") ||
            lowerClass.Contains("tooltip") ||
            lowerClass.Contains("observer"))
        {
            return false;
        }

        if (lowerClass.Contains("sdl") ||
            lowerClass.Contains("vulkan") ||
            lowerClass.Contains("render"))
        {
            return true;
        }

        if (lowerTitle.StartsWith("fps:", StringComparison.OrdinalIgnoreCase) ||
            lowerTitle.Contains("vulkan", StringComparison.Ordinal) ||
            lowerTitle.Contains("opengl", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(lowerTitle))
        {
            if (lowerTitle.Contains("shadps4", StringComparison.Ordinal) ||
                lowerTitle.Contains("qt", StringComparison.Ordinal))
            {
                looksLikePrimaryUi = true;
            }
            else if (hwnd == mainWindowHandle && lowerTitle.Length >= 2)
            {
                return true;
            }
            else if (!looksLikePrimaryUi && lowerTitle.Length >= 2)
            {
                return true;
            }
        }

        if (lowerClass.Contains("qt") && looksLikePrimaryUi)
            return false;

        return !looksLikePrimaryUi && (!hasCaption || !string.IsNullOrWhiteSpace(title));
    }

    private static IntPtr FindBestRenderChildWindow(Process process, IntPtr parentHwnd)
    {
        if (!OperatingSystem.IsWindows() || parentHwnd == IntPtr.Zero)
            return IntPtr.Zero;

        uint processId;
        try
        {
            process.Refresh();
            processId = (uint)process.Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        IntPtr bestHwnd = IntPtr.Zero;
        long bestScore = long.MinValue;

        Win32API.EnumChildWindows(parentHwnd, (child, _) =>
        {
            if (GetParent(child) != parentHwnd)
                return true;

            var score = ScoreRenderChildWindow(child, processId);
            if (score > bestScore)
            {
                bestScore = score;
                bestHwnd = child;
            }

            return true;
        }, IntPtr.Zero);

        return bestScore > long.MinValue ? bestHwnd : IntPtr.Zero;
    }

    private static long ScoreRenderChildWindow(IntPtr hwnd, uint processId)
    {
        if (hwnd == IntPtr.Zero)
            return long.MinValue;

        if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
            return long.MinValue;

        if (!Win32API.GetClientAreaOffsets(hwnd, out _, out _, out var clientWidth, out var clientHeight))
            return long.MinValue;

        if (clientWidth < 320 || clientHeight < 180)
            return long.MinValue;

        var className = GetWindowClassName(hwnd).Trim().ToLowerInvariant();
        if (!className.Contains("sdl") &&
            !className.Contains("vulkan") &&
            !className.Contains("render"))
        {
            return long.MinValue;
        }

        return (long)clientWidth * clientHeight;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
            catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
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

    private static void EnsureBackgroundInputEnabled(string? executablePath)
    {
        try
        {
            var userDirectory = ResolveShadPs4UserDirectory(executablePath);
            if (string.IsNullOrWhiteSpace(userDirectory))
                return;

            var configPath = Path.Combine(userDirectory, "config.toml");
            if (!File.Exists(configPath))
                return;

            var lines = File.ReadAllLines(configPath);
            var modified = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("background_controller_input", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Contains('='))
                {
                    var newLine = "background_controller_input = true";
                    if (!string.Equals(lines[i].Trim(), newLine, StringComparison.Ordinal))
                    {
                        lines[i] = newLine;
                        modified = true;
                    }
                }
            }

            if (modified)
                File.WriteAllLines(configPath, lines);
        }
        catch (Exception logEx) { Log.Warn("Exception caught", logEx); }
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);
}
