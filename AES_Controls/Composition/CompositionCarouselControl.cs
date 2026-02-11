using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
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
        private CompositionCustomVisual? _visual;
        private List<SKImage> _images = new();
        private Dictionary<object, SKImage> _imageCache = new();
        private SKImage? _sharedPlaceholder;
        private System.Reflection.PropertyInfo? _propBitmap;
        private System.Reflection.PropertyInfo? _propFile;
        private string? _cachedBitmapName;
        private string? _cachedFileName;
        private HashSet<System.ComponentModel.INotifyPropertyChanged> _subscribedItems = new();

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
            float w = 200 * scaleVal;
            float h = 200 * scaleVal;
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

            // Primary approach: map pointer X to a slot using same logic as dragging drop target
            double centerX = size.X / 2.0;
            double itemWidth = 200 * ItemScale * ItemSpacing;
            double relativeX = point.X - centerX;
            double targetFloat = _uiCurrentIndex + (relativeX / (itemWidth * 1.5));
            int candidate = (int)Math.Clamp(Math.Round(targetFloat), 0, Math.Max(0, _images.Count - 1));

            // Check candidate and neighbors using actual polygon containment (more accurate for perspective)
            int[] toCheck = { candidate, candidate - 1, candidate + 1 };
            var center = new Vector2(size.X / 2, (float)(size.Y / 2 + VerticalOffset));
            float w = 200 * (float)ItemScale; float h = 200 * (float)ItemScale; float spacing = (float)ItemSpacing;
            foreach (var idx in toCheck)
            {
                if (idx >= 0 && idx < _images.Count)
                {
                    if (IsPointInItem(point, idx, center, w, h, spacing, _uiCurrentIndex, (float)ItemScale))
                        return idx;
                }
            }

            // Fallback: return candidate slot
            return candidate;
        }

        private void ClearResources()
        {
            var oldCache = _imageCache.Values.ToList();
            var oldPlaceholder = _sharedPlaceholder;
            
            _imageCache.Clear();
            _sharedPlaceholder = null;
            _images.Clear();

            foreach (var item in _subscribedItems) item.PropertyChanged -= Item_PropertyChanged;
            _subscribedItems.Clear();
            
            // 1. Notify visual handler of empty list first so it stops rendering old items

            _visual?.SendHandlerMessage(new List<SKImage>());

            // 2. Then schedule disposal of native resources
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

        /// <summary>
        /// Draws the control background and then delegates to the base draw
        /// path. Rendering of the carousel itself is handled by the composition
        /// visual handler.
        /// </summary>
        /// <param name="context">Drawing context provided by Avalonia.</param>
        public override void Render(DrawingContext context)
        {
            if (Background != null)
                context.DrawRectangle(Background, null, new Rect(Bounds.Size));
            base.Render(context);
        }

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

        private int _lastVirtualizationIndex = -1;
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
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
            else if (change.Property == GlobalOpacityProperty)
                _visual?.SendHandlerMessage(new GlobalOpacityMessage(change.GetNewValue<double>()));
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
                    _images.Add(cached);
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
            const int pruneThreshold = 45;

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
                if (!exists || Math.Abs(idx - centerIdx) > pruneThreshold)
                {
                    if (_imageCache.Remove(key, out var img))
                    {
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

            // 3. Load missing items
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
                    if (bitmapValue != null) realImage = await ToSKImageAsync(bitmapValue);
                    else if (fileName != null && System.IO.File.Exists(fileName)) realImage = await Task.Run(() => LoadAndResize(fileName, cachePath), ct);
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
                    if (i < _images.Count) _images[i] = realImage;
                    SetImage(i, realImage);
                }
                else
                {
                    SetLoading(i, false);
                }
            }
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
                    if (bitmapValue != null) realImage = await ToSKImageAsync(bitmapValue);
                    else if (fileName != null && System.IO.File.Exists(fileName)) realImage = await Task.Run(() => LoadAndResize(fileName, cachePath));
                }
                catch { }

                if (realImage != null)
                {
                    if (_imageCache.TryGetValue(sender, out var old))
                        _visual?.SendHandlerMessage(new DisposeImageMessage(old));
                    _imageCache[sender] = realImage;
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
                _visual.Size = logicalSize;
                _visual.SendHandlerMessage(logicalSize);
                if (_images.Any()) _visual.SendHandlerMessage(_images);
                _visual.SendHandlerMessage(SelectedIndex);
                _visual.SendHandlerMessage(new SpacingMessage(ItemSpacing));
                _visual.SendHandlerMessage(new ScaleMessage(ItemScale));
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
                _visual.Size = logicalSize;
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
            base.OnPointerPressed(e);
            Focus();
            var pos = e.GetPosition(this);
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

                // Determine drop target slot
                double centerX = Bounds.Width / 2.0;
                double itemWidth = 200 * ItemScale * ItemSpacing;
                double relativeX = point.X - centerX;
                
                // Adjust for current scroll position to find visual slot
                double targetFloat = _uiCurrentIndex + (relativeX / (itemWidth * 1.5)); // Increased divisor for smoother target selection
                int targetIndex = (int)Math.Clamp(Math.Round(targetFloat), 0, _images.Count - 1);
                
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
                double centerX = Bounds.Width / 2.0;
                double itemWidth = 200 * ItemScale * ItemSpacing;
                double relativeX = e.GetPosition(this).X - centerX;
                double targetFloat = _uiCurrentIndex + (relativeX / (itemWidth * 1.5));
                int targetIndex = (int)Math.Clamp(Math.Round(targetFloat), 0, _images.Count - 1);

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
            float itemWidth = 200 * scaleVal;
            float itemHeight = 200 * scaleVal;
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

            // Match wide image attenuation logic for accurate hit-testing
            if (i >= 0 && i < _images.Count)
            {
                var img = _images[i];
                if (img is not null && img.Width > img.Height * 1.25)
                {
                    translationZ *= 0.45f;
                    rotationY *= 0.5f;
                }
            }

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
    }

    internal static class VectorExtensions { public static Point ToPoint(this Vector2 v) => new Point(v.X, v.Y); }

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
    internal record DisposeImageMessage(SKImage Image);
    internal record DragStateMessage(int Index, bool IsDragging);
    internal record DragPositionMessage(Vector2 Position);
    internal record DropTargetMessage(int Index);
    internal record SliderPressedMessage(bool IsPressed);

    public class CompositionCarouselVisualHandler : CompositionCustomVisualHandler
    {
        private float _visibleRange = 10;
        private bool _visibleRangeDirty = true;
        private double _targetIndex;
        private double _currentIndex;
        private double _currentVelocity;
        private long _lastTicks;
        private float _itemSpacing = 1.0f;
        private float _itemScale = 1.0f;
        private float _verticalOffset = 0.0f;
        private float _sliderVerticalOffset = 60.0f;
        private float _sliderTrackHeight = 4.0f;
        private float _sideTranslation = 320.0f;
        private float _stackSpacing = 160.0f;
        private Vector2 _visualSize;
        private SKColor _backgroundColor = SKColors.Transparent;
        private List<SKImage> _images = new();
        private Dictionary<SKImage, SKShader> _shaderCache = new();
        private HashSet<int> _loadingIndices = new();
        private float _spinnerRotation = 0;
        private static readonly SKColor[] Colors = { SKColors.Red, SKColors.Blue, SKColors.Green, SKColors.Yellow, SKColors.Purple };

        private int _draggingIndex = -1;
        private int _dropTargetIndex = -1;
        private double _smoothDropTargetIndex = -1;
        private Vector2 _dragPosition;
        private bool _isDropping = false;
        private float _dropAlpha = 1.0f;
        private float _globalTransitionAlpha = 1.0f;
        private float _currentGlobalOpacity = 1.0f;
        private float _targetGlobalOpacity = 1.0f;
        private bool _isSliderPressed;

        private readonly SKPaint _quadPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        // Projection / depth tuning to reduce perspective distortion on side items
        private readonly float _projectionDistance = 2500f; // larger => weaker perspective
        private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _spinnerPaint = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeWidth = 4, Style = SKPaintStyle.Stroke };
        private readonly SKPath _itemPath = new();
        private readonly SKPaint _sliderPaint = new() { IsAntialias = true };
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

        private readonly SKColor _trackColor3 = SKColor.Parse("#F0F0F0");

        public override void OnMessage(object message)
        {
            if (message is double index) 
            { 
                _targetIndex = index; 
                if (_lastTicks == 0) _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                RegisterForNextAnimationFrameUpdate(); 
            }
            else if (message is Vector2 size) { _visualSize = size; _visibleRangeDirty = true; Invalidate(); }
            else if (message is IEnumerable<SKImage> enumerableImgs && message is not string) 
            { 
                var imgs = enumerableImgs as List<SKImage> ?? enumerableImgs.ToList();
                var newImgs = new HashSet<SKImage>(imgs.Where(i => i != null));
                foreach (var img in _images) 
                {
                    if (img != null && !newImgs.Contains(img)) 
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

                    if (oldImg != null && oldImg != newImg)
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
            else if (message is VerticalOffsetMessage vom) { _verticalOffset = (float)vom.Value; Invalidate(); }
            else if (message is SliderVerticalOffsetMessage svim) { _sliderVerticalOffset = (float)svim.Value; ClearSliderShaders(); Invalidate(); }
            else if (message is SliderTrackHeightMessage sthm) { _sliderTrackHeight = (float)sthm.Value; ClearSliderShaders(); Invalidate(); }
            else if (message is SideTranslationMessage stm) { _sideTranslation = (float)stm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is StackSpacingMessage ssm) { _stackSpacing = (float)ssm.Value; _visibleRangeDirty = true; Invalidate(); }
            else if (message is BackgroundMessage bg) { _backgroundColor = bg.Color; Invalidate(); }
            else if (message is GlobalOpacityMessage gom)
            {
                _targetGlobalOpacity = (float)Math.Clamp(gom.Value, 0.0, 1.0);
                RegisterForNextAnimationFrameUpdate();
            }
            else if (message is SliderPressedMessage spm) { _isSliderPressed = spm.IsPressed; Invalidate(); }
        }

        private void ClearSliderShaders()
        {
            _trackShader?.Dispose(); _trackShader = null;
            _thumbShader?.Dispose(); _thumbShader = null;
        }

        public override void OnAnimationFrameUpdate()
        {
            long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastTicks == 0) _lastTicks = currentTicks;
            double dt = (double)(currentTicks - _lastTicks) / System.Diagnostics.Stopwatch.Frequency;
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

            // Smoothly animate global opacity target (for fade in/out)
            if (Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f)
            {
                float diff = _targetGlobalOpacity - _currentGlobalOpacity;
                // Simple easing
                _currentGlobalOpacity += diff * Math.Min(1.0f, (float)(dt * 4.0));
                Invalidate();
            }

            bool isAnimating = Math.Abs(distance) > 0.0001 || Math.Abs(_currentVelocity) > 0.0001 || _globalTransitionAlpha < 1.0f || Math.Abs(_currentGlobalOpacity - _targetGlobalOpacity) > 0.001f || _isDropping || _draggingIndex != -1;
            if (isAnimating || _loadingIndices.Count > 0) 
            {
                RegisterForNextAnimationFrameUpdate();
                Invalidate();
            }
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;
            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            if (_backgroundColor.Alpha > 0) canvas.Clear(_backgroundColor);
            var center = new Vector2(_visualSize.X / 2.0f, (_visualSize.Y / 2.0f) + _verticalOffset);
            float baseW = 200 * _itemScale; float baseH = 200 * _itemScale;
            
            if (_visibleRangeDirty)
            {
                float itemUnit = Math.Max(0.1f, _itemSpacing * _itemScale);
                _visibleRange = (float)Math.Max(10, (_visualSize.X / 2f / itemUnit - _sideTranslation) / Math.Max(1f, _stackSpacing)) + 8;
                _visibleRangeDirty = false;
            }
            
            int vRange = (int)_visibleRange;
            int total = _images.Count;
            int centerIdx = (int)Math.Round(_currentIndex);
            int start = Math.Max(0, centerIdx - vRange);
            int end = Math.Min(total - 1, centerIdx + vRange);

            // Apply global opacity multiplier from composition-level fade
            canvas.Save();
            var prevAlpha = canvas.TotalMatrix; // not directly setting alpha; use paint alpha in RenderItem via _currentGlobalOpacity
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

        private void RenderItem(SKCanvas canvas, int i, Vector2 center, float itemWidth, float itemHeight)
        {
            float visualI = i;
            if (_draggingIndex != -1 && _smoothDropTargetIndex != -1 && i != _draggingIndex)
            {
                float rank = (i < _draggingIndex) ? (float)i : (float)(i - 1);
                float slotDiff = rank - (float)_smoothDropTargetIndex;
                float shiftStrength = 1.0f / (1.0f + (float)Math.Exp(-(slotDiff + 0.5f) * 8.0f));
                float parting = (float)Math.Exp(-(slotDiff + 0.5f) * (slotDiff + 0.5f) * 2.0f);
                float partedVisualI = rank + shiftStrength + (slotDiff < -0.5f ? -0.25f : 0.25f) * parting;
                if (_isDropping) visualI = partedVisualI + ((float)i - partedVisualI) * (float)(1.0 - Math.Pow(1.0 - _dropAlpha, 3));
                else visualI = partedVisualI;
            }
            float diff = (float)(visualI - _currentIndex); float absDiff = Math.Abs(diff);
            float rotationY = -(float)Math.Tanh(diff * 2.0f) * 0.95f;
            float stackFactor = (float)Math.Sign(diff) * (float)Math.Pow(Math.Max(0, absDiff - 0.45f), 1.1f);
            float translationX = ((float)Math.Tanh(diff * 2.0f) * _sideTranslation + stackFactor * _stackSpacing) * _itemSpacing * _itemScale;
            float translationY = 0; float translationZ = (float)(-Math.Pow(absDiff, 0.7f) * 220f * _itemSpacing * _itemScale);

            // Reduce perspective distortion for very wide images to keep them readable
            if (i >= 0 && i < _images.Count)
            {
                var skImg = _images[i];
                if (skImg != null && _dimCache.TryGetValue(skImg, out var dims))
                {
                    if (dims.Width > dims.Height * 1.25)
                    {
                        // Use consistent attenuation for wide images to avoid non-monotonic "wobble"
                        translationZ *= 0.45f;
                        rotationY *= 0.5f;
                    }
                }
            }
            float centerPop = 0.18f * (float)Math.Exp(-absDiff * absDiff * 6.0f);
            float scale = Math.Max(0.1f, (1.0f + centerPop) - (absDiff * 0.06f));
            if (i == _draggingIndex)
            {
                if (_isDropping) { float eased = (float)(1.0 - Math.Pow(1.0 - _dropAlpha, 3)); float zS = (1000f - 200f * _itemScale) / 1000f; float dX = (_dragPosition.X - center.X) * zS; float dY = (_dragPosition.Y - center.Y) * zS; translationX = dX + (translationX - dX) * eased; translationY = dY + (translationY - dY) * eased; translationZ = 200f * _itemScale + (translationZ - 200f * _itemScale) * eased; scale = 0.82f + (scale - 0.82f) * eased; rotationY *= eased; }
                else { translationZ = 200f * _itemScale; float zS = (1000f - translationZ) / 1000f; translationX = (_dragPosition.X - center.X) * zS; translationY = (_dragPosition.Y - center.Y) * zS; scale = 0.82f; rotationY = 0; }
            }
            var matrix = Matrix4x4.CreateTranslation(new Vector3(translationX, translationY, translationZ)) * Matrix4x4.CreateRotationY(rotationY) * Matrix4x4.CreateScale(scale);
            SKImage? img = (i >= 0 && i < _images.Count) ? _images[i] : null;
            DrawQuad(canvas, itemWidth, itemHeight, matrix, img, i, (i == _draggingIndex ? 0 : absDiff), center);
            if (_loadingIndices.Contains(i)) DrawSpinner(canvas, center, matrix);
            var refMat = Matrix4x4.CreateScale(1, -1, 1) * Matrix4x4.CreateTranslation(0, itemHeight + 25, 0) * matrix;
            DrawQuad(canvas, itemWidth, itemHeight, refMat, img, i, (i == _draggingIndex ? 0 : absDiff), center, true);
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
            catch { } 
        }

        private void DrawQuad(SKCanvas canvas, float w, float h, Matrix4x4 model, SKImage? image, int index, float absDiff, Vector2 center, bool isRef = false)
        {
            float opacity = (float)(isRef ? 0.08 : 1.0) * (float)(1.0 - absDiff * 0.2) * _globalTransitionAlpha * _currentGlobalOpacity;
            if (opacity < 0.01f) return;
            void Proj(int idx, float x, float y)
            {
                var vt = Vector3.Transform(new Vector3(x, y, 0), model);
                float s = _projectionDistance / (_projectionDistance - vt.Z);
                // clamp projection scale to avoid extreme foreshortening on very wide/close items
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
                        _quadPaint.Shader = shader; float sc = Math.Max(w / dims.Width, h / dims.Height); float wR = w / sc; float hR = h / sc; float xO = (dims.Width - wR) / 2f; float yO = (dims.Height - hR) / 2f;
                        _tBuffer[0] = new SKPoint(xO, yO); _tBuffer[1] = new SKPoint(xO + wR, yO); _tBuffer[2] = new SKPoint(xO + wR, yO + hR); _tBuffer[3] = new SKPoint(xO, yO + hR);
                        Proj(0, -w/2, -h/2); Proj(1, w/2, -h/2); Proj(2, w/2, h/2); Proj(3, -w/2, h/2);
                        canvas.DrawVertices(SKVertexMode.Triangles, _vBuffer, _tBuffer, null, QuadIndices, _quadPaint);
                        _quadPaint.Shader = null; return;
                    }
                }
            }
            Proj(0, -w/2, -h/2); Proj(1, w/2, -h/2); Proj(2, w/2, h/2); Proj(3, -w/2, h/2);
            _itemPath.Reset(); _itemPath.MoveTo(_vBuffer[0]); _itemPath.LineTo(_vBuffer[1]); _itemPath.LineTo(_vBuffer[2]); _itemPath.LineTo(_vBuffer[3]); _itemPath.Close();
            _fillPaint.Color = Colors[index % Colors.Length].WithAlpha((byte)(255 * opacity)); canvas.DrawPath(_itemPath, _fillPaint);
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
