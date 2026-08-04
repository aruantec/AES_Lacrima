using System.Buffers;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using log4net;
using SkiaSharp;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;

namespace AES_Controls.Composition;

/// <summary>
/// A composition-based scrollable card grid for cover art with titles.
/// Renders entirely through a compositor visual (game-style frame); does not use ItemsControl containers.
/// </summary>
public class CompositionCardGridControl : Control, IScaleExclusionRenderTarget
{
    internal void BindSharedCoverCache(CompositionSharedCoverCache cache) => _parentSharedCoverCache = cache;

    internal void BindCardDisplayCache(CompositionSharedCoverCache cache) => _parentCardDisplayCache = cache;

    internal void SetCoverLoadingActive(bool active)
    {
        if (_coverLoadingActive == active)
            return;

        _coverLoadingActive = active;
        if (active)
            ResumeCoverLoading();
        else
            SuspendCoverLoading();
    }

    private bool _coverLoadingActive = false;

    private void SuspendCoverLoading()
    {
        _initialImageLoadScheduled = false;
        _pendingVisibleLoad = false;
        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        _coverLoadSuspended = false;
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling grid cover load", ex); }
        _loadCts = null;
        try { _prefetchCts?.Cancel(); _prefetchCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling grid cover prefetch", ex); }
        _prefetchCts = null;
        _coverLoadGeneration++;
        _coverReloadDebounceTimer?.Stop();
        _pendingCoverImageReloads.Clear();
        lock (_coverLoadLock)
        {
            _coverLoadInFlightGeneration.Clear();
            _coverLoadRetryCounts.Clear();
        }

        if (_subscribedItemsSource is INotifyCollectionChanged incc)
            incc.CollectionChanged -= ItemsSource_CollectionChanged;

        foreach (var item in _subscribedItems)
            item.PropertyChanged -= Item_PropertyChanged;
        _subscribedItems.Clear();

        // Deactivate loading without disposing baked images still referenced by the visual handler.
    }

    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<CompositionCardGridControl>();
    private const int CachedCardImageSize = 384;
    private const int PlaceholderScanLimit = 48;
    private const int IdleVisibleLoadBuffer = 6;
    private const int RetainBuffer = 22;
    private const int ViewportLoadBatchSize = 5;
    private const int ViewportLoadBatchSizeFastScroll = 3;
    private const int VisibleLoadBatchSize = 12;
    private const int IdleVisibleLoadBatchSize = 24;
    private const int ViewportLoadFrameMs = 14;
    private const int ViewportLoadFrameMsFastScroll = 18;
    private const int ScrollSettleMs = 24;
    private const double FastScrollVelocityThreshold = 220.0;
    private const double FastWheelScrollVelocityThreshold = 100.0;
    private const double DirectionalPrefetchLeadRows = 4;
    private const double InteractionSuspendVelocityThreshold = 40.0;
    private const double InteractionSuspendWheelVelocityThreshold = 24.0;
    private const int IdleCacheTrimMs = 500;
    private const int WheelScrollSettleMs = 150;
    private const int FallbackInitialVisibleSlots = 36;
    private const int FastItemsPathThreshold = 48;
    private const int SubscriptionBatchSize = 64;
    private const int AnimationHeartbeatMs = 16;
    private const int ActiveScrollVirtualizationDebounceMs = 96;
    private const int IdleVirtualizationDebounceMs = 32;
    private const int CoverReloadDebounceMs = 220;
    private const int ScrollDeferCoverRadius = 8;
    private static readonly SemaphoreSlim CoverDecodeConcurrency = CompositionCoverDecodeConcurrency.Gate;
    private const int IdleParallelImageLoadCount = 2;
    private const int ActiveScrollParallelImageLoadCount = 1;
    private const double WheelScrollPixels = 165.0;
    private const float ScrollbarMargin = 10f;
    private const double DragStartThreshold = 4.0;
    private const int DragAutoScrollMs = 16;
    private const int DragCommitMs = 300;

    private CompositionCustomVisual? _visual;
    private List<SKImage?> _images = new();
    private readonly Dictionary<object, SKImage> _imageCache = new();
    private SKImage? _sharedPlaceholder;
    private readonly HashSet<INotifyPropertyChanged> _subscribedItems = new();
    private readonly LinkedList<object> _imageCacheLru = new();
    private readonly Dictionary<object, LinkedListNode<object>> _imageCacheNodes = new();
    private readonly Dictionary<object, int> _itemIndices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _itemImageSourceKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _itemDisplayCacheKeys = new(ReferenceEqualityComparer.Instance);
    private CompositionSharedCoverCache? _parentSharedCoverCache;
    private CompositionSharedCoverCache? _parentCardDisplayCache;
    private readonly CompositionSharedCoverCache _fallbackSharedCoverCache = new();
    private readonly CompositionSharedCoverCache _fallbackCardDisplayCache = new();
    private CompositionSharedCoverCache SharedCoverCache => _parentSharedCoverCache ?? _fallbackSharedCoverCache;
    private CompositionSharedCoverCache CardDisplayCache => _parentCardDisplayCache ?? _fallbackCardDisplayCache;
    private object?[] _itemsSnapshot = Array.Empty<object?>();
    private int LayoutItemCount => Math.Max(_itemsSnapshot.Length, _images.Count);
    private string _resolvedBitmapProperty = nameof(MediaItem.CoverBitmap);
    private string _resolvedFileProperty = nameof(MediaItem.LocalCoverPath);
    private string _resolvedTitleProperty = nameof(MediaItem.Title);
    private readonly string _diskCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"ImageCache_{CachedCardImageSize}");
    private volatile bool _isDiskCachePathReady;
    private int _maxImageCacheEntries = 160;
    private const int MaxDisplayCacheEntries = 384;
    private int _lastVirtualizationIndex = -1;
    private double _lastVirtualizationScrollY = double.NaN;
    private bool _pendingVisibleLoad;
    private int _pendingScrollToIndex = -1;
    private bool _initialImageLoadScheduled;
    private double _knownScrollY;
    private CancellationTokenSource? _loadCts;
    private DispatcherTimer? _scrollSettleTimer;
    private DispatcherTimer? _idleCacheTrimTimer;
    private int _viewportLoadGeneration;
    private int _viewportEmptyLoadRetries;
    private int _shellUpdateGeneration;
    private int _completedShellUpdateGeneration;
    private readonly Dictionary<int, PendingAssign> _deferredAssigns = new();

    private readonly record struct PendingAssign(object Item, SKImage Source, SKImage Display, object? SourceKey, object? DisplayCacheKey);
    private DispatcherTimer? _coverReloadDebounceTimer;
    private DispatcherTimer? _updateItemsDebounceTimer;
    private IEnumerable? _subscribedItemsSource;
    private readonly CardGridAnimationSyncState _animationSync = new();
    private readonly Dictionary<object, int> _pendingCoverImageReloads = new(ReferenceEqualityComparer.Instance);
    private int _coverLoadGeneration;
    private readonly object _coverLoadLock = new();
    private readonly Dictionary<int, int> _coverLoadInFlightGeneration = new();
    private readonly Dictionary<int, int> _coverLoadRetryCounts = new();
    private readonly HashSet<int> _deferredCoverLoadIndices = new();
    private readonly object _deferredCoverLoadLock = new();
    private readonly object _displayBakeSync = new();
    private readonly Dictionary<object, Task<SKImage>> _displayBakeTasks = new();
    private readonly HashSet<object> _pendingDisplayCacheAssignKeys = new();
    private CancellationTokenSource? _prefetchCts;
    private bool _viewportMotionTracked;
    private bool _fastScrollTracked;
    private bool _viewportLoadChainActive;
    private bool _pendingViewportLoadAfterLayout;
    private Bitmap? _sectionPlaceholderBitmap;
    private VirtualizationLayoutSnapshot _visibilityLayoutSnapshot;
    private DispatcherTimer? _uiSyncTimer;
    private DispatcherTimer? _wheelScrollSettleTimer;
    private bool _isWheelScrolling;
    private bool _isInternalMove;
    private bool _isDragging;
    private int _pressedItemIndex = -1;
    private int _draggingIndex = -1;
    private int _dragStartIndex = -1;
    private int _currentDragTargetIndex = -1;
    private int _lastSentDropTargetIndex = -1;
    private int _cachedDragTargetIndex = -1;
    private Point _cachedDragTargetPoint;
    private long _lastDragTargetCalcTicks;
    private Avalonia.Vector _dragPointerOffset;
    private bool _hasDragMoved;
    private bool _visualDragActive;
    private double _savedScrollYOnDragFinish;
    private int _pendingReorderFrom = -1;
    private int _pendingReorderTo = -1;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _dragCommitTimer;

    private Point _startPoint;
    private Point _prevPoint;
    private ulong _prevTime;
    private double _velocityY;
    private bool _isPressed;
    private bool _isScrollbarPressed;
    private bool _isScrollbarHovered;
    private double _scrollbarGrabOffset;
    private bool _isPointerScrolling;
    private bool _interactionSuspended;
    private bool _userInteractionUnlock;
    private bool _coverLoadSuspended;
    private Point _lastPointerPosition;
    private bool _suppressSelectedIndexSideEffects;
    private double _targetScrollY;
    private double _scrollAtDragStart;
    private Rect _selectedItemBounds;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, IBrush?>(nameof(Background), Brushes.Transparent);

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

    public static readonly StyledProperty<int> GameplayPreviewItemIndexProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, int>(nameof(GameplayPreviewItemIndex), -1);

