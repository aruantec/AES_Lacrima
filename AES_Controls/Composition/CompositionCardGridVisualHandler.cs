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
internal record CardGridAttachSyncMessage(CardGridAnimationSyncState State);

public class CompositionCardGridVisualHandler : CompositionCustomVisualHandler
{
    private const float BaseCardWidth = 200f;
    private const float BaseCardHeight = 272f;
    private const float GridPaddingX = 28f;
    private const float GridPaddingTop = 20f;
    private const float ScrollbarMargin = 10f;
    private static readonly SKColor BackgroundColor = SKColor.Parse("#101010");
    private const float CardCornerRadius = 12f;
    private const float TitleAreaRatio = 0.24f;
    private const float MaxFullCoverAspectRatio = 1.35f;

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
    private float _scrollbarOpacity;
    private float _scrollbarOpacityVelocity;
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;
    private float _currentGlobalOpacity = 1f;
    private float _targetGlobalOpacity = 1f;
    private float _currentGlobalOpacityVelocity;
    private bool _pauseLoadingSpinnerAnimation;
    private CardGridAnimationSyncState? _animationSync;

    private List<SKImage?> _images = new();
    private string[] _titles = Array.Empty<string>();
    private HashSet<int> _loadingIndices = new();
    private float _spinnerRotation;

