using AES_Emulation.Windows.API;
using log4net;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AES_Emulation.EmulationHandlers;

public abstract class EmulatorHandlerBase : IEmulatorHandler
{
    private static readonly ILog SLog = LogManager.GetLogger(typeof(EmulatorHandlerBase));

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
            if (!SetProperty(ref _launcherPath, value))
                return;

            OnPropertyChanged(nameof(LauncherDisplayPath));
            OnPropertyChanged(nameof(HasLauncherPath));
        }
    }

    public string LauncherDisplayPath =>
        string.IsNullOrWhiteSpace(LauncherPath)
            ? "No executable selected"
            : LauncherPath;

    public bool HasLauncherPath => !string.IsNullOrWhiteSpace(LauncherPath);

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
        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty
        };

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public virtual int CaptureStartupDelayMs => 3000;

    public virtual void PrepareProcessForCapture(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;

        PrepareProcessForCaptureWindows(process);
    }

    public virtual void PrepareWindowForCapture(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows())
            return;

        PrepareWindowForCaptureWindows(hwnd);
    }

    public virtual IntPtr FindPreferredWindowHandle(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        return FindPreferredWindowHandleWindows(process);
    }
    protected static IReadOnlyList<IntPtr> EnumerateProcessTopLevelWindows(Process process, bool includeHiddenWindows = false)
    {
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
    public virtual bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle) => hwnd != IntPtr.Zero;

    public virtual async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 240;
        const int delayMs = 100;
        const int stableAttemptsBeforeAssign = 2;
        const int stableAttemptsBeforeStop = 6;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        IntPtr assignedHwnd = IntPtr.Zero;
        var assignedStableAttempts = 0;
        var hasAssignedHandle = false;

        TryWaitForInputIdle(process, 500);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HideUntilCaptured)
                PrepareProcessForCapture(process);

            var hwnd = FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
            {
                if (HideUntilCaptured)
                    PrepareWindowForCapture(hwnd);

                if (hwnd == observedHwnd)
                {
                    observedStableAttempts++;
                }
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                }

                var canAssign = !HideUntilCaptured || CanAssignWindow(hwnd, process.MainWindowHandle);

                if (canAssign && hwnd != assignedHwnd && observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    assignedHwnd = hwnd;
                    assignedStableAttempts = observedStableAttempts;
                    hasAssignedHandle = true;
                }
                else if (hwnd == assignedHwnd)
                {
                    assignedStableAttempts = observedStableAttempts;
                }

                if (hasAssignedHandle && assignedStableAttempts >= stableAttemptsBeforeStop)
                    break;
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return assignedHwnd;
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

    protected static void HideProcessWindowsForCapture(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;

        HideProcessWindowsForCaptureWindows(process);
    }

    [SupportedOSPlatform("windows")]
    private static void HideProcessWindowsForCaptureWindows(Process process)
    {
        uint processId;

        try
        {
            process.Refresh();
            processId = (uint)process.Id;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to refresh process while preparing windows for capture.", ex);
            return;
        }

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                return true;

            if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
                return true;

            HideWindowForCapture(hwnd);
            return true;
        }, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    protected static void PrepareProcessForCaptureWindows(Process process)
        => HideProcessWindowsForCaptureWindows(process);

    [SupportedOSPlatform("windows")]
    protected static void PrepareWindowForCaptureWindows(IntPtr hwnd)
        => HideWindowForCapture(hwnd);

    protected static void HideWindowForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        Win32API.RemoveWindowDecorations(hwnd);
        Win32API.MoveAway(hwnd);
        Win32API.SetWindowOpacity(hwnd, 255);
    }

    [SupportedOSPlatform("windows")]
    protected static IntPtr FindPreferredWindowHandleWindows(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: null);

    protected static IntPtr FindBestProcessWindowHandle(
        Process process,
        bool preferSpecificRenderWindow,
        bool allowHiddenWindows,
        Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow)
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;

        IntPtr mainWindowHandle;
        uint processId;

        try
        {
            process.Refresh();
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to refresh process while scoring window candidates.", ex);

            if (!TryResolveDetachedProcess(process, out process))
                return FindBestProcesslessWindowHandle(preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);

            try
            {
                process.Refresh();
            }
            catch (Exception fallbackEx)
            {
                SLog.Debug("Failed to refresh a detached emulator process candidate.", fallbackEx);
                return IntPtr.Zero;
            }
        }

        try
        {
            processId = (uint)process.Id;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to retrieve process Id while scoring window candidates.", ex);

            if (!TryResolveDetachedProcess(process, out process))
                return FindBestProcesslessWindowHandle(preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);

            try
            {
                processId = (uint)process.Id;
            }
            catch (Exception fallbackEx)
            {
                SLog.Debug("Failed to retrieve process Id for detached process candidate.", fallbackEx);
                return FindBestProcesslessWindowHandle(preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);
            }
        }

        try
        {
            mainWindowHandle = process.MainWindowHandle;
        }
        catch (Exception ex)
        {
            SLog.Debug("Failed to retrieve MainWindowHandle for process while scoring window candidates.", ex);
            mainWindowHandle = IntPtr.Zero;
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

    private static IntPtr FindBestProcesslessWindowHandle(
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

    private static bool TryResolveDetachedProcess(Process process, out Process resolvedProcess)
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
            SLog.Debug($"Resolving detached process for name='{processName}'. Candidates={candidates.Length}.");
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
                SLog.Debug($"Resolved detached emulator process candidate: pid={bestCandidate.Id}, name={bestCandidate.ProcessName}.");
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

    private static long ScoreProcessWindowCandidate(
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

    protected static string GetWindowTitle(IntPtr hwnd)
    {
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

    private const uint GW_OWNER = 4;
}
