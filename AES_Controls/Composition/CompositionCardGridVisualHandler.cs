using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace AES_Controls.Composition;

internal record CardGridScrollMessage(double TargetScrollY);
internal record CardGridScrollVelocityMessage(double VelocityY);
internal record CardGridDirectScrollFollowMessage(bool Enabled);
internal record CardGridSnapScrollMessage(double ScrollY);
internal record CardGridLayoutMessage(float CardScale, float CardSpacing, float TopPadding);
internal record CardGridTitlesMessage(string[] Titles);
internal record CardGridSelectedIndexMessage(int Index);
internal record CardGridHoveredIndexMessage(int Index);
internal record CardGridScrollbarPressedMessage(bool IsPressed);
internal record CardGridScrollbarHoverMessage(bool IsHovered);
internal record CardGridResetScrollbarMessage();
internal record CardGridBackgroundColorMessage(SKColor Color);
internal record CardGridTitleMarqueeMessage(bool Enabled);
internal record CardGridHorizontalScrollMessage(bool Enabled);
internal record CardGridAttachSyncMessage(CardGridAnimationSyncState State);
internal record CardGridDragStateMessage(int Index, bool IsDragging);
internal record CardGridDragPositionMessage(Vector2 Position);
internal record CardGridDropTargetMessage(int Index);
internal record CardGridDragCancelMessage();
internal record CardGridDragCommitMessage(int TargetIndex);
internal record CardGridDragFinalizeMessage();
internal record CardGridContentLoadingMessage(bool IsLoading);

public class CompositionCardGridVisualHandler : CompositionCustomVisualHandler
{
    private const float BaseCardWidth = 200f;
    private const float BaseCardHeight = 272f;
    private const float GridPaddingX = 28f;
    private const float GridPaddingTop = 20f;
    private const float ScrollbarMargin = 10f;
    private static readonly SKColor DefaultBackgroundColor = SKColor.Parse("#101010");
    private const float CardCornerRadius = 12f;
    private const float TitleAreaRatio = 0.24f;
    private const float TitleTextSizeRatio = 0.09f;
    private const float TitleTextSizeMin = 17f;
    private const float TitleTextSizeMax = 22f;
    private const float MaxFullCoverAspectRatio = 1.35f;
    private const float ViewportFadeHeightRatio = 0.14f;
    private const float ViewportFadeMinHeight = 48f;
    private const float ViewportFadeMaxHeight = 120f;
    private const float ViewportFadeMinOpacity = 0.35f;

    private Vector2 _visualSize;
    private float _cardScale = 1f;
    private float _cardSpacing = 16f;
    private float _topPadding = 20f;
    private double _targetScrollY;
    private double _currentScrollY;
    private double _scrollVelocity;
    private double _scrollSpringVelocity;
    private bool _directScrollFollow;
    private long _lastTicks;
    private bool _isScrollbarPressed;
    private bool _isScrollbarHovered;
    private long _scrollbarVisibleUntilTicks;
    private float _scrollbarOpacity;
    private float _scrollbarOpacityVelocity;
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;
    private float _selectionBorderFade;
    private float _selectionPulsePhase;
    private SKColor _backgroundColor = DefaultBackgroundColor;
    private const float SelectionFadeInRate = 18f;
    private const float SelectionFadeOutRate = 22f;
    private float _currentGlobalOpacity = 1f;
    private float _targetGlobalOpacity = 1f;
    private float _currentGlobalOpacityVelocity;
    private bool _pauseLoadingSpinnerAnimation;
    private bool _titleMarqueeEnabled = true;
    private bool _horizontalScrollEnabled;
    private float _marqueeOffset;
    private float _marqueeTime;
    private float _marqueeScrollRange;
    private bool _marqueeActive;
    private CardGridAnimationSyncState? _animationSync;
    private int _draggingIndex = -1;
    private int _dropTargetIndex = -1;
    private Vector2 _dragPosition;
    private bool _isDragCommitting;
    private float _dragCommitProgress;
    private const float SwapAnimationSeconds = 0.2f;
    private const float DragLiftScale = 1.04f;
    private readonly Dictionary<int, Vector2> _swapOffsets = new();
    private readonly Dictionary<int, Vector2> _swapOffsetTargets = new();

    private List<SKImage?> _images = new();
    private string[] _titles = Array.Empty<string>();
    private HashSet<int> _loadingIndices = new();
    private float _spinnerRotation;
    private bool _isContentLoading;

    private readonly SKPaint _cardPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private readonly SKPaint _titlePaint = CreateTitlePaint(SKColors.White);
    private readonly SKPaint _titleShadowPaint = CreateTitlePaint(SKColors.Black.WithAlpha(140));
    private readonly SKPaint _overlayPaint = new() { IsAntialias = true };
    private readonly SKPaint _scrollbarPaint = new() { IsAntialias = true };
    private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
    private readonly SKMaskFilter _scrollbarBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
    private readonly SKMaskFilter _selectionGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
    private readonly Dictionary<SKImage, (int Width, int Height)> _dimCache = new();
    private readonly Dictionary<SKImage, SKImage> _blurBackdropCache = new();
    private readonly Dictionary<int, (float Strength, float Velocity)> _hoverLift = new();
    private const float HoverLiftScale = 0.034f;
    private const float SelectionLiftScale = 0.072f;
    private const int BlurBackdropCacheWidth = 128;
    private const int BlurBackdropCacheHeight = 172;
    private SKShader? _trackShader;
    private SKShader? _thumbShader;

    private readonly SKColor _trackColor1 = SKColor.Parse("#333333").WithAlpha(120);
    private readonly SKColor _trackColor2 = SKColor.Parse("#666666").WithAlpha(120);
    private readonly SKColor _thumbColor1 = SKColors.White.WithAlpha(220);
    private readonly SKColor _thumbColor2 = SKColor.Parse("#E8E8E8").WithAlpha(220);

    private readonly struct GridMetrics
    {
        public int Columns { get; init; }
        public float CardWidth { get; init; }
        public float CardHeight { get; init; }
        public float Spacing { get; init; }
        public float PaddingLeft { get; init; }
        public float PaddingTop { get; init; }
        public float ContentWidth { get; init; }
        public float ContentHeight { get; init; }
        public float MaxScrollY { get; init; }
        public int RowCount { get; init; }
        public float ColumnPitch { get; init; }
    }

