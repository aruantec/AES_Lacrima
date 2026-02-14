using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using SkiaSharp;
using System.Collections;
using System.Collections.Specialized;
using System.Numerics;

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

        private CompositionCustomVisual? _visual;
        private List<SKImage> _images = new();
        private Dictionary<object, SKImage> _imageCache = new();
        private SKImage? _sharedPlaceholder;
        private System.Reflection.PropertyInfo? _propBitmap;
        private System.Reflection.PropertyInfo? _propFile;
        private string? _cachedBitmapName;
        private string? _cachedFileName;
        private HashSet<System.ComponentModel.INotifyPropertyChanged> _subscribedItems = new();
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
        private int _draggingIndex = -1;
        private Point _dragStartPoint;
        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollVelocity;

        #endregion

        #region Static Fields (Styled Properties)

        public static readonly StyledProperty<double> SelectedIndexProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SelectedIndex), 0.0);

        public static readonly StyledProperty<double> ItemSpacingProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemSpacing), 1.0);

        public static readonly StyledProperty<double> ItemScaleProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemScale), 1.0);

        public static readonly StyledProperty<double> VerticalOffsetProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(VerticalOffset), 0.0);

        public static readonly StyledProperty<double> SliderVerticalOffsetProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SliderVerticalOffset), 60.0);

        public static readonly StyledProperty<double> SliderTrackHeightProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SliderTrackHeight), 4.0);

        public static readonly StyledProperty<double> SideTranslationProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(SideTranslation), 320.0);

        public static readonly StyledProperty<double> StackSpacingProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(StackSpacing), 160.0);

        public static readonly StyledProperty<double> ItemWidthProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemWidth), 200.0);

        public static readonly StyledProperty<double> ItemHeightProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, double>(nameof(ItemHeight), 200.0);

        public static readonly StyledProperty<System.Windows.Input.ICommand?> ItemSelectedCommandProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, System.Windows.Input.ICommand?>(nameof(ItemSelectedCommand));

        public static readonly StyledProperty<System.Windows.Input.ICommand?> ItemDoubleClickedCommandProperty =
            AvaloniaProperty.Register<CompositionCarouselControl, System.Windows.Input.ICommand?>(nameof(ItemDoubleClickedCommand));

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
        public System.Windows.Input.ICommand? ItemSelectedCommand
        {
            get => GetValue(ItemSelectedCommandProperty);
            set => SetValue(ItemSelectedCommandProperty, value);
        }

        /// <summary>
        /// Command executed when an item is double-clicked. The command
        /// parameter will be the index (int) of the double-clicked item.
        /// </summary>
        public System.Windows.Input.ICommand? ItemDoubleClickedCommand
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

            _uiSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (s, e) =>
            {
                long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_uiLastTicks == 0) _uiLastTicks = currentTicks;
                double dt = (double)(currentTicks - _uiLastTicks) / System.Diagnostics.Stopwatch.Frequency;
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
                _visual?.SendHandlerMessage(new UpdateImageMessage(index, image, false));
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
                _sharedPlaceholder = GeneratePlaceholder(0); // Generic index 0 for placeholder
            return _sharedPlaceholder!;
        }

        // Compute projected center of item `i` using the same math used for rendering so hit-testing matches visuals
        private (Vector2 proj, float scale) ProjectedCenterForIndex(int i, double currentIndex, Vector2 size)
        {
            float scaleVal = (float)ItemScale;
            float w = (float)ItemWidth * scaleVal;
            float h = (float)ItemHeight * scaleVal;
            var center = new Vector2(size.X / 2, (float)(size.Y / 2 + VerticalOffset));

            float diff = (float)(i - currentIndex);
            float absDiff = Math.Abs(diff);

            const float sideRot = 0.95f;
            float sideTrans = (float)SideTranslation;
            float stackSpace = (float)StackSpacing;

            float transition = (float)Math.Tanh(diff * 2.0f);
            float rotationY = -transition * sideRot;
            float stackFactor = (float)Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f);
            float translationX = (transition * sideTrans + stackFactor * stackSpace) * (float)ItemSpacing * scaleVal;
            float translationZ = (float)(-Math.Pow(absDiff, 0.7f) * 220f * (float)ItemSpacing * scaleVal);

            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float itemPerspectiveScale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));

            var matrix = Matrix4x4.CreateTranslation(new Vector3(translationX, 0, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(itemPerspectiveScale);
            var vt = Vector3.Transform(new Vector3(0, 0, 0), matrix);
            float s = 1000f / (1000f - vt.Z);
            return (new Vector2(center.X + vt.X * s, center.Y + vt.Y * s), s);
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
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
            ClearResources();
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
                    try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
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
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
            }
            else if (change.Property == GlobalOpacityProperty)
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
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
            try { _loadCts?.Cancel(); _loadCts?.Dispose(); } catch { }
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

                if (items[i] is System.ComponentModel.INotifyPropertyChanged inpc)
                {
                    if (_subscribedItems.Add(inpc)) inpc.PropertyChanged += Item_PropertyChanged;
                }
            }
            
            _propBitmap = null; _propFile = null; _cachedBitmapName = null; _cachedFileName = null;
            
            _visual?.SendHandlerMessage(_images.ToArray());

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
            var list = ItemsSource as IList ?? ItemsSource.Cast<object>().ToList();
            int totalCount = list.Count;
            if (totalCount == 0) return;

            // Smaller window for fluidity, faster response
            const int loadWindow = 12;

            // 1. Build lookup dictionary for O(1) index check
            var itemToIndex = new Dictionary<object, int>();
            for (int k = 0; k < totalCount; k++) 
            {
                var val = list[k];
                if (val != null) itemToIndex[val] = k;
            }

            // 2. Prune distant items
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
                            _visual?.SendHandlerMessage(new UpdateImageMessage(idx, placeholder, false));
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

            var cachePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
            try { if (!System.IO.Directory.Exists(cachePath)) System.IO.Directory.CreateDirectory(cachePath); } catch { }

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
                    if (!string.IsNullOrEmpty(ImageBitmapProperty))
                    {
                        if (_propBitmap == null || _cachedBitmapName != ImageBitmapProperty)
                        {
                            _propBitmap = type.GetProperty(ImageBitmapProperty);
                            _cachedBitmapName = ImageBitmapProperty;
                        }
                        bitmapValue = _propBitmap?.GetValue(item) as Bitmap;
                    }
                    if (bitmapValue == null && !string.IsNullOrEmpty(ImageFileNameProperty))
                    {
                        if (_propFile == null || _cachedFileName != ImageFileNameProperty)
                        {
                            _propFile = type.GetProperty(ImageFileNameProperty);
                            _cachedFileName = ImageFileNameProperty;
                        }
                        fileName = _propFile?.GetValue(item) as string;
                    }
                }
                catch { }

                SKImage? realImage = null;
                try
                {
                    if (ct.IsCancellationRequested) return;
                    realImage = await LoadImageAsync(bitmapValue, fileName, cachePath, ct);
                }
                catch { }

                if (ct.IsCancellationRequested)
                {
                    realImage?.Dispose();
                    return;
                }

                if (realImage != null && realImage.Width > 0)
                {
                    // FIX: Dispose old image if it was replaced (e.g. by another load or property change)
                    if (_imageCache.TryGetValue(item, out var oldImg))
                    {
                        if (_visual != null) _visual.SendHandlerMessage(new DisposeImageMessage(oldImg));
                        else oldImg.Dispose();
                    }

                    _imageCache[item] = realImage;
                    TouchCacheItem(item);
                    if (i < _images.Count) _images[i] = realImage;
                    SetImage(i, realImage);
                }
                else
                {
                    SetLoading(i, false);
                }
            }

            TrimImageCache(itemToIndex);
        }

        private async Task LoadItemsAsync(IEnumerable? itemsSource, CancellationToken ct) => await VirtualizeAsync((int)Math.Round(SelectedIndex), ct);


        private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender == null || (e.PropertyName != ImageBitmapProperty && e.PropertyName != ImageFileNameProperty)) return;
            Dispatcher.UIThread.Post(async () =>
            {
                var items = ItemsSource?.Cast<object>().ToList();
                int idx = items?.IndexOf(sender) ?? -1;
                if (idx == -1) return;

                Bitmap? bitmapValue = null;
                string? fileName = null;
                try
                {
                    if (!string.IsNullOrEmpty(ImageBitmapProperty))
                        bitmapValue = sender.GetType().GetProperty(ImageBitmapProperty)?.GetValue(sender) as Bitmap;

                    if (bitmapValue == null && !string.IsNullOrEmpty(ImageFileNameProperty))
                        fileName = sender.GetType().GetProperty(ImageFileNameProperty)?.GetValue(sender) as string;
                }
                catch { }

                var cachePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImageCache");
                SKImage? realImage = null;
                try
                {
                    realImage = await LoadImageAsync(bitmapValue, fileName, cachePath, CancellationToken.None);
                }
                catch { }

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

        private async Task<SKImage?> ToSKImageAsync(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Format == null || bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0) return null;

            int w = bitmap.PixelSize.Width;
            int h = bitmap.PixelSize.Height;
            int stride = w * 4;
            int bufferSize = h * stride;
            byte[]? buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);

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
                        System.Diagnostics.Debug.WriteLine($"ToSKImage: CopyPixels failed: {ex.Message}");
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
                                    System.Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
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
                                    System.Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), skBmp.ByteCount, skBmp.ByteCount);
                                }
                            }
                        }

                        if (skBmp.Width <= 512 && skBmp.Height <= 512)
                            return SKImage.FromBitmap(skBmp);

                        using var resized = skBmp.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.Medium);
                        return resized != null ? SKImage.FromBitmap(resized) : SKImage.FromBitmap(skBmp);
                    }
                    catch { return null; }
                });
            }
            finally
            {
                if (buffer != null) System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<SKImage?> LoadImageAsync(Bitmap? bitmapValue, string? fileName, string cachePath, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return null;
            if (bitmapValue != null)
                return await ToSKImageAsync(bitmapValue);
            if (!string.IsNullOrEmpty(fileName) && System.IO.File.Exists(fileName))
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
                        _visual?.SendHandlerMessage(new UpdateImageMessage(idx, placeholder, false));
                    }

                    if (_visual != null)
                        _visual.SendHandlerMessage(new DisposeImageMessage(img));
                    else
                        img.Dispose();
                }
            }
        }

        private SKImage GeneratePlaceholder(int i, Random? random = null)
        {
            random ??= new Random(i);
            using var surface = SKSurface.Create(new SKImageInfo(300, 300));
            var canvas = surface.Canvas;
            var color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White, TextSize = 40, IsAntialias = true, FakeBoldText = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold) };
            using var shaderPaint = new SKPaint { IsAntialias = true };
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(300, 300), new[] { color, color.WithAlpha(150), color.WithAlpha(255) }, null, SKShaderTileMode.Clamp);
            shaderPaint.Shader = shader;
            canvas.DrawRect(new SKRect(0, 0, 300, 300), shaderPaint);
            canvas.DrawText($"ALBUM {i}", 150, 100, paint);
            paint.TextSize = 25;
            paint.Color = SKColors.White.WithAlpha(200);
            canvas.DrawText("Artist Name", 150, 240, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 2;
            canvas.DrawCircle(150, 170, 40, paint);
            canvas.DrawCircle(150, 170, 10, paint);
            paint.StrokeWidth = 10;
            paint.Color = SKColors.Black.WithAlpha(100);
            canvas.DrawRect(new SKRect(5, 5, 295, 295), paint);
            return surface.Snapshot();
        }

        private SKImage? LoadAndResize(string file, string cachePath)
        {
            try
            {
                var cachedFile = System.IO.Path.Combine(cachePath, System.IO.Path.GetFileName(file));
                if (System.IO.File.Exists(cachedFile))
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
                        using (var stream = System.IO.File.Create(cachedFile))
                            data.SaveTo(stream);
                        return img;
                    }
                }

            } catch { }
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
                if (ItemsSource != null) UpdateItems();
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
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
                    _dragStartPoint = _prevPoint;
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
            float diff = (float)(i - currentIndex);
            float absDiff = Math.Abs(diff);

            const float sideRot = 0.95f;   
            float sideTrans = (float)SideTranslation;  
            float stackSpace = (float)StackSpacing; 

            // Smoothly interpolate between center and stack states
            float transition = (float)Math.Tanh(diff * 2.0f);
            float rotationY = -transition * sideRot;
            
            // Smoother stack transition using a slight power curve
            float stackFactor = (float)Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f);
            float translationX = (transition * sideTrans + stackFactor * stackSpace) * spacing * scale;
            
            // Smooth Z transition
            float translationZ = (float)(-Math.Pow(absDiff, 0.7f) * 220f * spacing * scale);

            // Center pop effect: adds a smooth zoom for the item in the middle
            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float itemPerspectiveScale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));
            
            var matrix = Matrix4x4.CreateTranslation(new Vector3(translationX, 0, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(itemPerspectiveScale);
            
            Vector2 Proj(Vector3 v) { var vt = Vector3.Transform(v, matrix); float s = 1000f / (1000f - vt.Z); return new Vector2(center.X + vt.X * s, center.Y + vt.Y * s); }
            var p1 = Proj(new Vector3(-w/2, -h/2, 0)); var p2 = Proj(new Vector3(w/2, -h/2, 0)); var p3 = Proj(new Vector3(w/2, h/2, 0)); var p4 = Proj(new Vector3(-w/2, h/2, 0));
            
            double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            var c1 = Cross(p1.ToPoint(), p2.ToPoint(), p); var c2 = Cross(p2.ToPoint(), p3.ToPoint(), p); var c3 = Cross(p3.ToPoint(), p4.ToPoint(), p); var c4 = Cross(p4.ToPoint(), p1.ToPoint(), p);
            return (c1 >= 0 && c2 >= 0 && c3 >= 0 && c4 >= 0) || (c1 <= 0 && c2 <= 0 && c3 <= 0 && c4 <= 0);
        }

        #endregion
    }
}