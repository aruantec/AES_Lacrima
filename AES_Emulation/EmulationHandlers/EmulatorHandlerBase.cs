using AES_Emulation.Windows.API;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public abstract class EmulatorHandlerBase : IEmulatorHandler
{
    private bool _isActive;
    private bool _isPrepared;

    public event PropertyChangedEventHandler? PropertyChanged;

    public abstract string HandlerId { get; }

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

    public virtual ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty
        };

        startInfo.ArgumentList.Add(romPath);
        return startInfo;
    }

    public virtual void PrepareProcessForCapture(Process process)
    {
    }

    public virtual void PrepareWindowForCapture(IntPtr hwnd)
    {
    }

    public virtual IntPtr FindPreferredWindowHandle(Process process)
        => FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: null);

    public virtual bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle) => hwnd != IntPtr.Zero;

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

        uint processId;

        try
        {
            process.Refresh();
            processId = (uint)process.Id;
        }
        catch
        {
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

    protected static void HideWindowForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        Win32API.RemoveWindowDecorations(hwnd);
        Win32API.MoveAway(hwnd);
        Win32API.SetWindowOpacity(hwnd, 0);
    }

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
            mainWindowHandle = process.MainWindowHandle;
            processId = (uint)process.Id;
        }
        catch
        {
            return IntPtr.Zero;
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
        try
        {
            var builder = new StringBuilder(256);
            return GetWindowText(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    protected static string GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            var builder = new StringBuilder(256);
            return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    protected static int GetWindowStyle(IntPtr hwnd)
    {
        try
        {
            return GetWindowLong(hwnd, GWL_STYLE);
        }
        catch
        {
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