    public override void OnMessage(object message)
    {
        switch (message)
        {
            case Vector2 size:
                _visualSize = size;
                Invalidate();
                break;
            case IEnumerable<SKImage> enumerable when message is not string:
            {
                var imgs = enumerable.ToArray();
                var previous = _images.ToArray();
                _images = [.. imgs];
                foreach (var img in previous)
                {
                    if (img != null && !_images.Contains(img))
                        RemoveImageCaches(img);
                }

                foreach (var img in _images)
                {
                    if (img != null)
                        CacheImageDimensions(img);
                }

                Invalidate();
                break;
            }
            case UpdateImageMessage update:
                if (update.Index >= 0 && update.Index < _images.Count)
                {
                    var oldImg = _images[update.Index];
                    if (update.Image != null)
                    {
                        _images[update.Index] = update.Image;
                        CacheImageDimensions(update.Image);
                        _loadingIndices.Remove(update.Index);
                    }
                    else if (update.IsLoading)
                        _loadingIndices.Add(update.Index);
                    else if (update.ClearImage)
                    {
                        _images[update.Index] = null;
                        _loadingIndices.Remove(update.Index);
                    }
                    else
                        _loadingIndices.Remove(update.Index);

                    if (oldImg != null && !ReferenceEquals(oldImg, update.Image))
                        RemoveImageCaches(oldImg);
                    Invalidate();
                }
                break;
            case CardGridScrollMessage scroll:
                _targetScrollY = scroll.TargetScrollY;
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                break;
            case CardGridScrollVelocityMessage velocity:
                _scrollVelocity = velocity.VelocityY;
                ExtendScrollbarVisibility(1.25);
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                break;
            case CardGridDirectScrollFollowMessage direct:
                _directScrollFollow = direct.Enabled;
                ExtendScrollbarVisibility(direct.Enabled ? 0.0 : 0.9);
                if (direct.Enabled && _lastTicks == 0)
                    _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                break;
            case CardGridSnapScrollMessage snap:
                _targetScrollY = snap.ScrollY;
                _currentScrollY = snap.ScrollY;
                _scrollVelocity = 0;
                _scrollSpringVelocity = 0;
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                break;
            case CardGridLayoutMessage layout:
                _cardScale = layout.CardScale;
                _cardSpacing = layout.CardSpacing;
                _topPadding = layout.TopPadding;
                Invalidate();
                break;
            case CardGridTitlesMessage titles:
                _titles = titles.Titles;
                _marqueeTime = 0f;
                _marqueeOffset = 0f;
                Invalidate();
                break;
            case CardGridSelectedIndexMessage selected:
                if (selected.Index != _selectedIndex)
                {
                    _marqueeTime = 0f;
                    _marqueeOffset = 0f;
                    _marqueeScrollRange = 0f;
                    _marqueeActive = false;
                }

                _selectedIndex = selected.Index;
                if (_selectedIndex < 0)
                    _selectionPulsePhase = 0f;
                RequestScrollbarAnimationFrame();
                break;
            case CardGridHoveredIndexMessage hovered:
                if (hovered.Index != _hoveredIndex)
                {
                    _hoveredIndex = hovered.Index;
                    if (_hoveredIndex >= 0 && !_hoverLift.ContainsKey(_hoveredIndex))
                        _hoverLift[_hoveredIndex] = (0f, 0f);
                    RequestScrollbarAnimationFrame();
                }
                break;
            case CardGridScrollbarPressedMessage scrollbar:
                _isScrollbarPressed = scrollbar.IsPressed;
                if (scrollbar.IsPressed)
                    ExtendScrollbarVisibility(2.0);
                RequestScrollbarAnimationFrame();
                break;
            case CardGridScrollbarHoverMessage hover:
                _isScrollbarHovered = hover.IsHovered;
                RequestScrollbarAnimationFrame();
                break;
            case CardGridResetScrollbarMessage:
                _scrollbarVisibleUntilTicks = 0;
                if (!_isScrollbarHovered && !_isScrollbarPressed && !_directScrollFollow)
                {
                    _scrollbarOpacity = 0f;
                    _scrollbarOpacityVelocity = 0f;
                }
                RequestScrollbarAnimationFrame();
                break;
            case GlobalOpacityMessage opacity:
                _targetGlobalOpacity = (float)Math.Clamp(opacity.Value, 0.0, 1.0);
                _currentGlobalOpacity = _targetGlobalOpacity;
                _currentGlobalOpacityVelocity = 0;
                Invalidate();
                break;
            case PauseLoadingSpinnerAnimationMessage pause:
                _pauseLoadingSpinnerAnimation = pause.IsPaused;
                if (!_pauseLoadingSpinnerAnimation && (_loadingIndices.Count > 0 || _isContentLoading))
                {
                    if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                    RegisterForNextAnimationFrameUpdate();
                }
                break;
            case CardGridContentLoadingMessage contentLoading:
                _isContentLoading = contentLoading.IsLoading;
                if (_isContentLoading && !_pauseLoadingSpinnerAnimation)
                {
                    if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                    RegisterForNextAnimationFrameUpdate();
                }
                Invalidate();
                break;
            case CardGridBackgroundColorMessage background:
                _backgroundColor = background.Color;
                Invalidate();
                break;
            case CardGridTitleMarqueeMessage marquee:
                _titleMarqueeEnabled = marquee.Enabled;
                _marqueeTime = 0f;
                _marqueeOffset = 0f;
                Invalidate();
                break;
            case CardGridHorizontalScrollMessage horizontal:
                _horizontalScrollEnabled = horizontal.Enabled;
                Invalidate();
                break;
            case CardGridAttachSyncMessage attach:
                _animationSync = attach.State;
                break;
            case CardGridDragStateMessage dragState:
                if (dragState.IsDragging)
                {
                    _draggingIndex = dragState.Index;
                    _dropTargetIndex = dragState.Index;
                    _isDragCommitting = false;
                    _dragCommitProgress = 0f;
                    _swapOffsets.Clear();
                    _swapOffsetTargets.Clear();
                }
                else if (!_isDragCommitting)
                {
                    ClearDragVisualState();
                }

                RequestScrollbarAnimationFrame();
                break;
            case CardGridDragPositionMessage dragPosition:
                _dragPosition = dragPosition.Position;
                if (_draggingIndex != -1 && !_isDragCommitting)
                    RequestScrollbarAnimationFrame();
                else
                    Invalidate();
                break;
            case CardGridDropTargetMessage dropTarget:
                if (dropTarget.Index != _dropTargetIndex)
                {
                    _dropTargetIndex = dropTarget.Index;
                    UpdateSwapOffsetTargets();
                    RequestScrollbarAnimationFrame();
                }
                break;
            case CardGridDragCancelMessage:
                ClearDragVisualState();
                RequestScrollbarAnimationFrame();
                break;
            case CardGridDragCommitMessage commit:
                _dropTargetIndex = commit.TargetIndex;
                UpdateSwapOffsetTargets();
                _isDragCommitting = true;
                _dragCommitProgress = 0f;
                RequestScrollbarAnimationFrame();
                break;
            case CardGridDragFinalizeMessage:
                ClearDragVisualState();
                Invalidate();
                break;
        }
    }

