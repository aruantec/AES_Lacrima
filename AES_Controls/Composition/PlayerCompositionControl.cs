using System;
using System.Numerics;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Input;
using SkiaSharp;

namespace AES_Controls.Composition;

public class PlayerCompositionControl : UserControl
{
    private CompositionCustomVisual? _visual;
    private bool _isPressed;
    private long _lastSeekTime;

    public static readonly StyledProperty<ICommand?> SetPositionCommandProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, ICommand?>(nameof(SetPositionCommand));

    public ICommand? SetPositionCommand
    {
        get => GetValue(SetPositionCommandProperty);
        set => SetValue(SetPositionCommandProperty, value);
    }

    public static readonly StyledProperty<IBrush?> CircleBackgroundColorProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, IBrush?>(nameof(CircleBackgroundColor));

    public IBrush? CircleBackgroundColor
    {
        get => GetValue(CircleBackgroundColorProperty);
        set => SetValue(CircleBackgroundColorProperty, value);
    }

    public static readonly StyledProperty<Bitmap?> CircleBackgroundBitmapProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, Bitmap?>(nameof(CircleBackgroundBitmap));

    public Bitmap? CircleBackgroundBitmap
    {
        get => GetValue(CircleBackgroundBitmapProperty);
        set => SetValue(CircleBackgroundBitmapProperty, value);
    }

    public static readonly StyledProperty<double> CircleBackgroundOpacityProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, double>(nameof(CircleBackgroundOpacity), 1.0);

    public double CircleBackgroundOpacity
    {
        get => GetValue(CircleBackgroundOpacityProperty);
        set => SetValue(CircleBackgroundOpacityProperty, value);
    }

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, double>(nameof(Position), 0);

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, double>(nameof(Duration), 0);

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly StyledProperty<bool> ShowTimeProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, bool>(nameof(ShowTime), true);

