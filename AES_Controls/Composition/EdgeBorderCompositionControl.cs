using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace AES_Controls.Composition;

public sealed class EdgeBorderCompositionControl : Control
{
    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(BorderThickness), 1.5);

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(CornerRadius), 10.0);

    public static readonly StyledProperty<double> GlowSizeProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(GlowSize), 12.0);

    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(Speed), 0.085);

    public static readonly StyledProperty<double> HighlightLengthProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(HighlightLength), 0.18);

    public static readonly StyledProperty<double> OutsideOffsetProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(OutsideOffset), 3.0);

    public static readonly StyledProperty<double> BaseOpacityProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, double>(nameof(BaseOpacity), 0.42);

    public static readonly StyledProperty<Color> BaseStartColorProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, Color>(nameof(BaseStartColor), Color.Parse("#14D4FF"));

    public static readonly StyledProperty<Color> BaseEndColorProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, Color>(nameof(BaseEndColor), Color.Parse("#6A38FF"));

    public static readonly StyledProperty<Color> HighlightLeadColorProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, Color>(nameof(HighlightLeadColor), Color.Parse("#19F0FF"));

    public static readonly StyledProperty<Color> HighlightTailColorProperty =
        AvaloniaProperty.Register<EdgeBorderCompositionControl, Color>(nameof(HighlightTailColor), Color.Parse("#A24BFF"));

    private CompositionCustomVisual? _visual;

    public EdgeBorderCompositionControl()
    {
        ClipToBounds = false;
    }

    public double BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double GlowSize
    {
        get => GetValue(GlowSizeProperty);
        set => SetValue(GlowSizeProperty, value);
    }

    public double Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public double HighlightLength
    {
        get => GetValue(HighlightLengthProperty);
        set => SetValue(HighlightLengthProperty, value);
    }

    public double OutsideOffset
    {
        get => GetValue(OutsideOffsetProperty);
        set => SetValue(OutsideOffsetProperty, value);
    }

    public double BaseOpacity
    {
        get => GetValue(BaseOpacityProperty);
        set => SetValue(BaseOpacityProperty, value);
    }

    public Color BaseStartColor
    {
        get => GetValue(BaseStartColorProperty);
        set => SetValue(BaseStartColorProperty, value);
    }

    public Color BaseEndColor
    {
        get => GetValue(BaseEndColorProperty);
        set => SetValue(BaseEndColorProperty, value);
    }

    public Color HighlightLeadColor
    {
        get => GetValue(HighlightLeadColorProperty);
        set => SetValue(HighlightLeadColorProperty, value);
    }

    public Color HighlightTailColor
    {
        get => GetValue(HighlightTailColorProperty);
        set => SetValue(HighlightTailColorProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new EdgeBorderCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        SendSize();
        SendConfig();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SendSize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_visual == null)
            return;

        if (change.Property == BoundsProperty)
        {
            SendSize();
            return;
        }

        if (change.Property == BorderThicknessProperty ||
            change.Property == CornerRadiusProperty ||
            change.Property == GlowSizeProperty ||
            change.Property == SpeedProperty ||
            change.Property == HighlightLengthProperty ||
            change.Property == OutsideOffsetProperty ||
            change.Property == BaseOpacityProperty ||
            change.Property == BaseStartColorProperty ||
            change.Property == BaseEndColorProperty ||
            change.Property == HighlightLeadColorProperty ||
            change.Property == HighlightTailColorProperty)
        {
            SendConfig();
        }
    }

    private void SendSize()
    {
        if (_visual == null)
            return;

        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }

    private void SendConfig()
    {
        if (_visual == null)
            return;

        _visual.SendHandlerMessage(new EdgeBorderConfigMessage(
            (float)BorderThickness,
            (float)CornerRadius,
            (float)GlowSize,
            (float)Speed,
            (float)HighlightLength,
            (float)OutsideOffset,
            (float)BaseOpacity,
            ToSkColor(BaseStartColor),
            ToSkColor(BaseEndColor),
            ToSkColor(HighlightLeadColor),
            ToSkColor(HighlightTailColor)));
    }

    private static SkiaSharp.SKColor ToSkColor(Color color) => new(color.R, color.G, color.B, color.A);
}
