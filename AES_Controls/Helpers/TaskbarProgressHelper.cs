using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using log4net;
using System.Runtime.InteropServices;

namespace AES_Controls.Helpers;

/// <summary>
/// Specifies the state of the taskbar progress bar.
/// </summary>
public enum TaskbarProgressBarState
{
    /// <summary>No progress is displayed.</summary>
    NoProgress = 0,
    /// <summary>The progress indicator is indeterminate (cycling).</summary>
    Indeterminate = 1,
    /// <summary>The progress indicator is normal (green).</summary>
    Normal = 2,
    /// <summary>An error occurred (red).</summary>
    Error = 4,
    /// <summary>The progress is paused (yellow).</summary>
    Paused = 8
}

[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3
    void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    void SetProgressState(IntPtr hwnd, TaskbarProgressBarState tbpFlags);
    void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    void UnregisterTab(IntPtr hwndTab);
    void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
    void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
    void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
    void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
    void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
}

[ComImport]
[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarList { }

/// <summary>
/// Helper for managing the Windows taskbar progress bar.
/// Currently only supports Windows platforms.
/// </summary>
public static class TaskbarProgressHelper
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TaskbarProgressHelper));
    private static readonly ITaskbarList3? _taskbarList;
    private static readonly bool _isSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    static TaskbarProgressHelper()
    {
        if (_isSupported)
        {
            try
            {
                _taskbarList = (ITaskbarList3)new TaskbarList();
                _taskbarList.HrInit();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to initialize ITaskbarList3. Taskbar progress will be disabled.", ex);
                _isSupported = false;
            }
        }
    }

    private static IntPtr GetHwnd()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Use TryGetPlatformHandle() to safely obtain the native window handle.
            return desktop.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Sets the progress value for the taskbar icon of the main application window.
    /// </summary>
    /// <param name="current">The current progress value.</param>
    /// <param name="total">The total progress value (e.g., duration).</param>
    public static void SetProgressValue(double current, double total)
    {
        if (!_isSupported || _taskbarList == null) return;
        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _taskbarList.SetProgressValue(hwnd, (ulong)current, (ulong)total);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to set taskbar progress value", ex);
        }
    }

    /// <summary>
    /// Sets the progress state for the taskbar icon of the main application window.
    /// </summary>
    /// <param name="state">The new state of the progress bar.</param>
    public static void SetProgressState(TaskbarProgressBarState state)
    {
        if (!_isSupported || _taskbarList == null) return;
        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _taskbarList.SetProgressState(hwnd, state);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to set taskbar progress state", ex);
        }
    }
}
