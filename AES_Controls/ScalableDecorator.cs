using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AES_Controls.Helpers;

namespace AES_Controls;

public class ScalableDecorator : Decorator
{
    public static readonly AttachedProperty<bool> ExcludeFromScaleProperty =
        AvaloniaProperty.RegisterAttached<ScalableDecorator, Visual, bool>("ExcludeFromScale");

    /// <summary>
    /// When <see cref="ExcludeFromScaleProperty"/> is true, applies a render transform that
    /// cancels the ancestor decorator shrink (for stretch-filled surfaces like capture hosts).
    /// Set to false for fixed-layout regions such as album lists that should keep their
    /// current scaled layout size.
    /// </summary>
    public static readonly AttachedProperty<bool> ExcludeFromScaleCompensationProperty =
        AvaloniaProperty.RegisterAttached<ScalableDecorator, Visual, bool>(
            "ExcludeFromScaleCompensation",
            defaultValue: true);

    static ScalableDecorator()
    {
        ExcludeFromScaleProperty.Changed.AddClassHandler<Visual>(OnExcludeFromScaleChanged);
        ExcludeFromScaleCompensationProperty.Changed.AddClassHandler<Visual>(OnExcludeFromScaleCompensationChanged);
    }

    public static bool GetExcludeFromScale(Visual element) =>
        element.GetValue(ExcludeFromScaleProperty);

    public static void SetExcludeFromScale(Visual element, bool value) =>
        element.SetValue(ExcludeFromScaleProperty, value);

    public static bool GetExcludeFromScaleCompensation(Visual element) =>
        element.GetValue(ExcludeFromScaleCompensationProperty);

    public static void SetExcludeFromScaleCompensation(Visual element, bool value) =>
        element.SetValue(ExcludeFromScaleCompensationProperty, value);

    /// <summary>
    /// Returns the render scale applied by the nearest ancestor <see cref="ScalableDecorator"/>
    /// when <see cref="ExcludeFromScaleProperty"/> is set on this visual or an ancestor.
    /// </summary>
    public static double GetExclusionRenderScale(Visual? visual)
    {
        if (visual == null)
            return 1.0;

        for (var current = visual; current != null; current = current.GetVisualParent())
        {
            if (!GetExcludeFromScale(current))
                continue;

            var decorator = current.FindAncestorOfType<ScalableDecorator>();
            return decorator?.GetContentRenderScale() ?? 1.0;
        }

        return 1.0;
    }

    /// <summary>
    /// Maps a control's layout bounds to the render resolution that matches on-screen pixels
    /// when scale exclusion is active.
    /// </summary>
    public static Size GetExclusionAwareRenderSize(Visual visual, Size boundsSize)
    {
        var scale = GetExclusionRenderScale(visual);
        if (scale <= 1.0)
            return boundsSize;

        return new Size(
            Math.Max(1, boundsSize.Width / scale),
            Math.Max(1, boundsSize.Height / scale));
    }

