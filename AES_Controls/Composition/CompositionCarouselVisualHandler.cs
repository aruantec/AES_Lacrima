using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;
using System.Numerics;
using log4net;

namespace AES_Controls.Composition
{
    internal static class VectorExtensions { public static Point ToPoint(this Vector2 v) => new Point(v.X, v.Y); }

    internal record ItemWidthMessage(double Value);
    internal record ItemHeightMessage(double Value);
    internal record SpacingMessage(double Value);
    internal record ScaleMessage(double Value);
    internal record VerticalOffsetMessage(double Value);
    internal record SliderVerticalOffsetMessage(double Value);
    internal record SliderTrackHeightMessage(double Value);
    internal record SideTranslationMessage(double Value);
    internal record StackSpacingMessage(double Value);
    internal record BackgroundMessage(SKColor Color);
    internal record GlobalOpacityMessage(double Value);
    internal record UpdateImageMessage(int Index, SKImage? Image, bool IsLoading = false);
    internal record UseFullCoverSizeMessage(bool Value);
    internal record UpdateCoverFoundMessage(int Index, bool Found);
    internal record ResetCoverFoundMessage(HashSet<int> Indices);
    internal record UpdateOverlayHoverMessage(int Index, int ButtonId);
    internal record UpdateOverlayPressedMessage(int Index, int ButtonId);
    internal record DisposeImageMessage(SKImage Image);
    internal record DragStateMessage(int Index, bool IsDragging);
    internal record DragPositionMessage(Vector2 Position);
    internal record DropTargetMessage(int Index);
    internal record SliderPressedMessage(bool IsPressed);

