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

    public void MoveBelowWindow(Window siblingWindow)
    {
        if (siblingWindow == null)
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hWnd = TryGetPlatformHandle()?.Handle;
            var siblingHandle = siblingWindow.TryGetPlatformHandle()?.Handle;
            if (hWnd != null && hWnd != IntPtr.Zero && siblingHandle != null && siblingHandle != IntPtr.Zero)
            {
                SetWindowPos(hWnd.Value, siblingHandle.Value, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            LinuxWindowPlacement.TrySetKeepBelow(this, true);
            LinuxWindowPlacement.TryStackBelow(this, siblingWindow);
            LinuxWindowPlacement.TryLower(this);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            LinuxWindowPlacement.TryConfigureAsNormalWindow(this);
            LinuxWindowPlacement.TrySetKeepBelow(this, true);
            LinuxWindowPlacement.TryLower(this);
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

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
