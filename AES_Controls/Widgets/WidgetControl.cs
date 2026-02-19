using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Windows.Input;

namespace AES_Controls.Widgets;

public class MoveResizeResult(double left, double top, double width, double height)
{
    public double Left { get; init; } = left;
    public double Top { get; init; } = top;
    public double Width { get; init; } = width;
    public double Height { get; init; } = height;
}

// Draggable and resizable content control with focus visuals.
public class WidgetControl : ContentControl
{
    public static readonly StyledProperty<ICommand?> MoveResizeEndedCommandProperty =
        AvaloniaProperty.Register<WidgetControl, ICommand?>(nameof(MoveResizeEndedCommand));
    public ICommand? MoveResizeEndedCommand
    {
        get => GetValue(MoveResizeEndedCommandProperty);
        set => SetValue(MoveResizeEndedCommandProperty, value);
    }

    public static readonly StyledProperty<double> LeftProperty =
        AvaloniaProperty.Register<WidgetControl, double>(nameof(Left), double.NaN);
    public double Left
    {
        get => GetValue(LeftProperty);
        set => SetValue(LeftProperty, value);
    }

    public static readonly StyledProperty<double> TopProperty =
        AvaloniaProperty.Register<WidgetControl, double>(nameof(Top), double.NaN);
    public double Top
    {
        get => GetValue(TopProperty);
        set => SetValue(TopProperty, value);
    }

    // Bindable property to enable/disable the dash animation (default: true).
    public static readonly StyledProperty<bool> IsDashAnimatedProperty =
        AvaloniaProperty.Register<WidgetControl, bool>(nameof(IsDashAnimated), true);

    public bool IsDashAnimated
    {
        get => GetValue(IsDashAnimatedProperty);
        set => SetValue(IsDashAnimatedProperty, value);
    }

    public static readonly StyledProperty<bool> FollowWindowSizeProperty =
        AvaloniaProperty.Register<WidgetControl, bool>(nameof(FollowWindowSize));

    public bool FollowWindowSize
    {
        get => GetValue(FollowWindowSizeProperty);
        set => SetValue(FollowWindowSizeProperty, value);
    }

    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<WidgetControl, bool>(nameof(IsPinned), true);

    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    private enum DragMode { None, Move, ResizeLeft, ResizeRight, ResizeTop, ResizeBottom, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }

    private const double GripSize = 16.0;
    private const double MinW = 20.0;
    private const double MinH = 20.0;
    private const double SnapDistance = 15.0;
    private const double PinSize = 24.0;

    private DragMode _mode = DragMode.None;
    private Point _startPointer;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;
    private bool _isCaptured;
    private IPointer? _capturedPointer;
    private readonly TranslateTransform _moveTransform = new TranslateTransform();

    private Visual? _container;
    private IDisposable? _containerBoundsSubscription;
    private Size _lastContainerSize;

    // Relative metrics captured for responsive scaling when pinned
    private double _relLeft;
    private double _relTop;
    private double _relWidth;
    private double _relHeight;

    private TopLevel? _rootTopLevel;
    private StandardCursorType _currentCursorType = StandardCursorType.Arrow;

    // When true, use explicit Left/Top positioning instead of respecting alignment
    private bool _useExplicitPosition;

    // Retry counter for waiting until the control has been measured/arranged
    private int _initialApplyRetries;

    // Ensure initial loaded values are applied only once
    private bool _initialPositionApplied;

    // Dash animation state (for moving line along dashed focus rectangle)
    private double _dashOffset;
    private DispatcherTimer? _dashTimer;
    private const double DashLength = 8.0; // sum of dash + gap (4 + 4)

    // Track ancestors whose ClipToBounds was cleared so we can restore on detach
    private readonly List<Control> _unclippedAncestors = new List<Control>();