    public class CompositionCarouselVisualHandler : CompositionCustomVisualHandler
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(CompositionCarouselVisualHandler));

        private float _visibleRange = 10;
        private bool _visibleRangeDirty = true;
        private double _targetIndex;
        private double _currentIndex;
        private double _currentVelocity;
        private long _lastTicks;
        private float _itemSpacing = 1.0f;
        private float _itemScale = 1.0f;
        private float _itemWidth = 200.0f;
        private float _itemHeight = 200.0f;
        private float _verticalOffset;
        private float _sliderVerticalOffset = 60.0f;
        private float _sliderTrackHeight = 4.0f;
        private float _sideTranslation = 320.0f;
        private float _stackSpacing = 160.0f;
        private HashSet<int> _coverFoundIndices = new();
        private int _hoveredItemIndex = -1;
        private int _hoveredButtonId = 0;
        private int _pressedItemIndex = -1;
        private int _pressedButtonId = 0;
        private Vector2 _visualSize;
        private SKColor _backgroundColor = SKColors.Transparent;
        private List<SKImage> _images = new();
        private Dictionary<SKImage, SKShader> _shaderCache = new();
        private HashSet<int> _loadingIndices = new();
        private float _spinnerRotation;

        private int _draggingIndex = -1;
        private int _dropTargetIndex = -1;
        private double _smoothDropTargetIndex = -1;
        private Vector2 _dragPosition;
        private bool _isDropping;
        private float _dropAlpha = 1.0f;
        private float _globalTransitionAlpha = 1.0f;
        private float _currentGlobalOpacity = 1.0f;
        private float _targetGlobalOpacity = 1.0f;
        private float _currentGlobalOpacityVelocity;
        private bool _isSliderPressed;
        private bool _useFullCoverSize;
        private bool _fullCoverSizeInitialized;
        private float _fullCoverSizeFactor;
        private float _fullCoverSizeVelocity;

        private readonly SKPaint _quadPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        // Projection / depth tuning to reduce perspective distortion on side items
        private readonly float _projectionDistance = 2500f; // larger => weaker perspective
        private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 4, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _sliderPaint = new() { IsAntialias = true };
        private readonly SKPaint _overlayBgPaint = new() { IsAntialias = true, Color = SKColors.Black.WithAlpha(180) };
        private readonly SKPaint _overlayTextPaint = new() { IsAntialias = true, Color = SKColors.White, TextSize = 24, TextAlign = SKTextAlign.Center };
        private readonly SKPaint _okButtonPaint = new() { IsAntialias = true, Color = SKColor.Parse("#4CAF50") };
        private readonly SKPaint _cancelButtonPaint = new() { IsAntialias = true, Color = SKColor.Parse("#F44336") };
        private readonly SKPaint _buttonTextPaint = new() { IsAntialias = true, Color = SKColors.White, TextSize = 20, TextAlign = SKTextAlign.Center, FakeBoldText = true };
        private readonly SKMaskFilter _blurFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5);
        private readonly SKMaskFilter _sliderBlurFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2);
        private readonly Dictionary<SKImage, (int Width, int Height)> _dimCache = new();
        
        private readonly SKPoint[] _vBuffer = new SKPoint[4];
        private readonly SKPoint[] _tBuffer = new SKPoint[4];
        private static readonly ushort[] QuadIndices = { 0, 1, 2, 0, 2, 3 };
        

        private SKShader? _trackShader;
        private SKShader? _thumbShader;
        private float _lastSliderW, _lastThumbX;

        private readonly SKColor _trackColor1 = SKColor.Parse("#444444").WithAlpha(240);
        private readonly SKColor _trackColor2 = SKColor.Parse("#777777").WithAlpha(240);
        private readonly SKColor _thumbColor1 = SKColors.White;

        private readonly SKColor _thumbColor2 = SKColor.Parse("#F0F0F0");

        public override void OnMessage(object message)
        {
            if (message is double index) 
            { 
                _targetIndex = index; 
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate(); 
            }
            else if (message is Vector2 size) { _visualSize = size; _visibleRangeDirty = true; Invalidate(); }
            else if (message is IEnumerable<SKImage> enumerableImgs && message is not string) 
            { 
                var imgs = enumerableImgs as List<SKImage> ?? enumerableImgs.ToList();
                var newImgs = new HashSet<SKImage>(imgs.Where(i => i != null));
                foreach (var img in _images) 
                {
                    if (!newImgs.Contains(img)) 
                    {
                        _dimCache.Remove(img);
                        DisposeShaderOnly(img);
                    }
                }
                _images = imgs; 
                RegisterForNextAnimationFrameUpdate(); 
            }
            else if (message is UpdateImageMessage update)
            {
                if (update.Index >= 0 && update.Index < _images.Count)
                {
                    var oldImg = _images[update.Index];
                    var newImg = update.Image;

                    if (oldImg != newImg)
                    {
                        _dimCache.Remove(oldImg);
                        DisposeShaderOnly(oldImg);
                    }

                    if (newImg != null)
                    {
                        _images[update.Index] = newImg; 
                        _loadingIndices.Remove(update.Index);
                    }
                    
                    if (update.IsLoading) _loadingIndices.Add(update.Index);
                    else if (newImg == null)
                    {
                        _images[update.Index] = null!; 
                        _loadingIndices.Remove(update.Index);
                    }
                    Invalidate();
                }
            }
            else if (message is DisposeImageMessage dispose)
            {
                _dimCache.Remove(dispose.Image);
                DisposeImageAndShader(dispose.Image);
            }
            else if (message is DragStateMessage ds) 
            { 
                if (!ds.IsDragging && _draggingIndex != -1)
                {
                    _isDropping = true;
                    _dropAlpha = 0;
                    _draggingIndex = ds.Index;
                }
                else
                {
                    _draggingIndex = ds.IsDragging ? ds.Index : -1;
                    _isDropping = false;
                    _smoothDropTargetIndex = -1;
                }
                Invalidate(); 
            }
            else if (message is DragPositionMessage dp) { _dragPosition = dp.Position; Invalidate(); }
            else if (message is DropTargetMessage dtm) { _dropTargetIndex = dtm.Index; Invalidate(); }
            else if (message is SpacingMessage spacing) { _itemSpacing = (float)spacing.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is ScaleMessage sm) { _itemScale = (float)sm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is ItemWidthMessage iwm) { _itemWidth = (float)iwm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is ItemHeightMessage ihm) { _itemHeight = (float)ihm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is VerticalOffsetMessage vom) { _verticalOffset = (float)vom.Value; Invalidate(); }
            else if (message is SliderVerticalOffsetMessage svim) { _sliderVerticalOffset = (float)svim.Value; ClearSliderShaders(); Invalidate(); }
            else if (message is SliderTrackHeightMessage sthm) { _sliderTrackHeight = (float)sthm.Value; ClearSliderShaders(); Invalidate(); }
            else if (message is SideTranslationMessage stm) { _sideTranslation = (float)stm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is StackSpacingMessage ssm) { _stackSpacing = (float)ssm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is BackgroundMessage bg) { _backgroundColor = bg.Color; Invalidate(); }
            else if (message is GlobalOpacityMessage gom)
            {
                _targetGlobalOpacity = (float)Math.Clamp(gom.Value, 0.0, 1.0);
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
            }
            else if (message is SliderPressedMessage spm) { _isSliderPressed = spm.IsPressed; Invalidate(); }
            else if (message is UseFullCoverSizeMessage ufcs) 
            { 
                _useFullCoverSize = ufcs.Value; 
                if (!_fullCoverSizeInitialized)
                {
                    _fullCoverSizeFactor = _useFullCoverSize ? 1.0f : 0.0f;
                    _fullCoverSizeInitialized = true;
                }
                if (_lastTicks == 0) _lastTicks = Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate();
                Invalidate(); 
            }
            else if (message is UpdateCoverFoundMessage ucf) { if (ucf.Found) _coverFoundIndices.Add(ucf.Index); else _coverFoundIndices.Remove(ucf.Index); Invalidate(); }
            else if (message is ResetCoverFoundMessage rcf) { _coverFoundIndices = rcf.Indices; Invalidate(); }
            else if (message is UpdateOverlayHoverMessage ohover) { _hoveredItemIndex = ohover.Index; _hoveredButtonId = ohover.ButtonId; Invalidate(); }
            else if (message is UpdateOverlayPressedMessage opress) { _pressedItemIndex = opress.Index; _pressedButtonId = opress.ButtonId; Invalidate(); }
        }

        private void ClearSliderShaders()
        {
            _trackShader?.Dispose(); _trackShader = null;
            _thumbShader?.Dispose(); _thumbShader = null;
        }

        public override void OnAnimationFrameUpdate()
        {
            long currentTicks = Stopwatch.GetTimestamp();
            if (_lastTicks == 0) _lastTicks = currentTicks;
            double dt = (double)(currentTicks - _lastTicks) / Stopwatch.Frequency;
            _lastTicks = currentTicks;
            if (dt > 0.1) dt = 0.1;

            double distance = _targetIndex - _currentIndex;
            // tuned spring parameters for a smooth buttery glide
            double animStiffness = 45.0;
            double animDamping = 2.0 * Math.Sqrt(animStiffness) * 1.15;
            _currentVelocity += (distance * animStiffness - _currentVelocity * animDamping) * dt;
            _currentIndex += _currentVelocity * dt;
            _spinnerRotation = (_spinnerRotation + 8f) % 360f;
            
            // Snap to target when very close to ensure zero jitter or micro-vibrations
            if (Math.Abs(distance) < 0.0005 && Math.Abs(_currentVelocity) < 0.005)
            {
                _currentIndex = _targetIndex;
                _currentVelocity = 0;
            }

            if (_draggingIndex != -1)
            {
                if (_smoothDropTargetIndex == -1) _smoothDropTargetIndex = _dropTargetIndex;
                else
                {
                    // use an exponential smoothing (time-constant) so reaction feels natural and framerate-independent
                    // larger tau => slower, smoother movement when items shift to make room for dragged item
                    double tau = 0.25; // seconds — increase to make movement even slower
                    double alpha = 1.0 - Math.Exp(-dt / Math.Max(1e-6, tau));
                    _smoothDropTargetIndex += (_dropTargetIndex - _smoothDropTargetIndex) * alpha;
                }
            }
            if (_isDropping)
            {
                _dropAlpha += (float)(dt / 0.25);
                if (_dropAlpha >= 1.0f) { _dropAlpha = 1.0f; _isDropping = false; _draggingIndex = -1; _smoothDropTargetIndex = -1; }
                Invalidate();
            }
            if (_globalTransitionAlpha < 1.0f)
            {
                _globalTransitionAlpha += (float)(dt / 0.35);
                if (_globalTransitionAlpha > 1.0f) _globalTransitionAlpha = 1.0f;
                Invalidate();
            }

            // Smoothly animate global opacity target (for fade in/out) using a second-order spring
            // This produces a smoother, buttery fade compared to simple linear easing.
            if (Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.0005f || Math.Abs(_currentGlobalOpacityVelocity) > 0.0005f)
            {
                // Tuned spring parameters for opacity
                double opStiffness = 30.0; // higher -> snappier
                double opDamping = 2.0 * Math.Sqrt(opStiffness) * 1.0; // critical-ish damping multiplier

                _currentGlobalOpacityVelocity += (float)((_targetGlobalOpacity - _currentGlobalOpacity) * opStiffness - _currentGlobalOpacityVelocity * opDamping) * (float)dt;
                _currentGlobalOpacity += _currentGlobalOpacityVelocity * (float)dt;
                // clamp to valid range
                if (_currentGlobalOpacity < 0f) _currentGlobalOpacity = 0f;
                else if (_currentGlobalOpacity > 1f) _currentGlobalOpacity = 1f;
                Invalidate();
            }

            // Smoothly animate full cover size factor
            float targetFactor = _useFullCoverSize ? 1.0f : 0.0f;
            if (Math.Abs(_fullCoverSizeFactor - targetFactor) > 0.0005f || Math.Abs(_fullCoverSizeVelocity) > 0.0005f)
            {
                double stiffness = 40.0;
                double damping = 2.0 * Math.Sqrt(stiffness) * 1.0;
                _fullCoverSizeVelocity += (float)((targetFactor - _fullCoverSizeFactor) * stiffness - _fullCoverSizeVelocity * damping) * (float)dt;
                _fullCoverSizeFactor += _fullCoverSizeVelocity * (float)dt;
                if (_fullCoverSizeFactor < 0f) _fullCoverSizeFactor = 0f;
                else if (_fullCoverSizeFactor > 1f) _fullCoverSizeFactor = 1f;
                Invalidate();
            }

            bool isAnimating = Math.Abs(distance) > 0.0001 || Math.Abs(_currentVelocity) > 0.0001 || _globalTransitionAlpha < 1.0f || Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f || Math.Abs(_fullCoverSizeFactor - targetFactor) > 0.001f || _isDropping || _draggingIndex != -1;
            if (isAnimating || _loadingIndices.Count > 0) 
            {
                RegisterForNextAnimationFrameUpdate();
                Invalidate();
            }
            else
            {
                _lastTicks = 0;
            }
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            if (_backgroundColor.Alpha > 0) canvas.Clear(_backgroundColor);
            var center = new Vector2(_visualSize.X / 2.0f, (_visualSize.Y / 2.0f) + _verticalOffset);
            float baseW = _itemWidth * _itemScale; float baseH = _itemHeight * _itemScale;

            if (_visibleRangeDirty)
            {
                float itemUnit = Math.Max(0.1f, _itemSpacing * _itemScale);
                _visibleRange = Math.Max(10, (_visualSize.X / 2f / itemUnit - _sideTranslation) / Math.Max(1f, _stackSpacing)) + 8;
                _visibleRangeDirty = false;
            }
            
            int vRange = (int)_visibleRange;
            int total = _images.Count;
            int centerIdx = (int)Math.Round(_currentIndex);
            int start = Math.Max(0, centerIdx - vRange);
            int end = Math.Min(total - 1, centerIdx + vRange);

            // Apply global opacity multiplier from composition-level fade
            canvas.Save();
            for (int i = start; i < centerIdx; i++) RenderItem(canvas, i, center, baseW, baseH);
            for (int i = end; i > centerIdx; i--) RenderItem(canvas, i, center, baseW, baseH);
            if (centerIdx >= 0 && centerIdx < total) RenderItem(canvas, centerIdx, center, baseW, baseH);
            if (_draggingIndex != -1 && _draggingIndex < total) RenderItem(canvas, _draggingIndex, center, baseW, baseH);
            canvas.Restore();
            DrawSlider(canvas);
        }

        private void DrawSlider(SKCanvas canvas)
        {
            if (_images.Count <= 1) return;
            float margin = _sliderVerticalOffset;
            float sliderW = Math.Min(600, _visualSize.X * 0.8f);
            SKRect bounds = new SKRect((_visualSize.X - sliderW) / 2, _visualSize.Y - margin, (_visualSize.X + sliderW) / 2, _visualSize.Y - margin + 80);
            if (sliderW != _lastSliderW) { ClearSliderShaders(); _lastSliderW = sliderW; }
            float trackY = bounds.MidY; SKRect trackRect = new SKRect(bounds.Left, trackY - _sliderTrackHeight / 2, bounds.Right, trackY + _sliderTrackHeight / 2);
            if (_trackShader == null) _trackShader = SKShader.CreateLinearGradient(new SKPoint(trackRect.Left, trackRect.Top), new SKPoint(trackRect.Left, trackRect.Bottom), new[] { _trackColor1, _trackColor2 }, null, SKShaderTileMode.Clamp);
            // Apply global composition opacity to slider visuals; make track slightly transparent by default
            float g = Math.Max(0f, Math.Min(1f, _currentGlobalOpacity));
            float baseTrackAlpha = 170f; // slightly transparent base (0-255)
            float baseTrackStrokeAlpha = 60f;
            _sliderPaint.Style = SKPaintStyle.Fill; _sliderPaint.Shader = _trackShader; _sliderPaint.Color = SKColors.White.WithAlpha((byte)(baseTrackAlpha * g));
            canvas.DrawRoundRect(trackRect, _sliderTrackHeight/2, _sliderTrackHeight/2, _sliderPaint); _sliderPaint.Shader = null;
            _sliderPaint.Style = SKPaintStyle.Stroke; _sliderPaint.StrokeWidth = 1; _sliderPaint.Color = SKColors.White.WithAlpha((byte)(baseTrackStrokeAlpha * g)); canvas.DrawRoundRect(trackRect, _sliderTrackHeight/2, _sliderTrackHeight/2, _sliderPaint);
            float thumbW = 45; float thumbH = 16; float pct = (float)(_currentIndex / Math.Max(1, _images.Count - 1)); float thumbX = bounds.Left + (thumbW / 2) + pct * (bounds.Width - thumbW);

            SKRect thumbRect = new SKRect(thumbX - thumbW / 2, trackY - thumbH / 2, thumbX + thumbW / 2, trackY + thumbH / 2);
            if (Math.Abs(thumbX - _lastThumbX) > 0.1f || _thumbShader == null) { _thumbShader?.Dispose(); _thumbShader = SKShader.CreateLinearGradient(new SKPoint(thumbRect.Left, thumbRect.Top), new SKPoint(thumbRect.Left, thumbRect.Bottom), new[] { _thumbColor1, _thumbColor2 }, null, SKShaderTileMode.Clamp); _lastThumbX = thumbX; }
            if (_isSliderPressed) { _sliderPaint.Style = SKPaintStyle.Fill; _sliderPaint.Color = SKColors.White.WithAlpha((byte)(120 * g)); _sliderPaint.MaskFilter = _blurFilter; var glow = thumbRect; glow.Inflate(4,4); canvas.DrawRoundRect(glow, 10, 10, _sliderPaint); _sliderPaint.MaskFilter = null; }
            _sliderPaint.Style = SKPaintStyle.Fill; _sliderPaint.Color = SKColors.Black.WithAlpha((byte)(100 * g)); _sliderPaint.MaskFilter = _sliderBlurFilter; canvas.DrawRoundRect(new SKRect(thumbRect.Left, thumbRect.Top + 1, thumbRect.Right, thumbRect.Bottom + 1), 8, 8, _sliderPaint); _sliderPaint.MaskFilter = null;
            _sliderPaint.Shader = _thumbShader; _sliderPaint.Color = SKColors.White.WithAlpha((byte)(255 * g)); canvas.DrawRoundRect(thumbRect, 8, 8, _sliderPaint); _sliderPaint.Shader = null;
            _sliderPaint.Style = SKPaintStyle.Stroke; _sliderPaint.StrokeWidth = 1.0f; _sliderPaint.Color = SKColors.Black.WithAlpha((byte)(50 * g)); canvas.DrawRoundRect(thumbRect, 8, 8, _sliderPaint);
        }

        private void RenderItem(SKCanvas canvas, int i, Vector2 center, float baseWidth, float baseHeight)
        {
            SKImage? img = (i >= 0 && i < _images.Count) ? _images[i] : null;
            float itemW = baseWidth;
            float itemH = baseHeight;

            if (img != null && !_loadingIndices.Contains(i) && _fullCoverSizeFactor > 0.001f)
            {
                if (!_dimCache.TryGetValue(img, out var dims)) { try { dims = _dimCache[img] = (img.Width, img.Height); } catch { dims = (0, 0); } }
                if (dims.Width > 0 && dims.Height > 0)
                {
                    float aspect = (float)dims.Width / dims.Height;
                    // Respect dimensions: adjust width based on aspect ratio, keeping height constant
                    if (aspect > 1.1f || aspect < 0.9f)
                    {
                        float targetW = baseHeight * aspect;
                        itemW = baseWidth + (targetW - baseWidth) * _fullCoverSizeFactor;
                    }
                }
            }

            float visualI = i;
            if (_draggingIndex != -1 && _smoothDropTargetIndex != -1 && i != _draggingIndex)
            {
                float rank = (i < _draggingIndex) ? i : (float)(i - 1);
                float slotDiff = rank - (float)_smoothDropTargetIndex;
                float shiftStrength = 1.0f / (1.0f + (float)Math.Exp(-(slotDiff + 0.5f) * 8.0f));
                float parting = (float)Math.Exp(-(slotDiff + 0.5f) * (slotDiff + 0.5f) * 2.0f);
                float partedVisualI = rank + shiftStrength + (slotDiff < -0.5f ? -0.25f : 0.25f) * parting;

                if (_isDropping) visualI = partedVisualI + (i - partedVisualI) * (float)(1.0 - Math.Pow(1.0 - _dropAlpha, 3));
                else visualI = partedVisualI;
            }
            float diff = (float)(visualI - _currentIndex); float absDiff = Math.Abs(diff);

            float transitionEase = (float)Math.Tanh(diff * 2.2f);
            float rotationY = -transitionEase * 0.95f;
            float stackFactor = Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f) * _stackSpacing;
            float translationX = (transitionEase * _sideTranslation + stackFactor) * _itemSpacing * _itemScale;
            float translationY = 0;
            float translationZ = (float)(-Math.Pow(absDiff, 0.8f) * 220f * _itemSpacing * _itemScale);

            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float scale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));

            float curWidthRatio = itemW / (baseWidth > 0 ? baseWidth : 1.0f);
            float finalTranslationX = translationX * curWidthRatio;

            if (i == _draggingIndex)
            {
                if (_isDropping)
                {
                    float eased = (float)(1.0 - Math.Pow(1.0 - _dropAlpha, 3));
                    float zS = (1000f - itemW) / 1000f;
                    float dX = (_dragPosition.X - center.X) * zS;
                    float dY = (_dragPosition.Y - center.Y) * zS;
                    finalTranslationX = dX + (finalTranslationX - dX) * eased;
                    translationY = dY + (translationY - dY) * eased;
                    translationZ = itemW + (translationZ - itemW) * eased;
                    scale = 0.82f + (scale - 0.82f) * eased;
                    rotationY *= eased;
                }
                else
                {
                    translationZ = itemW;
                    float zS = (1000f - translationZ) / 1000f;
                    finalTranslationX = (_dragPosition.X - center.X) * zS;
                    translationY = (_dragPosition.Y - center.Y) * zS;
                    scale = 0.82f;
                    rotationY = 0;
                }
            }

            var matrix = Matrix4x4.CreateTranslation(new Vector3(finalTranslationX, translationY, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(scale);
            DrawQuad(canvas, itemW, itemH, matrix, img, (i == _draggingIndex ? 0 : absDiff), center);

            if (_coverFoundIndices.Contains(i))
            {
                void ProjToBuffer(SKPoint[] buffer, float x, float y, float sMult = 1.0f, float midX = 0, float midY = 0)
                {
                    float dx = (x - midX) * sMult;
                    float dy = (y - midY) * sMult;
                    var vt = Vector3.Transform(new Vector3(midX + dx, midY + dy, 0), matrix);
                    float s = _projectionDistance / (_projectionDistance - vt.Z);
                    if (s < 0.5f) s = 0.5f; else if (s > 1.6f) s = 1.6f;
                    buffer[0] = new SKPoint(center.X + vt.X * s, center.Y + vt.Y * s);
                }
                SKPoint[] pt = new SKPoint[1];

                float overlayH = itemH * 0.35f;
                float topY = itemH / 2 - overlayH;
                float botY = itemH / 2;

                // Draw background manually using vertices to handle tilt
                SKPoint[] rectV = new SKPoint[4];
                void FillRectV(float x1, float y1, float x2, float y2, float sMult = 1.0f) {
                   float midX = (x1 + x2) / 2;
                   float halfW = (x2 - x1) / 2 * sMult;
                   float midY = (y1 + y2) / 2;
                   float halfH = (y2 - y1) / 2 * sMult;

                   var v1 = new Vector3(midX - halfW, midY - halfH, 0);
                   var v2 = new Vector3(midX + halfW, midY - halfH, 0);
                   var v3 = new Vector3(midX + halfW, midY + halfH, 0);
                   var v4 = new Vector3(midX - halfW, midY + halfH, 0);

                   var vt1 = Vector3.Transform(v1, matrix); float st1 = _projectionDistance/(_projectionDistance-vt1.Z); rectV[0]=new SKPoint(center.X+vt1.X*st1, center.Y+vt1.Y*st1);
                   var vt2 = Vector3.Transform(v2, matrix); float st2 = _projectionDistance/(_projectionDistance-vt2.Z); rectV[1]=new SKPoint(center.X+vt2.X*st2, center.Y+vt2.Y*st2);
                   var vt3 = Vector3.Transform(v3, matrix); float st3 = _projectionDistance/(_projectionDistance-vt3.Z); rectV[2]=new SKPoint(center.X+vt3.X*st3, center.Y+vt3.Y*st3);
                   var vt4 = Vector3.Transform(v4, matrix); float st4 = _projectionDistance/(_projectionDistance-vt4.Z); rectV[3]=new SKPoint(center.X+vt4.X*st4, center.Y+vt4.Y*st4);
                }

                FillRectV(-itemW/2, topY, itemW/2, botY);
                canvas.DrawVertices(SKVertexMode.TriangleFan, rectV, null, null, null, _overlayBgPaint);

                ProjToBuffer(pt, 0, topY + 40);
                canvas.DrawText("Cover found", pt[0].X, pt[0].Y, _overlayTextPaint);

                float btnW = itemW * 0.4f;
                float btnH = itemH * 0.15f;
                float btnGap = itemW * 0.05f;
                float btnY = itemH / 2 - btnH - 12;

                // OK Button effect
                _okButtonPaint.Color = SKColor.Parse("#4CAF50");
                float okScale = 1.0f;
                if (_hoveredItemIndex == i && _hoveredButtonId == 1) _okButtonPaint.Color = SKColor.Parse("#66BB6A");
                if (_pressedItemIndex == i && _pressedButtonId == 1) { _okButtonPaint.Color = SKColor.Parse("#388E3C"); okScale = 0.95f; }

                float okMidX = -btnW / 2 - btnGap / 2;
                float btnMidY = btnY + btnH / 2;

                FillRectV(-btnW - btnGap / 2, btnY, -btnGap / 2, btnY + btnH, okScale);
                canvas.DrawVertices(SKVertexMode.TriangleFan, rectV, null, null, null, _okButtonPaint);
                ProjToBuffer(pt, okMidX, btnMidY + 7, okScale, okMidX, btnMidY);
                canvas.DrawText("SAVE", pt[0].X, pt[0].Y, _buttonTextPaint);

                // Cancel Button effect
                _cancelButtonPaint.Color = SKColor.Parse("#F44336");
                float cancelScale = 1.0f;
                if (_hoveredItemIndex == i && _hoveredButtonId == 2) _cancelButtonPaint.Color = SKColor.Parse("#EF5350");
                if (_pressedItemIndex == i && _pressedButtonId == 2) { _cancelButtonPaint.Color = SKColor.Parse("#D32F2F"); cancelScale = 0.95f; }

                float cancelMidX = btnW / 2 + btnGap / 2;
                FillRectV(btnGap / 2, btnY, btnGap / 2 + btnW, btnY + btnH, cancelScale);
                canvas.DrawVertices(SKVertexMode.TriangleFan, rectV, null, null, null, _cancelButtonPaint);
                ProjToBuffer(pt, cancelMidX, btnMidY + 7, cancelScale, cancelMidX, btnMidY);
                canvas.DrawText("Skip", pt[0].X, pt[0].Y, _buttonTextPaint);
            }

            if (_loadingIndices.Contains(i)) DrawSpinner(canvas, center, matrix);
            var refMat = Matrix4x4.CreateScale(1, -1, 1) * Matrix4x4.CreateTranslation(0, itemH + 25, 0) * matrix;
            DrawQuad(canvas, itemW, itemH, refMat, img, (i == _draggingIndex ? 0 : absDiff), center, true);
        }

        private void DisposeShaderOnly(SKImage? img) { if (img != null && _shaderCache.Remove(img, out var shader)) shader.Dispose(); }
        private void DisposeImageAndShader(SKImage? img) 
        { 
            if (img == null) return; 
            DisposeShaderOnly(img); 
            try 
            { 
                // Always dispose if requested from UI thread as it means it's removed from UI-side cache
                img.Dispose();
            } 
            catch (Exception ex) 
            {
                Log.Warn("Failed to dispose image", ex);
            } 
        }

        private void DrawQuad(SKCanvas canvas, float w, float h, Matrix4x4 model, SKImage? image, float absDiff, Vector2 center, bool isRef = false)
        {
            float opacity = (float)(isRef ? 0.08 : 1.0) * (float)(1.0 - absDiff * 0.2) * _globalTransitionAlpha * _currentGlobalOpacity;
            if (opacity < 0.01f) return;

            void Proj(int idx, float x, float y)
            {
                var vt = Vector3.Transform(new Vector3(x, y, 0), model);
                float s = _projectionDistance / (_projectionDistance - vt.Z);
                if (s < 0.5f) s = 0.5f;
                else if (s > 1.6f) s = 1.6f;
                _vBuffer[idx] = new SKPoint(center.X + vt.X * s, center.Y + vt.Y * s);
            }

            if (image != null)
            {
                if (!_dimCache.TryGetValue(image, out var dims)) { try { dims = _dimCache[image] = (image.Width, image.Height); } catch { image = null; } }
                if (image != null && dims.Width > 0)
                {
                    _quadPaint.Color = SKColors.White.WithAlpha((byte)(255 * opacity));
                    if (!_shaderCache.TryGetValue(image, out var shader)) { try { _shaderCache[image] = shader = image.ToShader(); } catch { image = null; } }
                    if (image != null && shader != null)
                    {
                        _quadPaint.Shader = shader;
                        float sc = Math.Max(w / dims.Width, h / dims.Height);
                        float wR = w / sc; float hR = h / sc;
                        float xO = (dims.Width - wR) / 2f; float yO = (dims.Height - hR) / 2f;

                        int horizontalSegments = (w > h * 1.25f) ? 8 : (w > h ? 4 : 2);
                        if (isRef) horizontalSegments = 1;

                        for (int s = 0; s < horizontalSegments; s++)
                        {
                            float x0 = -w / 2 + (w * s / horizontalSegments);
                            float x1 = -w / 2 + (w * (s + 1) / horizontalSegments);
                            float tX0 = xO + (wR * s / horizontalSegments);
                            float tX1 = xO + (wR * (s + 1) / horizontalSegments);

                            Proj(0, x0, -h / 2); Proj(1, x1, -h / 2); Proj(2, x1, h / 2); Proj(3, x0, h / 2);
                            _tBuffer[0] = new SKPoint(tX0, yO); _tBuffer[1] = new SKPoint(tX1, yO);
                            _tBuffer[2] = new SKPoint(tX1, yO + hR); _tBuffer[3] = new SKPoint(tX0, yO + hR);

                            canvas.DrawVertices(SKVertexMode.Triangles, _vBuffer, _tBuffer, null, QuadIndices, _quadPaint);
                        }
                        _quadPaint.Shader = null;
                        return;
                    }
                }
            }
        }

        private void DrawSpinner(SKCanvas canvas, Vector2 center, Matrix4x4 model)
        {
            var vt = Vector3.Transform(new Vector3(0, 0, 1), model); float s = 1000f / (1000f - vt.Z);
            canvas.Save(); canvas.Translate(center.X + vt.X * s, center.Y + vt.Y * s); canvas.RotateDegrees(_spinnerRotation);
            for (int i = 0; i < 10; i++) { _spinnerPaint.Color = SKColors.White.WithAlpha((byte)(255 * i / 10)); canvas.DrawLine(0, -10, 0, -20, _spinnerPaint); canvas.RotateDegrees(36f); }
            canvas.Restore();
        }
    }
}
