using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
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

    public static void ResetApplicationShuttingDown()
    {
        _isApplicationShuttingDown = false;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public PortalWindow()
    {
        InitializeComponent();
        CaptureHostControl?.AddHandler(InputElement.PointerPressedEvent, OnCaptureHostPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        var captureContextMenuLayer = this.FindControl<Border>("CaptureContextMenuLayer");
        captureContextMenuLayer?.AddHandler(InputElement.PointerPressedEvent, OnCaptureContextMenuLayerPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnCaptureHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsCaptureContextMenuPointer(e))
            return;

        TryOpenCaptureContextMenu();
        e.Handled = true;
    }

    private void OnCaptureContextMenuLayerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsCaptureContextMenuPointer(e))
            return;

        if (sender is Border layer)
            OpenCaptureContextMenu(layer);
        else
            TryOpenCaptureContextMenu();

        e.Handled = true;
    }

    private void TryOpenCaptureContextMenu()
    {
        var layer = this.FindControl<Border>("CaptureContextMenuLayer");
        if (layer != null)
            OpenCaptureContextMenu(layer);
    }

    private static void OpenCaptureContextMenu(Border layer)
    {
        if (layer.ContextMenu is not ContextMenu menu)
            return;

        menu.PlacementTarget = layer;
        menu.Open(layer);
    }

    private static bool IsCaptureContextMenuPointer(PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual)
            return false;

        return e.GetCurrentPoint(visual).Properties.IsRightButtonPressed;
    }

    public EmulatorCaptureHost? CaptureHostControl => this.FindControl<EmulatorCaptureHost>("CaptureControl");

    public void MoveResizeUnconstrained(PixelPoint position, int widthPixels, int heightPixels)
    {
        this.Position = position;
        var scaling = this.RenderScaling > 0 ? this.RenderScaling : 1;
        this.Width = Math.Ceiling(widthPixels / scaling);
        this.Height = Math.Ceiling(heightPixels / scaling);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Prevent closing unless the application is shutting down.
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
    }
}