    static WidgetControl()
    {
        FocusableProperty.OverrideDefaultValue<WidgetControl>(true);

        LeftProperty.Changed.AddClassHandler<WidgetControl>((s, e) =>
        {
            var newVal = e.NewValue is double d ? d : double.NaN;
            // Accept any concrete numeric value (including 0) as explicit intent
            if (!double.IsNaN(newVal))
            {
                s._useExplicitPosition = true;
                if (s.Parent != null && !s._initialPositionApplied)
                {
                    Dispatcher.UIThread.Post(() => s.TryApplyInitialPosition(), DispatcherPriority.Background);
                }
            }
        });

        TopProperty.Changed.AddClassHandler<WidgetControl>((s, e) =>
        {
            var newVal = e.NewValue is double d ? d : double.NaN;
            // Accept any concrete numeric value (including 0) as explicit intent
            if (!double.IsNaN(newVal))
            {
                s._useExplicitPosition = true;
                if (s.Parent != null && !s._initialPositionApplied)
                {
                    Dispatcher.UIThread.Post(() => s.TryApplyInitialPosition(), DispatcherPriority.Background);
                }
            }
        });

        // React to IsDashAnimated changes to start/stop the timer when bound value changes.
        IsDashAnimatedProperty.Changed.AddClassHandler<WidgetControl>((s, e) =>
        {
            var newVal = e.NewValue is bool b ? b : true;
            s.OnIsDashAnimatedChanged(newVal);
        });

        IsPinnedProperty.Changed.AddClassHandler<WidgetControl>((s, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => s.UpdateResponsiveSnapshot(), DispatcherPriority.Render);
            }
        });
    }

    public WidgetControl()
    {
        _moveTransform.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new ExponentialEaseOut() },
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new ExponentialEaseOut() }
            };
        RenderTransform = _moveTransform;

        ClipToBounds = false;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;

        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) =>
        {
            InvalidateVisual();
            if (_isCaptured) ReleasePointerCaptureAndReset();
        };
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // disable clipping on ancestor Controls so content can render outside bounds
        DisableAncestorClipping();

        _rootTopLevel = this.GetVisualRoot() as TopLevel;

        // Find container (Canvas or Panel)
        _container = FindPositioningContainer();
        if (_container != null)
        {
            _lastContainerSize = _container.Bounds.Size;
            _containerBoundsSubscription = _container.GetObservable(Visual.BoundsProperty).Subscribe(new Helpers.SimpleObserver<Rect>(OnContainerBoundsChanged));
        }

        if (_rootTopLevel != null)
        {
            _rootTopLevel.AddHandler(PointerPressedEvent, RootVisual_PointerPressed, RoutingStrategies.Tunnel, true);
        }

        LayoutUpdated += OnLayoutUpdated;
        // schedule initial application after render pass to wait for final container layout
        Dispatcher.UIThread.Post(() => {
            TryApplyInitialPosition();
            UpdateResponsiveSnapshot();
        }, DispatcherPriority.Render);

        // start dash animation timer only if animation is enabled
        if (IsDashAnimated && _dashTimer == null)
        {
            _dashTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, OnDashTick);
            _dashTimer.Start();
        }
    }

    private Visual? FindPositioningContainer()
    {
        Visual? ancestor = this.GetVisualParent();
        while (ancestor != null && !(ancestor is Canvas) && !(ancestor is Panel))
        {
            ancestor = ancestor.GetVisualParent();
        }
        return ancestor;
    }

    private void OnContainerBoundsChanged(Rect bounds)
    {
        if (!IsPinned || _isCaptured || bounds.Width <= 0 || bounds.Height <= 0)
        {
            _lastContainerSize = bounds.Size;
            return;
        }

        if (_lastContainerSize.Width <= 0 || _lastContainerSize.Height <= 0)
        {
            _lastContainerSize = bounds.Size;
            UpdateResponsiveSnapshot();
            return;
        }

        double newW = bounds.Width;
        double newH = bounds.Height;

        // Proportional scaling for pinned widgets
        double w = double.IsNaN(Width) ? Bounds.Width : Width;
        double h = double.IsNaN(Height) ? Bounds.Height : Height;

        // Maintain relative size/scale visually
        Width = Math.Max(MinW, _relWidth * newW);
        Height = Math.Max(MinH, _relHeight * newH);
        Left = Math.Clamp(_relLeft * newW, 0, Math.Max(0, newW - Width));
        Top = Math.Clamp(_relTop * newH, 0, Math.Max(0, newH - Height));

        ApplyExplicitPosition();
        _lastContainerSize = bounds.Size;
    }

    private void UpdateResponsiveSnapshot()
    {
        if (_container == null || _container.Bounds.Width <= 0 || _container.Bounds.Height <= 0) return;

        var currentPos = GetReportedPosition();
        double w = double.IsNaN(Width) ? Bounds.Width : Width;
        double h = double.IsNaN(Height) ? Bounds.Height : Height;
        double pw = _container.Bounds.Width;
        double ph = _container.Bounds.Height;

        _relLeft = currentPos.left / pw;
        _relTop = currentPos.top / ph;
        _relWidth = w / pw;
        _relHeight = h / ph;

        _lastContainerSize = _container.Bounds.Size;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // restore ancestor clipping first
        RestoreAncestorClipping();

        if (_rootTopLevel != null)
        {
            _rootTopLevel.RemoveHandler(PointerPressedEvent, RootVisual_PointerPressed);
            _rootTopLevel = null;
        }

        LayoutUpdated -= OnLayoutUpdated;
        _containerBoundsSubscription?.Dispose();
        _containerBoundsSubscription = null;
        _container = null;

        _useExplicitPosition = false;
        _initialPositionApplied = false;
        _initialApplyRetries = 0;

        // stop and dispose timer
        try
        {
            if (_dashTimer != null)
            {
                _dashTimer.Stop();
                _dashTimer = null;
            }
        }
        catch { /* ignore */ }
    }

    // Called when the bindable IsDashAnimated property changes.
    private void OnIsDashAnimatedChanged(bool enabled)
    {
        // Ensure this runs on UI thread
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (enabled)
                {
                    if (_dashTimer == null)
                    {
                        _dashTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, OnDashTick);
                    }
                    if (!_dashTimer.IsEnabled)
                        _dashTimer.Start();
                }
                else
                {
                    if (_dashTimer != null && _dashTimer.IsEnabled)
                        _dashTimer.Stop();
                    _dashOffset = 0.0;
                    InvalidateVisual();
                }
            }
            catch { /* ignore */ }
        }, DispatcherPriority.Normal);
    }

    // Timer tick: advance dash offset only when focused (dash visible) and when enabled
    private void OnDashTick(object? sender, EventArgs e)
    {
        if (!IsFocused) return; // only animate when the focus rectangle is shown
        if (!IsDashAnimated) return; // respect bindable setting

        // Decrement offset to make the dashed line travel clockwise around the rectangle.
        _dashOffset -= 1.0; // adjust speed as needed
        if (_dashOffset <= -DashLength) _dashOffset += DashLength;
        InvalidateVisual();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => TryApplyInitialPosition();

    // Helper: treat exact 0 as "unset" only for size (Width/Height)
    private static bool IsSizeUnset(double v) => v == 0.0;

    // Apply initial values only once when control is presented/loaded.
    private void TryApplyInitialPosition()
    {
        if (Parent == null) return;
        if (_initialPositionApplied) return;

        // If not yet measured/arranged, retry after render pass (limited attempts).
        if ((Bounds.Width == 0 || Bounds.Height == 0) && _initialApplyRetries < 10)
        {
            _initialApplyRetries++;
            Dispatcher.UIThread.Post(() => TryApplyInitialPosition(), DispatcherPriority.Render);
            return;
        }

        // Determine if there are any meaningful values to apply (Left/Top any numeric counts)
        var hasInitialValues =
            (!double.IsNaN(Left)) ||
            (!double.IsNaN(Top)) ||
            (!double.IsNaN(Width) && Width > 0.0) ||
            (!double.IsNaN(Height) && Height > 0.0) ||
            _useExplicitPosition;

        if (!hasInitialValues)
        {
            // No explicit position/size -> keep default sizing behaviour
            if (IsSizeUnset(Width))
                Width = double.NaN;
            if (IsSizeUnset(Height))
                Height = double.NaN;
            _initialPositionApplied = true;
            return;
        }

        // Apply width/height if > 0 (otherwise let layout decide)
        if (!double.IsNaN(Width) && Width > 0.0)
            Width = Width;
        else
            Width = double.NaN;

        if (!double.IsNaN(Height) && Height > 0.0)
            Height = Height;
        else
            Height = double.NaN;

        // Apply position according to nearest ancestor Canvas/Panel (handles ContentPresenter)
        ApplyExplicitPosition();

        _initialPositionApplied = true;
    }

    // Find nearest Canvas/Panel ancestor and apply explicit positioning/margins.
    private void ApplyExplicitPosition()
    {
        Visual? ancestor = _container ?? FindPositioningContainer();
        if (ancestor == null) return;

        Visual? FindDirectChildUnder(Visual root)
        {
            Visual? child = this;
            while (child != null)
            {
                var parent = child.GetVisualParent();
                if (parent == root)
                    return child;
                child = parent;
            }
            return null;
        }

        double? leftValue = (!double.IsNaN(Left)) ? Left : null;
        double? topValue = (!double.IsNaN(Top)) ? Top : null;

        if (ancestor is Canvas canvas)
        {
            var directChild = FindDirectChildUnder(canvas);
            if (directChild is Control c)
            {
                if (leftValue.HasValue) Canvas.SetLeft(c, leftValue.Value);
                if (topValue.HasValue) Canvas.SetTop(c, topValue.Value);
            }
        }
        else if (ancestor is Panel panel)
        {
            var directChild = FindDirectChildUnder(panel);
            if (directChild is Control c)
            {
                if (leftValue.HasValue || topValue.HasValue)
                {
                    c.HorizontalAlignment = HorizontalAlignment.Left;
                    c.VerticalAlignment = VerticalAlignment.Top;
                    c.Margin = new Thickness(leftValue ?? 0, topValue ?? 0, 0, 0);
                }
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!IsFocused || !IsContentInteractive()) return;

        // Draw focus rectangle with animated dash offset
        var pen = new Pen(Brushes.LightGray)
        {
            DashStyle = new DashStyle(new[] { 4.0, 4.0 }, _dashOffset),
            Thickness = 2.0
        };
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(null, pen, rect, 8.0, 8.0);

        // Draw Pinned icon toggle area (moved to right side)
        var pinBrush = IsPinned ? Brushes.SkyBlue : Brushes.DimGray;
        var pinRect = new Rect(Bounds.Width - 20 - PinSize, 20, PinSize - 8, PinSize - 8);
        context.DrawRectangle(pinBrush, null, pinRect, 10, 10);
        if (IsPinned)
            context.DrawRectangle(Brushes.White, null, new Rect(Bounds.Width - 20 - PinSize + 3, 23, PinSize - 14, PinSize - 14), 3, 3);
    }

    private StandardCursorType DetermineCursorTypeForLocal(Point local)
    {
        if (!IsFocused || !IsContentInteractive()) return StandardCursorType.Arrow;
        // Pin area check matches relocated position (right side)
        if (local.X >= Bounds.Width - 20 - PinSize && local.X <= Bounds.Width - 20 && local.Y >= 20 && local.Y <= 20 + PinSize) return StandardCursorType.Arrow;

        var cornerSize = GripSize * 1.5;
        bool left = local.X <= GripSize;
        bool right = local.X >= Bounds.Width - GripSize;
        bool top = local.Y <= GripSize;
        bool bottom = local.Y >= Bounds.Height - GripSize;

        // corner heuristics: return appropriate resize cursors
        if ((local.X <= cornerSize && local.Y <= cornerSize) || (local.X >= Bounds.Width - cornerSize && local.Y >= Bounds.Height - cornerSize))
            return StandardCursorType.TopLeftCorner;
        if ((local.X >= Bounds.Width - cornerSize && local.Y <= cornerSize) || (local.X <= cornerSize && local.Y >= Bounds.Height - cornerSize))
            return StandardCursorType.TopRightCorner;

        if (left || right) return StandardCursorType.SizeWestEast;
        if (top || bottom) return StandardCursorType.SizeNorthSouth;
        return StandardCursorType.Arrow;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsFocused) Focus();

        var reference = _container ?? Parent as Visual;
        if (reference == null) return;

        var local = e.GetPosition(this);
        // Check if clicked pin toggle area (right side)
        if (local.X >= Bounds.Width - 20 - PinSize && local.X <= Bounds.Width - 20 && local.Y >= 20 && local.Y <= 20 + PinSize)
        {
            IsPinned = !IsPinned;
            if (IsPinned) UpdateResponsiveSnapshot();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        _startPointer = e.GetPosition(reference);
        var currentPos = GetReportedPosition();
        _startLeft = currentPos.left;
        _startTop = currentPos.top;
        _startWidth = double.IsNaN(Width) ? Bounds.Width : Width;
        _startHeight = double.IsNaN(Height) ? Bounds.Height : Height;

        bool left = local.X <= GripSize;
        bool right = local.X >= Bounds.Width - GripSize;
        bool top = local.Y <= GripSize;
        bool bottom = local.Y >= Bounds.Height - GripSize;

        if (left && top) _mode = DragMode.ResizeTopLeft;
        else if (right && top) _mode = DragMode.ResizeTopRight;
        else if (left && bottom) _mode = DragMode.ResizeBottomLeft;
        else if (right && bottom) _mode = DragMode.ResizeBottomRight;
        else if (left) _mode = DragMode.ResizeLeft;
        else if (right) _mode = DragMode.ResizeRight;
        else if (top) _mode = DragMode.ResizeTop;
        else if (bottom) _mode = DragMode.ResizeBottom;
        else _mode = DragMode.Move;

        SetCursorIfDifferent(DetermineCursorTypeForLocal(local));
        _isCaptured = true;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        _useExplicitPosition = true;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!IsContentInteractive()) return;
        base.OnPointerMoved(e);

        if (!_isCaptured)
        {
            SetCursorIfDifferent(DetermineCursorTypeForLocal(e.GetPosition(this)));
            return;
        }

        var reference = _container ?? Parent as Visual;
        if (reference == null) return;

        var pos = e.GetPosition(reference);
        var dx = pos.X - _startPointer.X;
        var dy = pos.Y - _startPointer.Y;

        double newLeft = _startLeft + dx;
        double newTop = _startTop + dy;
        double newW = _startWidth;
        double newH = _startHeight;

        // Determine delta based on drag mode
        if (_mode != DragMode.Move)
        {
            newLeft = _startLeft;
            newTop = _startTop;
            switch (_mode)
            {
                case DragMode.ResizeLeft:
                    newW = Math.Max(MinW, _startWidth - dx);
                    newLeft = _startLeft + (_startWidth - newW);
                    break;
                case DragMode.ResizeRight:
                    newW = Math.Max(MinW, _startWidth + dx);
                    break;
                case DragMode.ResizeTop:
                    newH = Math.Max(MinH, _startHeight - dy);
                    newTop = _startTop + (_startHeight - newH);
                    break;
                case DragMode.ResizeBottom:
                    newH = Math.Max(MinH, _startHeight + dy);
                    break;
                case DragMode.ResizeTopLeft:
                    newW = Math.Max(MinW, _startWidth - dx);
                    newH = Math.Max(MinH, _startHeight - dy);
                    newLeft = _startLeft + (_startWidth - newW);
                    newTop = _startTop + (_startHeight - newH);
                    break;
                case DragMode.ResizeTopRight:
                    newW = Math.Max(MinW, _startWidth + dx);
                    newH = Math.Max(MinH, _startHeight - dy);
                    newTop = _startTop + (_startHeight - newH);
                    break;
                case DragMode.ResizeBottomLeft:
                    newW = Math.Max(MinW, _startWidth - dx);
                    newH = Math.Max(MinH, _startHeight + dy);
                    newLeft = _startLeft + (_startWidth - newW);
                    break;
                case DragMode.ResizeBottomRight:
                    newW = Math.Max(MinW, _startWidth + dx);
                    newH = Math.Max(MinH, _startHeight + dy);
                    break;
            }
        }

        double rawLeft = newLeft;
        double rawTop = newTop;

        // Snapping to parent edges (falling back to responsive container/window sizes)
        var boundingControl = _container as Control ?? reference as Control;
        double containerWidth = double.NaN;
        double containerHeight = double.NaN;
        if (boundingControl != null)
        {
            containerWidth = boundingControl.Bounds.Width;
            containerHeight = boundingControl.Bounds.Height;
        }
        if ((containerWidth <= 0 || containerHeight <= 0) && _lastContainerSize.Width > 0 && _lastContainerSize.Height > 0)
        {
            containerWidth = _lastContainerSize.Width;
            containerHeight = _lastContainerSize.Height;
        }
        if ((containerWidth <= 0 || containerHeight <= 0) && _rootTopLevel != null)
        {
            containerWidth = Math.Max(containerWidth, _rootTopLevel.Bounds.Width);
            containerHeight = Math.Max(containerHeight, _rootTopLevel.Bounds.Height);
        }
        if (containerWidth > 0 && containerHeight > 0)
        {
            if (Math.Abs(newLeft) < SnapDistance) newLeft = 0;
            else if (Math.Abs(newLeft + newW - containerWidth) < SnapDistance) newLeft = containerWidth - newW;
            if (Math.Abs(newTop) < SnapDistance) newTop = 0;
            else if (Math.Abs(newTop + newH - containerHeight) < SnapDistance) newTop = containerHeight - newH;

            newW = Math.Clamp(newW, MinW, containerWidth);
            newH = Math.Clamp(newH, MinH, containerHeight);
            newLeft = Math.Clamp(newLeft, 0, Math.Max(0, containerWidth - newW));
            newTop = Math.Clamp(newTop, 0, Math.Max(0, containerHeight - newH));
        }

        Left = newLeft;
        Top = newTop;
        Width = newW;
        Height = newH;

        // Smooth snap animation: Use the transform to visualy offset the snap jump, 
        // then the transition will animate it back to 0.
        bool horizontalClamped = rawLeft != newLeft;
        bool verticalClamped = rawTop != newTop;
        _moveTransform.X = horizontalClamped ? 0 : rawLeft - newLeft;
        _moveTransform.Y = verticalClamped ? 0 : rawTop - newTop;

        ApplyExplicitPosition();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isCaptured) return;

        ReleasePointerCaptureAndReset();
        UpdateResponsiveSnapshot();

        if (DataContext is WidgetItem widgetItem)
        {
            var pos = GetReportedPosition();
            widgetItem.Left = pos.left;
            widgetItem.Top = pos.top;
            widgetItem.Width = double.IsNaN(Width) ? Bounds.Width : Width;
            widgetItem.Height = double.IsNaN(Height) ? Bounds.Height : Height;
        }

        var finalPos = GetReportedPosition();
        var finalResult = new MoveResizeResult(finalPos.left, finalPos.top, Width, Height);
        if (MoveResizeEndedCommand?.CanExecute(finalResult) ?? false)
            MoveResizeEndedCommand.Execute(finalResult);

        e.Handled = true;
    }

    private (double left, double top) GetReportedPosition()
    {
        // If we have explicit valid numbers in properties, trust them over TranslatePoint
        // as TranslatePoint might be stale before the first layout completion after reload.
        if (!double.IsNaN(Left) && !double.IsNaN(Top) && (Left != 0 || Top != 0))
            return (Left, Top);

        var reference = _container ?? Parent as Visual;
        if (reference == null) return (double.IsNaN(Left) ? 0 : Left, double.IsNaN(Top) ? 0 : Top);
        var p = this.TranslatePoint(new Point(0, 0), reference);
        return p.HasValue ? (p.Value.X, p.Value.Y) : (double.IsNaN(Left) ? 0 : Left, double.IsNaN(Top) ? 0 : Top);
    }

    private void ReleasePointerCaptureAndReset()
    {
        if (_isCaptured)
        {
            try { _capturedPointer?.Capture(null); } catch { }
            _capturedPointer = null;
        }
        _isCaptured = false;
        _mode = DragMode.None;
        SetCursorIfDifferent(StandardCursorType.Arrow);
    }

    // Disable clipping on ancestor Controls so this widget can draw outside parents' bounds.
    private void DisableAncestorClipping()
    {
        try
        {
            Visual? parent = this.GetVisualParent();
            while (parent != null)
            {
                if (parent is Control c)
                {
                    // Only change if it currently clips so we can restore later.
                    if (c.ClipToBounds)
                    {
                        try { c.ClipToBounds = false; } catch { /* ignore read-only/platform issues */ }
                        _unclippedAncestors.Add(c);
                    }
                }
                parent = parent.GetVisualParent();
            }
        }
        catch { /* ignore traversal errors */ }
    }

    // Restore ancestor clipping state that we previously disabled.
    private void RestoreAncestorClipping()
    {
        try
        {
            foreach (var c in _unclippedAncestors)
            {
                try { c.ClipToBounds = true; } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        _unclippedAncestors.Clear();
    }

    private void RootVisual_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isCaptured || !IsFocused) return;

        var pos = e.GetPosition(this);
        if (pos.X < 0 || pos.Y < 0 || pos.X > Bounds.Width || pos.Y > Bounds.Height)
        {
            try { _rootTopLevel?.FocusManager?.ClearFocus(); } catch { /* ignore */ }
            SetCursorIfDifferent(StandardCursorType.Arrow);
            InvalidateVisual();
        }
    }

    private bool IsContentInteractive()
    {
        return Content is not Visual { DataContext: WidgetItem widgetItem } || widgetItem.IsVisible;
    }

    private void SetCursorIfDifferent(StandardCursorType desired)
    {
        if (_currentCursorType == desired) return;
        _currentCursorType = desired;
        Cursor = new Cursor(desired);
    }
}