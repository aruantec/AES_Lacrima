using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using AES_Core.Logging;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using log4net;
using SkiaSharp;

namespace AES_Controls.Composition;

internal record CardGridScrollMessage(double TargetScrollY);
internal record CardGridScrollVelocityMessage(double VelocityY);
internal record CardGridDirectScrollFollowMessage(bool Enabled);
internal record CardGridSnapScrollMessage(double ScrollY);
internal record CardGridLayoutMessage(float CardScale, float CardSpacing, float TopPadding);
internal record CardGridSlotCountMessage(int Count, bool ClearImages = false);
internal record CardGridSelectedIndexMessage(int Index);
internal record CardGridHoveredIndexMessage(int Index);
internal record CardGridInteractionSuspendedMessage(bool Suspended);
internal record CardGridScrollbarPressedMessage(bool IsPressed);
internal record CardGridScrollbarHoverMessage(bool IsHovered);
internal record CardGridResetScrollbarMessage();
internal record CardGridBackgroundColorMessage(SKColor Color);
internal record CardGridHorizontalScrollMessage(bool Enabled);
internal record CardGridAttachSyncMessage(CardGridAnimationSyncState State);
internal record CardGridDragStateMessage(int Index, bool IsDragging);
internal record CardGridDragPositionMessage(Vector2 Position);
internal record CardGridDropTargetMessage(int Index);
internal record CardGridDragCancelMessage();
internal record CardGridDragCommitMessage(int TargetIndex);
internal record CardGridDragFinalizeMessage();
internal record CardGridMoveImageMessage(int FromIndex, int ToIndex);
internal record CardGridContentLoadingMessage(bool IsLoading);
internal record CardGridSyncSlotsMessage(SKImage?[] Images);
internal record CardGridImageRevealHoldMessage(bool Hold);
internal record CardGridBeginWaveRevealMessage(int ItemCount, bool HorizontalScroll);
internal record CardGridExtendWaveRevealMessage(int ItemCount, bool HorizontalScroll);
internal record CardGridRequestFlushDisposalsMessage();
internal record CardGridQueueImageDisposalMessage(SKImage Image);

public class CompositionCardGridVisualHandler : CompositionCustomVisualHandler
{
    private static readonly ILog Log = LogHelper.For<CompositionCardGridVisualHandler>();

    public const float BaseCardWidth = 200f;
    public const float BaseCardHeight = 272f;
    private const float GridPaddingX = 28f;
    private const float GridPaddingTop = 20f;
    private const float ScrollbarMargin = 10f;
    private static readonly SKColor DefaultBackgroundColor = SKColor.Parse("#101010");
    private const float CardCornerRadius = 12f;
    private const float TitleAreaRatio = 0.24f;
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
    private const float ImageRevealStaggerSeconds = 0.008f;
    private const float ImageRevealMaxStaggerSeconds = 0.08f;
    private const float WaveRevealRowStaggerSeconds = 0.014f;
    private const float WaveRevealColStaggerSeconds = 0.006f;
    private const float WaveRevealMaxDelaySeconds = 0.22f;
    private float _currentGlobalOpacity = 1f;
    private bool _pauseLoadingSpinnerAnimation;
    private bool _horizontalScrollEnabled;
    private bool _interactionSuspended;
    private CardGridAnimationSyncState? _animationSync;
    private int _draggingIndex = -1;
    private int _dropTargetIndex = -1;
    private Vector2 _dragPosition;
    private bool _isDragCommitting;
    private float _dragCommitProgress;
    private Vector2 _dragCommitStartPosition;
    private SKImage? _draggedImage;
    private const float SwapAnimationSeconds = 0.2f;
    private const float DragCommitSeconds = 0.3f;
    private const float DragLiftScale = 1.04f;
    private readonly Dictionary<int, Vector2> _swapOffsets = new();
    private readonly Dictionary<int, Vector2> _swapOffsetTargets = new();
    private readonly Dictionary<int, ImageRevealState> _imageReveal = new();
    private bool _imageRevealHold;
    private readonly HashSet<int> _pendingImageReveals = new();
    private readonly HashSet<SKImage> _pendingDisposal = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SKImage, int> _pendingDisposalAge = new(ReferenceEqualityComparer.Instance);
    private const int PendingDisposalFrameDelay = 5;