    private readonly SKPaint _cardPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    private readonly SKPaint _titlePaint = CreateTitlePaint(SKColors.White);
    private readonly SKPaint _titleShadowPaint = CreateTitlePaint(SKColors.Black.WithAlpha(140));
    private readonly SKPaint _overlayPaint = new() { IsAntialias = true };
    private readonly SKPaint _scrollbarPaint = new() { IsAntialias = true };
    private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 3, Style = SKPaintStyle.Stroke };
    private readonly SKMaskFilter _scrollbarBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
    private readonly Dictionary<SKImage, (int Width, int Height)> _dimCache = new();
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
                        _dimCache.Remove(img);
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
                    else
                    {
                        _images[update.Index] = null;
                        _loadingIndices.Remove(update.Index);
                    }

                    if (oldImg != null && !ReferenceEquals(oldImg, update.Image))
                        _dimCache.Remove(oldImg);
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
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                break;
            case CardGridDirectScrollFollowMessage direct:
                _directScrollFollow = direct.Enabled;
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
                Invalidate();
                break;
            case CardGridSelectedIndexMessage selected:
                _selectedIndex = selected.Index;
                Invalidate();
                break;
            case CardGridHoveredIndexMessage hovered:
                _hoveredIndex = hovered.Index;
                Invalidate();
                break;
            case CardGridScrollbarPressedMessage scrollbar:
                _isScrollbarPressed = scrollbar.IsPressed;
                Invalidate();
                break;
            case GlobalOpacityMessage opacity:
                _targetGlobalOpacity = (float)Math.Clamp(opacity.Value, 0.0, 1.0);
                _currentGlobalOpacity = _targetGlobalOpacity;
                _currentGlobalOpacityVelocity = 0;
                Invalidate();
                break;
            case PauseLoadingSpinnerAnimationMessage pause:
                _pauseLoadingSpinnerAnimation = pause.IsPaused;
                if (!_pauseLoadingSpinnerAnimation && _loadingIndices.Count > 0)
                {
                    if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                    RegisterForNextAnimationFrameUpdate();
                }
                break;
            case CardGridAttachSyncMessage attach:
                _animationSync = attach.State;
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
        float desiredScrollbarOpacity = needsScrollbar && (_directScrollFollow || _isScrollbarPressed || Math.Abs(_scrollVelocity) > 20 || Math.Abs(_scrollSpringVelocity) > 20)
            ? 1f
            : needsScrollbar ? 0.55f : 0f;
        if (_isScrollbarPressed)
            desiredScrollbarOpacity = 1f;

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

        bool isAnimating = _directScrollFollow ||
                           Math.Abs(_targetScrollY - _currentScrollY) > 0.01 ||
                           Math.Abs(_scrollVelocity) > 0.5 ||
                           Math.Abs(_scrollSpringVelocity) > 0.01 ||
                           Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f ||
                           Math.Abs(_scrollbarOpacity - desiredScrollbarOpacity) > 0.01;
        bool animateSpinners = !_pauseLoadingSpinnerAnimation && _loadingIndices.Count > 0;

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
        canvas.Clear(BackgroundColor);

        if (_images.Count == 0 || _visualSize.X <= 0 || _visualSize.Y <= 0)
            return;

        var metrics = ComputeMetrics(_images.Count);
        float g = Math.Clamp(_currentGlobalOpacity, 0f, 1f);
        if (g <= 0f)
            return;

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, _visualSize.X, _visualSize.Y));
        canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)(g * 255)) });

        canvas.Clear(BackgroundColor);

        int firstRow = Math.Max(0, (int)Math.Floor((_currentScrollY - metrics.PaddingTop) / (metrics.CardHeight + metrics.Spacing)) - 1);
        int lastRow = Math.Min(metrics.RowCount - 1, (int)Math.Ceiling((_currentScrollY + _visualSize.Y) / (metrics.CardHeight + metrics.Spacing)) + 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = 0; col < metrics.Columns; col++)
            {
                int index = row * metrics.Columns + col;
                if (index >= _images.Count)
                    break;

                float x = metrics.PaddingLeft + col * (metrics.CardWidth + metrics.Spacing);
                float y = metrics.PaddingTop + row * (metrics.CardHeight + metrics.Spacing) - (float)_currentScrollY;
                if (y + metrics.CardHeight < 0 || y > _visualSize.Y)
                    continue;

                DrawCard(canvas, index, x, y, metrics, 1f);
            }
        }

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
                RowCount = 0
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
            RowCount = rowCount
        };
    }

    private void DrawCard(SKCanvas canvas, int index, float x, float y, GridMetrics metrics, float globalOpacity)
    {
        var rect = new SKRect(x, y, x + metrics.CardWidth, y + metrics.CardHeight);
        bool isSelected = index == _selectedIndex;
        bool isHovered = index == _hoveredIndex;
        float scale = isSelected ? 1.03f : isHovered ? 1.015f : 1f;

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

        if (img != null && !isLoading)
            DrawCoverImage(canvas, img, rect.Left, rect.Top, metrics.CardWidth, coverH);
        else
            DrawPlaceholder(canvas, rect.Left, rect.Top, metrics.CardWidth, coverH);

        DrawTitleBar(canvas, img, rect, metrics.CardWidth, coverH, titleH, index, globalOpacity);
        canvas.Restore();

        if (isSelected)
        {
            _cardPaint.Style = SKPaintStyle.Stroke;
            _cardPaint.StrokeWidth = 2f;
            _cardPaint.Color = SKColors.White.WithAlpha((byte)(180 * globalOpacity));
            _cardPaint.Shader = null;
            canvas.DrawRoundRect(rect, CardCornerRadius, CardCornerRadius, _cardPaint);
        }

        if (isLoading)
            DrawSpinner(canvas, rect.MidX, rect.Top + coverH * 0.5f, globalOpacity);
    }

    private void DrawCoverImage(SKCanvas canvas, SKImage img, float x, float y, float w, float h)
    {
        if (!TryGetImageDimensions(img, out var dims) || dims.Width <= 0 || dims.Height <= 0)
        {
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.Shader = null;
            _cardPaint.Color = SKColor.Parse("#1A1A1A");
            canvas.DrawRect(x, y, w, h, _cardPaint);
            return;
        }

        float srcAspect = (float)dims.Width / dims.Height;
        float dstAspect = w / h;
        float srcW = dims.Width;
        float srcH = dims.Height;
        if (srcAspect > dstAspect)
        {
            srcW = dims.Height * dstAspect;
            srcH = dims.Height;
        }
        else
        {
            srcW = dims.Width;
            srcH = dims.Width / dstAspect;
        }

        float srcX = (dims.Width - srcW) * 0.5f;
        float srcY = (dims.Height - srcH) * 0.5f;
        var src = new SKRect(srcX, srcY, srcX + srcW, srcY + srcH);
        var dst = new SKRect(x, y, x + w, y + h);
        _cardPaint.Style = SKPaintStyle.Fill;
        _cardPaint.Shader = null;
        _cardPaint.Color = SKColors.White;
        _cardPaint.IsAntialias = true;
        _cardPaint.FilterQuality = SKFilterQuality.Medium;
        canvas.DrawImage(img, src, dst, _cardPaint);
    }

    private void DrawPlaceholder(SKCanvas canvas, float x, float y, float w, float h)
    {
        _cardPaint.Style = SKPaintStyle.Fill;
        _cardPaint.Shader = null;
        _cardPaint.Color = SKColor.Parse("#1E1E1E");
        canvas.DrawRect(x, y, w, h, _cardPaint);
    }

    private void DrawTitleBar(SKCanvas canvas, SKImage? img, SKRect cardRect, float cardW, float coverH, float titleH, int index, float globalOpacity)
    {
        float titleTop = cardRect.Top + coverH;
        var titleRect = new SKRect(cardRect.Left, titleTop, cardRect.Right, cardRect.Bottom);

        canvas.Save();
        canvas.ClipRect(titleRect);

        if (img != null && TryGetImageDimensions(img, out var blurDims) && blurDims.Height > 0)
        {
            float bleed = 18f;
            float srcH = Math.Min(blurDims.Height * 0.4f, blurDims.Height);
            var src = new SKRect(0, blurDims.Height - srcH, blurDims.Width, blurDims.Height);
            var blurDst = new SKRect(cardRect.Left, titleTop - bleed, cardRect.Right, cardRect.Bottom);
            _cardPaint.Style = SKPaintStyle.Fill;
            _cardPaint.FilterQuality = SKFilterQuality.Low;
            _cardPaint.Color = SKColors.White.WithAlpha((byte)(90 * globalOpacity));
            canvas.DrawImage(img, src, blurDst, _cardPaint);
        }

        _overlayPaint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(titleRect.Left, titleTop),
            new SKPoint(titleRect.Left, titleRect.Bottom),
            new[] { SKColors.Black.WithAlpha(20), SKColors.Black.WithAlpha(210) },
            null,
            SKShaderTileMode.Clamp);
        _overlayPaint.Style = SKPaintStyle.Fill;
        canvas.DrawRect(titleRect, _overlayPaint);
        _overlayPaint.Shader = null;

        string title = index < _titles.Length ? _titles[index] : string.Empty;
        if (!string.IsNullOrWhiteSpace(title))
        {
            float textX = cardRect.Left + 12f;
            float textY = titleRect.MidY + 5f;
            title = CompositionSkiaTextHelper.TruncateText(title, cardW - 24f, _titlePaint);
            _titleShadowPaint.TextSize = _titlePaint.TextSize;
            CompositionSkiaTextHelper.DrawText(canvas, title, textX, textY + 1f, _titleShadowPaint);
            _titlePaint.Color = SKColors.White.WithAlpha((byte)(245 * globalOpacity));
            CompositionSkiaTextHelper.DrawText(canvas, title, textX, textY, _titlePaint);
        }

        canvas.Restore();
    }

    private static SKPaint CreateTitlePaint(SKColor color)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            TextSize = 15,
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

    private void DrawScrollbar(SKCanvas canvas, GridMetrics metrics, float globalOpacity)
    {
        if (metrics.MaxScrollY <= 1 || _scrollbarOpacity <= 0.01f)
            return;

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

    private bool TryGetImageDimensions(SKImage image, out (int Width, int Height) dims)
    {
        if (_dimCache.TryGetValue(image, out dims))
            return true;

        dims = (image.Width, image.Height);
        _dimCache[image] = dims;
        return dims.Width > 0 && dims.Height > 0;
    }

    private void CacheImageDimensions(SKImage image) => TryGetImageDimensions(image, out _);

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
