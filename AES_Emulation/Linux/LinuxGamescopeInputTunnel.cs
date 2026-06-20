using AES_Core.Logging;
using AES_Emulation.Linux.API;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using log4net;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace AES_Emulation.Linux;

/// <summary>
/// Forwards keyboard and mouse events from the Lacrima capture surface into gamescope's
/// isolated XWayland display. Headless gamescope does not receive host session input otherwise.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxGamescopeInputTunnel : IDisposable
{
    private const int DefaultViewportWidth = 1280;
    private const int DefaultViewportHeight = 720;

    private static readonly ILog Log = LogHelper.For(typeof(LinuxGamescopeInputTunnel));

    private static readonly Dictionary<Key, uint> KeySymByAvaloniaKey = BuildKeySymMap();

    private readonly InputElement _element;
    private readonly EventHandler<KeyEventArgs> _keyDownHandler;
    private readonly EventHandler<KeyEventArgs> _keyUpHandler;
    private readonly EventHandler<PointerEventArgs> _pointerMovedHandler;
    private readonly EventHandler<PointerPressedEventArgs> _pointerPressedHandler;
    private readonly EventHandler<PointerReleasedEventArgs> _pointerReleasedHandler;
    private readonly EventHandler<PointerWheelEventArgs> _pointerWheelHandler;

    private IntPtr _display;
    private IntPtr _targetWindow;
    private string? _displayName;
    private int _compositorPid;
    private int _viewportWidth = DefaultViewportWidth;
    private int _viewportHeight = DefaultViewportHeight;
    private bool _disposed;

    public LinuxGamescopeInputTunnel(InputElement element)
    {
        _element = element;
        _keyDownHandler = OnKeyDown;
        _keyUpHandler = OnKeyUp;
        _pointerMovedHandler = OnPointerMoved;
        _pointerPressedHandler = OnPointerPressed;
        _pointerReleasedHandler = OnPointerReleased;
        _pointerWheelHandler = OnPointerWheel;

        _element.AddHandler(InputElement.KeyDownEvent, _keyDownHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _element.AddHandler(InputElement.KeyUpEvent, _keyUpHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _element.AddHandler(InputElement.PointerMovedEvent, _pointerMovedHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _element.AddHandler(InputElement.PointerPressedEvent, _pointerPressedHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _element.AddHandler(InputElement.PointerReleasedEvent, _pointerReleasedHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        _element.AddHandler(InputElement.PointerWheelChangedEvent, _pointerWheelHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    public int CompositorProcessId
    {
        get => _compositorPid;
        set
        {
            if (_compositorPid == value)
                return;

            _compositorPid = value;
            ResetConnection();
        }
    }

    public int ViewportWidth
    {
        get => _viewportWidth;
        set => _viewportWidth = Math.Max(1, value);
    }

    public int ViewportHeight
    {
        get => _viewportHeight;
        set => _viewportHeight = Math.Max(1, value);
    }

    public Func<Point, (int X, int Y)?>? MapToTargetClient { get; set; }

    /// <summary>Moves X11 input focus to the gamescope game window.</summary>
    public void ForwardFocusToTarget()
    {
        if (!EnsureConnected(forceRetarget: true))
            return;

        FocusTargetWindow();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _element.RemoveHandler(InputElement.KeyDownEvent, _keyDownHandler);
        _element.RemoveHandler(InputElement.KeyUpEvent, _keyUpHandler);
        _element.RemoveHandler(InputElement.PointerMovedEvent, _pointerMovedHandler);
        _element.RemoveHandler(InputElement.PointerPressedEvent, _pointerPressedHandler);
        _element.RemoveHandler(InputElement.PointerReleasedEvent, _pointerReleasedHandler);
        _element.RemoveHandler(InputElement.PointerWheelChangedEvent, _pointerWheelHandler);
        CloseDisplay();
    }

    private void ResetConnection()
    {
        CloseDisplay();
        _targetWindow = IntPtr.Zero;
        _displayName = null;
    }

    private bool EnsureConnected(bool forceRetarget = false)
    {
        if (_compositorPid <= 0)
            return false;

        if (_display == IntPtr.Zero)
        {
            if (!LinuxGamescopeEnvironmentHelper.TryResolveGamescopeX11Display(_compositorPid, out var displayName) ||
                string.IsNullOrWhiteSpace(displayName))
            {
                return false;
            }

            var display = X11Interop.XOpenDisplay(displayName);
            if (display == IntPtr.Zero)
                return false;

            _display = display;
            _displayName = displayName;
            XTestInterop.XTestGrabControl(_display, impervious: true);
        }

        if (_targetWindow == IntPtr.Zero || forceRetarget)
        {
            var resolved = ResolveTargetWindow(_display);
            if (resolved == IntPtr.Zero)
            {
                if (forceRetarget)
                    Log.Debug($"LinuxGamescopeInputTunnel could not resolve a target window on DISPLAY={_displayName}.");
                return false;
            }

            if (_targetWindow != resolved)
            {
                _targetWindow = resolved;
                Log.Info(
                    $"LinuxGamescopeInputTunnel connected: DISPLAY={_displayName}, compositorPid={_compositorPid}, " +
                    $"targetWindow=0x{_targetWindow.ToInt64():X}.");
            }
        }

        FocusTargetWindow();
        return true;
    }

    private void FocusTargetWindow()
    {
        if (_display == IntPtr.Zero || _targetWindow == IntPtr.Zero)
            return;

        LinuxX11WindowHelper.TrySetInputFocus(_display, _targetWindow);
        X11Interop.XSync(_display, false);
    }

    private void CloseDisplay()
    {
        if (_display == IntPtr.Zero)
            return;

        try
        {
            X11Interop.XCloseDisplay(_display);
        }
        catch
        {
            // ignored
        }

        _display = IntPtr.Zero;
    }

    private static IntPtr ResolveTargetWindow(IntPtr display)
    {
        var root = LinuxX11WindowHelper.GetDefaultRootWindow(display);
        if (root == IntPtr.Zero)
            return IntPtr.Zero;

        var bestWindow = IntPtr.Zero;
        var bestArea = 0L;
        foreach (var window in LinuxX11WindowHelper.EnumerateMappedWindows(display, root))
        {
            if (!LinuxX11WindowHelper.TryGetWindowGeometry(display, window, out var width, out var height))
                continue;

            if (width < 64 || height < 64)
                continue;

            var area = (long)width * height;
            if (area <= bestArea)
                continue;

            bestArea = area;
            bestWindow = window;
        }

        return bestWindow != IntPtr.Zero ? bestWindow : root;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TrySendKeyEvent(e.Key, press: true))
            return;

        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TrySendKeyEvent(e.Key, press: false))
            return;

        e.Handled = true;
    }

    private bool TrySendKeyEvent(Key key, bool press)
    {
        if (_display == IntPtr.Zero || !KeySymByAvaloniaKey.TryGetValue(key, out var keysym))
            return false;

        var keycode = LinuxX11WindowHelper.KeysymToKeycode(_display, keysym);
        if (keycode == 0)
            return false;

        XTestInterop.XTestFakeKeyEvent(_display, keycode, press, XTestInterop.IsFake);
        X11Interop.XSync(_display, false);
        return true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TryMapPointer(e, out var x, out var y))
            return;

        XTestInterop.XTestFakeMotionEvent(_display, 0, x, y, XTestInterop.IsFake);
        X11Interop.XSync(_display, false);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TryMapPointer(e, out var x, out var y))
            return;

        var props = e.GetCurrentPoint(_element).Properties;
        XTestInterop.XTestFakeMotionEvent(_display, 0, x, y, XTestInterop.IsFake);
        if (props.IsLeftButtonPressed)
            XTestInterop.XTestFakeButtonEvent(_display, 1, true, XTestInterop.IsFake);
        if (props.IsMiddleButtonPressed)
            XTestInterop.XTestFakeButtonEvent(_display, 2, true, XTestInterop.IsFake);
        if (props.IsRightButtonPressed)
            XTestInterop.XTestFakeButtonEvent(_display, 3, true, XTestInterop.IsFake);
        X11Interop.XSync(_display, false);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TryMapPointer(e, out var x, out var y))
            return;

        var button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => 1u,
            MouseButton.Middle => 2u,
            MouseButton.Right => 3u,
            _ => 0u
        };

        if (button == 0)
            return;

        XTestInterop.XTestFakeMotionEvent(_display, 0, x, y, XTestInterop.IsFake);
        XTestInterop.XTestFakeButtonEvent(_display, button, false, XTestInterop.IsFake);
        X11Interop.XSync(_display, false);
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!EnsureConnected())
            return;

        if (!TryMapPointer(e, out var x, out var y))
            return;

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
            return;

        var button = delta > 0 ? 4u : 5u;
        var clicks = Math.Min(8, Math.Max(1, (int)Math.Round(Math.Abs(delta))));
        XTestInterop.XTestFakeMotionEvent(_display, 0, x, y, XTestInterop.IsFake);
        for (var i = 0; i < clicks; i++)
        {
            XTestInterop.XTestFakeButtonEvent(_display, button, true, XTestInterop.IsFake);
            XTestInterop.XTestFakeButtonEvent(_display, button, false, XTestInterop.IsFake);
        }

        X11Interop.XSync(_display, false);
        e.Handled = true;
    }

    private bool TryMapPointer(PointerEventArgs e, out int x, out int y)
    {
        x = 0;
        y = 0;

        var point = e.GetCurrentPoint(_element).Position;
        if (MapToTargetClient?.Invoke(point) is { } mapped)
        {
            x = mapped.X;
            y = mapped.Y;
            return true;
        }

        if (_element is not Visual visual)
            return false;

        var bounds = visual.Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return false;

        var normalizedX = Math.Clamp(point.X / bounds.Width, 0.0, 1.0);
        var normalizedY = Math.Clamp(point.Y / bounds.Height, 0.0, 1.0);
        x = (int)Math.Round(normalizedX * (_viewportWidth - 1));
        y = (int)Math.Round(normalizedY * (_viewportHeight - 1));
        return true;
    }

    private static Dictionary<Key, uint> BuildKeySymMap()
    {
        // X11 keysyms for common keyboard and game controls.
        return new Dictionary<Key, uint>
        {
            [Key.Escape] = 0xff1b,
            [Key.Tab] = 0xff09,
            [Key.Back] = 0xff08,
            [Key.Return] = 0xff0d,
            [Key.Space] = 0x0020,
            [Key.LeftShift] = 0xffe1,
            [Key.RightShift] = 0xffe2,
            [Key.LeftCtrl] = 0xffe3,
            [Key.RightCtrl] = 0xffe4,
            [Key.LeftAlt] = 0xffe9,
            [Key.RightAlt] = 0xffea,
            [Key.Up] = 0xff52,
            [Key.Down] = 0xff54,
            [Key.Left] = 0xff51,
            [Key.Right] = 0xff53,
            [Key.F1] = 0xffbe,
            [Key.F2] = 0xffbf,
            [Key.F3] = 0xffc0,
            [Key.F4] = 0xffc1,
            [Key.F5] = 0xffc2,
            [Key.F6] = 0xffc3,
            [Key.F7] = 0xffc4,
            [Key.F8] = 0xffc5,
            [Key.F9] = 0xffc6,
            [Key.F10] = 0xffc7,
            [Key.F11] = 0xffc8,
            [Key.F12] = 0xffc9,
            [Key.D0] = 0x0030,
            [Key.D1] = 0x0031,
            [Key.D2] = 0x0032,
            [Key.D3] = 0x0033,
            [Key.D4] = 0x0034,
            [Key.D5] = 0x0035,
            [Key.D6] = 0x0036,
            [Key.D7] = 0x0037,
            [Key.D8] = 0x0038,
            [Key.D9] = 0x0039,
            [Key.A] = 0x0061,
            [Key.B] = 0x0062,
            [Key.C] = 0x0063,
            [Key.D] = 0x0064,
            [Key.E] = 0x0065,
            [Key.F] = 0x0066,
            [Key.G] = 0x0067,
            [Key.H] = 0x0068,
            [Key.I] = 0x0069,
            [Key.J] = 0x006a,
            [Key.K] = 0x006b,
            [Key.L] = 0x006c,
            [Key.M] = 0x006d,
            [Key.N] = 0x006e,
            [Key.O] = 0x006f,
            [Key.P] = 0x0070,
            [Key.Q] = 0x0071,
            [Key.R] = 0x0072,
            [Key.S] = 0x0073,
            [Key.T] = 0x0074,
            [Key.U] = 0x0075,
            [Key.V] = 0x0076,
            [Key.W] = 0x0077,
            [Key.X] = 0x0078,
            [Key.Y] = 0x0079,
            [Key.Z] = 0x007a,
        };
    }
}
