using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AES_Emulation.Controls;
using AES_Lacrima.Mac.API;

namespace AES_Lacrima.Views;

public partial class PortalWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static bool _isApplicationShuttingDown;

    public static void SetApplicationShuttingDown()
    {
        _isApplicationShuttingDown = true;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public PortalWindow()
    {
        InitializeComponent();
    }

    public EmulatorCaptureHost? CaptureHostControl => this.FindControl<EmulatorCaptureHost>("CaptureControl");

    public void MoveResizeUnconstrained(PixelPoint position, int widthPixels, int heightPixels)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        LinuxWindowPlacement.TryMoveResize(this, position.X, position.Y, widthPixels, heightPixels);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Prevent closing unless the application is shutting down.
        // We handle visibility via Show/Hide/Move instead of closing.
        if (!_isApplicationShuttingDown)
        {
            e.Cancel = true;
            this.Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hWnd = TryGetPlatformHandle()?.Handle;
            if (hWnd != null && hWnd != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(hWnd.Value, GWL_EXSTYLE);
                SetWindowLong(hWnd.Value, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var handle = TryGetPlatformHandle()?.Handle;
            if (handle != null && handle != IntPtr.Zero)
                MacSystemDialogs.ConfigurePortalWindow(handle.Value);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            LinuxWindowPlacement.TryConfigureAsNormalWindow(this);
        }
    }
}
