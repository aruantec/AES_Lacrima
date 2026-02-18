using System.Buffers;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using log4net;
using SkiaSharp;

namespace AES_Controls.Composition
{
    /// <summary>
    /// A highly custom items control that renders a 3D-styled carousel of
    /// images using the Composition API and Skia for rendering. The control
    /// supports virtualization, drag/drop reordering, touch/pointer interaction
    /// and exposes properties to control spacing, scale and selection.
    /// </summary>
    public class CompositionCarouselControl : ItemsControl
    {
        #region Private Fields

        private static readonly ILog Log = LogManager.GetLogger(typeof(CompositionCarouselControl));

        private CompositionCustomVisual? _visual;
        private List<SKImage> _images = new();
        private Dictionary<object, SKImage> _imageCache = new();
        private SKImage? _sharedPlaceholder;
        private PropertyInfo? _propBitmap;
        private PropertyInfo? _propFile;
        private string? _cachedBitmapName;
        private string? _cachedFileName;
        private HashSet<INotifyPropertyChanged> _subscribedItems = new();
        private readonly LinkedList<object> _imageCacheLru = new();
        private readonly Dictionary<object, LinkedListNode<object>> _imageCacheNodes = new();
        private int _maxImageCacheEntries = 200;

        private int _lastVirtualizationIndex = -1;

        private Point _startPoint;
        private Point _prevPoint;
        private ulong _prevTime;
        private double _velocity;
        private bool _isPressed;
        private double _pressIndex;
        private double _uiCurrentIndex;
        private double _uiVelocity;
        private long _uiLastTicks;
        private CancellationTokenSource? _loadCts;
        private DispatcherTimer? _virtualizeDebounceTimer;
        private DispatcherTimer? _uiSyncTimer;
        private IEnumerable? _subscribedItemsSource;
        private bool _isInternalMove;

        private DispatcherTimer? _longPressTimer;
        private bool _isDragging;
        private bool _isSliderPressed;
        private int _lastHoveredItem = -1, _lastHoveredButton = 0;
        private int _lastPressedItem = -1, _lastPressedButton = 0;
        private int _draggingIndex = -1;
        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVelocity;

        // Projection cache to reduce heavy math during hit-testing and pointer moves
        private Dictionary<int, (Point p1, Point p2, Point p3, Point p4, float scale)> _projPolyCache = new();
        private Vector2 _projCacheSize = new Vector2(0,0);
        private double _projCacheForIndex = double.NaN;
        private int _projCacheCenterIdx = -1;

        #endregion

        #region Static Fields (Styled Properties)

