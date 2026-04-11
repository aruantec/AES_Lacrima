using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima.Views;

public partial class VisualOverlayWindow : Window
{
    public VisualOverlayWindow()
    {
        InitializeComponent();
        IsHitTestVisible = false;
        Background = null;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void MoveToBottomOfStack()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hWnd = TryGetPlatformHandle()?.Handle;
            if (hWnd != null && hWnd != IntPtr.Zero)
            {
                SetWindowPos(hWnd.Value, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var hWnd = TryGetPlatformHandle()?.Handle;
            if (hWnd != null && hWnd != IntPtr.Zero)
            {
                var display = XOpenDisplay(IntPtr.Zero);
                if (display != IntPtr.Zero)
                {
                    XLowerWindow(display, hWnd.Value);
                    XCloseDisplay(display);
                }
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            LinuxWindowPlacement.TryConfigureAsNormalWindow(this);
        }
    }

    public void MoveResizeUnconstrained(PixelPoint position, int widthPixels, int heightPixels)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        LinuxWindowPlacement.TryMoveResize(this, position.X, position.Y, widthPixels, heightPixels);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XLowerWindow(IntPtr display, IntPtr w);

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
