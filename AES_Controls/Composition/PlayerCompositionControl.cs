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
        if (IsOnRing(localPos))
        {
            var properties = e.GetCurrentPoint(this).Properties;
            if (properties.IsLeftButtonPressed)
            {
                Focus();
                _isPressed = true;
                e.Pointer.Capture(this);
                CalculateAndSeek(localPos, false);
                e.Handled = true;
                return;
            }
        }
        base.OnPointerPressed(e);
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
