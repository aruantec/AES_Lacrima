using System.Buffers;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using log4net;
using SkiaSharp;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;

namespace AES_Controls.Composition;

/// <summary>
/// A composition-based scrollable card grid for cover art with titles.
/// Supports smooth inertial scrolling, a custom vertical scrollbar, and the same
/// data bindings used by <see cref="CompositionCarouselControl"/>.
/// </summary>
public class CompositionCardGridControl : ItemsControl, IScaleExclusionRenderTarget
{
    private sealed class SharedImageEntry
    {
        public SharedImageEntry(SKImage image) => Image = image;
        public SKImage Image { get; }
        public int RefCount { get; set; } = 1;
    }

    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<CompositionCardGridControl>();
    private const int CachedCardImageSize = 384;
    private const int AnimationHeartbeatMs = 16;
    private const int ActiveScrollVirtualizationDebounceMs = 48;
    private const int IdleVirtualizationDebounceMs = 12;
    private const int ParallelImageLoadCount = 12;
    private const double WheelScrollPixels = 165.0;
    private const float ScrollbarMargin = 10f;

    private CompositionCustomVisual? _visual;
    private List<SKImage?> _images = new();
    private readonly Dictionary<object, SKImage> _imageCache = new();
    private SKImage? _sharedPlaceholder;
    private readonly HashSet<INotifyPropertyChanged> _subscribedItems = new();
    private readonly LinkedList<object> _imageCacheLru = new();
    private readonly Dictionary<object, LinkedListNode<object>> _imageCacheNodes = new();
    private readonly Dictionary<object, int> _itemIndices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _itemImageSourceKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, SharedImageEntry> _sharedImageCache = new();
    private object?[] _itemsSnapshot = Array.Empty<object?>();
    private readonly string _diskCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"ImageCache_{CachedCardImageSize}");
    private volatile bool _isDiskCachePathReady;
    private int _maxImageCacheEntries = 160;
    private int _lastVirtualizationIndex = -1;
    private double _lastVirtualizationScrollY = double.NaN;
    private double _lastLoadScrollY = double.NaN;
    private bool _pendingVisibleLoad;
    private int _pendingScrollToIndex = -1;
    private bool _initialImageLoadScheduled;
    private double _knownScrollY;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _virtualizeDebounceTimer;
    private IEnumerable? _subscribedItemsSource;
    private readonly CardGridAnimationSyncState _animationSync = new();
    private readonly Dictionary<object, int> _pendingCoverImageReloads = new(ReferenceEqualityComparer.Instance);
    private Bitmap? _sectionPlaceholderBitmap;
    private DispatcherTimer? _uiSyncTimer;
    private DispatcherTimer? _wheelScrollSettleTimer;
    private bool _isWheelScrolling;

    private Point _startPoint;
    private Point _prevPoint;
    private ulong _prevTime;
    private double _velocityY;
    private bool _isPressed;
    private bool _isScrollbarPressed;
    private bool _isScrollbarHovered;
    private bool _isPointerScrolling;
    private bool _suppressSelectedIndexSideEffects;
    private double _targetScrollY;
    private double _scrollAtDragStart;
    private Rect _selectedItemBounds;

    public static readonly StyledProperty<double> SelectedIndexProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, double>(nameof(SelectedIndex));

    public static readonly StyledProperty<double> CardScaleProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, double>(nameof(CardScale), 1.0);

    public static readonly StyledProperty<double> CardSpacingProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, double>(nameof(CardSpacing), 16.0);

    public static readonly StyledProperty<double> TopPaddingProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, double>(nameof(TopPadding), 20.0);

    public static readonly StyledProperty<ICommand?> ItemSelectedCommandProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, ICommand?>(nameof(ItemSelectedCommand));

    public static readonly StyledProperty<ICommand?> ItemDoubleClickedCommandProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, ICommand?>(nameof(ItemDoubleClickedCommand));

    public static readonly StyledProperty<string?> ImageFileNamePropertyProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, string?>(nameof(ImageFileNameProperty));

    public static readonly StyledProperty<string?> ImageBitmapPropertyProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, string?>(nameof(ImageBitmapProperty));

    public static readonly StyledProperty<string?> TitlePropertyProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, string?>(nameof(TitleProperty), nameof(MediaItem.Title));

    public static readonly StyledProperty<double> GlobalOpacityProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, double>(nameof(GlobalOpacity), 1.0);

    public static readonly StyledProperty<int> ImageCacheSizeProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, int>(nameof(ImageCacheSize), 160);

    public static readonly StyledProperty<int> PointedItemIndexProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, int>(nameof(PointedItemIndex), -1);

    public static readonly StyledProperty<bool> ShowCoverFoundOverlayProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(ShowCoverFoundOverlay), true);

    public static readonly StyledProperty<bool> PublishSelectedItemBoundsProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(PublishSelectedItemBounds), false);

    public static readonly StyledProperty<bool> PauseLoadingSpinnerAnimationProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(PauseLoadingSpinnerAnimation), false);

    public static readonly DirectProperty<CompositionCardGridControl, Rect> SelectedItemBoundsProperty =
        AvaloniaProperty.RegisterDirect<CompositionCardGridControl, Rect>(
            nameof(SelectedItemBounds),
            o => o.SelectedItemBounds);

    public double SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double CardScale
    {
        get => GetValue(CardScaleProperty);
        set => SetValue(CardScaleProperty, value);
    }

    public double CardSpacing
    {
        get => GetValue(CardSpacingProperty);
        set => SetValue(CardSpacingProperty, value);
    }

    public double TopPadding
    {
        get => GetValue(TopPaddingProperty);
        set => SetValue(TopPaddingProperty, value);
    }

    public string? ImageFileNameProperty
    {
        get => GetValue(ImageFileNamePropertyProperty);
        set => SetValue(ImageFileNamePropertyProperty, value);
    }

    public string? ImageBitmapProperty
    {
        get => GetValue(ImageBitmapPropertyProperty);
        set => SetValue(ImageBitmapPropertyProperty, value);
    }

    public string? TitleProperty
    {
        get => GetValue(TitlePropertyProperty);
        set => SetValue(TitlePropertyProperty, value);
    }

    public double GlobalOpacity
    {
        get => GetValue(GlobalOpacityProperty);
        set => SetValue(GlobalOpacityProperty, value);
    }

    public int ImageCacheSize
    {
        get => GetValue(ImageCacheSizeProperty);
        set => SetValue(ImageCacheSizeProperty, value);
    }

    public int PointedItemIndex
    {
        get => GetValue(PointedItemIndexProperty);
        set => SetValue(PointedItemIndexProperty, value);
    }

    public bool ShowCoverFoundOverlay
    {
        get => GetValue(ShowCoverFoundOverlayProperty);
        set => SetValue(ShowCoverFoundOverlayProperty, value);
    }

    public bool PublishSelectedItemBounds
    {
        get => GetValue(PublishSelectedItemBoundsProperty);
        set => SetValue(PublishSelectedItemBoundsProperty, value);
    }

    public bool PauseLoadingSpinnerAnimation
    {
        get => GetValue(PauseLoadingSpinnerAnimationProperty);
        set => SetValue(PauseLoadingSpinnerAnimationProperty, value);
    }

    public ICommand? ItemSelectedCommand
    {
        get => GetValue(ItemSelectedCommandProperty);
        set => SetValue(ItemSelectedCommandProperty, value);
    }

    public ICommand? ItemDoubleClickedCommand
    {
        get => GetValue(ItemDoubleClickedCommandProperty);
        set => SetValue(ItemDoubleClickedCommandProperty, value);
    }

    public Rect SelectedItemBounds
    {
        get => _selectedItemBounds;
        private set => SetAndRaise(SelectedItemBoundsProperty, ref _selectedItemBounds, value);
    }

    public CompositionCardGridControl()
    {
        ScalableDecorator.SetExcludeFromScale(this, true);
        ScalableDecorator.SetExcludeFromScaleCompensation(this, false);
        Focusable = true;
        Background = new SolidColorBrush(Color.Parse("#101010"));
        GlobalOpacity = Opacity;
        ClipToBounds = true;

        _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(AnimationHeartbeatMs), DispatcherPriority.Render, (_, _) =>
        {
            if (PublishSelectedItemBounds)
                UpdateSelectedItemBounds();

            if (_images.Count == 0)
                return;

            if (_isWheelScrolling)
                return;

            bool trackingCompositorScroll = _isPointerScrolling || _isScrollbarPressed ||
                (_animationSync.IsAnimating && Math.Abs(_animationSync.VelocityY) > 0.01);
            if (!trackingCompositorScroll)
                return;

            double syncScrollY = _animationSync.CurrentScrollY;
            if (Math.Abs(syncScrollY - _knownScrollY) > 0.5)
            {
                _knownScrollY = syncScrollY;
                UpdateVirtualization();
            }
        });

        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingScrollToIndex >= 0 && Bounds.Width > 0 && Bounds.Height > 0)
        {
            int index = _pendingScrollToIndex;
            _pendingScrollToIndex = -1;
            ScrollToIndex(index, animate: false);
        }

        if (_pendingVisibleLoad)
            ScheduleInitialImageLoad();
    }

    public void RefreshExclusionRenderSize()
    {
        UpdateCompositionVisualSize(Bounds.Size);
        UpdateSelectedItemBounds();
    }

    public override void Render(DrawingContext context)
    {
        if (Background != null)
            context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        base.Render(context);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new CompositionCardGridVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.SendHandlerMessage(new CardGridAttachSyncMessage(_animationSync));
        SendLayoutMessages();
        UpdateCompositionVisualSize(Bounds.Size);
        if (_images.Count > 0)
            SyncVisualImageSlots();
        SendTitles();
        _visual.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Round(SelectedIndex)));
        _visual.SendHandlerMessage(new GlobalOpacityMessage(Opacity));
        _visual.SendHandlerMessage(new PauseLoadingSpinnerAnimationMessage(PauseLoadingSpinnerAnimation));
        if (ItemsSource != null)
            UpdateItems();

        _uiSyncTimer?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during detach", ex); }
        _uiSyncTimer?.Stop();
        ClearResources();
        SelectedItemBounds = default;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateCompositionVisualSize(e.NewSize);
        UpdateSelectedItemBounds();
        if (_pendingScrollToIndex >= 0 && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            int index = _pendingScrollToIndex;
            _pendingScrollToIndex = -1;
            ScrollToIndex(index, animate: false);
        }

        if (_pendingVisibleLoad)
            ScheduleInitialImageLoad();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedIndexProperty)
        {
            int idx = (int)Math.Round(change.GetNewValue<double>());
            _visual?.SendHandlerMessage(new CardGridSelectedIndexMessage(idx));
            if (!_suppressSelectedIndexSideEffects)
            {
                ScrollToIndex(idx, animate: true);
                UpdateVirtualization();
            }

            UpdateSelectedItemBounds();
        }
        else if (change.Property == CardScaleProperty || change.Property == CardSpacingProperty || change.Property == TopPaddingProperty)
        {
            SendLayoutMessages();
            UpdateSelectedItemBounds();
        }
        else if (change.Property == OpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
        else if (change.Property == GlobalOpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
        else if (change.Property == ImageCacheSizeProperty)
            _maxImageCacheEntries = Math.Max(1, change.GetNewValue<int>());
        else if (change.Property == PauseLoadingSpinnerAnimationProperty)
            _visual?.SendHandlerMessage(new PauseLoadingSpinnerAnimationMessage(change.GetNewValue<bool>()));
        else if (change.Property == ItemsSourceProperty ||
                 change.Property == ImageFileNamePropertyProperty ||
                 change.Property == ImageBitmapPropertyProperty ||
                 change.Property == TitlePropertyProperty)
        {
            UpdateItems();
        }
        else if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>() && _images.Count > 0)
                ScheduleInitialImageLoad();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            PointedItemIndex = HitTestIndex(pos);
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
        Focus();
        _settlePointerState();

        if (TryBeginScrollbarDrag(pos, e.Pointer))
        {
            e.Handled = true;
            return;
        }

        int hitIndex = HitTestIndex(pos);
        if (e.ClickCount >= 2 && hitIndex != -1)
        {
            PublishSelectedIndex(hitIndex, force: true);
            ItemSelectedCommand?.Execute(hitIndex);
            ItemDoubleClickedCommand?.Execute(hitIndex);
            e.Handled = true;
            return;
        }

        _isPressed = true;
        _isPointerScrolling = true;
        _scrollAtDragStart = _knownScrollY;
        _startPoint = pos;
        _prevPoint = pos;
        _prevTime = e.Timestamp;
        _velocityY = 0;
        e.Pointer.Capture(this);
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(true));
        UpdateHoverState(pos);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _prevPoint = pos;

        if (_isScrollbarPressed)
        {
            ApplyScrollbarPosition(pos.Y);
            UpdateVirtualization();
            e.Handled = true;
            return;
        }

        if (!_isPressed)
        {
            UpdateHoverState(pos);
            return;
        }

        double dy = pos.Y - _startPoint.Y;
        _targetScrollY = Math.Clamp(_scrollAtDragStart - dy, -80, GetMaxScrollY() + 80);
        _knownScrollY = _targetScrollY;
        _visual?.SendHandlerMessage(new CardGridScrollMessage(_targetScrollY));

        ulong dt = e.Timestamp - _prevTime;
        if (dt > 0)
            _velocityY = -(pos.Y - _prevPoint.Y) / (dt / 1000.0);

        _prevTime = e.Timestamp;
        UpdateHoverState(pos);
        UpdateVirtualization();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);

        if (_isScrollbarPressed)
        {
            _isScrollbarPressed = false;
            _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(false));
            _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_isPressed)
            return;

        _isPressed = false;
        _isPointerScrolling = false;
        e.Pointer.Capture(null);
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));

        int hit = HitTestIndex(pos);
        bool isClick = Math.Abs(pos.X - _startPoint.X) < 8 && Math.Abs(pos.Y - _startPoint.Y) < 8;
        if (isClick && hit != -1)
        {
            PublishSelectedIndex(hit, force: true);
            ItemSelectedCommand?.Execute(hit);
        }
        else
        {
            _targetScrollY = Math.Clamp(_knownScrollY, 0, GetMaxScrollY());
            SyncKnownScrollY(_targetScrollY);
            _visual?.SendHandlerMessage(new CardGridScrollMessage(_targetScrollY));
            _visual?.SendHandlerMessage(new CardGridScrollVelocityMessage(_velocityY * 0.85));
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double wheelDelta = e.Delta.Y * WheelScrollPixels;
        _targetScrollY = Math.Clamp(_animationSync.TargetScrollY - wheelDelta, 0, GetMaxScrollY());
        _knownScrollY = _targetScrollY;

        double impulse = -wheelDelta * 11.0;
        double newVelocity = Math.Clamp(_animationSync.VelocityY + impulse, -5500, 5500);
        _visual?.SendHandlerMessage(new CardGridScrollMessage(_targetScrollY));
        _visual?.SendHandlerMessage(new CardGridScrollVelocityMessage(newVelocity));

        _isWheelScrolling = true;
        _wheelScrollSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _wheelScrollSettleTimer.Tick -= WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Tick += WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Stop();
        _wheelScrollSettleTimer.Start();
        UpdateVirtualization();
        e.Handled = true;
    }

    private void WheelScrollSettleTimer_Tick(object? sender, EventArgs e)
    {
        _wheelScrollSettleTimer?.Stop();
        _isWheelScrolling = false;
        _knownScrollY = _animationSync.CurrentScrollY;
        _targetScrollY = _knownScrollY;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_images.Count == 0)
            return;

        int columns = GetLayoutMetrics().Columns;
        int current = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _images.Count - 1);
        int next = current;
        if (e.Key == Key.Left) next = current - 1;
        else if (e.Key == Key.Right) next = current + 1;
        else if (e.Key == Key.Up) next = current - columns;
        else if (e.Key == Key.Down) next = current + columns;
        else if (e.Key == Key.Home) next = 0;
        else if (e.Key == Key.End) next = _images.Count - 1;
        else return;

        next = Math.Clamp(next, 0, _images.Count - 1);
        if (next != current || e.Key is Key.Home or Key.End)
        {
            PublishSelectedIndex(next, force: true);
            ItemSelectedCommand?.Execute(next);
            e.Handled = true;
        }
    }

    private void _settlePointerState()
    {
        _isPressed = false;
        _isScrollbarPressed = false;
        _isPointerScrolling = false;
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
        _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(false));
    }

    private bool TryBeginScrollbarDrag(Point pos, IPointer pointer)
    {
        var metrics = GetLayoutMetrics();
        if (metrics.MaxScrollY <= 1)
            return false;

        float hitRight = (float)Bounds.Width - CardGridLayoutHelper.ScrollbarRightInset;
        var trackRect = new Rect(
            hitRight - CardGridLayoutHelper.ScrollbarHitWidth,
            ScrollbarMargin,
            CardGridLayoutHelper.ScrollbarHitWidth,
            Bounds.Height - ScrollbarMargin * 2);
        if (!trackRect.Contains(pos))
            return false;

        _isScrollbarPressed = true;
        _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(true));
        pointer.Capture(this);
        ApplyScrollbarPosition(pos.Y);
        return true;
    }

    private void ApplyScrollbarPosition(double pointerY)
    {
        var metrics = GetLayoutMetrics();
        double trackTop = ScrollbarMargin;
        double trackHeight = Math.Max(1, Bounds.Height - ScrollbarMargin * 2);
        double ratio = Math.Clamp((pointerY - trackTop) / trackHeight, 0, 1);
        _targetScrollY = ratio * metrics.MaxScrollY;
        SyncKnownScrollY(_targetScrollY);
        _visual?.SendHandlerMessage(new CardGridScrollMessage(_targetScrollY));
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(true));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateHoverState(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetScrollbarHovered(false);
    }

    private void UpdateHoverState(Point pos)
    {
        int hit = HitTestIndex(pos);
        if (hit != PointedItemIndex)
            PointedItemIndex = hit;
        _visual?.SendHandlerMessage(new CardGridHoveredIndexMessage(hit));
        SetScrollbarHovered(IsPointerOverScrollbarArea(pos));
    }

    private void SetScrollbarHovered(bool hovered)
    {
        if (_isScrollbarHovered == hovered)
            return;

        _isScrollbarHovered = hovered;
        _visual?.SendHandlerMessage(new CardGridScrollbarHoverMessage(hovered));
    }

    private bool IsPointerOverScrollbarArea(Point pos)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || GetMaxScrollY() <= 1)
            return false;

        float hitRight = (float)Bounds.Width - CardGridLayoutHelper.ScrollbarRightInset;
        float hoverLeft = hitRight - CardGridLayoutHelper.ScrollbarHitWidth - 12f;
        var hoverRect = new Rect(
            hoverLeft,
            ScrollbarMargin,
            (float)Bounds.Width - hoverLeft,
            Bounds.Height - ScrollbarMargin * 2);
        return hoverRect.Contains(pos);
    }

    private int HitTestIndex(Point pos)
    {
        return CardGridLayoutHelper.HitTestCard(
            pos,
            _knownScrollY,
            _images.Count,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);
    }

    private CardGridLayoutMetrics GetLayoutMetrics() =>
        CardGridLayoutHelper.Compute(
            (float)Bounds.Width,
            (float)Bounds.Height,
            _images.Count,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

    private double GetMaxScrollY() => GetLayoutMetrics().MaxScrollY;

    private void ScrollToIndex(int index, bool animate)
    {
        if (index < 0 || _images.Count == 0)
            return;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _pendingScrollToIndex = index;
            return;
        }

        double offset = CardGridLayoutHelper.ScrollOffsetForIndex(
            index,
            (float)Bounds.Width,
            (float)Bounds.Height,
            _images.Count,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

        _pendingScrollToIndex = -1;
        SyncKnownScrollY(offset);
        if (animate)
            _visual?.SendHandlerMessage(new CardGridScrollMessage(offset));
        else
            _visual?.SendHandlerMessage(new CardGridSnapScrollMessage(offset));
    }

    private void SyncKnownScrollY(double scrollY)
    {
        _knownScrollY = scrollY;
        _targetScrollY = scrollY;
        _animationSync.CurrentScrollY = scrollY;
        _animationSync.TargetScrollY = scrollY;
        _animationSync.VelocityY = 0;
    }

    private void PublishSelectedIndex(double index, bool force = false)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _images.Count - 1));
        if (!force && Math.Abs(index - SelectedIndex) < 0.001)
            return;

        _suppressSelectedIndexSideEffects = true;
        SelectedIndex = index;
        _suppressSelectedIndexSideEffects = false;
        _visual?.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Round(index)));
        UpdateSelectedItemBounds();
    }

    private void UpdateSelectedItemBounds()
    {
        if (!PublishSelectedItemBounds || _images.Count == 0 || Bounds.Width <= 0)
        {
            SelectedItemBounds = default;
            return;
        }

        int index = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _images.Count - 1);
        SelectedItemBounds = CardGridLayoutHelper.GetCardBounds(
            index,
            _knownScrollY,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);
    }

    private void SendLayoutMessages() =>
        _visual?.SendHandlerMessage(new CardGridLayoutMessage((float)CardScale, (float)CardSpacing, (float)TopPadding));

    private void SendTitles()
    {
        string? titleProp = TitleProperty;
        var titles = new string[_itemsSnapshot.Length];
        for (int i = 0; i < _itemsSnapshot.Length; i++)
            titles[i] = GetTitleValue(_itemsSnapshot[i], titleProp) ?? string.Empty;
        _visual?.SendHandlerMessage(new CardGridTitlesMessage(titles));
    }

    private void UpdateCompositionVisualSize(Size size)
    {
        if (_visual == null || size.Width <= 0 || size.Height <= 0)
            return;

        var logicalSize = new Vector2((float)size.Width, (float)size.Height);
        _visual.Size = logicalSize;
        _visual.SendHandlerMessage(logicalSize);
    }

    private void UpdateItems()
    {
        if (_subscribedItemsSource != ItemsSource)
        {
            if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;

            foreach (var item in _subscribedItems)
                item.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Clear();

            _subscribedItemsSource = ItemsSource;
            if (_subscribedItemsSource is INotifyCollectionChanged newIncc)
                newIncc.CollectionChanged += ItemsSource_CollectionChanged;
        }

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        _lastLoadScrollY = double.NaN;
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during items update", ex); }
        _loadCts = new CancellationTokenSource();

        if (ItemsSource == null)
        {
            ClearResources();
            return;
        }

        var items = ItemsSource.Cast<object?>().ToArray();
        UpdateItemsSnapshot(items);
        string? bitmapProp = ImageBitmapProperty;
        string? fileProp = ImageFileNameProperty;
        _sectionPlaceholderBitmap = CompositionCoverImageHelper.DetectSectionPlaceholder(items, bitmapProp, GetBitmapValue);
        _images.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item != null && _imageCache.TryGetValue(item, out var cached))
            {
                CompositionCoverImageHelper.ReadCoverSources(
                    item,
                    bitmapProp,
                    fileProp,
                    GetBitmapValue,
                    ResolveCoverImagePath,
                    _sectionPlaceholderBitmap,
                    out var cachedBitmap,
                    out var cachedFile);
                var currentSourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
                    item as MediaItem, cachedBitmap, cachedFile, _sectionPlaceholderBitmap);
                bool hasMatchingSource = _itemImageSourceKeys.TryGetValue(item, out var cachedSourceKey) && Equals(cachedSourceKey, currentSourceKey);
                _images.Add(hasMatchingSource ? cached : null);
                if (hasMatchingSource)
                    TouchCacheItem(item);
                else
                    ReleaseItemImage(item);
            }
            else
            {
                _images.Add(null);
            }

            if (item is INotifyPropertyChanged inpc && _subscribedItems.Add(inpc))
                inpc.PropertyChanged += Item_PropertyChanged;
        }

        SyncVisualImageSlots();
        SendTitles();
        _visual?.SendHandlerMessage(new CardGridResetScrollbarMessage());
        _visual?.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, items.Length - 1))));

        if (items.Length > 0)
        {
            int initialIndex = (int)Math.Clamp(Math.Round(SelectedIndex), 0, items.Length - 1);
            _lastVirtualizationIndex = -1;
            _lastVirtualizationScrollY = double.NaN;
            ScrollToIndex(initialIndex, animate: false);
            ScheduleInitialImageLoad();
        }

        UpdateSelectedItemBounds();
    }

    private void ScheduleInitialImageLoad()
    {
        _pendingVisibleLoad = true;
        if (_initialImageLoadScheduled)
            return;

        _initialImageLoadScheduled = true;
        Dispatcher.UIThread.Post(ExecuteInitialImageLoad, DispatcherPriority.Loaded);
    }

    private void ExecuteInitialImageLoad()
    {
        _initialImageLoadScheduled = false;
        if (_images.Count == 0 || !IsVisible)
        {
            _pendingVisibleLoad = false;
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        _pendingVisibleLoad = false;
        if (_pendingScrollToIndex >= 0)
            ScrollToIndex(_pendingScrollToIndex, animate: false);

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        _ = VirtualizeAsync(GetVisibleVirtualizationCenterIndex(), CancellationToken.None);
    }

    private int GetVisibleVirtualizationCenterIndex()
    {
        if (Bounds.Height <= 0 || _images.Count == 0)
            return (int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, _images.Count - 1));

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            _images.Count,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

        if (visibleStart >= 0 && visibleEnd >= visibleStart)
            return (visibleStart + visibleEnd) / 2;

        return (int)Math.Clamp(Math.Round(SelectedIndex), 0, _images.Count - 1);
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateItems);

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string? bitmapProp = ImageBitmapProperty;
            string? fileProp = ImageFileNameProperty;
            string? titleProp = TitleProperty;
            if (sender == null)
                return;

            if (e.PropertyName == titleProp || e.PropertyName == nameof(MediaItem.Title))
            {
                SendTitles();
                return;
            }

            if (e.PropertyName != bitmapProp && e.PropertyName != fileProp)
                return;

            if (!_itemIndices.TryGetValue(sender, out var idx) || !IsCurrentSnapshotItem(sender, idx))
            {
                UpdateItems();
                return;
            }

            _pendingCoverImageReloads[sender] = idx;
            ProcessPendingCoverImageReloads();
        });
    }

    private void ProcessPendingCoverImageReloads()
    {
        if (_pendingCoverImageReloads.Count == 0)
            return;

        var pending = _pendingCoverImageReloads.ToArray();
        _pendingCoverImageReloads.Clear();
        _ = ReloadCoverImagesBatchAsync(pending);
    }

    private async Task ReloadCoverImagesBatchAsync(KeyValuePair<object, int>[] pending)
    {
        try
        {
            await ReloadCoverImagesBatchAsyncCore(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Card grid cover image reload failed", ex);
        }
    }

    private async Task ReloadCoverImagesBatchAsyncCore(KeyValuePair<object, int>[] pending)
    {
        string? bitmapProp = ImageBitmapProperty;
        string? fileProp = ImageFileNameProperty;

        foreach (var (sender, idx) in pending)
        {
            if (idx < 0 || idx >= _itemsSnapshot.Length || !ReferenceEquals(_itemsSnapshot[idx], sender))
                continue;

            CompositionCoverImageHelper.ReadCoverSources(
                sender,
                bitmapProp,
                fileProp,
                GetBitmapValue,
                ResolveCoverImagePath,
                _sectionPlaceholderBitmap,
                out var bitmapValue,
                out var fileName);

            object? sourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
                sender as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);
            if (_itemImageSourceKeys.TryGetValue(sender, out var existingSourceKey) && Equals(existingSourceKey, sourceKey))
            {
                if (idx < _images.Count && _images[idx] == null && _imageCache.TryGetValue(sender, out var cachedImage))
                    AssignItemImage(sender, idx, cachedImage, sourceKey);

                continue;
            }

            if (TryAcquireSharedImage(sourceKey, out var sharedImage))
            {
                AssignItemImage(sender, idx, sharedImage!, sourceKey);
                continue;
            }

            SKImage? realImage = null;
            try
            {
                realImage = await LoadImageAsync(bitmapValue, fileName, sender as MediaItem, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to reload cover image for item at index {idx}", ex);
            }

            if (realImage != null)
            {
                var imageToUse = RegisterSharedImage(sourceKey, realImage);
                AssignItemImage(sender, idx, imageToUse, sourceKey);
            }
            else if (idx >= 0 && idx < _images.Count)
            {
                PostToUi(() =>
                {
                    if (!IsCurrentSnapshotItem(sender, idx))
                        return;

                    ReleaseItemImage(sender);
                    _images[idx] = null;
                    _visual?.SendHandlerMessage(new UpdateImageMessage(idx, null));
                }, DispatcherPriority.Render);
            }
        }
    }

    private void UpdateVirtualization() => QueueVirtualization();

    private void QueueVirtualization()
    {
        if (_images.Count == 0 || Bounds.Height <= 0)
            return;

        var metrics = GetLayoutMetrics();
        double scrollY = _knownScrollY;
        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            scrollY,
            (float)Bounds.Height,
            _images.Count,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

        int centerIdx = visibleStart >= 0 && visibleEnd >= visibleStart
            ? (visibleStart + visibleEnd) / 2
            : (int)Math.Round(SelectedIndex);

        bool scrollMoved = double.IsNaN(_lastVirtualizationScrollY) ||
                           Math.Abs(scrollY - _lastVirtualizationScrollY) > Math.Max(24, metrics.CardHeight * 0.35f);
        bool rangeMoved = centerIdx != _lastVirtualizationIndex;

        if (!scrollMoved && !rangeMoved)
            return;

        _lastVirtualizationIndex = centerIdx;
        _lastVirtualizationScrollY = scrollY;

        _virtualizeDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleVirtualizationDebounceMs) };
        _virtualizeDebounceTimer.Tick -= VirtualizeDebounceTimer_Tick;
        _virtualizeDebounceTimer.Tick += VirtualizeDebounceTimer_Tick;
        _virtualizeDebounceTimer.Interval = TimeSpan.FromMilliseconds(
            _isPointerScrolling || _animationSync.IsAnimating ? ActiveScrollVirtualizationDebounceMs : IdleVirtualizationDebounceMs);
        _virtualizeDebounceTimer.Stop();
        _virtualizeDebounceTimer.Start();
    }

    private void VirtualizeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _virtualizeDebounceTimer?.Stop();

        var metrics = GetLayoutMetrics();
        double scrollY = _knownScrollY;
        bool significantScroll = double.IsNaN(_lastLoadScrollY) ||
                                 Math.Abs(scrollY - _lastLoadScrollY) > Math.Max(48, metrics.CardHeight * 0.6f);

        if (significantScroll || _loadCts == null)
        {
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during virtualization debounce", ex); }
            _loadCts = new CancellationTokenSource();
            _lastLoadScrollY = scrollY;
        }

        _ = VirtualizeAsync(_lastVirtualizationIndex, _loadCts!.Token);
    }

    private async Task VirtualizeAsync(int centerIdx, CancellationToken ct)
    {
        try
        {
            await VirtualizeAsyncCore(centerIdx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("Card grid image virtualization failed", ex);
        }
    }

    private async Task VirtualizeAsyncCore(int centerIdx, CancellationToken ct)
    {
        if (ItemsSource == null)
            return;

        string? bitmapProp = ImageBitmapProperty;
        string? fileProp = ImageFileNameProperty;
        var items = _itemsSnapshot;
        int totalCount = items.Length;
        if (totalCount == 0)
            return;

        const int loadBuffer = 12;
        const int retainBuffer = 20;
        var itemToIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < totalCount; k++)
        {
            var val = items[k];
            if (val != null)
                itemToIndex[val] = k;
        }

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            totalCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

        int loadStart = Math.Max(0, visibleStart - loadBuffer);
        int loadEnd = Math.Min(totalCount - 1, Math.Max(visibleEnd, centerIdx) + loadBuffer);
        int retainStart = Math.Max(0, loadStart - retainBuffer);
        int retainEnd = Math.Min(totalCount - 1, loadEnd + retainBuffer);

        var evictKeys = new List<(object Key, int CachedIndex)>();
        foreach (var key in _imageCache.Keys.ToList())
        {
            if (ct.IsCancellationRequested)
                return;
            if (!itemToIndex.TryGetValue(key, out var cachedIndex) || cachedIndex < retainStart || cachedIndex > retainEnd)
                evictKeys.Add((key, cachedIndex));
        }

        if (evictKeys.Count > 0)
        {
            var keysToEvict = evictKeys;
            PostToUi(() =>
            {
                foreach (var (key, cachedIndex) in keysToEvict)
                {
                    if (cachedIndex >= 0 && cachedIndex < _images.Count && IsCurrentSnapshotItem(key, cachedIndex))
                    {
                        _images[cachedIndex] = null;
                        _visual?.SendHandlerMessage(new UpdateImageMessage(cachedIndex, null));
                    }

                    ReleaseItemImage(key);
                }
            }, DispatcherPriority.Background);
        }

        EnsureDiskCacheDirectory();

        int prioritizedStart = Math.Max(loadStart, visibleStart);
        int prioritizedEnd = Math.Min(loadEnd, visibleEnd);
        var loadOrder = new List<int>();
        for (int i = prioritizedStart; i <= prioritizedEnd; i++)
            loadOrder.Add(i);
        for (int i = loadStart; i <= loadEnd; i++)
        {
            if (i < prioritizedStart || i > prioritizedEnd)
                loadOrder.Add(i);
        }

        using var gate = new SemaphoreSlim(ParallelImageLoadCount);
        var loadTasks = loadOrder.Select(async index =>
        {
            if (ct.IsCancellationRequested)
                return;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await TryLoadItemAsync(index, items, bitmapProp, fileProp, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(loadTasks).ConfigureAwait(false);
        PostToUi(() => TrimImageCache(itemToIndex), DispatcherPriority.Background);
    }

    private async Task<bool> TryLoadItemAsync(int index, object?[] items, string? bitmapProp, string? fileProp, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || index < 0 || index >= items.Length)
            return false;

        var item = items[index];
        if (item == null)
            return false;

        CompositionCoverImageHelper.ReadCoverSources(
            item,
            bitmapProp,
            fileProp,
            GetBitmapValue,
            ResolveCoverImagePath,
            _sectionPlaceholderBitmap,
            out var bitmapValue,
            out var fileName);

        object? sourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
            item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);
        if (_imageCache.TryGetValue(item, out var cachedImage))
        {
            bool hasMatchingSource = _itemImageSourceKeys.TryGetValue(item, out var existingSourceKey) && Equals(existingSourceKey, sourceKey);
            if (hasMatchingSource)
            {
                if (CompositionCoverImageHelper.ShouldReloadCachedCover(
                        item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap))
                {
                    PostToUi(() => ReleaseItemImage(item));
                }
                else
                {
                    TouchCacheItem(item);
                    if (index < _images.Count && _images[index] == null)
                        AssignItemImage(item, index, cachedImage, sourceKey);

                    return false;
                }
            }
            else
            {
                PostToUi(() => ReleaseItemImage(item));
            }
        }

        SetLoading(index, true);
        if (TryAcquireSharedImage(sourceKey, out var sharedImage))
        {
            if (!ct.IsCancellationRequested)
                AssignItemImage(item, index, sharedImage!, sourceKey);
            return true;
        }

        SKImage? realImage = null;
        try
        {
            realImage = await LoadImageAsync(bitmapValue, fileName, item as MediaItem, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load image for item at index {index}", ex);
        }

        if (realImage != null)
        {
            var imageToUse = RegisterSharedImage(sourceKey, realImage);
            if (!ct.IsCancellationRequested)
                AssignItemImage(item, index, imageToUse, sourceKey);
        }
        else
        {
            SetLoading(index, false);
        }

        return true;
    }

    private void PostToUi(Action action) => PostToUi(action, DispatcherPriority.Normal);

    private void PostToUi(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action, priority);
    }

    private void SendVisualMessage(object message) =>
        PostToUi(() => _visual?.SendHandlerMessage(message), DispatcherPriority.Background);

    private void SetLoading(int index, bool isLoading) =>
        SendVisualMessage(new UpdateImageMessage(index, null, isLoading));

    private void AssignItemImage(object item, int index, SKImage image, object? sourceKey)
    {
        PostToUi(() =>
        {
            if (!IsCurrentSnapshotItem(item, index))
                return;

            ReleaseItemImage(item);
            _imageCache[item] = image;
            _itemImageSourceKeys[item] = sourceKey!;
            TouchCacheItem(item);
            _images[index] = image;
            _visual?.SendHandlerMessage(new UpdateImageMessage(index, image));
            UpdateSelectedItemBounds();
        }, DispatcherPriority.Background);
    }

    private void ClearResources()
    {
        _pendingVisibleLoad = false;
        _pendingScrollToIndex = -1;
        _initialImageLoadScheduled = false;
        _pendingCoverImageReloads.Clear();
        _knownScrollY = 0;
        var disposedImages = new HashSet<SKImage>(ReferenceEqualityComparer.Instance);
        foreach (var key in _imageCache.Keys.ToList())
            ReleaseItemImage(key, disposedImages);

        _imageCache.Clear();
        _imageCacheNodes.Clear();
        _imageCacheLru.Clear();
        _sharedImageCache.Clear();
        _itemImageSourceKeys.Clear();
        _sharedPlaceholder = null;
        _sectionPlaceholderBitmap = null;
        _images.Clear();
        _itemsSnapshot = Array.Empty<object?>();
        _itemIndices.Clear();
        foreach (var item in _subscribedItems)
            item.PropertyChanged -= Item_PropertyChanged;
        _subscribedItems.Clear();
        _visual?.SendHandlerMessage(Array.Empty<SKImage>());
        foreach (var img in disposedImages)
            DisposeImage(img);
    }

    private void UpdateItemsSnapshot(IReadOnlyList<object?> items)
    {
        _itemsSnapshot = new object?[items.Count];
        _itemIndices.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            _itemsSnapshot[i] = items[i];
            if (items[i] != null)
                _itemIndices[items[i]!] = i;
        }
    }

    private bool IsCurrentSnapshotItem(object? item, int index) =>
        index >= 0 && index < _itemsSnapshot.Length && ReferenceEquals(_itemsSnapshot[index], item);

    private static string? GetTitleValue(object? item, string? propertyName) =>
        item switch
        {
            MediaItem mediaItem when string.IsNullOrEmpty(propertyName) || string.Equals(propertyName, nameof(MediaItem.Title), StringComparison.Ordinal) => mediaItem.Title,
            _ => null
        };

    private static Bitmap? GetBitmapValue(object item, string? propertyName) =>
        item switch
        {
            MediaItem mediaItem when string.Equals(propertyName, nameof(MediaItem.CoverBitmap), StringComparison.Ordinal) => mediaItem.CoverBitmap,
            _ => null
        };

    private static string? GetFileNameValue(object item, string? propertyName)
    {
        if (item is not MediaItem mediaItem || string.IsNullOrEmpty(propertyName))
            return null;
        if (string.Equals(propertyName, nameof(MediaItem.FileName), StringComparison.Ordinal))
            return mediaItem.FileName;
        if (string.Equals(propertyName, nameof(MediaItem.LocalCoverPath), StringComparison.Ordinal))
            return mediaItem.LocalCoverPath;
        return null;
    }

    private string? ResolveCoverImagePath(object? item, string? configuredFileProp)
    {
        if (item is not MediaItem mediaItem)
            return null;

        if (!string.IsNullOrWhiteSpace(mediaItem.LocalCoverPath) && File.Exists(mediaItem.LocalCoverPath))
        {
            if (CompositionMetadataCoverHelper.IsMetadataCachePath(mediaItem.LocalCoverPath))
            {
                if (CompositionMetadataCoverHelper.MetadataCacheHasCoverImage(mediaItem.LocalCoverPath))
                    return mediaItem.LocalCoverPath;
            }
            else if (CompositionCoverImageHelper.IsLikelyImageFile(mediaItem.LocalCoverPath))
            {
                return mediaItem.LocalCoverPath;
            }
        }

        var configuredPath = GetFileNameValue(mediaItem, configuredFileProp);
        if (CompositionCoverImageHelper.IsLikelyImageFile(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        if (CompositionCoverImageHelper.IsLikelyImageFile(mediaItem.FileName) && File.Exists(mediaItem.FileName))
            return mediaItem.FileName;

        return CompositionMetadataCoverHelper.GetMetadataCachePath(mediaItem.FileName);
    }

    private static object? GetImageSourceKey(Bitmap? bitmap, string? fileName)
    {
        if (bitmap != null)
            return bitmap;
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;
        return null;
    }

    private SKImage GetPlaceholder() => _sharedPlaceholder ??= GeneratePlaceholder();

    private SKImage GeneratePlaceholder()
    {
        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        surface.Canvas.Clear(SKColor.Parse("#1E1E1E"));
        return surface.Snapshot();
    }

    private bool IsPlaceholderImage(SKImage? image) =>
        image == null || ReferenceEquals(image, _sharedPlaceholder);

    private void SyncVisualImageSlots()
    {
        if (_visual == null)
            return;

        int count = _images.Count;
        if (count == 0)
        {
            _visual.SendHandlerMessage(Array.Empty<SKImage>());
            return;
        }

        _visual.SendHandlerMessage(Enumerable.Repeat<SKImage?>(null, count).ToArray());
        for (int i = 0; i < count; i++)
        {
            if (_images[i] != null)
                _visual.SendHandlerMessage(new UpdateImageMessage(i, _images[i]));
        }
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new Panel
        {
            Width = 0,
            Height = 0,
            IsVisible = false,
            IsHitTestVisible = false,
            Focusable = false
        };

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter") is { } presenter)
        {
            presenter.IsVisible = false;
            presenter.IsHitTestVisible = false;
            presenter.Opacity = 0;
            presenter.RenderTransform = new ScaleTransform(0, 0);
        }
    }

    private void EnsureDiskCacheDirectory()
    {
        if (_isDiskCachePathReady)
            return;
        try
        {
            Directory.CreateDirectory(_diskCachePath);
            _isDiskCachePathReady = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to create image cache directory", ex);
        }
    }

    private string GetCachedImagePath(string file)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file)));
        return Path.Combine(_diskCachePath, hash + ".png");
    }

    private async Task<SKImage?> LoadImageAsync(Bitmap? bitmapValue, string? fileName, MediaItem? owner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        if (CompositionCoverImageHelper.ShouldPreferFileOverBitmap(owner, bitmapValue, fileName, _sectionPlaceholderBitmap))
        {
            var fromFile = await Task.Run(() => LoadAndResize(fileName!, owner), ct);
            if (fromFile != null)
                return fromFile;
        }

        if (bitmapValue != null &&
            !CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bitmapValue, _sectionPlaceholderBitmap))
        {
            var fromBitmap = await ToSkImageAsync(bitmapValue, owner, fileName);
            if (fromBitmap != null)
                return fromBitmap;
        }

        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
            return await Task.Run(() => LoadAndResize(fileName, owner), ct);
        return null;
    }

    private async Task<SKImage?> ToSkImageAsync(Bitmap bitmap, MediaItem? owner = null, string? persistImagePath = null)
    {
        if (bitmap.Format == null || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            return null;

        int w = bitmap.PixelSize.Width;
        int h = bitmap.PixelSize.Height;
        int stride = w * 4;
        int bufferSize = h * stride;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            bool success = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    unsafe
                    {
                        fixed (byte* p = buffer)
                            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), (IntPtr)p, bufferSize, stride);
                    }
                    success = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ToSKImage: CopyPixels failed: {ex.Message}");
                }
            });
            if (!success)
                return null;

            return await Task.Run(() =>
            {
                using var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* p = buffer)
                        Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
                }
                return CreateCardImage(skBmp);
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private SKImage? CreateCardImage(SKBitmap source)
    {
        SKBitmap? cropped = null;
        var working = source;
        try
        {
            cropped = CoverImageBarCropHelper.TryCrop(source, out _);
            if (cropped != null)
                working = cropped;

            int targetW = CachedCardImageSize;
            int targetH = CachedCardImageSize;
            if (working.Width > working.Height)
                targetH = (int)(CachedCardImageSize * (double)working.Height / working.Width);
            else
                targetW = (int)(CachedCardImageSize * (double)working.Width / working.Height);

            if (working.Width <= CachedCardImageSize && working.Height <= CachedCardImageSize)
                return SKImage.FromBitmap(working);

            using var resized = working.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.Medium);
            return resized != null ? SKImage.FromBitmap(resized) : SKImage.FromBitmap(working);
        }
        finally
        {
            if (cropped != null && !ReferenceEquals(cropped, source))
                cropped.Dispose();
        }
    }

    private SKImage? LoadAndResize(string file, MediaItem? owner = null)
    {
        try
        {
            if (CompositionMetadataCoverHelper.IsMetadataCachePath(file))
            {
                var bytes = CompositionMetadataCoverHelper.TryReadCoverBytes(file);
                return bytes == null
                    ? null
                    : CompositionMetadataCoverHelper.LoadCoverFromBytes(bytes, CachedCardImageSize, CreateCardImage);
            }

            EnsureDiskCacheDirectory();
            var cachedFile = GetCachedImagePath(file);
            if (File.Exists(cachedFile))
            {
                using var data = SKData.Create(cachedFile);
                if (data != null)
                    return SKImage.FromEncodedData(data);
            }

            using var codec = SKCodec.Create(file);
            if (codec == null)
                return null;

            using var bmp = new SKBitmap(codec.Info);
            codec.GetPixels(bmp.Info, bmp.GetPixels());
            var img = CreateCardImage(bmp);
            if (img != null)
            {
                using var data = img.Encode(SKEncodedImageFormat.Png, 80);
                using var stream = File.Create(cachedFile);
                data.SaveTo(stream);
            }

            return img;
        }
        catch (Exception ex)
        {
            Log.Error($"Error in LoadAndResize for file: {file}", ex);
            return null;
        }
    }

    private bool TryAcquireSharedImage(object? sourceKey, out SKImage? image)
    {
        image = null;
        if (sourceKey == null || !_sharedImageCache.TryGetValue(sourceKey, out var entry))
            return false;
        entry.RefCount++;
        image = entry.Image;
        return true;
    }

    private SKImage RegisterSharedImage(object? sourceKey, SKImage image)
    {
        if (sourceKey == null)
            return image;
        if (_sharedImageCache.TryGetValue(sourceKey, out var existing))
        {
            existing.RefCount++;
            DisposeImage(image);
            return existing.Image;
        }

        _sharedImageCache[sourceKey] = new SharedImageEntry(image);
        return image;
    }

    private void ReleaseItemImage(object key, HashSet<SKImage>? disposedImages = null)
    {
        if (_imageCache.TryGetValue(key, out var image))
        {
            _imageCache.Remove(key);
            RemoveCacheNode(key);
            if (_itemImageSourceKeys.Remove(key, out var sourceKey) && _sharedImageCache.TryGetValue(sourceKey, out var entry))
            {
                entry.RefCount--;
                if (entry.RefCount <= 0)
                {
                    _sharedImageCache.Remove(sourceKey);
                    DisposeImage(entry.Image, disposedImages);
                }
            }
            else
            {
                DisposeImage(image, disposedImages);
            }
        }
        else
        {
            RemoveCacheNode(key);
        }
    }

    private void DisposeImage(SKImage? image, HashSet<SKImage>? disposedImages = null)
    {
        if (image == null || ReferenceEquals(image, _sharedPlaceholder))
            return;
        if (disposedImages != null)
        {
            disposedImages.Add(image);
            return;
        }

        SendVisualMessage(new DisposeImageMessage(image));
    }

    private void TouchCacheItem(object key)
    {
        if (_imageCacheNodes.TryGetValue(key, out var node))
        {
            _imageCacheLru.Remove(node);
            _imageCacheLru.AddFirst(node);
            return;
        }

        _imageCacheNodes[key] = _imageCacheLru.AddFirst(key);
    }

    private void RemoveCacheNode(object key)
    {
        if (_imageCacheNodes.Remove(key, out var node))
            _imageCacheLru.Remove(node);
    }

    private void TrimImageCache(Dictionary<object, int> itemToIndex)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(() => TrimImageCache(itemToIndex), DispatcherPriority.Background);
            return;
        }

        while (_imageCache.Count > _maxImageCacheEntries && _imageCacheLru.Last != null)
        {
            var key = _imageCacheLru.Last.Value;
            _imageCacheLru.RemoveLast();
            if (itemToIndex.TryGetValue(key, out var idx) && idx >= 0 && idx < _images.Count)
            {
                _images[idx] = null;
                _visual?.SendHandlerMessage(new UpdateImageMessage(idx, null));
            }

            ReleaseItemImage(key);
        }
    }
}