    public static readonly StyledProperty<bool> PauseLoadingSpinnerAnimationProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(PauseLoadingSpinnerAnimation), false);

    public static readonly StyledProperty<bool> IsContentLoadingProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(IsContentLoading), false);

    public static readonly StyledProperty<bool> HorizontalScrollEnabledProperty =
        AvaloniaProperty.Register<CompositionCardGridControl, bool>(nameof(HorizontalScrollEnabled), true);

    public static readonly DirectProperty<CompositionCardGridControl, Rect> SelectedItemBoundsProperty =
        AvaloniaProperty.RegisterDirect<CompositionCardGridControl, Rect>(
            nameof(SelectedItemBounds),
            o => o.SelectedItemBounds);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

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

    public int GameplayPreviewItemIndex
    {
        get => GetValue(GameplayPreviewItemIndexProperty);
        set => SetValue(GameplayPreviewItemIndexProperty, value);
    }

    public bool PauseLoadingSpinnerAnimation
    {
        get => GetValue(PauseLoadingSpinnerAnimationProperty);
        set => SetValue(PauseLoadingSpinnerAnimationProperty, value);
    }

    public bool IsContentLoading
    {
        get => GetValue(IsContentLoadingProperty);
        set => SetValue(IsContentLoadingProperty, value);
    }

    public bool HorizontalScrollEnabled
    {
        get => GetValue(HorizontalScrollEnabledProperty);
        set => SetValue(HorizontalScrollEnabledProperty, value);
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
        GlobalOpacity = Opacity;
        ClipToBounds = true;

        _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(AnimationHeartbeatMs), DispatcherPriority.Render, (_, _) =>
        {
            if (PublishSelectedItemBounds && !ShouldBlockCoverWork())
                UpdateSelectedItemBounds();

            if (_images.Count == 0)
                return;

            if (_isWheelScrolling || _isPointerScrolling || _isScrollbarPressed || _animationSync.IsAnimating)
            {
                SyncViewportMotionState();
                if (!_isWheelScrolling && !_isPointerScrolling && !_isScrollbarPressed)
                    return;
            }
            else
            {
                return;
            }

            bool trackingCompositorScroll = _isPointerScrolling || _isScrollbarPressed ||
                (_animationSync.IsAnimating && Math.Abs(_animationSync.VelocityY) > 0.01);
            if (!trackingCompositorScroll)
                return;
            double syncScrollY = _animationSync.CurrentScrollY;
            if (Math.Abs(syncScrollY - _knownScrollY) > 2.0)
            {
                _knownScrollY = syncScrollY;
                if (!IsFastScrollInMotion())
                    TryScheduleViewportLoadOnScroll();
            }
        });

        LayoutUpdated += OnLayoutUpdated;

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragAutoScrollMs) };
        _autoScrollTimer.Tick += AutoScrollTimer_Tick;
        _dragCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragCommitMs) };
        _dragCommitTimer.Tick += DragCommitTimer_Tick;
        CompositionViewportState.MotionChanged += OnViewportMotionChanged;
    }

    private void OnViewportMotionChanged(bool inMotion)
    {
        if (!inMotion)
            ScheduleScrollSettleFill();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        RefreshVisibilityLayoutSnapshot();

        if (_pendingScrollToIndex >= 0 && Bounds.Width > 0 && Bounds.Height > 0)
        {
            int index = _pendingScrollToIndex;
            _pendingScrollToIndex = -1;
            ScrollToIndex(index, animate: false);
        }

        if (_pendingVisibleLoad && Bounds.Width > 0 && Bounds.Height > 0)
            ScheduleInitialImageLoad();

        if (_pendingViewportLoadAfterLayout && Bounds.Width > 0 && Bounds.Height > 0)
            TryStartDeferredViewportLoad();
    }

    private void TryStartDeferredViewportLoad()
    {
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0 || Bounds.Height <= 0)
            return;

        _pendingViewportLoadAfterLayout = false;
        PresentItemsShell(_itemsSnapshot.Length, clearSlotImages: false);
        BeginViewportLoadChain(restart: true);
        SyncCoverLoadingIndicators();
    }

    public void RefreshExclusionRenderSize()
    {
        UpdateCompositionVisualSize(Bounds.Size);
        UpdateSelectedItemBounds();
    }

    internal void RefreshMissingCoverSlots(bool forceFullRescan = false)
    {
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0)
            return;

        if (forceFullRescan)
        {
            _initialImageLoadScheduled = false;
            _pendingVisibleLoad = true;
            _lastVirtualizationIndex = -1;

            foreach (int i in EnumerateViewportVisibleIndices(rowBuffer: 4))
            {
                if (i < 0 || i >= _itemsSnapshot.Length || _itemsSnapshot[i] is not { } item)
                    continue;

                if (i < _images.Count)
                    _images[i] = null;

                ReleaseItemImage(item);
            }
        }

        BeginViewportLoadChain(restart: true);
        SyncCoverLoadingIndicators();
    }

    private bool HasVisibleEmptyCoverSlots()
    {
        if (_itemsSnapshot.Length == 0)
            return false;

        foreach (int i in EnumerateViewportVisibleIndices(rowBuffer: 2))
        {
            if (i < 0 || i >= _itemsSnapshot.Length || _itemsSnapshot[i] == null)
                continue;

            if (i >= _images.Count || _images[i] == null || IsPlaceholderImage(_images[i]))
                return true;
        }

        int fallback = Math.Min(_itemsSnapshot.Length, FallbackInitialVisibleSlots);
        for (int i = 0; i < fallback; i++)
        {
            if (_itemsSnapshot[i] == null)
                continue;

            if (i >= _images.Count || _images[i] == null || IsPlaceholderImage(_images[i]))
                return true;
        }

        return false;
    }

    private void ScheduleViewportLoadRetry(int coverLoadGeneration, int delayMs = 350)
    {
        const int maxRetries = 16;
        if (_viewportEmptyLoadRetries >= maxRetries)
            return;

        _viewportEmptyLoadRetries++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            PostToUi(() =>
            {
                if (!_coverLoadingActive || coverLoadGeneration != _coverLoadGeneration || ShouldBlockCoverWork())
                    return;

                if (!HasVisibleEmptyCoverSlots())
                    return;

                BeginViewportLoadChain(restart: true);
                SyncCoverLoadingIndicators();
            }, DispatcherPriority.Background);
        });
    }

    private bool HasMissingCoverSlots()
    {
        if (_itemsSnapshot.Length == 0)
            return false;

        foreach (int i in EnumerateViewportVisibleIndices(rowBuffer: 4))
        {
            if (SlotNeedsCoverLoad(i))
                return true;
        }

        int fallback = Math.Min(_itemsSnapshot.Length, FallbackInitialVisibleSlots);
        for (int i = 0; i < fallback; i++)
        {
            if (SlotNeedsCoverLoad(i))
                return true;
        }

        return false;
    }

    private void ClearLoadingForDisplayedCovers()
    {
        for (int i = 0; i < _images.Count; i++)
        {
            if (_images[i] != null && !IsPlaceholderImage(_images[i]))
                SetLoading(i, false);
        }
    }

    internal void SyncItemsSourceMetadataOnly(IEnumerable? source)
    {
        if (source == null)
        {
            ClearResources();
            return;
        }

        var items = CaptureItemsSnapshot(source);
        UpdateItemsSnapshot(items);
    }

    internal bool SyncItemsSourceLightweight(IEnumerable? source)
    {
        int sourceCount = GetSourceCount(source);
        bool sourceChanged = !ReferenceEquals(_subscribedItemsSource, source);
        if (!sourceChanged &&
            _itemsSnapshot.Length > 0 &&
            sourceCount == _itemsSnapshot.Length)
            return false;

        if (_subscribedItemsSource != source)
        {
            if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;

            foreach (var item in _subscribedItems)
                item.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Clear();
            _subscribedItemsSource = source;
        }

        if (source == null)
        {
            ClearResources();
            return true;
        }

        if (sourceChanged)
        {
            _shellUpdateGeneration++;
            BeginItemsUpdateReset();
            _images.Clear();
            _visual?.SendHandlerMessage(new CardGridImageRevealHoldMessage(true));
        }

        int previousCount = _itemsSnapshot.Length;
        var items = CaptureItemsSnapshot(source);
        bool countChanged = items.Length != previousCount;
        UpdateItemsSnapshot(items);

        if (_coverLoadingActive)
            EnsureItemSubscriptions(source, items);

        if (!_coverLoadingActive)
            return true;

        if (sourceChanged)
        {
            PresentItemsShell(items.Length, clearSlotImages: true);
            ResetScrollToStart();
            SnapToSelectedIndex();
        }
        else if (countChanged)
        {
            _shellUpdateGeneration++;
            BeginItemsUpdateReset();
            PresentItemsShell(items.Length, clearSlotImages: false);
        }
        else
        {
            PresentItemsShell(items.Length, clearSlotImages: false);
        }

        int generation = _coverLoadGeneration;
        int shellGeneration = _shellUpdateGeneration;
        Dispatcher.UIThread.Post(
            () => CompleteItemsUpdate(items, generation, shellGeneration, scheduleLoads: true, detachAllCaches: sourceChanged),
            DispatcherPriority.Background);

        return true;
    }

    private static int GetSourceCount(IEnumerable? source) =>
        source switch
        {
            null => 0,
            ICollection collection => collection.Count,
            _ => source.Cast<object?>().Count()
        };

    private static object?[] CaptureItemsSnapshot(IEnumerable source)
    {
        if (source is object?[] array)
            return array;

        if (source is IList list)
        {
            var snapshot = new object?[list.Count];
            for (int i = 0; i < list.Count; i++)
                snapshot[i] = list[i];
            return snapshot;
        }

        return source.Cast<object?>().ToArray();
    }

    internal void RefreshItemsFromCurrentSource()
    {
        if (!_coverLoadingActive)
            return;

        SyncItemsSourceLightweight(_subscribedItemsSource ?? ItemsSource);
    }

    internal void ResumeCoverLoading()
    {
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0)
            return;

        EnsureItemSubscriptions(_subscribedItemsSource ?? ItemsSource, _itemsSnapshot);
        BeginViewportLoadChain(restart: true);
        SyncCoverLoadingIndicators();
    }

    private void EnsureItemSubscriptions(IEnumerable? source, IReadOnlyList<object?> items)
    {
        var collectionSource = _subscribedItemsSource ?? ItemsSource ?? source;
        if (collectionSource != null && !ReferenceEquals(_subscribedItemsSource, collectionSource))
        {
            if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;

            _subscribedItemsSource = collectionSource;
        }

        if (_subscribedItemsSource is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged -= ItemsSource_CollectionChanged;
            incc.CollectionChanged += ItemsSource_CollectionChanged;
        }

        if (items.Count <= SubscriptionBatchSize)
        {
            SubscribeItemsRange(items, 0, items.Count);
            return;
        }

        _pendingSubscriptionItems = items;
        _pendingSubscriptionIndex = 0;
        ScheduleSubscriptionBatch();
    }

    private IReadOnlyList<object?>? _pendingSubscriptionItems;
    private int _pendingSubscriptionIndex;
    private DispatcherTimer? _subscriptionBatchTimer;

    private void SubscribeItemsRange(IReadOnlyList<object?> items, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            var item = items[i];
            if (item is INotifyPropertyChanged inpc && _subscribedItems.Add(inpc))
                inpc.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void ScheduleSubscriptionBatch()
    {
        _subscriptionBatchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _subscriptionBatchTimer.Tick -= SubscriptionBatchTimer_Tick;
        _subscriptionBatchTimer.Tick += SubscriptionBatchTimer_Tick;
        _subscriptionBatchTimer.Stop();
        _subscriptionBatchTimer.Start();
    }

    private void SubscriptionBatchTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingSubscriptionItems == null)
        {
            _subscriptionBatchTimer?.Stop();
            return;
        }

        int end = Math.Min(_pendingSubscriptionIndex + SubscriptionBatchSize, _pendingSubscriptionItems.Count);
        SubscribeItemsRange(_pendingSubscriptionItems, _pendingSubscriptionIndex, end);
        _pendingSubscriptionIndex = end;
        if (_pendingSubscriptionIndex >= _pendingSubscriptionItems.Count)
        {
            _subscriptionBatchTimer?.Stop();
            _pendingSubscriptionItems = null;
        }
    }

    internal void TryAssignCachedCover(object item, SKImage image, object? sourceKey, bool replacePlaceholderDisplay = false)
    {
        if (!_coverLoadingActive)
            return;

        if (!_itemIndices.TryGetValue(item, out var index))
            return;

        if (index < _images.Count &&
            _images[index] != null &&
            !IsPlaceholderImage(_images[index]))
        {
            if (!replacePlaceholderDisplay)
                return;

            _itemImageSourceKeys.TryGetValue(item, out var existingKey);
            if (existingKey != null && !IsPlaceholderSourceKey(existingKey))
                return;
        }

        SKImage adopted = image;
        if (sourceKey != null && !IsPlaceholderSourceKey(sourceKey))
        {
            if (SharedCoverCache.TryAcquire(sourceKey, out var shared))
                adopted = shared;
            else
                adopted = RegisterSharedImage(sourceKey, image);
        }

        AssignItemImage(item, index, adopted, sourceKey);
    }

    internal void ImportCoverImagesFrom(CompositionCarouselControl? source, bool replacePlaceholderDisplay = false)
    {
        if (source == null)
            return;

        foreach (var (item, image, sourceKey) in source.EnumerateDecodedCovers())
        {
            if (!_itemIndices.TryGetValue(item, out _))
                continue;

            TryAssignCachedCover(item, image, sourceKey, replacePlaceholderDisplay);
        }
    }

    internal void SetImageRevealHold(bool hold) =>
        _visual?.SendHandlerMessage(new CardGridImageRevealHoldMessage(hold));

    internal void RequestCoverDisplayRefresh(bool forceFullRescan = false)
    {
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0)
            return;

        if (_shellUpdateGeneration != _completedShellUpdateGeneration)
            return;

        RefreshMissingCoverSlots(forceFullRescan);
    }

    internal void HydrateCoverImagesFrom(CompositionCarouselControl? source)
    {
        ImportCoverImagesFrom(source, replacePlaceholderDisplay: true);
        BeginViewportLoadChain(restart: true);
        SyncCoverLoadingIndicators();
    }

    internal void SnapToSelectedIndex()
    {
        if (_itemsSnapshot.Length == 0)
            return;

        int index = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _itemsSnapshot.Length - 1);
        ScrollToIndex(index, animate: false);
    }

    internal IEnumerable<(object Item, SKImage Image, object? SourceKey)> EnumerateDecodedCovers()
    {
        foreach (var item in _imageCache.Keys.ToList())
        {
            if (!_imageCache.TryGetValue(item, out var image) || IsPlaceholderImage(image))
                continue;

            _itemImageSourceKeys.TryGetValue(item, out var sourceKey);
            if (IsPlaceholderSourceKey(sourceKey))
                continue;

            yield return (item, image, sourceKey);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Background is ISolidColorBrush solid)
        {
            var opaque = new SolidColorBrush(Color.FromArgb(255, solid.Color.R, solid.Color.G, solid.Color.B));
            context.DrawRectangle(opaque, null, new Rect(Bounds.Size));
        }
        else if (Background != null)
            context.DrawRectangle(Background, null, new Rect(Bounds.Size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new CompositionCardGridVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        UpdateResolvedCoverPropertyNames();
        _visual.SendHandlerMessage(new CardGridAttachSyncMessage(_animationSync));
        SendLayoutMessages();
        UpdateCompositionVisualSize(Bounds.Size);
        if (_images.Count > 0)
            SyncVisualImageSlots();
        _visual.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Round(SelectedIndex)));
        _visual.SendHandlerMessage(new CardGridBackgroundColorMessage(GetSkColor(Background)));
        _visual.SendHandlerMessage(new CardGridHorizontalScrollMessage(HorizontalScrollEnabled));
        _visual.SendHandlerMessage(new GlobalOpacityMessage(Opacity));
        _visual.SendHandlerMessage(new PauseLoadingSpinnerAnimationMessage(PauseLoadingSpinnerAnimation));
        _visual.SendHandlerMessage(new CardGridContentLoadingMessage(IsContentLoading));
        if (_subscribedItemsSource != null || ItemsSource != null)
            SyncItemsSourceLightweight(_subscribedItemsSource ?? ItemsSource);

        _uiSyncTimer?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CompositionViewportState.MotionChanged -= OnViewportMotionChanged;
        LayoutUpdated -= OnLayoutUpdated;
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during detach", ex); }
        _uiSyncTimer?.Stop();
        _autoScrollTimer?.Stop();
        _dragCommitTimer?.Stop();
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

        if (_pendingVisibleLoad && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            ScheduleInitialImageLoad();

        if (_pendingViewportLoadAfterLayout && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            TryStartDeferredViewportLoad();
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
                EnsureIndexVisible(idx, animate: false);
                UpdateVirtualization();
            }

            UpdateSelectedItemBounds();
        }
        else if (change.Property == CardScaleProperty || change.Property == CardSpacingProperty || change.Property == TopPaddingProperty)
        {
            SendLayoutMessages();
            UpdateSelectedItemBounds();
        }
        else if (change.Property == BackgroundProperty)
            _visual?.SendHandlerMessage(new CardGridBackgroundColorMessage(GetSkColor(change.GetNewValue<IBrush?>())));
        else if (change.Property == HorizontalScrollEnabledProperty)
        {
            _visual?.SendHandlerMessage(new CardGridHorizontalScrollMessage(change.GetNewValue<bool>()));
            SyncKnownScrollY(0);
            _visual?.SendHandlerMessage(new CardGridSnapScrollMessage(0));
            EnsureIndexVisible((int)Math.Round(SelectedIndex), animate: false);
            UpdateSelectedItemBounds();
            if (_coverLoadingActive && _itemsSnapshot.Length > 0)
            {
                PresentItemsShell(_itemsSnapshot.Length, clearSlotImages: false);
                BeginViewportLoadChain(restart: true);
                SyncCoverLoadingIndicators();
            }
        }
        else if (change.Property == ImageCacheSizeProperty)
            _maxImageCacheEntries = Math.Max(1, change.GetNewValue<int>());
        else if (change.Property == PauseLoadingSpinnerAnimationProperty)
            _visual?.SendHandlerMessage(new PauseLoadingSpinnerAnimationMessage(change.GetNewValue<bool>()));
        else if (change.Property == PublishSelectedItemBoundsProperty)
            UpdateSelectedItemBounds();
        else if (change.Property == IsContentLoadingProperty)
            _visual?.SendHandlerMessage(new CardGridContentLoadingMessage(change.GetNewValue<bool>()));
        else if (change.Property == OpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
        else if (change.Property == GlobalOpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
        else if (change.Property == ItemsSourceProperty)
        {
            SyncItemsSourceLightweight(change.GetNewValue<IEnumerable?>());
        }
        else if (change.Property == ImageFileNamePropertyProperty ||
                 change.Property == ImageBitmapPropertyProperty ||
                 change.Property == TitlePropertyProperty)
        {
            UpdateResolvedCoverPropertyNames();
            SyncItemsSourceLightweight(_subscribedItemsSource ?? ItemsSource);
        }
        else if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>())
                ScheduleInitialImageLoad();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        _lastPointerPosition = pos;
        OnUserGridInteractionStarted();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            OpenItemContextMenu(HitTestIndex(pos));
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
        _isPointerScrolling = false;
        _scrollAtDragStart = _knownScrollY;
        _startPoint = pos;
        _prevPoint = pos;
        _prevTime = e.Timestamp;
        _velocityY = 0;
        _pressedItemIndex = hitIndex;
        e.Pointer.Capture(this);

        if (hitIndex >= 0)
        {
            BeginItemDrag(hitIndex);
        }
        else
        {
            _isPointerScrolling = true;
            _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(true));
        }

        UpdateHoverState(pos);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        _lastPointerPosition = pos;
        _prevPoint = pos;

        if (_isScrollbarPressed)
        {
            ApplyScrollbarPosition(pos);
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            if (!_hasDragMoved)
            {
                double moved = Point.Distance(_startPoint, pos);
                if (moved > DragStartThreshold)
                {
                    _hasDragMoved = true;
                    ActivateVisualDrag();
                }
                else
                {
                    e.Handled = true;
                    return;
                }
            }

            UpdateDragInteraction(pos);
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
                _autoScrollTimer.Start();
            e.Handled = true;
            return;
        }

        if (!_isPressed)
        {
            UpdateHoverState(pos);
            return;
        }

        if (!_isPointerScrolling)
        {
            _isPointerScrolling = true;
            _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(true));
        }

        double scrollDelta = HorizontalScrollEnabled ? pos.X - _startPoint.X : pos.Y - _startPoint.Y;
        _targetScrollY = Math.Clamp(_scrollAtDragStart - scrollDelta, -80, GetMaxScrollY() + 80);
        _knownScrollY = _targetScrollY;
        _visual?.SendHandlerMessage(new CardGridScrollMessage(_targetScrollY));

        ulong dt = e.Timestamp - _prevTime;
        if (dt > 0)
        {
            double pointerDelta = HorizontalScrollEnabled ? pos.X - _prevPoint.X : pos.Y - _prevPoint.Y;
            _velocityY = -pointerDelta / (dt / 1000.0);
        }

        _prevTime = e.Timestamp;
        if (!_interactionSuspended)
            UpdateHoverState(pos);
        else
            SetScrollbarHovered(IsPointerOverScrollbarArea(pos));
        if (!_viewportMotionTracked)
            SyncViewportMotionState();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pos = e.GetPosition(this);

        _autoScrollTimer?.Stop();

        if (_isScrollbarPressed)
        {
            _isScrollbarPressed = false;
            _scrollbarGrabOffset = 0;
            _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(false));
            _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            int targetIndex = GetDragTargetIndex(pos);
            FinishDrag(targetIndex, cancel: false, e.Pointer);
            e.Handled = true;
            return;
        }

        if (!_isPressed)
            return;

        _isPressed = false;
        _pressedItemIndex = -1;
        bool wasScrolling = _isPointerScrolling;
        _isPointerScrolling = false;
        e.Pointer.Capture(null);
        if (wasScrolling)
            _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
        OnUserGridInteractionEnded();
        SyncViewportMotionState();

        int hit = HitTestIndex(pos);
        bool isClick = Math.Abs(pos.X - _startPoint.X) < 8 && Math.Abs(pos.Y - _startPoint.Y) < 8;
        if (isClick && hit != -1)
        {
            PublishSelectedIndex(hit, force: true);
            ItemSelectedCommand?.Execute(hit);
        }
        else if (wasScrolling)
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
        _wheelScrollSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WheelScrollSettleMs) };
        _wheelScrollSettleTimer.Tick -= WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Tick += WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Stop();
        _wheelScrollSettleTimer.Start();
        SyncViewportMotionState();
        e.Handled = true;
    }

    private void WheelScrollSettleTimer_Tick(object? sender, EventArgs e)
    {
        _wheelScrollSettleTimer?.Stop();
        _isWheelScrolling = false;
        _knownScrollY = _animationSync.CurrentScrollY;
        _targetScrollY = _knownScrollY;
        SyncViewportMotionState();
    }

    private void SyncViewportMotionState()
    {
        bool fastScroll = IsFastScrollInMotion();
        bool suspendInteraction = ShouldSuspendInteraction();
        if (suspendInteraction != _interactionSuspended)
            SetInteractionSuspended(suspendInteraction);

        if (fastScroll != _fastScrollTracked)
        {
            _fastScrollTracked = fastScroll;
            if (fastScroll)
            {
                _coverLoadSuspended = true;
                CompositionViewportState.EnterMotion();
                CancelViewportLoadChain();
                return;
            }

            _coverLoadSuspended = false;
            CompositionViewportState.ExitMotion();
            _knownScrollY = _animationSync.CurrentScrollY;
            _targetScrollY = _animationSync.TargetScrollY;
            ResumeCoverWorkAfterScroll();
            return;
        }

        if (!fastScroll && _viewportMotionTracked != IsScrollInMotion())
        {
            _viewportMotionTracked = IsScrollInMotion();
            if (!_viewportMotionTracked)
            {
                _knownScrollY = _animationSync.CurrentScrollY;
                _targetScrollY = _animationSync.TargetScrollY;
                ScheduleScrollSettleFill();
            }
        }
    }

    private void ResumeCoverWorkAfterScroll()
    {
        FlushDeferredAssigns();
        FlushDeferredCoverLoads();
        ProcessPendingCoverImageReloads();
        BeginViewportLoadChain(restart: true);
    }

    private void TryScheduleViewportLoadOnScroll()
    {
        if (ShouldBlockCoverWork())
            return;

        QueueVirtualization();
        BeginViewportLoadChain();
    }

    private void OnUserGridInteractionStarted()
    {
        _userInteractionUnlock = true;
        _scrollSettleTimer?.Stop();
        if (_interactionSuspended)
            SetInteractionSuspended(false);

        _coverLoadSuspended = false;
        FlushDeferredAssigns();
        FlushDeferredCoverLoads();
        ProcessPendingCoverImageReloads();
        BeginViewportLoadChain();
    }

    private void OnUserGridInteractionEnded()
    {
        if (!IsScrollInMotion())
            _userInteractionUnlock = false;
        SyncViewportMotionState();
    }

    private void ScheduleScrollSettleFill()
    {
        _scrollSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollSettleMs) };
        _scrollSettleTimer.Tick -= ScrollSettleTimer_Tick;
        _scrollSettleTimer.Tick += ScrollSettleTimer_Tick;
        _scrollSettleTimer.Stop();
        _scrollSettleTimer.Start();
    }

    private void ScrollSettleTimer_Tick(object? sender, EventArgs e)
    {
        _scrollSettleTimer?.Stop();
        if (IsScrollInMotion())
        {
            ScheduleScrollSettleFill();
            return;
        }

        OnScrollSettled();
    }

    private void OnScrollSettled()
    {
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0)
            return;

        _userInteractionUnlock = false;
        _coverLoadSuspended = false;
        CompositionViewportState.SetVisibleIndices(EnumerateViewportVisibleIndices().ToArray());
        _visual?.SendHandlerMessage(new CardGridExtendWaveRevealMessage(_itemsSnapshot.Length, HorizontalScrollEnabled));
        FlushDeferredAssigns();
        FlushDeferredCoverLoads();
        ProcessPendingCoverImageReloads();
        BeginViewportLoadChain(restart: true);
        SyncVisibleLoadingIndicators();
        ScheduleIdleCacheTrim();
    }

    private bool ShouldDeferCoverLoad(int index) =>
        ShouldBlockCoverWork();

    private void DeferCoverLoad(int index)
    {
        if (index < 0)
            return;

        lock (_deferredCoverLoadLock)
            _deferredCoverLoadIndices.Add(index);
    }

    private void FlushDeferredCoverLoads()
    {
        if (ShouldBlockCoverWork())
            return;

        List<int> indices;
        lock (_deferredCoverLoadLock)
        {
            indices = _deferredCoverLoadIndices.ToList();
            _deferredCoverLoadIndices.Clear();
        }

        foreach (int index in indices)
            ScheduleCoverImageAtIndex(index);

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
    }

    private DispatcherPriority GetCoverAssignPriority() =>
        IsScrollInMotion() ? DispatcherPriority.Background : DispatcherPriority.Loaded;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_images.Count == 0)
            return;

        int columns = GetLayoutMetrics().Columns;
        int horizontalColumns = GetHorizontalColumnCount();
        int current = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _images.Count - 1);
        int next = current;
        if (HorizontalScrollEnabled)
        {
            if (e.Key == Key.Left) next = current - 1;
            else if (e.Key == Key.Right) next = current + 1;
            else if (e.Key == Key.Up) next = current - horizontalColumns;
            else if (e.Key == Key.Down) next = current + horizontalColumns;
            else if (e.Key == Key.Home) next = 0;
            else if (e.Key == Key.End) next = _images.Count - 1;
            else return;
        }
        else
        {
            if (e.Key == Key.Left) next = current - 1;
            else if (e.Key == Key.Right) next = current + 1;
            else if (e.Key == Key.Up) next = current - columns;
            else if (e.Key == Key.Down) next = current + columns;
            else if (e.Key == Key.Home) next = 0;
            else if (e.Key == Key.End) next = _images.Count - 1;
            else return;
        }

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
        _autoScrollTimer?.Stop();
        _dragCommitTimer?.Stop();
        if (_isDragging)
            FinishDrag(_dragStartIndex, cancel: true);

        _isPressed = false;
        _pressedItemIndex = -1;
        _isScrollbarPressed = false;
        _isPointerScrolling = false;
        _scrollbarGrabOffset = 0;
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
        _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(false));
    }

    private void AutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_visualDragActive)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        UpdateDragInteraction(_prevPoint);
        UpdateDragAutoScroll(_prevPoint);
    }

    private Point GetDragVisualPoint(Point pointerPoint) =>
        new(pointerPoint.X + _dragPointerOffset.X, pointerPoint.Y + _dragPointerOffset.Y);

    private void BeginItemDrag(int hit)
    {
        _isDragging = true;
        _hasDragMoved = false;
        _visualDragActive = false;
        _draggingIndex = hit;
        _dragStartIndex = hit;
        _currentDragTargetIndex = hit;
        _lastSentDropTargetIndex = -1;
        _cachedDragTargetIndex = -1;
        _isPointerScrolling = false;

        var bounds = CardGridLayoutHelper.GetCardBounds(
            hit,
            _knownScrollY,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled,
            _images.Count);
        var dragCenter = new Point(bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5);
        _dragPointerOffset = dragCenter - _prevPoint;
    }

    private void ActivateVisualDrag()
    {
        if (_visualDragActive || _draggingIndex < 0)
            return;

        _visualDragActive = true;
        _visual?.SendHandlerMessage(new CardGridDragStateMessage(_draggingIndex, true));
        _visual?.SendHandlerMessage(new CardGridDropTargetMessage(_draggingIndex));
        _lastSentDropTargetIndex = _draggingIndex;
        UpdateDragInteraction(_prevPoint);
        _autoScrollTimer?.Start();
    }

    private void UpdateDragInteraction(Point pointerPoint)
    {
        var dragPoint = GetDragVisualPoint(pointerPoint);
        _visual?.SendHandlerMessage(new CardGridDragPositionMessage(new Vector2((float)dragPoint.X, (float)dragPoint.Y)));

        int targetIndex = _hasDragMoved ? GetDragTargetIndexThrottled(pointerPoint) : _dragStartIndex;
        _currentDragTargetIndex = targetIndex;
        if (targetIndex != _lastSentDropTargetIndex)
        {
            _lastSentDropTargetIndex = targetIndex;
            _visual?.SendHandlerMessage(new CardGridDropTargetMessage(targetIndex));
        }
    }

    private void UpdateDragAutoScroll(Point pointerPoint)
    {
        if (!_visualDragActive || _images.Count <= 1)
            return;

        var dragPoint = GetDragVisualPoint(pointerPoint);
        double maxScroll = GetMaxScrollY();
        if (maxScroll <= 1)
            return;

        double zone;
        double scrollDelta = 0;
        if (HorizontalScrollEnabled)
        {
            if (Bounds.Width <= 0)
                return;

            double w = Bounds.Width;
            zone = Math.Clamp(w * 0.14, 48, 120);
            if (dragPoint.X < zone)
                scrollDelta = -Math.Pow((zone - dragPoint.X) / zone, 2);
            else if (dragPoint.X > w - zone)
                scrollDelta = Math.Pow((dragPoint.X - (w - zone)) / zone, 2);
        }
        else
        {
            if (Bounds.Height <= 0)
                return;

            double h = Bounds.Height;
            zone = Math.Clamp(h * 0.14, 48, 120);
            if (dragPoint.Y < zone)
                scrollDelta = -Math.Pow((zone - dragPoint.Y) / zone, 2);
            else if (dragPoint.Y > h - zone)
                scrollDelta = Math.Pow((dragPoint.Y - (h - zone)) / zone, 2);
        }

        if (Math.Abs(scrollDelta) < 0.02)
            return;

        const double scrollSpeed = 420.0;
        double nextScroll = Math.Clamp(_knownScrollY + scrollDelta * scrollSpeed * (DragAutoScrollMs / 1000.0), 0, maxScroll);
        if (Math.Abs(nextScroll - _knownScrollY) < 0.25)
            return;

        _targetScrollY = nextScroll;
        _knownScrollY = nextScroll;
        _visual?.SendHandlerMessage(new CardGridScrollMessage(nextScroll));
    }

    private int GetDragTargetIndexThrottled(Point pointerPoint)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = _lastDragTargetCalcTicks == 0
            ? double.MaxValue
            : (now - _lastDragTargetCalcTicks) * 1000.0 / Stopwatch.Frequency;

        if (_cachedDragTargetIndex >= 0 &&
            elapsedMs < 24 &&
            Point.Distance(pointerPoint, _cachedDragTargetPoint) < 10.0)
        {
            return _cachedDragTargetIndex;
        }

        _cachedDragTargetIndex = GetDragTargetIndex(pointerPoint);
        _cachedDragTargetPoint = pointerPoint;
        _lastDragTargetCalcTicks = now;
        return _cachedDragTargetIndex;
    }

    private int GetDragTargetIndex(Point pointerPoint)
    {
        if (_images.Count == 0)
            return -1;

        var dragCenter = GetDragVisualPoint(pointerPoint);
        return CardGridLayoutHelper.FindNearestDropTargetIndex(
            dragCenter,
            _knownScrollY,
            _images.Count,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);
    }

    private IList? GetBoundItemsList()
    {
        var source = _subscribedItemsSource ?? ItemsSource;
        return source as IList;
    }

    private void MoveItem(int from, int to)
    {
        if (GetBoundItemsList() is not IList list || from < 0 || to < 0 || from >= list.Count || to >= list.Count)
            return;

        var item = list[from]!;
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    private void MoveSnapshotItem(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _itemsSnapshot.Length || to >= _itemsSnapshot.Length)
            return;

        var updatedItems = _itemsSnapshot.ToList();
        var item = updatedItems[from];
        updatedItems.RemoveAt(from);
        updatedItems.Insert(to, item);
        UpdateItemsSnapshot(updatedItems);
    }

    private void InsertItemsAt(int startIndex, object?[] newItems)
    {
        if (newItems.Length == 0 || startIndex < 0)
            return;

        var merged = _itemsSnapshot.ToList();
        startIndex = Math.Clamp(startIndex, 0, merged.Count);
        merged.InsertRange(startIndex, newItems);
        UpdateItemsSnapshot(merged);
        SetSectionPlaceholderBitmap(CompositionCoverImageHelper.DetectSectionPlaceholder(
            _itemsSnapshot, ImageBitmapProperty, GetBitmapValue));

        for (int i = 0; i < newItems.Length; i++)
            _images.Insert(startIndex + i, null);

        foreach (var item in newItems)
        {
            if (item is INotifyPropertyChanged inpc && _subscribedItems.Add(inpc))
                inpc.PropertyChanged += Item_PropertyChanged;
        }

        _visual?.SendHandlerMessage(_images.ToArray());

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        if (_itemsSnapshot.Length > 0)
            ScheduleInitialImageLoad();
        else
            UpdateVirtualization();
    }

    private void RemoveItemsAt(int startIndex, int count)
    {
        if (count <= 0 || startIndex < 0 || startIndex >= _images.Count)
            return;

        count = Math.Min(count, _images.Count - startIndex);
        double savedScrollY = _knownScrollY;

        for (int i = count - 1; i >= 0; i--)
        {
            int index = startIndex + i;
            if (index < _itemsSnapshot.Length && _itemsSnapshot[index] is { } removedItem)
            {
                ReleaseItemImage(removedItem);
                if (removedItem is INotifyPropertyChanged inpc)
                {
                    inpc.PropertyChanged -= Item_PropertyChanged;
                    _subscribedItems.Remove(inpc);
                }
            }

            if (index < _images.Count)
                _images.RemoveAt(index);
        }

        var updatedItems = _itemsSnapshot.ToList();
        updatedItems.RemoveRange(startIndex, count);
        UpdateItemsSnapshot(updatedItems);
        SetSectionPlaceholderBitmap(CompositionCoverImageHelper.DetectSectionPlaceholder(
            _itemsSnapshot, ImageBitmapProperty, GetBitmapValue));

        _visual?.SendHandlerMessage(_images.ToArray());

        RestoreScrollPosition(savedScrollY);
        UpdateSelectedItemBounds();
        UpdateVirtualization();
    }

    private void RestoreScrollPosition(double scrollY)
    {
        double maxScroll = GetMaxScrollY();
        double clampedScroll = Math.Clamp(scrollY, 0, maxScroll);
        SyncKnownScrollY(clampedScroll);
        _visual?.SendHandlerMessage(new CardGridSnapScrollMessage(clampedScroll));
    }

    private void FinishDrag(int targetIndex, bool cancel, IPointer? pointer = null)
    {
        _autoScrollTimer?.Stop();

        if (cancel || !_hasDragMoved)
        {
            int clickIndex = _dragStartIndex;
            EndVisualDrag();
            ClearDragState(pointer);

            if (!cancel && !_hasDragMoved && clickIndex >= 0)
            {
                bool changed = Math.Abs(clickIndex - SelectedIndex) >= 0.001;
                PublishSelectedIndex(clickIndex, force: changed);
                ItemSelectedCommand?.Execute(clickIndex);
            }

            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, _images.Count - 1));
        _savedScrollYOnDragFinish = _knownScrollY;
        _visual?.SendHandlerMessage(new CardGridDropTargetMessage(targetIndex));
        _visual?.SendHandlerMessage(new CardGridDragCommitMessage(targetIndex));

        if (targetIndex == _draggingIndex)
        {
            _visual?.SendHandlerMessage(new CardGridDragFinalizeMessage());
            _visualDragActive = false;
            ClearDragState(pointer);
            PublishSelectedIndexWithoutScroll(targetIndex);
            return;
        }

        _pendingReorderFrom = _draggingIndex;
        _pendingReorderTo = targetIndex;
        _isDragging = false;
        _isPressed = false;
        pointer?.Capture(null);
        _dragCommitTimer?.Stop();
        _dragCommitTimer?.Start();
    }

    private void DragCommitTimer_Tick(object? sender, EventArgs e)
    {
        _dragCommitTimer?.Stop();
        if (_pendingReorderFrom >= 0)
        {
            int from = _pendingReorderFrom;
            int to = _pendingReorderTo;
            _pendingReorderFrom = -1;
            _pendingReorderTo = -1;
            CompleteDragReorder(from, to);
        }

        _visual?.SendHandlerMessage(new CardGridDragFinalizeMessage());
        _visualDragActive = false;
        ClearDragState(null);
    }

    private void EndVisualDrag()
    {
        if (!_visualDragActive)
            return;

        _visual?.SendHandlerMessage(new CardGridDragCancelMessage());
        _visual?.SendHandlerMessage(new CardGridDragStateMessage(-1, false));
        _visualDragActive = false;
    }

    private void CompleteDragReorder(int from, int to)
    {
        if (from < 0 || to < 0 || from >= _images.Count || to >= _images.Count || from == to)
            return;

        _isInternalMove = true;
        try
        {
            var img = _images[from];
            _images.RemoveAt(from);
            _images.Insert(to, img);
            MoveSnapshotItem(from, to);
            MoveItem(from, to);
            _visual?.SendHandlerMessage(new CardGridMoveImageMessage(from, to));

            SyncKnownScrollY(_savedScrollYOnDragFinish);
            _visual?.SendHandlerMessage(new CardGridSnapScrollMessage(_savedScrollYOnDragFinish));
            PublishSelectedIndexWithoutScroll(to);
        }
        finally
        {
            _isInternalMove = false;
        }
    }

    private void ClearDragState(IPointer? pointer)
    {
        _isDragging = false;
        _hasDragMoved = false;
        _visualDragActive = false;
        _draggingIndex = -1;
        _dragStartIndex = -1;
        _pressedItemIndex = -1;
        _currentDragTargetIndex = -1;
        _lastSentDropTargetIndex = -1;
        _cachedDragTargetIndex = -1;
        _pendingReorderFrom = -1;
        _pendingReorderTo = -1;
        _dragPointerOffset = default;
        _isPressed = false;
        _isPointerScrolling = false;
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(false));
        UpdateSelectedItemBounds();
        pointer?.Capture(null);
    }

    private void PublishSelectedIndexWithoutScroll(double index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _images.Count - 1));
        _suppressSelectedIndexSideEffects = true;
        SelectedIndex = index;
        _suppressSelectedIndexSideEffects = false;
        _visual?.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Round(index)));
        UpdateSelectedItemBounds();
    }

    private double GetScrollbarRenderScrollY() =>
        Math.Clamp(_animationSync.CurrentScrollY, 0, GetMaxScrollY());

    private bool TryGetScrollbarThumbRect(double scrollY, out Rect thumbRect)
    {
        thumbRect = default;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || GetMaxScrollY() <= 1)
            return false;

        double maxScroll = GetMaxScrollY();
        double scrollPct = maxScroll <= 0 ? 0 : scrollY / maxScroll;

        if (HorizontalScrollEnabled)
        {
            float trackLeft = ScrollbarMargin + 24f;
            float trackRight = (float)Bounds.Width - ScrollbarMargin - 24f;
            float trackWidth = trackRight - trackLeft;
            float trackY = (float)Bounds.Height - ScrollbarMargin - 8f;

            var horizontalMetrics = CardGridHorizontalLayout.ComputeMetrics(
                _images.Count,
                (float)Bounds.Width,
                (float)Bounds.Height,
                (float)CardScale,
                (float)CardSpacing,
                (float)TopPadding);

            float viewportRatio = Math.Clamp((float)Bounds.Width / Math.Max(1f, horizontalMetrics.ContentWidth), 0.08f, 1f);
            float thumbW = Math.Max(48f, trackWidth * viewportRatio);
            float thumbX = trackLeft + (trackWidth - thumbW) * (float)scrollPct;
            thumbRect = new Rect(thumbX, trackY - 5f, thumbW, 10f);
            return true;
        }

        double trackTop = ScrollbarMargin;
        double trackHeight = Math.Max(1, Bounds.Height - ScrollbarMargin * 2);
        float hitRight = (float)Bounds.Width - CardGridLayoutHelper.ScrollbarRightInset;
        float hitLeft = hitRight - CardGridLayoutHelper.ScrollbarHitWidth;
        float trackX = hitLeft + (CardGridLayoutHelper.ScrollbarHitWidth - CardGridLayoutHelper.ScrollbarWidth) * 0.5f;

        var metrics = GetLayoutMetrics();
        float verticalViewportRatio = Math.Clamp((float)(Bounds.Height / Math.Max(1, metrics.ContentHeight)), 0.08f, 1f);
        double thumbH = Math.Max(36, trackHeight * verticalViewportRatio);
        double thumbY = trackTop + (trackHeight - thumbH) * scrollPct;
        thumbRect = new Rect(trackX - 1, thumbY, CardGridLayoutHelper.ScrollbarWidth + 2, thumbH);
        return true;
    }

    private bool TryBeginScrollbarDrag(Point pos, IPointer pointer)
    {
        if (GetMaxScrollY() <= 1)
            return false;

        if (!TryGetScrollbarThumbRect(GetScrollbarRenderScrollY(), out var thumbRect))
            return false;

        if (!thumbRect.Inflate(4).Contains(pos))
            return false;

        _isScrollbarPressed = true;
        _scrollbarGrabOffset = HorizontalScrollEnabled
            ? pos.X - thumbRect.X
            : pos.Y - thumbRect.Y;
        _visual?.SendHandlerMessage(new CardGridScrollbarPressedMessage(true));
        _visual?.SendHandlerMessage(new CardGridDirectScrollFollowMessage(true));
        pointer.Capture(this);
        return true;
    }

    private void ApplyScrollbarPosition(Point pointer)
    {
        if (GetMaxScrollY() <= 1)
            return;

        double maxScroll = GetMaxScrollY();

        if (HorizontalScrollEnabled)
        {
            float trackLeft = ScrollbarMargin + 24f;
            float trackRight = (float)Bounds.Width - ScrollbarMargin - 24f;
            double trackWidth = Math.Max(1, trackRight - trackLeft);

            var horizontalMetrics = CardGridHorizontalLayout.ComputeMetrics(
                _images.Count,
                (float)Bounds.Width,
                (float)Bounds.Height,
                (float)CardScale,
                (float)CardSpacing,
                (float)TopPadding);

            float viewportRatio = Math.Clamp((float)Bounds.Width / Math.Max(1f, horizontalMetrics.ContentWidth), 0.08f, 1f);
            double thumbW = Math.Max(48, trackWidth * viewportRatio);
            double usable = Math.Max(1, trackWidth - thumbW);
            double thumbX = pointer.X - _scrollbarGrabOffset;
            double scrollPct = Math.Clamp((thumbX - trackLeft) / usable, 0, 1);
            _targetScrollY = scrollPct * maxScroll;
        }
        else
        {
            double trackTop = ScrollbarMargin;
            double trackHeight = Math.Max(1, Bounds.Height - ScrollbarMargin * 2);

            var metrics = GetLayoutMetrics();
            float viewportRatio = Math.Clamp((float)(Bounds.Height / Math.Max(1, metrics.ContentHeight)), 0.08f, 1f);
            double thumbH = Math.Max(36, trackHeight * viewportRatio);
            double usable = Math.Max(1, trackHeight - thumbH);
            double thumbY = pointer.Y - _scrollbarGrabOffset;
            double scrollPct = Math.Clamp((thumbY - trackTop) / usable, 0, 1);
            _targetScrollY = scrollPct * maxScroll;
        }

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
        _lastPointerPosition = pos;
        if (_interactionSuspended || _isPointerScrolling || _isWheelScrolling)
        {
            SetScrollbarHovered(IsPointerOverScrollbarArea(pos));
            return;
        }

        int hit = HitTestIndex(pos);
        if (hit != PointedItemIndex)
            PointedItemIndex = hit;
        _visual?.SendHandlerMessage(new CardGridHoveredIndexMessage(hit));
        SetScrollbarHovered(IsPointerOverScrollbarArea(pos));
    }

    private void SetInteractionSuspended(bool suspended)
    {
        if (_interactionSuspended == suspended)
            return;

        _interactionSuspended = suspended;
        _visual?.SendHandlerMessage(new CardGridInteractionSuspendedMessage(suspended));
        if (suspended)
            ClearCardHover();
        else if (IsPointerOver)
            UpdateHoverState(_lastPointerPosition);
        else
            ClearCardHover();
    }

    private void ClearCardHover()
    {
        if (PointedItemIndex != -1)
            PointedItemIndex = -1;
        _visual?.SendHandlerMessage(new CardGridHoveredIndexMessage(-1));
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

        if (HorizontalScrollEnabled)
        {
            float trackLeft = ScrollbarMargin + 24f;
            float trackRight = (float)Bounds.Width - ScrollbarMargin - 24f;
            float trackY = (float)Bounds.Height - ScrollbarMargin - 8f;
            var hoverRect = new Rect(trackLeft - 8f, trackY - 16f, trackRight - trackLeft + 16f, 32f);
            return hoverRect.Contains(pos);
        }

        float hitRight = (float)Bounds.Width - CardGridLayoutHelper.ScrollbarRightInset;
        float hoverLeft = hitRight - CardGridLayoutHelper.ScrollbarHitWidth - 12f;
        var verticalHoverRect = new Rect(
            hoverLeft,
            ScrollbarMargin,
            (float)Bounds.Width - hoverLeft,
            Bounds.Height - ScrollbarMargin * 2);
        return verticalHoverRect.Contains(pos);
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
            (float)TopPadding,
            HorizontalScrollEnabled);
    }

    private CardGridLayoutMetrics GetLayoutMetrics() =>
        CardGridLayoutHelper.Compute(
            (float)Bounds.Width,
            (float)Bounds.Height,
            _images.Count,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding);

    private double GetMaxScrollY() =>
        CardGridLayoutHelper.GetMaxScroll(
            (float)Bounds.Width,
            (float)Bounds.Height,
            _images.Count,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

    private int GetHorizontalColumnCount()
    {
        if (!HorizontalScrollEnabled || Bounds.Width <= 0 || _images.Count == 0)
            return 1;

        return CardGridHorizontalLayout.ComputeMetrics(
            _images.Count,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding).Columns;
    }

    private void ScrollToIndex(int index, bool animate) => EnsureIndexVisible(index, animate);

    private void EnsureIndexVisible(int index, bool animate)
    {
        if (index < 0 || _images.Count == 0)
            return;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _pendingScrollToIndex = index;
            return;
        }

        double offset = CardGridLayoutHelper.ScrollOffsetToRevealIndex(
            index,
            _knownScrollY,
            (float)Bounds.Width,
            (float)Bounds.Height,
            _images.Count,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        _pendingScrollToIndex = -1;
        if (Math.Abs(offset - _knownScrollY) < 0.5)
            return;

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

    private void OpenItemContextMenu(int pointedIndex)
    {
        PointedItemIndex = pointedIndex;
        if (pointedIndex >= 0)
        {
            bool changed = Math.Abs(pointedIndex - SelectedIndex) >= 0.001;
            PublishSelectedIndex(pointedIndex, force: changed);
        }

        Control? menuHost = this.FindAncestorOfType<CompositionCoverControl>();
        menuHost ??= this;
        if (menuHost.ContextMenu is { } menu)
            ContextMenuHelper.OpenExclusive(menu, menuHost);
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
        EnsureIndexVisible((int)Math.Round(index), animate: true);
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
            (float)TopPadding,
            HorizontalScrollEnabled,
            _images.Count);
    }

    internal void RefreshSelectedItemBounds() => UpdateSelectedItemBounds();

    internal void PostGameplayPreviewVisualState(int index, bool visible)
    {
        _visual?.SendHandlerMessage(new GameplayPreviewVisualMessage(index, visible));
    }

    internal void PostGameplayPreviewFrame(SKImage? frame)
    {
        _visual?.SendHandlerMessage(new GameplayPreviewFrameMessage(frame));
    }

    private void SendLayoutMessages() =>
        _visual?.SendHandlerMessage(new CardGridLayoutMessage((float)CardScale, (float)CardSpacing, (float)TopPadding));

    private void UpdateCompositionVisualSize(Size size)
    {
        if (_visual == null || size.Width <= 0 || size.Height <= 0)
            return;

        var logicalSize = new Vector2((float)size.Width, (float)size.Height);
        _visual.Size = logicalSize;
        _visual.SendHandlerMessage(logicalSize);
    }

    private void UpdateItems() =>
        SyncItemsSourceLightweight(_subscribedItemsSource ?? ItemsSource);

    private void BeginItemsUpdateReset()
    {
        var source = _subscribedItemsSource ?? ItemsSource;
        if (source != null && !ReferenceEquals(_subscribedItemsSource, source))
        {
            if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;

            foreach (var item in _subscribedItems)
                item.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Clear();

            _subscribedItemsSource = source;
        }

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        _viewportLoadGeneration++;
        _viewportLoadChainActive = false;
        _viewportEmptyLoadRetries = 0;
        _scrollSettleTimer?.Stop();
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during items update", ex); }
        _loadCts = null;
        try { _prefetchCts?.Cancel(); _prefetchCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling prefetch during items update", ex); }
        _prefetchCts = null;
        _coverLoadGeneration++;
        lock (_coverLoadLock)
        {
            _coverLoadInFlightGeneration.Clear();
            _coverLoadRetryCounts.Clear();
        }

        _pendingCoverImageReloads.Clear();
        _coverReloadDebounceTimer?.Stop();
        _subscriptionBatchTimer?.Stop();
        _pendingSubscriptionItems = null;
        _pendingSubscriptionIndex = 0;
        _scrollSettleTimer?.Stop();
        _idleCacheTrimTimer?.Stop();
        _deferredAssigns.Clear();
        _fastScrollTracked = false;
        _viewportMotionTracked = false;
        _pendingViewportLoadAfterLayout = false;
        lock (_displayBakeSync)
            _displayBakeTasks.Clear();
        lock (_pendingDisplayCacheAssignKeys)
            _pendingDisplayCacheAssignKeys.Clear();
    }

    private bool CanTrimSharedCacheKey(object key)
    {
        lock (_pendingDisplayCacheAssignKeys)
            return !_pendingDisplayCacheAssignKeys.Contains(key);
    }

    private void TrimIdleImageCaches()
    {
        if (!_coverLoadingActive || ShouldBlockCoverWork() || _viewportLoadChainActive || IsScrollInMotion())
            return;

        lock (_displayBakeSync)
        {
            if (_displayBakeTasks.Count > 0)
                return;
        }

        if (_deferredAssigns.Count > 0)
            return;

        SharedCoverCache.TrimUnreferenced(_maxImageCacheEntries, QueueNativeImageDisposal, CanTrimSharedCacheKey);
        CardDisplayCache.TrimUnreferenced(MaxDisplayCacheEntries, QueueNativeImageDisposal, CanTrimSharedCacheKey);
    }

    private void ScheduleDeferredSharedCacheTrim()
    {
        int shellGeneration = _shellUpdateGeneration;
        PostToUi(() =>
        {
            if (shellGeneration != _shellUpdateGeneration || ShouldBlockCoverWork() || _viewportLoadChainActive)
                return;

            TrimIdleImageCaches();
        }, DispatcherPriority.Render);
    }

    private void DetachAllItemImageCaches()
    {
        foreach (var key in _imageCache.Keys.ToList())
            DetachItemImageCache(key);

        _imageCache.Clear();
        _imageCacheNodes.Clear();
        _imageCacheLru.Clear();
        _itemImageSourceKeys.Clear();
        _itemDisplayCacheKeys.Clear();
    }

    private void ClearCardDisplayCache(bool deferDispose = true)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(() => ClearCardDisplayCache(deferDispose), DispatcherPriority.Render);
            return;
        }

        if (!deferDispose)
        {
            CardDisplayCache.Clear(QueueNativeImageDisposal);
            _itemDisplayCacheKeys.Clear();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            CardDisplayCache.Clear(QueueNativeImageDisposal);
            _itemDisplayCacheKeys.Clear();
        }, DispatcherPriority.Render);
    }

    private void ReleaseItemImageForPurge(object key) => DetachItemImageCache(key);

    private void AbandonPendingDisplayCacheRegistration(object displayCacheKey)
    {
        bool wasPending;
        lock (_pendingDisplayCacheAssignKeys)
            wasPending = _pendingDisplayCacheAssignKeys.Remove(displayCacheKey);

        if (!wasPending)
            return;

        if (CardDisplayCache.TryGetEntry(displayCacheKey, out _, out int refCount) && refCount <= 0)
            CardDisplayCache.Release(displayCacheKey, QueueNativeImageDisposal);
    }

    private void AbandonPendingDisplayCacheRegistration(object? sourceKey, string? title)
    {
        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
            return;

        AbandonPendingDisplayCacheRegistration(CreateDisplayCacheKey(sourceKey, title));
    }

    private void RetainDisplayImage(object? displayCacheKey)
    {
        if (displayCacheKey != null)
            CardDisplayCache.Acquire(displayCacheKey);
    }

    private void DetachItemImageCache(object key)
    {
        _imageCache.Remove(key);
        RemoveCacheNode(key);

        if (_itemDisplayCacheKeys.Remove(key, out var displayKey))
            CardDisplayCache.Release(displayKey, QueueNativeImageDisposal);

        if (_itemImageSourceKeys.Remove(key, out var sourceKey) && sourceKey != null)
            SharedCoverCache.Release(sourceKey, QueueNativeImageDisposal);
    }

    private void PresentItemsShell(int count, bool clearSlotImages = true)
    {
        if (_visual == null || !_coverLoadingActive)
            return;

        SendLayoutMessages();
        _visual.SendHandlerMessage(new CardGridSlotCountMessage(count, clearSlotImages));
        _visual.SendHandlerMessage(new CardGridResetScrollbarMessage());
        _visual.SendHandlerMessage(new CardGridSelectedIndexMessage((int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, count - 1))));
        _visual.SendHandlerMessage(new CardGridBeginWaveRevealMessage(count, HorizontalScrollEnabled));
        EnsureImageSlotCount(count);
    }

    private void CompleteItemsUpdate(object?[] items, int generation, int shellGeneration, bool scheduleLoads, bool detachAllCaches = false)
    {
        if (shellGeneration != _shellUpdateGeneration)
            return;

        if (generation != _coverLoadGeneration || !_coverLoadingActive)
        {
            _completedShellUpdateGeneration = shellGeneration;
            _visual?.SendHandlerMessage(new CardGridImageRevealHoldMessage(false));
            return;
        }

        EnsureItemSubscriptions(ItemsSource, items);

        Dispatcher.UIThread.Post(() =>
        {
            if (shellGeneration != _shellUpdateGeneration)
                return;

            if (generation != _coverLoadGeneration || !_coverLoadingActive)
            {
                _completedShellUpdateGeneration = shellGeneration;
                _visual?.SendHandlerMessage(new CardGridImageRevealHoldMessage(false));
                return;
            }

            if (detachAllCaches)
            {
                for (int i = 0; i < _images.Count; i++)
                {
                    if (_images[i] == null)
                        continue;

                    _images[i] = null;
                    _visual?.SendHandlerMessage(new UpdateImageMessage(i, null, ClearImage: true));
                }

                DetachAllItemImageCaches();
            }

            EnsureImageSlotCount(items.Length);

            string? bitmapProp = ImageBitmapProperty;
            SetSectionPlaceholderBitmap(CompositionCoverImageHelper.DetectSectionPlaceholder(
                items, bitmapProp, GetBitmapValue, PlaceholderScanLimit));

            var activeItems = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var item in items)
            {
                if (item != null)
                    activeItems.Add(item);
            }

            var staleCacheKeys = _imageCache.Keys
                .Where(key => key != null && !activeItems.Contains(key))
                .ToList();

            foreach (var key in staleCacheKeys)
                DetachItemImageCache(key);

            PurgePlaceholderSharedImages();

            if (scheduleLoads && items.Length > 0)
            {
                RestoreScrollPosition(_knownScrollY);
                _lastVirtualizationIndex = -1;
                _lastVirtualizationScrollY = double.NaN;

                FlushDeferredAssigns();
                FlushDeferredCoverLoads();
            }
            else
            {
                FlushDeferredAssigns();
                FlushDeferredCoverLoads();
            }

            _visual?.SendHandlerMessage(new CardGridImageRevealHoldMessage(false));
            _completedShellUpdateGeneration = shellGeneration;

            if (scheduleLoads && items.Length > 0)
            {
                BeginViewportLoadChain(restart: true);
                SyncCoverLoadingIndicators();
            }

            UpdateSelectedItemBounds();
        }, DispatcherPriority.Background);
    }

    private void PushSeededImagesToVisual()
    {
        if (_visual == null)
            return;

        for (int i = 0; i < _images.Count; i++)
        {
            if (_images[i] != null)
                _visual.SendHandlerMessage(new UpdateImageMessage(i, _images[i]));
        }
    }

    private readonly record struct CoverDecodeRequest(
        int Index,
        object Item,
        object? SourceKey,
        string? FileName,
        Bitmap? BitmapValue,
        Bitmap? SectionPlaceholder,
        string? Title,
        int CoverLoadGeneration,
        int ViewportLoadGeneration);

    private string ResolvedBitmapProperty => _resolvedBitmapProperty;

    private string ResolvedFileProperty => _resolvedFileProperty;

    private void UpdateResolvedCoverPropertyNames()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return;

        _resolvedBitmapProperty = ImageBitmapProperty ?? nameof(MediaItem.CoverBitmap);
        _resolvedFileProperty = ImageFileNameProperty ?? nameof(MediaItem.LocalCoverPath);
        _resolvedTitleProperty = TitleProperty ?? nameof(MediaItem.Title);
    }

    private CoverImageLoadContext CaptureCoverImageLoadContext()
    {
        UpdateResolvedCoverPropertyNames();
        return new(ResolvedBitmapProperty, ResolvedFileProperty, _sectionPlaceholderBitmap);
    }

    private void ScheduleVisibleCoverImages()
    {
        if (_visual == null || _itemsSnapshot.Length == 0)
            return;

        if (Bounds.Width <= 0 || Bounds.Height <= 0 || !IsVisible)
        {
            int count = Math.Min(_itemsSnapshot.Length, 14);
            for (int i = 0; i < count; i++)
                ScheduleCoverImageAtIndex(i);
            return;
        }

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            _itemsSnapshot.Length,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        if (visibleStart < 0 || visibleEnd < visibleStart)
        {
            int count = Math.Min(_itemsSnapshot.Length, 14);
            for (int i = 0; i < count; i++)
                ScheduleCoverImageAtIndex(i);
            return;
        }

        foreach (int i in EnumerateViewportVisibleIndices())
            ScheduleCoverImageAtIndex(i);
    }

    private bool TryCreateCoverDecodeRequest(int index, out CoverDecodeRequest request)
    {
        request = default!;
        if (index < 0 || index >= _itemsSnapshot.Length)
            return false;

        var item = _itemsSnapshot[index];
        if (item == null)
            return false;

        CompositionCoverImageHelper.ReadCoverSources(
            item,
            ResolvedBitmapProperty,
            ResolvedFileProperty,
            GetBitmapValue,
            ResolveCoverImagePath,
            _sectionPlaceholderBitmap,
            out var bitmapValue,
            out var fileName);

        var sourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
            item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);

        if (item is MediaItem mediaItem && mediaItem.IsLoadingCover && !mediaItem.CoverFound)
        {
            if (!CompositionCoverImageHelper.HasResolvableLocalCoverFile(mediaItem))
                return false;
        }

        if (sourceKey != null &&
            sourceKey is Bitmap sourceBitmap &&
            CompositionCoverImageHelper.IsSectionPlaceholderBitmap(sourceBitmap, _sectionPlaceholderBitmap) &&
            string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (sourceKey != null &&
            _imageCache.TryGetValue(item, out _) &&
            _itemImageSourceKeys.TryGetValue(item, out var existingKey) &&
            Equals(existingKey, sourceKey) &&
            index < _images.Count &&
            _images[index] != null &&
            !IsPlaceholderImage(_images[index]))
        {
            return false;
        }

        if (bitmapValue != null &&
            CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bitmapValue, _sectionPlaceholderBitmap) &&
            string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        request = new CoverDecodeRequest(
            index,
            item,
            sourceKey,
            fileName,
            bitmapValue,
            _sectionPlaceholderBitmap,
            GetTitleValue(item, _resolvedTitleProperty),
            _coverLoadGeneration,
            _viewportLoadGeneration);
        return true;
    }

    private void ScheduleCoverImageAtIndex(int index)
    {
        if (_visual == null || index < 0 || index >= _itemsSnapshot.Length || !IsVisible)
            return;

        if (ShouldDeferCoverLoad(index))
        {
            DeferCoverLoad(index);
            return;
        }

        if (!TryCreateCoverDecodeRequest(index, out _))
        {
            if (_itemsSnapshot[index] is MediaItem { IsLoadingCover: true } && !HasDisplayedCover(index))
                SetLoading(index, true);
            else if (!HasDisplayedCover(index))
                ScheduleCoverImageRetry(index, _coverLoadGeneration, delayMs: 400);
            return;
        }

        BeginViewportLoadChain();
    }

    private bool TryMarkCoverLoadInFlight(int index)
    {
        lock (_coverLoadLock)
        {
            if (_coverLoadInFlightGeneration.TryGetValue(index, out var inFlightGen) &&
                inFlightGen == _coverLoadGeneration)
                return false;

            _coverLoadInFlightGeneration[index] = _coverLoadGeneration;
            return true;
        }
    }

    private void ClearCoverLoadInFlight(int index)
    {
        lock (_coverLoadLock)
            _coverLoadInFlightGeneration.Remove(index);
    }

    private bool IsCoverLoadInFlight(int index)
    {
        lock (_coverLoadLock)
            return _coverLoadInFlightGeneration.TryGetValue(index, out var gen) && gen == _coverLoadGeneration;
    }

    private void ScheduleCoverImageRetry(int index, int generation, int delayMs = 250)
    {
        const int maxRetries = 3;
        lock (_coverLoadLock)
        {
            _coverLoadRetryCounts.TryGetValue(index, out var retries);
            if (retries >= maxRetries)
                return;
            _coverLoadRetryCounts[index] = retries + 1;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (generation != _coverLoadGeneration || _coverLoadSuspended)
                return;

            PostToUi(() =>
            {
                if (generation != _coverLoadGeneration || _coverLoadSuspended || index >= _itemsSnapshot.Length)
                    return;

                if (index < _images.Count && _images[index] != null && !IsPlaceholderImage(_images[index]))
                    return;

                ScheduleCoverImageAtIndex(index);
            }, DispatcherPriority.Background);
        });
    }

    private void ScheduleAllMissingCoverImages()
    {
        if (_visual == null || _itemsSnapshot.Length == 0 || !IsVisible)
            return;

        int generation = _coverLoadGeneration;
        PostToUi(() =>
        {
            if (generation != _coverLoadGeneration)
                return;

            foreach (int index in BuildCoverLoadOrder())
            {
                if (index >= _images.Count)
                    continue;

                if (_images[index] != null && !IsPlaceholderImage(_images[index]))
                    continue;

                ScheduleCoverImageAtIndex(index);
            }
        }, DispatcherPriority.Background);
    }

    private IEnumerable<int> BuildCoverLoadOrder()
    {
        int count = _itemsSnapshot.Length;
        if (count == 0)
            yield break;

        var (visibleStart, visibleEnd) = Bounds.Width > 0 && Bounds.Height > 0
            ? CardGridLayoutHelper.GetVisibleIndexRange(
                _knownScrollY,
                (float)Bounds.Height,
                count,
                (float)Bounds.Width,
                (float)CardScale,
                (float)CardSpacing,
                (float)TopPadding,
                HorizontalScrollEnabled)
            : (-1, -1);

        var scheduled = new HashSet<int>();
        if (visibleStart >= 0 && visibleEnd >= visibleStart)
        {
            for (int i = visibleStart; i <= visibleEnd; i++)
            {
                scheduled.Add(i);
                yield return i;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (scheduled.Add(i))
                yield return i;
        }
    }

    private bool HasDisplayedCover(int index)
    {
        if (index < 0 || index >= _images.Count || index >= _itemsSnapshot.Length)
            return false;

        if (_images[index] == null || IsPlaceholderImage(_images[index]))
            return false;

        var item = _itemsSnapshot[index];
        if (item == null)
            return false;

        CompositionCoverImageHelper.ReadCoverSources(
            item,
            ResolvedBitmapProperty,
            ResolvedFileProperty,
            GetBitmapValue,
            ResolveCoverImagePath,
            _sectionPlaceholderBitmap,
            out var bitmapValue,
            out var fileName);

        var sourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
            item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);

        return IsDisplayedCoverCurrent(item, index, sourceKey);
    }

    private bool SlotNeedsCoverLoad(int index)
    {
        if (index < 0 || index >= _itemsSnapshot.Length)
            return false;

        if (HasDisplayedCover(index))
            return false;

        return _itemsSnapshot[index] != null && TryCreateCoverDecodeRequest(index, out _);
    }

    private bool ShouldShowLoadingSpinner(int index)
    {
        if (index < 0 || index >= _itemsSnapshot.Length)
            return false;

        if (index < _images.Count && _images[index] != null && !IsPlaceholderImage(_images[index]))
            return false;

        if (IsCoverLoadInFlight(index))
            return true;

        var item = _itemsSnapshot[index];
        if (item is MediaItem { IsLoadingCover: true } mediaItem)
        {
            CompositionCoverImageHelper.ReadCoverSources(
                item,
                ResolvedBitmapProperty,
                ResolvedFileProperty,
                GetBitmapValue,
                ResolveCoverImagePath,
                _sectionPlaceholderBitmap,
                out var bitmapValue,
                out var fileName);

            if (CompositionCoverImageHelper.HasResolvableLocalCoverFile(mediaItem))
                return true;

            if (bitmapValue != null &&
                !CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bitmapValue, _sectionPlaceholderBitmap))
                return true;

            if (!string.IsNullOrWhiteSpace(fileName) && File.Exists(fileName))
                return true;

            return false;
        }

        return TryCreateCoverDecodeRequest(index, out _);
    }

    private void SyncCoverLoadingIndicators()
    {
        for (int i = 0; i < _itemsSnapshot.Length; i++)
            SetLoading(i, ShouldShowLoadingSpinner(i));
    }

    private void RefreshLoadingSpinnerAt(int index) => SetLoading(index, ShouldShowLoadingSpinner(index));

    private static async Task<SKImage?> DecodeCoverRequestAsync(CoverDecodeRequest request)
    {
        if (request.BitmapValue != null &&
            !CompositionCoverImageHelper.IsSectionPlaceholderBitmap(request.BitmapValue, request.SectionPlaceholder))
        {
            return await CompositionBitmapHelper.ToCoverSkImageAsync(
                request.BitmapValue,
                CachedCardImageSize,
                cancellationToken: CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(request.FileName))
            return LoadAndResizeStatic(request.FileName, request.Item as MediaItem);

        return null;
    }

    private static SKImage? LoadAndResizeStatic(string file, MediaItem? owner)
    {
        try
        {
            if (CompositionMetadataCoverHelper.IsMetadataCachePath(file) ||
                CompositionMetadataCoverHelper.IsCoverSidecarPath(file))
            {
                var bytes = CompositionMetadataCoverHelper.TryReadCoverBytes(file);
                return bytes == null
                    ? null
                    : CompositionMetadataCoverHelper.LoadCoverFromBytes(bytes, CachedCardImageSize, CreateCardImageStatic);
            }

            using var codec = SKCodec.Create(file);
            if (codec == null)
                return null;

            using var bmp = new SKBitmap(codec.Info);
            codec.GetPixels(bmp.Info, bmp.GetPixels());
            return CreateCardImageStatic(bmp);
        }
        catch
        {
            return null;
        }
    }

    private static SKImage? CreateCardImageStatic(SKBitmap source) =>
        CompositionBitmapHelper.CreateCoverSkImage(source, CachedCardImageSize);

    private void ScheduleIdleAlbumCoverPrefetch()
    {
        // Viewport virtualization loads visible covers; prefetching the full list stalls the UI thread.
    }

    private void SyncItemLoadingStates()
    {
        SyncCoverLoadingIndicators();
    }

    private void ScheduleInitialImageLoad()
    {
        if (!_coverLoadingActive)
            return;

        _pendingVisibleLoad = true;
        if (_initialImageLoadScheduled)
            return;

        _initialImageLoadScheduled = true;
        Dispatcher.UIThread.Post(ExecuteInitialImageLoad, DispatcherPriority.Background);
    }

    private void ExecuteInitialImageLoad()
    {
        _initialImageLoadScheduled = false;
        if (!_coverLoadingActive || _itemsSnapshot.Length == 0)
        {
            _pendingVisibleLoad = false;
            return;
        }

        if (!IsVisible)
        {
            _pendingVisibleLoad = true;
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _pendingVisibleLoad = true;
            return;
        }

        _pendingVisibleLoad = false;
        if (_pendingScrollToIndex >= 0)
            ScrollToIndex(_pendingScrollToIndex, animate: false);

        _lastVirtualizationIndex = -1;
        _lastVirtualizationScrollY = double.NaN;
        if (ShouldBlockCoverWork())
            ScheduleScrollSettleFill();
        else
            OnScrollSettled();
    }

    private readonly record struct CoverImageLoadContext(
        string BitmapProperty,
        string FileProperty,
        Bitmap? SectionPlaceholder);

    private readonly record struct VirtualizationLayoutSnapshot(
        double ScrollY,
        float Width,
        float Height,
        float CardScale,
        float CardSpacing,
        float TopPadding,
        bool HorizontalScrollEnabled,
        bool ScrollInMotion);

    private VirtualizationLayoutSnapshot CaptureVirtualizationLayout() =>
        new(
            _knownScrollY,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled,
            IsFastScrollInMotion());

    private void RefreshVisibilityLayoutSnapshot()
    {
        if (!Dispatcher.UIThread.CheckAccess() || Bounds.Height <= 0)
            return;

        _visibilityLayoutSnapshot = CaptureVirtualizationLayout();
    }

    private VirtualizationLayoutSnapshot GetVisibilityLayoutSnapshot()
    {
        if (Dispatcher.UIThread.CheckAccess() && Bounds.Height > 0)
        {
            _visibilityLayoutSnapshot = CaptureVirtualizationLayout();
            return _visibilityLayoutSnapshot;
        }

        return _visibilityLayoutSnapshot;
    }

    private IEnumerable<int> EnumerateViewportVisibleIndices(VirtualizationLayoutSnapshot layout, int rowBuffer = 2)
    {
        if (LayoutItemCount == 0 || layout.Height <= 0 || layout.Width <= 0)
            yield break;

        foreach (int index in CardGridLayoutHelper.EnumerateVisibleIndices(
                     layout.ScrollY,
                     layout.Height,
                     LayoutItemCount,
                     layout.Width,
                     layout.CardScale,
                     layout.CardSpacing,
                     layout.TopPadding,
                     layout.HorizontalScrollEnabled,
                     rowBuffer))
        {
            yield return index;
        }
    }

    private (int Start, int End) GetViewportIndexRange(VirtualizationLayoutSnapshot layout)
    {
        if (LayoutItemCount == 0 || layout.Height <= 0)
            return (0, -1);

        return CardGridLayoutHelper.GetVisibleIndexRange(
            layout.ScrollY,
            layout.Height,
            LayoutItemCount,
            layout.Width,
            layout.CardScale,
            layout.CardSpacing,
            layout.TopPadding,
            layout.HorizontalScrollEnabled);
    }

    private void ScheduleVirtualization(int centerIdx, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            RunVirtualization(centerIdx, ct);
            return;
        }

        Dispatcher.UIThread.Post(() => RunVirtualization(centerIdx, ct), DispatcherPriority.Background);
    }

    private void RunVirtualization(int centerIdx, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || ShouldBlockCoverWork())
            return;

        BeginViewportLoadChain();
    }

    private int GetVisibleVirtualizationCenterIndex()
    {
        if (Bounds.Height <= 0 || LayoutItemCount == 0)
            return (int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, LayoutItemCount - 1));

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            LayoutItemCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        if (visibleStart >= 0 && visibleEnd >= visibleStart)
            return GetViewportCenterIndex();

        return (int)Math.Clamp(Math.Round(SelectedIndex), 0, LayoutItemCount - 1);
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInternalMove)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_coverLoadingActive)
                return;

            if (_visual == null)
                return;

            if (e.Action == NotifyCollectionChangedAction.Move &&
                e.OldStartingIndex != e.NewStartingIndex &&
                e.OldStartingIndex >= 0 && e.OldStartingIndex < _images.Count &&
                e.NewStartingIndex >= 0 && e.NewStartingIndex < _images.Count)
            {
                var img = _images[e.OldStartingIndex];
                _images.RemoveAt(e.OldStartingIndex);
                _images.Insert(e.NewStartingIndex, img);
                MoveSnapshotItem(e.OldStartingIndex, e.NewStartingIndex);
                _visual.SendHandlerMessage(new CardGridMoveImageMessage(e.OldStartingIndex, e.NewStartingIndex));
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Add &&
                e.NewItems?.Count > 0 &&
                e.NewStartingIndex >= 0)
            {
                InsertItemsAt(e.NewStartingIndex, e.NewItems.Cast<object?>().ToArray());
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Remove &&
                e.OldStartingIndex >= 0 &&
                e.OldItems?.Count > 0)
            {
                RemoveItemsAt(e.OldStartingIndex, e.OldItems.Count);
                return;
            }

            ScheduleUpdateItemsDebounced();
        }, DispatcherPriority.Background);
    }

    private void ScheduleUpdateItemsDebounced()
    {
        _updateItemsDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _updateItemsDebounceTimer.Stop();
        _updateItemsDebounceTimer.Tick -= OnUpdateItemsDebounceTick;
        _updateItemsDebounceTimer.Tick += OnUpdateItemsDebounceTick;
        _updateItemsDebounceTimer.Start();
    }

    private void OnUpdateItemsDebounceTick(object? sender, EventArgs e)
    {
        _updateItemsDebounceTimer?.Stop();
        SyncItemsSourceLightweight(_subscribedItemsSource ?? ItemsSource);
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_coverLoadingActive)
                return;

            string? bitmapProp = _resolvedBitmapProperty;
            string? fileProp = _resolvedFileProperty;
            string? titleProp = _resolvedTitleProperty;
            if (sender == null)
                return;

            if (e.PropertyName == titleProp || e.PropertyName == nameof(MediaItem.Title))
            {
                RebakeDisplayImageForItem(sender);
                return;
            }

            if (e.PropertyName == nameof(MediaItem.IsLoadingCover))
            {
                if (!_itemIndices.TryGetValue(sender, out var loadingIdx) || !IsCurrentSnapshotItem(sender, loadingIdx))
                    return;

                HandleLoadingCoverStateChanged(sender, loadingIdx);
                return;
            }

            if (e.PropertyName != bitmapProp && e.PropertyName != fileProp && e.PropertyName != "CoverFound")
                return;

            if (!_itemIndices.TryGetValue(sender, out var idx) || !IsCurrentSnapshotItem(sender, idx))
                return;

            if (e.PropertyName == "CoverFound")
            {
                if (sender is MediaItem { CoverFound: var found })
                    _visual?.SendHandlerMessage(new UpdateCoverFoundMessage(idx, found));

                if (sender is MediaItem { CoverFound: true })
                {
                    _pendingCoverImageReloads[sender] = idx;
                    ProcessPendingCoverImageReloads();
                }

                return;
            }

            _pendingCoverImageReloads[sender] = idx;
            ProcessPendingCoverImageReloads();
            if (!HasDisplayedCover(idx))
                ScheduleCoverImageAtIndex(idx);
            else if (SlotNeedsCoverLoad(idx))
                ScheduleCoverImageAtIndex(idx);
        }, DispatcherPriority.Background);
    }

    private void ProcessPendingCoverImageReloads()
    {
        if (!_coverLoadingActive || _pendingCoverImageReloads.Count == 0)
            return;

        if (ShouldBlockCoverWork())
            return;

        _coverReloadDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CoverReloadDebounceMs) };
        _coverReloadDebounceTimer.Tick -= CoverReloadDebounceTimer_Tick;
        _coverReloadDebounceTimer.Tick += CoverReloadDebounceTimer_Tick;
        _coverReloadDebounceTimer.Stop();
        _coverReloadDebounceTimer.Start();
    }

    private void CoverReloadDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _coverReloadDebounceTimer?.Stop();
        if (_pendingCoverImageReloads.Count == 0 || ShouldBlockCoverWork())
            return;

        var pending = SortPendingByVisiblePriority(_pendingCoverImageReloads.ToArray());
        _pendingCoverImageReloads.Clear();
        _ = ReloadCoverImagesBatchAsync(pending);
    }

    private KeyValuePair<object, int>[] SortPendingByVisiblePriority(KeyValuePair<object, int>[] pending)
    {
        if (pending.Length <= 1 || Bounds.Height <= 0 || _images.Count == 0)
            return pending;

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            LayoutItemCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        int center = GetViewportCenterIndex();

        return pending
            .OrderBy(pair => Math.Abs(pair.Value - center))
            .ToArray();
    }

    private async Task ReloadCoverImagesBatchAsync(KeyValuePair<object, int>[] pending)
    {
        try
        {
            var requests = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateResolvedCoverPropertyNames();
                var batch = new List<CoverDecodeRequest>(pending.Length);
                foreach (var (sender, idx) in pending)
                {
                    if (idx < 0 || idx >= _itemsSnapshot.Length || !ReferenceEquals(_itemsSnapshot[idx], sender))
                        continue;

                    if (!TryCreateCoverDecodeRequest(idx, out var request))
                        continue;

                    batch.Add(request);
                }

                return batch.ToArray();
            }, DispatcherPriority.Background);

            await ReloadCoverImagesBatchAsyncCore(requests).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("Card grid cover image reload failed", ex);
        }
    }

    private async Task ReloadCoverImagesBatchAsyncCore(CoverDecodeRequest[] pending)
    {
        foreach (var request in pending)
        {
            if (!IsCoverDecodeRequestCurrent(request))
                continue;

            var sender = request.Item;
            var idx = request.Index;
            var bitmapValue = request.BitmapValue;
            var fileName = request.FileName;
            var sourceKey = request.SourceKey;
            var sectionPlaceholder = request.SectionPlaceholder;

            bool forceReload = false;
            if (_itemImageSourceKeys.TryGetValue(sender, out var existingSourceKey) && Equals(existingSourceKey, sourceKey))
            {
                var skipReload = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCoverDecodeRequestCurrent(request))
                        return true;

                    if (CompositionCoverImageHelper.ShouldReloadCachedCover(
                            sender as MediaItem, bitmapValue, fileName, sectionPlaceholder))
                    {
                        ReleaseItemImage(sender);
                        return false;
                    }

                    if (!IsDisplayedCoverCurrent(sender, idx, sourceKey) &&
                        _imageCache.TryGetValue(sender, out var cachedImage))
                    {
                        AssignItemImage(sender, idx, cachedImage, sourceKey);
                        return true;
                    }

                    if (IsDisplayedCoverCurrent(sender, idx, sourceKey))
                    {
                        TouchCacheItem(sender);
                        return true;
                    }

                    TouchCacheItem(sender);
                    return true;
                });

                if (skipReload)
                    continue;

                forceReload = true;
            }

            if (!forceReload && TryAcquireSharedImage(sourceKey, out var sharedImage))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCoverDecodeRequestCurrent(request))
                        AssignItemImage(sender, idx, sharedImage!, sourceKey);
                });
                continue;
            }

            SKImage? realImage = null;
            try
            {
                realImage = await LoadImageAsync(bitmapValue, fileName, sender as MediaItem, CancellationToken.None, sectionPlaceholder);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to reload cover image for item at index {idx}", ex);
            }

            if (realImage != null)
            {
                var imageToUse = forceReload
                    ? RegisterReloadedSharedImage(sourceKey, realImage)
                    : RegisterSharedImage(sourceKey, realImage);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCoverDecodeRequestCurrent(request))
                        AssignItemImage(sender, idx, imageToUse, sourceKey);
                });
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCoverDecodeRequestCurrent(request))
                        return;

                    ReleaseItemImage(sender);
                    if (idx < _images.Count)
                        _images[idx] = null;
                    _visual?.SendHandlerMessage(new UpdateImageMessage(idx, null, ClearImage: true));
                    SetLoading(idx, ShouldShowLoadingSpinner(idx));
                });
            }
        }
    }

    private bool IsCoverDecodeRequestCurrent(CoverDecodeRequest request) =>
        _coverLoadingActive &&
        request.CoverLoadGeneration == _coverLoadGeneration &&
        request.ViewportLoadGeneration == _viewportLoadGeneration &&
        IsCurrentSnapshotItem(request.Item, request.Index);

    private void HandleLoadingCoverStateChanged(object sender, int index)
    {
        if (!IsIndexNearVisibleRange(index))
            return;

        var isLoading = sender is MediaItem { IsLoadingCover: true };
        if (isLoading)
        {
            if (!HasDisplayedCover(index))
            {
                SetLoading(index, true);
                ScheduleCoverImageAtIndex(index);
            }

            return;
        }

        if (ShouldBlockCoverWork())
        {
            DeferCoverLoad(index);
            return;
        }

        ScheduleCoverImageAtIndex(index);
        _pendingCoverImageReloads[sender] = index;
        ProcessPendingCoverImageReloads();
    }

    private bool IsFastScrollInMotion() =>
        _isPointerScrolling ||
        (_isWheelScrolling && Math.Abs(_animationSync.VelocityY) > FastWheelScrollVelocityThreshold) ||
        Math.Abs(_animationSync.VelocityY) > FastScrollVelocityThreshold;

    private bool IsScrollInMotion() =>
        IsFastScrollInMotion() ||
        Math.Abs(_animationSync.VelocityY) > 3 ||
        Math.Abs(_animationSync.TargetScrollY - _animationSync.CurrentScrollY) > 1.5;

    private bool ShouldSuspendInteraction()
    {
        if (_userInteractionUnlock)
            return false;

        if (_isPointerScrolling)
            return true;

        if (Math.Abs(_animationSync.TargetScrollY - _animationSync.CurrentScrollY) < 0.75 &&
            Math.Abs(_animationSync.VelocityY) < InteractionSuspendVelocityThreshold)
            return false;

        if (_isWheelScrolling && Math.Abs(_animationSync.VelocityY) > InteractionSuspendWheelVelocityThreshold)
            return true;

        return Math.Abs(_animationSync.VelocityY) > InteractionSuspendVelocityThreshold;
    }

    private bool ShouldBlockCoverWork() =>
        _coverLoadSuspended ||
        IsFastScrollInMotion() ||
        _shellUpdateGeneration != _completedShellUpdateGeneration;

    private bool IsIndexNearVisibleRange(int index)
    {
        if (index < 0 || _images.Count == 0)
            return true;

        var layout = GetVisibilityLayoutSnapshot();
        if (layout.Height <= 0)
            return true;

        const int buffer = 8;
        var visibleIndices = EnumerateViewportVisibleIndices(layout, rowBuffer: buffer).ToArray();
        if (visibleIndices.Length > 0)
            return visibleIndices.Contains(index);

        var (visibleStart, visibleEnd) = GetViewportIndexRange(layout);
        if (visibleStart < 0 || visibleEnd < visibleStart)
            return true;

        return index >= visibleStart - buffer && index <= visibleEnd + buffer;
    }

    private void UpdateVirtualization()
    {
        if (!_coverLoadingActive)
            return;

        QueueVirtualization();
    }

    private void QueueVirtualization()
    {
        if (_itemsSnapshot.Length == 0 || Bounds.Height <= 0)
            return;

        var metrics = GetLayoutMetrics();
        double scrollY = _knownScrollY;
        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            scrollY,
            (float)Bounds.Height,
            LayoutItemCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        int centerIdx = visibleStart >= 0 && visibleEnd >= visibleStart
            ? GetViewportCenterIndex()
            : (int)Math.Round(SelectedIndex);

        bool scrollMoved = double.IsNaN(_lastVirtualizationScrollY) ||
                           Math.Abs(scrollY - _lastVirtualizationScrollY) > Math.Max(48, metrics.CardHeight * 0.55f);
        bool rangeMoved = centerIdx != _lastVirtualizationIndex;

        if (!scrollMoved && !rangeMoved)
            return;

        _lastVirtualizationIndex = centerIdx;
        _lastVirtualizationScrollY = scrollY;
        CompositionViewportState.VisibleCenterIndex = centerIdx;
        CompositionViewportState.SetVisibleIndices(EnumerateViewportVisibleIndices().ToArray());
        if (IsFastScrollInMotion())
            ScheduleScrollSettleFill();
        else
            BeginViewportLoadChain();
    }

    private void CancelViewportLoadChain()
    {
        _viewportLoadGeneration++;
        _viewportLoadChainActive = false;
        _scrollSettleTimer?.Stop();
        try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling grid load on scroll start", ex); }
        _loadCts = null;
    }

    private void BeginViewportLoadChain(bool restart = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(() => BeginViewportLoadChain(restart), DispatcherPriority.Background);
            return;
        }

        if (ShouldBlockCoverWork())
            return;

        if (_itemsSnapshot.Length == 0)
            return;

        if (Bounds.Height <= 0)
        {
            _pendingViewportLoadAfterLayout = true;
            return;
        }

        _pendingViewportLoadAfterLayout = false;

        if (!restart && _viewportLoadChainActive && _loadCts is { IsCancellationRequested: false })
            return;

        if (restart)
            CancelViewportLoadChain();

        _loadCts = new CancellationTokenSource();
        _viewportLoadChainActive = true;
        int generation = _viewportLoadGeneration;
        var token = _loadCts.Token;
        _ = RunViewportLoadBatchAsync(generation, token);
    }

    private (int Start, int End, int VisibleStart, int VisibleEnd) GetLoadIndexRange()
    {
        var (visibleStart, visibleEnd) = GetViewportIndexRange();
        int count = _itemsSnapshot.Length;
        if (visibleStart < 0 || visibleEnd < visibleStart || count == 0)
            return (visibleStart, visibleEnd, visibleStart, visibleEnd);

        bool fastScroll = IsFastScrollInMotion();
        int buffer = fastScroll ? 1 : IdleVisibleLoadBuffer;
        int leadExtra = fastScroll ? 0 : (int)DirectionalPrefetchLeadRows;
        double velocity = _animationSync.VelocityY;

        int loadStart;
        int loadEnd;
        if (velocity > 25)
        {
            loadStart = Math.Max(0, visibleStart - Math.Max(2, buffer / 2));
            loadEnd = Math.Min(count - 1, visibleEnd + buffer + leadExtra);
        }
        else if (velocity < -25)
        {
            loadStart = Math.Max(0, visibleStart - buffer - leadExtra);
            loadEnd = Math.Min(count - 1, visibleEnd + Math.Max(2, buffer / 2));
        }
        else
        {
            loadStart = Math.Max(0, visibleStart - buffer);
            loadEnd = Math.Min(count - 1, visibleEnd + buffer);
        }

        return (loadStart, loadEnd, visibleStart, visibleEnd);
    }

    private async Task RunViewportLoadBatchAsync(int generation, CancellationToken ct)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(() => _ = RunViewportLoadBatchAsync(generation, ct), DispatcherPriority.Background);
            return;
        }

        try
        {
            if (generation != _viewportLoadGeneration || ct.IsCancellationRequested || ShouldBlockCoverWork())
            {
                _viewportLoadChainActive = false;
                return;
            }

            UpdateResolvedCoverPropertyNames();
            var (loadStart, loadEnd, visibleStart, visibleEnd) = GetLoadIndexRange();
            if (loadStart < 0 || loadEnd < loadStart)
            {
                _viewportLoadChainActive = false;
                return;
            }

            int center = visibleStart >= 0 && visibleEnd >= visibleStart
                ? GetViewportCenterIndex()
                : (loadStart + loadEnd) / 2;
            int batchLimit = IsFastScrollInMotion() ? ViewportLoadBatchSizeFastScroll : ViewportLoadBatchSize;
            var visibleIndices = EnumerateViewportVisibleIndices().ToArray();
            var batch = new List<CoverDecodeRequest>(batchLimit);
            foreach (int index in BuildPrioritizedLoadOrder(loadStart, loadEnd, visibleIndices, center))
            {
                if (!TryCreateCoverDecodeRequest(index, out var request))
                    continue;

                batch.Add(request);
                if (batch.Count >= batchLimit)
                    break;
            }

            if (batch.Count == 0)
            {
                _viewportLoadChainActive = false;
                if (HasVisibleEmptyCoverSlots())
                    ScheduleViewportLoadRetry(_coverLoadGeneration);
                return;
            }

            var loadTasks = new List<Task>(batch.Count);
            foreach (var request in batch)
            {
                if (generation != _viewportLoadGeneration || ct.IsCancellationRequested || ShouldBlockCoverWork())
                {
                    _viewportLoadChainActive = false;
                    return;
                }

                loadTasks.Add(TryLoadDecodeRequestAsync(request, ct));
            }

            await Task.WhenAll(loadTasks).ConfigureAwait(false);

            var capturedVisibleIndices = visibleIndices;
            PostToUi(
                () => _ = ContinueViewportLoadBatchAsync(generation, ct, capturedVisibleIndices, loadStart, loadEnd),
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            _viewportLoadChainActive = false;
        }
        catch (Exception ex)
        {
            _viewportLoadChainActive = false;
            Log.Warn("Viewport cover fill batch failed", ex);
        }
    }

    private async Task ContinueViewportLoadBatchAsync(
        int generation,
        CancellationToken ct,
        int[] visibleIndices,
        int loadStart,
        int loadEnd)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(
                () => _ = ContinueViewportLoadBatchAsync(generation, ct, visibleIndices, loadStart, loadEnd),
                DispatcherPriority.Background);
            return;
        }

        try
        {
            if (generation != _viewportLoadGeneration || ct.IsCancellationRequested)
            {
                _viewportLoadChainActive = false;
                return;
            }

            if (ShouldBlockCoverWork())
            {
                PostToUi(
                    () => _ = ContinueViewportLoadBatchAsync(generation, ct, visibleIndices, loadStart, loadEnd),
                    DispatcherPriority.Background);
                return;
            }

            bool moreWork = false;
            if (visibleIndices.Length > 0)
            {
                foreach (int i in visibleIndices)
                {
                    if (!SlotNeedsCoverLoad(i))
                        continue;

                    moreWork = true;
                    break;
                }
            }
            else
            {
                for (int i = loadStart; i <= loadEnd; i++)
                {
                    if (!SlotNeedsCoverLoad(i))
                        continue;

                    moreWork = true;
                    break;
                }
            }

            if (!moreWork)
            {
                _viewportLoadChainActive = false;
                return;
            }

            int frameDelay = IsFastScrollInMotion() ? ViewportLoadFrameMsFastScroll : ViewportLoadFrameMs;
            await Task.Delay(frameDelay, ct).ConfigureAwait(false);
            if (generation != _viewportLoadGeneration || ct.IsCancellationRequested || ShouldBlockCoverWork())
            {
                _viewportLoadChainActive = false;
                return;
            }

            PostToUi(() => _ = RunViewportLoadBatchAsync(generation, ct), DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            _viewportLoadChainActive = false;
        }
        catch (Exception ex)
        {
            _viewportLoadChainActive = false;
            Log.Warn("Viewport cover fill continuation failed", ex);
        }
    }

    private static IEnumerable<int> BuildPrioritizedLoadOrder(
        int loadStart,
        int loadEnd,
        IReadOnlyList<int> visibleIndices,
        int centerIdx)
    {
        if (visibleIndices.Count > 0)
        {
            var visibleSet = new HashSet<int>(visibleIndices);
            foreach (int index in BuildCenterOutLoadOrderFromList(visibleIndices, centerIdx))
                yield return index;

            for (int index = loadStart; index <= loadEnd; index++)
            {
                if (!visibleSet.Contains(index))
                    yield return index;
            }

            yield break;
        }

        foreach (int index in BuildCenterOutLoadOrder(loadStart, loadEnd, centerIdx))
            yield return index;
    }

    private static List<int> BuildCenterOutLoadOrderFromList(IReadOnlyList<int> indices, int centerIdx)
    {
        var order = new List<int>(indices.Count);
        if (indices.Count == 0)
            return order;

        int centerPos = 0;
        int minDistance = int.MaxValue;
        for (int i = 0; i < indices.Count; i++)
        {
            int distance = Math.Abs(indices[i] - centerIdx);
            if (distance < minDistance)
            {
                minDistance = distance;
                centerPos = i;
            }
        }

        order.Add(indices[centerPos]);
        for (int offset = 1; order.Count < indices.Count; offset++)
        {
            int before = centerPos - offset;
            int after = centerPos + offset;
            if (before >= 0)
                order.Add(indices[before]);
            if (after < indices.Count)
                order.Add(indices[after]);
        }

        return order;
    }

    private void ScheduleIdleCacheTrim()
    {
        _idleCacheTrimTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleCacheTrimMs) };
        _idleCacheTrimTimer.Tick -= IdleCacheTrimTimer_Tick;
        _idleCacheTrimTimer.Tick += IdleCacheTrimTimer_Tick;
        _idleCacheTrimTimer.Stop();
        _idleCacheTrimTimer.Start();
    }

    private void IdleCacheTrimTimer_Tick(object? sender, EventArgs e)
    {
        _idleCacheTrimTimer?.Stop();
        if (IsScrollInMotion() || _itemsSnapshot.Length == 0)
        {
            ScheduleIdleCacheTrim();
            return;
        }

        TrimIdleImageCaches();
        TrimImageCacheToViewport();
    }

    private void TrimImageCacheToViewport()
    {
        if (_itemsSnapshot.Length <= 256)
            return;

        var itemToIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _itemsSnapshot.Length; i++)
        {
            if (_itemsSnapshot[i] != null)
                itemToIndex[_itemsSnapshot[i]!] = i;
        }

        var (visibleStart, visibleEnd) = GetViewportIndexRange();
        if (visibleStart < 0)
            return;

        int retainStart = Math.Max(0, visibleStart - RetainBuffer);
        int retainEnd = Math.Min(_itemsSnapshot.Length - 1, visibleEnd + RetainBuffer);
        var evictKeys = new List<(object Key, int CachedIndex)>();
        foreach (var key in _imageCache.Keys.ToList())
        {
            if (!itemToIndex.TryGetValue(key, out var cachedIndex) || cachedIndex < retainStart || cachedIndex > retainEnd)
                evictKeys.Add((key, cachedIndex));
        }

        foreach (var (key, _) in evictKeys)
            EvictRawImageCacheEntry(key);
    }

    private void EvictRawImageCacheEntry(object key)
    {
        if (!_imageCache.Remove(key))
        {
            RemoveCacheNode(key);
            _itemImageSourceKeys.Remove(key);
            _itemDisplayCacheKeys.Remove(key);
            return;
        }

        RemoveCacheNode(key);
        if (_itemImageSourceKeys.Remove(key, out var sourceKey) && sourceKey != null)
            SharedCoverCache.Release(sourceKey, QueueNativeImageDisposal);
        _itemDisplayCacheKeys.Remove(key);
    }

    private void FlushDeferredAssigns()
    {
        if (_deferredAssigns.Count == 0)
            return;

        var pending = _deferredAssigns.ToArray();
        _deferredAssigns.Clear();
        foreach (var (index, assign) in pending)
            AssignItemImageCore(assign.Item, index, assign.Source, assign.Display, assign.SourceKey);
    }

    private void QueueDeferredAssign(int index, object item, SKImage sourceImage, SKImage displayImage, object? sourceKey)
    {
        if (_deferredAssigns.TryGetValue(index, out var previous) &&
            !ReferenceEquals(previous.Display, displayImage))
        {
            if (previous.DisplayCacheKey != null)
                ReleaseDisplayImage(previous.DisplayCacheKey, previous.Display);
        }

        object? displayCacheKey = sourceKey != null && !IsPlaceholderSourceKey(sourceKey)
            ? CreateDisplayCacheKey(sourceKey, GetItemTitle(item))
            : null;
        _deferredAssigns[index] = new PendingAssign(item, sourceImage, displayImage, sourceKey, displayCacheKey);
    }

    private async Task VirtualizeAsync(int centerIdx, VirtualizationLayoutSnapshot layout, CoverImageLoadContext context, CancellationToken ct) =>
        await VirtualizeAsyncCore(centerIdx, layout, context, ct).ConfigureAwait(false);

    private async Task VirtualizeAsyncCore(int centerIdx, VirtualizationLayoutSnapshot layout, CoverImageLoadContext context, CancellationToken ct)
    {
        if (!_coverLoadingActive || _subscribedItemsSource == null)
            return;

        var bitmapProp = context.BitmapProperty;
        var fileProp = context.FileProperty;
        var sectionPlaceholder = context.SectionPlaceholder;
        var items = _itemsSnapshot;
        int totalCount = items.Length;
        if (totalCount == 0)
            return;

        var itemToIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < totalCount; k++)
        {
            var val = items[k];
            if (val != null)
                itemToIndex[val] = k;
        }

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            layout.ScrollY,
            layout.Height,
            totalCount,
            layout.Width,
            layout.CardScale,
            layout.CardSpacing,
            layout.TopPadding,
            layout.HorizontalScrollEnabled);

        var visibleIndices = CardGridLayoutHelper.EnumerateVisibleIndices(
            layout.ScrollY,
            layout.Height,
            totalCount,
            layout.Width,
            layout.CardScale,
            layout.CardSpacing,
            layout.TopPadding,
            layout.HorizontalScrollEnabled).ToArray();
        var visibleSet = visibleIndices.Length > 0 ? new HashSet<int>(visibleIndices) : null;

        int loadBuffer = layout.ScrollInMotion ? 0 : IdleVisibleLoadBuffer;
        int loadStart = Math.Max(0, visibleStart - loadBuffer);
        int loadEnd = visibleStart >= 0 && visibleEnd >= visibleStart
            ? Math.Min(totalCount - 1, visibleEnd + loadBuffer)
            : Math.Min(totalCount - 1, centerIdx + IdleVisibleLoadBuffer);
        int retainStart = Math.Max(0, loadStart - RetainBuffer);
        int retainEnd = Math.Min(totalCount - 1, loadEnd + RetainBuffer);
        int maxLoadsThisPass = layout.ScrollInMotion ? VisibleLoadBatchSize : IdleVisibleLoadBatchSize;

        var evictKeys = new List<(object Key, int CachedIndex)>();
        foreach (var key in _imageCache.Keys.ToList())
        {
            if (ct.IsCancellationRequested)
                return;
            if (!itemToIndex.TryGetValue(key, out var cachedIndex) || cachedIndex < retainStart || cachedIndex > retainEnd)
                evictKeys.Add((key, cachedIndex));
        }

        if (evictKeys.Count > 0 && !layout.ScrollInMotion && totalCount > 256)
        {
            var keysToEvict = evictKeys;
            PostToUi(() =>
            {
                foreach (var (key, _) in keysToEvict)
                    EvictRawImageCacheEntry(key);
            }, DispatcherPriority.Background);
        }

        EnsureDiskCacheDirectory();

        int center = visibleIndices.Length > 0
            ? CardGridLayoutHelper.EstimateViewportCenterIndex(
                layout.ScrollY,
                layout.Height,
                totalCount,
                layout.Width,
                layout.CardScale,
                layout.CardSpacing,
                layout.TopPadding,
                layout.HorizontalScrollEnabled)
            : visibleStart >= 0 && visibleEnd >= visibleStart
                ? Math.Clamp(centerIdx, visibleStart, visibleEnd)
                : Math.Clamp(centerIdx, 0, totalCount - 1);
        IEnumerable<int> loadOrder = visibleIndices.Length > 0
            ? BuildPrioritizedLoadOrder(loadStart, loadEnd, visibleIndices, center)
            : BuildCenterOutLoadOrder(loadStart, loadEnd, center);
        int loadedThisPass = 0;
        bool hasMoreVisibleWork = false;

        foreach (int index in loadOrder)
        {
            if (ct.IsCancellationRequested)
                return;

            bool isVisible = visibleSet == null || visibleSet.Contains(index);
            if (layout.ScrollInMotion && visibleSet != null && !isVisible)
                continue;

            if (loadedThisPass >= maxLoadsThisPass)
            {
                if (isVisible)
                    hasMoreVisibleWork = true;
                continue;
            }

            bool started = await TryLoadItemAsync(index, items, bitmapProp, fileProp, sectionPlaceholder, ct).ConfigureAwait(false);
            if (started)
                loadedThisPass++;
            else if (isVisible &&
                     index < _images.Count &&
                     (_images[index] == null || IsPlaceholderImage(_images[index])))
            {
                hasMoreVisibleWork = true;
            }

            if (!layout.ScrollInMotion && visibleSet != null && !isVisible)
                await Task.Yield();
        }

        if (hasMoreVisibleWork && !ct.IsCancellationRequested)
        {
            PostToUi(() =>
            {
                if (!_coverLoadingActive || ShouldBlockCoverWork())
                    return;

                QueueVirtualization();
            }, DispatcherPriority.Background);
        }

        PostToUi(() => TrimImageCache(itemToIndex), DispatcherPriority.SystemIdle);
    }

    private async Task<bool> TryLoadDecodeRequestAsync(CoverDecodeRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !IsCoverDecodeRequestCurrent(request))
            return false;

        if (!TryMarkCoverLoadInFlight(request.Index))
            return false;

        try
        {
            return await TryLoadDecodeRequestCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            ClearCoverLoadInFlight(request.Index);
            PostToUi(() => RefreshLoadingSpinnerAt(request.Index), DispatcherPriority.Background);
        }
    }

    private async Task<bool> TryLoadDecodeRequestCoreAsync(CoverDecodeRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !IsCoverDecodeRequestCurrent(request))
            return false;

        int index = request.Index;
        object item = request.Item;
        object? sourceKey = request.SourceKey;
        var bitmapValue = request.BitmapValue;
        var fileName = request.FileName;
        var sectionPlaceholder = request.SectionPlaceholder;
        string? title = request.Title;

        if (_imageCache.TryGetValue(item, out var cachedImage))
        {
            bool hasMatchingSource = _itemImageSourceKeys.TryGetValue(item, out var existingSourceKey) &&
                                     Equals(existingSourceKey, sourceKey);
            if (hasMatchingSource)
            {
                if (CompositionCoverImageHelper.ShouldReloadCachedCover(
                        item as MediaItem, bitmapValue, fileName, sectionPlaceholder))
                {
                    PostToUi(() => ReleaseItemImage(item));
                }
                else
                {
                    TouchCacheItem(item);
                    if (!ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request))
                    {
                        var displayImage = await ResolveDisplayImageAsync(
                            cachedImage, sourceKey, title, ct, request.CoverLoadGeneration).ConfigureAwait(false);
                        if (!ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request))
                            AssignItemImage(item, index, cachedImage, displayImage, sourceKey);
                    }

                    return !ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request);
                }
            }
            else
            {
                PostToUi(() => ReleaseItemImage(item));
            }
        }

        if (!IsCoverDecodeRequestCurrent(request))
            return false;

        PostToUi(() => SetLoading(index, true));

        if (!IsPlaceholderSourceKey(sourceKey) && TryAcquireSharedImage(sourceKey, out var sharedImage))
        {
            if (!ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request))
            {
                var displayImage = await ResolveDisplayImageAsync(
                    sharedImage!, sourceKey, title, ct, request.CoverLoadGeneration).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request))
                    AssignItemImage(item, index, sharedImage!, displayImage, sourceKey);
            }

            return true;
        }

        SKImage? realImage = null;
        try
        {
            realImage = await LoadImageAsync(bitmapValue, fileName, item as MediaItem, ct, sectionPlaceholder)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load image for item at index {index}", ex);
        }

        if (!IsCoverDecodeRequestCurrent(request) || ct.IsCancellationRequested)
            return false;

        if (realImage != null && !IsPlaceholderImage(realImage))
        {
            var sourceImage = RegisterSharedImage(sourceKey, realImage);
            var displayImage = await ResolveDisplayImageAsync(
                sourceImage, sourceKey, title, ct, request.CoverLoadGeneration).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && IsCoverDecodeRequestCurrent(request))
                AssignItemImage(item, index, sourceImage, displayImage, sourceKey);
        }
        else
        {
            PostToUi(() =>
            {
                if (!IsCoverDecodeRequestCurrent(request))
                    return;

                SetLoading(index, ShouldShowLoadingSpinner(index));
                ScheduleCoverImageRetry(index, _coverLoadGeneration);
            });
        }

        return true;
    }

    private async Task<bool> TryLoadItemAsync(
        int index,
        object?[] items,
        string bitmapProp,
        string fileProp,
        Bitmap? sectionPlaceholder,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || index < 0 || index >= items.Length || items[index] == null)
            return false;

        CoverDecodeRequest? request = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsCurrentSnapshotItem(items[index]!, index))
                return (CoverDecodeRequest?)null;

            if (HasDisplayedCover(index))
                return null;

            return TryCreateCoverDecodeRequest(index, out var req) ? req : null;
        }, DispatcherPriority.Background);

        if (request == null)
            return false;

        return await TryLoadDecodeRequestAsync(request.Value, ct).ConfigureAwait(false);
    }

    private void PostToUi(Action action) => PostToUi(action, DispatcherPriority.Normal);

    private void PostToUi(Action action, DispatcherPriority priority)
    {
        Dispatcher.UIThread.Post(action, priority);
    }

    private void SendVisualMessage(object message) =>
        PostToUi(() => _visual?.SendHandlerMessage(message), DispatcherPriority.Background);

        private void SetLoading(int index, bool isLoading)
        {
            if (isLoading && (HasDisplayedCover(index) || ShouldBlockCoverWork()))
                return;

            void Apply()
            {
                if (index < 0 || index >= _itemsSnapshot.Length)
                    return;

                if (isLoading)
                {
                    _visual?.SendHandlerMessage(new UpdateImageMessage(index, null, ClearImage: true, IsLoading: true));
                    return;
                }

                _visual?.SendHandlerMessage(new UpdateImageMessage(index, null, IsLoading: false));
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                PostToUi(Apply, DispatcherPriority.Background);
        }

    private async Task<SKImage> ResolveDisplayImageAsync(
        SKImage sourceImage,
        object? sourceKey,
        string? title,
        CancellationToken ct,
        int coverLoadGeneration = -1)
    {
        if (!_coverLoadingActive)
            return sourceImage;

        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
            return sourceImage;

        if (coverLoadGeneration < 0)
            coverLoadGeneration = _coverLoadGeneration;

        var displayKey = CreateDisplayCacheKey(sourceKey, title);
        if (CardDisplayCache.TryPeek(displayKey, out var cached))
            return cached;

        SKImage bakeSourceCopy;
        try
        {
            bakeSourceCopy = Dispatcher.UIThread.CheckAccess()
                ? CloneImageForBake(sourceImage)
                : await Dispatcher.UIThread.InvokeAsync(() => CloneImageForBake(sourceImage), DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to clone cover image for display bake", ex);
            if (CardDisplayCache.TryPeek(displayKey, out var fallback))
                return fallback;
            return sourceImage;
        }

        return await ResolveDisplayImageFromCloneAsync(bakeSourceCopy, displayKey, title, ct, coverLoadGeneration).ConfigureAwait(false);
    }

    private async Task<SKImage> ResolveDisplayImageFromCloneAsync(
        SKImage bakeSourceCopy,
        object displayKey,
        string? title,
        CancellationToken ct,
        int coverLoadGeneration)
    {
        if (CardDisplayCache.TryPeek(displayKey, out var cached))
        {
            QueueNativeImageDisposal(bakeSourceCopy);
            return cached;
        }

        Task<SKImage> bakeTask;
        int bakeGeneration = _coverLoadGeneration;
        lock (_displayBakeSync)
        {
            if (_displayBakeTasks.TryGetValue(displayKey, out bakeTask!))
            {
                QueueNativeImageDisposal(bakeSourceCopy);
            }
            else
            {
                var copy = bakeSourceCopy;
                bakeTask = Task.Run(() =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        return CompositionCardDisplayBaker.Bake(copy, title);
                    }
                    finally
                    {
                        QueueNativeImageDisposal(copy);
                    }
                }, ct);
                _displayBakeTasks[displayKey] = bakeTask;
            }
        }

        try
        {
            var baked = await bakeTask.ConfigureAwait(false);
            if (ct.IsCancellationRequested || coverLoadGeneration != _coverLoadGeneration || bakeGeneration != _coverLoadGeneration)
            {
                QueueNativeImageDisposal(baked);
                throw new OperationCanceledException(ct);
            }

            if (CardDisplayCache.TryPeek(displayKey, out var alreadyRegistered))
            {
                if (!ReferenceEquals(alreadyRegistered, baked))
                    QueueNativeImageDisposal(baked);
                return alreadyRegistered;
            }

            var registered = CardDisplayCache.RegisterUnretained(displayKey, baked);
            if (!ReferenceEquals(registered, baked))
            {
                QueueNativeImageDisposal(baked);
            }
            else
            {
                lock (_pendingDisplayCacheAssignKeys)
                    _pendingDisplayCacheAssignKeys.Add(displayKey);
            }

            return registered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (CardDisplayCache.TryPeek(displayKey, out var fallback))
                return fallback;
            throw;
        }
        finally
        {
            lock (_displayBakeSync)
                _displayBakeTasks.Remove(displayKey);
        }
    }

    private static SKImage CloneImageForBake(SKImage source)
    {
        using var bitmap = SKBitmap.FromImage(source);
        return SKImage.FromBitmap(bitmap);
    }

    private bool TryAcquireCachedDisplayImage(SKImage sourceImage, object? sourceKey, string? title, out SKImage displayImage)
    {
        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
        {
            displayImage = sourceImage;
            return true;
        }

        var displayKey = CreateDisplayCacheKey(sourceKey, title);
        if (CardDisplayCache.TryPeek(displayKey, out displayImage))
            return true;

        displayImage = null!;
        return false;
    }

    private void ScheduleDisplayImageBake(object item, int index, SKImage sourceImage, object? sourceKey, string? title)
    {
        if (!_coverLoadingActive)
            return;

        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
        {
            AssignItemImage(item, index, sourceImage, sourceImage, sourceKey);
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            PostToUi(() => ScheduleDisplayImageBake(item, index, sourceImage, sourceKey, title), GetCoverAssignPriority());
            return;
        }

        SKImage? bakeSourceCopy;
        try
        {
            bakeSourceCopy = CloneImageForBake(sourceImage);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to clone cover for display bake at index {index}", ex);
            SetLoading(index, true);
            return;
        }

        int generation = _coverLoadGeneration;
        int viewportGeneration = _viewportLoadGeneration;
        var displayKey = CreateDisplayCacheKey(sourceKey, title);
        SetLoading(index, true);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_coverLoadingActive || generation != _coverLoadGeneration || viewportGeneration != _viewportLoadGeneration)
                    return;

                var ct = _loadCts?.Token ?? CancellationToken.None;
                if (!IsCurrentSnapshotItem(item, index))
                    return;

                var displayImage = await ResolveDisplayImageFromCloneAsync(
                    bakeSourceCopy, displayKey, title, ct, generation).ConfigureAwait(false);
                if (!_coverLoadingActive || generation != _coverLoadGeneration || viewportGeneration != _viewportLoadGeneration || ct.IsCancellationRequested)
                {
                    AbandonPendingDisplayCacheRegistration(displayKey);
                    return;
                }

                if (!IsCurrentSnapshotItem(item, index))
                {
                    AbandonPendingDisplayCacheRegistration(displayKey);
                    return;
                }

                AssignItemImage(item, index, sourceImage, displayImage, sourceKey);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to bake display image for item at index {index}", ex);
            }
        });
    }

    private void AssignItemImage(object item, int index, SKImage sourceImage, object? sourceKey)
    {
        if (!_coverLoadingActive)
        {
            AssignItemImage(item, index, sourceImage, sourceImage, sourceKey);
            return;
        }

        var title = GetItemTitle(item);
        if (TryAcquireCachedDisplayImage(sourceImage, sourceKey, title, out var cachedDisplay))
        {
            AssignItemImage(item, index, sourceImage, cachedDisplay, sourceKey);
            return;
        }

        ScheduleDisplayImageBake(item, index, sourceImage, sourceKey, title);
    }

    private void AssignItemImage(object item, int index, SKImage sourceImage, SKImage displayImage, object? sourceKey)
    {
        if (Dispatcher.UIThread.CheckAccess())
            AssignItemImageCore(item, index, sourceImage, displayImage, sourceKey);
        else
            PostToUi(() => AssignItemImageCore(item, index, sourceImage, displayImage, sourceKey), GetCoverAssignPriority());
    }

    private void AssignItemImageCore(object item, int index, SKImage sourceImage, SKImage displayImage, object? sourceKey)
    {
        if (!IsCurrentSnapshotItem(item, index))
        {
            AbandonPendingDisplayCacheRegistration(sourceKey, GetItemTitle(item));
            return;
        }

        if (ShouldBlockCoverWork())
        {
            QueueDeferredAssign(index, item, sourceImage, displayImage, sourceKey);
            return;
        }

        if (sourceKey is Bitmap sourceBitmap &&
            CompositionCoverImageHelper.IsSectionPlaceholderBitmap(sourceBitmap, _sectionPlaceholderBitmap))
        {
            DisposeImage(sourceImage);
            if (!ReferenceEquals(sourceImage, displayImage))
                DisposeImage(displayImage);
            RefreshLoadingSpinnerAt(index);
            return;
        }

        if (index >= _images.Count)
            EnsureImageSlotCount(index + 1);

        object? previousDisplayKey = _itemDisplayCacheKeys.TryGetValue(item, out var existingDisplayKey)
            ? existingDisplayKey
            : null;

        object? displayCacheKey = sourceKey != null && !IsPlaceholderSourceKey(sourceKey)
            ? CreateDisplayCacheKey(sourceKey, GetItemTitle(item))
            : null;

        _imageCache[item] = sourceImage;
        _itemImageSourceKeys[item] = sourceKey!;
        StoreDisplayCacheKey(item, sourceKey, GetItemTitle(item));
        if (previousDisplayKey != null && !Equals(previousDisplayKey, displayCacheKey))
            CardDisplayCache.Release(previousDisplayKey, QueueNativeImageDisposal);
        RetainDisplayImage(displayCacheKey);
        if (displayCacheKey != null)
        {
            lock (_pendingDisplayCacheAssignKeys)
                _pendingDisplayCacheAssignKeys.Remove(displayCacheKey);
        }

        TouchCacheItem(item);
        _images[index] = displayImage;
        _visual?.SendHandlerMessage(new UpdateImageMessage(index, displayImage));
        SetLoading(index, false);
        UpdateSelectedItemBounds();
    }

    private void ReleaseDisplayImage(object? displayCacheKey, SKImage displayImage)
    {
        if (displayCacheKey != null)
            CardDisplayCache.Release(displayCacheKey, QueueNativeImageDisposal);
    }

    private void FlushDisplayImageCaches()
    {
        var disposedImages = new HashSet<SKImage>(ReferenceEqualityComparer.Instance);
        foreach (var key in _imageCache.Keys.ToList())
            ReleaseItemImage(key, disposedImages);

        _imageCache.Clear();
        _imageCacheNodes.Clear();
        _imageCacheLru.Clear();
        _itemImageSourceKeys.Clear();
        _itemDisplayCacheKeys.Clear();
        CardDisplayCache.Clear(img =>
        {
            if (!IsImageUsedByDisplay(img))
                QueueNativeImageDisposal(img);
        });
    }

    private void ClearResources()
    {
        _pendingVisibleLoad = false;
        _pendingScrollToIndex = -1;
        _initialImageLoadScheduled = false;
        _coverLoadSuspended = false;
        _pendingCoverImageReloads.Clear();
        _knownScrollY = 0;
        var disposedImages = new HashSet<SKImage>(ReferenceEqualityComparer.Instance);
        foreach (var key in _imageCache.Keys.ToList())
            ReleaseItemImage(key, disposedImages);

        _imageCache.Clear();
        _imageCacheNodes.Clear();
        _imageCacheLru.Clear();
        _itemImageSourceKeys.Clear();
        _itemDisplayCacheKeys.Clear();
        ClearCardDisplayCache(deferDispose: false);
        _visual?.SendHandlerMessage(new CardGridRequestFlushDisposalsMessage());
        _sharedPlaceholder = null;
        SetSectionPlaceholderBitmap(null);
        _images.Clear();
        _itemsSnapshot = Array.Empty<object?>();
        _itemIndices.Clear();
        foreach (var item in _subscribedItems)
            item.PropertyChanged -= Item_PropertyChanged;
        _subscribedItems.Clear();
        _subscriptionBatchTimer?.Stop();
        _pendingSubscriptionItems = null;
        _pendingSubscriptionIndex = 0;
        _visual?.SendHandlerMessage(new CardGridSlotCountMessage(0));
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

    private string? GetItemTitle(object? item) => GetTitleValue(item, _resolvedTitleProperty);

    private static object CreateDisplayCacheKey(object sourceKey, string? title) =>
        (sourceKey, title ?? string.Empty);

    private void StoreDisplayCacheKey(object item, object? sourceKey, string? title)
    {
        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
            _itemDisplayCacheKeys.Remove(item);
        else
            _itemDisplayCacheKeys[item] = CreateDisplayCacheKey(sourceKey, title);
    }

    private void RebakeDisplayImageForItem(object item)
    {
        if (!_coverLoadingActive)
            return;

        if (!_itemIndices.TryGetValue(item, out var index) || !IsCurrentSnapshotItem(item, index))
            return;

        if (!_imageCache.TryGetValue(item, out var sourceImage))
            return;

        if (!_itemImageSourceKeys.TryGetValue(item, out var sourceKey))
            return;

        ScheduleDisplayImageBake(item, index, sourceImage, sourceKey, GetItemTitle(item));
    }

    private static Bitmap? GetBitmapValue(object item, string? propertyName) =>
        item switch
        {
            MediaItem mediaItem when string.IsNullOrEmpty(propertyName) ||
                                      string.Equals(propertyName, nameof(MediaItem.CoverBitmap), StringComparison.Ordinal)
                => mediaItem.CoverBitmap,
            _ => null
        };

    private static string? GetFileNameValue(object item, string? propertyName)
    {
        if (item is not MediaItem mediaItem)
            return null;

        if (string.IsNullOrEmpty(propertyName) ||
            string.Equals(propertyName, nameof(MediaItem.LocalCoverPath), StringComparison.Ordinal))
            return mediaItem.LocalCoverPath;

        if (string.Equals(propertyName, nameof(MediaItem.FileName), StringComparison.Ordinal))
            return mediaItem.FileName;

        return null;
    }

    private string? ResolveCoverImagePath(object? item, string? configuredFileProp)
    {
        if (item is not MediaItem mediaItem)
            return null;

        if (!string.IsNullOrWhiteSpace(mediaItem.LocalCoverPath) && File.Exists(mediaItem.LocalCoverPath))
            return mediaItem.LocalCoverPath;

        var configuredPath = GetFileNameValue(mediaItem, configuredFileProp);
        if (CompositionCoverImageHelper.IsLikelyImageFile(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        if (CompositionCoverImageHelper.IsLikelyImageFile(mediaItem.FileName) && File.Exists(mediaItem.FileName))
            return mediaItem.FileName;

        return CompositionMetadataCoverHelper.GetCoverCachePath(mediaItem.FileName)
            ?? CompositionMetadataCoverHelper.GetMetadataCachePath(mediaItem.FileName);
    }

    private static object? GetImageSourceKey(Bitmap? bitmap, string? fileName)
    {
        if (bitmap != null)
            return bitmap;
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;
        return null;
    }

    private SKImage? _sectionPlaceholderSkImage;

    private SKImage ResolveLoadingPlaceholderImage(int index)
    {
        SKImage? sourceImage = null;
        if (index >= 0 && index < _itemsSnapshot.Length &&
            _itemsSnapshot[index] is MediaItem { IsLoadingCover: true, CoverFound: false, CoverBitmap: { } coverBitmap })
        {
            if (_sectionPlaceholderBitmap == null ||
                ReferenceEquals(coverBitmap, _sectionPlaceholderBitmap) ||
                CompositionCoverImageHelper.IsSectionPlaceholderBitmap(coverBitmap, _sectionPlaceholderBitmap))
            {
                sourceImage = GetOrCreateSectionPlaceholderSkImage(coverBitmap);
            }
        }

        sourceImage ??= _sectionPlaceholderBitmap != null
            ? GetOrCreateSectionPlaceholderSkImage(_sectionPlaceholderBitmap)
            : null;
        sourceImage ??= GetPlaceholder();

        var title = index >= 0 && index < _itemsSnapshot.Length
            ? GetItemTitle(_itemsSnapshot[index])
            : null;
        if (string.IsNullOrWhiteSpace(title))
            return sourceImage;

        var displayKey = CreateDisplayCacheKey(sourceImage, title);
        if (CardDisplayCache.TryPeek(displayKey, out var cachedDisplay))
            return cachedDisplay;

        var baked = CompositionCardDisplayBaker.Bake(sourceImage, title);
        var registered = CardDisplayCache.RegisterUnretained(displayKey, baked);
        if (!ReferenceEquals(registered, baked))
            QueueNativeImageDisposal(baked);
        return registered;
    }

    private void SetSectionPlaceholderBitmap(Bitmap? bitmap)
    {
        if (ReferenceEquals(_sectionPlaceholderBitmap, bitmap))
            return;

        var previousSectionImage = _sectionPlaceholderSkImage;
        _sectionPlaceholderBitmap = bitmap;
        _sectionPlaceholderSkImage = null;

        if (previousSectionImage == null)
            return;

        PurgeCachedImage(previousSectionImage);

        for (int i = 0; i < _images.Count; i++)
        {
            if (!ReferenceEquals(_images[i], previousSectionImage))
                continue;

            var replacement = GetPlaceholder();
            _images[i] = replacement;
            _visual?.SendHandlerMessage(new UpdateImageMessage(i, replacement));
        }

        DisposeImage(previousSectionImage);
    }

    private void PurgeCachedImage(SKImage image)
    {
        foreach (var key in _imageCache.Keys.ToList())
        {
            if (!ReferenceEquals(_imageCache[key], image))
                continue;

            _imageCache.Remove(key);
            RemoveCacheNode(key);
            _itemImageSourceKeys.Remove(key);
            _itemDisplayCacheKeys.Remove(key);
        }
    }

    private SKImage GetOrCreateSectionPlaceholderSkImage(Bitmap? sourceBitmap)
    {
        if (_sectionPlaceholderSkImage != null)
            return _sectionPlaceholderSkImage;

        var bitmap = sourceBitmap ?? _sectionPlaceholderBitmap;
        if (bitmap == null)
            return GetPlaceholder();

        try
        {
            _sectionPlaceholderSkImage = CompositionBitmapHelper.ToSkImage(bitmap, CachedCardImageSize);
        }
        catch
        {
            _sectionPlaceholderSkImage = null;
        }

        return _sectionPlaceholderSkImage ?? GetPlaceholder();
    }

    private SKImage GetPlaceholder() => _sharedPlaceholder ??= GeneratePlaceholder();

    private SKImage GeneratePlaceholder()
    {
        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        surface.Canvas.Clear(SKColor.Parse("#1E1E1E"));
        return surface.Snapshot();
    }

    private bool IsPlaceholderImage(SKImage? image) =>
        image == null ||
        ReferenceEquals(image, _sharedPlaceholder) ||
        ReferenceEquals(image, _sectionPlaceholderSkImage);

    private bool IsPlaceholderSourceKey(object? sourceKey) =>
        sourceKey is Bitmap bmp &&
        CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bmp, _sectionPlaceholderBitmap);

    private void RehydrateCoverSlots(bool forceAll, bool replacePlaceholderDisplays = false)
    {
        if (!_coverLoadingActive)
            return;

        string? bitmapProp = ImageBitmapProperty;
        string? fileProp = ImageFileNameProperty;

        IEnumerable<int> indices = forceAll
            ? Enumerable.Range(0, _itemsSnapshot.Length)
            : EnumerateViewportVisibleIndices(rowBuffer: 4);

        if (!forceAll)
        {
            var visible = indices.ToArray();
            if (visible.Length == 0)
            {
                int fallbackEnd = Math.Min(_itemsSnapshot.Length - 1, 17);
                if (fallbackEnd >= 0)
                    visible = Enumerable.Range(0, fallbackEnd + 1).ToArray();
            }

            indices = visible;
        }

        foreach (int i in indices)
        {
            if (i < 0 || i >= _itemsSnapshot.Length)
                continue;

            var item = _itemsSnapshot[i];
            if (item == null)
                continue;

            RehydrateCoverSlotAt(i, item, bitmapProp, fileProp, forceAll, replacePlaceholderDisplays);
        }
    }

    private void RehydrateCoverSlotAt(
        int i,
        object item,
        string? bitmapProp,
        string? fileProp,
        bool forceAll,
        bool replacePlaceholderDisplays)
    {
        if (!forceAll && i < _images.Count && _images[i] != null && !IsPlaceholderImage(_images[i]))
        {
            _itemImageSourceKeys.TryGetValue(item, out var existingKey);
            var existingIsPlaceholder = IsPlaceholderSourceKey(existingKey);

            if (!replacePlaceholderDisplays || !existingIsPlaceholder)
            {
                bool hasRawSource = _imageCache.TryGetValue(item, out var cachedSource) &&
                                    ReferenceEquals(_images[i], cachedSource);
                if (!hasRawSource &&
                    existingKey != null &&
                    IsDisplayedCoverCurrent(item, i, existingKey))
                {
                    return;
                }

                if (hasRawSource)
                    return;
            }
        }

        if (forceAll && i < _images.Count && _images[i] != null && !IsPlaceholderImage(_images[i]))
        {
            _images[i] = null;
            ReleaseItemImage(item);
        }

        if (!TryRestoreDisplayImage(item, bitmapProp, fileProp, out var restored) || restored == null)
        {
            if (forceAll)
            {
                while (_images.Count <= i)
                    _images.Add(null);
                _images[i] = null;
            }

            return;
        }

        while (_images.Count <= i)
            _images.Add(null);

        _images[i] = restored;
    }

    private void ResetScrollToStart()
    {
        _knownScrollY = 0;
        _targetScrollY = 0;
        _lastVirtualizationScrollY = double.NaN;
        _visual?.SendHandlerMessage(new CardGridSnapScrollMessage(0));
        _visual?.SendHandlerMessage(new CardGridScrollMessage(0));
    }

    private bool TryRestoreDisplayImage(object item, string? bitmapProp, string? fileProp, out SKImage? image)
    {
        image = null;
        if (TryRestoreItemCacheImage(item, bitmapProp, fileProp, out var source))
        {
            _itemImageSourceKeys.TryGetValue(item, out var cachedKey);
            if (TryAcquireCachedDisplayImage(source!, cachedKey, GetItemTitle(item), out var cachedDisplay))
            {
                image = cachedDisplay;
                return true;
            }

            if (_itemIndices.TryGetValue(item, out var index))
                ScheduleDisplayImageBake(item, index, source!, cachedKey, GetItemTitle(item));
            return false;
        }

        CompositionCoverImageHelper.ReadCoverSources(
            item,
            bitmapProp,
            fileProp,
            GetBitmapValue,
            ResolveCoverImagePath,
            _sectionPlaceholderBitmap,
            out var bitmapValue,
            out var fileName);

        var sourceKey = CompositionCoverImageHelper.ResolveImageSourceKey(
            item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);
        if (IsPlaceholderSourceKey(sourceKey))
            return false;

        if (!TryAcquireSharedImage(sourceKey, out var sharedImage) || sharedImage == null)
        {
            if (bitmapValue != null &&
                !CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bitmapValue, _sectionPlaceholderBitmap))
            {
                var fromBitmap = CompositionBitmapHelper.ToSkImage(bitmapValue, CachedCardImageSize);
                if (fromBitmap != null)
                {
                    var registered = RegisterSharedImage(sourceKey, fromBitmap);
                    StoreItemImage(item, registered, sourceKey);
                    if (TryAcquireCachedDisplayImage(registered, sourceKey, GetItemTitle(item), out var cachedDisplay))
                    {
                        image = cachedDisplay;
                        return true;
                    }

                    if (_itemIndices.TryGetValue(item, out var index))
                        ScheduleDisplayImageBake(item, index, registered, sourceKey, GetItemTitle(item));
                }
            }

            return false;
        }

        StoreItemImage(item, sharedImage, sourceKey);
        if (TryAcquireCachedDisplayImage(sharedImage, sourceKey, GetItemTitle(item), out var display))
        {
            image = display;
            return true;
        }

        if (_itemIndices.TryGetValue(item, out var bakeIndex))
            ScheduleDisplayImageBake(item, bakeIndex, sharedImage, sourceKey, GetItemTitle(item));
        return false;
    }

    private bool TryRestoreItemCacheImage(object item, string? bitmapProp, string? fileProp, out SKImage? image)
    {
        image = null;
        if (!_imageCache.TryGetValue(item, out var cached))
            return false;

        if (!_itemImageSourceKeys.TryGetValue(item, out var existingKey) || IsPlaceholderSourceKey(existingKey))
        {
            ReleaseItemImage(item);
            return false;
        }

        CompositionCoverImageHelper.ReadCoverSources(
            item,
            bitmapProp ?? nameof(MediaItem.CoverBitmap),
            fileProp ?? nameof(MediaItem.LocalCoverPath),
            GetBitmapValue,
            ResolveCoverImagePath,
            _sectionPlaceholderBitmap,
            out var bitmapValue,
            out var fileName);

        var currentKey = CompositionCoverImageHelper.ResolveImageSourceKey(
            item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap);

        if (!Equals(existingKey, currentKey) ||
            CompositionCoverImageHelper.ShouldReloadCachedCover(
                item as MediaItem, bitmapValue, fileName, _sectionPlaceholderBitmap) ||
            IsPlaceholderImage(cached))
        {
            ReleaseItemImage(item);
            return false;
        }

        image = cached;
        TouchCacheItem(item);
        return true;
    }

    private bool IsDisplayedCoverCurrent(object item, int index, object? sourceKey)
    {
        if (index < 0 || index >= _images.Count)
            return false;

        if (_images[index] == null || IsPlaceholderImage(_images[index]))
            return false;

        if (!_itemImageSourceKeys.TryGetValue(item, out var existingKey) || !Equals(existingKey, sourceKey))
            return false;

        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
            return true;

        return _itemDisplayCacheKeys.TryGetValue(item, out var existingDisplayKey) &&
               Equals(existingDisplayKey, CreateDisplayCacheKey(sourceKey, GetItemTitle(item)));
    }

    private void StoreItemImage(object item, SKImage image, object? sourceKey)
    {
        _imageCache[item] = image;
        if (sourceKey != null)
            _itemImageSourceKeys[item] = sourceKey;
        TouchCacheItem(item);
    }

    private void PurgePlaceholderSharedImages()
    {
        foreach (var key in SharedCoverCache.Keys.ToList())
        {
            if (!IsPlaceholderSourceKey(key))
                continue;

            SharedCoverCache.Release(key, QueueNativeImageDisposal);
        }
    }

    private void SyncVisualImageSlots()
    {
        if (_visual == null || !_coverLoadingActive)
            return;

        int count = _itemsSnapshot.Length;
        if (count == 0)
        {
            _visual.SendHandlerMessage(new CardGridSlotCountMessage(0));
            return;
        }

        if (count <= FastItemsPathThreshold)
        {
            for (int i = 0; i < count; i++)
            {
                if (i >= _images.Count || _images[i] == null || IsPlaceholderImage(_images[i]))
                    continue;

                _visual.SendHandlerMessage(new UpdateImageMessage(i, _images[i]));
            }
        }
        else
        {
            var pushed = new HashSet<int>();
            foreach (int i in EnumerateViewportVisibleIndices(rowBuffer: 4))
            {
                if (i < 0 || i >= count || i >= _images.Count || _images[i] == null || IsPlaceholderImage(_images[i]) || !pushed.Add(i))
                    continue;

                _visual.SendHandlerMessage(new UpdateImageMessage(i, _images[i]));
            }

            if (pushed.Count == 0)
            {
                int fallback = Math.Min(count, FallbackInitialVisibleSlots);
                for (int i = 0; i < fallback; i++)
                {
                    if (i >= _images.Count || _images[i] == null || IsPlaceholderImage(_images[i]))
                        continue;

                    _visual.SendHandlerMessage(new UpdateImageMessage(i, _images[i]));
                }
            }
        }

        SyncVisibleLoadingIndicators();
    }

    private void SyncVisibleLoadingIndicators()
    {
        if (_visual == null || _itemsSnapshot.Length == 0)
            return;

        var (visibleStart, visibleEnd) = GetViewportIndexRange();
        if (visibleStart < 0 || visibleEnd < visibleStart)
        {
            int fallbackCount = Math.Min(_itemsSnapshot.Length, FallbackInitialVisibleSlots);
            for (int i = 0; i < fallbackCount; i++)
                SetLoading(i, ShouldShowLoadingSpinner(i));
            return;
        }

        foreach (int i in EnumerateViewportVisibleIndices())
            SetLoading(i, ShouldShowLoadingSpinner(i));
    }

    private void SyncAllLoadingIndicators()
    {
        for (int i = 0; i < _itemsSnapshot.Length; i++)
            SetLoading(i, ShouldShowLoadingSpinner(i));
    }

    private (int Start, int End) GetViewportIndexRange()
    {
        if (LayoutItemCount == 0 || Bounds.Height <= 0 || Bounds.Width <= 0)
            return (-1, -1);

        var (visibleStart, visibleEnd) = CardGridLayoutHelper.GetVisibleIndexRange(
            _knownScrollY,
            (float)Bounds.Height,
            LayoutItemCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);

        return (visibleStart, visibleEnd);
    }

    private IEnumerable<int> EnumerateViewportVisibleIndices(int rowBuffer = 2)
    {
        if (LayoutItemCount == 0 || Bounds.Height <= 0 || Bounds.Width <= 0)
            yield break;

        foreach (int index in CardGridLayoutHelper.EnumerateVisibleIndices(
                     _knownScrollY,
                     (float)Bounds.Height,
                     LayoutItemCount,
                     (float)Bounds.Width,
                     (float)CardScale,
                     (float)CardSpacing,
                     (float)TopPadding,
                     HorizontalScrollEnabled,
                     rowBuffer))
        {
            yield return index;
        }
    }

    private int GetViewportCenterIndex()
    {
        if (LayoutItemCount == 0 || Bounds.Height <= 0 || Bounds.Width <= 0)
            return (int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, LayoutItemCount - 1));

        return CardGridLayoutHelper.EstimateViewportCenterIndex(
            _knownScrollY,
            (float)Bounds.Height,
            LayoutItemCount,
            (float)Bounds.Width,
            (float)CardScale,
            (float)CardSpacing,
            (float)TopPadding,
            HorizontalScrollEnabled);
    }

    private void EnsureImageSlotCount(int count)
    {
        if (_images.Count == count)
            return;

        if (_images.Count > count)
            _images.RemoveRange(count, _images.Count - count);
        else
        {
            int add = count - _images.Count;
            for (int i = 0; i < add; i++)
                _images.Add(null);
        }
    }

    private static List<int> BuildCenterOutLoadOrder(int start, int end, int centerIdx)
    {
        var order = new List<int>(Math.Max(0, end - start + 1));
        if (start < 0 || end < start)
            return order;

        centerIdx = Math.Clamp(centerIdx, start, end);
        order.Add(centerIdx);
        for (int offset = 1; order.Count < end - start + 1; offset++)
        {
            int before = centerIdx - offset;
            int after = centerIdx + offset;
            if (before >= start)
                order.Add(before);
            if (after <= end)
                order.Add(after);
        }

        return order;
    }

    protected override Size MeasureOverride(Size availableSize) => availableSize;

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

    private async Task<SKImage?> LoadImageAsync(
        Bitmap? bitmapValue,
        string? fileName,
        MediaItem? owner,
        CancellationToken ct,
        Bitmap? sectionPlaceholder = null)
    {
        sectionPlaceholder ??= _sectionPlaceholderBitmap;
        if (ct.IsCancellationRequested)
            return null;

        if (bitmapValue != null &&
            !CompositionCoverImageHelper.IsSectionPlaceholderBitmap(bitmapValue, sectionPlaceholder))
        {
            var fromBitmap = await CompositionBitmapHelper.ToCoverSkImageAsync(
                bitmapValue,
                CachedCardImageSize,
                cancellationToken: ct).ConfigureAwait(false);
            if (fromBitmap != null)
                return fromBitmap;
        }

        if (CompositionCoverImageHelper.ShouldPreferFileOverBitmap(owner, bitmapValue, fileName, sectionPlaceholder))
        {
            var fromFile = await Task.Run(() => LoadAndResize(fileName!, owner), ct).ConfigureAwait(false);
            if (fromFile != null)
                return fromFile;
        }

        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
        {
            var fromFile = await Task.Run(() => LoadAndResize(fileName, owner), ct).ConfigureAwait(false);
            if (fromFile != null)
                return fromFile;
        }

        return null;
    }

    private SKImage? CreateCardImage(SKBitmap source) =>
        CompositionBitmapHelper.CreateCoverSkImage(source, CachedCardImageSize);

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
        if (sourceKey == null || !SharedCoverCache.TryAcquire(sourceKey, out var acquired))
            return false;

        image = acquired;
        return true;
    }

    private SKImage RegisterSharedImage(object? sourceKey, SKImage image)
    {
        if (sourceKey == null || IsPlaceholderSourceKey(sourceKey))
            return image;

        if (SharedCoverCache.TryGetEntry(sourceKey, out var existing, out _))
        {
            SharedCoverCache.TryAcquire(sourceKey, out _);
            QueueNativeImageDisposal(image);
            return existing;
        }

        return SharedCoverCache.Register(sourceKey, image);
    }

    private SKImage RegisterReloadedSharedImage(object? sourceKey, SKImage image)
    {
        if (sourceKey == null)
            return image;

        if (SharedCoverCache.TryGetEntry(sourceKey, out _, out var refCount) && refCount > 1)
            return image;

        SharedCoverCache.Release(sourceKey, QueueNativeImageDisposal);
        return SharedCoverCache.Register(sourceKey, image);
    }

    private bool IsImageUsedByDisplay(SKImage image) =>
        _images.Any(img => ReferenceEquals(img, image));

    private void ReleaseItemImage(object key, HashSet<SKImage>? disposedImages = null)
    {
        if (!_imageCache.TryGetValue(key, out _))
        {
            RemoveCacheNode(key);
            _itemImageSourceKeys.Remove(key);
            _itemDisplayCacheKeys.Remove(key);
            return;
        }

        _imageCache.Remove(key);
        RemoveCacheNode(key);
        if (!_itemImageSourceKeys.Remove(key, out var sourceKey))
        {
            _itemDisplayCacheKeys.Remove(key);
            return;
        }

        if (_itemDisplayCacheKeys.Remove(key, out var displayKey))
            CardDisplayCache.Release(displayKey, QueueNativeImageDisposal);

        if (_itemIndices.TryGetValue(key, out var idx) &&
            idx >= 0 &&
            idx < _images.Count &&
            IsCurrentSnapshotItem(key, idx))
        {
            _images[idx] = null;
            _visual?.SendHandlerMessage(new UpdateImageMessage(idx, null, ClearImage: true));
        }

        _itemDisplayCacheKeys.Remove(key);

        if (sourceKey != null)
            SharedCoverCache.Release(sourceKey, QueueNativeImageDisposal);
    }

    internal void QueueNativeImageDisposal(SKImage image)
    {
        if (ReferenceEquals(image, _sharedPlaceholder) ||
            ReferenceEquals(image, _sectionPlaceholderSkImage))
            return;

        if (Dispatcher.UIThread.CheckAccess())
            _visual?.SendHandlerMessage(new CardGridQueueImageDisposalMessage(image));
        else
            PostToUi(() => _visual?.SendHandlerMessage(new CardGridQueueImageDisposalMessage(image)), DispatcherPriority.Render);
    }

    private void DisposeImage(SKImage? image, HashSet<SKImage>? disposedImages = null)
    {
        if (image == null ||
            ReferenceEquals(image, _sharedPlaceholder) ||
            ReferenceEquals(image, _sectionPlaceholderSkImage))
            return;
        if (disposedImages != null)
        {
            disposedImages.Add(image);
            return;
        }

        QueueNativeImageDisposal(image);
    }

    private void DisposeImageOnUiThread(SKImage image) => QueueNativeImageDisposal(image);

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
        if (!_imageCacheNodes.Remove(key, out var node))
            return;

        if (node.List == _imageCacheLru)
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
            if (itemToIndex.TryGetValue(key, out var idx) && idx >= 0 && idx < _images.Count)
            {
                _images[idx] = null;
                _visual?.SendHandlerMessage(new UpdateImageMessage(idx, null, ClearImage: true));
            }

            ReleaseItemImage(key);
        }
    }

    private static SKColor GetSkColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush solid)
            return new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, 255);
        return SKColor.Parse("#101010");
    }
}
