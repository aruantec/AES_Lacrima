using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// A custom control that renders and manages an equalizer visualization using the
/// Avalonia composition API. The control forwards visual updates to a
/// composition visual handler and exposes a set of equalizer bands that can be
/// manipulated by pointer input.
/// </summary>
public class EqualizerCompositionControl : Control
{
    private const float MinGain = -10f;
    private const float MaxGain = 10f;

    private const float LeftPadding = 20f;
    private const float RightPadding = 20f;
    private const float TopPadding = 14f;
    private const float BottomPadding = 26f;

    private CompositionCustomVisual? _visual;
    private AvaloniaList<BandModel>? _subscribedBands;
    private readonly HashSet<INotifyPropertyChanged> _subscribedItems = new();
    private DispatcherTimer? _sendBandsTimer;
    private bool _isPointerDown;
    private int _activeBandIndex = -1;
    private bool _bandsDirty;

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, IBrush?>(nameof(Background), Brushes.Transparent);

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public static readonly StyledProperty<AvaloniaList<BandModel>?> BandsProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, AvaloniaList<BandModel>?>(nameof(Bands));

    public AvaloniaList<BandModel>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public static readonly StyledProperty<double> TextFontSizeProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, double>(nameof(TextFontSize), 12.0);

    public double TextFontSize
    {
        get => GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public static readonly StyledProperty<IBrush?> TextForegroundProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, IBrush?>(nameof(TextForeground), Brushes.White);

    public IBrush? TextForeground
    {
        get => GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }
    public static readonly StyledProperty<Thickness> LabelMarginProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, Thickness>(nameof(LabelMargin), new Thickness(8,0,0,0));

    public Thickness LabelMargin
    {
        get => GetValue(LabelMarginProperty);
        set => SetValue(LabelMarginProperty, value);
    }

    public static readonly StyledProperty<double> LabelGapProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, double>(nameof(LabelGap), 40.0);

    public double LabelGap
    {
        get => GetValue(LabelGapProperty);
        set => SetValue(LabelGapProperty, value);
    }
    public static readonly StyledProperty<double> TextMarginProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, double>(nameof(TextMargin), 6.0);

    public double TextMargin
    {
        get => GetValue(TextMarginProperty);
        set => SetValue(TextMarginProperty, value);
    }

    public static readonly StyledProperty<double> RenderMarginProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, double>(nameof(RenderMargin));

    /// <summary>
    /// Margin (in pixels) between the control bounds and the rendered content.
    /// The visual handler will translate the canvas by this margin and reduce
    /// the available rendering area accordingly.
    /// </summary>
    public double RenderMargin
    {
        get => GetValue(RenderMarginProperty);
        set => SetValue(RenderMarginProperty, value);
    }

    public EqualizerCompositionControl()
    {
        ClipToBounds = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EqualizerCompositionControl"/> class.
    /// </summary>

    public static readonly StyledProperty<bool> IsFadedInProperty =
        AvaloniaProperty.Register<EqualizerCompositionControl, bool>(nameof(IsFadedIn), true);

    public bool IsFadedIn
    {
        get => GetValue(IsFadedInProperty);
        set => SetValue(IsFadedInProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Background != null)
            context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        base.Render(context);
    }

    /// <summary>
    /// Renders the control background and allows the base control to draw its
    /// contents. The composition visual is used for the main equalizer rendering,
    /// but this method ensures the configured <see cref="Background"/> is drawn.
    /// </summary>

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null) return;

        _visual = compositor.CreateCustomVisual(new EqualizerCompositionVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateHandlerSize();
        UpdateBackground();
        UpdateBands();
        // Start with fade according to property
        Opacity = IsFadedIn ? 1.0 : 0.0;
    }

    /// <summary>
    /// Called when the control is attached to the visual tree. Creates and
    /// initializes the composition visual handler and synchronizes visual state
    /// such as background, bands and size.
    /// </summary>

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // no manual fade timer to clean up
        UnsubscribeBands();
        _sendBandsTimer?.Stop();
        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }
    }

    /// <summary>
    /// Handles property changes for the control and forwards relevant
    /// information to the composition visual handler. This includes changes to
    /// opacity, size, bands and styling properties.
    /// </summary>

    private void OnIsFadedInChanged(bool isIn)
    {
        Opacity = isIn ? 1.0 : 0.0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFadedInProperty)
        {
            OnIsFadedInChanged(change.GetNewValue<bool>());
            return;
        }
        if (change.Property == BandsProperty)
        {
            UpdateBands();
        }
        else if (change.Property == BackgroundProperty)
        {
            UpdateBackground();
        }
        else if (change.Property == TextFontSizeProperty || change.Property == TextForegroundProperty)
        {
            UpdateBackground();
        }
        else if (change.Property == OpacityProperty)
        {
            if (_visual != null)
                _visual.SendHandlerMessage(new EqualizerGlobalOpacityMessage((float)change.GetNewValue<double>()));
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateHandlerSize();
        }
    }

    private void Bands_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            UpdateBands();
            return;
        }

        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<INotifyPropertyChanged>())
            {
                if (_subscribedItems.Remove(item))
                    item.PropertyChanged -= Band_PropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>())
            {
                if (_subscribedItems.Add(item))
                    item.PropertyChanged += Band_PropertyChanged;
            }
        }

        SendBands();
    }

    private void Band_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BandModel.Gain) or nameof(BandModel.Frequency) or null)
            SendBands();
    }

    private void UpdateHandlerSize()
    {
        if (_visual == null) return;
        var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        _visual.Size = size;
        _visual.SendHandlerMessage(size);
    }

    private void UpdateBackground()
    {
        if (_visual == null) return;
        _visual.SendHandlerMessage(new EqualizerBackgroundMessage(GetSkColor(Background)));
        // Send text color and size when background updates to ensure visual sync
        _visual.SendHandlerMessage(new EqualizerTextStyleMessage(GetSkColor(TextForeground), (float)TextFontSize, (float)TextMargin));
        // Send render margin as part of background update
        _visual.SendHandlerMessage(new EqualizerRenderMarginMessage((float)RenderMargin));
        // Send label margin (Thickness) so visual can position labels exactly
        var lm = LabelMargin;
        _visual.SendHandlerMessage(new EqualizerLabelMarginMessage((float)lm.Left, (float)lm.Top, (float)lm.Bottom));
        // Send explicit LabelGap as an additive gap value from the left LTR corner into the content area
        _visual.SendHandlerMessage(new EqualizerLabelGapMessage((float)LabelGap));
    }

    private void UpdateBands()
    {
        if (!ReferenceEquals(_subscribedBands, Bands))
        {
            UnsubscribeBands();
            _subscribedBands = Bands;
            if (_subscribedBands != null)
            {
                _subscribedBands.CollectionChanged += Bands_CollectionChanged;
                foreach (var band in _subscribedBands)
                {
                    if (band is INotifyPropertyChanged inpc && _subscribedItems.Add(inpc))
                        inpc.PropertyChanged += Band_PropertyChanged;
                }
            }
        }

        SendBands();
    }

    private void SendBands()
    {
        if (_visual == null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SendBands);
            return;
        }
        _bandsDirty = true;
        if (_sendBandsTimer == null)
        {
            _sendBandsTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _sendBandsTimer.Tick += SendBandsTimer_Tick;
        }
        if (!_sendBandsTimer.IsEnabled)
            _sendBandsTimer.Start();
    }

    private void SendBandsTimer_Tick(object? sender, EventArgs e)
    {
        if (_sendBandsTimer == null) return;
        if (!_bandsDirty)
        {
            _sendBandsTimer.Stop();
            return;
        }

        _bandsDirty = false;
        if (_visual == null) return;
        var bands = _subscribedBands?.Select(b => new EqualizerBand(b.Gain, b.Frequency)).ToList()
                   ?? new List<EqualizerBand>();
        _visual.SendHandlerMessage(new EqualizerBandsMessage(bands));
    }

    private void UnsubscribeBands()
    {
        if (_subscribedBands != null)
            _subscribedBands.CollectionChanged -= Bands_CollectionChanged;
        foreach (var item in _subscribedItems)
            item.PropertyChanged -= Band_PropertyChanged;
        _subscribedItems.Clear();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_subscribedBands == null || _subscribedBands.Count == 0) return;

        var point = e.GetPosition(this);
        if (TryUpdateGain(point, out var index))
        {
            _isPointerDown = true;
            _activeBandIndex = index;
            e.Pointer.Capture(this);
            // Notify visual about active band start (glow)
            _visual?.SendHandlerMessage(new EqualizerActiveBandMessage(_activeBandIndex, true));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPointerDown) return;
        var point = e.GetPosition(this);
        if (TryUpdateGain(point, out var idx))
        {
            _activeBandIndex = idx;
            // Update visual with currently active index while dragging
            _visual?.SendHandlerMessage(new EqualizerActiveBandMessage(_activeBandIndex, true));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPointerDown)
        {
            _isPointerDown = false;
            _activeBandIndex = -1;
            e.Pointer.Capture(null);
            // Notify visual that dragging stopped
            _visual?.SendHandlerMessage(new EqualizerActiveBandMessage(-1, false));
        }
    }

    private bool TryUpdateGain(Point point, out int index)
    {
        index = -1;
        if (_subscribedBands == null || _subscribedBands.Count == 0) return false;

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0) return false;

        int count = _subscribedBands.Count;
        // Mirror the visual handler layout so pointer X maps to same slider centers.
        float leftPad = LeftPadding + (float)RenderMargin;
        float rightPad = RightPadding + (float)RenderMargin;
        float topPad = TopPadding + (float)RenderMargin;
        float bottomPad = BottomPadding + (float)RenderMargin;

        // Estimate left label block width using font size and character count (approximate)
        float approxCharWidth = (float)TextFontSize * 0.55f;
        float maxLeftLabelWidth = 0f;
        foreach (var unused in _subscribedBands)
        {
            var s = "10dB";
            var w = s.Length * approxCharWidth;
            if (w > maxLeftLabelWidth) maxLeftLabelWidth = w;
        }

        float labelBlockRightX = leftPad + (float)LabelMargin.Left + maxLeftLabelWidth;
        float sliderStartX = labelBlockRightX + (float)LabelGap;

        // Estimate bottom freq label sizes
        float maxFreqLabelWidth = 0f;
        foreach (var b in _subscribedBands)
        {
            var s = b.Frequency ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s)) continue;
            var w = s.Length * approxCharWidth;
            if (w > maxFreqLabelWidth) maxFreqLabelWidth = w;
        }
        float maxFreqHalf = maxFreqLabelWidth * 0.5f;

        float leftSlotStart = sliderStartX + maxFreqHalf;
        float rightSlotEnd = (float)width - rightPad - maxFreqHalf;
        float availableWidth = Math.Max(1f, rightSlotEnd - leftSlotStart);
        float step = count > 1 ? availableWidth / (count - 1) : 0f;

        // clamp targetX into usable slider area
        float targetX = (float)Math.Clamp(point.X, leftSlotStart, rightSlotEnd);
        int nearest = 0;
        if (count > 1)
            nearest = (int)Math.Clamp(Math.Round((targetX - leftSlotStart) / step), 0, count - 1);

        // Vertical track bounds should match the visual handler's computation
        float freqTextHeight = (float)TextFontSize;
        float effectiveBottomPadding = bottomPad + freqTextHeight + (float)TextMargin;
        float trackTop = topPad + (float)LabelMargin.Top;
        float trackBottom = (float)Math.Max(trackTop + 10f, height - effectiveBottomPadding - (float)LabelMargin.Bottom);

        float clampedY = (float)Math.Clamp(point.Y, trackTop, trackBottom);
        float t = (trackBottom - clampedY) / (trackBottom - trackTop);
        float gain = MinGain + t * (MaxGain - MinGain);

        index = nearest;
        var band = _subscribedBands[nearest];
        if (Math.Abs(band.Gain - gain) > 0.001f)
            band.Gain = gain;

        return true;
    }

    private SKColor GetSkColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush scb)
            return new SKColor(scb.Color.R, scb.Color.G, scb.Color.B, scb.Color.A);
        return SKColors.Transparent;
    }
}