using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using System.Numerics;
using System.Windows.Input;

namespace AES_Controls.Composition;

/// <summary>
/// Composition-based progress bar that renders an audio waveform and allows scrubbing by pointer.
/// </summary>
public class CompositionWaveformProgressBar : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<IList<float>?> WaveformProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IList<float>?>(nameof(Waveform));

    public static readonly StyledProperty<bool> IsDraggingProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(IsDragging));

    public static readonly StyledProperty<ICommand?> DragCompletedCommandProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, ICommand?>(nameof(DragCompletedCommand));

    public static readonly StyledProperty<IBrush?> PlayedColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(PlayedColor), Brushes.LightBlue);

    public static readonly StyledProperty<IBrush?> UnPlayedColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(UnPlayedColor), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> IndicatorColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(IndicatorColor), Brushes.White);

    public static readonly StyledProperty<IBrush?> TextForegroundColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(TextForegroundColor), Brushes.White);

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(TextSize), 12);

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(IsLoading));

    public static readonly StyledProperty<IBrush?> LoadingIndicatorColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(LoadingIndicatorColor), Brushes.White);

    public static readonly StyledProperty<double> LoadingIndicatorSizeProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(LoadingIndicatorSize), 30);

    public static readonly StyledProperty<IBrush?> TriangleColorProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, IBrush?>(nameof(TriangleColor), Brushes.White);

    public static readonly StyledProperty<double> TriangleOffsetProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(TriangleOffset), 2.0);

    public static readonly StyledProperty<double> TriangleWidthProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(TriangleWidth), 14.0);

    public static readonly StyledProperty<double> TriangleHeightProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(TriangleHeight), 12.0);

    public static readonly StyledProperty<double> WaveformVerticalOffsetProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(WaveformVerticalOffset), 5.0);

    public static readonly StyledProperty<bool> IsTriangleUpwardsProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(IsTriangleUpwards));

    public static readonly StyledProperty<Thickness> WaveformMarginProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, Thickness>(nameof(WaveformMargin), new Thickness(0));

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(BarGap), 0.0);

    public static readonly StyledProperty<double> BlockHeightProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(BlockHeight), 0.0);

    public static readonly StyledProperty<double> VerticalGapProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, double>(nameof(VerticalGap), 1.0);

    public static readonly StyledProperty<int> VisualBarCountProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, int>(nameof(VisualBarCount), 0);

    public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, LinearGradientBrush?>(nameof(BarGradient));

    public static readonly StyledProperty<bool> ShowReflectionProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(ShowReflection), false);

    public static readonly StyledProperty<bool> IsSymmetricProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(IsSymmetric), false);

    public static readonly StyledProperty<bool> IsDigitalModeProperty =
        AvaloniaProperty.Register<CompositionWaveformProgressBar, bool>(nameof(IsDigitalMode));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public IList<float>? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public ICommand? DragCompletedCommand { get => GetValue(DragCompletedCommandProperty); set => SetValue(DragCompletedCommandProperty, value); }
    public IBrush? PlayedColor { get => GetValue(PlayedColorProperty); set => SetValue(PlayedColorProperty, value); }
    public IBrush? UnPlayedColor { get => GetValue(UnPlayedColorProperty); set => SetValue(UnPlayedColorProperty, value); }
    public IBrush? IndicatorColor { get => GetValue(IndicatorColorProperty); set => SetValue(IndicatorColorProperty, value); }
    public IBrush? TextForegroundColor { get => GetValue(TextForegroundColorProperty); set => SetValue(TextForegroundColorProperty, value); }
    public double TextSize { get => GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public IBrush? LoadingIndicatorColor { get => GetValue(LoadingIndicatorColorProperty); set => SetValue(LoadingIndicatorColorProperty, value); }
    public double LoadingIndicatorSize { get => GetValue(LoadingIndicatorSizeProperty); set => SetValue(LoadingIndicatorSizeProperty, value); }
    public IBrush? TriangleColor { get => GetValue(TriangleColorProperty); set => SetValue(TriangleColorProperty, value); }
    public double TriangleOffset { get => GetValue(TriangleOffsetProperty); set => SetValue(TriangleOffsetProperty, value); }
    public double TriangleWidth { get => GetValue(TriangleWidthProperty); set => SetValue(TriangleWidthProperty, value); }
    public double TriangleHeight { get => GetValue(TriangleHeightProperty); set => SetValue(TriangleHeightProperty, value); }
    public double WaveformVerticalOffset { get => GetValue(WaveformVerticalOffsetProperty); set => SetValue(WaveformVerticalOffsetProperty, value); }
    public bool IsTriangleUpwards { get => GetValue(IsTriangleUpwardsProperty); set => SetValue(IsTriangleUpwardsProperty, value); }
    public Thickness WaveformMargin { get => GetValue(WaveformMarginProperty); set => SetValue(WaveformMarginProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
    public double BlockHeight { get => GetValue(BlockHeightProperty); set => SetValue(BlockHeightProperty, value); }
    public double VerticalGap { get => GetValue(VerticalGapProperty); set => SetValue(VerticalGapProperty, value); }
    public int VisualBarCount { get => GetValue(VisualBarCountProperty); set => SetValue(VisualBarCountProperty, value); }
    public bool IsDragging { get => GetValue(IsDraggingProperty); set => SetValue(IsDraggingProperty, value); }
    public LinearGradientBrush? BarGradient { get => GetValue(BarGradientProperty); set => SetValue(BarGradientProperty, value); }
    public bool ShowReflection { get => GetValue(ShowReflectionProperty); set => SetValue(ShowReflectionProperty, value); }
    public bool IsSymmetric { get => GetValue(IsSymmetricProperty); set => SetValue(IsSymmetricProperty, value); }
    public bool IsDigitalMode { get => GetValue(IsDigitalModeProperty); set => SetValue(IsDigitalModeProperty, value); }

    private CompositionCustomVisual? _visual;
    private double _lastValue;
    private double _dragValue;
    private double _loadingAngle;
    private bool _hooksSetup;

    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _loadingTimer;
    private readonly List<IDisposable> _propertySubscriptions = [];
    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _waveformCollectionHandler;
    private System.Collections.Specialized.INotifyCollectionChanged? _waveformCollectionRef;

    public CompositionWaveformProgressBar()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Focusable = true;

        _propertySubscriptions.Add(this.GetObservable(WaveformProperty).Subscribe(new SimpleObserver<IList<float>?>(OnWaveformChanged)));
        _propertySubscriptions.Add(this.GetObservable(PlayedColorProperty).Subscribe(new SimpleObserver<IBrush?>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(UnPlayedColorProperty).Subscribe(new SimpleObserver<IBrush?>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(WaveformMarginProperty).Subscribe(new SimpleObserver<Thickness>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(BarGapProperty).Subscribe(new SimpleObserver<double>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(BlockHeightProperty).Subscribe(new SimpleObserver<double>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(VerticalGapProperty).Subscribe(new SimpleObserver<double>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(VisualBarCountProperty).Subscribe(new SimpleObserver<int>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(BarGradientProperty).Subscribe(new SimpleObserver<LinearGradientBrush?>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(ShowReflectionProperty).Subscribe(new SimpleObserver<bool>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(IsSymmetricProperty).Subscribe(new SimpleObserver<bool>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(IsDigitalModeProperty).Subscribe(new SimpleObserver<bool>(_ => SendVisualConfiguration())));
        _propertySubscriptions.Add(this.GetObservable(WaveformVerticalOffsetProperty).Subscribe(new SimpleObserver<double>(_ => SendVisualConfiguration())));

        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        _loadingTimer.Tick += LoadingTimer_Tick;

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        _updateTimer.Tick += UpdateTimer_Tick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor != null)
        {
            _visual = compositor.CreateCustomVisual(new CompositionWaveformProgressBarVisualHandler());
            ElementComposition.SetElementChildVisual(this, _visual);
            UpdateVisualLayout();
            SendVisualConfiguration();
            SendProgressToVisual(NormalizeValue(Value), IsDragging);
        }

        _loadingTimer.Start();
        _updateTimer.Start();
        SetupGlobalHitTest();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _loadingTimer.Stop();
        _updateTimer.Stop();
        TeardownGlobalHitTest();
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual == null)
            return;

        UpdateVisualLayout();
        _visual.SendHandlerMessage(new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
    }

    private void UpdateVisualLayout()
    {
        if (_visual == null)
            return;

        float width = (float)Bounds.Width;
        float height = (float)Bounds.Height;
        float topExtension = ComputeTopExtension(IsTriangleUpwards, (float)TriangleOffset, (float)TriangleHeight);

        _visual.ClipToBounds = false;
        _visual.Size = new Vector2(width, height + topExtension);
        _visual.Offset = new Vector3(0, -topExtension, 0);
    }

    private static float ComputeTopExtension(bool isTriangleUpwards, float triangleOffset, float triangleHeight)
    {
        if (isTriangleUpwards)
            return 0f;

        float topY = Math.Min(triangleOffset, triangleOffset + triangleHeight);
        return Math.Max(0f, -topY);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_visual == null)
            return;

        if (change.Property == ValueProperty)
        {
            if (IsDragging)
                return;

            SendProgressToVisual(NormalizeValue(change.GetNewValue<double>()), false);
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty)
        {
            _visual.SendHandlerMessage(new WaveformRangeMessage(Minimum, Maximum));
            if (!IsDragging)
                SendProgressToVisual(NormalizeValue(Value), false);
        }
        else if (change.Property == IndicatorColorProperty
                 || change.Property == TextForegroundColorProperty
                 || change.Property == TextSizeProperty
                 || change.Property == LoadingIndicatorColorProperty
                 || change.Property == LoadingIndicatorSizeProperty
                 || change.Property == TriangleColorProperty
                 || change.Property == TriangleOffsetProperty
                 || change.Property == TriangleWidthProperty
                 || change.Property == TriangleHeightProperty
                 || change.Property == IsTriangleUpwardsProperty
                 || change.Property == IsDigitalModeProperty
                 || change.Property == BarGradientProperty
                 || change.Property == IsLoadingProperty
                 || change.Property == BorderBrushProperty
                 || change.Property == BorderThicknessProperty
                 || change.Property == BackgroundProperty)
        {
            SendVisualConfiguration();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        if (IsDragging)
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        IsDragging = true;
        _dragValue = Value;
        e.Pointer.Capture(this);
        UpdateValueFromPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _visual?.SendHandlerMessage(new WaveformHoverMessage((float)pos.X, (float)pos.Y));

        if (IsDragging)
            UpdateValueFromPointer(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging)
            return;

        UpdateValueFromPointer(e);
        Value = _dragValue;
        _lastValue = _dragValue;
        DragCompletedCommand?.Execute(_dragValue);
        IsDragging = false;
        e.Pointer.Capture(null);
        SendProgressToVisual(NormalizeValue(_dragValue), false);
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        var pos = e.GetPosition(this);
        _visual?.SendHandlerMessage(new WaveformHoverMessage((float)pos.X, (float)pos.Y));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _visual?.SendHandlerMessage(new WaveformHoverMessage(null, null));
    }

    private void OnWaveformChanged(IList<float>? list)
    {
        if (_waveformCollectionRef != null && _waveformCollectionHandler != null)
            _waveformCollectionRef.CollectionChanged -= _waveformCollectionHandler;

        _waveformCollectionRef = list as System.Collections.Specialized.INotifyCollectionChanged;
        if (_waveformCollectionRef != null)
        {
            _waveformCollectionHandler = (_, _) => SendWaveformData();
            _waveformCollectionRef.CollectionChanged += _waveformCollectionHandler;
        }

        SendWaveformData();
    }

    private void SendWaveformData()
    {
        if (_visual == null)
            return;

        float[] samples = Waveform == null ? [] : Waveform.ToArray();
        _visual.SendHandlerMessage(new WaveformDataMessage(samples));
    }

    private void SendVisualConfiguration()
    {
        if (_visual == null)
            return;

        var margin = WaveformMargin;
        UpdateVisualLayout();
        _visual.SendHandlerMessage(new Vector2((float)Bounds.Width, (float)Bounds.Height));
        _visual.SendHandlerMessage(new WaveformColorsMessage(
            ConvertBrushToSkColor(PlayedColor, SKColor.Parse("#4169E1")),
            ConvertBrushToSkColor(UnPlayedColor, SKColor.Parse("#696969")),
            ConvertBrushToSkColor(IndicatorColor, SKColors.White),
            ConvertBrushToSkColor(TriangleColor, SKColors.White),
            ConvertBrushToSkColor(TextForegroundColor, SKColors.White),
            ConvertBrushToSkColor(LoadingIndicatorColor, SKColors.White),
            ConvertBrushToSkColor(BorderBrush, SKColors.Gray),
            ConvertBrushToSkColor(Background, SKColors.Transparent)));
        _visual.SendHandlerMessage(new WaveformLayoutMessage(
            (float)WaveformVerticalOffset,
            (float)BarGap,
            (float)BlockHeight,
            (float)VerticalGap,
            VisualBarCount,
            (float)margin.Left,
            (float)margin.Right,
            (float)margin.Top,
            (float)margin.Bottom));
        _visual.SendHandlerMessage(new WaveformStyleMessage(
            IsSymmetric,
            ShowReflection,
            IsTriangleUpwards,
            IsDigitalMode,
            (float)TriangleOffset,
            (float)TriangleWidth,
            (float)TriangleHeight,
            (float)TextSize,
            (float)LoadingIndicatorSize,
            (float)BorderThickness.Left));
        _visual.SendHandlerMessage(ConvertGradient(BarGradient));
        _visual.SendHandlerMessage(new WaveformRangeMessage(Minimum, Maximum));
        _visual.SendHandlerMessage(new WaveformLoadingMessage(IsLoading, _loadingAngle));
        SendWaveformData();
    }

    private void SendProgressToVisual(double progress, bool isDragging)
    {
        _visual?.SendHandlerMessage(new WaveformProgressMessage(progress, isDragging));
    }

    private void UpdateValueFromPointer(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        var margin = WaveformMargin;
        double width = Bounds.Width - margin.Left - margin.Right;
        double ratio = Math.Clamp((point.X - margin.Left) / Math.Max(1.0, width), 0, 1);
        double newVal = Minimum + ratio * (Maximum - Minimum);

        if (IsDragging)
        {
            _dragValue = newVal;
            _lastValue = _dragValue;
        }
        else
        {
            Value = newVal;
            _lastValue = Value;
        }

        SendProgressToVisual(NormalizeValue(IsDragging ? _dragValue : Value), IsDragging);
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsDragging && Math.Abs(Value - _lastValue) > 0.0001)
        {
            _lastValue = Value;
            SendProgressToVisual(NormalizeValue(Value), false);
        }
    }

    private void LoadingTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsLoading)
            return;

        _loadingAngle = (_loadingAngle + 5) % 360;
        _visual?.SendHandlerMessage(new WaveformLoadingMessage(true, _loadingAngle));
    }

    private double NormalizeValue(double val)
    {
        double min = Minimum;
        double max = Maximum;
        if (max <= min)
            return 0.0;

        return Math.Clamp((val - min) / (max - min), 0.0, 1.0);
    }

    private void SetupGlobalHitTest()
    {
        if (_hooksSetup)
            return;

        _hooksSetup = true;
        if (TopLevel.GetTopLevel(this) is InputElement ie)
            ie.AddHandler(InputElement.PointerPressedEvent, GlobalPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void TeardownGlobalHitTest()
    {
        if (TopLevel.GetTopLevel(this) is InputElement ie)
            ie.RemoveHandler(InputElement.PointerPressedEvent, GlobalPointerPressed);

        _hooksSetup = false;
    }

    private void GlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEffectivelyVisible)
            return;

        if (e.Source == this)
            return;

        if (e.Source is Visual sourceVisual)
        {
            if (IsBlockingOverlay(sourceVisual))
                return;

            if (TopLevel.GetTopLevel(this) is Visual root)
            {
                var hit = root.GetVisualAt(e.GetPosition(root));
                if (hit != null)
                {
                    if (IsBlockingOverlay(hit))
                        return;
                    if (IsVisualDescendant(hit, this))
                        return;
                }
            }
        }

        var pt = e.GetPosition(this);
        double buffer = TriangleHeight + TriangleOffset + 5;
        if (pt.X >= -buffer && pt.X <= Bounds.Width + buffer && pt.Y >= -buffer && pt.Y <= Bounds.Height + buffer)
        {
            HandlePointerPressed(e);
            e.Handled = true;
        }
    }

    private static bool IsVisualDescendant(Visual? visual, Visual ancestor)
    {
        if (visual == null)
            return false;

        return visual == ancestor || visual.GetVisualAncestors().Contains(ancestor);
    }

    private static bool IsBlockingOverlay(Visual visual)
    {
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is Border { Name: "SettingsOverlay" })
                return true;

            if (ancestor is Control { ZIndex: >= 2000 })
                return true;

            var typeName = ancestor.GetType().Name;
            if (typeName is "SettingsOverlay"
                or "MetadataOverlay"
                or "EmulationMetadataOverlay"
                or "PopupRoot"
                or "OverlayPopupHost")
                return true;
        }

        return false;
    }

    private static WaveformGradientMessage ConvertGradient(LinearGradientBrush? brush)
    {
        if (brush?.GradientStops == null || brush.GradientStops.Count == 0)
            return new WaveformGradientMessage(null, null);

        var stops = brush.GradientStops.OrderBy(s => s.Offset).ToList();
        var colors = stops.Select(s =>
        {
            var c = s.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }).ToArray();
        var offsets = stops.Select(s => (float)s.Offset).ToArray();
        return new WaveformGradientMessage(colors, offsets);
    }

    private static SKColor ConvertBrushToSkColor(IBrush? brush, SKColor fallback)
    {
        if (brush is not ISolidColorBrush solidColorBrush)
            return fallback;

        try
        {
            var c = solidColorBrush.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}