    private static void OnExcludeFromScaleChanged(Visual visual, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.NewValue is bool excluded)
        {
            if (excluded)
                ScaleExclusion.Attach(visual);
            else
                ScaleExclusion.Detach(visual);
        }
    }

    private static void OnExcludeFromScaleCompensationChanged(Visual visual, AvaloniaPropertyChangedEventArgs change) =>
        ScaleExclusion.RefreshIfAttached(visual);

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ScalableDecorator, double>(nameof(Scale), 1.0);

    public static readonly StyledProperty<bool> AntialiasProperty =
        AvaloniaProperty.Register<ScalableDecorator, bool>(nameof(Antialias), true);

    /// <summary>
    /// When true, limits internal render supersampling on high-DPI/large surfaces
    /// while preserving layout size and the user's scale preference.
    /// </summary>
    public static readonly StyledProperty<bool> EfficientScalingProperty =
        AvaloniaProperty.Register<ScalableDecorator, bool>(nameof(EfficientScaling), true);

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public bool Antialias
    {
        get => GetValue(AntialiasProperty);
        set => SetValue(AntialiasProperty, value);
    }

    public bool EfficientScaling
    {
        get => GetValue(EfficientScalingProperty);
        set => SetValue(EfficientScalingProperty, value);
    }

    private TopLevel? _hostTopLevel;
    private IDisposable? _clientSizeSubscription;
    private ScaleTransform? _childScaleTransform;
    private BitmapInterpolationMode? _appliedInterpolationMode;

    private double _lastObservedClientWidth = double.NaN;
    private double _lastObservedClientHeight = double.NaN;
    private bool _measureInvalidationPosted;

    private Size _lastMeasureAvailable;
    private double _lastMeasureLayoutScale = double.NaN;
    private Size _lastMeasureResult;
    private bool _hasMeasureCache;

    private double _lastArrangeLayoutScale = double.NaN;
    private double _lastArrangeRenderScale = double.NaN;

    public ScalableDecorator()
    {
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateHostSubscription();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _clientSizeSubscription?.Dispose();
        _clientSizeSubscription = null;
        _hostTopLevel = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChildProperty)
        {
            _childScaleTransform = null;
            _appliedInterpolationMode = null;
            InvalidateMeasureCache();
            return;
        }

        if (change.Property == ScaleProperty)
        {
            InvalidateMeasureCache();
            UpdateHostSubscription();
            InvalidateMeasure();
            return;
        }

        if (change.Property == EfficientScalingProperty)
        {
            InvalidateMeasureCache();
            UpdateHostSubscription();
            InvalidateMeasure();
            return;
        }

        if (change.Property == AntialiasProperty)
        {
            _appliedInterpolationMode = null;
            InvalidateVisual();
        }
    }

    private void UpdateHostSubscription()
    {
        _clientSizeSubscription?.Dispose();
        _clientSizeSubscription = null;
        _hostTopLevel = null;

        if (GetLayoutScale() <= 1.0)
            return;

        _hostTopLevel = TopLevel.GetTopLevel(this);
        if (_hostTopLevel == null)
            return;

        _clientSizeSubscription = _hostTopLevel.GetObservable(TopLevel.ClientSizeProperty)
            .Subscribe(new SimpleObserver<Size>(OnHostClientSizeChanged));
    }

    private void OnHostClientSizeChanged(Size clientSize)
    {
        if (EfficientScaling &&
            Math.Abs(clientSize.Width - _lastObservedClientWidth) < 0.5 &&
            Math.Abs(clientSize.Height - _lastObservedClientHeight) < 0.5)
            return;

        _lastObservedClientWidth = clientSize.Width;
        _lastObservedClientHeight = clientSize.Height;
        InvalidateMeasureCache();
        RequestMeasureInvalidation();
    }

    private void RequestMeasureInvalidation()
    {
        if (!EfficientScaling)
        {
            InvalidateMeasure();
            return;
        }

        if (_measureInvalidationPosted)
            return;

        _measureInvalidationPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _measureInvalidationPosted = false;
            InvalidateMeasure();
        }, DispatcherPriority.Background);
    }

    private void InvalidateMeasureCache()
    {
        _hasMeasureCache = false;
        _lastMeasureLayoutScale = double.NaN;
    }

    private double GetLayoutScale() => Math.Clamp(Scale, 0.1, 10.0);

    /// <summary>
    /// Effective internal render scale used by this decorator (matches arrange multiplier).
    /// </summary>
    public double GetContentRenderScale() => GetRenderScale(GetLayoutScale());

    /// <summary>
    /// Internal arrange/render multiplier paired with the inverse child transform.
    /// Must match <paramref name="layoutScale"/> so measure/arrange preserve the user's scale.
    /// </summary>
    private double GetRenderScale(double layoutScale)
    {
        if (!EfficientScaling || layoutScale <= 1.0)
            return layoutScale;

        // Do not cap to TopLevel.RenderScaling (Windows display DPI). That value is already
        // applied by the compositor; clamping here incorrectly limits UI scale to 1.5x on 150%
        // displays even when ScaleFactor or carousel/album settings request 2x–3x.
        return layoutScale;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null)
            return default;

        var layoutScale = GetLayoutScale();

        if (_hasMeasureCache &&
            layoutScale == _lastMeasureLayoutScale &&
            available == _lastMeasureAvailable)
            return _lastMeasureResult;

        Child.Measure(new Size(available.Width * layoutScale, available.Height * layoutScale));

        var result = Child.DesiredSize / layoutScale;

        _lastMeasureAvailable = available;
        _lastMeasureLayoutScale = layoutScale;
        _lastMeasureResult = result;
        _hasMeasureCache = true;

        return result;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null)
            return finalSize;

        var layoutScale = GetLayoutScale();
        var renderScale = GetRenderScale(layoutScale);

        if (layoutScale != _lastArrangeLayoutScale || renderScale != _lastArrangeRenderScale)
        {
            ApplyChildScale(1.0 / renderScale);
            ApplyChildInterpolationMode();
            _lastArrangeLayoutScale = layoutScale;
            _lastArrangeRenderScale = renderScale;
        }

        Child.Arrange(new Rect(0, 0, finalSize.Width * renderScale, finalSize.Height * renderScale));
        return finalSize;
    }

    private void ApplyChildScale(double scale)
    {
        if (Child == null)
            return;

        if (_childScaleTransform == null)
        {
            _childScaleTransform = new ScaleTransform(scale, scale);
            Child.RenderTransformOrigin = RelativePoint.TopLeft;
            Child.RenderTransform = _childScaleTransform;
            return;
        }

        if (_childScaleTransform.ScaleX != scale)
            _childScaleTransform.ScaleX = scale;

        if (_childScaleTransform.ScaleY != scale)
            _childScaleTransform.ScaleY = scale;

        if (!ReferenceEquals(Child.RenderTransform, _childScaleTransform))
        {
            Child.RenderTransformOrigin = RelativePoint.TopLeft;
            Child.RenderTransform = _childScaleTransform;
        }
    }

    private void ApplyChildInterpolationMode()
    {
        if (Child == null)
            return;

        var mode = Antialias && !EfficientScaling
            ? BitmapInterpolationMode.HighQuality
            : BitmapInterpolationMode.LowQuality;

        if (_appliedInterpolationMode == mode)
            return;

        RenderOptions.SetBitmapInterpolationMode(Child, mode);
        _appliedInterpolationMode = mode;
    }
}
