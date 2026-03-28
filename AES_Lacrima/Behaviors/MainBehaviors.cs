using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using AES_Lacrima.Mini.ViewModels;

namespace AES_Lacrima.Behaviors
{
    /// <summary>
    /// Enables window dragging behavior for a visual element by allowing the user to click and drag the associated
    /// control to move the window.
    /// </summary>
    internal class WindowDragBehavior : Behavior<Control>
    {
        private Control? _attachedControl;

        protected override void OnAttached()
        {
            if (AssociatedObject is not { } control)
                return;

            _attachedControl = control;
            _attachedControl.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        }

        protected override void OnDetaching()
        {
            if (_attachedControl is not null)
            {
                _attachedControl.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                _attachedControl = null;
            }

            base.OnDetaching();
        }

        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control dragArea)
                return;

            if (!e.GetCurrentPoint(dragArea).Properties.IsLeftButtonPressed)
                return;

            if (e.Source is Control sourceControl && sourceControl.GetSelfAndVisualAncestors().OfType<Button>().Any())
                return;

            if (TopLevel.GetTopLevel(dragArea) is Window window && window.WindowState != WindowState.FullScreen)
                window.BeginMoveDrag(e);
        }
    }

    /// <summary>
    /// Restores edge/corner resize support for borderless windows.
    /// </summary>
    internal class WindowResizeBehavior : Behavior<Window>
    {
        private WindowEdge? _currentEdge;

        public double ResizeBorderThickness { get; set; } = 8d;

        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject is not { } window)
                return;

            if (OperatingSystem.IsMacOS())
            {
                // On macOS, SystemDecorations.None disables native resizing, and manual BeginResizeDrag
                // is not supported for borderless windows. Using SystemDecorations.Full with ExtendedClientArea
                // provides natively resizable borderless windows.
                window.SystemDecorations = SystemDecorations.Full;
                return;
            }

            window.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        protected override void OnDetaching()
        {
            if (AssociatedObject is { } window && !OperatingSystem.IsMacOS())
            {
                window.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
                window.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                window.Cursor = null;
            }

            base.OnDetaching();
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not Window window)
                return;

            if (!CanResize(window))
            {
                _currentEdge = null;
                window.Cursor = null;
                return;
            }

            var point = e.GetPosition(window);
            _currentEdge = GetResizeEdge(window, point);
            window.Cursor = GetCursor(_currentEdge);
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Window window)
                return;

            if (!CanResize(window))
                return;

            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                return;

            _currentEdge = GetResizeEdge(window, e.GetPosition(window));
            if (_currentEdge is null)
                return;

            window.BeginResizeDrag(_currentEdge.Value, e);
            e.Handled = true;
        }

        private bool CanResize(Window window)
        {
            return window.CanResize && window.WindowState == WindowState.Normal;
        }

        private WindowEdge? GetResizeEdge(Window window, Point point)
        {
            var width = window.Bounds.Width;
            var height = window.Bounds.Height;

            if (width <= 0 || height <= 0)
                return null;

            var threshold = Math.Max(1d, ResizeBorderThickness);
            var cornerThreshold = Math.Max(threshold, 14d);
            var left = point.X <= threshold;
            var right = point.X >= width - threshold;
            var top = point.Y <= threshold;
            var bottom = point.Y >= height - threshold;
            var leftCorner = point.X <= cornerThreshold;
            var rightCorner = point.X >= width - cornerThreshold;
            var topCorner = point.Y <= cornerThreshold;
            var bottomCorner = point.Y >= height - cornerThreshold;

            if (leftCorner && topCorner)
                return WindowEdge.NorthWest;
            if (rightCorner && topCorner)
                return WindowEdge.NorthEast;
            if (leftCorner && bottomCorner)
                return WindowEdge.SouthWest;
            if (rightCorner && bottomCorner)
                return WindowEdge.SouthEast;
            if (left)
                return WindowEdge.West;
            if (right)
                return WindowEdge.East;
            if (top)
                return WindowEdge.North;
            if (bottom)
                return WindowEdge.South;

            return null;
        }

        private static Cursor? GetCursor(WindowEdge? edge)
        {
            return edge switch
            {
                WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
                WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
                WindowEdge.NorthWest or WindowEdge.SouthEast => new Cursor(StandardCursorType.TopLeftCorner),
                WindowEdge.NorthEast or WindowEdge.SouthWest => new Cursor(StandardCursorType.TopRightCorner),
                _ => null
            };
        }
    }

    /// <summary>
    /// Initializes the mini window's dedicated scale setting from the current render scaling
    /// the first time the window is opened, without keeping that logic in code-behind.
    /// </summary>
    internal class MiniWindowScaleBehavior : Behavior<Window>
    {
        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject is { } window)
            {
                window.Opened += OnOpened;
            }
        }

        protected override void OnDetaching()
        {
            if (AssociatedObject is { } window)
            {
                window.Opened -= OnOpened;
            }

            base.OnDetaching();
        }

        private static void OnOpened(object? sender, EventArgs e)
        {
            if (sender is not Window { DataContext: MinViewModel { SettingsViewModel: { } settings } } window)
                return;

            if (!settings.IsPrepared)
            {
                settings.Prepare();
                settings.IsPrepared = true;
            }

            if (!settings.HasPersistedMiniScaleFactor)
            {
                settings.MiniScaleFactor = Math.Clamp(TopLevel.GetTopLevel(window)?.RenderScaling ?? 1.0, 1.0, 2.0);
            }
        }
    }
}