    private readonly struct ImageRevealState(float opacity, float velocity, float delayRemaining)
    {
        public float Opacity { get; } = opacity;
        public float Velocity { get; } = velocity;
        public float DelayRemaining { get; } = delayRemaining;

        public ImageRevealState WithOpacity(float opacity, float velocity) =>
            new(opacity, velocity, DelayRemaining);

        public ImageRevealState WithDelay(float delayRemaining) =>
            new(Opacity, Velocity, delayRemaining);
    }

    private List<SKImage?> _images = new();
    private HashSet<int> _loadingIndices = new();
    private float _spinnerRotation;
    private bool _isContentLoading;

    private readonly SKPaint _cardPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private readonly SKPaint _overlayPaint = new() { IsAntialias = true };
    private readonly SKPaint _scrollbarPaint = new() { IsAntialias = true };
    private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
    private readonly SKMaskFilter _scrollbarBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
    private readonly SKMaskFilter _selectionGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f);
    private readonly Dictionary<SKImage, (int Width, int Height)> _dimCache = new();
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
                    {
                        RemoveImageCaches(img);
                        QueueNativeImageDisposal(img);
                    }
                }

                for (int i = 0; i < _images.Count; i++)
                {
                    var img = _images[i];
                    if (img != null)
                    {
                        CacheImageDimensions(img);
                        if (i >= previous.Length || previous[i] == null)
                            BeginImageReveal(i);
                    }
                }

                PruneImageRevealStates(_images.Count);
                Invalidate();
                break;
            }
            case UpdateImageMessage update:
                if (update.Index >= 0 && update.Index < _images.Count)
                {
                    var oldImg = _images[update.Index];
                    if (update.Image != null)
                    {
                        bool isFirstShow = oldImg == null;
                        _images[update.Index] = update.Image;
                        CacheImageDimensions(update.Image);
                        _loadingIndices.Remove(update.Index);
                        if (isFirstShow &&
                            (!_imageReveal.TryGetValue(update.Index, out var reveal) || reveal.Opacity >= 0.98f))
                        {
                            BeginImageReveal(update.Index);
                        }
                    }
                    else if (update.IsLoading)
                    {
                        _loadingIndices.Add(update.Index);
                        _imageReveal.Remove(update.Index);
                    }
                    else if (update.ClearImage)
                    {
                        _images[update.Index] = null;
                        _loadingIndices.Remove(update.Index);
                        _imageReveal.Remove(update.Index);
                    }
                    else
                    {
                        _loadingIndices.Remove(update.Index);
                        _imageReveal.Remove(update.Index);
                    }

                    if (oldImg != null && !ReferenceEquals(oldImg, update.Image))
                    {
                        RemoveImageCaches(oldImg);
                        QueueNativeImageDisposal(oldImg);
                    }
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
            case CardGridSlotCountMessage slotCount:
            {
                ResizeImageSlots(slotCount.Count, slotCount.ClearImages);
                break;
            }
            case CardGridSyncSlotsMessage sync:
            {
                var imgs = sync.Images ?? Array.Empty<SKImage?>();
                var previous = _images.ToArray();
                var previousImages = previous.ToList();
                _images = [.. imgs];

                foreach (var img in previousImages)
                {
                    if (img != null && !_images.Contains(img))
                    {
                        RemoveImageCaches(img);
                        QueueNativeImageDisposal(img);
                    }
                }

                for (int i = 0; i < _images.Count; i++)
                {
                    var img = _images[i];
                    if (img != null)
                    {
                        CacheImageDimensions(img);
                        if (i >= previous.Length || previous[i] == null)
                            BeginImageReveal(i);
                    }
                }

                _loadingIndices.RemoveWhere(index => index >= _images.Count);
                PruneImageRevealStates(_images.Count);
                Invalidate();
                break;
            }
            case CardGridImageRevealHoldMessage hold:
                _imageRevealHold = hold.Hold;
                if (!hold.Hold)
                    FlushPendingImageReveals();
                break;
            case CardGridBeginWaveRevealMessage wave:
                BeginWaveEntryReveal(wave.ItemCount, wave.HorizontalScroll);
                Invalidate();
                break;
            case CardGridExtendWaveRevealMessage extend:
                ExtendWaveEntryReveal(extend.ItemCount, extend.HorizontalScroll);
                Invalidate();
                break;
            case DisposeImageMessage dispose:
                if (dispose.Image != null)
                    QueueNativeImageDisposal(dispose.Image);
                break;
            case CardGridRequestFlushDisposalsMessage:
                FlushPendingDisposals(force: true);
                break;
            case CardGridQueueImageDisposalMessage queue:
                if (queue.Image != null)
                    QueueNativeImageDisposal(queue.Image);
                break;
            case CardGridSelectedIndexMessage selected:
                _selectedIndex = selected.Index;
                if (_selectedIndex < 0)
                    _selectionPulsePhase = 0f;
                RequestScrollbarAnimationFrame();
                break;
            case CardGridHoveredIndexMessage hovered:
                if (_interactionSuspended)
                    break;

                if (hovered.Index != _hoveredIndex)
                {
                    _hoveredIndex = hovered.Index;
                    if (_hoveredIndex >= 0 && !_hoverLift.ContainsKey(_hoveredIndex))
                        _hoverLift[_hoveredIndex] = (0f, 0f);
                    RequestScrollbarAnimationFrame();
                }
                break;
            case CardGridInteractionSuspendedMessage suspended:
                if (_interactionSuspended == suspended.Suspended)
                    break;

                _interactionSuspended = suspended.Suspended;
                if (suspended.Suspended)
                {
                    _hoveredIndex = -1;
                    _hoverLift.Clear();
                }

                RequestScrollbarAnimationFrame();
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
            {
                var clamped = (float)Math.Clamp(opacity.Value, 0.0, 1.0);
                if (Math.Abs(_currentGlobalOpacity - clamped) < 0.0001f)
                    break;
                _currentGlobalOpacity = clamped;
                Invalidate();
                break;
            }
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
                _backgroundColor = background.Color.WithAlpha(255);
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
                    _draggedImage = _draggingIndex >= 0 && _draggingIndex < _images.Count
                        ? _images[_draggingIndex]
                        : null;
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
                _dragCommitStartPosition = _dragPosition;
                UpdateSwapOffsetTargets();
                _isDragCommitting = true;
                _dragCommitProgress = 0f;
                RequestScrollbarAnimationFrame();
                break;
            case CardGridMoveImageMessage move:
                MoveImageSlot(move.FromIndex, move.ToIndex);
                Invalidate();
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

        bool selectionPulsing = !_interactionSuspended && _selectedIndex >= 0 && _selectionBorderFade > 0.2f;
        if (selectionPulsing)
            _selectionPulsePhase += (float)dt * 3.1f;

        bool dragAnimating = AnimateSwapOffsets((float)dt);
        if (_isDragCommitting && _dragCommitProgress < 1f)
        {
            _dragCommitProgress += (float)(dt / DragCommitSeconds);
            if (_dragCommitProgress > 1f)
                _dragCommitProgress = 1f;
            dragAnimating = true;
        }

        bool hoverAnimating = false;
        if (!_interactionSuspended && _hoverLift.Count > 0)
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

        bool imageRevealAnimating = AnimateImageReveals((float)dt);

        bool isAnimating = _directScrollFollow ||
                           Math.Abs(_targetScrollY - _currentScrollY) > 0.01 ||
                           Math.Abs(_scrollVelocity) > 0.5 ||
                           Math.Abs(_scrollSpringVelocity) > 0.01 ||
                           Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.01 ||
                           selectionFading ||
                           selectionPulsing ||
                           hoverAnimating ||
                           dragAnimating ||
                           imageRevealAnimating;
        bool animateSpinners = !_interactionSuspended &&
                               !_pauseLoadingSpinnerAnimation &&
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

        canvas.SaveLayer();
        if (g < 0.999f)
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

        if (g < 0.999f)
            canvas.Restore();

        canvas.Restore();
        DrawScrollbar(canvas, metrics, 1f);
        canvas.Restore();

        FlushPendingDisposals(force: false);
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
        _draggedImage = null;
        _swapOffsets.Clear();
        _swapOffsetTargets.Clear();
    }

    private void MoveImageSlot(int from, int to)
    {
        if (from == to ||
            from < 0 || to < 0 ||
            from >= _images.Count || to >= _images.Count)
            return;

        var image = _images[from];
        _images.RemoveAt(from);
        _images.Insert(to, image);

        bool wasLoading = _loadingIndices.Remove(from);
        ShiftIndexedSet(_loadingIndices, from, to);
        if (wasLoading)
            _loadingIndices.Add(to);

        ShiftIndexedDictionary(_hoverLift, from, to);
        ShiftImageRevealStates(from, to);
    }

    private static void ShiftIndexedSet(HashSet<int> indices, int from, int to)
    {
        if (indices.Count == 0)
            return;

        var shifted = new HashSet<int>();
        foreach (int index in indices)
            shifted.Add(ShiftIndex(index, from, to));
        indices.Clear();
        foreach (int index in shifted)
            indices.Add(index);
    }

    private static void ShiftIndexedDictionary<T>(Dictionary<int, T> values, int from, int to)
    {
        if (values.Count == 0)
            return;

        var shifted = new Dictionary<int, T>();
        foreach (var (index, value) in values)
            shifted[ShiftIndex(index, from, to)] = value;
        values.Clear();
        foreach (var (index, value) in shifted)
            values[index] = value;
    }

    private void ShiftImageRevealStates(int from, int to)
    {
        if (_imageReveal.Count == 0)
            return;

        var shifted = new Dictionary<int, ImageRevealState>();
        foreach (var (index, state) in _imageReveal)
            shifted[ShiftIndex(index, from, to)] = state;
        _imageReveal.Clear();
        foreach (var (index, state) in shifted)
            _imageReveal[index] = state;
    }

    private static int ShiftIndex(int index, int from, int to)
    {
        if (index == from)
            return to;

        if (from < to)
        {
            if (index > from && index <= to)
                return index - 1;
        }
        else if (index >= to && index < from)
        {
            return index + 1;
        }

        return index;
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
        if (_draggingIndex < 0 || _isDragCommitting)
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
            float eased = EaseOutCubic(_dragCommitProgress);
            float targetCx = targetX + w * 0.5f;
            float targetCy = targetY + h * 0.5f;
            cx = _dragCommitStartPosition.X + (targetCx - _dragCommitStartPosition.X) * eased;
            cy = _dragCommitStartPosition.Y + (targetCy - _dragCommitStartPosition.Y) * eased;
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

        DrawCardCore(canvas, index, x, y, metrics, globalOpacity, _draggedImage);
        canvas.Restore();
    }

    private bool IsCardVisible(float x, float y, float cardWidth, float cardHeight) =>
        _horizontalScrollEnabled
            ? x + cardWidth >= 0 && x <= _visualSize.X && y + cardHeight >= 0 && y <= _visualSize.Y
            : y + cardHeight >= 0 && y <= _visualSize.Y;

    private void DrawCard(SKCanvas canvas, int index, float x, float y, GridMetrics metrics, float globalOpacity) =>
        DrawCardCore(canvas, index, x, y, metrics, globalOpacity);

    private void DrawCardCore(
        SKCanvas canvas,
        int index,
        float x,
        float y,
        GridMetrics metrics,
        float globalOpacity,
        SKImage? imageOverride = null)
    {
        var rect = new SKRect(x, y, x + metrics.CardWidth, y + metrics.CardHeight);
        var img = imageOverride ?? (index < _images.Count ? _images[index] : null);

        if (_interactionSuspended)
        {
            DrawScrollFrameCard(canvas, rect, img);
            return;
        }

        bool isSelected = index == _selectedIndex;
        float borderFade = isSelected ? _selectionBorderFade : 0f;
        float hoverLift = !_interactionSuspended && _hoverLift.TryGetValue(index, out var hoverState)
            ? hoverState.Strength
            : 0f;
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

        bool isLoading = !_interactionSuspended && _loadingIndices.Contains(index);
        float titleH = metrics.CardHeight * TitleAreaRatio;
        float reveal = GetImageRevealOpacity(index);

        if (reveal < 0.001f)
        {
            canvas.Restore();
            return;
        }

        if (reveal < 0.999f)
            canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)(reveal * 255)) });

        if (img != null)
        {
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.Shader = null;
            _cardPaint.ImageFilter = null;
            _cardPaint.Color = SKColors.White;
            _cardPaint.IsAntialias = true;
            _cardPaint.FilterQuality = _interactionSuspended ? SKFilterQuality.Low : SKFilterQuality.Medium;

            if (IsBakedCardImage(img))
            {
                canvas.DrawImage(img, rect, _cardPaint);
            }
            else
            {
                var coverRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + (metrics.CardHeight - titleH));
                var src = UniformToFillSrc(img.Width, img.Height, coverRect);
                canvas.DrawImage(img, src, coverRect, _cardPaint);
            }
        }
        else
            DrawPlaceholder(canvas, rect.Left, rect.Top, metrics.CardWidth, metrics.CardHeight);

        if (reveal < 0.999f)
            canvas.Restore();

        canvas.Restore();

        if (isSelected && borderFade > 0.001f)
        {
            float pulse = _interactionSuspended
                ? 1f
                : 0.72f + 0.28f * (0.5f + 0.5f * MathF.Sin(_selectionPulsePhase));
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
            float coverH = metrics.CardHeight - titleH;
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

    private void ResizeImageSlots(int count, bool clearImages = false)
    {
        count = Math.Max(0, count);

        if (clearImages)
        {
            for (int i = 0; i < _images.Count; i++)
            {
                var img = _images[i];
                _images[i] = null;
                if (img != null)
                {
                    RemoveImageCaches(img);
                    QueueNativeImageDisposal(img);
                }
            }

            _loadingIndices.Clear();
        }

        while (_images.Count < count)
            _images.Add(null);

        while (_images.Count > count)
        {
            int last = _images.Count - 1;
            var removed = _images[last];
            _images.RemoveAt(last);
            if (removed != null)
            {
                RemoveImageCaches(removed);
                QueueNativeImageDisposal(removed);
            }
        }

        _loadingIndices.RemoveWhere(index => index >= count);
        PruneImageRevealStates(count);
        _pendingImageReveals.RemoveWhere(index => index >= count);
        Invalidate();
    }

    private void BeginImageReveal(int index)
    {
        if (index < 0)
            return;

        if (_imageRevealHold)
        {
            _pendingImageReveals.Add(index);
            return;
        }

        BeginImageRevealCore(index);
    }

    private void FlushPendingImageReveals()
    {
        if (_pendingImageReveals.Count == 0)
            return;

        int center = EstimateViewportCenterIndex();
        foreach (var index in _pendingImageReveals.OrderBy(i => Math.Abs(i - center)))
            BeginImageRevealCore(index);
        _pendingImageReveals.Clear();
    }

    private void BeginImageRevealCore(int index)
    {
        if (index < 0)
            return;

        int center = EstimateViewportCenterIndex();
        float delay = Math.Min(
            ImageRevealMaxStaggerSeconds,
            Math.Abs(index - center) * ImageRevealStaggerSeconds);
        _imageReveal[index] = new ImageRevealState(0f, 0f, delay);
        if (_lastTicks == 0)
            _lastTicks = Stopwatch.GetTimestamp();
        RegisterForNextAnimationFrameUpdate();
    }

    private void BeginWaveEntryReveal(int itemCount, bool horizontalScroll)
    {
        int count = Math.Max(itemCount, _images.Count);
        if (count <= 0)
            return;

        if (!TryGetWaveRevealViewportRange(count, horizontalScroll, out var range))
        {
            BootstrapWaveReveal(count, horizontalScroll, clearExisting: true);
            return;
        }

        ApplyWaveRevealRange(count, horizontalScroll, range, clearExisting: true);
    }

    private void ExtendWaveEntryReveal(int itemCount, bool horizontalScroll)
    {
        int count = Math.Max(itemCount, _images.Count);
        if (count <= 0)
            return;

        if (!TryGetWaveRevealViewportRange(count, horizontalScroll, out var range))
            return;

        ApplyWaveRevealRange(count, horizontalScroll, range, clearExisting: false);
    }

    private readonly record struct WaveRevealViewportRange(
        int Columns,
        int FirstRow,
        int LastRow,
        int FirstCol,
        int LastCol);

    private bool TryGetWaveRevealViewportRange(int count, bool horizontalScroll, out WaveRevealViewportRange range)
    {
        range = default;
        if (_visualSize.X <= 0 || _visualSize.Y <= 0)
            return false;

        var metrics = ComputeMetrics(count);
        if (metrics.Columns <= 0 || metrics.RowCount <= 0)
            return false;

        int columns = Math.Max(1, metrics.Columns);
        int firstRow = 0;
        int lastRow = metrics.RowCount - 1;
        int firstCol = 0;
        int lastCol = columns - 1;

        if (horizontalScroll)
        {
            float pitch = Math.Max(1f, metrics.ColumnPitch);
            firstCol = Math.Max(0, (int)Math.Floor((_currentScrollY - metrics.PaddingLeft) / pitch) - 1);
            lastCol = Math.Min(
                columns - 1,
                (int)Math.Ceiling((_currentScrollY + _visualSize.X - metrics.PaddingLeft) / pitch) + 2);
        }
        else
        {
            float rowHeight = metrics.CardHeight + metrics.Spacing;
            firstRow = Math.Max(0, (int)Math.Floor((_currentScrollY - metrics.PaddingTop) / rowHeight) - 1);
            lastRow = Math.Min(
                metrics.RowCount - 1,
                (int)Math.Ceiling((_currentScrollY + _visualSize.Y) / rowHeight) + 2);
        }

        range = new WaveRevealViewportRange(columns, firstRow, lastRow, firstCol, lastCol);
        return true;
    }

    private void BootstrapWaveReveal(int count, bool horizontalScroll, bool clearExisting)
    {
        var metrics = ComputeMetrics(count);
        int columns = Math.Max(1, metrics.Columns);
        int bootstrapRows = horizontalScroll ? Math.Max(1, metrics.RowCount) : 4;
        int lastRow = Math.Min(metrics.RowCount - 1, bootstrapRows - 1);
        int lastCol = horizontalScroll
            ? Math.Min(columns - 1, 7)
            : columns - 1;
        var range = new WaveRevealViewportRange(columns, 0, lastRow, 0, lastCol);
        ApplyWaveRevealRange(count, horizontalScroll, range, clearExisting);
    }

    private void ApplyWaveRevealRange(
        int count,
        bool horizontalScroll,
        WaveRevealViewportRange range,
        bool clearExisting)
    {
        if (clearExisting)
            _imageReveal.Clear();

        bool added = false;
        for (int row = range.FirstRow; row <= range.LastRow; row++)
        {
            for (int col = range.FirstCol; col <= range.LastCol; col++)
            {
                int index = row * range.Columns + col;
                if (index < 0 || index >= count || _imageReveal.ContainsKey(index))
                    continue;

                float delay = horizontalScroll
                    ? col * WaveRevealRowStaggerSeconds + row * WaveRevealColStaggerSeconds
                    : row * WaveRevealRowStaggerSeconds + col * WaveRevealColStaggerSeconds;
                delay = Math.Min(WaveRevealMaxDelaySeconds, delay);
                _imageReveal[index] = new ImageRevealState(0f, 0f, delay);
                added = true;
            }
        }

        if (!added)
            return;

        if (_lastTicks == 0)
            _lastTicks = Stopwatch.GetTimestamp();
        RegisterForNextAnimationFrameUpdate();
    }

    private bool AnimateImageReveals(float dt)
    {
        if (_imageReveal.Count == 0)
            return false;

        bool animating = false;
        var finished = new List<int>();
        foreach (var index in _imageReveal.Keys.ToArray())
        {
            var state = _imageReveal[index];
            if (state.DelayRemaining > 0f)
            {
                float nextDelay = state.DelayRemaining - dt;
                if (nextDelay > 0f)
                {
                    _imageReveal[index] = state.WithDelay(nextDelay);
                    animating = true;
                    continue;
                }

                state = state.WithDelay(0f);
            }

            const float target = 1f;
            if (Math.Abs(state.Opacity - target) > 0.001f || Math.Abs(state.Velocity) > 0.001f)
            {
                double stiffness = 190.0;
                double damping = 2.0 * Math.Sqrt(stiffness) * 0.9;
                float velocity = state.Velocity +
                                 (float)((target - state.Opacity) * stiffness - state.Velocity * damping) * dt;
                float opacity = state.Opacity + velocity * dt;
                opacity = Math.Clamp(opacity, 0f, 1f);
                _imageReveal[index] = state.WithOpacity(opacity, velocity);
                animating = true;
            }
            else
                finished.Add(index);
        }

        foreach (var index in finished)
            _imageReveal.Remove(index);

        return animating;
    }

    private float GetImageRevealOpacity(int index) =>
        _imageReveal.TryGetValue(index, out var state) ? Math.Clamp(state.Opacity, 0f, 1f) : 1f;

    private void PruneImageRevealStates(int slotCount)
    {
        if (_imageReveal.Count == 0)
            return;

        var stale = _imageReveal.Keys.Where(index => index >= slotCount).ToList();
        foreach (var index in stale)
            _imageReveal.Remove(index);
    }

    private int EstimateViewportCenterIndex()
    {
        if (_images.Count == 0)
            return 0;

        if (_selectedIndex >= 0 && _selectedIndex < _images.Count)
            return _selectedIndex;

        var metrics = ComputeMetrics(_images.Count);
        if (metrics.RowCount <= 0 || metrics.Columns <= 0)
            return 0;

        if (_horizontalScrollEnabled)
        {
            float centerX = (float)_currentScrollY + _visualSize.X * 0.5f;
            int col = (int)MathF.Round((centerX - metrics.PaddingLeft) / Math.Max(1f, metrics.ColumnPitch));
            col = Math.Clamp(col, 0, Math.Max(0, metrics.Columns - 1));
            int row = Math.Clamp(metrics.RowCount / 2, 0, Math.Max(0, metrics.RowCount - 1));
            return Math.Clamp(row * metrics.Columns + col, 0, _images.Count - 1);
        }

        float rowHeight = metrics.CardHeight + metrics.Spacing;
        float centerY = (float)_currentScrollY + _visualSize.Y * 0.5f;
        int centerRow = (int)MathF.Round((centerY - metrics.PaddingTop) / Math.Max(1f, rowHeight));
        centerRow = Math.Clamp(centerRow, 0, Math.Max(0, metrics.RowCount - 1));
        int centerCol = Math.Clamp(metrics.Columns / 2, 0, Math.Max(0, metrics.Columns - 1));
        return Math.Clamp(centerRow * metrics.Columns + centerCol, 0, _images.Count - 1);
    }

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

    private void DrawScrollFrameCard(SKCanvas canvas, SKRect rect, SKImage? img)
    {
        if (img != null)
        {
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.Shader = null;
            _cardPaint.ImageFilter = null;
            _cardPaint.IsAntialias = false;
            _cardPaint.FilterQuality = SKFilterQuality.Low;
            _cardPaint.Color = SKColors.White;
            canvas.DrawImage(img, rect, _cardPaint);
            return;
        }

        DrawScrollFramePlaceholder(canvas, rect);
    }

    private void DrawScrollFramePlaceholder(SKCanvas canvas, SKRect rect)
    {
        _cardPaint.Style = SKPaintStyle.Fill;
        _cardPaint.Shader = null;
        _cardPaint.ImageFilter = null;
        _cardPaint.IsAntialias = false;
        _cardPaint.FilterQuality = SKFilterQuality.None;
        _cardPaint.Color = SKColor.Parse("#1E1E1E");
        canvas.DrawRoundRect(rect, CardCornerRadius, CardCornerRadius, _cardPaint);
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
    }

    private void QueueNativeImageDisposal(SKImage img)
    {
        if (_pendingDisposal.Add(img))
            _pendingDisposalAge[img] = 0;
        RegisterForNextAnimationFrameUpdate();
    }

    private void FlushPendingDisposals(bool force)
    {
        if (_pendingDisposal.Count == 0)
            return;

        SKImage[] candidates = [.. _pendingDisposal];
        foreach (var img in candidates)
        {
            if (!force && IsImageReferencedByGrid(img))
            {
                _pendingDisposalAge[img] = 0;
                continue;
            }

            if (!force)
            {
                _pendingDisposalAge.TryGetValue(img, out int age);
                if (age < PendingDisposalFrameDelay)
                {
                    _pendingDisposalAge[img] = age + 1;
                    continue;
                }
            }

            if (!_pendingDisposal.Remove(img))
                continue;

            _pendingDisposalAge.Remove(img);
            RemoveImageCaches(img);
            DisposeNativeImage(img);
        }

        if (_pendingDisposal.Count > 0)
            RegisterForNextAnimationFrameUpdate();
    }

    private static bool IsBakedCardImage(SKImage img)
    {
        if (img.Width <= 0)
            return false;

        float expected = BaseCardHeight / BaseCardWidth;
        float actual = img.Height / (float)img.Width;
        return Math.Abs(actual - expected) <= 0.04f;
    }

    private static void DisposeNativeImage(SKImage img)
    {
        try { img.Dispose(); } catch (Exception ex) { Log.Warn("Failed to dispose SKImage", ex); }
    }

    private bool IsImageReferencedByGrid(SKImage? image)
    {
        if (image == null)
            return false;

        if (ReferenceEquals(_draggedImage, image))
            return true;

        for (int i = 0; i < _images.Count; i++)
        {
            if (ReferenceEquals(_images[i], image))
                return true;
        }

        return false;
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
