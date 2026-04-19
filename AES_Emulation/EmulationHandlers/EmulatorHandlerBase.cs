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

        if (OperatingSystem.IsLinux())
        {
            startInfo.Environment["SDL_VIDEODRIVER"] = "x11";
            startInfo.Environment["GDK_BACKEND"] = "x11";
            startInfo.Environment["QT_QPA_PLATFORM"] = "xcb";
            startInfo.Environment.Remove("WAYLAND_DISPLAY");
        }

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
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

    public virtual int CaptureStartupDelayMs => 3000;

    public virtual void PrepareProcessForCapture(Process process) => CaptureService?.PrepareProcessForCapture(process);

    public virtual void PrepareWindowForCapture(IntPtr hwnd) => CaptureService?.PrepareWindowForCapture(hwnd);

    public virtual IntPtr FindPreferredWindowHandle(Process process) => CaptureService?.FindPreferredWindowHandle(process) ?? process.MainWindowHandle;
    protected static IReadOnlyList<IntPtr> EnumerateProcessTopLevelWindows(Process process, bool includeHiddenWindows = false, string? fallbackTitleHint = null)
    {
        if (OperatingSystem.IsLinux())
        {
            var pWindows = new List<IntPtr>();
            try
            {
                process.Refresh();
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
            var pWindows = EnumerateProcessTopLevelWindows(process, allowHiddenWindows, fallbackTitleHint);
            IntPtr linuxMain = process.MainWindowHandle;
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
            return await CaptureService.ResolveCaptureTargetAsync(process, cancellationToken);

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
