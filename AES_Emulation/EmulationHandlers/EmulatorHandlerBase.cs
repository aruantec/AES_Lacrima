using AES_Emulation.Windows.API;
using AES_Core.Logging;
using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AES_Emulation.Platform;
using AES_Core.DI;
using AES_Emulation.Controls;

namespace AES_Emulation.EmulationHandlers;

public abstract class EmulatorHandlerBase : IEmulatorHandler
{
    private static readonly ILog SLog = LogHelper.For<EmulatorHandlerBase>();

    [AutoResolve]
    protected IScreenCaptureService? CaptureService;

    private bool _isActive;
    private bool _isPrepared;
    private string? _launcherPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public abstract string HandlerId { get; }

    public abstract string SectionKey { get; }

    public abstract string SectionTitle { get; }

    public abstract string DisplayName { get; }

    public string? LauncherPath
    {
        get => _launcherPath;
        set
        {
            var normalizedPath = NormalizeLauncherPath(value);
            if (!SetProperty(ref _launcherPath, normalizedPath))
                return;

            OnPropertyChanged(nameof(LauncherDisplayPath));
            OnPropertyChanged(nameof(HasLauncherPath));
        }
    }

    public string LauncherDisplayPath =>
        string.IsNullOrWhiteSpace(LauncherPath)
            ? "No executable selected"
            : LauncherPath;

    public bool HasLauncherPath => IsLauncherPathValid(LauncherPath);

    public virtual bool IsLauncherPathValid(string? launcherPath)
    {
        var normalizedPath = NormalizeLauncherPath(launcherPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
            return false;

        return File.Exists(normalizedPath) || IsMacAppBundle(normalizedPath);
    }

    public virtual string? NormalizeLauncherPath(string? launcherPath)
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

        if (IsMacAppBundle(normalizedPath))
            return normalizedPath;

        return File.Exists(normalizedPath) ? normalizedPath : launcherPath.Trim();
    }

    public ICommand? BrowseLauncherCommand { get; set; }

    public ICommand? SelectFlatpakLauncherCommand { get; set; }

    public ICommand? ClearLauncherCommand { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsPrepared
    {
        get => _isPrepared;
        protected set => SetProperty(ref _isPrepared, value);
    }

    public abstract bool CanHandleAlbumTitle(string? albumTitle);

    public virtual bool HideUntilCaptured => false;

    public virtual bool ForceUseTargetClientAreaCapture => false;

    public virtual int ClientAreaCropLeftInset => 0;

    public virtual int ClientAreaCropTopInset => 0;

    public virtual int ClientAreaCropRightInset => 0;

    public virtual int ClientAreaCropBottomInset => 0;
    
    public virtual EmulatorCaptureMode PreferredCaptureMode => EmulatorCaptureMode.DirectComposition;

    public virtual void Prepare() => IsPrepared = true;

    public virtual void OnShowViewModel() => IsActive = true;

    public virtual void OnViewFullyVisible()
    {
    }

    public virtual void OnLeaveViewModel() => IsActive = false;

    public virtual void SaveSettings()
    {
    }

    public virtual void LoadSettings()
    {
    }

    public virtual Task LoadSettingsAsync()
    {
        LoadSettings();
        return Task.CompletedTask;
    }

    public virtual ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        if (OperatingSystem.IsLinux() && TryBuildLinuxDesktopEntryStartInfo(launcherPath, romPath, out var desktopStartInfo))
            return desktopStartInfo;

        var executablePath = ResolveLauncherExecutablePath(launcherPath) ?? launcherPath;
        var workingDirectory = ResolveLauncherWorkingDirectory(launcherPath) ??
                               Path.GetDirectoryName(executablePath) ??
                               string.Empty;

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        ApplyLinuxLaunchEnvironment(startInfo, executablePath);

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    private static bool TryBuildLinuxDesktopEntryStartInfo(string? launcherPath, string romPath, out ProcessStartInfo startInfo)
    {
        startInfo = null!;

        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(launcherPath))
            return false;

        var normalizedLauncherPath = launcherPath.Trim();
        if (!normalizedLauncherPath.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) || !File.Exists(normalizedLauncherPath))
            return false;

