using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AES_Lacrima.Views;

public partial class PortalFullscreenOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public event EventHandler? DoubleClicked;

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public PortalFullscreenOverlayWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
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
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
