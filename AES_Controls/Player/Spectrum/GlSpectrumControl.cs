using System.Collections.Specialized;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Player.Spectrum;

/// <summary>
/// Composition-based spectrum visualiser control. Retains the public surface and
/// smoothing behaviour of the original GL implementation while rendering through
/// Avalonia's composition API.
/// </summary>
public sealed class GlSpectrumControl : Control, IDisposable
{
    private const double MinAdaptiveFrameIntervalMs = 1000.0 / 120.0;
    private const double MaxAdaptiveFrameIntervalMs = 1000.0 / 60.0;
    private const double AdaptiveIntervalToleranceMs = 0.25;
    private const double SpectrumDensityFloor = 0.72;
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
    private double[] _displayedBarLevels = [];
    private double[] _peakLevels = [];
    private double[] _rawSmoothed = [];
    private float[] _spectrumSnapshot = [];
    private int[] _sampleMap = [];
    private readonly SKColor[] _gradientColors =
    [
        new SKColor(0x00, 0xCC, 0xFF),
        new SKColor(0x33, 0x33, 0xFF),
        new SKColor(0xCC, 0x00, 0xCC),
        new SKColor(0xFF, 0x00, 0x4D),
        new SKColor(0xFF, 0xB3, 0x00)
    ];

    private double _globalMax = 1e-6;
    private bool _isFirstFrame = true;
    private bool _gradientDirty = true;
    private bool _isAnimating;
    private int _spectrumCount;
    private int _sampleMapTargetCount = -1;
    private int _sampleMapSourceCount = -1;
    private DispatcherTimer? _renderTimer;
    private double _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs;
    private double _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5;
    private double _averageFrameDurationMs = MinAdaptiveFrameIntervalMs;
    private int _overBudgetFrames;
    private int _underBudgetFrames;
    private int _pendingRedraw;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastTicks;
    private INotifyCollectionChanged? _spectrumCollectionRef;
    private NotifyCollectionChangedEventHandler? _spectrumCollectionHandler;