        if (!TryParseDesktopExecLine(normalizedLauncherPath, out var execLine))
            return false;

        if (!TryTokenizeDesktopExecLine(execLine, normalizedLauncherPath, romPath, out var fileName, out var arguments, out var hasFieldCode))
            return false;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            WorkingDirectory = ResolveLauncherWorkingDirectory(normalizedLauncherPath) ?? string.Empty
        };

        ApplyLinuxLaunchEnvironment(startInfo, fileName);

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ApplyLinuxSandboxLaunchOverrides(startInfo);

        if (!hasFieldCode && !string.IsNullOrWhiteSpace(romPath))
            startInfo.ArgumentList.Add(romPath);

        return true;
    }

    private static void ApplyLinuxSandboxLaunchOverrides(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(startInfo.FileName))
            return;

        var executableName = Path.GetFileName(startInfo.FileName);
        if (string.IsNullOrWhiteSpace(executableName))
            return;

        var arguments = startInfo.ArgumentList.ToArray();

        if (string.Equals(executableName, "flatpak", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(executableName, "flatpak-spawn", StringComparison.OrdinalIgnoreCase))
        {
            InjectFlatpakEnvironmentArguments(startInfo.ArgumentList, 1);
            return;
        }

        if (!string.Equals(executableName, "env", StringComparison.OrdinalIgnoreCase))
            return;

        for (var i = 0; i < arguments.Length; i++)
        {
            var argumentName = Path.GetFileName(arguments[i]);
            if (string.Equals(argumentName, "flatpak", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argumentName, "flatpak-spawn", StringComparison.OrdinalIgnoreCase))
            {
                InjectFlatpakEnvironmentArguments(startInfo.ArgumentList, i + 2);
                return;
            }
        }
    }

    private static void InjectFlatpakEnvironmentArguments(System.Collections.ObjectModel.Collection<string> argumentList, int insertIndex)
    {
        var environmentArguments = new[]
        {
            "--env=GDK_BACKEND=x11",
            "--env=SDL_VIDEODRIVER=x11",
            "--env=QT_QPA_PLATFORM=xcb"
        };

        foreach (var environmentArgument in environmentArguments)
        {
            argumentList.Insert(insertIndex++, environmentArgument);
        }
    }

    private static void ApplyLinuxLaunchEnvironment(ProcessStartInfo startInfo, string executablePath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        startInfo.Environment["SDL_VIDEODRIVER"] = "x11";
        startInfo.Environment["GDK_BACKEND"] = "x11";
        startInfo.Environment["QT_QPA_PLATFORM"] = "xcb";
        startInfo.Environment.Remove("WAYLAND_DISPLAY");

        // Let AppImage binaries run on systems without functional FUSE mounts,
        // but avoid forcing extraction when FUSE appears to be available.
        if (IsAppImagePath(executablePath) && !HasLikelyFuseSupport())
        {
            startInfo.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
        }
    }

    private static bool IsAppImagePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        return executablePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLikelyFuseSupport()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            if (!File.Exists("/dev/fuse"))
                return false;

            return IsCommandAvailable("fusermount3") || IsCommandAvailable("fusermount");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (OperatingSystem.IsWindows())
            return false;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var pathEntries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries)
        {
            try
            {
                var candidate = Path.Combine(entry, command);
                if (!File.Exists(candidate))
                    continue;

                var mode = File.GetUnixFileMode(candidate);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return true;
            }
            catch
            {
                // Ignore inaccessible path entries.
            }
        }

        return false;
    }

    public static bool IsMacAppBundle(string? launcherPath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(launcherPath))
            return false;

        return launcherPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(launcherPath);
    }

    public static string? ResolveLauncherExecutablePath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        if (TryResolveLinuxDesktopEntryExecutablePath(launcherPath, out var desktopExecutablePath))
            return desktopExecutablePath;

        if (!IsMacAppBundle(launcherPath))
            return launcherPath;

        var executableDirectory = Path.Combine(launcherPath, "Contents", "MacOS");
        if (!Directory.Exists(executableDirectory))
            return null;

        try
        {
            var executable = Directory.EnumerateFiles(executableDirectory)
                .FirstOrDefault(path => IsFileExecutable(path));
            return executable;
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveLauncherWorkingDirectory(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return null;

        if (IsMacAppBundle(launcherPath))
            return Path.Combine(launcherPath, "Contents", "MacOS");

        return Path.GetDirectoryName(launcherPath);
    }

    private static bool TryResolveLinuxDesktopEntryExecutablePath(string launcherPath, out string executablePath)
    {
        executablePath = string.Empty;

        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(launcherPath) ||
            !launcherPath.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(launcherPath))
        {
            return false;
        }

        if (!TryParseDesktopExecLine(launcherPath, out var execLine))
            return false;

        if (!TryTokenizeDesktopExecLine(execLine, launcherPath, string.Empty, out var fileName, out _, out _))
            return false;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        executablePath = fileName;
        return true;
    }

    private static bool TryParseDesktopExecLine(string desktopFilePath, out string execLine)
    {
        execLine = string.Empty;

        try
        {
            var inDesktopEntrySection = false;
            foreach (var rawLine in File.ReadLines(desktopFilePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.Equals("[Desktop Entry]", StringComparison.OrdinalIgnoreCase))
                {
                    inDesktopEntrySection = true;
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    if (inDesktopEntrySection)
                        break;

                    continue;
                }

                if (!inDesktopEntrySection || !line.StartsWith("Exec=", StringComparison.Ordinal))
                    continue;

                execLine = line.Substring("Exec=".Length).Trim();
                return !string.IsNullOrWhiteSpace(execLine);
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryTokenizeDesktopExecLine(
        string execLine,
        string launcherPath,
        string romPath,
        out string fileName,
        out List<string> arguments,
        out bool hasFieldCode)
    {
        fileName = string.Empty;
        arguments = new List<string>();
        hasFieldCode = false;

        var tokens = new List<string>();
        var token = new StringBuilder();
        var quoteChar = '\0';

        for (var i = 0; i < execLine.Length; i++)
        {
            var current = execLine[i];

            if (current == '\\' && i + 1 < execLine.Length)
            {
                token.Append(execLine[++i]);
                continue;
            }

            if (quoteChar != '\0')
            {
                if (current == quoteChar)
                {
                    quoteChar = '\0';
                    continue;
                }

                token.Append(current);
                continue;
            }

            if (current == '"' || current == '\'')
            {
                quoteChar = current;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }

                continue;
            }

            token.Append(current);
        }

        if (token.Length > 0)
            tokens.Add(token.ToString());

        if (tokens.Count == 0)
            return false;

        foreach (var rawToken in tokens)
        {
            var expandedToken = ExpandDesktopExecToken(rawToken, launcherPath, romPath, out var tokenUsedFieldCode);
            hasFieldCode |= tokenUsedFieldCode;

            if (string.IsNullOrWhiteSpace(expandedToken))
                continue;

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = expandedToken;
            else
                arguments.Add(expandedToken);
        }

        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static string ExpandDesktopExecToken(string token, string launcherPath, string romPath, out bool usedFieldCode)
    {
        usedFieldCode = false;

        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var result = new StringBuilder();
        for (var i = 0; i < token.Length; i++)
        {
            var current = token[i];
            if (current != '%')
            {
                result.Append(current);
                continue;
            }

            if (i + 1 >= token.Length)
            {
                result.Append(current);
                continue;
            }

            var code = token[++i];
            switch (code)
            {
                case '%':
                    result.Append('%');
                    break;
                case 'f':
                case 'F':
                    usedFieldCode = true;
                    result.Append(romPath);
                    break;
                case 'u':
                case 'U':
                    usedFieldCode = true;
                    result.Append(BuildFileUriArgument(romPath));
                    break;
                case 'k':
                    result.Append(launcherPath);
                    break;
                case 'c':
                    result.Append(Path.GetFileNameWithoutExtension(launcherPath));
                    break;
                case 'i':
                    break;
                default:
                    result.Append('%').Append(code);
                    break;
            }
        }

        return result.ToString();
    }

    private static string BuildFileUriArgument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        catch
        {
            return path;
        }
    }

    public virtual int CaptureStartupDelayMs => 3000;

    public virtual void PrepareProcessForCapture(Process process) => CaptureService?.PrepareProcessForCapture(process);

    public virtual void PrepareWindowForCapture(IntPtr hwnd) => CaptureService?.PrepareWindowForCapture(hwnd);

    public virtual IntPtr FindPreferredWindowHandle(Process process)
        => CaptureService?.FindPreferredWindowHandle(process) ?? (TryGetMainWindowHandle(process, out var mainWindowHandle) ? mainWindowHandle : IntPtr.Zero);

    protected static bool IsProcessAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    protected static bool TryGetMainWindowHandle(Process process, out IntPtr mainWindowHandle)
    {
        mainWindowHandle = IntPtr.Zero;

        if (!IsProcessAlive(process))
            return false;

        try
        {
            process.Refresh();
            mainWindowHandle = process.MainWindowHandle;
            return true;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to read process main window handle.", ex);
            return false;
        }
    }

    protected static IReadOnlyList<IntPtr> EnumerateProcessTopLevelWindows(Process process, bool includeHiddenWindows = false, string? fallbackTitleHint = null)
    {
        if (OperatingSystem.IsLinux())
        {
            var pWindows = new List<IntPtr>();

            try
            {
                var hwnds = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByPid(process.Id);

                // If PID-based search fails (common with XWayland forks or launcher scripts), fallback to a broad title search
                if (hwnds.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTitleHint))
                {
                    hwnds = AES_Emulation.Linux.API.LinuxWindowHelper.FindWindowsByTitle(fallbackTitleHint);
                }

                foreach (var hwnd in hwnds)
                {
                    if (includeHiddenWindows || AES_Emulation.Linux.API.LinuxWindowHelper.IsWindowVisible(hwnd))
                        pWindows.Add(hwnd);
                }
            }
            catch (Exception ex)
            {
                SLog.Debug("Failed to enumerate Linux process windows.", ex);
            }
            return pWindows;
        }

        if (!OperatingSystem.IsWindows())
            return Array.Empty<IntPtr>();

        try
        {
            process.Refresh();
            var processId = (uint)process.Id;
            var handles = new List<IntPtr>();

            EnumWindows((hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero)
                    return true;

                if (!includeHiddenWindows && !IsWindowVisible(hwnd))
                    return true;

                if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
                    return true;

                handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            return handles;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to enumerate process windows.", ex);
            return Array.Empty<IntPtr>();
        }
    }

    protected static void HideProcessWindowsForCapture(Process process, string? fallbackTitleHint = null)
    {
        if (OperatingSystem.IsLinux())
        {
            var svc = new AES_Emulation.Linux.Platform.LinuxScreenCaptureService();
            foreach (var h in EnumerateProcessTopLevelWindows(process, true, fallbackTitleHint))
            {
                svc.PrepareWindowForCapture(h);
            }
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

        // Note: For now, we delegate to the service if available, 
        // but many inheritance patterns rely on these static methods.
    }

    protected static void HideWindowForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (OperatingSystem.IsLinux())
        {
            var svc = new AES_Emulation.Linux.Platform.LinuxScreenCaptureService();
            svc.PrepareWindowForCapture(hwnd);
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

        Win32API.RemoveWindowDecorations(hwnd);
        Win32API.MoveAway(hwnd);
        Win32API.SetWindowOpacity(hwnd, 0);
    }

    protected static IntPtr FindBestProcessWindowHandle(
        Process process,
        bool preferSpecificRenderWindow,
        bool allowHiddenWindows,
        Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow,
        string? fallbackTitleHint = null)
    {
        if (OperatingSystem.IsLinux())
        {
            if (!TryGetMainWindowHandle(process, out var linuxMain))
            {
                if (!TryResolveDetachedProcess(process, out process) || !TryGetMainWindowHandle(process, out linuxMain))
                    return FindBestProcesslessWindowHandle(preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);
            }

            var pWindows = EnumerateProcessTopLevelWindows(process, allowHiddenWindows, fallbackTitleHint);
            IntPtr linuxBest = IntPtr.Zero;

            foreach (var w1 in pWindows)
            {
                if (preferSpecificRenderWindow && isPreferredRenderWindow != null && isPreferredRenderWindow(w1, linuxMain))
                {
                    linuxBest = w1;
                    break;
                }

                // If there's no preferred filter, or we haven't matched one yet, grab the first non-zero window we see
                if (linuxBest == IntPtr.Zero && w1 != IntPtr.Zero)
                    linuxBest = w1;
            }

            return linuxBest != IntPtr.Zero ? linuxBest : linuxMain;
        }

        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        IntPtr mainWindowHandle;
        uint processId;

        try
        {
            process.Refresh();
            processId = (uint)process.Id;
            mainWindowHandle = process.MainWindowHandle;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to refresh process while scoring window candidates.", ex);

            if (!TryResolveDetachedProcess(process, out process))
                return FindBestProcesslessWindowHandle(preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);

            try
            {
                process.Refresh();
                processId = (uint)process.Id;
                mainWindowHandle = process.MainWindowHandle;
            }
            catch (Exception fallbackEx)
            {
                SLog.Debug("Failed to refresh a detached emulator process candidate.", fallbackEx);
                return IntPtr.Zero;
            }
        }

        IntPtr bestHandle = IntPtr.Zero;
        long bestScore = long.MinValue;

        EnumWindows((hwnd, _) =>
        {
            var score = ScoreProcessWindowCandidate(
                hwnd,
                processId,
                mainWindowHandle,
                preferSpecificRenderWindow,
                allowHiddenWindows,
                isPreferredRenderWindow);

            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        if (bestHandle != IntPtr.Zero)
            return bestHandle;

        if (preferSpecificRenderWindow)
            return IntPtr.Zero;

        return ScoreProcessWindowCandidate(
                mainWindowHandle,
                processId,
                mainWindowHandle,
                preferSpecificRenderWindow,
                allowHiddenWindows,
                isPreferredRenderWindow) > long.MinValue
            ? mainWindowHandle
            : IntPtr.Zero;
    }

    protected static IntPtr FindBestProcesslessWindowHandle(
        bool preferSpecificRenderWindow,
        bool allowHiddenWindows,
        Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow)
    {
        IntPtr bestHandle = IntPtr.Zero;
        long bestScore = long.MinValue;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero)
                return true;

            if (!allowHiddenWindows && !IsWindowVisible(hwnd))
                return true;

            if (!GetWindowRect(hwnd, out RECT windowRect))
                return true;

            var width = Math.Max(0, windowRect.Right - windowRect.Left);
            var height = Math.Max(0, windowRect.Bottom - windowRect.Top);
            if (width <= 0 || height <= 0)
                return true;

            long score = (long)width * height * 10;
            score += IsWindowVisible(hwnd) ? 100_000 : -100_000;

            if (GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                score += 1_000_000;

            if (width >= 640 && height >= 360)
                score += 250_000;

            if (preferSpecificRenderWindow)
            {
                if (isPreferredRenderWindow == null || !isPreferredRenderWindow(hwnd, IntPtr.Zero))
                    return true;

                score += 5_000_000;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestHandle;
    }

    protected static bool TryResolveDetachedProcess(Process process, out Process resolvedProcess)
    {
        resolvedProcess = null!;

        try
        {
            string? processName = null;
            try
            {
                processName = Path.GetFileNameWithoutExtension(process.StartInfo?.FileName ?? string.Empty);
            }
            catch
            {
                processName = null;
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                try
                {
                    processName = process.ProcessName;
                }
                catch
                {
                    processName = null;
                }
            }

            if (string.IsNullOrWhiteSpace(processName))
                return false;

            var candidates = Process.GetProcessesByName(processName);
            if (candidates.Length == 0)
                return false;

            Process? bestCandidate = null;
            DateTime bestStartTime = DateTime.MinValue;
            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate.StartTime > bestStartTime)
                    {
                        bestStartTime = candidate.StartTime;
                        bestCandidate = candidate;
                    }
                }
                catch
                {
                    // ignore processes we cannot inspect
                }
            }

            if (bestCandidate != null)
            {
                resolvedProcess = bestCandidate;
                return true;
            }
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to resolve detached process candidate.", ex);
        }

        return false;
    }

    protected static long ScoreProcessWindowCandidate(
        IntPtr hwnd,
        uint processId,
        IntPtr mainWindowHandle,
        bool preferSpecificRenderWindow,
        bool allowHiddenWindows,
        Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow)
    {
        if (hwnd == IntPtr.Zero)
            return long.MinValue;

        var isVisible = IsWindowVisible(hwnd);
        if (!isVisible && !allowHiddenWindows)
            return long.MinValue;

        if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
            return long.MinValue;

        if (!GetWindowRect(hwnd, out RECT windowRect))
            return long.MinValue;

        var width = Math.Max(0, windowRect.Right - windowRect.Left);
        var height = Math.Max(0, windowRect.Bottom - windowRect.Top);
        if (width <= 0 || height <= 0)
            return long.MinValue;

        long score = (long)width * height * 10;
        score += isVisible ? 100_000 : -100_000;

        if (GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
            score += 1_000_000;

        if (hwnd == mainWindowHandle)
            score += 750_000;

        if (width >= 640 && height >= 360)
            score += 250_000;

        if (preferSpecificRenderWindow)
        {
            if (isPreferredRenderWindow == null || !isPreferredRenderWindow(hwnd, mainWindowHandle))
                return long.MinValue;

            score += 5_000_000;
        }

        return score;
    }

    public virtual bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle) => hwnd != IntPtr.Zero;

    public virtual async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            const int maxAttempts = 240;
            const int delayMs = 100;
            IntPtr observedHwnd = IntPtr.Zero;
            int stableCount = 0;

            var svc = new AES_Emulation.Linux.Platform.LinuxScreenCaptureService();

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsProcessAlive(process))
                    return IntPtr.Zero;

                var hwnd = this.FindPreferredWindowHandle(process);

                if (hwnd != IntPtr.Zero)
                {
                    if (hwnd == observedHwnd)
                    {
                        stableCount++;
                        if (stableCount >= 2)
                        {
                            svc.PrepareWindowForCapture(hwnd);
                            return hwnd;
                        }
                    }
                    else
                    {
                        observedHwnd = hwnd;
                        stableCount = 1;
                    }
                }
                else
                {
                    observedHwnd = IntPtr.Zero;
                    stableCount = 0;
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            return IntPtr.Zero;
        }

        if (CaptureService != null)
            return await CaptureService.ResolveCaptureTargetAsync(process, this, cancellationToken);

        return process.MainWindowHandle;
    }

    protected static void TryWaitForInputIdle(Process process, int timeoutMs)
    {
        try
        {
            process.WaitForInputIdle(timeoutMs);
        }
        catch (Exception ex)
        {
            SLog.Debug("Emulator did not provide an input-idle state; falling back to polling.", ex);
        }
    }

    private static bool IsFileExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            if (OperatingSystem.IsWindows())
                return true;

            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    protected static string GetWindowTitle(IntPtr hwnd)
    {
        if (OperatingSystem.IsLinux())
            return AES_Emulation.Linux.API.LinuxWindowHelper.GetWindowTitle(hwnd);

        if (!OperatingSystem.IsWindows())
            return string.Empty;

        return GetWindowTitleWindows(hwnd);
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowTitleWindows(IntPtr hwnd)
    {
        try
        {
            var builder = new StringBuilder(256);
            return GetWindowText(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to retrieve window title.", ex);
            return string.Empty;
        }
    }

    protected static string GetWindowClassName(IntPtr hwnd)
    {
        if (OperatingSystem.IsLinux())
            return AES_Emulation.Linux.API.LinuxWindowHelper.GetWindowClassName(hwnd);

        if (!OperatingSystem.IsWindows())
            return string.Empty;

        return GetWindowClassNameWindows(hwnd);
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowClassNameWindows(IntPtr hwnd)
    {
        try
        {
            var builder = new StringBuilder(256);
            return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to retrieve window class name.", ex);
            return string.Empty;
        }
    }

    protected static int GetWindowStyle(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        return GetWindowStyleWindows(hwnd);
    }

    [SupportedOSPlatform("windows")]
    private static int GetWindowStyleWindows(IntPtr hwnd)
    {
        try
        {
            return GetWindowLong(hwnd, GWL_STYLE);
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to retrieve window style.", ex);
            return 0;
        }
    }

    protected const int GWL_STYLE = -16;
    protected const uint GW_OWNER = 4;
    protected const int WS_CAPTION = 0x00C00000;
    protected const int WS_THICKFRAME = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