        public static readonly StyledProperty<double> SelectedIndexProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SelectedIndex));

        public static readonly StyledProperty<double> ItemSpacingProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemSpacing), 0.93);

        public static readonly StyledProperty<double> ItemScaleProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemScale), 1.88);

        public static readonly StyledProperty<double> VerticalOffsetProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(VerticalOffset), -95.0);

        public static readonly StyledProperty<double> SliderVerticalOffsetProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SliderVerticalOffset), 134.0);

        public static readonly StyledProperty<double> SliderTrackHeightProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SliderTrackHeight), 17.0);

        public static readonly StyledProperty<double> SideTranslationProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SideTranslation), 73.0);

        public static readonly StyledProperty<double> StackSpacingProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(StackSpacing), 39.0);

        public static readonly StyledProperty<double> ItemWidthProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemWidth), 240.0);

        public static readonly StyledProperty<double> ItemHeightProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemHeight), 200.0);

        public static readonly StyledProperty<ICommand?> ItemSelectedCommandProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, ICommand?>(nameof(ItemSelectedCommand));

        public static readonly StyledProperty<ICommand?> ItemDoubleClickedCommandProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, ICommand?>(nameof(ItemDoubleClickedCommand));

        public static readonly StyledProperty<string?> ImageFileNamePropertyProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, string?>(nameof(ImageFileNameProperty));

        public static readonly StyledProperty<string?> ImageBitmapPropertyProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, string?>(nameof(ImageBitmapProperty));

        public static readonly StyledProperty<double> GlobalOpacityProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(GlobalOpacity), 1.0);

        public static readonly StyledProperty<int> ImageCacheSizeProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, int>(nameof(ImageCacheSize), 200);

        public static readonly StyledProperty<int> PointedItemIndexProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, int>(nameof(PointedItemIndex), -1);

        public static readonly StyledProperty<bool> UseFullCoverSizeProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, bool>(nameof(UseFullCoverSize), false);

        #endregion

        #region Public Properties

        /// <summary>
        /// The currently selected index in the carousel. This is a styled
        /// property and can be data-bound to view models.
        /// </summary>
        public double SelectedIndex
        {
            get => GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        /// <summary>
        /// Multiplier that controls horizontal spacing between items.
        /// </summary>
        public double ItemSpacing
        {
            get => GetValue(ItemSpacingProperty);
            set => SetValue(ItemSpacingProperty, value);
        }

        /// <summary>
        /// Global scale applied to items. Useful to zoom the entire carousel.
        /// </summary>
        public double ItemScale
        {
            get => GetValue(ItemScaleProperty);
            set => SetValue(ItemScaleProperty, value);
        }

        /// <summary>
        /// Vertical offset applied to the carousel's visual centre.
        /// </summary>
        public double VerticalOffset
        {
            get => GetValue(VerticalOffsetProperty);
            set => SetValue(VerticalOffsetProperty, value);
        }

        /// <summary>
        /// Vertical offset for the slider control rendered below the carousel.
        /// </summary>
        public double SliderVerticalOffset
        {
            get => GetValue(SliderVerticalOffsetProperty);
            set => SetValue(SliderVerticalOffsetProperty, value);
        }

        /// <summary>
        /// Height of the slider track in device independent pixels.
        /// </summary>
        public double SliderTrackHeight
        {
            get => GetValue(SliderTrackHeightProperty);
            set => SetValue(SliderTrackHeightProperty, value);
        }

        /// <summary>
        /// Amount of horizontal translation applied to side items for perspective.
        /// </summary>
        public double SideTranslation
        {
            get => GetValue(SideTranslationProperty);
            set => SetValue(SideTranslationProperty, value);
        }

        /// <summary>
        /// Spacing used when stacking items visually at the sides of the carousel.
        /// </summary>
        public double StackSpacing
        {
            get => GetValue(StackSpacingProperty);
            set => SetValue(StackSpacingProperty, value);
        }

        /// <summary>
        /// Fixed width for all items in the carousel.
        /// </summary>
        public double ItemWidth
        {
            get => GetValue(ItemWidthProperty);
            set => SetValue(ItemWidthProperty, value);
        }

        /// <summary>
        /// Fixed height for all items in the carousel.
        /// </summary>
        public double ItemHeight
        {
            get => GetValue(ItemHeightProperty);
            set => SetValue(ItemHeightProperty, value);
        }

        /// <summary>
        /// Optional property name on items which contains a file path to load
        /// the image from. When set the control will read this property via
        /// reflection during virtualization.
        /// </summary>
        public string? ImageFileNameProperty
        {
            get => GetValue(ImageFileNamePropertyProperty);
            set => SetValue(ImageFileNamePropertyProperty, value);
        }

        /// <summary>
        /// Optional property name on items which contains an Avalonia Bitmap
        /// that can be converted into a Skia image. The control will attempt
        /// to read this before falling back to <see cref="ImageFileNameProperty"/>.
        /// </summary>
        public string? ImageBitmapProperty
        {
            get => GetValue(ImageBitmapPropertyProperty);
            set => SetValue(ImageBitmapPropertyProperty, value);
        }

        /// <summary>
        /// Global opacity multiplier applied to the entire composition visual.
        /// </summary>
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

        /// <summary>
        /// When true, items will be rendered with their actual source aspect ratio. 
        /// When false (default), all items are forced to a square.
        /// </summary>
        public bool UseFullCoverSize
        {
            get => GetValue(UseFullCoverSizeProperty);
            set => SetValue(UseFullCoverSizeProperty, value);
        }

        private Rect SliderBounds
        {
            get
            {
                double w = Bounds.Width;
                double h = Bounds.Height;
                double sliderW = Math.Min(600, w * 0.8);
                return new Rect((w - sliderW) / 2, h - SliderVerticalOffset, sliderW, 80);
            }
        }

        #endregion

        #region Commands

        /// <summary>
        /// Command executed when an item is selected. The command parameter
        /// will be the selected index (int).
        /// </summary>
        public ICommand? ItemSelectedCommand
        {
            get => GetValue(ItemSelectedCommandProperty);
            set => SetValue(ItemSelectedCommandProperty, value);
        }

        /// <summary>
        /// Command executed when an item is double-clicked. The command
        /// parameter will be the index (int) of the double-clicked item.
        /// </summary>
        public ICommand? ItemDoubleClickedCommand
        {
            get => GetValue(ItemDoubleClickedCommandProperty);
            set => SetValue(ItemDoubleClickedCommandProperty, value);
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="CompositionCarouselControl"/>
        /// and configures timers used for UI synchronization and virtualization.
        /// </summary>
        public CompositionCarouselControl()
        {
            Focusable = true;
            Background = Brushes.Transparent;
            // keep GlobalOpacity in sync with local Opacity initially
            GlobalOpacity = Opacity;
            
            _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _longPressTimer.Tick += LongPressTimer_Tick;

            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _autoScrollTimer.Tick += AutoScrollTimer_Tick;

            _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                long currentTicks = Stopwatch.GetTimestamp();
                if (_uiLastTicks == 0) _uiLastTicks = currentTicks;
                double dt = (double)(currentTicks - _uiLastTicks) / Stopwatch.Frequency;
                _uiLastTicks = currentTicks;

                if (dt > 0.1) dt = 0.1;

                // tuned spring parameters: smoother glide logic
                double uiStiffness = 45.0; 
                double uiDamping = 2.0 * Math.Sqrt(uiStiffness) * 1.15; // slightly overdamped for clean landing

                double distance = SelectedIndex - _uiCurrentIndex;
                double force = distance * uiStiffness;
                _uiVelocity += (force - _uiVelocity * uiDamping) * dt;
                _uiCurrentIndex += _uiVelocity * dt;

                if (Math.Abs(distance) < 0.001 && Math.Abs(_uiVelocity) < 0.01)
                {
                    _uiCurrentIndex = SelectedIndex;
                    _uiVelocity = 0;
                    _uiLastTicks = 0;
                    _uiSyncTimer?.Stop();
                }
            });
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Replace the visual images used by the carousel. The provided list
        /// is sent to the composition visual handler for GPU rendering.
        /// </summary>
        /// <param name="images">Collection of Skia SKImage instances.</param>
        public void SetImages(IEnumerable<SKImage> images)
        {
            _images = images.ToList();
            _visual?.SendHandlerMessage(_images);
        }

        /// <summary>
        /// Update a single image at <paramref name="index"/> with the
        /// supplied SKImage. The visual handler will be notified of the change.
        /// </summary>
        /// <param name="index">Index of the image to replace.</param>
        /// <param name="image">New SKImage instance to display.</param>
        public void SetImage(int index, SKImage image)
        {
            if (index >= 0 && index < _images.Count)
            {
                _images[index] = image;
                _visual?.SendHandlerMessage(new UpdateImageMessage(index, image));
                ClearProjectionCache();
            }
        }

        /// <summary>
        /// Mark the image at the specified index as loading. This will show
        /// a spinner overlay for that item in the visual handler.
        /// </summary>
        /// <param name="index">Index to mark loading.</param>
        /// <param name="isLoading">True to display loading spinner.</param>
        public void SetLoading(int index, bool isLoading)
        {
            _visual?.SendHandlerMessage(new UpdateImageMessage(index, null, isLoading));
        }

        public override void Render(DrawingContext context)
        {
            if (Background != null)
                context.DrawRectangle(Background, null, new Rect(Bounds.Size));
            base.Render(context);
        }

        #endregion

        #region Private Methods

        private SKImage GetPlaceholder()
        {
            if (_sharedPlaceholder == null || _sharedPlaceholder.Width == 0)
                _sharedPlaceholder = GeneratePlaceholder(); 
            return _sharedPlaceholder!;
        }

        // Ensure projection cache is populated for the relevant visible range around currentIndex
        private void EnsureProjectionCache(double currentIndex, Vector2 size)
        {
            // simple invalidation heuristics: if size changed significantly or index moved enough, rebuild
            if (_projCacheCenterIdx >= 0 && _projCacheSize == size && Math.Abs(_projCacheForIndex - currentIndex) < 0.001) return;

            _projPolyCache.Clear();
            _projCacheSize = size;
            _projCacheForIndex = currentIndex;
            _projCacheCenterIdx = (int)Math.Round(currentIndex);

            if (_images.Count == 0 || size.X <= 0 || size.Y <= 0) return;

            float scaleVal = (float)ItemScale;
            float itemWidth = (float)ItemWidth * scaleVal;
            float itemHeight = (float)ItemHeight * scaleVal;
            var center = new Vector2(size.X / 2, (float)(size.Y / 2 + VerticalOffset));
            float spacing = (float)ItemSpacing;

            int visibleRange = (int)Math.Max(10, (size.X / 2.0 / Math.Max(0.1, spacing * scaleVal) - SideTranslation) / Math.Max(1.0, StackSpacing)) + 5;
            int start = Math.Max(0, (int)Math.Floor(currentIndex - visibleRange));
            int end = Math.Min(Math.Max(0, _images.Count - 1), (int)Math.Ceiling(currentIndex + visibleRange));

            for (int i = start; i <= end; i++)
            {
                float w = itemWidth;
                float h = itemHeight;
                if (UseFullCoverSize && i >= 0 && i < _images.Count && _images[i] is SKImage img)
                {
                    float aspect = (float)img.Width / img.Height;
                    if (aspect > 1.1f || aspect < 0.9f)
                        w = h * aspect;
                }

                float diff = (float)(i - currentIndex);
                float absDiff = Math.Abs(diff);

                const float sideRot = 0.95f;
                float sideTrans = (float)SideTranslation;
                float stackSpace = (float)StackSpacing;

                float transitionEase = (float)Math.Tanh(diff * 2.2f);
                float rotationY = -transitionEase * sideRot;
                float stackFactor = Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f) * stackSpace;
                float translationXBeforeRatio = (transitionEase * sideTrans + stackFactor) * spacing * scaleVal;

                float widthRatio = w / (itemWidth > 0 ? itemWidth : 1f);
                float translationZ = (float)(-Math.Pow(absDiff, 0.8f) * 220f * spacing * scaleVal);

                float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
                float itemPerspectiveScale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));

                var matrix = Matrix4x4.CreateTranslation(new Vector3(translationXBeforeRatio * widthRatio, 0, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(itemPerspectiveScale);

                Vector2 TransformProj(Vector3 v) { var vt = Vector3.Transform(v, matrix); float s = 1000f / (1000f - vt.Z); return new Vector2(center.X + vt.X * s, center.Y + vt.Y * s); }

                var p1 = TransformProj(new Vector3(-w/2, -h/2, 0));
                var p2 = TransformProj(new Vector3(w/2, -h/2, 0));
                var p3 = TransformProj(new Vector3(w/2, h/2, 0));
                var p4 = TransformProj(new Vector3(-w/2, h/2, 0));
                float scale = 1000f / (1000f - translationZ);

                _projPolyCache[i] = (p1.ToPoint(), p2.ToPoint(), p3.ToPoint(), p4.ToPoint(), scale);
            }
        }

        private void ClearProjectionCache()
        {
            _projPolyCache.Clear();
            _projCacheForIndex = double.NaN;
            _projCacheCenterIdx = -1;
            _projCacheSize = new Vector2(0,0);
        }

        // Helper for polygon hit testing
        private static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        private static bool PointInQuad(Point p, Point p1, Point p2, Point p3, Point p4)
        {
            double c1 = Cross(p1, p2, p);
            double c2 = Cross(p2, p3, p);
            double c3 = Cross(p3, p4, p);
            double c4 = Cross(p4, p1, p);
            return (c1 >= 0 && c2 >= 0 && c3 >= 0 && c4 >= 0) || (c1 <= 0 && c2 <= 0 && c3 <= 0 && c4 <= 0);
        }

        // Determine which logical slot the pointer is over using the UI-smoothed index and X position mapping.
        // Falls back to polygon containment checks for edge cases.
        private int IndexAtPoint(Point point)
        {
            if (_images.Count == 0) return -1;
            var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);

            // Primary approach: Use robust hit testing to identify an actual item being clicked
            int hit = HitTest(point, size, _uiCurrentIndex);
            if (hit != -1) return hit;

            // Fallback: map pointer X to a slot using linear mapping for "empty space" clicks
            double centerX = size.X / 2.0;
            double itemWidth = ItemWidth * ItemScale * ItemSpacing;
            double relativeX = point.X - centerX;
            double targetFloat = _uiCurrentIndex + (relativeX / (itemWidth * 1.5));
            return (int)Math.Clamp(Math.Round(targetFloat), 0, Math.Max(0, _images.Count - 1));
        }

        private void ClearResources()
        {
            var oldCache = _imageCache.Values.ToList();
            var oldPlaceholder = _sharedPlaceholder;
            
            _imageCache.Clear();
            _imageCacheNodes.Clear();
            _imageCacheLru.Clear();
            _sharedPlaceholder = null;
            _images.Clear();

            foreach (var item in _subscribedItems) item.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Clear();
            
            // Notify visual handler of empty list first so it stops rendering old items

            _visual?.SendHandlerMessage(new List<SKImage>());

            // Then schedule disposal of native resources
            foreach (var img in oldCache) 
            {
                var capturedImg = img;
                _visual?.SendHandlerMessage(new DisposeImageMessage(capturedImg));
            }
            if (oldPlaceholder != null)
            {
                _visual?.SendHandlerMessage(new DisposeImageMessage(oldPlaceholder));
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during detach", ex); }
            ClearResources();
            ClearProjectionCache();
        }
        private void UpdateVirtualization()
        {
            int centerIdx = (int)Math.Round(SelectedIndex);
            if (centerIdx == _lastVirtualizationIndex) return;
            _lastVirtualizationIndex = centerIdx;

            if (_virtualizeDebounceTimer == null)
            {
                _virtualizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
                _virtualizeDebounceTimer.Tick += (s, e) =>
                {
                    _virtualizeDebounceTimer.Stop();
                    try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during virtualization debounce", ex); }
                    _loadCts = new CancellationTokenSource();
                    _ = VirtualizeAsync(_lastVirtualizationIndex, _loadCts.Token);
                };
            }
            _virtualizeDebounceTimer.Stop();
            _virtualizeDebounceTimer.Start();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SelectedIndexProperty)
            {
                _visual?.SendHandlerMessage(change.GetNewValue<double>());
                UpdateVirtualization();
                ClearProjectionCache();
                if (_uiSyncTimer != null && !_uiSyncTimer.IsEnabled) _uiSyncTimer.Start();
            }
            else if (change.Property == ItemSpacingProperty)
                _visual?.SendHandlerMessage(new SpacingMessage(change.GetNewValue<double>()));
            else if (change.Property == ItemScaleProperty)
                _visual?.SendHandlerMessage(new ScaleMessage(change.GetNewValue<double>()));
            else if (change.Property == ItemWidthProperty)
                _visual?.SendHandlerMessage(new ItemWidthMessage(change.GetNewValue<double>()));
            else if (change.Property == ItemHeightProperty)
                _visual?.SendHandlerMessage(new ItemHeightMessage(change.GetNewValue<double>()));
            else if (change.Property == VerticalOffsetProperty)
                _visual?.SendHandlerMessage(new VerticalOffsetMessage(change.GetNewValue<double>()));
            else if (change.Property == SliderVerticalOffsetProperty)
                _visual?.SendHandlerMessage(new SliderVerticalOffsetMessage(change.GetNewValue<double>()));
            else if (change.Property == SliderTrackHeightProperty)
                _visual?.SendHandlerMessage(new SliderTrackHeightMessage(change.GetNewValue<double>()));
            else if (change.Property == SideTranslationProperty)
                _visual?.SendHandlerMessage(new SideTranslationMessage(change.GetNewValue<double>()));
            else if (change.Property == StackSpacingProperty)
                _visual?.SendHandlerMessage(new StackSpacingMessage(change.GetNewValue<double>()));
            else if (change.Property == BackgroundProperty)
                _visual?.SendHandlerMessage(new BackgroundMessage(GetSkColor(change.GetNewValue<IBrush>())));
            else if (change.Property == OpacityProperty)
            {
                // Forward opacity changes to visuals that support global opacity messaging
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
            }
            else if (change.Property == GlobalOpacityProperty)
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
            else if (change.Property == UseFullCoverSizeProperty)
            {
                _visual?.SendHandlerMessage(new UseFullCoverSizeMessage(change.GetNewValue<bool>()));
                ClearProjectionCache();
            }
            else if (change.Property == ImageCacheSizeProperty)
                UpdateImageCacheSize(change.GetNewValue<int>());
            else if (change.Property == ItemsSourceProperty || 
                     change.Property == ImageFileNamePropertyProperty || 
                     change.Property == ImageBitmapPropertyProperty)
                UpdateItems();
        }

        private void UpdateItems()
        {
            if (_subscribedItemsSource != ItemsSource)
            {
                if (_subscribedItemsSource is INotifyCollectionChanged oldIncc)
                    oldIncc.CollectionChanged -= ItemsSource_CollectionChanged;
                
                foreach (var item in _subscribedItems) item.PropertyChanged -= Item_PropertyChanged;
                _subscribedItems.Clear();

                _subscribedItemsSource = ItemsSource;
                if (_subscribedItemsSource is INotifyCollectionChanged newIncc)
                    newIncc.CollectionChanged += ItemsSource_CollectionChanged;
            }

            _lastVirtualizationIndex = -1; 
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch (Exception ex) { Log.Warn("Error canceling load during items update", ex); }
            _loadCts = new CancellationTokenSource();

            if (ItemsSource == null)
            {
                ClearResources();
                _visual?.SendHandlerMessage(new List<SKImage>());
                return;
            }

            var items = ItemsSource?.Cast<object>().ToList() ?? new List<object>();
            
            // Re-sync _images list and reuse cached images if available
            _images.Clear();
            var placeholder = GetPlaceholder();
            for (int i = 0; i < items.Count; i++) 
            {
                if (_imageCache.TryGetValue(items[i], out var cached))
                {
                    _images.Add(cached);
                    TouchCacheItem(items[i]);
                }
                else
                    _images.Add(placeholder);

                if (items[i] is INotifyPropertyChanged inpc)
                {
                    if (_subscribedItems.Add(inpc)) inpc.PropertyChanged += Item_PropertyChanged;
                }
            }
            
            _propBitmap = null; _propFile = null; _cachedBitmapName = null; _cachedFileName = null;

            _visual?.SendHandlerMessage(_images.ToArray());

            // Initial CoverFound sync
            var coverFoundSet = new HashSet<int>();
            for (int i = 0; i < items.Count; i++)
            {
                var prop = items[i]?.GetType().GetProperty("CoverFound");
                if (prop != null && prop.GetValue(items[i]) is bool found && found)
                    coverFoundSet.Add(i);
            }
            _visual?.SendHandlerMessage(new ResetCoverFoundMessage(coverFoundSet));

            ClearProjectionCache();
            UpdateVirtualization();
        }

        private void UpdateImageCacheSize(int cacheSize)
        {
            _maxImageCacheEntries = Math.Max(1, cacheSize);
            TrimImageCache(new Dictionary<object, int>());
        }


        private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isInternalMove) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_visual == null) return;

                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Move:
                        if (e.OldStartingIndex != e.NewStartingIndex && 
                            e.OldStartingIndex >= 0 && e.OldStartingIndex < _images.Count &&
                            e.NewStartingIndex >= 0 && e.NewStartingIndex < _images.Count)
                        {
                            var img = _images[e.OldStartingIndex];
                            _images.RemoveAt(e.OldStartingIndex);
                            _images.Insert(e.NewStartingIndex, img);
                            _visual.SendHandlerMessage(_images);

                            // Re-sync CoverFound on move
                            var list = ItemsSource as IList ?? ItemsSource?.Cast<object>().ToList();
                            if (list != null)
                            {
                                var set = new HashSet<int>();
                                for (int i = 0; i < list.Count; i++) {
                                    var p = list[i]?.GetType().GetProperty("CoverFound");
                                    if (p != null && p.GetValue(list[i]) is bool found && found) set.Add(i);
                                }
                                _visual.SendHandlerMessage(new ResetCoverFoundMessage(set));
                            }
                        }
                        break;
                    case NotifyCollectionChangedAction.Add:
                    case NotifyCollectionChangedAction.Remove:
                    case NotifyCollectionChangedAction.Replace:
                    case NotifyCollectionChangedAction.Reset:
                    default:
                        UpdateItems();
                        break;
                }
            });
        }

        private async Task VirtualizeAsync(int centerIdx, CancellationToken ct)
        {
            if (ItemsSource == null) return;

            // Capture property names on UI thread before we switch to background threads
            string? bitmapProp = ImageBitmapProperty;
            string? fileProp = ImageFileNameProperty;

            var list = ItemsSource as IList ?? ItemsSource.Cast<object>().ToList();
            int totalCount = list.Count;
            if (totalCount == 0) return;

            // Smaller window for fluidity, faster response
            const int loadWindow = 12;

            // Build lookup dictionary for O(1) index check
            var itemToIndex = new Dictionary<object, int>();
            for (int k = 0; k < totalCount; k++) 
            {
                var val = list[k];
                if (val != null) itemToIndex[val] = k;
            }

            // Prune distant items
            foreach (var key in _imageCache.Keys.ToList())
            {
                if (ct.IsCancellationRequested) return;
                bool exists = itemToIndex.TryGetValue(key, out int idx);
                if (!exists)
                {
                    if (_imageCache.Remove(key, out var img))
                    {
                        RemoveCacheNode(key);
                        if (exists && idx >= 0 && idx < _images.Count)
                        {
                            var placeholder = GetPlaceholder();
                            _images[idx] = placeholder;
                            _visual?.SendHandlerMessage(new UpdateImageMessage(idx, placeholder));
                        }

                        var imgToDispose = img;
                        if (_visual != null)
                            _visual.SendHandlerMessage(new DisposeImageMessage(imgToDispose));
                        else
                            imgToDispose.Dispose();
                    }
                }
            }

            // Load missing items
            int start = Math.Max(0, centerIdx - loadWindow);
            int end = Math.Min(totalCount - 1, centerIdx + loadWindow);
            var indicesToLoad = Enumerable.Range(start, end - start + 1)
                                          .OrderBy(i => Math.Abs(i - centerIdx))
                                          .ToList();

            var cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
            try { if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath); } catch (Exception ex) { Log.Warn("Could not create ImageCache directory", ex); }

            foreach (int i in indicesToLoad)
            {
                if (ct.IsCancellationRequested) return;
                var item = list[i];
                if (item == null || _imageCache.ContainsKey(item)) continue;

                SetLoading(i, true);

                Bitmap? bitmapValue = null;
                string? fileName = null;
                try
                {
                    var type = item.GetType();
                    if (!string.IsNullOrEmpty(bitmapProp))
                    {
                        if (_propBitmap == null || _cachedBitmapName != bitmapProp)
                        {
                            _propBitmap = type.GetProperty(bitmapProp);
                            _cachedBitmapName = bitmapProp;
                        }
                        bitmapValue = _propBitmap?.GetValue(item) as Bitmap;
                    }
                    if (bitmapValue == null && !string.IsNullOrEmpty(fileProp))
                    {
                        if (_propFile == null || _cachedFileName != fileProp)
                        {
                            _propFile = type.GetProperty(fileProp);
                            _cachedFileName = fileProp;
                        }
                        fileName = _propFile?.GetValue(item) as string;
                    }
                }
                catch (Exception ex) { Log.Warn($"Failed to read image properties for item at index {i}", ex); }

                SKImage? realImage = null;
                try
                {
                    if (ct.IsCancellationRequested) return;
                    realImage = await LoadImageAsync(bitmapValue, fileName, cachePath, ct);
                }
                catch (Exception ex) { Log.Warn($"Failed to load image for item at index {i}", ex); }

                if (ct.IsCancellationRequested)
                {
                    realImage?.Dispose();
                    return;
                }

                if (realImage != null && realImage.Width > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            realImage.Dispose();
                            return;
                        }

                        // Dispose old image if it was replaced (e.g. by another load or property change)
                        if (_imageCache.TryGetValue(item, out var oldImg))
                        {
                            if (_visual != null) _visual.SendHandlerMessage(new DisposeImageMessage(oldImg));
                            else oldImg.Dispose();
                        }

                        _imageCache[item] = realImage;
                        TouchCacheItem(item);
                        if (i < _images.Count) _images[i] = realImage;
                        SetImage(i, realImage);
                    });
                }
                else
                {
                    SetLoading(i, false);
                }
            }

            TrimImageCache(itemToIndex);
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(async void () =>
            {
                string? bitmapProp = ImageBitmapProperty;
                string? fileProp = ImageFileNameProperty;

                if (sender == null || (e.PropertyName != bitmapProp && e.PropertyName != fileProp && e.PropertyName != "CoverFound")) return;

                var list = ItemsSource as IList ?? ItemsSource?.Cast<object>().ToList();
                int idx = list?.IndexOf(sender) ?? -1;
                if (idx == -1) return;

                if (e.PropertyName == "CoverFound")
                {
                    var prop = sender.GetType().GetProperty("CoverFound");
                    if (prop != null && prop.GetValue(sender) is bool found)
                        _visual?.SendHandlerMessage(new UpdateCoverFoundMessage(idx, found));
                    return;
                }

                Bitmap? bitmapValue = null;
                string? fileName = null;
                try
                {
                    if (!string.IsNullOrEmpty(bitmapProp))
                        bitmapValue = sender.GetType().GetProperty(bitmapProp)?.GetValue(sender) as Bitmap;

                    if (bitmapValue == null && !string.IsNullOrEmpty(fileProp))
                        fileName = sender.GetType().GetProperty(fileProp)?.GetValue(sender) as string;
                }
                catch (Exception ex) { Log.Warn("Failed to read image properties in PropertyChanged", ex); }

                var cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
                SKImage? realImage = null;
                try
                {
                    realImage = await LoadImageAsync(bitmapValue, fileName, cachePath, CancellationToken.None);
                }
                catch (Exception ex) { Log.Warn("Failed to load image in PropertyChanged", ex); }

                if (realImage != null)
                {
                    if (_imageCache.TryGetValue(sender, out var old))
                        _visual?.SendHandlerMessage(new DisposeImageMessage(old));
                    _imageCache[sender] = realImage;
                    TouchCacheItem(sender);
                    SetImage(idx, realImage);
                }
            });
        }

        private async Task<SKImage?> ToSkImageAsync(Bitmap bitmap)
        {
            if (bitmap.Format == null || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) return null;

            int w = bitmap.PixelSize.Width;
            int h = bitmap.PixelSize.Height;
            int stride = w * 4;
            int bufferSize = h * stride;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            bool success = false;
            try
            {
                // Copy pixels must happen on UI thread for some Avalonia bitmap implementations
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        unsafe
                        {
                            fixed (byte* p = buffer)
                            {
                                bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), (IntPtr)p, bufferSize, stride);
                            }
                        }
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ToSKImage: CopyPixels failed: {ex.Message}");
                    }
                });

                if (!success) return null;

                return await Task.Run(() =>
                {
                    try
                    {
                        int targetW = 512;
                        int targetH = 512;
                        if (w > h) targetH = (int)(512.0 * h / w);
                        else targetW = (int)(512.0 * w / h);

                        using var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                        unsafe
                        {
                            bool needSwap = OperatingSystem.IsMacOS();
                            if (!needSwap)
                            {
                                fixed (byte* p = buffer)
                                {
                                    Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
                                }
                            }
                            else
                            {
                                // Swizzle for MacOS
                                for (int i = 0; i < bufferSize; i += 4)
                                {
                                    byte r = buffer[i + 0]; byte g = buffer[i + 1]; byte b = buffer[i + 2]; byte a = buffer[i + 3];
                                    buffer[i + 0] = b; buffer[i + 1] = g; buffer[i + 2] = r; buffer[i + 3] = a;
                                }
                                fixed (byte* p = buffer)
                                {
                                    Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
                                }
                            }
                        }

                        if (skBmp.Width <= 512 && skBmp.Height <= 512)
                            return SKImage.FromBitmap(skBmp);

                        using var resized = skBmp.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.Medium);
                        return resized != null ? SKImage.FromBitmap(resized) : SKImage.FromBitmap(skBmp);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("ToSKImageAsync: Skia conversion failed", ex);
                        return null;
                    }
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<SKImage?> LoadImageAsync(Bitmap? bitmapValue, string? fileName, string cachePath, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return null;
            if (bitmapValue != null)
                return await ToSkImageAsync(bitmapValue);
            if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
                return await Task.Run(() => LoadAndResize(fileName, cachePath), ct);
            return null;
        }

        private void TouchCacheItem(object key)
        {
            if (_imageCacheNodes.TryGetValue(key, out var node))
            {
                _imageCacheLru.Remove(node);
                _imageCacheLru.AddFirst(node);
                return;
            }

            var newNode = _imageCacheLru.AddFirst(key);
            _imageCacheNodes[key] = newNode;
        }

        private void RemoveCacheNode(object key)
        {
            if (_imageCacheNodes.TryGetValue(key, out var node))
            {
                _imageCacheNodes.Remove(key);
                _imageCacheLru.Remove(node);
            }
        }

        private void TrimImageCache(Dictionary<object, int> itemToIndex)
        {
            while (_imageCache.Count > _maxImageCacheEntries && _imageCacheLru.Last != null)
            {
                var key = _imageCacheLru.Last.Value;
                _imageCacheLru.RemoveLast();
                _imageCacheNodes.Remove(key);

                if (_imageCache.Remove(key, out var img))
                {
                    if (itemToIndex.TryGetValue(key, out var idx) && idx >= 0 && idx < _images.Count)
                    {
                        var placeholder = GetPlaceholder();
                        _images[idx] = placeholder;
                        _visual?.SendHandlerMessage(new UpdateImageMessage(idx, placeholder));
                    }

                    if (_visual != null)
                        _visual.SendHandlerMessage(new DisposeImageMessage(img));
                    else
                        img.Dispose();
                }
            }
        }

        private SKImage GeneratePlaceholder()
        {
            using var surface = SKSurface.Create(new SKImageInfo(300, 300));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            return surface.Snapshot();
        }

        private SKImage? LoadAndResize(string file, string cachePath)
        {
            try
            {
                var cachedFile = Path.Combine(cachePath, Path.GetFileName(file));
                if (File.Exists(cachedFile))
                {
                    using var data = SKData.Create(cachedFile);
                    if (data != null) return SKImage.FromEncodedData(data);
                }
                using var codec = SKCodec.Create(file);
                if (codec != null)
                {
                    using var bmp = new SKBitmap(codec.Info);
                    codec.GetPixels(bmp.Info, bmp.GetPixels());

                    int targetW = 512;
                    int targetH = 512;
                    if (bmp.Width > bmp.Height)
                        targetH = (int)(512.0 * bmp.Height / bmp.Width);
                    else
                        targetW = (int)(512.0 * bmp.Width / bmp.Height);

                    using var resized = bmp.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.Medium);
                    if (resized != null)
                    {
                        var img = SKImage.FromBitmap(resized);
                        using (var data = img.Encode(SKEncodedImageFormat.Png, 80))
                        using (var stream = File.Create(cachedFile))
                            data.SaveTo(stream);
                        return img;
                    }
                }

            }
            catch (Exception ex)
            {
                Log.Error($"Error in LoadAndResize for file: {file}", ex);
            }
            return null;
        }

        private SKColor GetSkColor(IBrush? brush)
        {
            if (brush is ISolidColorBrush scb) return new SKColor(scb.Color.R, scb.Color.G, scb.Color.B, scb.Color.A);
            return SKColors.Transparent;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor != null)
            {
                _visual = compositor.CreateCustomVisual(new CompositionCarouselVisualHandler());
                ElementComposition.SetElementChildVisual(this, _visual);
                var logicalSize = new Vector2((float)Bounds.Width, (float)Bounds.Height);
                _visual.Size = logicalSize + new Vector2(0, 1000); // Allow extra space for reflections below
                _visual.SendHandlerMessage(logicalSize);
                if (_images.Any()) _visual.SendHandlerMessage(_images);
                _visual.SendHandlerMessage(SelectedIndex);
                _visual.SendHandlerMessage(new SpacingMessage(ItemSpacing));
                _visual.SendHandlerMessage(new ScaleMessage(ItemScale));
                _visual.SendHandlerMessage(new ItemWidthMessage(ItemWidth));
                _visual.SendHandlerMessage(new ItemHeightMessage(ItemHeight));
                _visual.SendHandlerMessage(new VerticalOffsetMessage(VerticalOffset));
                _visual.SendHandlerMessage(new SliderVerticalOffsetMessage(SliderVerticalOffset));
                _visual.SendHandlerMessage(new SliderTrackHeightMessage(SliderTrackHeight));
                _visual.SendHandlerMessage(new SideTranslationMessage(SideTranslation));
                _visual.SendHandlerMessage(new StackSpacingMessage(StackSpacing));
                _visual.SendHandlerMessage(new BackgroundMessage(GetSkColor(Background)));
                _visual.SendHandlerMessage(new GlobalOpacityMessage(GlobalOpacity));
                _visual.SendHandlerMessage(new UseFullCoverSizeMessage(UseFullCoverSize));
                if (ItemsSource != null) UpdateItems();
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            ClearProjectionCache();
            if (_visual != null)
            {
                var logicalSize = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                _visual.Size = logicalSize + new Vector2(0, 1000); // Allow extra space for reflections below
                _visual.SendHandlerMessage(logicalSize);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double delta = 0;
            if (e.Key == Key.Left) delta = -1;
            else if (e.Key == Key.Right) delta = 1;
            else if (e.Key == Key.Home) { SelectedIndex = 0; delta = 0.0001; } // trigger update
            else if (e.Key == Key.End) { SelectedIndex = Math.Max(0, _images.Count - 1); delta = 0.0001; }

            if (delta != 0 || e.Key == Key.Home || e.Key == Key.End)
            {
                var newIndex = Math.Clamp(Math.Round(SelectedIndex + delta), 0, Math.Max(0, _images.Count - 1));
                if (newIndex != SelectedIndex || e.Key == Key.Home || e.Key == Key.End)
                {
                    SelectedIndex = newIndex;
                    ItemSelectedCommand?.Execute((int)newIndex);
                    e.Handled = true;
                }
            }
        }

        private void LongPressTimer_Tick(object? sender, EventArgs e)
        {
            _longPressTimer?.Stop();
            if (_isPressed && !_isDragging)
            {
                int hit = HitTest(_prevPoint, new Vector2((float)Bounds.Width, (float)Bounds.Height), _uiCurrentIndex);
                if (hit != -1)
                {
                    _isDragging = true;
                    _draggingIndex = hit;
                    _visual?.SendHandlerMessage(new DragStateMessage(_draggingIndex, true));
                    _visual?.SendHandlerMessage(new DragPositionMessage(new Vector2((float)_prevPoint.X, (float)_prevPoint.Y)));
                    _visual?.SendHandlerMessage(new DropTargetMessage(_draggingIndex));
                }
            }
        }

        private void AutoScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDragging) { _autoScrollTimer?.Stop(); return; }
            
            double threshold = 120;
            double mouseX = _prevPoint.X;
            double w = Bounds.Width;
            
            double delta = 0;
            if (mouseX < threshold) delta = -(threshold - mouseX) / threshold;
            else if (mouseX > w - threshold) delta = (mouseX - (w - threshold)) / threshold;
            
            if (Math.Abs(delta) > 0.01)
            {
                _autoScrollVelocity = delta * 0.5;
                SelectedIndex = Math.Clamp(SelectedIndex + _autoScrollVelocity, 0, Math.Max(0, _images.Count - 1));
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var pos = e.GetPosition(this);
            var pointerProps = e.GetCurrentPoint(this).Properties;
            if (pointerProps.IsRightButtonPressed)
            {
                var size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
                PointedItemIndex = HitTest(pos, size, _uiCurrentIndex);
                e.Handled = true;
                return;
            }

            base.OnPointerPressed(e);
            Focus();
            _isPressed = true;
            _startPoint = _prevPoint = pos;
            _prevTime = e.Timestamp;
            _velocity = 0;
            _pressIndex = SelectedIndex;
            e.Pointer.Capture(this);

            if (SliderBounds.Inflate(new Thickness(40, 30)).Contains(pos))
            {
                _isSliderPressed = true;
                _visual?.SendHandlerMessage(new SliderPressedMessage(true));
                UpdateSliderPosition(pos.X);
                return;
            }
            
            _longPressTimer?.Start();

            int hitIndex = IndexAtPoint(pos);
            if (e.ClickCount == 2 && hitIndex != -1) ItemDoubleClickedCommand?.Execute(hitIndex);

            if (hitIndex != -1)
            {
                var list = ItemsSource as IList ?? ItemsSource?.Cast<object>().ToList();
                if (list != null && hitIndex < list.Count)
                {
                    var item = list[hitIndex];
                    var cfProp = item?.GetType().GetProperty("CoverFound");
                    if (cfProp != null && cfProp.GetValue(item) is bool found && found)
                    {
                        int btn = HitTestOverlayButtons(pos, hitIndex, new Vector2((float)Bounds.Width, (float)Bounds.Height));
                        if (btn != 0)
                        {
                            _lastPressedItem = hitIndex; _lastPressedButton = btn;
                            _visual?.SendHandlerMessage(new UpdateOverlayPressedMessage(hitIndex, btn));
                        }
                    }
                }
            }
        }

        private void UpdateSliderPosition(double x)
        {
            var bounds = SliderBounds;
            const double thumbW = 45.0;
            double clickableWidth = Math.Max(1.0, bounds.Width - thumbW);
            double pct = Math.Clamp((x - bounds.Left - thumbW / 2) / clickableWidth, 0, 1);
            SelectedIndex = Math.Clamp(pct * Math.Max(0, _images.Count - 1), 0, Math.Max(0, _images.Count - 1));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            
            if (_isSliderPressed)
            {
                UpdateSliderPosition(point.X);
            }
            else if (_isDragging)
            {
                _visual?.SendHandlerMessage(new DragPositionMessage(new Vector2((float)point.X, (float)point.Y)));

                int targetIndex = IndexAtPoint(point);

                _visual?.SendHandlerMessage(new DropTargetMessage(targetIndex));

                if (!_autoScrollTimer!.IsEnabled) _autoScrollTimer.Start();
            }
            else if (_isPressed)
            {
                if (Point.Distance(_startPoint, point) > 15) _longPressTimer?.Stop();

                long dt = (long)(e.Timestamp - _prevTime);
                if (dt > 0)
                {
                    double dx = point.X - _prevPoint.X;
                    _velocity = -dx / (250.0 * dt);
                }
                var deltaX = point.X - _startPoint.X;
                SelectedIndex = Math.Clamp(_pressIndex - deltaX / 250.0, 0, Math.Max(0, _images.Count - 1));
            }
            _prevPoint = point;
            _prevTime = e.Timestamp;

            if (!_isDragging && !_isSliderPressed)
            {
                int hIdx = IndexAtPoint(point);
                int hBtn = 0;
                if (hIdx != -1)
                {
                    var list = ItemsSource as IList ?? ItemsSource?.Cast<object>().ToList();
                    if (list != null && hIdx < list.Count)
                    {
                        var item = list[hIdx];
                        var cfProp = item?.GetType().GetProperty("CoverFound");
                        if (cfProp != null && cfProp.GetValue(item) is bool found && found)
                            hBtn = HitTestOverlayButtons(point, hIdx, new Vector2((float)Bounds.Width, (float)Bounds.Height));
                    }
                }

                if (hIdx != _lastHoveredItem || hBtn != _lastHoveredButton)
                {
                    _lastHoveredItem = hIdx; _lastHoveredButton = hBtn;
                    _visual?.SendHandlerMessage(new UpdateOverlayHoverMessage(hIdx, hBtn));
                    Cursor = hBtn != 0 ? new Cursor(StandardCursorType.Hand) : null;
                }
            }
        }

        private void MoveItem(int from, int to)
        {
            if (ItemsSource is IList list)
            {
                var item = list[from];
                list.RemoveAt(from);
                list.Insert(to, item);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _longPressTimer?.Stop();
            _autoScrollTimer?.Stop();

            if (_lastPressedItem != -1)
            {
                _visual?.SendHandlerMessage(new UpdateOverlayPressedMessage(-1, 0));
                _lastPressedItem = -1; _lastPressedButton = 0;
            }

            if (_isSliderPressed)
            {
                _isSliderPressed = false;
                _visual?.SendHandlerMessage(new SliderPressedMessage(false));
            }

            if (_isDragging)
            {
                // Determine drop target one last time to be sure
                int targetIndex = IndexAtPoint(e.GetPosition(this));

                if (targetIndex != _draggingIndex)
                {
                    _isInternalMove = true;
                    try
                    {
                        // Manually sync local images state to avoid waiting for INotifyCollectionChanged
                        var img = _images[_draggingIndex];
                        _images.RemoveAt(_draggingIndex);
                        _images.Insert(targetIndex, img);
                        _visual?.SendHandlerMessage(_images);

                        MoveItem(_draggingIndex, targetIndex);
                        SelectedIndex = targetIndex;
                    }
                    finally { _isInternalMove = false; }
                }

                _isDragging = false;
                _visual?.SendHandlerMessage(new DropTargetMessage(targetIndex));
                _visual?.SendHandlerMessage(new DragStateMessage(targetIndex, false));
                _draggingIndex = -1;
                e.Pointer.Capture(null);
                _isPressed = false;
                return;
            }

            base.OnPointerReleased(e);
            if (_isPressed)
            {
                _isPressed = false;
                e.Pointer.Capture(null);
                var point = e.GetPosition(this);
                double projectedIndex = SelectedIndex + _velocity * 100.0;
                SelectedIndex = Math.Clamp(Math.Round(projectedIndex), 0, Math.Max(0, _images.Count - 1));
                if (Point.Distance(_startPoint, point) < 5)
                {
                    int hitIndex = IndexAtPoint(point);
                    if (hitIndex != -1)
                    {
                        var list = ItemsSource as IList ?? ItemsSource?.Cast<object>().ToList();
                        if (list != null && hitIndex < list.Count)
                        {
                            var item = list[hitIndex];
                            var cfProp = item?.GetType().GetProperty("CoverFound");
                            if (cfProp != null && cfProp.GetValue(item) is bool found && found)
                            {
                                int btn = HitTestOverlayButtons(point, hitIndex, new Vector2((float)Bounds.Width, (float)Bounds.Height));
                                if (btn == 1) // OK
                                {
                                    var cmd = item?.GetType().GetProperty("SaveCoverBitmapCommand")?.GetValue(item) as ICommand;
                                    cmd?.Execute(null);
                                    return;
                                }
                                else if (btn == 2) // Cancel
                                {
                                    var cmd = item?.GetType().GetProperty("CancelCommand")?.GetValue(item) as ICommand;
                                    cmd?.Execute(null);
                                    return;
                                }
                            }
                        }

                        SelectedIndex = hitIndex;
                        ItemSelectedCommand?.Execute(hitIndex);
                    }
                }
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var newIndex = Math.Clamp(Math.Round(SelectedIndex - e.Delta.Y), 0, Math.Max(0, _images.Count - 1));
            if (newIndex != SelectedIndex)
            {
                SelectedIndex = newIndex;
                ItemSelectedCommand?.Execute((int)newIndex);
            }
            e.Handled = true;
        }


        private int HitTest(Point point, Vector2 size, double currentIndex)

        {
            if (_images.Count == 0 || size.X <= 0 || size.Y <= 0) return -1;

            // Ensure cache populated for current frame to avoid heavy math in tight loops
            EnsureProjectionCache(currentIndex, size);

            float scaleVal = (float)ItemScale;
            float itemWidth = (float)ItemWidth * scaleVal;
            float itemHeight = (float)ItemHeight * scaleVal;
            var center = new Vector2(size.X / 2, (float)(size.Y / 2 + VerticalOffset));
            float spacing = (float)ItemSpacing;
            int centerIdx = (int)Math.Round(currentIndex);
            
            int visibleRange = (int)Math.Max(10, (size.X / 2.0 / Math.Max(0.1, spacing * scaleVal) - SideTranslation) / Math.Max(1.0, StackSpacing)) + 5;
            int start = Math.Max(0, (int)Math.Floor(currentIndex - visibleRange));
            int end = Math.Min(Math.Max(0, _images.Count - 1), (int)Math.Ceiling(currentIndex + visibleRange));
            
            // Check center item first (it's topmost)
            if (centerIdx >= 0 && centerIdx < _images.Count && IsPointInItem(point, centerIdx, center, itemWidth, itemHeight, spacing, currentIndex, scaleVal)) return centerIdx;

            // Check neighbors from inside out
            for (int offset = 1; offset <= visibleRange; offset++) {
                int r = centerIdx + offset;
                if (r <= end && IsPointInItem(point, r, center, itemWidth, itemHeight, spacing, currentIndex, scaleVal)) return r;
                int l = centerIdx - offset;
                if (l >= start && IsPointInItem(point, l, center, itemWidth, itemHeight, spacing, currentIndex, scaleVal)) return l;
            }
            return -1;
        }

        private bool IsPointInItem(Point p, int i, Vector2 center, float w, float h, float spacing, double currentIndex, float scale)
        {
            if (UseFullCoverSize && i >= 0 && i < _images.Count && _images[i] is SKImage img)
            {
                float aspect = (float)img.Width / img.Height;
                w = h * aspect;
            }

            // Use cached polygon if available
            if (_projPolyCache.TryGetValue(i, out var poly))
            {
                return PointInQuad(p, poly.p1, poly.p2, poly.p3, poly.p4);
            }

            float diff = (float)(i - currentIndex);
            float absDiff = Math.Abs(diff);

            const float sideRot = 0.95f;   
            float sideTrans = (float)SideTranslation;  
            float stackSpace = (float)StackSpacing; 

            float transitionEase = (float)Math.Tanh(diff * 2.2f);
            float rotationY = -transitionEase * sideRot;
            float stackFactor = Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f) * stackSpace;
            float translationXBeforeRatio = (transitionEase * sideTrans + stackFactor) * spacing * scale;
            float widthRatio = w / ((float)ItemWidth * scale);

            float translationZ = (float)(-Math.Pow(absDiff, 0.8f) * 220f * spacing * scale);

            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float itemPerspectiveScale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));

            var matrix = Matrix4x4.CreateTranslation(new Vector3(translationXBeforeRatio * widthRatio, 0, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(itemPerspectiveScale);

            Vector2 Proj(Vector3 v) { var vt = Vector3.Transform(v, matrix); float s = 1000f / (1000f - vt.Z); return new Vector2(center.X + vt.X * s, center.Y + vt.Y * s); }
            var p1 = Proj(new Vector3(-w/2, -h/2, 0)); var p2 = Proj(new Vector3(w/2, -h/2, 0)); var p3 = Proj(new Vector3(w/2, h/2, 0)); var p4 = Proj(new Vector3(-w/2, h/2, 0));

            return PointInQuad(p, p1.ToPoint(), p2.ToPoint(), p3.ToPoint(), p4.ToPoint());
        }

        private int HitTestOverlayButtons(Point p, int i, Vector2 size)
        {
            float scaleVal = (float)ItemScale;
            float spacing = (float)ItemSpacing;
            float w = (float)ItemWidth * scaleVal;
            float h = (float)ItemHeight * scaleVal;

            if (i >= 0 && i < _images.Count && _images[i] is SKImage img)
            {
                float aspect = (float)img.Width / img.Height;
                if (aspect > 1.1f || aspect < 0.9f) w = h * aspect;
            }

            float currentIndex = (float)_uiCurrentIndex;
            var center = new Vector2(size.X / 2, (float)(size.Y / 2 + VerticalOffset));
            float diff = (float)(i - currentIndex);
            float absDiff = Math.Abs(diff);

            const float sideRot = 0.95f;   
            float sideTrans = (float)SideTranslation;  
            float stackSpace = (float)StackSpacing; 

            float transitionEase = (float)Math.Tanh(diff * 2.2f);
            float rotationY = -transitionEase * sideRot;
            float stackFactor = Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f) * stackSpace;
            float translationXBeforeRatio = (transitionEase * sideTrans + stackFactor) * spacing * scaleVal;
            float widthRatio = w / ((float)ItemWidth * scaleVal);
            float translationZ = (float)(-Math.Pow(absDiff, 0.8f) * 220f * spacing * scaleVal);
            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float itemPerspectiveScale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));

            var matrix = Matrix4x4.CreateTranslation(new Vector3(translationXBeforeRatio * widthRatio, 0, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(itemPerspectiveScale);

            float btnW = w * 0.4f;
            float btnH = h * 0.15f;
            float btnGap = w * 0.05f;
            float btnY = h / 2 - btnH - 12;

            if (PointInProjectedRect(p, SKRect.Create(-btnW - btnGap / 2, btnY, btnW, btnH), matrix, center)) return 1;
            if (PointInProjectedRect(p, SKRect.Create(btnGap / 2, btnY, btnW, btnH), matrix, center)) return 2;
            return 0;
        }

        private bool PointInProjectedRect(Point p, SKRect rect, Matrix4x4 matrix, Vector2 center)
        {
            Vector2 Proj(Vector3 v) { var vt = Vector3.Transform(v, matrix); float s = 1000f / (1000f - vt.Z); return new Vector2(center.X + vt.X * s, center.Y + vt.Y * s); }
            var p1 = Proj(new Vector3(rect.Left, rect.Top, 0));
            var p2 = Proj(new Vector3(rect.Right, rect.Top, 0));
            var p3 = Proj(new Vector3(rect.Right, rect.Bottom, 0));
            var p4 = Proj(new Vector3(rect.Left, rect.Bottom, 0));
            return PointInQuad(p, p1.ToPoint(), p2.ToPoint(), p3.ToPoint(), p4.ToPoint());
        }

        #endregion
    }
}


