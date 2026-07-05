using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Windows.Input;
using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
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

namespace AES_Controls.Composition;

/// <summary>
/// A composition-based horizontally scrollable album row using folder fan visuals.
/// </summary>
public class CompositionAlbumRowControl : ItemsControl, IScaleExclusionRenderTarget
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<CompositionAlbumRowControl>();
    private const int AnimationHeartbeatMs = 16;
    private const int ScrollIdleMs = 150;
    private const double WheelStrideFactor = 0.80;
    private const double WheelVelocityScale = 8.2;
    private const double WheelSmoothBoostWindowMs = 100.0;
    private const double WheelSmoothBoostMax = 1.22;
    private const double WheelBurstBoostMax = 0.20;
    private const double WheelMaxVelocity = 4000.0;
    private const double DragReleaseVelocityScale = 0.76;
    private const double DragStartThreshold = 4.0;
    private const int DragAutoScrollMs = 16;
    private const int DragCommitMs = 300;
    private const int TileCoverReloadDebounceMs = 48;

    private CompositionCustomVisual? _visual;
    private readonly AlbumRowAnimationSyncState _animationSync = new();
    private readonly HashSet<INotifyPropertyChanged> _subscribedItems = new();
    private readonly HashSet<MediaItem> _subscribedPreviewItems = new();
    private readonly HashSet<MediaItem> _subscribedChildItems = new();
    private readonly Dictionary<int, TileCoverFingerprint> _tileCoverFingerprints = new();
    private readonly HashSet<int> _pendingTileCoverIndices = new();

    private readonly record struct TileCoverFingerprint(
        FolderMediaItem? Folder,
        Bitmap? FolderCover,
        Bitmap?[] PreviewCovers)
    {
        public bool Matches(FolderMediaItem folder, Bitmap? folderCover, Bitmap?[] previewCovers) =>
            ReferenceEquals(Folder, folder) &&
            ReferenceEquals(FolderCover, folderCover) &&
            PreviewCovers.AsSpan().SequenceEqual(previewCovers);
    }
    private DispatcherTimer? _tileCoverReloadDebounceTimer;
    private object?[] _itemsSnapshot = [];
    private bool _pendingScrollResetOnLayout;
    private double _lastCoverLoadScrollX = double.NaN;
    private IEnumerable? _subscribedItemsSource;
    private double _knownScrollX;
    private double _targetScrollX;
    private double _velocityX;
    private ulong _lastWheelTimestamp;
    private bool _isPressed;
    private bool _isPointerScrolling;
    private bool _isScrollbarPressed;
    private bool _isScrollbarHovered;
    private bool _isWheelScrolling;
    private bool _isScrollFrozen;
    private bool _isDragging;
    private bool _hasDragMoved;
    private bool _visualDragActive;
    private bool _isInternalMove;
    private int _pressedItemIndex = -1;
    private int _draggingIndex = -1;
    private int _dragStartIndex = -1;
    private int _currentDragTargetIndex = -1;
    private int _lastSentDropTargetIndex = -1;
    private int _pendingReorderFrom = -1;
    private int _pendingReorderTo = -1;
    private Point _startPoint;
    private Point _prevPoint;
    private ulong _prevTime;
    private double _scrollAtDragStart;
    private double _savedScrollXOnDragFinish;
    private Avalonia.Vector _dragPointerOffset;
    private DispatcherTimer? _uiSyncTimer;
    private DispatcherTimer? _wheelScrollSettleTimer;
    private DispatcherTimer? _scrollIdleTimer;
    private DispatcherTimer? _autoScrollTimer;
    private DispatcherTimer? _dragCommitTimer;

    public static readonly StyledProperty<double> SelectedIndexProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, double>(nameof(SelectedIndex));

    public static readonly StyledProperty<double> TileScaleProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, double>(nameof(TileScale), 1.0);

    public static readonly StyledProperty<double> TileSpacingProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, double>(nameof(TileSpacing), 20.0);

    public static readonly StyledProperty<int> PointedItemIndexProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, int>(nameof(PointedItemIndex), -1);

    public static readonly StyledProperty<ICommand?> ItemSelectedCommandProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, ICommand?>(nameof(ItemSelectedCommand));

    public static readonly StyledProperty<ICommand?> ItemDoubleClickedCommandProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, ICommand?>(nameof(ItemDoubleClickedCommand));

    public static readonly StyledProperty<ICommand?> DropCommandProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, ICommand?>(nameof(DropCommand));

    public static readonly StyledProperty<double> GlobalOpacityProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, double>(nameof(GlobalOpacity), 1.0);

    public static readonly StyledProperty<int> RenamingIndexProperty =
        AvaloniaProperty.Register<CompositionAlbumRowControl, int>(nameof(RenamingIndex), -1);

    public double SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double TileScale
    {
        get => GetValue(TileScaleProperty);
        set => SetValue(TileScaleProperty, value);
    }

    public double TileSpacing
    {
        get => GetValue(TileSpacingProperty);
        set => SetValue(TileSpacingProperty, value);
    }

    public int PointedItemIndex
    {
        get => GetValue(PointedItemIndexProperty);
        set => SetValue(PointedItemIndexProperty, value);
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

    public ICommand? DropCommand
    {
        get => GetValue(DropCommandProperty);
        set => SetValue(DropCommandProperty, value);
    }

    public double GlobalOpacity
    {
        get => GetValue(GlobalOpacityProperty);
        set => SetValue(GlobalOpacityProperty, value);
    }

    public int RenamingIndex
    {
        get => GetValue(RenamingIndexProperty);
        set => SetValue(RenamingIndexProperty, value);
    }

    /// <summary>
    /// Raised while an album title is being edited and the row scroll position changes.
    /// </summary>
    public event EventHandler? RenameOverlayLayoutRequested;

    private double _lastRenameOverlayScrollX = double.NaN;

    public CompositionAlbumRowControl()
    {
        ScalableDecorator.SetExcludeFromScale(this, true);
        ScalableDecorator.SetExcludeFromScaleCompensation(this, false);
        Focusable = true;
        Background = new SolidColorBrush(Color.Parse("#101010"));
        GlobalOpacity = Opacity;
        ClipToBounds = true;

        _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(AnimationHeartbeatMs), DispatcherPriority.Render, (_, _) =>
        {
            double syncScrollX = _animationSync.CurrentScrollX;
            bool scrollChanged = Math.Abs(syncScrollX - _lastRenameOverlayScrollX) > 0.01;
            if (Math.Abs(syncScrollX - _knownScrollX) > 0.5)
            {
                _knownScrollX = syncScrollX;
                EnsureVisibleTileCoversLoadedIfNeeded();
            }

            if (RenamingIndex >= 0 && scrollChanged)
            {
                _lastRenameOverlayScrollX = syncScrollX;
                RenameOverlayLayoutRequested?.Invoke(this, EventArgs.Empty);
            }

            if (_isWheelScrolling)
                return;

            bool tracking = _isPointerScrolling || _isScrollbarPressed ||
                            (_animationSync.IsAnimating && Math.Abs(_animationSync.VelocityX) > 0.01);
            if (!tracking)
                return;
        });

        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragAutoScrollMs) };
        _autoScrollTimer.Tick += (_, _) => UpdateDragAutoScroll(_prevPoint);
        _dragCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragCommitMs) };
        _dragCommitTimer.Tick += DragCommitTimer_Tick;
    }

    public void RefreshExclusionRenderSize() => UpdateCompositionVisualSize(Bounds.Size);

    public Rect GetTileBounds(int index) =>
        AlbumRowLayoutHelper.GetTileBounds(
            index,
            GetLiveScrollX(),
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)TileScale,
            (float)TileSpacing);

    private double GetLiveScrollX() => _animationSync.CurrentScrollX;

    public void EnsureSelectedItemVisible(bool animate = false)
    {
        int index = (int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, _itemsSnapshot.Length - 1));
        EnsureIndexVisible(index, animate);
    }

    public void ResetScrollToStart()
    {
        _pendingScrollResetOnLayout = false;
        _knownScrollX = 0;
        _targetScrollX = 0;
        _velocityX = 0;
        _animationSync.CurrentScrollX = 0;
        _animationSync.TargetScrollX = 0;
        _animationSync.VelocityX = 0;
        _animationSync.IsAnimating = false;
        _visual?.SendHandlerMessage(new AlbumRowSnapScrollMessage(0));
    }

    public void ResetScrollPositionOnViewShown() => ScheduleScrollResetOnShow();

    private void ScheduleScrollResetOnShow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || Bounds.Width <= 0 || _itemsSnapshot.Length == 0)
                return;

            ResetScrollToStart();
        }, DispatcherPriority.Loaded);
    }

    public Rect GetTileTitleBarBounds(int index)
    {
        var tile = GetTileBounds(index);
        double scale = index == (int)Math.Round(SelectedIndex)
            ? 1.0 + AlbumRowLayoutHelper.SelectionLiftScale
            : 1.0;

        if (scale > 1.001)
        {
            double scaledW = tile.Width * scale;
            double scaledH = tile.Height * scale;
            tile = new Rect(
                tile.X - (scaledW - tile.Width) * 0.5,
                tile.Y - (scaledH - tile.Height) * 0.5,
                scaledW,
                scaledH);
        }

        double padX = AlbumRowLayoutHelper.TitleBarPaddingX * scale;
        double titleH = AlbumRowLayoutHelper.TitleBarHeight * scale;
        return new Rect(
            tile.X + padX,
            tile.Y + tile.Height - titleH,
            Math.Max(0, tile.Width - padX * 2),
            titleH);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor == null)
            return;

        _visual = compositor.CreateCustomVisual(new CompositionAlbumRowVisualHandler());
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.SendHandlerMessage(new AlbumRowAttachSyncMessage(_animationSync));
        SendLayoutMessage();
        UpdateCompositionVisualSize(Bounds.Size);
        _visual.SendHandlerMessage(new AlbumRowSelectedIndexMessage((int)Math.Round(SelectedIndex)));
        _visual.SendHandlerMessage(new AlbumRowRenamingIndexMessage(RenamingIndex));
        _visual.SendHandlerMessage(new AlbumRowBackgroundColorMessage(GetSkColor(Background)));
        _visual.SendHandlerMessage(new GlobalOpacityMessage(Opacity));
        if (ItemsSource != null)
            UpdateItems();
        else
            ResetScrollToStart();

        _uiSyncTimer?.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ResetScrollToStart();
        _uiSyncTimer?.Stop();
        _wheelScrollSettleTimer?.Stop();
        _scrollIdleTimer?.Stop();
        _autoScrollTimer?.Stop();
        _dragCommitTimer?.Stop();
        ClearSubscriptions();
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
        UpdateCompositionVisualSize(e.NewSize);
        _lastCoverLoadScrollX = double.NaN;
        if (_pendingScrollResetOnLayout && e.NewSize.Width > 0)
        {
            _pendingScrollResetOnLayout = false;
            ResetScrollToStart();
        }

        EnsureVisibleTileCoversLoaded();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedIndexProperty)
        {
            int idx = (int)Math.Round(change.GetNewValue<double>());
            _visual?.SendHandlerMessage(new AlbumRowSelectedIndexMessage(idx));
        }
        else if (change.Property == IsVisibleProperty &&
                 change.GetNewValue<bool>() &&
                 _visual != null &&
                 _itemsSnapshot.Length > 0)
        {
            ScheduleScrollResetOnShow();
        }
        else if (change.Property == TileScaleProperty || change.Property == TileSpacingProperty)
        {
            SendLayoutMessage();
            _tileCoverFingerprints.Clear();
            _lastCoverLoadScrollX = double.NaN;
            EnsureVisibleTileCoversLoaded();
        }
        else if (change.Property == ItemsSourceProperty)
        {
            UpdateItems();
        }
        else if (change.Property == BackgroundProperty)
        {
            _visual?.SendHandlerMessage(new AlbumRowBackgroundColorMessage(GetSkColor(change.GetNewValue<IBrush?>())));
        }
        else if (change.Property == RenamingIndexProperty)
        {
            _lastRenameOverlayScrollX = double.NaN;
            _visual?.SendHandlerMessage(new AlbumRowRenamingIndexMessage(change.GetNewValue<int>()));
            if (change.GetNewValue<int>() >= 0)
                RenameOverlayLayoutRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (change.Property == OpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
        else if (change.Property == GlobalOpacityProperty)
            _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        ResetPointerInteraction(applyInertia: false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            int hit = HitTestIndex(pos);
            if (hit >= 0)
                PublishSelectedIndex(hit, force: true);
            if (ContextMenu is { } menu)
                menu.Open(this);
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
        Focus();
        SettlePointerState();

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
        _scrollAtDragStart = _knownScrollX;
        _startPoint = pos;
        _prevPoint = pos;
        _prevTime = e.Timestamp;
        _velocityX = 0;
        _pressedItemIndex = hitIndex;
        e.Pointer.Capture(this);

        if (hitIndex >= 0)
            BeginItemDrag(hitIndex);
        else
        {
            _isPointerScrolling = true;
            _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(true));
        }

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
            ApplyScrollbarPosition(pos.X);
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            if (!_hasDragMoved)
            {
                if (Point.Distance(_startPoint, pos) > DragStartThreshold)
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
            _autoScrollTimer?.Start();
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
            _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(true));
        }

        double dx = pos.X - _startPoint.X;
        _targetScrollX = Math.Clamp(_scrollAtDragStart - dx, -80, GetMaxScrollX() + 80);
        _knownScrollX = _targetScrollX;
        _visual?.SendHandlerMessage(new AlbumRowScrollMessage(_targetScrollX));

        ulong dt = e.Timestamp - _prevTime;
        if (dt > 0)
        {
            double instantVelocity = -(pos.X - _prevPoint.X) / (dt / 1000.0);
            _velocityX = _velocityX * 0.5 + instantVelocity * 0.5;
        }

        _prevTime = e.Timestamp;
        UpdateHoverState(pos);
        BeginScrollInteraction();
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
            _visual?.SendHandlerMessage(new AlbumRowScrollbarPressedMessage(false));
            _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(false));
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_isDragging)
        {
            FinishDrag(GetDragTargetIndex(pos), cancel: false, e.Pointer);
            e.Handled = true;
            return;
        }

        if (!_isPressed)
            return;

        bool wasScrolling = _isPointerScrolling;
        int hit = HitTestIndex(pos);
        bool isClick = Math.Abs(pos.X - _startPoint.X) < 8 && Math.Abs(pos.Y - _startPoint.Y) < 8;
        if (isClick && hit != -1)
        {
            PublishSelectedIndex(hit, force: true);
            ItemSelectedCommand?.Execute(hit);
            ResetPointerInteraction(applyInertia: false);
        }
        else
        {
            ResetPointerInteraction(applyInertia: wasScrolling);
            if (wasScrolling)
            {
                _targetScrollX = Math.Clamp(_knownScrollX, 0, GetMaxScrollX());
                SyncKnownScrollX(_targetScrollX);
                _visual?.SendHandlerMessage(new AlbumRowScrollMessage(_targetScrollX));
                _visual?.SendHandlerMessage(new AlbumRowScrollVelocityMessage(_velocityX * DragReleaseVelocityScale));
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double rawDelta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (Math.Abs(rawDelta) < 0.0001)
            return;

        // ~one album tile per wheel notch; scales with tile size for a natural list feel.
        double wheelDelta = rawDelta * GetTileStridePx() * WheelStrideFactor;
        _targetScrollX = Math.Clamp(_animationSync.TargetScrollX - wheelDelta, 0, GetMaxScrollX());
        _knownScrollX = _targetScrollX;

        // Keep only a small smoothing tail for rapid trackpad micro-deltas.
        ulong now = e.Timestamp;
        double sinceLastMs = _lastWheelTimestamp == 0
            ? double.MaxValue
            : now - _lastWheelTimestamp;
        _lastWheelTimestamp = now;

        double smoothFactor = 1.0;
        if (sinceLastMs < WheelSmoothBoostWindowMs && Math.Abs(rawDelta) < 0.6)
        {
            double smoothT = 1.0 - Math.Clamp(sinceLastMs / WheelSmoothBoostWindowMs, 0.0, 1.0);
            smoothFactor = 1.0 + smoothT * (WheelSmoothBoostMax - 1.0);
        }

        // Faster consecutive scroll events build higher velocity (trackpad flings / fast wheel).
        double burstBoost = 1.0;
        if (sinceLastMs < WheelSmoothBoostWindowMs * 1.35)
        {
            double burstT = 1.0 - Math.Clamp(sinceLastMs / (WheelSmoothBoostWindowMs * 1.35), 0.0, 1.0);
            burstBoost = 1.0 + burstT * WheelBurstBoostMax;
        }

        double scrollDir = Math.Sign(-wheelDelta);
        double momentumBoost = 1.0;
        if (scrollDir != 0 &&
            Math.Abs(_animationSync.VelocityX) > 220 &&
            Math.Sign(_animationSync.VelocityX) == scrollDir)
        {
            momentumBoost = 1.0 + Math.Min(Math.Abs(_animationSync.VelocityX) / 3500.0, 0.22);
        }

        double impulse = -wheelDelta * WheelVelocityScale * smoothFactor * burstBoost * momentumBoost;
        double newVelocity = Math.Clamp(_animationSync.VelocityX + impulse, -WheelMaxVelocity, WheelMaxVelocity);
        _visual?.SendHandlerMessage(new AlbumRowScrollMessage(_targetScrollX));
        _visual?.SendHandlerMessage(new AlbumRowScrollVelocityMessage(newVelocity));
        EnsureVisibleTileCoversLoadedIfNeeded();

        _isWheelScrolling = true;
        _wheelScrollSettleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollIdleMs) };
        _wheelScrollSettleTimer.Tick -= WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Tick += WheelScrollSettleTimer_Tick;
        _wheelScrollSettleTimer.Stop();
        _wheelScrollSettleTimer.Start();
        BeginScrollInteraction();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_itemsSnapshot.Length == 0)
            return;

        int current = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _itemsSnapshot.Length - 1);
        int next = current;
        if (e.Key == Key.Left) next = current - 1;
        else if (e.Key == Key.Right) next = current + 1;
        else if (e.Key == Key.Home) next = 0;
        else if (e.Key == Key.End) next = _itemsSnapshot.Length - 1;
        else return;

        next = Math.Clamp(next, 0, _itemsSnapshot.Length - 1);
        if (next != current || e.Key is Key.Home or Key.End)
        {
            PublishSelectedIndex(next, force: true);
            ItemSelectedCommand?.Execute(next);
            e.Handled = true;
        }
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new Panel { Width = 0, Height = 0, IsVisible = false, IsHitTestVisible = false, Focusable = false };

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

    public override void Render(DrawingContext context)
    {
        if (Background != null)
            context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        base.Render(context);
    }

    private void WheelScrollSettleTimer_Tick(object? sender, EventArgs e)
    {
        _wheelScrollSettleTimer?.Stop();
        _isWheelScrolling = false;
        _knownScrollX = _animationSync.CurrentScrollX;
        _targetScrollX = _knownScrollX;
        FlushDeferredTileCoverPushes();
    }

    private bool IsAlbumRowScrollActive()
    {
        if (_isScrollFrozen || _isWheelScrolling || _isPointerScrolling || _isScrollbarPressed)
            return true;

        if (Math.Abs(_velocityX) > 0.5)
            return true;

        if (Math.Abs(_targetScrollX - _knownScrollX) > 0.5)
            return true;

        return _animationSync.IsAnimating &&
               (Math.Abs(_animationSync.VelocityX) > 0.5 ||
                Math.Abs(_animationSync.TargetScrollX - _animationSync.CurrentScrollX) > 0.5);
    }

    private void BeginScrollInteraction()
    {
        if (!_isScrollFrozen)
        {
            _isScrollFrozen = true;
            _visual?.SendHandlerMessage(new AlbumRowScrollFrozenMessage(true));
            FolderCompositionTileControl.SetAlbumListScrollFrozen(true);
        }

        _scrollIdleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollIdleMs) };
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Tick -= ScrollIdleTimer_Tick;
        _scrollIdleTimer.Tick += ScrollIdleTimer_Tick;
        _scrollIdleTimer.Start();
    }

    private void ScrollIdleTimer_Tick(object? sender, EventArgs e)
    {
        _scrollIdleTimer?.Stop();
        if (_isScrollFrozen)
        {
            _isScrollFrozen = false;
            _visual?.SendHandlerMessage(new AlbumRowScrollFrozenMessage(false));
            FolderCompositionTileControl.SetAlbumListScrollFrozen(false);
        }

        FlushDeferredTileCoverPushes();
    }

    private void SettlePointerState()
    {
        _autoScrollTimer?.Stop();
        _dragCommitTimer?.Stop();
        if (_isDragging)
            FinishDrag(_dragStartIndex, cancel: true);
        _isScrollbarPressed = false;
        _visual?.SendHandlerMessage(new AlbumRowScrollbarPressedMessage(false));
        ResetPointerInteraction(applyInertia: false);
    }

    private void ResetPointerInteraction(bool applyInertia)
    {
        _isPressed = false;
        _pressedItemIndex = -1;
        _isPointerScrolling = false;
        _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(false));
        if (!applyInertia)
        {
            _velocityX = 0;
            _visual?.SendHandlerMessage(new AlbumRowScrollVelocityMessage(0));
        }
    }

    private int HitTestIndex(Point point) =>
        AlbumRowLayoutHelper.HitTestTile(
            point,
            _knownScrollX,
            _itemsSnapshot.Length,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)TileScale,
            (float)TileSpacing);

    private double GetMaxScrollX() =>
        AlbumRowLayoutHelper.Compute(
            (float)Bounds.Width,
            (float)Bounds.Height,
            _itemsSnapshot.Length,
            (float)TileScale,
            (float)TileSpacing).MaxScrollX;

    private double GetTileStridePx()
    {
        if (Bounds.Width <= 0)
            return AlbumRowLayoutHelper.BaseTileWidth + 20.0;

        var metrics = AlbumRowLayoutHelper.Compute(
            (float)Bounds.Width,
            (float)Bounds.Height,
            Math.Max(1, _itemsSnapshot.Length),
            (float)TileScale,
            (float)TileSpacing);
        return metrics.TileWidth + metrics.Spacing;
    }

    private void PublishSelectedIndex(int index, bool force = false)
    {
        if (_itemsSnapshot.Length == 0)
            return;

        index = Math.Clamp(index, 0, _itemsSnapshot.Length - 1);
        if (!force && Math.Abs(index - SelectedIndex) < 0.001)
            return;

        SelectedIndex = index;
        _visual?.SendHandlerMessage(new AlbumRowSelectedIndexMessage(index));
        EnsureIndexVisible(index, animate: true);
    }

    private void EnsureIndexVisible(int index, bool animate)
    {
        if (Bounds.Width <= 0 || index < 0)
            return;

        double offset = AlbumRowLayoutHelper.ScrollOffsetToRevealIndex(
            index,
            GetLiveScrollX(),
            (float)Bounds.Width,
            (float)Bounds.Height,
            _itemsSnapshot.Length,
            (float)TileScale,
            (float)TileSpacing);

        if (Math.Abs(offset - GetLiveScrollX()) < 0.5)
            return;

        if (!animate)
        {
            SyncKnownScrollX(offset);
            _visual?.SendHandlerMessage(new AlbumRowSnapScrollMessage(offset));
            return;
        }

        _targetScrollX = offset;
        _visual?.SendHandlerMessage(new AlbumRowScrollMessage(offset));
    }

    private void SyncKnownScrollX(double scrollX)
    {
        _knownScrollX = scrollX;
        _targetScrollX = scrollX;
        _animationSync.CurrentScrollX = scrollX;
        _animationSync.TargetScrollX = scrollX;
        _animationSync.VelocityX = 0;
        EnsureVisibleTileCoversLoadedIfNeeded();
    }

    private void UpdateHoverState(Point point)
    {
        int hit = HitTestIndex(point);
        if (hit != PointedItemIndex)
        {
            PointedItemIndex = hit;
            _visual?.SendHandlerMessage(new AlbumRowHoveredIndexMessage(hit));
        }

        SetScrollbarHovered(IsPointerOverScrollbarArea(point));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetScrollbarHovered(false);
    }

    private void SetScrollbarHovered(bool hovered)
    {
        if (_isScrollbarHovered == hovered)
            return;

        _isScrollbarHovered = hovered;
        _visual?.SendHandlerMessage(new AlbumRowScrollbarHoverMessage(hovered));
    }

    private bool IsPointerOverScrollbarArea(Point pos)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || GetMaxScrollX() <= 1)
            return false;

        float trackY = (float)Bounds.Height - AlbumRowLayoutHelper.ScrollbarBottomInset - AlbumRowLayoutHelper.ScrollbarHitHeight;
        return pos.Y >= trackY;
    }

    private bool TryBeginScrollbarDrag(Point pos, IPointer pointer)
    {
        if (Bounds.Height <= 0 || GetMaxScrollX() <= 1)
            return false;

        float trackY = (float)Bounds.Height - AlbumRowLayoutHelper.ScrollbarBottomInset - AlbumRowLayoutHelper.ScrollbarHitHeight;
        if (pos.Y < trackY)
            return false;

        _isScrollbarPressed = true;
        _visual?.SendHandlerMessage(new AlbumRowScrollbarPressedMessage(true));
        _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(true));
        ApplyScrollbarPosition(pos.X);
        pointer.Capture(this);
        return true;
    }

    private void ApplyScrollbarPosition(double pointerX)
    {
        double maxScroll = GetMaxScrollX();
        if (maxScroll <= 0)
            return;

        float trackLeft = AlbumRowLayoutHelper.RowPaddingX;
        float trackRight = (float)Bounds.Width - AlbumRowLayoutHelper.RowPaddingX;
        float trackWidth = trackRight - trackLeft;
        float thumbRatio = (float)Bounds.Width / Math.Max(
            AlbumRowLayoutHelper.Compute((float)Bounds.Width, (float)Bounds.Height, _itemsSnapshot.Length, (float)TileScale, (float)TileSpacing).ContentWidth,
            (float)Bounds.Width);
        float thumbWidth = Math.Max(32f, trackWidth * thumbRatio);
        float usable = trackWidth - thumbWidth;
        if (usable <= 0)
            return;

        float ratio = Math.Clamp((float)(pointerX - trackLeft - thumbWidth / 2f) / usable, 0f, 1f);
        double scrollX = ratio * maxScroll;
        _knownScrollX = scrollX;
        _targetScrollX = scrollX;
        _visual?.SendHandlerMessage(new AlbumRowScrollMessage(scrollX));
    }

    private void BeginItemDrag(int hit)
    {
        _isDragging = true;
        _hasDragMoved = false;
        _visualDragActive = false;
        _draggingIndex = hit;
        _dragStartIndex = hit;
        _currentDragTargetIndex = hit;
        _lastSentDropTargetIndex = -1;
        _isPointerScrolling = false;

        var bounds = GetTileBounds(hit);
        var dragCenter = new Point(bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5);
        _dragPointerOffset = dragCenter - _prevPoint;
    }

    private void ActivateVisualDrag()
    {
        if (_visualDragActive || _draggingIndex < 0)
            return;

        _visualDragActive = true;
        PointedItemIndex = -1;
        _visual?.SendHandlerMessage(new AlbumRowHoveredIndexMessage(-1));
        _visual?.SendHandlerMessage(new AlbumRowDirectScrollFollowMessage(true));
        _visual?.SendHandlerMessage(new AlbumRowDragStateMessage(_draggingIndex, true));
        _visual?.SendHandlerMessage(new AlbumRowDropTargetMessage(_draggingIndex));
        _lastSentDropTargetIndex = _draggingIndex;
        UpdateDragInteraction(_prevPoint);
    }

    private Point GetDragVisualPoint(Point pointerPoint) =>
        new(pointerPoint.X + _dragPointerOffset.X, pointerPoint.Y + _dragPointerOffset.Y);

    private void UpdateDragInteraction(Point pointerPoint)
    {
        var dragPoint = GetDragVisualPoint(pointerPoint);
        _visual?.SendHandlerMessage(new AlbumRowDragPositionMessage(new Vector2((float)dragPoint.X, (float)dragPoint.Y)));

        int targetIndex = _hasDragMoved ? GetDragTargetIndex(pointerPoint) : _dragStartIndex;
        _currentDragTargetIndex = targetIndex;
        if (targetIndex != _lastSentDropTargetIndex)
        {
            _lastSentDropTargetIndex = targetIndex;
            _visual?.SendHandlerMessage(new AlbumRowDropTargetMessage(targetIndex));
        }
    }

    private void UpdateDragAutoScroll(Point pointerPoint)
    {
        if (!_visualDragActive || Bounds.Width <= 0)
            return;

        var dragPoint = GetDragVisualPoint(pointerPoint);
        double maxScroll = GetMaxScrollX();
        if (maxScroll <= 1)
            return;

        double w = Bounds.Width;
        double zone = Math.Clamp(w * 0.16, 64, 140);
        double scrollDelta = 0;
        if (dragPoint.X < zone)
            scrollDelta = -Math.Pow((zone - dragPoint.X) / zone, 2);
        else if (dragPoint.X > w - zone)
            scrollDelta = Math.Pow((dragPoint.X - (w - zone)) / zone, 2);

        if (Math.Abs(scrollDelta) < 0.02)
            return;

        // Scroll faster when there is still a long distance to traverse.
        double remaining = scrollDelta < 0 ? _knownScrollX : maxScroll - _knownScrollX;
        double distanceBoost = 1.0 + Math.Min(2.5, remaining / 1800.0);
        const double scrollSpeed = 780.0;
        double step = scrollDelta * scrollSpeed * distanceBoost * (DragAutoScrollMs / 1000.0);

        double next = Math.Clamp(_knownScrollX + step, 0, maxScroll);
        if (Math.Abs(next - _knownScrollX) < 0.25)
            return;

        _knownScrollX = next;
        _targetScrollX = next;
        _visual?.SendHandlerMessage(new AlbumRowScrollMessage(next));
    }

    private int GetDragTargetIndex(Point pointerPoint)
    {
        var dragCenter = GetDragVisualPoint(pointerPoint);
        return AlbumRowLayoutHelper.FindNearestDropTargetIndex(
            dragCenter,
            _knownScrollX,
            _itemsSnapshot.Length,
            (float)Bounds.Width,
            (float)Bounds.Height,
            (float)TileScale,
            (float)TileSpacing);
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
                PublishSelectedIndex(clickIndex, force: true);
                ItemSelectedCommand?.Execute(clickIndex);
            }

            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, _itemsSnapshot.Length - 1));
        _savedScrollXOnDragFinish = _knownScrollX;

        if (targetIndex == _draggingIndex)
        {
            _visual?.SendHandlerMessage(new AlbumRowDragFinalizeMessage());
            ClearDragState(pointer);
            return;
        }

        _visual?.SendHandlerMessage(new AlbumRowDropTargetMessage(targetIndex));
        _visual?.SendHandlerMessage(new AlbumRowDragCommitMessage(targetIndex));

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

        _visual?.SendHandlerMessage(new AlbumRowDragFinalizeMessage());
        ClearDragState(null);
    }

    private void CompleteDragReorder(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _itemsSnapshot.Length || to >= _itemsSnapshot.Length)
            return;

        _isInternalMove = true;
        try
        {
            MoveItem(from, to);
            MoveSnapshotItem(from, to);
            SwapTileCoverState(from, to);
            SendTitles();
            _lastCoverLoadScrollX = double.NaN;
            EnsureVisibleTileCoversLoaded();

            SyncKnownScrollX(_savedScrollXOnDragFinish);
            _visual?.SendHandlerMessage(new AlbumRowSnapScrollMessage(_savedScrollXOnDragFinish));
            PublishSelectedIndexWithoutScroll(to);

            if (_itemsSnapshot[to] is FolderMediaItem folder)
                DropCommand?.Execute(folder);
        }
        finally
        {
            _isInternalMove = false;
        }
    }

    private void MoveItem(int from, int to)
    {
        if (ItemsSource is IList list && from >= 0 && to >= 0 && from < list.Count && to < list.Count)
        {
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
        }
    }

    private void MoveSnapshotItem(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _itemsSnapshot.Length || to >= _itemsSnapshot.Length)
            return;

        var updated = _itemsSnapshot.ToList();
        var item = updated[from];
        updated.RemoveAt(from);
        updated.Insert(to, item);
        _itemsSnapshot = updated.ToArray();
    }

    private void EndVisualDrag() => _visual?.SendHandlerMessage(new AlbumRowDragCancelMessage());

    private void ClearDragState(IPointer? pointer)
    {
        _isDragging = false;
        _hasDragMoved = false;
        _visualDragActive = false;
        _draggingIndex = -1;
        _dragStartIndex = -1;
        _currentDragTargetIndex = -1;
        _lastSentDropTargetIndex = -1;
        _pendingReorderFrom = -1;
        _pendingReorderTo = -1;
        _dragPointerOffset = default;
        ResetPointerInteraction(applyInertia: false);
        pointer?.Capture(null);
    }

    private void PublishSelectedIndexWithoutScroll(int index)
    {
        if (_itemsSnapshot.Length == 0)
            return;

        index = Math.Clamp(index, 0, _itemsSnapshot.Length - 1);
        SelectedIndex = index;
        _visual?.SendHandlerMessage(new AlbumRowSelectedIndexMessage(index));
    }

    private static SKColor GetSkColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush solid)
            return SKColor.Parse(solid.Color.ToString());
        return SKColor.Parse("#101010");
    }

    private void UpdateItems()
    {
        if (_subscribedItemsSource != ItemsSource)
        {
            if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;

            ClearSubscriptions();
            _subscribedItemsSource = ItemsSource;
            if (_subscribedItemsSource is INotifyCollectionChanged newIncc)
                newIncc.CollectionChanged += ItemsSource_CollectionChanged;
        }

        _itemsSnapshot = ItemsSource?.Cast<object?>().ToArray() ?? [];
        _tileCoverFingerprints.Clear();
        _lastCoverLoadScrollX = double.NaN;
        SendTitles();
        SubscribeAllItems();
        EnsureVisibleTileCoversLoaded();

        int initialIndex = (int)Math.Clamp(Math.Round(SelectedIndex), 0, Math.Max(0, _itemsSnapshot.Length - 1));
        _visual?.SendHandlerMessage(new AlbumRowSelectedIndexMessage(initialIndex));
        if (_itemsSnapshot.Length > 0)
        {
            if (Bounds.Width <= 0)
                _pendingScrollResetOnLayout = true;
            else
                ResetScrollToStart();
        }
        else
        {
            _pendingScrollResetOnLayout = false;
        }
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isInternalMove)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move &&
                e.OldStartingIndex != e.NewStartingIndex &&
                e.OldStartingIndex >= 0 && e.OldStartingIndex < _itemsSnapshot.Length &&
                e.NewStartingIndex >= 0 && e.NewStartingIndex < _itemsSnapshot.Length)
            {
                MoveSnapshotItem(e.OldStartingIndex, e.NewStartingIndex);
                SwapTileCoverState(e.OldStartingIndex, e.NewStartingIndex);
                SendTitles();
                _lastCoverLoadScrollX = double.NaN;
                EnsureVisibleTileCoversLoaded();
                return;
            }

            if (e.Action == NotifyCollectionChangedAction.Add &&
                e.NewStartingIndex >= 0 &&
                e.NewItems is { Count: > 0 })
            {
                _itemsSnapshot = ItemsSource?.Cast<object?>().ToArray() ?? [];
                SendTitles();
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    int index = e.NewStartingIndex + i;
                    SubscribeItemAt(index);
                    _tileCoverFingerprints.Remove(index);
                    PushTileCovers(index);
                }

                RenameOverlayLayoutRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            UpdateItems();
        });
    }

    private void ClearSubscriptions()
    {
        foreach (var item in _subscribedItems)
        {
            if (item is FolderMediaItem folder)
            {
                folder.PreviewItems.CollectionChanged -= PreviewItems_CollectionChanged;
                folder.Children.CollectionChanged -= Children_CollectionChanged;
            }

            item.PropertyChanged -= Item_PropertyChanged;
        }

        _subscribedItems.Clear();
        foreach (var child in _subscribedPreviewItems)
            child.PropertyChanged -= PreviewItem_PropertyChanged;
        _subscribedPreviewItems.Clear();
        foreach (var child in _subscribedChildItems)
            child.PropertyChanged -= ChildItem_PropertyChanged;
        _subscribedChildItems.Clear();
        _tileCoverFingerprints.Clear();
    }

    private void SubscribeAllItems()
    {
        for (int i = 0; i < _itemsSnapshot.Length; i++)
        {
            if (_itemsSnapshot[i] is not INotifyPropertyChanged inpc || !_subscribedItems.Add(inpc))
                continue;

            inpc.PropertyChanged += Item_PropertyChanged;
            if (_itemsSnapshot[i] is FolderMediaItem folder)
            {
                folder.PreviewItems.CollectionChanged += PreviewItems_CollectionChanged;
                folder.Children.CollectionChanged += Children_CollectionChanged;
                SubscribePreviewItems(folder);
                SubscribeChildItems(folder);
            }
        }
    }

    private void SubscribeItemAt(int index)
    {
        if (index < 0 || index >= _itemsSnapshot.Length)
            return;

        if (_itemsSnapshot[index] is not INotifyPropertyChanged inpc || !_subscribedItems.Add(inpc))
            return;

        inpc.PropertyChanged += Item_PropertyChanged;
        if (_itemsSnapshot[index] is FolderMediaItem folder)
        {
            folder.PreviewItems.CollectionChanged += PreviewItems_CollectionChanged;
            folder.Children.CollectionChanged += Children_CollectionChanged;
            SubscribePreviewItems(folder);
            SubscribeChildItems(folder);
        }
    }

    private void SubscribePreviewItems(FolderMediaItem folder)
    {
        foreach (var child in folder.PreviewItems)
        {
            if (_subscribedPreviewItems.Add(child))
                child.PropertyChanged += PreviewItem_PropertyChanged;
        }
    }

    private void SubscribeChildItems(FolderMediaItem folder)
    {
        foreach (var child in folder.GetPresentationCoverChildren())
        {
            if (_subscribedChildItems.Add(child))
                child.PropertyChanged += ChildItem_PropertyChanged;
        }
    }

    private void Children_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SendTitles();
        if (sender is not AvaloniaList<MediaItem> list)
            return;

        var folder = _itemsSnapshot.OfType<FolderMediaItem>().FirstOrDefault(f => ReferenceEquals(f.Children, list));
        if (folder == null)
            return;

        SubscribeChildItems(folder);
        int index = Array.IndexOf(_itemsSnapshot, folder);
        if (index >= 0)
            SchedulePushTileCovers(index);
    }

    private void PreviewItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not AvaloniaList<MediaItem> list)
            return;

        var folder = _itemsSnapshot.OfType<FolderMediaItem>().FirstOrDefault(f => ReferenceEquals(f.PreviewItems, list));
        if (folder == null)
            return;

        SubscribePreviewItems(folder);
        SchedulePushTileCovers(Array.IndexOf(_itemsSnapshot, folder));
    }

    private void PreviewItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaItem.CoverBitmap))
            return;

        if (sender is not MediaItem mediaItem)
            return;

        for (int i = 0; i < _itemsSnapshot.Length; i++)
        {
            if (_itemsSnapshot[i] is FolderMediaItem folder && folder.PreviewItems.Contains(mediaItem))
            {
                SchedulePushTileCovers(i);
                return;
            }
        }
    }

    private void ChildItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaItem.CoverBitmap))
            return;

        if (sender is not MediaItem mediaItem)
            return;

        for (int i = 0; i < _itemsSnapshot.Length; i++)
        {
            if (_itemsSnapshot[i] is FolderMediaItem folder && folder.Children.Contains(mediaItem))
            {
                SchedulePushTileCovers(i);
                return;
            }
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        int index = Array.IndexOf(_itemsSnapshot, sender);
        if (index < 0)
            return;

        if (e.PropertyName is nameof(MediaItem.Title) or nameof(FolderMediaItem.Children) or nameof(FolderMediaItem.TotalChildCount) or nameof(MediaItem.IsLoadingCover))
        {
            ScheduleSendTitles();
            return;
        }

        if (e.PropertyName == nameof(FolderMediaItem.PreviewItems) && sender is FolderMediaItem folder)
        {
            folder.PreviewItems.CollectionChanged -= PreviewItems_CollectionChanged;
            folder.PreviewItems.CollectionChanged += PreviewItems_CollectionChanged;
            SubscribePreviewItems(folder);
            SchedulePushTileCovers(index);
            return;
        }

        if (e.PropertyName is nameof(MediaItem.CoverBitmap) or nameof(FolderMediaItem.PreviewItems))
            SchedulePushTileCovers(index);
    }

    /// <summary>
    /// Rebuilds all album-row tile cover visuals from the current item bindings.
    /// </summary>
    public void RefreshAllTileCovers()
    {
        PostToUi(() =>
        {
            _tileCoverFingerprints.Clear();
            _lastCoverLoadScrollX = double.NaN;
            for (int i = 0; i < _itemsSnapshot.Length; i++)
                SchedulePushTileCovers(i);
        });
    }

    private void PostToUi(Action action) => PostToUi(action, DispatcherPriority.Background);

    private void PostToUi(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action, priority);
    }

    private void SchedulePushTileCovers(int index)
    {
        if (index < 0)
            return;

        _tileCoverFingerprints.Remove(index);
        _pendingTileCoverIndices.Add(index);

        if (IsAlbumRowScrollActive())
            return;

        EnsureTileCoverReloadDebounceTimer();
    }

    private void EnsureTileCoverReloadDebounceTimer()
    {
        _tileCoverReloadDebounceTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TileCoverReloadDebounceMs)
        };
        _tileCoverReloadDebounceTimer.Tick -= TileCoverReloadDebounceTimer_Tick;
        _tileCoverReloadDebounceTimer.Tick += TileCoverReloadDebounceTimer_Tick;
        _tileCoverReloadDebounceTimer.Stop();
        _tileCoverReloadDebounceTimer.Start();
    }

    private void FlushDeferredTileCoverPushes()
    {
        if (_pendingTileCoverIndices.Count == 0)
            return;

        EnsureTileCoverReloadDebounceTimer();
    }

    private void TileCoverReloadDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _tileCoverReloadDebounceTimer?.Stop();
        if (_pendingTileCoverIndices.Count == 0)
            return;

        var indices = _pendingTileCoverIndices.ToArray();
        _pendingTileCoverIndices.Clear();
        PostToUi(() =>
        {
            foreach (var index in indices)
                PushTileCoversCore(index);
        });
    }

    private void ScheduleSendTitles() => PostToUi(SendTitles);

    private void SendTitles()
    {
        var titles = new string[_itemsSnapshot.Length];
        var counts = new int[_itemsSnapshot.Length];
        var loading = new bool[_itemsSnapshot.Length];
        for (int i = 0; i < _itemsSnapshot.Length; i++)
        {
            if (_itemsSnapshot[i] is FolderMediaItem folder)
            {
                titles[i] = folder.Title ?? string.Empty;
                counts[i] = folder.TotalChildCount > 0 ? folder.TotalChildCount : folder.Children.Count;
                loading[i] = folder.IsLoadingCover && !HasVisibleAlbumPreview(folder);
            }
        }

        _visual?.SendHandlerMessage(new AlbumRowTitlesMessage(titles, counts, loading));
    }

    private static bool HasVisibleAlbumPreview(FolderMediaItem folder)
    {
        var preview = folder.PreviewItems;
        if (preview == null || preview.Count == 0)
            return folder.CoverBitmap != null;

        return preview.Any(item => item.CoverBitmap != null);
    }

    private void EnsureVisibleTileCoversLoadedIfNeeded()
    {
        if (_itemsSnapshot.Length == 0)
            return;

        float stride = (float)(AlbumRowLayoutHelper.BaseTileWidth * TileScale + TileSpacing);
        if (double.IsNaN(_lastCoverLoadScrollX) || Math.Abs(_knownScrollX - _lastCoverLoadScrollX) >= stride * 0.5)
            EnsureVisibleTileCoversLoaded();
    }

    private void EnsureVisibleTileCoversLoaded(int buffer = 2)
    {
        if (_visual == null || _itemsSnapshot.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var (start, end) = AlbumRowLayoutHelper.GetVisibleIndexRange(
            _knownScrollX,
            (float)Bounds.Width,
            (float)Bounds.Height,
            _itemsSnapshot.Length,
            (float)TileScale,
            (float)TileSpacing,
            buffer);

        if (start < 0 || end < start)
            return;

        int selected = (int)Math.Clamp(Math.Round(SelectedIndex), 0, _itemsSnapshot.Length - 1);
        start = Math.Min(start, selected);
        end = Math.Max(end, selected);
        _lastCoverLoadScrollX = _knownScrollX;

        for (int i = start; i <= end; i++)
        {
            if (_itemsSnapshot[i] is FolderMediaItem folder && IsTileCoverCurrent(i, folder))
                continue;

            PushTileCovers(i);
        }
    }

    private static Bitmap?[] CapturePreviewCoverBitmaps(FolderMediaItem folder)
    {
        var preview = folder.PreviewItems;
        if (preview == null || preview.Count == 0)
            return [];

        var covers = new Bitmap?[preview.Count];
        for (int i = 0; i < preview.Count; i++)
            covers[i] = preview[i].CoverBitmap;

        return covers;
    }

    private bool IsTileCoverCurrent(int index, FolderMediaItem folder)
    {
        if (!_tileCoverFingerprints.TryGetValue(index, out var pushed))
            return false;

        var previewCovers = CapturePreviewCoverBitmaps(folder);
        return pushed.Matches(folder, folder.CoverBitmap, previewCovers);
    }

    private void SwapTileCoverState(int from, int to)
    {
        if (from == to)
            return;

        _tileCoverFingerprints.Remove(from, out var fromFingerprint);
        _tileCoverFingerprints.Remove(to, out var toFingerprint);

        if (fromFingerprint.Folder != null)
            _tileCoverFingerprints[to] = fromFingerprint;
        if (toFingerprint.Folder != null)
            _tileCoverFingerprints[from] = toFingerprint;

        _visual?.SendHandlerMessage(new AlbumRowSwapTileCoversMessage(from, to));
    }

    private void PushTileCovers(int index)
    {
        if (IsAlbumRowScrollActive())
        {
            SchedulePushTileCovers(index);
            return;
        }

        PushTileCoversCore(index);
    }

    private void PushTileCoversCore(int index)
    {
        if (_visual == null || index < 0 || index >= _itemsSnapshot.Length)
            return;

        if (_itemsSnapshot[index] is not FolderMediaItem folder)
            return;

        if (folder.PreviewItems.Count == 0)
            folder.RebuildPreviewItems(useFirstItemCover: true);

        folder.SyncAlbumTileTopCoverFromChildren();
        var previewCovers = CapturePreviewCoverBitmaps(folder);
        if (IsTileCoverCurrent(index, folder))
            return;

        var snapshots = BuildSnapshots(folder);
        var defaultSk = CompositionBitmapHelper.ToSkImage(folder.CoverBitmap, CompositionBitmapHelper.FolderCoverMaxEdge);
        _tileCoverFingerprints[index] = new TileCoverFingerprint(folder, folder.CoverBitmap, previewCovers);
        _visual?.SendHandlerMessage(new AlbumRowTileCoversMessage(index, snapshots, defaultSk));
    }

    private static List<FolderItemSnapshot> BuildSnapshots(FolderMediaItem folder)
    {
        var list = new List<FolderItemSnapshot>();
        var items = folder.PreviewItems;
        if (items == null || items.Count == 0)
        {
            list.Add(new FolderItemSnapshot(null, true));
            return list;
        }

        int count = items.Count;
        int startIndex = Math.Max(0, count - FolderMediaItem.AlbumTilePresentationCoverCount);
        for (int i = startIndex; i < count; i++)
        {
            var item = items[i];
            var sk = CompositionBitmapHelper.ToSkImage(item.CoverBitmap, CompositionBitmapHelper.FolderCoverMaxEdge);
            list.Add(new FolderItemSnapshot(sk, false));
        }

        return list;
    }

    private void SendLayoutMessage() =>
        _visual?.SendHandlerMessage(new AlbumRowLayoutMessage((float)TileScale, (float)TileSpacing));

    private void UpdateCompositionVisualSize(Size size)
    {
        if (_visual == null || size.Width <= 0 || size.Height <= 0)
            return;

        var logicalSize = new Vector2((float)size.Width, (float)size.Height);
        _visual.Size = logicalSize;
        _visual.SendHandlerMessage(logicalSize);
    }
}
