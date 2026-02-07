using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AES_Controls.Helpers;
using Avalonia.Media.Imaging;

namespace AES_Controls;

public class ScalableDecorator : Decorator
{
    /// <summary>
    /// Dependency property that controls the scale factor applied to the child
    /// content. A value of 1.0 displays the child at its native size, values
    /// greater than 1.0 enlarge and values between 0 and 1 shrink the content.
    /// </summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ScalableDecorator, double>(nameof(Scale), 1.0);

    /// <summary>
    /// Dependency property that determines whether high-quality bitmap
    /// interpolation should be used when rendering scaled content.
    /// </summary>
    public static readonly StyledProperty<bool> AntialiasProperty =
        AvaloniaProperty.Register<ScalableDecorator, bool>(nameof(Antialias), true);

    /// <summary>
    /// Scale factor applied to the child content.
    /// </summary>
    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>
    /// When true, use high-quality bitmap interpolation when scaling the child.
    /// </summary>
    public bool Antialias
    {
        get => GetValue(AntialiasProperty);
        set => SetValue(AntialiasProperty, value);
    }

    private Window? _hostWindow;
    private IDisposable? _boundsSubscription;

    /// <summary>
    /// Create a new <see cref="ScalableDecorator"/>. The decorator clips
    /// child content to its bounds by default.
    /// </summary>
    public ScalableDecorator()
    {
        ClipToBounds = true;
    }

    /// <summary>
    /// Called when the control is attached to the visual tree. Subscribes to
    /// the host window's bounds changes so the decorator can re-measure the
    /// child when the window size changes.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hostWindow = e.Root as Window;

        if (_hostWindow != null)
        {
            _boundsSubscription = _hostWindow.GetObservable(Visual.BoundsProperty)
                .Subscribe(new SimpleObserver<Rect>(_ => InvalidateMeasure()));
        }
    }

    /// <summary>
    /// Called when the control is detached from the visual tree. Unsubscribes
    /// from any host window notifications and clears references.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _boundsSubscription?.Dispose();
        _hostWindow = null;
    }

    /// <summary>
    /// Monitors changes to the <see cref="Scale"/> and <see cref="Antialias"/>
    /// properties and invalidates measure/visual when they change.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ScaleProperty && change.Property != AntialiasProperty) return;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Measures the child control using the scaled available size and then
    /// returns the unscaled desired size so layout behaves as if the child
    /// were smaller/larger by the specified scale factor.
    /// </summary>
    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return new Size(0, 0);

        var s = Scale;
        if (s < 0.1) s = 0.1;

        var constraint = new Size(available.Width * s, available.Height * s);
        Child.Measure(constraint);

        return Child.DesiredSize / s;
    }

    /// <summary>
    /// Arranges the child at the scaled size and sets a render transform so
    /// the visual output appears scaled while the layout system receives the
    /// unscaled size.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null) return finalSize;

        var s = Scale;
        if (s < 0.1) s = 0.1;

        Child.Arrange(new Rect(new Point(0, 0), finalSize * s));
        Child.RenderTransformOrigin = RelativePoint.TopLeft;
        Child.RenderTransform = new ScaleTransform(1.0 / s, 1.0 / s);

        var interpolationMode = Antialias ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.LowQuality;
        RenderOptions.SetBitmapInterpolationMode(Child, interpolationMode);

        return finalSize;
    }
}
