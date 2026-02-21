using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// A custom control that renders a circular clock widget with time, date,
/// and weekday using the Avalonia composition API.
/// </summary>
public class ClockCompositionControl : Control
{
    private CompositionCustomVisual? _visual;
    private DispatcherTimer? _updateTimer;

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ClockCompositionControl, IBrush?>(nameof(Background), Brushes.Transparent);

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> RingColorProperty =
        AvaloniaProperty.Register<ClockCompositionControl, IBrush?>(nameof(RingColor), Brushes.White);

    public IBrush? RingColor
    {
        get => GetValue(RingColorProperty);
        set => SetValue(RingColorProperty, value);
    }

    public static readonly StyledProperty<IBrush?> TextColorProperty =
        AvaloniaProperty.Register<ClockCompositionControl, IBrush?>(nameof(TextColor), Brushes.White);

    public IBrush? TextColor
    {
        get => GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<ClockCompositionControl, double>(nameof(RingThickness), 4.0);

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public static readonly StyledProperty<double> RingGapAngleProperty =
        AvaloniaProperty.Register<ClockCompositionControl, double>(nameof(RingGapAngle), 60.0);

    public double RingGapAngle
    {
        get => GetValue(RingGapAngleProperty);
        set => SetValue(RingGapAngleProperty, value);
    }

    public static readonly StyledProperty<IBrush?> CircleBackgroundColorProperty =
        AvaloniaProperty.Register<ClockCompositionControl, IBrush?>(nameof(CircleBackgroundColor));

    public IBrush? CircleBackgroundColor
    {
        get => GetValue(CircleBackgroundColorProperty);
        set => SetValue(CircleBackgroundColorProperty, value);
    }

    public static readonly StyledProperty<Bitmap?> CircleBackgroundBitmapProperty =
        AvaloniaProperty.Register<ClockCompositionControl, Bitmap?>(nameof(CircleBackgroundBitmap));

    public Bitmap? CircleBackgroundBitmap
    {
        get => GetValue(CircleBackgroundBitmapProperty);
        set => SetValue(CircleBackgroundBitmapProperty, value);
    }

    public static readonly StyledProperty<double> CircleBackgroundOpacityProperty =
        AvaloniaProperty.Register<ClockCompositionControl, double>(nameof(CircleBackgroundOpacity), 1.0);

    public double CircleBackgroundOpacity
    {
        get => GetValue(CircleBackgroundOpacityProperty);
        set => SetValue(CircleBackgroundOpacityProperty, value);
    }

    static ClockCompositionControl()
    {
        AffectsRender<ClockCompositionControl>(
            BackgroundProperty,
            RingColorProperty,
            TextColorProperty,
            RingThicknessProperty,
            RingGapAngleProperty,
            CircleBackgroundColorProperty,
            CircleBackgroundBitmapProperty,
            CircleBackgroundOpacityProperty);
    }

    public ClockCompositionControl()
    {
        ClipToBounds = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;

        _visual = compositor.CreateCustomVisual(new ClockCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.SendHandlerMessage(new Vector2((float)Bounds.Width, (float)Bounds.Height));

        // Start update timer (update every second)
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _updateTimer.Tick += OnTimerTick;
        _updateTimer.Start();

        SendAllPropertiesToVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _updateTimer?.Stop();
        _updateTimer = null;
        _visual?.SendHandlerMessage(null!);
        _visual = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_visual == null) return;

        if (change.Property == RingColorProperty && change.NewValue is IBrush ringBrush)
        {
            _visual.SendHandlerMessage(new ClockRingColorMessage(GetSKColor(ringBrush)));
        }
        else if (change.Property == TextColorProperty && change.NewValue is IBrush textBrush)
        {
            _visual.SendHandlerMessage(new ClockTextColorMessage(GetSKColor(textBrush)));
        }
        else if (change.Property == RingThicknessProperty && change.NewValue is double thickness)
        {
            _visual.SendHandlerMessage(new ClockRingThicknessMessage((float)thickness));
        }
        else if (change.Property == RingGapAngleProperty && change.NewValue is double gapAngle)
        {
            _visual.SendHandlerMessage(new ClockRingGapAngleMessage((float)gapAngle));
        }
        else if (change.Property == CircleBackgroundColorProperty && change.NewValue is IBrush circleBrush)
        {
            _visual.SendHandlerMessage(new ClockCircleBackgroundColorMessage(GetSKColor(circleBrush)));
        }
        else if (change.Property == CircleBackgroundBitmapProperty && change.NewValue is Bitmap bitmap)
        {
            _visual.SendHandlerMessage(new ClockCircleBackgroundBitmapMessage(ConvertToSKBitmap(bitmap)));
        }
        else if (change.Property == CircleBackgroundOpacityProperty && change.NewValue is double opacity)
        {
            _visual.SendHandlerMessage(new ClockCircleBackgroundOpacityMessage((float)opacity));
        }
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

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Trigger a visual update to refresh time display
        _visual?.SendHandlerMessage(new ClockUpdateMessage(DateTime.Now));
    }

    private void SendAllPropertiesToVisual()
    {
        if (_visual == null) return;

        _visual.SendHandlerMessage(new ClockRingColorMessage(GetSKColor(RingColor)));
        _visual.SendHandlerMessage(new ClockTextColorMessage(GetSKColor(TextColor)));
        _visual.SendHandlerMessage(new ClockRingThicknessMessage((float)RingThickness));
        _visual.SendHandlerMessage(new ClockRingGapAngleMessage((float)RingGapAngle));
        _visual.SendHandlerMessage(new ClockCircleBackgroundColorMessage(GetSKColor(CircleBackgroundColor)));
        _visual.SendHandlerMessage(new ClockCircleBackgroundOpacityMessage((float)CircleBackgroundOpacity));
        if (CircleBackgroundBitmap != null)
        {
            _visual.SendHandlerMessage(new ClockCircleBackgroundBitmapMessage(ConvertToSKBitmap(CircleBackgroundBitmap)));
        }
        _visual.SendHandlerMessage(new ClockUpdateMessage(DateTime.Now));
    }

    private static SKColor GetSKColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush solid)
        {
            var c = solid.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        return SKColors.White;
    }

    private static SKBitmap? ConvertToSKBitmap(Bitmap? bitmap)
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
