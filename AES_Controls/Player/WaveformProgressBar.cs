using AES_Controls.Helpers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Globalization;
using System.Windows.Input;

namespace AES_Controls.Player
{
    /// <summary>
    /// A progress bar that renders an audio waveform and allows scrubbing by pointer.
    /// </summary>
    public class WaveformProgressBar : ProgressBar
    {
        #region Styled Properties
        public static readonly StyledProperty<IList<float>?> WaveformProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IList<float>?>(nameof(Waveform));

        public static readonly StyledProperty<ICommand?> DragCompletedCommandProperty =
            AvaloniaProperty.Register<WaveformProgressBar, ICommand?>(nameof(DragCompletedCommand));

        public static readonly StyledProperty<IBrush?> PlayedColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(PlayedColor), Brushes.LightBlue);

        public static readonly StyledProperty<IBrush?> UnPlayedColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(UnPlayedColor), Brushes.LightGray);

        public static readonly StyledProperty<IBrush?> IndicatorColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(IndicatorColor), Brushes.White);

        public static readonly StyledProperty<IBrush?> TextForegroundColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(TextForegroundColor), Brushes.White);

        public static readonly StyledProperty<double> TextSizeProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(TextSize), 12);

        public static readonly StyledProperty<bool> IsLoadingProperty =
            AvaloniaProperty.Register<WaveformProgressBar, bool>(nameof(IsLoading));

        public static readonly StyledProperty<IBrush?> LoadingIndicatorColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(LoadingIndicatorColor), Brushes.White);

        public static readonly StyledProperty<double> LoadingIndicatorSizeProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(LoadingIndicatorSize), 30);

        public static readonly StyledProperty<IBrush?> TriangleColorProperty =
            AvaloniaProperty.Register<WaveformProgressBar, IBrush?>(nameof(TriangleColor), Brushes.White);

        public static readonly StyledProperty<double> TriangleOffsetProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(TriangleOffset), 2.0);

        public static readonly StyledProperty<double> TriangleWidthProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(TriangleWidth), 14.0);

        public static readonly StyledProperty<double> TriangleHeightProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(TriangleHeight), 12.0);

        public static readonly StyledProperty<double> WaveformVerticalOffsetProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(WaveformVerticalOffset), 5.0);

        public static readonly StyledProperty<bool> IsTriangleUpwardsProperty =
            AvaloniaProperty.Register<WaveformProgressBar, bool>(nameof(IsTriangleUpwards));

        public static readonly StyledProperty<Thickness> WaveformMarginProperty =
            AvaloniaProperty.Register<WaveformProgressBar, Thickness>(nameof(WaveformMargin), new Thickness(0));

        public static readonly StyledProperty<double> BarGapProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(BarGap), 0.0);

        public static readonly StyledProperty<double> BlockHeightProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(BlockHeight), 0.0);

        public static readonly StyledProperty<double> VerticalGapProperty =
            AvaloniaProperty.Register<WaveformProgressBar, double>(nameof(VerticalGap), 1.0);

        public static readonly StyledProperty<int> VisualBarCountProperty =
            AvaloniaProperty.Register<WaveformProgressBar, int>(nameof(VisualBarCount), 0);

        public static readonly StyledProperty<LinearGradientBrush?> BarGradientProperty =
            AvaloniaProperty.Register<WaveformProgressBar, LinearGradientBrush?>(nameof(BarGradient));

        public static readonly StyledProperty<bool> ShowReflectionProperty =
            AvaloniaProperty.Register<WaveformProgressBar, bool>(nameof(ShowReflection), false);

        public static readonly StyledProperty<bool> IsSymmetricProperty =
            AvaloniaProperty.Register<WaveformProgressBar, bool>(nameof(IsSymmetric), false);

        /// <summary>Waveform sample data used to draw the waveform.</summary>
        public IList<float>? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
        /// <summary>Command executed when a drag/scrub operation completes.</summary>
        public ICommand? DragCompletedCommand { get => GetValue(DragCompletedCommandProperty); set => SetValue(DragCompletedCommandProperty, value); }
        /// <summary>Brush used for the played portion of the waveform.</summary>
        public IBrush? PlayedColor { get => GetValue(PlayedColorProperty); set => SetValue(PlayedColorProperty, value); }
        /// <summary>Brush used for the unplayed portion of the waveform.</summary>
        public IBrush? UnPlayedColor { get => GetValue(UnPlayedColorProperty); set => SetValue(UnPlayedColorProperty, value); }
        /// <summary>Brush used for the progress indicator line.</summary>
        public IBrush? IndicatorColor { get => GetValue(IndicatorColorProperty); set => SetValue(IndicatorColorProperty, value); }
        /// <summary>Brush used for tooltip text.</summary>
        public IBrush? TextForegroundColor { get => GetValue(TextForegroundColorProperty); set => SetValue(TextForegroundColorProperty, value); }
        /// <summary>Font size for tooltip text.</summary>
        public double TextSize { get => GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
        /// <summary>Whether to show a loading spinner overlay.</summary>
        public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
        /// <summary>Color for the loading indicator.</summary>
        public IBrush? LoadingIndicatorColor { get => GetValue(LoadingIndicatorColorProperty); set => SetValue(LoadingIndicatorColorProperty, value); }
        /// <summary>Size of the loading indicator.</summary>
        public double LoadingIndicatorSize { get => GetValue(LoadingIndicatorSizeProperty); set => SetValue(LoadingIndicatorSizeProperty, value); }
        /// <summary>Color for the small triangle indicator under the progress line.</summary>
        public IBrush? TriangleColor { get => GetValue(TriangleColorProperty); set => SetValue(TriangleColorProperty, value); }
        /// <summary>Offset of the triangle from the edge.</summary>
        public double TriangleOffset { get => GetValue(TriangleOffsetProperty); set => SetValue(TriangleOffsetProperty, value); }
        /// <summary>Triangle base width.</summary>
        public double TriangleWidth { get => GetValue(TriangleWidthProperty); set => SetValue(TriangleWidthProperty, value); }
        /// <summary>Triangle height.</summary>
        public double TriangleHeight { get => GetValue(TriangleHeightProperty); set => SetValue(TriangleHeightProperty, value); }
        /// <summary>Vertical offset for waveform rendering.</summary>
        public double WaveformVerticalOffset { get => GetValue(WaveformVerticalOffsetProperty); set => SetValue(WaveformVerticalOffsetProperty, value); }
        /// <summary>Whether the triangle points upward.</summary>
        public bool IsTriangleUpwards { get => GetValue(IsTriangleUpwardsProperty); set => SetValue(IsTriangleUpwardsProperty, value); }
        /// <summary>Margin around the waveform area.</summary>
        public Thickness WaveformMargin { get => GetValue(WaveformMarginProperty); set => SetValue(WaveformMarginProperty, value); }
        /// <summary>Horizontal gap between bars.</summary>
        public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
        /// <summary>Block height when rendering block-style bars.</summary>
        public double BlockHeight { get => GetValue(BlockHeightProperty); set => SetValue(BlockHeightProperty, value); }
        /// <summary>Vertical gap between blocks when rendering in blocks.</summary>
        public double VerticalGap { get => GetValue(VerticalGapProperty); set => SetValue(VerticalGapProperty, value); }
        /// <summary>Number of visual bars to render (0 = use waveform sample count).</summary>
        public int VisualBarCount { get => GetValue(VisualBarCountProperty); set => SetValue(VisualBarCountProperty, value); }
        /// <summary>Optional gradient brush for bar fill when played.</summary>
        public LinearGradientBrush? BarGradient { get => GetValue(BarGradientProperty); set => SetValue(BarGradientProperty, value); }
        /// <summary>Whether to draw a reflected copy of bars below the baseline.</summary>
        public bool ShowReflection { get => GetValue(ShowReflectionProperty); set => SetValue(ShowReflectionProperty, value); }
        /// <summary>Whether to render bars symmetrically about the center line.</summary>
        public bool IsSymmetric { get => GetValue(IsSymmetricProperty); set => SetValue(IsSymmetricProperty, value); }
        #endregion

        private bool _isDragging;
        private double _lastValue;
        private Point? _lastMousePosition;
        private double _loadingAngle;

        private RenderTargetBitmap? _unplayedCache;
        private RenderTargetBitmap? _playedCache;

        private readonly DispatcherTimer _updateTimer;
        private readonly DispatcherTimer _loadingTimer;
        private readonly List<IDisposable> _propertySubscriptions = [];
        private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _waveformCollectionHandler;
        private System.Collections.Specialized.INotifyCollectionChanged? _waveformCollectionRef;

        public WaveformProgressBar()
        {
            ClipToBounds = false;

            // Property Observers
            _propertySubscriptions.Add(this.GetObservable(WaveformProperty).Subscribe(new SimpleObserver<IList<float>?>(OnWaveformChanged)));
            _propertySubscriptions.Add(this.GetObservable(PlayedColorProperty).Subscribe(new SimpleObserver<IBrush?>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(UnPlayedColorProperty).Subscribe(new SimpleObserver<IBrush?>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(WaveformMarginProperty).Subscribe(new SimpleObserver<Thickness>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(BarGapProperty).Subscribe(new SimpleObserver<double>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(BlockHeightProperty).Subscribe(new SimpleObserver<double>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(VerticalGapProperty).Subscribe(new SimpleObserver<double>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(VisualBarCountProperty).Subscribe(new SimpleObserver<int>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(BarGradientProperty).Subscribe(new SimpleObserver<LinearGradientBrush?>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(ShowReflectionProperty).Subscribe(new SimpleObserver<bool>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(IsSymmetricProperty).Subscribe(new SimpleObserver<bool>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(WaveformVerticalOffsetProperty).Subscribe(new SimpleObserver<double>(_ => InvalidateCachesAndRedraw())));
            _propertySubscriptions.Add(this.GetObservable(BoundsProperty).Subscribe(new SimpleObserver<Rect>(_ => InvalidateCachesAndRedraw())));

            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            _loadingTimer.Tick += LoadingTimer_Tick;

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            _updateTimer.Tick += UpdateTimer_Tick;
        }

        private void OnWaveformChanged(IList<float>? list)
        {
            if (_waveformCollectionRef != null && _waveformCollectionHandler != null)
                _waveformCollectionRef.CollectionChanged -= _waveformCollectionHandler;

            _waveformCollectionRef = list as System.Collections.Specialized.INotifyCollectionChanged;
            if (_waveformCollectionRef != null)
            {
                _waveformCollectionHandler = (s, e) => InvalidateCachesAndRedraw();
                _waveformCollectionRef.CollectionChanged += _waveformCollectionHandler;
            }
            InvalidateCachesAndRedraw();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;
            PointerEntered += OnPointerEntered;
            PointerExited += OnPointerExited;

            _loadingTimer.Start();
            _updateTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _loadingTimer.Stop();
            _updateTimer.Stop();

            PointerPressed -= OnPointerPressed;
            PointerMoved -= OnPointerMoved;
            PointerReleased -= OnPointerReleased;
            PointerEntered -= OnPointerEntered;
            PointerExited -= OnPointerExited;

            // Explicit disposal of memory-heavy resources
            _unplayedCache?.Dispose();
            _playedCache?.Dispose();
            _unplayedCache = null;
            _playedCache = null;

            // Unsubscribe timers and property subscriptions to avoid leaks
            try { _loadingTimer.Tick -= LoadingTimer_Tick; } catch { }
            try { _updateTimer.Tick -= UpdateTimer_Tick; } catch { }

            try
            {
                foreach (var d in _propertySubscriptions) d.Dispose();
            }
            catch { }
            _propertySubscriptions.Clear();
        }

        private void InvalidateCachesAndRedraw()
        {
            _unplayedCache?.Dispose();
            _playedCache?.Dispose();
            _unplayedCache = null;
            _playedCache = null;
            InvalidateVisual();
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDragging && Math.Abs(Value - _lastValue) > 0.0001)
            {
                _lastValue = Value;
                UpdatePlayedCache();
                InvalidateVisual();
            }
        }

        private void LoadingTimer_Tick(object? sender, EventArgs e)
        {
            if (IsLoading)
            {
                _loadingAngle = (_loadingAngle + 5) % 360;
                InvalidateVisual();
            }
        }

        #region Pointer Handling
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _isDragging = true;
            e.Pointer.Capture(this);
            UpdateValueFromPointer(e);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            _lastMousePosition = e.GetPosition(this);
            if (_isDragging)
                UpdateValueFromPointer(e);
            else
                InvalidateVisual();
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                UpdateValueFromPointer(e);
                e.Pointer.Capture(null);
                DragCompletedCommand?.Execute(Value);
            }
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e) => _lastMousePosition = e.GetPosition(this);
        private void OnPointerExited(object? sender, PointerEventArgs e) => _lastMousePosition = null;

        private void UpdateValueFromPointer(PointerEventArgs e)
        {
            var point = e.GetPosition(this);
            double width = Bounds.Width - WaveformMargin.Left - WaveformMargin.Right;
            double ratio = Math.Clamp((point.X - WaveformMargin.Left) / Math.Max(1.0, width), 0, 1);
            Value = Minimum + ratio * (Maximum - Minimum);
            _lastValue = Value;
            UpdatePlayedCache();
            InvalidateVisual();
        }
        #endregion

        private void UpdatePlayedCache()
        {
            if (Waveform == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

            int pxW = (int)Math.Max(1, Math.Round(Bounds.Width));
            int pxH = (int)Math.Max(1, Math.Round(Bounds.Height));

            // Only recreate if needed
            if (_unplayedCache != null && _unplayedCache.PixelSize.Width == pxW && _unplayedCache.PixelSize.Height == pxH)
                return;

            double width = Bounds.Width - WaveformMargin.Left - WaveformMargin.Right;
            double height = Bounds.Height - WaveformMargin.Top - WaveformMargin.Bottom;
            double offset = WaveformVerticalOffset + WaveformMargin.Top;
            double availableHeight = Math.Max(0, height - WaveformVerticalOffset);

            if (width <= 0 || availableHeight <= 0) return;

            // Dispose old bitmaps
            _unplayedCache?.Dispose();
            _playedCache?.Dispose();

            var unplayedBrush = UnPlayedColor ?? Brushes.LightGray;
            var playedBrush = PlayedColor ?? Brushes.LightBlue;

            _unplayedCache = new RenderTargetBitmap(new PixelSize(pxW, pxH));
            using (var ctx = _unplayedCache.CreateDrawingContext())
            {
                DrawWaveformSection(ctx, unplayedBrush, 0, 1.0, width, availableHeight, offset);
            }

            _playedCache = new RenderTargetBitmap(new PixelSize(pxW, pxH));
            using (var ctx = _playedCache.CreateDrawingContext())
            {
                DrawWaveformSection(ctx, playedBrush, 0, 1.0, width, availableHeight, offset);
            }
        }

        private readonly Dictionary<Color, IBrush> _brushCache = new();

        internal void DrawWaveformSection(DrawingContext ctx, IBrush brush, double startRatio, double endRatio, double width, double availableHeight, double offset)
        {
            if (Waveform == null || Waveform.Count == 0) return;

            int visualCount = VisualBarCount > 0 ? VisualBarCount : Waveform.Count;
            double barWidthRaw = width / visualCount;
            double hGap = BarGap;
            double vBlockH = BlockHeight;
            double vGap = VerticalGap;

            double marginBottom = WaveformMargin.Bottom;
            double marginTop = WaveformMargin.Top + offset;
            double canvasHeight = Bounds.Height;
            double centerY = (canvasHeight - marginBottom + marginTop) / 2.0;
            double bottom = canvasHeight - marginBottom;

            int startIndex = (int)(startRatio * visualCount);
            int endIndex = (int)(endRatio * visualCount);

            IBrush drawBrush = brush;
            bool useGradient = BarGradient != null && brush == PlayedColor;

            for (int i = startIndex; i < endIndex; i++)
            {
                float val = GetWaveformValue(i, visualCount);

                double barHeightTotal = Math.Clamp(val * availableHeight, 0, availableHeight);
                if (barHeightTotal < 1.5) barHeightTotal = 1.5;

                var (drawX, w) = CalculateBarX(i, visualCount, barWidthRaw, hGap, width);
                if (w <= 0) continue;

                if (vBlockH > 0)
                {
                    if (barHeightTotal < vBlockH)
                    {
                        double lineY = IsSymmetric ? centerY - 0.75 : bottom - 1.5;
                        ctx.FillRectangle(drawBrush, new Rect(drawX, lineY, w, 1.5));
                        continue;
                    }

                    double startY = IsSymmetric ? centerY + (barHeightTotal / 2.0) : bottom;
                    double currentY = startY;
                    double limitY = IsSymmetric ? centerY - (barHeightTotal / 2.0) : offset;

                    while (currentY > limitY + 0.1 && (startY - currentY + vBlockH) <= barHeightTotal)
                    {
                        double blockTop = Math.Max(limitY, currentY - vBlockH);
                        double actualBlockH = currentY - blockTop;

                        if (useGradient)
                        {
                            double normY = 1.0 - (blockTop - offset) / availableHeight;
                            drawBrush = GetGradientColor(normY);
                        }

                        ctx.FillRectangle(drawBrush, new Rect(drawX, blockTop, w, actualBlockH));

                        if (ShowReflection && !IsSymmetric)
                        {
                            double reflOpacity = 0.3 * (1.0 - (bottom - currentY) / availableHeight);
                            ctx.FillRectangle(drawBrush, new Rect(drawX, bottom + (bottom - blockTop), w, actualBlockH), (float)reflOpacity);
                        }
                        currentY -= (vBlockH + vGap);
                    }
                }
                else
                {
                    double y = IsSymmetric ? centerY - (barHeightTotal / 2.0) : bottom - barHeightTotal;
                    if (useGradient)
                    {
                        ctx.FillRectangle(BarGradient!, new Rect(drawX, y, w, barHeightTotal));
                    }
                    else
                    {
                        ctx.FillRectangle(drawBrush, new Rect(drawX, y, w, barHeightTotal));
                    }
                }
            }
        }

        private float GetWaveformValue(int i, int visualCount)
        {
            if (Waveform == null || Waveform.Count == 0) return 0;
            if (VisualBarCount > 0)
            {
                double dataPerBar = (double)Waveform.Count / visualCount;
                if (dataPerBar >= 1.0)
                {
                    float val = 0;
                    int dataStart = (int)(i * dataPerBar);
                    int dataEnd = (int)((i + 1) * dataPerBar);
                    for (int d = dataStart; d < dataEnd && d < Waveform.Count; d++)
                        if (Waveform[d] > val) val = Waveform[d];
                    return val;
                }
                else
                {
                    int index = (int)(i * dataPerBar);
                    return index < Waveform.Count ? Waveform[index] : 0;
                }
            }
            return i < Waveform.Count ? Waveform[i] : 0;
        }

        private (double drawX, double w) CalculateBarX(int i, int visualCount, double barWidthRaw, double hGap, double width)
        {
            double left = WaveformMargin.Left;
            double x1 = left + i * barWidthRaw;
            double x2 = (i == visualCount - 1) ? left + width : left + (i + 1) * barWidthRaw;

            // Round slot boundaries to integer pixels
            int p1 = (int)Math.Round(x1, MidpointRounding.AwayFromZero);
            int p2 = (int)Math.Round(x2, MidpointRounding.AwayFromZero);

            // Apply horizontal gap symmetrically
            int drawX = p1 + (int)Math.Floor(hGap / 2.0);
            int drawEnd = p2 - (int)Math.Ceiling(hGap / 2.0);

            // Ensure we never collapse to 0 width if data exists and slot > 0
            if (drawEnd <= drawX && p2 > p1) drawEnd = drawX + 1;

            return (drawX, Math.Max(0, drawEnd - drawX));
        }

        private IBrush GetGradientColor(double offset)
        {
            if (BarGradient == null) return Brushes.White;
            var stops = BarGradient.GradientStops.OrderBy(s => s.Offset).ToList();
            if (stops.Count == 0) return Brushes.White;

            Color targetColor;
            if (stops.Count == 1) 
            {
                targetColor = stops[0].Color;
            }
            else 
            {
                var left = stops.LastOrDefault(s => s.Offset <= offset) ?? stops[0];
                var right = stops.FirstOrDefault(s => s.Offset > offset) ?? stops.Last();

                if (left == right) 
                {
                    targetColor = left.Color;
                }
                else 
                {
                    double t = (offset - left.Offset) / (right.Offset - left.Offset);
                    targetColor = Color.FromArgb(
                        (byte)(left.Color.A + t * (right.Color.A - left.Color.A)),
                        (byte)(left.Color.R + t * (right.Color.R - left.Color.R)),
                        (byte)(left.Color.G + t * (right.Color.G - left.Color.G)),
                        (byte)(left.Color.B + t * (right.Color.B - left.Color.B))
                    );
                }
            }

            if (_brushCache.TryGetValue(targetColor, out var cached)) return cached;
            var newBrush = new SolidColorBrush(targetColor).ToImmutable();
            _brushCache[targetColor] = newBrush;
            return newBrush;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double width = Bounds.Width;
            double height = Bounds.Height;

            if (_unplayedCache == null) UpdatePlayedCache();

            if (_unplayedCache != null)
            {
                context.DrawImage(_unplayedCache, new Rect(0, 0, width, height));

                if (_playedCache != null)
                {
                    double progress = (Value - Minimum) / Math.Max(1.0, (Maximum - Minimum));
                    double clipX = WaveformMargin.Left + progress * (width - WaveformMargin.Left - WaveformMargin.Right);

                    using (context.PushClip(new Rect(0, 0, clipX, height)))
                    {
                        context.DrawImage(_playedCache, new Rect(0, 0, width, height));
                    }
                }
            }

            // Indicator Line
            double progressRatio = (Value - Minimum) / Math.Max(1.0, (Maximum - Minimum));
            double indicatorX = Math.Round(WaveformMargin.Left + progressRatio * (width - WaveformMargin.Left - WaveformMargin.Right));
            context.FillRectangle(IndicatorColor ?? Brushes.White, new Rect(indicatorX - 1, 0, 2, height));

            // Triangle
            DrawTriangle(context, indicatorX, height);

            // Hover Tooltip
            if (_lastMousePosition.HasValue) DrawTooltip(context, width);

            if (IsLoading) DrawLoadingIndicator(context, width, height);
        }

        private void DrawTriangle(DrawingContext context, double x, double height)
        {
            var triangle = new StreamGeometry();
            using (var ctx = triangle.Open())
            {
                double halfWidth = TriangleWidth / 2;
                if (IsTriangleUpwards)
                {
                    double baseY = height - TriangleOffset;
                    ctx.BeginFigure(new Point(x, baseY - TriangleHeight), true);
                    ctx.LineTo(new Point(x - halfWidth, baseY));
                    ctx.LineTo(new Point(x + halfWidth, baseY));
                }
                else
                {
                    double baseY = TriangleOffset;
                    ctx.BeginFigure(new Point(x, baseY + TriangleHeight), true);
                    ctx.LineTo(new Point(x - halfWidth, baseY));
                    ctx.LineTo(new Point(x + halfWidth, baseY));
                }
                ctx.EndFigure(true);
            }
            context.DrawGeometry(TriangleColor ?? Brushes.White, null, triangle);
        }

        private void DrawTooltip(DrawingContext context, double width)
        {
            if (!_lastMousePosition.HasValue) return;
            double ratio = Math.Clamp((_lastMousePosition.Value.X - WaveformMargin.Left) / Math.Max(1.0, width - WaveformMargin.Left - WaveformMargin.Right), 0, 1);
            TimeSpan time = TimeSpan.FromSeconds(Minimum + ratio * (Maximum - Minimum));
            
            var text = new FormattedText(time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, TextSize, TextForegroundColor ?? Brushes.White);

            context.DrawText(text, new Point(_lastMousePosition.Value.X, Math.Max(0, _lastMousePosition.Value.Y - TextSize - 5)));
        }

        private void DrawLoadingIndicator(DrawingContext context, double width, double height)
        {
            double size = LoadingIndicatorSize;
            IBrush brush = LoadingIndicatorColor ?? Brushes.White;
            Color color = brush is SolidColorBrush scb ? scb.Color : Colors.White;

            double centerX = width / 2;
            double centerY = height / 2;
            double radius = size / 2;
            int segments = 12;

            for (int i = 0; i < segments; i++)
            {
                double angle = (_loadingAngle + i * (360.0 / segments)) % 360;
                double rad = angle * Math.PI / 180.0;
                var p1 = new Point(centerX + Math.Cos(rad) * (radius * 0.5), centerY + Math.Sin(rad) * (radius * 0.5));
                var p2 = new Point(centerX + Math.Cos(rad) * radius, centerY + Math.Sin(rad) * radius);
                context.DrawLine(new Pen(new SolidColorBrush(color) { Opacity = (i + 1) / (double)segments }, 3), p1, p2);
            }
        }
    }
}