    public bool ShowTime
    {
        get => GetValue(ShowTimeProperty);
        set => SetValue(ShowTimeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowPlayPauseProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, bool>(nameof(ShowPlayPause), false);

    public bool ShowPlayPause
    {
        get => GetValue(ShowPlayPauseProperty);
        set => SetValue(ShowPlayPauseProperty, value);
    }

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, bool>(nameof(IsPlaying), false);

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly StyledProperty<double> PlayPauseOpacityProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, double>(nameof(PlayPauseOpacity), 1.0);

    public double PlayPauseOpacity
    {
        get => GetValue(PlayPauseOpacityProperty);
        set => SetValue(PlayPauseOpacityProperty, value);
    }

    public static readonly StyledProperty<ICommand?> TogglePlayCommandProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, ICommand?>(nameof(TogglePlayCommand));

    public ICommand? TogglePlayCommand
    {
        get => GetValue(TogglePlayCommandProperty);
        set => SetValue(TogglePlayCommandProperty, value);
    }

    public static readonly StyledProperty<bool> RotateProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, bool>(nameof(Rotate), false);

    public bool Rotate
    {
        get => GetValue(RotateProperty);
        set => SetValue(RotateProperty, value);
    }

    public static readonly StyledProperty<bool> ShowDiscCenterProperty =
        AvaloniaProperty.Register<PlayerCompositionControl, bool>(nameof(ShowDiscCenter), false);

    public bool ShowDiscCenter
    {
        get => GetValue(ShowDiscCenterProperty);
        set => SetValue(ShowDiscCenterProperty, value);
    }

    static PlayerCompositionControl()
    {
        PositionProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            var newValue = (double)(args.NewValue ?? 0.0);
            Debug.WriteLine($"Position changed to: {newValue}");
            control._visual?.SendHandlerMessage(new PlayerProgressMessage(newValue, control.Duration));
        });

        DurationProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            var newValue = (double)(args.NewValue ?? 0.0);
            Debug.WriteLine($"Duration changed to: {newValue}");
            control._visual?.SendHandlerMessage(new PlayerProgressMessage(control.Position, newValue));
        });

        CircleBackgroundColorProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            if (control._visual != null)
            {
                var color = control.GetSKColor(args.NewValue as IBrush);
                control._visual.SendHandlerMessage(new PlayerCircleBackgroundColorMessage(color));
            }
        });

        CircleBackgroundBitmapProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            if (control._visual != null)
            {
                var bitmap = control.ConvertToSKBitmap(args.NewValue as Bitmap);
                control._visual.SendHandlerMessage(new PlayerCircleBackgroundBitmapMessage(bitmap));
            }
        });

        CircleBackgroundOpacityProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            if (control._visual != null)
            {
                var opacity = (double)(args.NewValue ?? 1.0);
                control._visual.SendHandlerMessage(new PlayerCircleBackgroundOpacityMessage((float)opacity));
            }
        });

        ShowTimeProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerShowTimeMessage((bool)(args.NewValue ?? true)));
        });

        ShowPlayPauseProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerShowPlayPauseMessage((bool)(args.NewValue ?? false)));
        });

        IsPlayingProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerIsPlayingMessage((bool)(args.NewValue ?? false)));
        });

        PlayPauseOpacityProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerPlayPauseOpacityMessage((float)(double)(args.NewValue ?? 1.0)));
        });

        RotateProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerRotateMessage((bool)(args.NewValue ?? false)));
        });

        ShowDiscCenterProperty.Changed.AddClassHandler<PlayerCompositionControl>((control, args) =>
        {
            control._visual?.SendHandlerMessage(new PlayerShowDiscCenterMessage((bool)(args.NewValue ?? false)));
        });
    }

    public PlayerCompositionControl()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Focusable = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var localPos = e.GetPosition(this);
        var isOnRing = IsOnRing(localPos);
        var isOnCenter = IsOnCenter(localPos);

        if (isOnRing || (ShowPlayPause && isOnCenter))
        {
            var properties = e.GetCurrentPoint(this).Properties;
            if (properties.IsLeftButtonPressed)
            {
                Focus();

                if (ShowPlayPause && isOnCenter && TogglePlayCommand != null)
                {
                    if (TogglePlayCommand.CanExecute(null))
                        TogglePlayCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (isOnRing)
                {
                    _isPressed = true;
                    e.Pointer.Capture(this);
                    CalculateAndSeek(localPos, false);
                }

                e.Handled = true;
                return;
            }
        }
        base.OnPointerPressed(e);
    }

    private bool IsOnCenter(Point pointerPos)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var delta = pointerPos - center;
        var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var innerRadius = (size / 2.0) - 40.0; // Play/Pause area inside the ring
        return distance < innerRadius;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isPressed)
        {
            CalculateAndSeek(e.GetPosition(this), true);
            e.Handled = true;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isPressed)
        {
            _isPressed = false;
            e.Pointer.Capture(null);
            CalculateAndSeek(e.GetPosition(this), false); // Final update
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    private bool IsOnRing(Point pointerPos)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var delta = pointerPos - center;
        var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var ringRadius = (size / 2.0) - 13.5; // Matches calculation in visual handler

        // Allow a 25px tolerance around the ring
        return Math.Abs(distance - ringRadius) < 25.0;
    }

    private void CalculateAndSeek(Point pointerPos, bool throttle)
    {
        if (Duration <= 0) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var delta = pointerPos - center;

        // Calculate progress based on angle (12 o'clock is -90 degrees)
        var angleRad = Math.Atan2(delta.Y, delta.X);
        var angleDeg = angleRad * (180.0 / Math.PI);
        var normalizedAngle = (angleDeg + 90.0 + 360.0) % 360.0;
        var progress = normalizedAngle / 360.0;

        var newPos = progress * Duration;

        // Update UI immediately for visual responsiveness
        Position = newPos;

        if (throttle)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastSeekTime < 300) // Increased throttle for performance
                return;
            _lastSeekTime = now;
        }

        if (SetPositionCommand != null && SetPositionCommand.CanExecute(newPos))
        {
            SetPositionCommand.Execute(newPos);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;

        _visual = compositor.CreateCustomVisual(new PlayerCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.SendHandlerMessage(new Vector2((float)Bounds.Width, (float)Bounds.Height));

        SendAllPropertiesToVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _visual?.SendHandlerMessage(null!);
        _visual = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual != null)
        {
            _visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            _visual.SendHandlerMessage(new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        }
    }

    private void SendAllPropertiesToVisual()
    {
        if (_visual == null) return;

        _visual.SendHandlerMessage(new PlayerCircleBackgroundColorMessage(GetSKColor(CircleBackgroundColor)));
        _visual.SendHandlerMessage(new PlayerCircleBackgroundOpacityMessage((float)CircleBackgroundOpacity));
        if (CircleBackgroundBitmap != null)
        {
            _visual.SendHandlerMessage(new PlayerCircleBackgroundBitmapMessage(ConvertToSKBitmap(CircleBackgroundBitmap)));
        }
        _visual.SendHandlerMessage(new PlayerProgressMessage(Position, Duration));
        _visual.SendHandlerMessage(new PlayerShowTimeMessage(ShowTime));
        _visual.SendHandlerMessage(new PlayerShowPlayPauseMessage(ShowPlayPause));
        _visual.SendHandlerMessage(new PlayerIsPlayingMessage(IsPlaying));
        _visual.SendHandlerMessage(new PlayerPlayPauseOpacityMessage((float)PlayPauseOpacity));
        _visual.SendHandlerMessage(new PlayerRotateMessage(Rotate));
        _visual.SendHandlerMessage(new PlayerShowDiscCenterMessage(ShowDiscCenter));
    }

    private SKColor GetSKColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        return SKColors.Transparent;
    }

    private SKBitmap? ConvertToSKBitmap(Bitmap? bitmap)
    {
        if (bitmap == null) return null;

        try
        {
            using var memoryStream = new System.IO.MemoryStream();
            bitmap.Save(memoryStream);
            memoryStream.Seek(0, System.IO.SeekOrigin.Begin);
            return SKBitmap.Decode(memoryStream);
        }
        catch
        {
            return null;
        }
    }
}
