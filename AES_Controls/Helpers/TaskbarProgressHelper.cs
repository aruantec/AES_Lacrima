using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using log4net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

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

// ReSharper disable once InconsistentNaming
// SYSLIB1062 is suppressed because it's a false positive in some IDE versions when <AllowUnsafeBlocks> is already enabled in the project file.
#pragma warning disable SYSLIB1062
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[GeneratedComInterface]
internal partial interface ITaskbarList3
{
#pragma warning restore SYSLIB1062
    // ITaskbarList
    [PreserveSig]
    int HrInit();
    [PreserveSig]
    int AddTab(IntPtr hwnd);
    [PreserveSig]
    int DeleteTab(IntPtr hwnd);
    [PreserveSig]
    int ActivateTab(IntPtr hwnd);
    [PreserveSig]
    int SetActiveAlt(IntPtr hwnd);

    // ITaskbarList2
    [PreserveSig]
    int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ITaskbarList3
    [PreserveSig]
    int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    [PreserveSig]
    int SetProgressState(IntPtr hwnd, TaskbarProgressBarState tbpFlags);
    [PreserveSig]
    int RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
    [PreserveSig]
    int UnregisterTab(IntPtr hwndTab);
    [PreserveSig]
    int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
    [PreserveSig]
    int SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
    [PreserveSig]
    int ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
    [PreserveSig]
    int ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
    [PreserveSig]
    int ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
    [PreserveSig]
    int SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    [PreserveSig]
    int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
    [PreserveSig]
    int SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct THUMBBUTTON
{
    public uint dwMask;
    public uint iId;
    public uint iBitmap;
    public IntPtr hIcon;
    public unsafe fixed char szTip[260];
    public uint dwFlags;
}

[Flags]
public enum THUMBBUTTONMASK : uint
{
    Bitmap = 0x00000001,
    Icon = 0x00000002,
    Tooltip = 0x00000004,
    Flags = 0x00000008
}

[Flags]
public enum THUMBBUTTONFLAGS : uint
{
    Enabled = 0x00000000,
    Disabled = 0x00000001,
    DismissOnClick = 0x00000002,
    NoBackground = 0x00000004,
    Hidden = 0x00000008,
    NonInteractive = 0x00000010
}

/// <summary>
/// Identifier for taskbar thumbnail buttons.
/// </summary>
public enum TaskbarButtonId : uint
{
    Previous = 101,
    PlayPause = 102,
    Next = 103
}

/// <summary>
/// Represents a button in the taskbar thumbnail toolbar.
/// </summary>
public class TaskbarButton
{
    public TaskbarButtonId Id { get; set; }
    public IntPtr HIcon { get; set; }
    public string Tooltip { get; set; } = string.Empty;
    public THUMBBUTTONFLAGS Flags { get; set; } = THUMBBUTTONFLAGS.Enabled;
}

#if WINDOWS_TASKBAR
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
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For(typeof(TaskbarProgressHelper));
    private static ITaskbarList3? _taskbarList;
    private static bool _isSupported = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static void EnsureInitialized()
    {
        if (!_isSupported || _taskbarList != null) return;

        try
        {
            var guid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
            
            // In .NET 10 with AOT/Trimming, Type.GetTypeFromCLSID often fails with System.NotSupportedException.
            // We use CoCreateInstance directly to bypass the built-in COM requirement.
            int hr = CoCreateInstance(ref guid, IntPtr.Zero, 1, ref _taskbarListIid, out var taskbarListPtr);
            Log.Debug($"CoCreateInstance for ITaskbarList3 returned HRESULT: 0x{hr:X8}");

            if (hr >= 0 && taskbarListPtr != IntPtr.Zero)
            {
                var cw = new StrategyBasedComWrappers();
                _taskbarList = (ITaskbarList3)cw.GetOrCreateObjectForComInstance(taskbarListPtr, CreateObjectFlags.None);
                
                // We keep one reference in _taskbarList, so we can release the local pointer
                Marshal.Release(taskbarListPtr);

                if (_taskbarList != null)
                {
                    hr = _taskbarList.HrInit();
                    Log.Debug($"ITaskbarList3.HrInit returned HRESULT: 0x{hr:X8}");
                    if (hr < 0)
                    {
                        Log.Warn($"ITaskbarList3.HrInit failed with HRESULT: 0x{hr:X8}");
                        _taskbarList = null;
                        _isSupported = false;
                    }
                }
            }
            else
            {
                Log.Warn($"CoCreateInstance for ITaskbarList3 failed with HRESULT: 0x{hr:X8}");
                _isSupported = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to initialize ITaskbarList3 via CoCreateInstance. Taskbar progress will be disabled.", ex);
            _taskbarList = null;
            _isSupported = false;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    private static Guid _taskbarListIid = new Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

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
        if (!_isSupported) return;
        EnsureInitialized();
        if (_taskbarList == null) return;

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
        if (!_isSupported) return;
        EnsureInitialized();
        if (_taskbarList == null) return;

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

    /// <summary>
    /// Adds a set of buttons to the thumbnail toolbar of the main application window.
    /// This method should only be called once to initialize the toolbar.
    /// </summary>
    /// <param name="buttons">The buttons to add.</param>
    public static void SetThumbnailButtons(TaskbarButton[] buttons)
    {
        if (!_isSupported || buttons == null || buttons.Length == 0) return;
        EnsureInitialized();
        if (_taskbarList == null) return;

        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int hr = _taskbarList.HrInit();
            if (hr < 0)
            {
                Log.Debug($"ITaskbarList3.HrInit already initialized or returned: 0x{hr:X}");
            }
            
            var nativeButtons = CreateNativeButtons(buttons);
            
            Log.Debug($"Calling ThumbBarAddButtons for HWND 0x{hwnd:X16}");
            hr = _taskbarList.ThumbBarAddButtons(hwnd, (uint)buttons.Length, nativeButtons);
            Log.Info($"ThumbBarAddButtons result: 0x{hr:X8} for HWND 0x{hwnd:X16}");
            if (hr < 0)
            {
                Log.Warn($"ThumbBarAddButtons failed with HRESULT: 0x{hr:X} for HWND 0x{hwnd:X16}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to set taskbar thumbnail buttons", ex);
        }
    }

    /// <summary>
    /// Updates the state or icon of existing buttons in the thumbnail toolbar.
    /// </summary>
    /// <param name="buttons">The updated button definitions.</param>
    public static void UpdateThumbnailButtons(TaskbarButton[] buttons)
    {
        if (!_isSupported || buttons == null || buttons.Length == 0) return;
        EnsureInitialized();
        if (_taskbarList == null) return;

        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var nativeButtons = CreateNativeButtons(buttons);
            int hr = _taskbarList.ThumbBarUpdateButtons(hwnd, (uint)buttons.Length, nativeButtons);
            if (hr < 0)
            {
                Log.Debug($"ThumbBarUpdateButtons failed with HRESULT: 0x{hr:X8} for HWND 0x{hwnd:X16}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to update taskbar thumbnail buttons", ex);
        }
    }

    private static THUMBBUTTON[] CreateNativeButtons(TaskbarButton[] buttons)
    {
        var nativeButtons = new THUMBBUTTON[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            var btn = new THUMBBUTTON
            {
                dwMask = (uint)(THUMBBUTTONMASK.Icon | THUMBBUTTONMASK.Tooltip | THUMBBUTTONMASK.Flags),
                iId = (uint)buttons[i].Id,
                hIcon = buttons[i].HIcon,
                dwFlags = (uint)buttons[i].Flags
            };

            unsafe
            {
                string tooltip = buttons[i].Tooltip ?? string.Empty;
                int length = Math.Min(tooltip.Length, 259);
                for (int j = 0; j < length; j++)
                {
                    btn.szTip[j] = tooltip[j];
                }
                btn.szTip[length] = '\0';
            }
            nativeButtons[i] = btn;
        }
        return nativeButtons;
    }

    /// <summary>
    /// Creates an HICON from an Avalonia Geometry.
    /// Useful for generating taskbar button icons from UI paths.
    /// </summary>
    public static IntPtr CreateHIconFromGeometry(Geometry geometry, Color color, int size = 32)
    {
        if (!_isSupported) return IntPtr.Zero;

        try
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = rtb.CreateDrawingContext())
            {
                // Source geometries might use 0..100 coordinates, scale them down
                var bounds = geometry.GetRenderBounds(new Pen(Brushes.White, 0));
                var maxBound = Math.Max(bounds.Width, bounds.Height);
                if (maxBound > 0)
                {
                    var scale = (size * 0.7) / maxBound;
                    var translate = new Vector(
                        (size - bounds.Width * scale) / 2 - bounds.Left * scale,
                        (size - bounds.Height * scale) / 2 - bounds.Top * scale);

                    using (ctx.PushTransform(Matrix.CreateTranslation(translate) * Matrix.CreateScale(scale, scale)))
                    {
                        ctx.DrawGeometry(new SolidColorBrush(color), null, geometry);
                    }
                }
                else
                {
                    ctx.DrawGeometry(new SolidColorBrush(color), null, geometry);
                }
            }

            return CreateHIconFromBitmap(rtb);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to create HICON from geometry", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates an HICON from a character using the specified font family.
    /// Standard Windows icons often use Segoe MDL2 Assets or Segoe Fluent Icons.
    /// </summary>
    public static IntPtr CreateHIconFromCharacter(string character, Color color, string fontFamily = "Segoe MDL2 Assets", int size = 64)
    {
        if (!_isSupported) return IntPtr.Zero;

        try
        {
            // Use larger size for better quality then icon will be scaled by Windows
            using var rtb = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = rtb.CreateDrawingContext())
            {
                var glyphTypeface = new Typeface(fontFamily);
                var formattedText = new FormattedText(
                    character,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    glyphTypeface,
                    size * 0.8, // Slightly larger
                    new SolidColorBrush(color));

                var origin = new Point((size - formattedText.Width) / 2, (size - formattedText.Height) / 2);
                ctx.DrawText(formattedText, origin);
            }

            return CreateHIconFromBitmap(rtb);
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to create HICON from character", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates an HICON from an Avalonia Bitmap.
    /// </summary>
    public static unsafe IntPtr CreateHIconFromBitmap(Bitmap bitmap)
    {
        if (!_isSupported) return IntPtr.Zero;

        var size = bitmap.PixelSize;
        var pixels = new byte[size.Width * size.Height * 4];

        fixed (byte* p = pixels)
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (IntPtr)p, pixels.Length, size.Width * 4);
        }

        IntPtr hBitmap = CreateNativeBitmap(size.Width, size.Height, pixels, 32);
        // Mask must be 1-bit monochrome. For 32-bit ARGB, the mask is often ignored or used inversely.
        // We provide a full-size dummy mask (all zeros usually means opaque but it depends on fIcon and bit depth).
        // Actually, for hbmMask in ICONINFO: 0 = opaque, 1 = transparent.
        byte[] maskBytes = new byte[((size.Width + 15) / 16 * 2) * size.Height];
        IntPtr hMask = CreateNativeBitmap(size.Width, size.Height, maskBytes, 1);

        var iconInfo = new ICONINFO
        {
            fIcon = true,
            hbmColor = hBitmap,
            hbmMask = hMask
        };

        IntPtr hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(hBitmap);
        DeleteObject(hMask);

        return hIcon;
    }

    private static IntPtr CreateNativeBitmap(int width, int height, byte[] pixels, uint bitsPerPixel)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return CreateBitmap(width, height, 1, bitsPerPixel, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, IntPtr lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    private static Delegate? _wndProcDelegate;
    private static IntPtr _prevWndProc = IntPtr.Zero;
    private const int GWL_WNDPROC = -4;
    private const int WM_COMMAND = 0x0111;

    // handle that we previously hooked; used to restore original proc when window changes
    private static IntPtr _hookedHwnd = IntPtr.Zero;
    private static Action<TaskbarButtonId>? _hookCallback;

    private delegate IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Unhooks any previously hooked window and restores its original window procedure.
    /// </summary>
    private static void ResetHook()
    {
        if (_hookedHwnd != IntPtr.Zero && _prevWndProc != IntPtr.Zero)
        {
            try
            {
                SetWindowLongPtr(_hookedHwnd, GWL_WNDPROC, _prevWndProc);
            }
            catch { }
            finally
            {
                _hookedHwnd = IntPtr.Zero;
                _prevWndProc = IntPtr.Zero;
                _hookCallback = null;
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8) return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Hooks the message loop of an Avalonia Window to receive thumbnail button click events.
    /// </summary>
    public static void HookWindow(Avalonia.Controls.Window window, Action<TaskbarButtonId> onButtonClick)
    {
        if (!_isSupported) return;

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // if already hooked to the same window just update the callback
        if (_hookedHwnd == hwnd)
        {
            _hookCallback = onButtonClick;
            return;
        }

        // unhook previous window (if any)
        ResetHook();

        _hookCallback = onButtonClick;
        _hookedHwnd = hwnd;

        WndProcHandler wndProc = (hWnd, msg, wParam, lParam) =>
        {
            if (msg == WM_COMMAND)
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                uint notifyCode = (uint)((wParam.ToInt64() >> 16) & 0xFFFF);

                // THBN_CLICKED = 0x1800
                if ((notifyCode == 0x1800 || notifyCode == 0) && Enum.IsDefined(typeof(TaskbarButtonId), id))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _hookCallback?.Invoke((TaskbarButtonId)id));
                }
            }
            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        };

        _wndProcDelegate = wndProc;
        _prevWndProc = SetWindowLongPtr(hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(wndProc));
    }
}
#else
/// <summary>
/// Helper for managing taskbar progress integration.
/// Non-Windows builds provide a no-op implementation so desktop Native AOT
/// can compile without COM interop.
/// </summary>
public static class TaskbarProgressHelper
{
    public static void SetProgressValue(double current, double total) { }

    public static void SetProgressState(TaskbarProgressBarState state) { }

    public static void SetThumbnailButtons(TaskbarButton[] buttons) { }

    public static void UpdateThumbnailButtons(TaskbarButton[] buttons) { }

    public static IntPtr CreateHIconFromGeometry(Geometry geometry, Color color, int size = 32) => IntPtr.Zero;

    public static IntPtr CreateHIconFromCharacter(string character, Color color, string fontFamily = "Segoe MDL2 Assets", int size = 64) => IntPtr.Zero;

    public static IntPtr CreateHIconFromBitmap(Bitmap bitmap) => IntPtr.Zero;

    public static bool DestroyIcon(IntPtr hIcon) => false;

    public static void HookWindow(Avalonia.Controls.Window window, Action<TaskbarButtonId> onButtonClick) { }
}
#endif