    public GlSpectrumControl()
    {
        ClipToBounds = true;
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(MinAdaptiveFrameIntervalMs), DispatcherPriority.Render, OnRenderTimerTick);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new SpectrumCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _lastTicks = _stopwatch.Elapsed.TotalSeconds;
        ResetAdaptiveFramePacing();
        UpdateHandlerSize();
        UpdateHandlerOpacity((float)Opacity);
        RequestRedraw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _renderTimer?.Stop();
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
        UpdateHandlerSize();
        RequestRedraw();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SpectrumProperty)
        {
            OnSpectrumChanged(change.GetNewValue<AvaloniaList<double>?>());
            return;
        }

        if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>() && !IsRenderingPaused)
            {
                if (_isAnimating || Volatile.Read(ref _pendingRedraw) != 0)
                    _renderTimer?.Start();

                RequestRedraw();
            }
            else
            {
                _renderTimer?.Stop();
            }

            return;
        }

        if (change.Property == IsRenderingPausedProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _renderTimer?.Stop();
            }
            else
            {
                _lastTicks = _stopwatch.Elapsed.TotalSeconds;
                ResetAdaptiveFramePacing();
                RequestRedraw();
            }

            return;
        }

        if (change.Property == BarGradientProperty)
            _gradientDirty = true;

        if (change.Property == OpacityProperty)
        {
            UpdateHandlerOpacity((float)change.GetNewValue<double>());
            return;
        }

        if (IsRenderAffectingProperty(change.Property))
            RequestRedraw();
    }

    private static bool IsRenderAffectingProperty(AvaloniaProperty property)
    {
        return property == BarGradientProperty
            || property == UseDeltaTimeProperty
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

    private void OnSpectrumChanged(AvaloniaList<double>? col)
    {
        try
        {
            if (_spectrumCollectionRef != null && _spectrumCollectionHandler != null)
                _spectrumCollectionRef.CollectionChanged -= _spectrumCollectionHandler;
        }
        catch
        {
        }

        _spectrumCollectionRef = null;
        _spectrumCollectionHandler = null;

        if (col is INotifyCollectionChanged notify)
        {
            _spectrumCollectionRef = notify;
            _spectrumCollectionHandler = (_, _) => RequestRedraw();
            notify.CollectionChanged += _spectrumCollectionHandler;
        }

        _isFirstFrame = true;
        _isAnimating = true;
        RequestRedraw();
    }

    private void OnRenderTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || IsRenderingPaused || _visual == null || (Volatile.Read(ref _pendingRedraw) == 0 && !_isAnimating))
            return;

        Interlocked.Exchange(ref _pendingRedraw, 0);
        RenderFrame();
    }

    private void RequestRedraw()
    {
        if (IsRenderingPaused)
            return;

        _isAnimating = true;
        Interlocked.Exchange(ref _pendingRedraw, 1);

        if (IsVisible && _visual != null)
            _renderTimer?.Start();
    }

    private void RenderFrame()
    {
        if (_visual == null)
            return;

        float logicalWidth = (float)Bounds.Width;
        float logicalHeight = (float)Bounds.Height;
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return;

        double renderStartMs = _stopwatch.Elapsed.TotalMilliseconds;
        double currentTicks = _stopwatch.Elapsed.TotalSeconds;
        float delta = (float)(currentTicks - _lastTicks);
        if (delta <= 0)
            delta = 1f / 120f;

        _lastTicks = currentTicks;

        int targetCount = GetTargetBarCount(logicalWidth);
        float scaling = (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

        SnapshotSpectrum();
        _isAnimating = UpdatePhysics(targetCount, delta);
        UpdateGradientColors();

        double denom = (_globalMax * 1.1) + 1e-9;
        float[] barLevels = GC.AllocateUninitializedArray<float>(targetCount);
        float[] peakLevels = GC.AllocateUninitializedArray<float>(targetCount);
        for (int i = 0; i < targetCount; i++)
        {
            barLevels[i] = (float)Math.Clamp(_displayedBarLevels[i] / denom, 0.0, 1.0);
            peakLevels[i] = (float)Math.Clamp(_peakLevels[i] / denom, 0.0, 1.0);
        }

        _visual.SendHandlerMessage(new SpectrumFrameMessage(
            barLevels,
            peakLevels,
            (SKColor[])_gradientColors.Clone(),
            (float)BarWidth,
            (float)BarSpacing,
            (float)BlockHeight,
            DefaultPeakThicknessPixels / Math.Max(1f, scaling)));

        UpdateAdaptiveFramePacing(delta, _stopwatch.Elapsed.TotalMilliseconds - renderStartMs);

        if (!_isAnimating && Volatile.Read(ref _pendingRedraw) == 0)
            _renderTimer?.Stop();
    }

    private void UpdateHandlerSize()
    {
        if (_visual == null)
            return;

        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }

    private void UpdateHandlerOpacity(float opacity)
    {
        _visual?.SendHandlerMessage(new SpectrumOpacityMessage(Math.Clamp(opacity, 0f, 1f)));
    }

    private void SnapshotSpectrum()
    {
        var spectrum = Spectrum;
        if (spectrum == null)
        {
            _spectrumCount = 0;
            return;
        }

        lock (spectrum)
        {
            int count = spectrum.Count;
            if (_spectrumSnapshot.Length < count)
                _spectrumSnapshot = new float[count];

            for (int i = 0; i < count; i++)
                _spectrumSnapshot[i] = (float)spectrum[i];

            _spectrumCount = count;
        }
    }

    private bool UpdatePhysics(int targetCount, float delta)
    {
        if (_displayedBarLevels.Length != targetCount)
        {
            _displayedBarLevels = new double[targetCount];
            _peakLevels = new double[targetCount];
            _rawSmoothed = new double[targetCount];
            _sampleMap = new int[targetCount];
            _sampleMapTargetCount = -1;
            _isFirstFrame = true;
        }

        if (_sampleMapTargetCount != targetCount || _sampleMapSourceCount != _spectrumCount)
        {
            if (_sampleMap.Length < targetCount)
                _sampleMap = new int[targetCount];

            int srcCount = _spectrumCount;
            for (int i = 0; i < targetCount; i++)
            {
                _sampleMap[i] = srcCount > 0
                    ? Math.Min(srcCount - 1, (int)((i / (double)targetCount) * srcCount))
                    : 0;
            }

            _sampleMapTargetCount = targetCount;
            _sampleMapSourceCount = srcCount;
        }

        float timeFactor = UseDeltaTime ? delta * 60f : 1.0f;
        double adjAttack = 1.0 - Math.Pow(1.0 - AttackLerp, timeFactor);
        double adjRelease = 1.0 - Math.Pow(1.0 - ReleaseLerp, timeFactor);
        double adjPeakDecay = 1.0 - Math.Pow(1.0 - PeakDecay, timeFactor);

        double observedMax = 0.0;
        bool hasVisibleActivity = false;

        for (int i = 0; i < targetCount; i++)
        {
            double src = _spectrumCount > 0 ? _spectrumSnapshot[_sampleMap[i]] : 0.0;

            if (double.IsNaN(src) || double.IsInfinity(src) || src < 0.0001)
                src = 0.0;

            _rawSmoothed[i] += (src - _rawSmoothed[i]) * Math.Min(1.0, PrePowAttackAlpha * timeFactor);
            double target = _rawSmoothed[i];

            if (_isFirstFrame && target > 0)
            {
                _displayedBarLevels[i] = target;
                _peakLevels[i] = target;
            }
            else
            {
                double effectiveTarget = target > _displayedBarLevels[i]
                    ? Math.Min(target, _displayedBarLevels[i] + Math.Max(target * MaxRiseFraction, MaxRiseAbsolute) * timeFactor)
                    : target;

                double lerp = effectiveTarget < _displayedBarLevels[i] ? adjRelease : adjAttack;
                _displayedBarLevels[i] += (effectiveTarget - _displayedBarLevels[i]) * lerp;

                if (_displayedBarLevels[i] > _peakLevels[i])
                    _peakLevels[i] = _displayedBarLevels[i];
                else
                    _peakLevels[i] += (_displayedBarLevels[i] - _peakLevels[i]) * adjPeakDecay;
            }

            if (_displayedBarLevels[i] > observedMax)
                observedMax = _displayedBarLevels[i];

            if (_displayedBarLevels[i] > 0.001 || _peakLevels[i] > 0.001 || _rawSmoothed[i] > 0.001)
                hasVisibleActivity = true;
        }

        if (observedMax > 0.001)
        {
            if (_isFirstFrame)
            {
                _globalMax = observedMax;
                _isFirstFrame = false;
            }
            else
            {
                double lerpSpeed = observedMax > _globalMax ? 0.15 : 0.01;
                _globalMax += (observedMax - _globalMax) * Math.Min(1.0, lerpSpeed * timeFactor);
            }
        }

        if (_globalMax < 0.05)
            _globalMax = 0.05;

        return hasVisibleActivity || observedMax > 0.001 || _globalMax > 0.051;
    }

    private int GetTargetBarCount(float logicalWidth)
    {
        double step = Math.Max(1.0, BarWidth + BarSpacing);
        int rawCount = Math.Max(1, (int)(logicalWidth / step));

        double normalizedLoad = Math.Clamp(
            (MaxAdaptiveFrameIntervalMs - _targetFrameIntervalMs) / (MaxAdaptiveFrameIntervalMs - MinAdaptiveFrameIntervalMs),
            0.0,
            1.0);
        double densityScale = SpectrumDensityFloor + ((1.0 - SpectrumDensityFloor) * normalizedLoad);

        if (OperatingSystem.IsMacOS())
            densityScale = Math.Min(densityScale, 0.88);

        return Math.Max(1, (int)Math.Round(rawCount * densityScale));
    }

    private void UpdateGradientColors()
    {
        if (!_gradientDirty)
            return;

        var stops = BarGradient?.GradientStops;
        if (stops == null || stops.Count == 0)
        {
            for (int i = 0; i < _gradientColors.Length; i++)
                _gradientColors[i] = new SKColor(0x00, 0xCC, 0xFF);

            _gradientDirty = false;
            return;
        }

        for (int i = 0; i < _gradientColors.Length; i++)
        {
            var color = GetColorAtOffset(stops, i / 4.0f);
            _gradientColors[i] = new SKColor(color.R, color.G, color.B, color.A);
        }

        _gradientDirty = false;
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

        if (leftOffset == rightOffset)
            return left.Color;

        float t = (offset - (float)left.Offset) / (float)(right.Offset - left.Offset);
        return Color.FromArgb(
            (byte)(left.Color.A + ((right.Color.A - left.Color.A) * t)),
            (byte)(left.Color.R + ((right.Color.R - left.Color.R) * t)),
            (byte)(left.Color.G + ((right.Color.G - left.Color.G) * t)),
            (byte)(left.Color.B + ((right.Color.B - left.Color.B) * t)));
    }

    private void ResetAdaptiveFramePacing()
    {
        _targetFrameIntervalMs = MinAdaptiveFrameIntervalMs;
        _averageRenderDurationMs = MinAdaptiveFrameIntervalMs * 0.5;
        _averageFrameDurationMs = MinAdaptiveFrameIntervalMs;
        _overBudgetFrames = 0;
        _underBudgetFrames = 0;
        ApplyRenderTimerInterval();
    }

    private void UpdateAdaptiveFramePacing(float delta, double renderDurationMs)
    {
        double frameDurationMs = Math.Clamp(delta * 1000.0, MinAdaptiveFrameIntervalMs, MaxAdaptiveFrameIntervalMs * 2.0);
        _averageFrameDurationMs += (_averageFrameDurationMs - frameDurationMs) * -0.12;
        _averageRenderDurationMs += (_averageRenderDurationMs - renderDurationMs) * -0.18;

        double budgetMs = _targetFrameIntervalMs;
        bool overBudget = _averageRenderDurationMs > budgetMs * 0.82 || renderDurationMs > budgetMs * 0.95;
        bool underBudget = _averageRenderDurationMs < budgetMs * 0.45 && _averageFrameDurationMs <= budgetMs * 1.15;

        if (overBudget)
        {
            _overBudgetFrames++;
            _underBudgetFrames = 0;
            if (_overBudgetFrames >= 3)
            {
                _targetFrameIntervalMs = Math.Min(MaxAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 1.15) + 0.5);
                _overBudgetFrames = 0;
                ApplyRenderTimerInterval();
            }

            return;
        }

        _overBudgetFrames = 0;

        if (underBudget)
        {
            _underBudgetFrames++;
            if (_underBudgetFrames >= 18)
            {
                _targetFrameIntervalMs = Math.Max(MinAdaptiveFrameIntervalMs, (_targetFrameIntervalMs * 0.9) - 0.25);
                _underBudgetFrames = 0;
                ApplyRenderTimerInterval();
            }
        }
        else if (_underBudgetFrames > 0)
        {
            _underBudgetFrames--;
        }
    }

    private void ApplyRenderTimerInterval()
    {
        if (_renderTimer == null)
            return;

        if (Math.Abs(_renderTimer.Interval.TotalMilliseconds - _targetFrameIntervalMs) > AdaptiveIntervalToleranceMs)
            _renderTimer.Interval = TimeSpan.FromMilliseconds(_targetFrameIntervalMs);
    }

    public void Dispose()
    {
    }
}