    public override void OnAnimationFrameUpdate()
    {
        long currentTicks = Stopwatch.GetTimestamp();
        if (_lastTicks == 0) _lastTicks = currentTicks;
        double dt = (double)(currentTicks - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = currentTicks;
        if (dt > 0.1) dt = 0.1;

        var metrics = ComputeMetrics(_images.Count);
        double maxScroll = metrics.MaxScrollY;

        if (_directScrollFollow)
        {
            _currentScrollY = _targetScrollY;
            _scrollVelocity = 0;
            _scrollSpringVelocity = 0;
        }
        else
        {
            if (Math.Abs(_scrollVelocity) > 0.5)
            {
                _targetScrollY += _scrollVelocity * dt;
                _scrollVelocity *= Math.Exp(-2.15 * dt);
            }
            else
            {
                _scrollVelocity = 0;
            }

            if (_targetScrollY < 0)
            {
                _targetScrollY += (-_targetScrollY) * Math.Min(1.0, 12.0 * dt);
                if (Math.Abs(_targetScrollY) < 0.5 && Math.Abs(_scrollVelocity) < 2)
                    _targetScrollY = 0;
            }
            else if (_targetScrollY > maxScroll)
            {
                double overshoot = _targetScrollY - maxScroll;
                _targetScrollY -= overshoot * Math.Min(1.0, 12.0 * dt);
                if (overshoot < 0.5 && Math.Abs(_scrollVelocity) < 2)
                    _targetScrollY = maxScroll;
            }

            double distance = _targetScrollY - _currentScrollY;
            double stiffness = 420.0;
            double damping = 2.0 * Math.Sqrt(stiffness) * 0.92;
            _scrollSpringVelocity += (distance * stiffness - _scrollSpringVelocity * damping) * dt;
            _currentScrollY += _scrollSpringVelocity * dt;

            if (Math.Abs(distance) < 0.01 && Math.Abs(_scrollSpringVelocity) < 0.01)
            {
                _currentScrollY = _targetScrollY;
                _scrollSpringVelocity = 0;
            }
        }

        if (!_directScrollFollow)
            _spinnerRotation = (_spinnerRotation + 8f) % 360f;

        bool needsScrollbar = maxScroll > 1;
        bool scrollActive = _directScrollFollow ||
                            Math.Abs(_scrollVelocity) > 2 ||
                            Stopwatch.GetTimestamp() < _scrollbarVisibleUntilTicks;
        float desiredScrollbarOpacity = needsScrollbar && (_isScrollbarPressed || _isScrollbarHovered || scrollActive)
            ? 1f
            : 0f;

        if (Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.001f || Math.Abs(_scrollbarOpacityVelocity) > 0.001f)
        {
            double opStiffness = 45.0;
            double opDamping = 2.0 * Math.Sqrt(opStiffness);
            _scrollbarOpacityVelocity += (float)((desiredScrollbarOpacity - _scrollbarOpacity) * opStiffness - _scrollbarOpacityVelocity * opDamping) * (float)dt;
            _scrollbarOpacity += _scrollbarOpacityVelocity * (float)dt;
            _scrollbarOpacity = Math.Clamp(_scrollbarOpacity, 0f, 1f);
        }
        else
        {
            _scrollbarOpacity = desiredScrollbarOpacity;
            _scrollbarOpacityVelocity = 0;
        }

        if (Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.0005f || Math.Abs(_currentGlobalOpacityVelocity) > 0.0005f)
        {
            double opStiffness = 30.0;
            double opDamping = 2.0 * Math.Sqrt(opStiffness);
            _currentGlobalOpacityVelocity += (float)((_targetGlobalOpacity - _currentGlobalOpacity) * opStiffness - _currentGlobalOpacityVelocity * opDamping) * (float)dt;
            _currentGlobalOpacity += _currentGlobalOpacityVelocity * (float)dt;
            _currentGlobalOpacity = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
        }

        float fadeTarget = _selectedIndex >= 0 ? 1f : 0f;
        bool selectionFading = Math.Abs(_selectionBorderFade - fadeTarget) > 0.001f;
        if (selectionFading)
        {
            float rate = fadeTarget > _selectionBorderFade ? SelectionFadeInRate : SelectionFadeOutRate;
            _selectionBorderFade += (fadeTarget - _selectionBorderFade) * Math.Min(1f, (float)dt * rate);
            _selectionBorderFade = Math.Clamp(_selectionBorderFade, 0f, 1f);
        }
        else
        {
            _selectionBorderFade = fadeTarget;
        }

        bool selectionPulsing = _selectedIndex >= 0 && _selectionBorderFade > 0.2f;
        if (selectionPulsing)
            _selectionPulsePhase += (float)dt * 3.1f;

        bool marqueeAnimating = UpdateMarquee((float)dt);

        bool dragAnimating = AnimateSwapOffsets((float)dt);
        if (_isDragCommitting && _dragCommitProgress < 1f)
        {
            _dragCommitProgress += (float)(dt / SwapAnimationSeconds);
            if (_dragCommitProgress > 1f)
                _dragCommitProgress = 1f;
            dragAnimating = true;
        }

        bool hoverAnimating = false;
        if (_hoverLift.Count > 0)
        {
            var finished = new List<int>();
            foreach (var index in _hoverLift.Keys.ToArray())
            {
                float target = index == _hoveredIndex ? 1f : 0f;
                var (strength, velocity) = _hoverLift[index];
                if (Math.Abs(strength - target) > 0.001f || Math.Abs(velocity) > 0.001f)
                {
                    double hoverStiffness = 260.0;
                    double hoverDamping = 2.0 * Math.Sqrt(hoverStiffness) * 0.88;
                    velocity += (float)((target - strength) * hoverStiffness - velocity * hoverDamping) * (float)dt;
                    strength += velocity * (float)dt;
                    strength = Math.Clamp(strength, 0f, 1f);
                    _hoverLift[index] = (strength, velocity);
                    hoverAnimating = true;
                }
                else
                {
                    _hoverLift[index] = (target, 0f);
                    if (target <= 0f)
                        finished.Add(index);
                }
            }

            foreach (var index in finished)
                _hoverLift.Remove(index);
        }

        bool isAnimating = _directScrollFollow ||
                           Math.Abs(_targetScrollY - _currentScrollY) > 0.01 ||
                           Math.Abs(_scrollVelocity) > 0.5 ||
                           Math.Abs(_scrollSpringVelocity) > 0.01 ||
                           Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f ||
                           Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.01 ||
                           selectionFading ||
                           selectionPulsing ||
                           marqueeAnimating ||
                           hoverAnimating ||
                           dragAnimating;
        bool animateSpinners = !_pauseLoadingSpinnerAnimation &&
                               (_loadingIndices.Count > 0 || (_isContentLoading && _images.Count == 0));

        if (isAnimating || animateSpinners)
        {
            RegisterForNextAnimationFrameUpdate();
            Invalidate();
        }
        else
        {
            _lastTicks = 0;
        }

        if (_animationSync != null)
        {
            _animationSync.CurrentScrollY = _currentScrollY;
            _animationSync.TargetScrollY = _targetScrollY;
            _animationSync.VelocityY = _scrollVelocity;
            _animationSync.IsAnimating = isAnimating || animateSpinners;
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;
        canvas.Clear(_backgroundColor);

        if (_images.Count == 0 || _visualSize.X <= 0 || _visualSize.Y <= 0)
        {
            if (_isContentLoading && _visualSize.X > 0 && _visualSize.Y > 0)
            {
                float loadingOpacity = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
                if (loadingOpacity > 0f)
                    DrawSpinner(canvas, _visualSize.X * 0.5f, _visualSize.Y * 0.42f, loadingOpacity);
            }

            return;
        }

        float g = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
        if (g <= 0f)
            return;

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, _visualSize.X, _visualSize.Y));
        canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)(g * 255)) });

        var metrics = ComputeMetrics(_images.Count);

        bool isDragging = _draggingIndex != -1;

        if (isDragging)
        {
            if (_draggingIndex >= 0 &&
                TryGetCardPosition(_draggingIndex, metrics, out float holeX, out float holeY) &&
                IsCardVisible(holeX, holeY, metrics.CardWidth, metrics.CardHeight))
            {
                DrawPlaceholder(canvas, holeX, holeY, metrics.CardWidth, metrics.CardHeight);
            }

            for (int index = 0; index < _images.Count; index++)
            {
                if (index == _selectedIndex || index == _draggingIndex)
                    continue;

                if (!TryGetCardDrawPosition(index, metrics, out float x, out float y))
                    continue;
                if (!IsCardVisible(x, y, metrics.CardWidth, metrics.CardHeight))
                    continue;

                DrawCard(canvas, index, x, y, metrics, 1f);
            }
        }
        else
        {
            if (_horizontalScrollEnabled)
            {
                int firstCol = Math.Max(0, (int)Math.Floor((_currentScrollY - metrics.PaddingLeft) / metrics.ColumnPitch) - 1);
                int lastCol = Math.Min(
                    metrics.Columns - 1,
                    (int)Math.Ceiling((_currentScrollY + _visualSize.X - metrics.PaddingLeft) / metrics.ColumnPitch) + 1);

                for (int row = 0; row < metrics.RowCount; row++)
                {
                    for (int col = firstCol; col <= lastCol; col++)
                    {
                        int index = row * metrics.Columns + col;
                        if (index >= _images.Count)
                            continue;
                        if (index == _selectedIndex)
                            continue;

                        if (!TryGetCardPosition(index, metrics, out float x, out float y))
                            continue;
                        if (!IsCardVisible(x, y, metrics.CardWidth, metrics.CardHeight))
                            continue;

                        DrawCard(canvas, index, x, y, metrics, 1f);
                    }
                }
            }
            else
            {
                int firstRow = Math.Max(0, (int)Math.Floor((_currentScrollY - metrics.PaddingTop) / (metrics.CardHeight + metrics.Spacing)) - 1);
                int lastRow = Math.Min(metrics.RowCount - 1, (int)Math.Ceiling((_currentScrollY + _visualSize.Y) / (metrics.CardHeight + metrics.Spacing)) + 1);

                for (int row = firstRow; row <= lastRow; row++)
                {
                    for (int col = 0; col < metrics.Columns; col++)
                    {
                        int index = row * metrics.Columns + col;
                        if (index >= _images.Count)
                            break;
                        if (index == _selectedIndex)
                            continue;

                        if (!TryGetCardPosition(index, metrics, out float x, out float y))
                            continue;
                        if (!IsCardVisible(x, y, metrics.CardWidth, metrics.CardHeight))
                            continue;

                        DrawCard(canvas, index, x, y, metrics, 1f);
                    }
                }
            }
        }

        if (_selectedIndex >= 0 &&
            _selectedIndex < _images.Count &&
            _selectedIndex != _draggingIndex &&
            TryGetCardDrawPosition(_selectedIndex, metrics, out float selX, out float selY) &&
            IsCardVisible(selX, selY, metrics.CardWidth, metrics.CardHeight))
        {
            DrawCard(canvas, _selectedIndex, selX, selY, metrics, 1f);
        }

        if (isDragging && _draggingIndex >= 0 && _draggingIndex < _images.Count)
            DrawDraggedCard(canvas, _draggingIndex, metrics, 1f);

        ApplyViewportFadeMask(canvas);
        DrawScrollbar(canvas, metrics, 1f);
        canvas.Restore();
        canvas.Restore();
    }

    private GridMetrics ComputeMetrics(int itemCount)
    {
        if (itemCount <= 0 || _visualSize.X <= 0)
        {
            return new GridMetrics
            {
                Columns = 1,
                CardWidth = BaseCardWidth * _cardScale,
                CardHeight = BaseCardHeight * _cardScale,
                Spacing = _cardSpacing,
                PaddingLeft = GridPaddingX,
                PaddingTop = _topPadding,
                ContentWidth = _visualSize.X,
                ContentHeight = 0,
                MaxScrollY = 0,
                RowCount = 0,
                ColumnPitch = BaseCardWidth * _cardScale
            };
        }

        if (_horizontalScrollEnabled)
        {
            var horizontal = CardGridHorizontalLayout.ComputeMetrics(
                itemCount,
                _visualSize.X,
                _visualSize.Y,
                _cardScale,
                _cardSpacing,
                _topPadding);

            float horizontalContentHeight = _topPadding + horizontal.Rows * horizontal.CardHeight +
                                            Math.Max(0, horizontal.Rows - 1) * horizontal.Spacing;

            return new GridMetrics
            {
                Columns = horizontal.Columns,
                CardWidth = horizontal.CardWidth,
                CardHeight = horizontal.CardHeight,
                Spacing = horizontal.Spacing,
                PaddingLeft = horizontal.PaddingLeft,
                PaddingTop = horizontal.PaddingTop,
                ContentWidth = horizontal.ContentWidth,
                ContentHeight = horizontalContentHeight,
                MaxScrollY = horizontal.MaxScrollX,
                RowCount = horizontal.Rows,
                ColumnPitch = horizontal.ColumnPitch
            };
        }

        float availW = Math.Max(80f, _visualSize.X - GridPaddingX * 2 - CardGridLayoutHelper.ScrollbarReserve);
        float minCardW = BaseCardWidth * _cardScale * 0.75f;
        int columns = Math.Max(1, (int)((availW + _cardSpacing) / (minCardW + _cardSpacing)));
        float cardW = (availW - _cardSpacing * (columns - 1)) / columns;
        float cardH = cardW * (BaseCardHeight / BaseCardWidth);
        int rowCount = (itemCount + columns - 1) / columns;
        float contentHeight = _topPadding + rowCount * cardH + Math.Max(0, rowCount - 1) * _cardSpacing + 28f;
        float maxScroll = Math.Max(0, contentHeight - _visualSize.Y);

        return new GridMetrics
        {
            Columns = columns,
            CardWidth = cardW,
            CardHeight = cardH,
            Spacing = _cardSpacing,
            PaddingLeft = GridPaddingX,
            PaddingTop = _topPadding,
            ContentWidth = availW,
            ContentHeight = contentHeight,
            MaxScrollY = maxScroll,
            RowCount = rowCount,
            ColumnPitch = cardW + _cardSpacing
        };
    }

    private bool TryGetCardPosition(int index, GridMetrics metrics, out float x, out float y)
    {
        if (index < 0 || index >= _images.Count)
        {
            x = 0;
            y = 0;
            return false;
        }

        if (_horizontalScrollEnabled)
        {
            int columns = Math.Max(1, metrics.Columns);
            int col = index % columns;
            int row = index / columns;
            x = metrics.PaddingLeft + col * metrics.ColumnPitch - (float)_currentScrollY;
            y = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
            return true;
        }

        int vRow = index / metrics.Columns;
        int vCol = index % metrics.Columns;
        x = metrics.PaddingLeft + vCol * (metrics.CardWidth + metrics.Spacing);
        y = metrics.PaddingTop + vRow * (metrics.CardHeight + metrics.Spacing) - (float)_currentScrollY;
        return true;
    }

    private void ClearDragVisualState()
    {
        _draggingIndex = -1;
        _dropTargetIndex = -1;
        _isDragCommitting = false;
        _dragCommitProgress = 0f;
        _swapOffsets.Clear();
        _swapOffsetTargets.Clear();
    }

    private void UpdateSwapOffsetTargets()
    {
        if (_draggingIndex < 0 || _images.Count == 0)
            return;

        _swapOffsetTargets.Clear();
        for (int i = 0; i < _images.Count; i++)
        {
            if (i == _draggingIndex)
                continue;

            var offset = CardGridLayoutHelper.GetSwapOffset(
                i,
                _draggingIndex,
                _dropTargetIndex,
                _images.Count,
                _currentScrollY,
                _visualSize.X,
                _visualSize.Y,
                _cardScale,
                _cardSpacing,
                _topPadding,
                _horizontalScrollEnabled);
            _swapOffsetTargets[i] = new Vector2((float)offset.X, (float)offset.Y);
            if (!_swapOffsets.ContainsKey(i))
                _swapOffsets[i] = Vector2.Zero;
        }
    }

    private bool AnimateSwapOffsets(float dt)
    {
        if (_draggingIndex < 0)
            return false;

        bool animating = _isDragCommitting;
        float step = 1f - MathF.Exp(-dt / Math.Max(0.001f, SwapAnimationSeconds * 0.45f));

        foreach (var (index, target) in _swapOffsetTargets)
        {
            _swapOffsets.TryGetValue(index, out var current);
            var next = current + (target - current) * step;
            _swapOffsets[index] = next;
            if (Vector2.DistanceSquared(next, target) > 0.25f)
                animating = true;
        }

        foreach (var index in _swapOffsets.Keys.ToArray())
        {
            if (_swapOffsetTargets.ContainsKey(index))
                continue;

            var current = _swapOffsets[index];
            var next = current + (Vector2.Zero - current) * step;
            _swapOffsets[index] = next;
            if (Vector2.DistanceSquared(next, Vector2.Zero) > 0.25f)
                animating = true;
            else
                _swapOffsets.Remove(index);
        }

        return animating;
    }

    private bool TryGetCardDrawPosition(int index, GridMetrics metrics, out float x, out float y)
    {
        if (!TryGetCardPosition(index, metrics, out x, out y))
            return false;

        if (_draggingIndex < 0 || index == _draggingIndex)
            return true;

        if (_swapOffsets.TryGetValue(index, out var offset))
        {
            x += offset.X;
            y += offset.Y;
        }

        return true;
    }

    private void DrawDraggedCard(SKCanvas canvas, int index, GridMetrics metrics, float globalOpacity)
    {
        float w = metrics.CardWidth;
        float h = metrics.CardHeight;
        float cx = _dragPosition.X;
        float cy = _dragPosition.Y;
        float scale = DragLiftScale;

        if (_isDragCommitting && TryGetCardPosition(_dropTargetIndex, metrics, out float targetX, out float targetY))
        {
            float eased = MathF.Sin(_dragCommitProgress * MathF.PI * 0.5f);
            float targetCx = targetX + w * 0.5f;
            float targetCy = targetY + h * 0.5f;
            cx = _dragPosition.X + (targetCx - _dragPosition.X) * eased;
            cy = _dragPosition.Y + (targetCy - _dragPosition.Y) * eased;
            scale = DragLiftScale + (1f - DragLiftScale) * eased;
        }

        float x = cx - w * 0.5f;
        float y = cy - h * 0.5f;

        canvas.Save();
        if (Math.Abs(scale - 1f) > 0.001f)
        {
            canvas.Translate(cx, cy);
            canvas.Scale(scale, scale);
            canvas.Translate(-cx, -cy);
        }

        DrawCard(canvas, index, x, y, metrics, globalOpacity);
        canvas.Restore();
    }

    private bool IsCardVisible(float x, float y, float cardWidth, float cardHeight) =>
        _horizontalScrollEnabled
            ? x + cardWidth >= 0 && x <= _visualSize.X && y + cardHeight >= 0 && y <= _visualSize.Y
            : y + cardHeight >= 0 && y <= _visualSize.Y;

    private void DrawCard(SKCanvas canvas, int index, float x, float y, GridMetrics metrics, float globalOpacity) =>
        DrawCardCore(canvas, index, x, y, metrics, globalOpacity);

    private void DrawCardCore(SKCanvas canvas, int index, float x, float y, GridMetrics metrics, float globalOpacity)
    {
        var rect = new SKRect(x, y, x + metrics.CardWidth, y + metrics.CardHeight);
        bool isSelected = index == _selectedIndex;
        float borderFade = isSelected ? _selectionBorderFade : 0f;
        float hoverLift = _hoverLift.TryGetValue(index, out var hoverState) ? hoverState.Strength : 0f;
        float easedHover = EaseOutCubic(hoverLift);
        float scale = isSelected
            ? 1f + SelectionLiftScale * borderFade
            : 1f + HoverLiftScale * easedHover;

        using var clipPath = new SKPath();
        clipPath.AddRoundRect(rect, CardCornerRadius, CardCornerRadius);
        canvas.Save();

        if (Math.Abs(scale - 1f) > 0.001f)
        {
            float cx = rect.MidX;
            float cy = rect.MidY;
            canvas.Translate(cx, cy);
            canvas.Scale(scale, scale);
            canvas.Translate(-cx, -cy);
        }

        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);

        var img = index < _images.Count ? _images[index] : null;
        bool isLoading = _loadingIndices.Contains(index);
        float titleH = metrics.CardHeight * TitleAreaRatio;
        float coverH = metrics.CardHeight - titleH;

        if (img != null)
            DrawCoverImage(canvas, img, rect.Left, rect.Top, metrics.CardWidth, coverH);
        else
            DrawPlaceholder(canvas, rect.Left, rect.Top, metrics.CardWidth, coverH);

        DrawTitleBar(canvas, img, rect, metrics.CardWidth, coverH, titleH, index, isSelected, globalOpacity);

        canvas.Restore();

        if (isSelected && borderFade > 0.001f)
        {
            float pulse = 0.72f + 0.28f * (0.5f + 0.5f * MathF.Sin(_selectionPulsePhase));
            canvas.Save();
            if (Math.Abs(scale - 1f) > 0.001f)
            {
                float cx = rect.MidX;
                float cy = rect.MidY;
                canvas.Translate(cx, cy);
                canvas.Scale(scale, scale);
                canvas.Translate(-cx, -cy);
            }

            DrawSelectionGlowBorder(canvas, rect, borderFade, pulse, globalOpacity);
            canvas.Restore();
        }

        if (isLoading)
        {
            var coverRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + coverH);
            using var loadPaint = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#88000000") };
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(coverRect, CardCornerRadius, CardCornerRadius), SKClipOperation.Intersect, true);
            canvas.DrawRoundRect(coverRect, CardCornerRadius, CardCornerRadius, loadPaint);
            canvas.Restore();
            DrawSpinner(canvas, rect.MidX, rect.Top + coverH * 0.5f, globalOpacity);
        }
    }

    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    private void DrawSelectionGlowBorder(SKCanvas canvas, SKRect rect, float strength, float pulse, float globalOpacity)
    {
        float baseA = strength * globalOpacity;
        if (baseA <= 0.001f)
            return;

        float glowA = baseA * (0.65f + 0.35f * pulse);
        var glowRect = rect;
        glowRect.Inflate(2.4f, 2.4f);

        _overlayPaint.Style = SKPaintStyle.Stroke;
        _overlayPaint.Shader = null;
        _overlayPaint.MaskFilter = _selectionGlowBlur;
        _overlayPaint.StrokeWidth = 6f;
        _overlayPaint.Color = SKColor.Parse("#7EC8F2").WithAlpha((byte)(110 * glowA));
        canvas.DrawRoundRect(glowRect, CardCornerRadius + 2.4f, CardCornerRadius + 2.4f, _overlayPaint);

        _overlayPaint.MaskFilter = null;
        _overlayPaint.StrokeWidth = 2.1f;
        _overlayPaint.Color = SKColors.White.WithAlpha((byte)(230 * baseA));
        canvas.DrawRoundRect(rect, CardCornerRadius, CardCornerRadius, _overlayPaint);
    }

    /// <summary>
    /// Subtle slot-machine style viewport fade: pixel rows near the top/bottom lose a little alpha.
    /// Uses DstIn so covers fade by screen position, not per-card opacity multiplication.
    /// </summary>
    private void ApplyViewportFadeMask(SKCanvas canvas)
    {
        float height = _visualSize.Y;
        if (height <= 0f)
            return;

        float fadeH = Math.Clamp(height * ViewportFadeHeightRatio, ViewportFadeMinHeight, ViewportFadeMaxHeight);
        float fadeStop = Math.Clamp(fadeH / height, 0.04f, 0.28f);
        byte edgeAlpha = (byte)Math.Clamp((int)(255f * ViewportFadeMinOpacity), 0, 255);
        var edge = SKColors.White.WithAlpha(edgeAlpha);

        canvas.SaveLayer(new SKPaint { BlendMode = SKBlendMode.DstIn });
        _overlayPaint.Style = SKPaintStyle.Fill;
        _overlayPaint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, height),
            new[] { edge, SKColors.White, SKColors.White, edge },
            new[] { 0f, fadeStop, 1f - fadeStop, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, _visualSize.X, height, _overlayPaint);
        _overlayPaint.Shader = null;
        canvas.Restore();
    }

    private void DrawCoverImage(SKCanvas canvas, SKImage img, float x, float y, float w, float h)
    {
        var dst = new SKRect(x, y, x + w, y + h);
        if (!TryGetImageDimensions(img, out var dims) || dims.Width <= 0 || dims.Height <= 0)
        {
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.Shader = null;
            _cardPaint.Color = SKColor.Parse("#1A1A1A");
            canvas.DrawRect(dst, _cardPaint);
            return;
        }

        var fillSrc = UniformToFillSrc(dims.Width, dims.Height, dst);
        _cardPaint.Style = SKPaintStyle.Fill;
        _cardPaint.Shader = null;
        _cardPaint.ImageFilter = null;
        _cardPaint.Color = SKColors.White;
        _cardPaint.IsAntialias = true;
        _cardPaint.FilterQuality = SKFilterQuality.Medium;
        canvas.DrawImage(img, fillSrc, dst, _cardPaint);
    }

    private void DrawPlaceholder(SKCanvas canvas, float x, float y, float w, float h)
    {
        _cardPaint.Style = SKPaintStyle.Fill;
        _cardPaint.Shader = null;
        _cardPaint.Color = SKColor.Parse("#1E1E1E");
        canvas.DrawRect(x, y, w, h, _cardPaint);
    }

    private bool UpdateMarquee(float dt)
    {
        if (!_titleMarqueeEnabled || !_marqueeActive || _marqueeScrollRange <= 0.5f)
            return false;

        const float pauseSeconds = 0.85f;
        float scrollSeconds = Math.Clamp(_marqueeScrollRange / 42f, 1.8f, 8f);
        float cycle = pauseSeconds + scrollSeconds + pauseSeconds;
        _marqueeTime += dt;
        float phase = _marqueeTime % cycle;

        if (phase < pauseSeconds)
            _marqueeOffset = 0f;
        else if (phase < pauseSeconds + scrollSeconds)
            _marqueeOffset = (phase - pauseSeconds) / scrollSeconds * _marqueeScrollRange;
        else
            _marqueeOffset = _marqueeScrollRange;

        return true;
    }

    private void DrawTitleBar(SKCanvas canvas, SKImage? img, SKRect cardRect, float cardW, float coverH, float titleH, int index, bool isSelected, float globalOpacity)
    {
        float titleTop = cardRect.Top + coverH;
        var titleRect = new SKRect(cardRect.Left, titleTop, cardRect.Right, cardRect.Bottom);

        canvas.Save();
        canvas.ClipRect(titleRect);

        if (img != null)
        {
            var backdrop = GetOrCreateBlurBackdrop(img);
            if (backdrop != null)
            {
                var coverRect = new SKRect(cardRect.Left, cardRect.Top, cardRect.Right, titleTop);
                _cardPaint.Style = SKPaintStyle.Fill;
                _cardPaint.Shader = null;
                _cardPaint.ImageFilter = null;
                _cardPaint.FilterQuality = SKFilterQuality.Low;
                _cardPaint.Color = SKColors.White.WithAlpha((byte)(255 * globalOpacity));

                canvas.Save();
                canvas.Translate(0, titleTop);
                canvas.Scale(1, -1);
                canvas.Translate(0, -titleTop);
                canvas.DrawImage(backdrop, coverRect);
                canvas.Restore();
            }
        }

        _overlayPaint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(titleRect.Left, titleTop),
            new SKPoint(titleRect.Left, titleRect.Bottom),
            new[] { SKColors.Black.WithAlpha(10), SKColors.Black.WithAlpha(175) },
            null,
            SKShaderTileMode.Clamp);
        _overlayPaint.Style = SKPaintStyle.Fill;
        canvas.DrawRect(titleRect, _overlayPaint);
        _overlayPaint.Shader = null;

        string title = index < _titles.Length ? _titles[index] : string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
        {
            float textSize = Math.Clamp(cardW * TitleTextSizeRatio, TitleTextSizeMin, TitleTextSizeMax);
            _titlePaint.TextSize = textSize;
            _titleShadowPaint.TextSize = textSize;
            float textX = cardRect.Left + 12f;
            float maxTextWidth = cardW - 24f;
            float lineHeight = textSize * 1.18f;
            var textClip = new SKRect(textX, titleTop, cardRect.Right - 12f, titleRect.Bottom);
            bool drewMarquee = false;

            if (isSelected && _titleMarqueeEnabled)
            {
                float textWidth = CompositionSkiaTextHelper.MeasureText(title, _titlePaint);
                if (textWidth > maxTextWidth)
                {
                    _marqueeScrollRange = textWidth - maxTextWidth;
                    _marqueeActive = true;
                    float textY = titleRect.MidY + textSize * 0.12f;

                    canvas.Save();
                    canvas.ClipRect(textClip);
                    CompositionSkiaTextHelper.DrawText(canvas, title, textX - _marqueeOffset, textY + 1f, _titleShadowPaint);
                    _titlePaint.Color = SKColors.White.WithAlpha((byte)(245 * globalOpacity));
                    CompositionSkiaTextHelper.DrawText(canvas, title, textX - _marqueeOffset, textY, _titlePaint);
                    canvas.Restore();
                    drewMarquee = true;
                }
                else
                {
                    _marqueeActive = false;
                }
            }
            else if (isSelected)
            {
                _marqueeActive = false;
            }

            if (!drewMarquee)
            {
                int maxLines = Math.Clamp((int)Math.Floor(titleH / lineHeight), 1, 2);
                IReadOnlyList<string> lines = _titleMarqueeEnabled && !isSelected
                    ? new[] { CompositionSkiaTextHelper.TruncateText(title, maxTextWidth, _titlePaint) }
                    : CompositionSkiaTextHelper.WrapTextLines(title, maxTextWidth, _titlePaint, maxLines);

                float totalHeight = lines.Count * lineHeight;
                float firstBaselineY = titleRect.MidY - totalHeight * 0.5f + textSize * 0.82f;

                canvas.Save();
                canvas.ClipRect(textClip);
                CompositionSkiaTextHelper.DrawTextLines(canvas, lines, textX, firstBaselineY + 1f, lineHeight, _titleShadowPaint);
                _titlePaint.Color = SKColors.White.WithAlpha((byte)(245 * globalOpacity));
                CompositionSkiaTextHelper.DrawTextLines(canvas, lines, textX, firstBaselineY, lineHeight, _titlePaint);
                canvas.Restore();
            }
        }

        canvas.Restore();
    }

    private static SKPaint CreateTitlePaint(SKColor color)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            TextSize = 18,
            IsLinearText = true,
            SubpixelText = true
        };
        CompositionSkiaTextHelper.ConfigurePaint(paint);
        return paint;
    }

    private void DrawSpinner(SKCanvas canvas, float cx, float cy, float globalOpacity)
    {
        _spinnerPaint.Color = SKColors.White.WithAlpha((byte)(200 * globalOpacity));
        var oval = new SKRect(cx - 14, cy - 14, cx + 14, cy + 14);
        canvas.DrawArc(oval, _spinnerRotation, 270, false, _spinnerPaint);
    }

    private void ExtendScrollbarVisibility(double extraSeconds)
    {
        if (extraSeconds <= 0)
            return;

        long until = Stopwatch.GetTimestamp() + (long)(extraSeconds * Stopwatch.Frequency);
        if (until > _scrollbarVisibleUntilTicks)
            _scrollbarVisibleUntilTicks = until;
    }

    private void RequestScrollbarAnimationFrame()
    {
        if (_lastTicks == 0)
            _lastTicks = Stopwatch.GetTimestamp();
        RegisterForNextAnimationFrameUpdate();
        Invalidate();
    }

    private void DrawScrollbar(SKCanvas canvas, GridMetrics metrics, float globalOpacity)
    {
        if (metrics.MaxScrollY <= 1 || _scrollbarOpacity <= 0.01f)
            return;

        if (_horizontalScrollEnabled)
        {
            DrawHorizontalScrollbar(canvas, metrics, globalOpacity);
            return;
        }

        float trackTop = ScrollbarMargin;
        float trackBottom = _visualSize.Y - ScrollbarMargin;
        float trackHeight = trackBottom - trackTop;
        float hitRight = _visualSize.X - CardGridLayoutHelper.ScrollbarRightInset;
        float hitLeft = hitRight - CardGridLayoutHelper.ScrollbarHitWidth;
        float trackX = hitLeft + (CardGridLayoutHelper.ScrollbarHitWidth - CardGridLayoutHelper.ScrollbarWidth) * 0.5f;
        var trackRect = new SKRect(trackX, trackTop, trackX + CardGridLayoutHelper.ScrollbarWidth, trackBottom);

        byte alpha = (byte)(255 * globalOpacity * _scrollbarOpacity);
        if (_trackShader == null)
        {
            _trackShader = SKShader.CreateLinearGradient(
                new SKPoint(trackRect.Left, trackRect.Top),
                new SKPoint(trackRect.Left, trackRect.Bottom),
                new[] { _trackColor1, _trackColor2 },
                null,
                SKShaderTileMode.Clamp);
        }

        _scrollbarPaint.Style = SKPaintStyle.Fill;
        _scrollbarPaint.Shader = _trackShader;
        _scrollbarPaint.Color = SKColors.White.WithAlpha(alpha);
        canvas.DrawRoundRect(trackRect, CardGridLayoutHelper.ScrollbarWidth * 0.5f, CardGridLayoutHelper.ScrollbarWidth * 0.5f, _scrollbarPaint);
        _scrollbarPaint.Shader = null;

        float viewportRatio = Math.Clamp(_visualSize.Y / Math.Max(1f, metrics.ContentHeight), 0.08f, 1f);
        float thumbH = Math.Max(36f, trackHeight * viewportRatio);
        float scrollPct = metrics.MaxScrollY <= 0 ? 0 : (float)(_currentScrollY / metrics.MaxScrollY);
        float thumbY = trackTop + (trackHeight - thumbH) * scrollPct;
        var thumbRect = new SKRect(trackX - 1f, thumbY, trackX + CardGridLayoutHelper.ScrollbarWidth + 1f, thumbY + thumbH);

        if (_thumbShader == null)
        {
            _thumbShader = SKShader.CreateLinearGradient(
                new SKPoint(thumbRect.Left, thumbRect.Top),
                new SKPoint(thumbRect.Left, thumbRect.Bottom),
                new[] { _thumbColor1, _thumbColor2 },
                null,
                SKShaderTileMode.Clamp);
        }

        if (_isScrollbarPressed)
        {
            _scrollbarPaint.MaskFilter = _scrollbarBlur;
            _scrollbarPaint.Color = SKColors.White.WithAlpha((byte)(80 * globalOpacity * _scrollbarOpacity));
            var glow = thumbRect;
            glow.Inflate(3, 3);
            canvas.DrawRoundRect(glow, 6, 6, _scrollbarPaint);
            _scrollbarPaint.MaskFilter = null;
        }

        _scrollbarPaint.Shader = _thumbShader;
        _scrollbarPaint.Color = SKColors.White.WithAlpha(alpha);
        canvas.DrawRoundRect(thumbRect, 4f, 4f, _scrollbarPaint);
        _scrollbarPaint.Shader = null;
    }

    private void DrawHorizontalScrollbar(SKCanvas canvas, GridMetrics metrics, float globalOpacity)
    {
        float trackLeft = ScrollbarMargin + 24f;
        float trackRight = _visualSize.X - ScrollbarMargin - 24f;
        float trackWidth = trackRight - trackLeft;
        float trackY = _visualSize.Y - ScrollbarMargin - 8f;
        var trackRect = new SKRect(trackLeft, trackY - 4f, trackRight, trackY + 4f);

        byte alpha = (byte)(255 * globalOpacity * _scrollbarOpacity);
        _scrollbarPaint.Style = SKPaintStyle.Fill;
        _scrollbarPaint.Shader = null;
        _scrollbarPaint.Color = SKColors.White.WithAlpha((byte)(alpha * 0.45f));
        canvas.DrawRoundRect(trackRect, 4f, 4f, _scrollbarPaint);

        float viewportRatio = Math.Clamp(_visualSize.X / Math.Max(1f, metrics.ContentWidth), 0.08f, 1f);
        float thumbW = Math.Max(48f, trackWidth * viewportRatio);
        float scrollPct = metrics.MaxScrollY <= 0 ? 0 : (float)(_currentScrollY / metrics.MaxScrollY);
        float thumbX = trackLeft + (trackWidth - thumbW) * scrollPct;
        var thumbRect = new SKRect(thumbX, trackY - 5f, thumbX + thumbW, trackY + 5f);
        _scrollbarPaint.Color = SKColors.White.WithAlpha(alpha);
        canvas.DrawRoundRect(thumbRect, 5f, 5f, _scrollbarPaint);
    }

    private bool TryGetImageDimensions(SKImage image, out (int Width, int Height) dims)
    {
        if (_dimCache.TryGetValue(image, out dims))
            return true;

        dims = (image.Width, image.Height);
        _dimCache[image] = dims;
        return dims.Width > 0 && dims.Height > 0;
    }

    private void CacheImageDimensions(SKImage image) => TryGetImageDimensions(image, out _);

    private void RemoveImageCaches(SKImage img)
    {
        _dimCache.Remove(img);
        if (_blurBackdropCache.Remove(img, out var backdrop))
            backdrop.Dispose();
    }

    private SKImage? GetOrCreateBlurBackdrop(SKImage img)
    {
        if (_blurBackdropCache.TryGetValue(img, out var cached))
            return cached;

        if (!TryGetImageDimensions(img, out var dims) || dims.Width <= 0 || dims.Height <= 0)
            return null;

        try
        {
            var cacheDst = new SKRect(0, 0, BlurBackdropCacheWidth, BlurBackdropCacheHeight);
            var fillSrc = UniformToFillSrc(dims.Width, dims.Height, cacheDst);

            using var surface = SKSurface.Create(new SKImageInfo(BlurBackdropCacheWidth, BlurBackdropCacheHeight));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            using var blurFilter = SKImageFilter.CreateBlur(8f, 8f);
            using var blurPaint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.Low,
                ImageFilter = blurFilter
            };

            canvas.SaveLayer(blurPaint);
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.Shader = null;
            _cardPaint.ImageFilter = null;
            _cardPaint.Color = SKColors.White;
            _cardPaint.FilterQuality = SKFilterQuality.Low;
            canvas.DrawImage(img, fillSrc, cacheDst, _cardPaint);
            canvas.Restore();

            var snapshot = surface.Snapshot();
            _blurBackdropCache[img] = snapshot;
            return snapshot;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SKRect UniformToFillSrc(float srcW, float srcH, SKRect dest)
    {
        float srcAspect = srcW / srcH;
        float destAspect = dest.Width / dest.Height;
        float cropW = srcW;
        float cropH = srcH;
        float cropX = 0;
        float cropY = 0;

        if (srcAspect > destAspect)
        {
            cropW = srcH * destAspect;
            cropX = (srcW - cropW) * 0.5f;
        }
        else
        {
            cropH = srcW / destAspect;
            cropY = (srcH - cropH) * 0.5f;
        }

        return new SKRect(cropX, cropY, cropX + cropW, cropY + cropH);
    }

    public int HitTestCard(Point point, int itemCount)
    {
        if (itemCount <= 0)
            return -1;

        var metrics = ComputeMetrics(itemCount);
        float localY = (float)point.Y + (float)_currentScrollY;
        if (localY < metrics.PaddingTop - metrics.Spacing)
            return -1;

        int row = (int)((localY - metrics.PaddingTop) / (metrics.CardHeight + metrics.Spacing));
        if (row < 0 || row >= metrics.RowCount)
            return -1;

        float rowTop = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing);
        if (localY > rowTop + metrics.CardHeight)
            return -1;

        float localX = (float)point.X;
        if (localX < metrics.PaddingLeft)
            return -1;

        int col = (int)((localX - metrics.PaddingLeft) / (metrics.CardWidth + metrics.Spacing));
        if (col < 0 || col >= metrics.Columns)
            return -1;

        float colLeft = metrics.PaddingLeft + col * (metrics.CardWidth + metrics.Spacing);
        if (localX > colLeft + metrics.CardWidth)
            return -1;

        int index = row * metrics.Columns + col;
        return index < itemCount ? index : -1;
    }

    public bool HitTestScrollbar(Point point, int itemCount, out double scrollRatio)
    {
        scrollRatio = 0;
        var metrics = ComputeMetrics(itemCount);
        if (metrics.MaxScrollY <= 1)
            return false;

        float hitRight = _visualSize.X - CardGridLayoutHelper.ScrollbarRightInset;
        var trackRect = new SKRect(
            hitRight - CardGridLayoutHelper.ScrollbarHitWidth,
            ScrollbarMargin,
            hitRight,
            _visualSize.Y - ScrollbarMargin);
        if (!trackRect.Contains((float)point.X, (float)point.Y))
            return false;

        float trackHeight = trackRect.Height;
        scrollRatio = Math.Clamp((point.Y - trackRect.Top) / trackHeight, 0, 1);
        return true;
    }

    public double CurrentScrollY => _currentScrollY;
    public double TargetScrollY => _targetScrollY;
}
