using System.Collections.Specialized;
using System.Numerics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using SkiaSharp;

namespace AES_Controls.Player.Spectrum;

/// <summary>
/// Composition-based spectrum visualiser. Physics and bar rendering run on the
/// compositor animation thread into a fixed-size offscreen surface; presentation
/// is a single blit to the control bounds.
/// </summary>
public sealed class GlSpectrumControl : Control, IDisposable
{
    private const float DefaultPeakThicknessPixels = 2.0f;

    public static readonly StyledProperty<AvaloniaList<double>?> SpectrumProperty =
        AvaloniaProperty.Register<GlSpectrumControl, AvaloniaList<double>?>(nameof(Spectrum));

    public static readonly StyledProperty<bool> UseDeltaTimeProperty =
        AvaloniaProperty.Register<GlSpectrumControl, bool>(nameof(UseDeltaTime), true);

    public static readonly StyledProperty<bool> DisableVSyncProperty =
        AvaloniaProperty.Register<GlSpectrumControl, bool>(nameof(DisableVSync), true);

    public static readonly StyledProperty<bool> IsRenderingPausedProperty =
        AvaloniaProperty.Register<GlSpectrumControl, bool>(nameof(IsRenderingPaused), false);

    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BarWidth), 4.0);

    public static readonly StyledProperty<double> BarSpacingProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BarSpacing), 2.0);

    public static readonly StyledProperty<double> BlockHeightProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(BlockHeight), 8.0);

    public static readonly StyledProperty<double> AttackLerpProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(AttackLerp), 0.42);

    public static readonly StyledProperty<double> ReleaseLerpProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(ReleaseLerp), 0.38);

    public static readonly StyledProperty<double> PeakDecayProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(PeakDecay), 0.85);

    public static readonly StyledProperty<double> PrePowAttackAlphaProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(PrePowAttackAlpha), 0.90);

    public static readonly StyledProperty<double> MaxRiseFractionProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(MaxRiseFraction), 0.55);

    public static readonly StyledProperty<double> MaxRiseAbsoluteProperty =
        AvaloniaProperty.Register<GlSpectrumControl, double>(nameof(MaxRiseAbsolute), 0.05);

    public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty =
        AvaloniaProperty.Register<GlSpectrumControl, LinearGradientBrush?>(nameof(BarGradient),
            new LinearGradientBrush
            {
                GradientStops =
                [
                    new GradientStop(Color.Parse("#00CCFF"), 0.0),
                    new GradientStop(Color.Parse("#3333FF"), 0.25),
                    new GradientStop(Color.Parse("#CC00CC"), 0.5),
                    new GradientStop(Color.Parse("#FF004D"), 0.75),
                    new GradientStop(Color.Parse("#FFB300"), 1.0)
                ]
            });

    public AvaloniaList<double>? Spectrum
    {
        get => GetValue(SpectrumProperty);
        set => SetValue(SpectrumProperty, value);
    }

    public bool UseDeltaTime
    {
        get => GetValue(UseDeltaTimeProperty);
        set => SetValue(UseDeltaTimeProperty, value);
    }

    public bool DisableVSync
    {
        get => GetValue(DisableVSyncProperty);
        set => SetValue(DisableVSyncProperty, value);
    }

    public bool IsRenderingPaused
    {
        get => GetValue(IsRenderingPausedProperty);
        set => SetValue(IsRenderingPausedProperty, value);
    }

    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public double BarSpacing
    {
        get => GetValue(BarSpacingProperty);
        set => SetValue(BarSpacingProperty, value);
    }

    public double BlockHeight
    {
        get => GetValue(BlockHeightProperty);
        set => SetValue(BlockHeightProperty, value);
    }

    public double AttackLerp
    {
        get => GetValue(AttackLerpProperty);
        set => SetValue(AttackLerpProperty, value);
    }

    public double ReleaseLerp
    {
        get => GetValue(ReleaseLerpProperty);
        set => SetValue(ReleaseLerpProperty, value);
    }

    public double PeakDecay
    {
        get => GetValue(PeakDecayProperty);
        set => SetValue(PeakDecayProperty, value);
    }

    public double PrePowAttackAlpha
    {
        get => GetValue(PrePowAttackAlphaProperty);
        set => SetValue(PrePowAttackAlphaProperty, value);
    }

    public double MaxRiseFraction
    {
        get => GetValue(MaxRiseFractionProperty);
        set => SetValue(MaxRiseFractionProperty, value);
    }

    public double MaxRiseAbsolute
    {
        get => GetValue(MaxRiseAbsoluteProperty);
        set => SetValue(MaxRiseAbsoluteProperty, value);
    }

    public LinearGradientBrush? BarGradient
    {
        get => GetValue(BarGradientProperty);
        set => SetValue(BarGradientProperty, value);
    }

    private CompositionCustomVisual? _visual;
    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;
    private bool _isAttachedToVisualTree;

    public GlSpectrumControl()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new SpectrumCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        PushAllStateToHandler();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        _visual?.SendHandlerMessage(new SpectrumRuntimeMessage(false, true));

        if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null)
            _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler;
        _spectrumCollectionRef = null;
        _spectrumCollectionHandler = null;

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
        PushSizeToHandler();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SpectrumProperty)
        {
            AttachSpectrumCollection(change.GetNewValue<AvaloniaList<double>?>());
            _visual?.SendHandlerMessage(new SpectrumSourceMessage(Spectrum));
            return;
        }

        if (change.Property == IsVisibleProperty
            || change.Property == IsRenderingPausedProperty)
        {
            PushRuntimeToHandler();
            return;
        }

        if (change.Property == OpacityProperty)
        {
            PushOpacityToHandler();
            return;
        }

        if (change.Property == BarGradientProperty)
        {
            PushGradientToHandler();
            return;
        }

        if (IsRenderAffectingProperty(change.Property))
            PushConfigToHandler();
    }

    private static bool IsRenderAffectingProperty(AvaloniaProperty property)
    {
        return property == UseDeltaTimeProperty
            || property == DisableVSyncProperty
            || property == BarWidthProperty
            || property == BarSpacingProperty
            || property == BlockHeightProperty
            || property == AttackLerpProperty
            || property == ReleaseLerpProperty
            || property == PeakDecayProperty
            || property == PrePowAttackAlphaProperty
            || property == MaxRiseFractionProperty
            || property == MaxRiseAbsoluteProperty;
    }

    private void AttachSpectrumCollection(AvaloniaList<double>? col)
    {
        if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null)
            _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler;

        _spectrumCollectionRef = null;
        _spectrumCollectionHandler = null;

        if (col is INotifyCollectionChanged notify)
        {
            _spectrumCollectionRef = notify;
            _spectrumCollectionHandler = (_, _) =>
            {
                if (!ShouldRunCompositorAnimation())
                    return;

                _visual?.SendHandlerMessage(new SpectrumWakeMessage());
            };
            notify.CollectionChanged += _spectrumCollectionHandler;
        }
    }

    private bool ShouldRunCompositorAnimation() =>
        _visual != null && _isAttachedToVisualTree && IsVisible && !IsRenderingPaused;

    private void PushAllStateToHandler()
    {
        PushSizeToHandler();
        PushConfigToHandler();
        PushGradientToHandler();
        PushOpacityToHandler();
        PushRuntimeToHandler();
        AttachSpectrumCollection(Spectrum);
        _visual?.SendHandlerMessage(new SpectrumSourceMessage(Spectrum));
    }

    private void PushSizeToHandler()
    {
        if (_visual == null)
            return;

        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }

    private void PushConfigToHandler()
    {
        if (_visual == null)
            return;

        float scaling = (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
        _visual.SendHandlerMessage(new SpectrumConfigMessage(
            (float)BarWidth,
            (float)BarSpacing,
            (float)BlockHeight,
            DefaultPeakThicknessPixels / Math.Max(1f, scaling),
            UseDeltaTime,
            AttackLerp,
            ReleaseLerp,
            PeakDecay,
            PrePowAttackAlpha,
            MaxRiseFraction,
            MaxRiseAbsolute));
    }

    private void PushGradientToHandler()
    {
        if (_visual == null)
            return;

        var colors = new SKColor[5];
        var stops = BarGradient?.GradientStops;
        if (stops == null || stops.Count == 0)
        {
            colors[0] = colors[1] = colors[2] = colors[3] = colors[4] = new SKColor(0x00, 0xCC, 0xFF);
        }
        else
        {
            for (int i = 0; i < colors.Length; i++)
            {
                var color = GetColorAtOffset(stops, i / 4.0f);
                colors[i] = new SKColor(color.R, color.G, color.B, color.A);
            }
        }

        _visual.SendHandlerMessage(new SpectrumGradientMessage(colors));
    }

    private void PushOpacityToHandler()
    {
        _visual?.SendHandlerMessage(new SpectrumOpacityMessage((float)Opacity));
    }

    private void PushRuntimeToHandler()
    {
        var runtimeVisible = IsVisible && _isAttachedToVisualTree;
        _visual?.SendHandlerMessage(new SpectrumRuntimeMessage(runtimeVisible, IsRenderingPaused || !runtimeVisible));
    }

    private static Color GetColorAtOffset(AvaloniaList<GradientStop> stops, float offset)
    {
        GradientStop left = stops[0];
        GradientStop right = stops[0];
        double leftOffset = double.NegativeInfinity;
        double rightOffset = double.PositiveInfinity;

        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            double stopOffset = stop.Offset;

            if (stopOffset <= offset && stopOffset >= leftOffset)
            {
                left = stop;
                leftOffset = stopOffset;
            }

            if (stopOffset >= offset && stopOffset <= rightOffset)
            {
                right = stop;
                rightOffset = stopOffset;
            }
        }

        if (double.IsNegativeInfinity(leftOffset))
            left = right;

        if (double.IsPositiveInfinity(rightOffset))
            right = left;

        if (Math.Abs(leftOffset - rightOffset) < 0.0001)
            return left.Color;

        float t = (offset - (float)left.Offset) / (float)(right.Offset - left.Offset);
        return Color.FromArgb(
            (byte)(left.Color.A + ((right.Color.A - left.Color.A) * t)),
            (byte)(left.Color.R + ((right.Color.R - left.Color.R) * t)),
            (byte)(left.Color.G + ((right.Color.G - left.Color.G) * t)),
            (byte)(left.Color.B + ((right.Color.B - left.Color.B) * t)));
    }

    public void Dispose()
    {
    }
}
