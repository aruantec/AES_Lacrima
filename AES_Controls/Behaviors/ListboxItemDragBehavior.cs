using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Input;
using log4net;

namespace AES_Controls.Behaviors
{
    /// <summary>
    /// Represents the layout dimensions and position of an item in the list.
    /// </summary>
    internal class ItemDimension
    {
        /// <summary>
        /// Gets or sets the bounds of the item container.
        /// </summary>
        public Rect Bounds { get; set; }

        /// <summary>
        /// Gets or sets the stable virtual position of the item within the panel.
        /// </summary>
        public Point VirtualPosition { get; set; }

        /// <summary>
        /// Gets or sets the center point of the item in virtual space.
        /// </summary>
        public Point VirtualCenter { get; set; }

        /// <summary>
        /// Gets or sets the item index.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the dimensions are estimated (virtualized).
        /// </summary>
        public bool IsEstimated { get; set; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemDimension"/> class.
        /// </summary>
        public ItemDimension(Control? control, Visual? relativeTo, int index = -1)
        {
            Index = index;
            if (control != null && relativeTo != null)
            {
                Update(control, relativeTo, index);
            }
        }

        /// <summary>
        /// Updates the dimension data from a live control.
        /// </summary>
        public void Update(Control control, Visual relativeTo, int index = -1)
        {
            IsEstimated = false;
            
            if (index != -1) Index = index;
            Bounds = control.Bounds;
            
            var p = control.TranslatePoint(default, relativeTo);
            if (p.HasValue)
            {
                Point actual = p.Value;
                if (control.RenderTransform is TranslateTransform tt)
                    actual = new Point(actual.X - tt.X, actual.Y - tt.Y);
                
                VirtualPosition = actual;
                VirtualCenter = new Point(VirtualPosition.X + Bounds.Width * 0.5, VirtualPosition.Y + Bounds.Height * 0.5);
            }
        }
    }

    internal class ItemsSourceObserver : IObserver<IEnumerable?>
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ItemsSourceObserver));
        private readonly ListboxItemDragBehavior _behavior;

        public ItemsSourceObserver(ListboxItemDragBehavior behavior)
        {
            _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        }

        public void OnNext(IEnumerable? value) => _behavior.OnItemsSourceChanged(value);
        public void OnError(Exception error) => Log.Error("ItemsSourceMonitor Error", error);
        public void OnCompleted() { }
    }

    /// <summary>
    /// Provides drag-and-drop reordering functionality for Avalonia ListBox items with smooth animations and multi-selection support.
    /// </summary>
    public class ListboxItemDragBehavior : Behavior<ListBox>
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ListboxItemDragBehavior));

        /// <summary>
        /// Defines the <see cref="DropCommand"/> property.
        /// </summary>
        public static readonly StyledProperty<ICommand?> DropCommandProperty =
            AvaloniaProperty.Register<ListboxItemDragBehavior, ICommand?>(nameof(DropCommand));

        /// <summary>
        /// Gets or sets the command executed when a drag operation completes successfully.
        /// Receives (item, fromIndex, toIndex) as parameter.
        /// </summary>
        public ICommand? DropCommand
        {
            get => GetValue(DropCommandProperty);
            set => SetValue(DropCommandProperty, value);
        }

        /// <summary>
        /// Defines the <see cref="AutoScrollZoneWidth"/> property.
        /// </summary>
        public static readonly StyledProperty<double> AutoScrollZoneWidthProperty =
            AvaloniaProperty.Register<ListboxItemDragBehavior, double>(nameof(AutoScrollZoneWidth), 80.0);

        /// <summary>
        /// Gets or sets the width/height of the edge zone that triggers auto-scrolling during drag.
        /// </summary>
        public double AutoScrollZoneWidth
        {
            get => GetValue(AutoScrollZoneWidthProperty);
            set => SetValue(AutoScrollZoneWidthProperty, value);
        }

        /// <summary>
        /// Defines the <see cref="MaxAutoScrollSpeed"/> property.
        /// </summary>
        public static readonly StyledProperty<double> MaxAutoScrollSpeedProperty =
            AvaloniaProperty.Register<ListboxItemDragBehavior, double>(nameof(MaxAutoScrollSpeed), 16.0);

        /// <summary>
        /// Gets or sets the maximum speed of the auto-scrolling behavior.
        /// </summary>
        public double MaxAutoScrollSpeed
        {
            get => GetValue(MaxAutoScrollSpeedProperty);
            set => SetValue(MaxAutoScrollSpeedProperty, value);
        }

        private Point _dragStartMouseVirtual;
        private Point _dragStartItemVirtual;
        private int _lastValidSlotIndex = -1;
        private Control? _currentDragged;
        private int _currentDraggedIndex;
        private readonly List<Control> _draggedContainers = new();
        private readonly List<int> _draggedIndices = new();
        private bool _isDragging;
        private TranslateTransform? _dragTransform;
        private readonly List<TranslateTransform> _draggedTransforms = new();
        private readonly List<Vector> _gluedOffsets = new();
        private List<Vector> _targetOffsetsRelPrimary = new(); 
        private readonly List<ItemDimension> _itemDimensions = new();
        private ScrollViewer? _scrollViewer;
        private Panel? _itemsPanel;
        private DispatcherTimer? _autoScrollTimer;
        private DispatcherTimer? _glueTimer;

        private Vector _autoScrollSpeed = default;
        private Vector _targetAutoScrollSpeed = default;
        private Vector _manualScrollDelta = default;

        /// <summary>
        /// Stores active animations for containers to allow cancellation.
        /// </summary>
        private readonly Dictionary<Control, (DispatcherTimer Timer, Point Target)> _activeAnimations = new();

        /// <summary>
        /// The duration of the swap animation in milliseconds.
        /// </summary>
        private const int SwapAnimationDurationMs = 200;

        /// <summary>
        /// The smoothing factor for auto-scrolling speed transitions.
        /// </summary>
        private const double AutoScrollSmoothing = 0.20;

        /// <summary>
        /// The threshold below which auto-scrolling is stopped.
        /// </summary>
        private const double AutoScrollStopThreshold = 0.12;

        /// <summary>
        /// The hysteresis in pixels to avoid flickering during item swaps.
        /// </summary>
        private const double SwapHysteresisPx = 8.0;

        /// <summary>
        /// The time of the last item swap to enforce a cooldown.
        /// </summary>
        private DateTime _lastSwapTime = DateTime.MinValue;

        /// <summary>
        /// The cooldown in milliseconds between successive item swaps.
        /// </summary>
        private const int SwapCooldownMs = 12;

        /// <summary>
        /// The maximum multiplier applied to <see cref="MaxAutoScrollSpeed"/> based on overshoot.
        /// </summary>
        private const double MaxAutoScrollSpeedMultiplier = 3.0;
        
        /// <summary>
        /// The minimum distance the pointer must move to start a drag operation.
        /// </summary>
        private const double DragStartThreshold = 4.0;

        /// <summary>
        /// Gets or sets a value indicating whether a significant drag has occurred.
        /// </summary>
        private bool _hasDragged;
        
        /// <summary>
        /// The last known screen position of the pointer.
        /// </summary>
        private Point? _lastPointerScreenPos;
        

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
                AssociatedObject.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
                AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, handledEventsToo: true);
                AssociatedObject.PropertyChanged += AssociatedObjectOnPropertyChanged;
                AssociatedObject
                    .GetObservable(ItemsControl.ItemsSourceProperty)
                    .Subscribe(new ItemsSourceObserver(this));
                AssociatedObject.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, handledEventsToo: true);

                _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
        }

        /// <summary>
        /// Called when the ItemsSource of the associated ListBox changes.
        /// </summary>
        public void OnItemsSourceChanged(object? newItemsSource)
        {
            if (AssociatedObject == null) return;
            if (!_isDragging)
            {
                _itemDimensions.Clear();
                _itemsPanel = null;
                
                int count = AssociatedObject.Items.Count;
                for(int i=0; i<count; i++)
                    _itemDimensions.Add(new ItemDimension(null, null, i));
            }
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            if (AssociatedObject == null) return;
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            AssociatedObject.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            AssociatedObject.PropertyChanged -= AssociatedObjectOnPropertyChanged;
            AssociatedObject.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);

            _autoScrollTimer?.Stop();
            _autoScrollTimer = null;

            foreach (var kv in _activeAnimations.ToArray())
            {
                try { kv.Value.Timer.Stop(); } catch (Exception ex) { Log.Error("Error stopping animation timer", ex); }
            }
            _activeAnimations.Clear();
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (AssociatedObject == null) return;
            _scrollViewer ??= FindScrollViewer(AssociatedObject);
            if (_scrollViewer == null) return;

            // Use accumulated delta for smooth kinetic manual scrolling
            const double ScrollIntensity = 120.0;
            
            double deltaX = e.Delta.X;
            double deltaY = e.Delta.Y;

            // Redirect vertical wheel to horizontal scroll if only horizontal scrolling is possible.
            // This enables standard mouse wheels to scroll through horizontal-only lists (like the playlist).
            bool canScrollH = _scrollViewer.Extent.Width > _scrollViewer.Viewport.Width;
            bool canScrollV = _scrollViewer.Extent.Height > _scrollViewer.Viewport.Height;

            if (canScrollH && !canScrollV && deltaY != 0 && deltaX == 0)
            {
                deltaX = deltaY;
                deltaY = 0;
            }

            _manualScrollDelta = new Vector(
                _manualScrollDelta.X - deltaX * ScrollIntensity,
                _manualScrollDelta.Y - deltaY * ScrollIntensity
            );

            // Ensure timer is running to process the manual scroll delta
            if (_autoScrollTimer != null && !_autoScrollTimer.IsEnabled)
                _autoScrollTimer.Start();

            e.Handled = true;
        }

        private void AssociatedObjectOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != ItemsControl.ItemsSourceProperty) return;
            var itemsObj = AssociatedObject?.ItemsSource ?? AssociatedObject?.Items;
            if (itemsObj is not INotifyCollectionChanged sourceCollection) return;
            sourceCollection.CollectionChanged -= SourceCollectionOnCollectionChanged;
            sourceCollection.CollectionChanged += SourceCollectionOnCollectionChanged;
        }

        private void SourceCollectionOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                if (e.NewStartingIndex >= 0 && e.NewStartingIndex <= _itemDimensions.Count)
                    _itemDimensions.Insert(e.NewStartingIndex, new ItemDimension(AssociatedObject?.ContainerFromIndex(e.NewStartingIndex) as Control, _itemsPanel, e.NewStartingIndex));
                else
                    _itemDimensions.Add(new ItemDimension(AssociatedObject?.ContainerFromIndex(e.NewStartingIndex) as Control, _itemsPanel, e.NewStartingIndex));
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                if (e.OldStartingIndex >= 0 && e.OldStartingIndex < _itemDimensions.Count)
                    _itemDimensions.RemoveAt(e.OldStartingIndex);
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _itemDimensions.Clear();
            }
            else if (e.Action == NotifyCollectionChangedAction.Move)
            {
                if (e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0
                    && e.OldStartingIndex < _itemDimensions.Count && e.NewStartingIndex < _itemDimensions.Count)
                {
                    var item = _itemDimensions[e.OldStartingIndex];
                    _itemDimensions.RemoveAt(e.OldStartingIndex);
                    _itemDimensions.Insert(e.NewStartingIndex, item);
                }
            }

            // Ensure all indices are up to date after collection changes
            for (int i = 0; i < _itemDimensions.Count; i++)
            {
                _itemDimensions[i].Index = i;
            }
        }

        /// <summary>
        /// Resets the drag-and-drop state.
        /// </summary>
        private void ResetFlags()
        {
            _isDragging = false;
            _hasDragged = false;
            _dragStartMouseVirtual = default;
            _dragStartItemVirtual = default;
            _currentDraggedIndex = -1;
            _lastValidSlotIndex = -1;
            
            foreach (var container in _draggedContainers)
                container.ZIndex = 0;
            
            _draggedContainers.Clear();
            _draggedIndices.Clear();
            _draggedTransforms.Clear();
            _gluedOffsets.Clear();
            _targetOffsetsRelPrimary.Clear();
            _currentDragged = null;
            _autoScrollTimer?.Stop();
            _glueTimer?.Stop();
            _glueTimer = null;
            _autoScrollSpeed = default;
            _targetAutoScrollSpeed = default;
            _manualScrollDelta = default;
            _scrollViewer = null;
            _itemsPanel = null;
            StopAllAnimationsAndResetTransforms();
        }

        /// <summary>
        /// Determines if the provided control is a button or is contained within one.
        /// </summary>
        private static bool IsOnButton(Control? start)
        {
            var c = start;
            while (c != null)
            {
                if (c is Button) return true;
                c = c.Parent as Control;
            }
            return false;
        }

        /// <summary>
        /// Handles the pointer pressed event to initiate a drag operation.
        /// </summary>
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ResetFlags();
            if (AssociatedObject == null) return;

            var srcControl = e.Source as Control;
            if (IsOnButton(srcControl)) return;

            var hitControl = e.Source as Control;
            while (hitControl != null && hitControl is not ListBoxItem)
            {
                hitControl = hitControl.Parent as Control;
            }

            if (hitControl is ListBoxItem listBoxItem)
            {
                _currentDragged = listBoxItem;
                _currentDraggedIndex = AssociatedObject.IndexFromContainer(listBoxItem);
                
                _draggedContainers.Clear();
                _draggedIndices.Clear();

                // Multi-selection check: gather selected items if the clicked item is part of the selection
                var selection = AssociatedObject.Selection;
                bool isClickedItemSelected = selection != null && selection.IsSelected(_currentDraggedIndex);
                
                if (isClickedItemSelected && selection != null)
                {
                    int itemCount = AssociatedObject.Items.Count;
                    for (int i = 0; i < itemCount; i++)
                    {
                        if (selection.IsSelected(i))
                        {
                            _draggedIndices.Add(i);
                            if (AssociatedObject.ContainerFromIndex(i) is Control container)
                                _draggedContainers.Add(container);
                        }
                    }
                }
                else
                {
                    if (_currentDraggedIndex >= 0)
                    {
                        _draggedIndices.Add(_currentDraggedIndex);
                        _draggedContainers.Add(listBoxItem);
                    }
                }

                _lastValidSlotIndex = _currentDraggedIndex; 
                
                _scrollViewer = FindScrollViewer(AssociatedObject);
                _itemsPanel = FindItemsPanel(AssociatedObject);
                
                if (_itemsPanel == null) return;
                
                // Cache dimensions in Virtual Space (Relative to ItemsPanel)
                _itemDimensions.Clear();
                int totalCount = AssociatedObject.Items.Count;
                for (int i = 0; i < totalCount; i++)
                {
                    var container = AssociatedObject.ContainerFromIndex(i);
                    _itemDimensions.Add(new ItemDimension(container as Control, _itemsPanel, i));
                }

                EstimateMissingDimensions();

                // Capture initial virtual positions
                _dragStartMouseVirtual = e.GetPosition(_itemsPanel);
                
                var itemPos = _currentDragged.TranslatePoint(default, _itemsPanel);
                if (itemPos.HasValue) 
                {
                    // ItemDimension.Update already handles removing active transforms, but here we enforce it
                    _dragStartItemVirtual = itemPos.Value;
                    if (_currentDragged.RenderTransform is TranslateTransform tt)
                    {
                        _dragStartItemVirtual = new Point(_dragStartItemVirtual.X - tt.X, _dragStartItemVirtual.Y - tt.Y);
                    }
                }

                _draggedTransforms.Clear();
                _gluedOffsets.Clear();

                // Calculate glued positions relative to _currentDragged
                int primaryIndexInSelection = _draggedContainers.IndexOf(_currentDragged);
                var orientation = Orientation.Vertical;
                if (_itemsPanel is StackPanel sp) orientation = sp.Orientation;
                else if (_itemsPanel is VirtualizingStackPanel vsp) orientation = vsp.Orientation;
                else if (_itemsPanel is WrapPanel wp) orientation = wp.Orientation;

                // Estimate gap from existing items
                double listGap = 0;
                if (_itemDimensions.Count > 1 && !_itemDimensions[0].IsEstimated && !_itemDimensions[1].IsEstimated)
                {
                    if (orientation == Orientation.Horizontal)
                        listGap = Math.Max(0, _itemDimensions[1].VirtualPosition.X - (_itemDimensions[0].VirtualPosition.X + _itemDimensions[0].Bounds.Width));
                    else
                        listGap = Math.Max(0, _itemDimensions[1].VirtualPosition.Y - (_itemDimensions[0].VirtualPosition.Y + _itemDimensions[0].Bounds.Height));
                }

                _targetOffsetsRelPrimary = new List<Vector>(new Vector[_draggedContainers.Count]);
                _targetOffsetsRelPrimary[primaryIndexInSelection] = default;

                // Glue items before
                for (int i = primaryIndexInSelection - 1; i >= 0; i--)
                {
                    var curr = _draggedContainers[i];
                    if (orientation == Orientation.Horizontal)
                        _targetOffsetsRelPrimary[i] = _targetOffsetsRelPrimary[i + 1] + new Vector(-(curr.Bounds.Width + listGap), 0);
                    else
                        _targetOffsetsRelPrimary[i] = _targetOffsetsRelPrimary[i + 1] + new Vector(0, -(curr.Bounds.Height + listGap));
                }
                // Glue items after
                for (int i = primaryIndexInSelection + 1; i < _draggedContainers.Count; i++)
                {
                    var prev = _draggedContainers[i - 1];
                    if (orientation == Orientation.Horizontal)
                        _targetOffsetsRelPrimary[i] = _targetOffsetsRelPrimary[i - 1] + new Vector(prev.Bounds.Width + listGap, 0);
                    else
                        _targetOffsetsRelPrimary[i] = _targetOffsetsRelPrimary[i - 1] + new Vector(0, prev.Bounds.Height + listGap);
                }

                if (_currentDraggedIndex >= 0 && _currentDraggedIndex < _itemDimensions.Count)
                {
                    Point primP = _itemDimensions[_currentDraggedIndex].VirtualPosition;
                    var finalGluedOffsets = new List<Vector>();

                    for (int i = 0; i < _draggedContainers.Count; i++)
                    {
                        var container = _draggedContainers[i];
                        int globalIndex = _draggedIndices[i];
                        Point myP = _itemDimensions[globalIndex].VirtualPosition;

                        Vector glued = new Vector(
                            primP.X + _targetOffsetsRelPrimary[i].X - myP.X,
                            primP.Y + _targetOffsetsRelPrimary[i].Y - myP.Y
                        );
                        finalGluedOffsets.Add(glued);
                        _gluedOffsets.Add(default); // Start with no offset (smooth transition)

                        var tt = new TranslateTransform(0, 0);
                        _draggedTransforms.Add(tt);
                        container.RenderTransform = tt;
                        container.ZIndex = 1000;

                        if (container == _currentDragged)
                            _dragTransform = tt;
                    }

                    AnimateGlue(finalGluedOffsets, 250);

                    e.Pointer.Capture(_currentDragged);
                    _isDragging = true;

                    _autoScrollTimer?.Start();
                }
                _lastPointerScreenPos = e.GetPosition(null);
            }
        }

        /// <summary>
        /// Handles the pointer moved event during dragging.
        /// </summary>
        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_currentDragged == null || _dragTransform == null || AssociatedObject == null || !_isDragging || _itemsPanel == null)
                return;

            var screenPos = e.GetPosition(null);
            _lastPointerScreenPos = screenPos;

            UpdateDragTransform();

            // Check if displacement exceeds threshold to start dragging
            if (!_hasDragged)
            {
                var dx = _dragTransform.X;
                var dy = _dragTransform.Y;
                if (Math.Abs(dx) > DragStartThreshold || Math.Abs(dy) > DragStartThreshold)
                    _hasDragged = true;
            }

            UpdateAutoScrollSpeed(screenPos);
            UpdateSwapTarget();
        }

        private void AnimateGlue(List<Vector> targets, int durationMs)
        {
            _glueTimer?.Stop();
            var sw = Stopwatch.StartNew();
            _glueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _glueTimer.Tick += (_, _) =>
            {
                var elapsed = sw.ElapsedMilliseconds;
                double progress = Math.Min(1.0, elapsed / (double)durationMs);
                
                // Sinusoidal ease out for smooth convergence
                double ease = Math.Sin(progress * Math.PI * 0.5);

                for (int i = 0; i < _gluedOffsets.Count; i++)
                {
                    _gluedOffsets[i] = new Vector(targets[i].X * ease, targets[i].Y * ease);
                }

                UpdateDragTransform();

                if (progress >= 1.0)
                {
                    _glueTimer?.Stop();
                    _glueTimer = null;
                }
            };
            _glueTimer.Start();
        }

        /// <summary>
        /// Updates the render transform of all dragged items according to current mouse position.
        /// </summary>
        private void UpdateDragTransform()
        {
            if (_currentDragged == null || _dragTransform == null || _itemsPanel == null || _lastPointerScreenPos == null) 
                return;

            var root = AssociatedObject?.GetVisualRoot() as Visual;
            if (root == null) return;

            var currentMouseVirtual = root.TranslatePoint(_lastPointerScreenPos.Value, _itemsPanel);
            if (!currentMouseVirtual.HasValue) return;

            double dx = currentMouseVirtual.Value.X - _dragStartMouseVirtual.X;
            double dy = currentMouseVirtual.Value.Y - _dragStartMouseVirtual.Y;

            for (int i = 0; i < _draggedTransforms.Count; i++)
            {
                _draggedTransforms[i].X = dx + _gluedOffsets[i].X;
                _draggedTransforms[i].Y = dy + _gluedOffsets[i].Y;
            }
        }

        /// <summary>
        /// Handles the pointer released event to finalize the drag operation.
        /// </summary>
        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_currentDragged == null || _dragTransform == null || AssociatedObject == null || !_isDragging)
                return;

            var capturedDragged = _currentDragged;
            var capturedIndices = _draggedIndices.ToList();
            var capturedToIndex = _lastValidSlotIndex;
            
            // Calculate visual target based on virtual position difference
            Point capturedLastValidSlotVirtual;
            if (capturedToIndex >= 0 && capturedToIndex < _itemDimensions.Count)
                capturedLastValidSlotVirtual = _itemDimensions[capturedToIndex].VirtualPosition;
            else
                capturedLastValidSlotVirtual = _dragStartItemVirtual;

            object? capturedSampleItem = null;
            if (AssociatedObject.ItemsSource is IList srcList && _currentDraggedIndex >= 0 && _currentDraggedIndex < srcList.Count)
                capturedSampleItem = srcList[_currentDraggedIndex];
            else if (AssociatedObject.Items is IList itemsList && _currentDraggedIndex >= 0 && _currentDraggedIndex < itemsList.Count)
                capturedSampleItem = itemsList[_currentDraggedIndex];

            Vector? savedOffset = _scrollViewer?.Offset;

            if (_hasDragged && capturedToIndex >= 0 && capturedIndices.Count > 0)
            {
                // Calculate the start of the block based on the primary item's rank in selection
                int primaryRank = capturedIndices.IndexOf(_currentDraggedIndex);
                int blockToIndex = Math.Max(0, capturedToIndex - primaryRank);

                var capturedContainers = _draggedContainers.ToList();
                var capturedTargetOffsets = _targetOffsetsRelPrimary.ToList();

                for (int i = 0; i < capturedContainers.Count; i++)
                {
                    var container = capturedContainers[i];
                    var relOffset = capturedTargetOffsets[i];
                    var itemStartP = _itemDimensions[capturedIndices[i]].VirtualPosition;

                    var targetP = new Point(capturedLastValidSlotVirtual.X + relOffset.X,
                                          capturedLastValidSlotVirtual.Y + relOffset.Y);

                    var finalDx = targetP.X - itemStartP.X;
                    var finalDy = targetP.Y - itemStartP.Y;

                    System.Action? onDone = (container == capturedDragged) ? () =>
                    {
                        try
                        {
                            TryReorderItems(capturedIndices, blockToIndex);
                            if (_scrollViewer != null && savedOffset.HasValue)
                            {
                                Dispatcher.UIThread.Post(() => {
                                    try { _scrollViewer.Offset = savedOffset.Value; } catch (Exception ex) { Log.Error("Error restoring scroll viewer offset", ex); }
                                }, DispatcherPriority.Background);
                            }
                            ExecuteDropCommand(capturedSampleItem, _currentDraggedIndex, capturedToIndex);
                        }
                        catch (Exception ex) { Log.Error("Error during drag drop collection update", ex); }
                        finally { StopAllAnimationsAndResetTransforms(); }
                    } : null;

                    AnimateTranslate(container, finalDx, finalDy, SwapAnimationDurationMs, onDone);
                }
            }
            else
            {
                StopAllAnimationsAndResetTransforms();
            }

            e.Pointer.Capture(null);
            _isDragging = false;
            _autoScrollTimer?.Stop();
            _targetAutoScrollSpeed = default;
            _autoScrollSpeed = default;
            _lastPointerScreenPos = null;
            _currentDraggedIndex = -1;
            _draggedContainers.Clear();
            _draggedIndices.Clear();
            _draggedTransforms.Clear();
            _gluedOffsets.Clear();
            _targetOffsetsRelPrimary.Clear();
            _currentDragged = null;
            _dragTransform = null;
            _lastValidSlotIndex = -1;
        }

        /// <summary>
        /// Animates a control to a target relative position.
        /// </summary>
        private void AnimateTranslate(Control control, double toX, double toY, int durationMs, System.Action? completed = null)
        {
            if (control.RenderTransform is not TranslateTransform t)
            {
                t = new TranslateTransform(0, 0);
                control.RenderTransform = t;
            }

            var targetPoint = new Point(toX, toY);
            if (_activeAnimations.TryGetValue(control, out var anim))
            {
                // If the target hasn't changed, let the existing animation continue
                if (Math.Abs(anim.Target.X - toX) < 0.1 && Math.Abs(anim.Target.Y - toY) < 0.1)
                    return;

                anim.Timer.Stop();
                _activeAnimations.Remove(control);
            }

            double fromX = t.X;
            double fromY = t.Y;
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) }; // High frequency for smoothness
            timer.Tick += (_, _) =>
            {
                var elapsed = sw.ElapsedMilliseconds;
                double progress = Math.Min(1.0, elapsed / (double)durationMs);
                
                // Sinusoidal out easing for very smooth motion
                double ease = Math.Sin(progress * Math.PI * 0.5);
                
                t.X = fromX + (toX - fromX) * ease;
                t.Y = fromY + (toY - fromY) * ease;

                if (progress >= 1.0)
                {
                    timer.Stop();
                    _activeAnimations.Remove(control);
                    completed?.Invoke();
                }
            };
            _activeAnimations[control] = (timer, targetPoint);
            timer.Start();
        }

        /// <summary>
        /// Animates containers that are affected by a potential swap.
        /// </summary>
        private void ShiftItemsForSwap(int targetIndex)
        {
            if (AssociatedObject == null || _currentDragged == null || _itemDimensions.Count == 0 || _draggedIndices.Count == 0) return;
            int itemsCount = AssociatedObject.Items.Count;
            
            var draggedSet = new HashSet<int>(_draggedIndices);
            int primaryPosInDragged = _draggedIndices.IndexOf(_currentDraggedIndex);
            int draggedCount = _draggedIndices.Count;

            for (int i = 0; i < itemsCount; i++)
            {
                if (draggedSet.Contains(i)) continue;
                var container = AssociatedObject.ContainerFromIndex(i);
                if (container == null) continue;

                Point targetOffset = default;

                // Shift non-dragged items to accommodate the dragged block
                int movedBefore = _draggedIndices.Count(idx => idx < i);
                int rankAmongNonDragged = i - movedBefore;
                int targetStart = targetIndex - primaryPosInDragged;

                int iNew = (rankAmongNonDragged < targetStart) ? rankAmongNonDragged : rankAmongNonDragged + draggedCount;

                if (iNew >= 0 && iNew < _itemDimensions.Count && iNew != i)
                {
                    var destPos = _itemDimensions[iNew].VirtualPosition;
                    var myOrig = _itemDimensions[i].VirtualPosition;
                    targetOffset = destPos - myOrig;
                }

                AnimateTranslate(container, targetOffset.X, targetOffset.Y, SwapAnimationDurationMs);
            }
        }

        /// <summary>
        /// Physically reorders the items in the ItemsSource or Items collection.
        /// </summary>
        private void TryReorderItems(List<int> fromIndices, int toIndex)
        {
            if (AssociatedObject == null || fromIndices.Count == 0) return;
            fromIndices.Sort();

            // Preserve selection to restore it after reordering
            var preservedSelection = new List<object>();
            try
            {
                if (AssociatedObject.SelectedItems is IList selList)
                {
                    foreach (var s in selList)
                        preservedSelection.Add(s!);
                }
            }
            catch (Exception ex) { Log.Error("Error preserving selection during reorder", ex); }

            var itemsSource = AssociatedObject.ItemsSource;
            if (itemsSource is IList list)
            {
                // Use Move method if supported for contiguous blocks to preserve notifications
                var listType = list.GetType();
                var moveMethod = listType.GetMethod("Move", new[] { typeof(int), typeof(int) });

                int start = fromIndices.First();
                int count = fromIndices.Count;
                bool contiguous = (fromIndices.Last() - start + 1) == count;

                if (moveMethod != null && contiguous)
                {
                    // Compute safe target start such that block fits in list
                    int adjustedTarget = Math.Clamp(toIndex, 0, Math.Max(0, list.Count - count));

                    if (adjustedTarget != start)
                    {
                        // Moving block downwards (towards larger indices)
                        if (adjustedTarget > start)
                        {
                            // For k in start+count .. adjustedTarget+count-1: move element at k to k-count
                            for (int k = start + count; k <= adjustedTarget + count - 1; k++)
                            {
                                try { moveMethod.Invoke(list, new object[] { k, k - count }); } catch (Exception ex) { Log.Error("Error during downward block move", ex); }
                            }
                        }
                        else // moving upwards
                        {
                            // For k in start-1 down to adjustedTarget: move element at k to k+count
                            for (int k = start - 1; k >= adjustedTarget; k--)
                            {
                                try { moveMethod.Invoke(list, new object[] { k, k + count }); } catch (Exception ex) { Log.Error("Error during upward block move", ex); }
                            }
                        }
                    }

                    // Restore selection by re-selecting the preserved objects (if present)
                    try
                    {
                        if (AssociatedObject.SelectedItems is IList selList2)
                        {
                            selList2.Clear();
                            foreach (var o in preservedSelection)
                            {
                                try { selList2.Add(o); } catch (Exception ex) { Log.Error("Error restoring item to selection after block move", ex); }
                            }
                        }
                    }
                    catch (Exception ex) { Log.Error("Error restoring selection after block move", ex); }

                    return;
                }

                // Fallback to remove and insert
                var movedItems = fromIndices.Select(i => list[i]).ToList();
                for (int i = fromIndices.Count - 1; i >= 0; i--)
                {
                    int removeIdx = fromIndices[i];
                    if (removeIdx >= 0 && removeIdx < list.Count)
                        list.RemoveAt(removeIdx);
                }

                int adjustedToIndex = Math.Clamp(toIndex, 0, list.Count);

                for (int i = 0; i < movedItems.Count; i++)
                    list.Insert(adjustedToIndex + i, movedItems[i]);

                // Restore selection by re-selecting the preserved objects (if present)
                try
                {
                    if (AssociatedObject.SelectedItems is IList selList2)
                    {
                        selList2.Clear();
                        foreach (var o in preservedSelection)
                        {
                            try { selList2.Add(o); } catch (Exception ex) { Log.Error("Error restoring item to selection after fallback move", ex); }
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Error restoring selection after fallback move", ex); }

                return;
            }

            if (AssociatedObject.Items is IList items)
            {
                var movedItems = fromIndices.Select(i => items[i]).ToList();
                for (int i = fromIndices.Count - 1; i >= 0; i--)
                {
                    int removeIdx = fromIndices[i];
                    if (removeIdx >= 0 && removeIdx < items.Count)
                        items.RemoveAt(removeIdx);
                }

                int adjustedToIndex = Math.Clamp(toIndex, 0, items.Count);

                for (int i = 0; i < movedItems.Count; i++)
                    items.Insert(adjustedToIndex + i, movedItems[i]);

                // Restore selection by re-selecting the preserved objects (if present)
                try
                {
                    if (AssociatedObject.SelectedItems is IList selList3)
                    {
                        selList3.Clear();
                        foreach (var o in preservedSelection)
                        {
                            try { selList3.Add(o); } catch (Exception ex) { Log.Error("Error restoring item to selection after items move", ex); }
                        }
                    }
                }
                catch (Exception ex) { Log.Error("Error restoring selection after items move", ex); }
            }
        }

        private void StopAllAnimationsAndResetTransforms()
        {
            foreach (var kv in _activeAnimations.ToArray())
            {
                try { kv.Value.Timer.Stop(); } catch (Exception ex) { Log.Error("Error stopping animation timer during reset", ex); }
            }
            _activeAnimations.Clear();

            if (AssociatedObject == null) return;

            int itemsCount = AssociatedObject.Items.Count;
            for (int i = 0; i < itemsCount; i++)
            {
                var c = AssociatedObject.ContainerFromIndex(i);
                if (c is Control control)
                {
                    control.RenderTransform = new TranslateTransform(0, 0);
                    control.ZIndex = 0;
                }
            }
        }

        private void ExecuteDropCommand(object? item, int fromIndex, int toIndex)
        {
            try
            {
                var cmd = DropCommand;
                if (cmd == null) return;
                var param = (item, fromIndex, toIndex);
                if (cmd.CanExecute(param))
                    cmd.Execute(param);
                else
                    cmd.Execute(param);
            }
            catch (Exception ex)
            {
                Log.Error("DropCommand execution error", ex);
            }
        }

        private void EstimateMissingDimensions()
        {
            if (AssociatedObject == null || _itemDimensions.Count == 0) return;

            // Find the first valid item to use as a baseline
            ItemDimension? anchor = null;
            foreach (var d in _itemDimensions)
                if (!d.IsEstimated && d.Bounds.Width > 0.001) { anchor = d; break; }

            if (anchor == null) return;

            // Determine orientation and step sizing in virtual space
            double stepX = 0, stepY = 0;
            double w = anchor.Bounds.Width;
            double h = anchor.Bounds.Height;

            ItemDimension? second = null;
            for (int i = anchor.Index + 1; i < _itemDimensions.Count; i++)
                if (!_itemDimensions[i].IsEstimated && _itemDimensions[i].Bounds.Width > 0.001) { second = _itemDimensions[i]; break; }

            if (second != null)
            {
                stepX = (second.VirtualPosition.X - anchor.VirtualPosition.X) / (second.Index - anchor.Index);
                stepY = (second.VirtualPosition.Y - anchor.VirtualPosition.Y) / (second.Index - anchor.Index);
            }
            else
            {
                var orientation = Orientation.Vertical;
                if (_itemsPanel is StackPanel sp) orientation = sp.Orientation;
                else if (_itemsPanel is VirtualizingStackPanel vsp) orientation = vsp.Orientation;
                else if (_itemsPanel is WrapPanel wp) orientation = wp.Orientation;

                if (orientation == Orientation.Horizontal) stepX = w; else stepY = h;
            }

            // Fill in virtual position for all items
            for (int i = 0; i < _itemDimensions.Count; i++)
            {
                var dim = _itemDimensions[i];
                if (dim.IsEstimated)
                {
                    dim.Index = i;
                    dim.Bounds = new Rect(0, 0, w, h);
                    dim.VirtualPosition = new Point(
                        anchor.VirtualPosition.X + (i - anchor.Index) * stepX,
                        anchor.VirtualPosition.Y + (i - anchor.Index) * stepY
                    );
                    dim.VirtualCenter = new Point(dim.VirtualPosition.X + w * 0.5, dim.VirtualPosition.Y + h * 0.5);
                }
            }
        }

        /// <summary>
        /// Updates the auto-scroll speed based on the pointer position.
        /// </summary>
        private void UpdateAutoScrollSpeed(Point pointerWindowPos)
        {
            if (_scrollViewer == null || AssociatedObject == null)
            {
                _targetAutoScrollSpeed = default;
                return;
            }

            // Get local position relative to ScrollViewer accurately
            var root = AssociatedObject.GetVisualRoot() as Visual;
            if (root == null) return;
            
            var localPosToSv = root.TranslatePoint(pointerWindowPos, _scrollViewer);
            if (!localPosToSv.HasValue) return;

            var localPos = localPosToSv.Value;

            double svWidth = _scrollViewer.Viewport.Width > 0 ? _scrollViewer.Viewport.Width : _scrollViewer.Bounds.Width;
            double svHeight = _scrollViewer.Viewport.Height > 0 ? _scrollViewer.Viewport.Height : _scrollViewer.Bounds.Height;
            
            // Adjust zones to be slightly smaller relative to Viewport if necessary
            double zoneX = Math.Min(AutoScrollZoneWidth, svWidth * 0.35);
            double zoneY = Math.Min(AutoScrollZoneWidth, svHeight * 0.35);

            double ratioX = 0, ratioY = 0;
            double dirX = 0, dirY = 0;
            double overshootX = 0, overshootY = 0;

            if (localPos.X < zoneX)
            {
                ratioX = Math.Clamp((zoneX - localPos.X) / zoneX, 0.0, 1.0);
                dirX = -1.0;
                overshootX = Math.Max(0.0, -localPos.X);
            }
            else if (localPos.X > svWidth - zoneX)
            {
                ratioX = Math.Clamp((localPos.X - (svWidth - zoneX)) / zoneX, 0.0, 1.0);
                dirX = 1.0;
                overshootX = Math.Max(0.0, localPos.X - svWidth);
            }

            if (localPos.Y < zoneY)
            {
                ratioY = Math.Clamp((zoneY - localPos.Y) / zoneY, 0.0, 1.0);
                dirY = -1.0;
                overshootY = Math.Max(0.0, -localPos.Y);
            }
            else if (localPos.Y > svHeight - zoneY)
            {
                ratioY = Math.Clamp((localPos.Y - (svHeight - zoneY)) / zoneY, 0.0, 1.0);
                dirY = 1.0;
                overshootY = Math.Max(0.0, localPos.Y - svHeight);
            }

            double mulX = 1.0 + Math.Clamp(overshootX / 150.0, 0.0, MaxAutoScrollSpeedMultiplier - 1.0);
            double mulY = 1.0 + Math.Clamp(overshootY / 150.0, 0.0, MaxAutoScrollSpeedMultiplier - 1.0);

            _targetAutoScrollSpeed = new Vector(
                dirX * MaxAutoScrollSpeed * mulX * Math.Pow(ratioX, 1.5), // More curve for fine control
                dirY * MaxAutoScrollSpeed * mulY * Math.Pow(ratioY, 1.5)
            );

            EnsureDraggedVisible();
        }

        private void EnsureDraggedVisible()
        {
            if (_currentDragged == null || _scrollViewer == null || AssociatedObject == null || _itemsPanel == null) return;

            // Determine item position relative to viewport considering scroll and transform
            var draggedInSv = _currentDragged.TranslatePoint(new Point(0, 0), _scrollViewer);
            if (!draggedInSv.HasValue) return;

            double svWidth = _scrollViewer.Viewport.Width > 0 ? _scrollViewer.Viewport.Width : _scrollViewer.Bounds.Width;
            double svHeight = _scrollViewer.Viewport.Height > 0 ? _scrollViewer.Viewport.Height : _scrollViewer.Bounds.Height;
            
            double width = _currentDragged.Bounds.Width;
            double height = _currentDragged.Bounds.Height;

            double boostX = 0, boostY = 0;
            // Adaptive boost: only if significantly out of bounds of the actual viewport.
            // Reduced intensity to prevent "scrolling fight".
            if (draggedInSv.Value.X < -20) boostX = -MaxAutoScrollSpeed * 0.5;
            else if (draggedInSv.Value.X + width > svWidth + 20) boostX = MaxAutoScrollSpeed * 0.5;

            if (draggedInSv.Value.Y < -20) boostY = -MaxAutoScrollSpeed * 0.5;
            else if (draggedInSv.Value.Y + height > svHeight + 20) boostY = MaxAutoScrollSpeed * 0.5;

            var totalTarget = _targetAutoScrollSpeed + new Vector(boostX, boostY);
            double maxM = MaxAutoScrollSpeed * MaxAutoScrollSpeedMultiplier;
            _targetAutoScrollSpeed = new Vector(
                Math.Clamp(totalTarget.X, -maxM, maxM),
                Math.Clamp(totalTarget.Y, -maxM, maxM)
            );
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (_scrollViewer == null) return;

            // Process auto-scroll speed while dragging
            if (_isDragging && _lastPointerScreenPos.HasValue)
            {
                if (Math.Abs(_autoScrollSpeed.Y) < AutoScrollStopThreshold && Math.Abs(_targetAutoScrollSpeed.Y) < AutoScrollStopThreshold)
                    _autoScrollSpeed = new Vector(_autoScrollSpeed.X, 0);
            }
            else
            {
                _autoScrollSpeed = new Vector(0, 0);
                _targetAutoScrollSpeed = new Vector(0, 0);
            }

            // Decay manual scroll delta smoothly
            double manualStepX = _manualScrollDelta.X * 0.22;
            double manualStepY = _manualScrollDelta.Y * 0.22;
            _manualScrollDelta = new Vector(_manualScrollDelta.X - manualStepX, _manualScrollDelta.Y - manualStepY);

            // Threshold to clean up small values
            if (Math.Abs(_manualScrollDelta.X) < 0.1) { manualStepX += _manualScrollDelta.X; _manualScrollDelta = new Vector(0, _manualScrollDelta.Y); }
            if (Math.Abs(_manualScrollDelta.Y) < 0.1) { manualStepY += _manualScrollDelta.Y; _manualScrollDelta = new Vector(_manualScrollDelta.X, 0); }

            // Stop the timer if there is no movement and not dragging
            bool hasMovement = Math.Abs(_autoScrollSpeed.X) > 0.001 || Math.Abs(_autoScrollSpeed.Y) > 0.001 
                            || Math.Abs(manualStepX) > 0.001 || Math.Abs(manualStepY) > 0.001;

            if (!hasMovement)
            {
                if (!_isDragging) _autoScrollTimer?.Stop();
                return;
            }

            var currentOffset = _scrollViewer.Offset;
            var maxScrollX = _scrollViewer.Extent.Width - _scrollViewer.Viewport.Width;
            var maxScrollY = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;

            // Combine movement
            var newX = Math.Max(0, Math.Min(currentOffset.X + _autoScrollSpeed.X + manualStepX, Math.Max(0, maxScrollX)));
            var newY = Math.Max(0, Math.Min(currentOffset.Y + _autoScrollSpeed.Y + manualStepY, Math.Max(0, maxScrollY)));

            if (Math.Abs(newX - currentOffset.X) > 0.001 || Math.Abs(newY - currentOffset.Y) > 0.001)
            {
                _scrollViewer.Offset = new Vector(newX, newY);
            }

            // Update drag state for swapping
            if (_isDragging)
            {
                UpdateDragTransform();
                UpdateSwapTarget();
            }
        }

        /// <summary>
        /// Identifies the potentially new swap target index based on the primary item's current position.
        /// </summary>
        private void UpdateSwapTarget()
        {
            if (_currentDragged == null || _dragTransform == null || AssociatedObject == null || !_isDragging || _itemsPanel == null)
                return;

            int itemsCount = AssociatedObject.Items.Count;
            if (_itemDimensions.Count != itemsCount) return;

            // Current virtual center of the dragged item
            var dragVirtual = new Point(
                _dragStartItemVirtual.X + _dragTransform.X + (_currentDragged.Bounds.Width * 0.5),
                _dragStartItemVirtual.Y + _dragTransform.Y + (_currentDragged.Bounds.Height * 0.5)
            );

            int bestTargetIndex = _currentDraggedIndex;
            double minDistanceSq = double.MaxValue;

            for (int i = 0; i < itemsCount; i++)
            {
                var dim = _itemDimensions[i];
                if (dim.IsEstimated)
                {
                    var c = AssociatedObject.ContainerFromIndex(i);
                    if (c is Control control && control.Bounds.Width > 0.001)
                    {
                        dim.Update(control, _itemsPanel, i);
                    }
                }

                if (dim.Bounds.Width <= 0.001) continue;

                double dx = dragVirtual.X - dim.VirtualCenter.X;
                double dy = dragVirtual.Y - dim.VirtualCenter.Y;
                double distSq = dx * dx + dy * dy;

                double coreWidth = dim.Bounds.Width * 0.42; 
                double coreHeight = dim.Bounds.Height * 0.42;
                
                bool isPastCenter = Math.Abs(dx) < coreWidth && Math.Abs(dy) < coreHeight;
                
                // Only consider swap if we are either inside the "core" or clearly the closest neighbor
                if (isPastCenter || (distSq < minDistanceSq && distSq < (Math.Pow(dim.Bounds.Width * 0.5, 2) + SwapHysteresisPx * SwapHysteresisPx)))
                {
                    minDistanceSq = distSq;
                    bestTargetIndex = i;
                }
            }

            if (bestTargetIndex != _lastValidSlotIndex)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastSwapTime).TotalMilliseconds >= SwapCooldownMs)
                {
                    _lastSwapTime = now;
                    _lastValidSlotIndex = bestTargetIndex;

                    ShiftItemsForSwap(bestTargetIndex);
                }
            }
        }

        /// <summary>
        /// Finds the ScrollViewer parent of a control.
        /// </summary>
        private ScrollViewer? FindScrollViewer(Visual control)
        {
            foreach (var ancestor in control.GetVisualAncestors())
            {
                if (ancestor is ScrollViewer ancestorSv) return ancestorSv;
            }

            if (AssociatedObject?.ItemsPanelRoot is Visual panelRoot)
            {
                foreach (var child in panelRoot.GetVisualAncestors().Concat(new[] { panelRoot }))
                {
                    if (child is ScrollViewer sv) return sv;
                }
            }

            foreach (var child in control.GetVisualChildren())
            {
                if (child is ScrollViewer sv) return sv;
                if (child is { } vchild)
                {
                    var result = FindScrollViewer(vchild);
                    if (result != null) return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to find the panel that hosts the list items.
        /// </summary>
        private Panel? FindItemsPanel(Visual control)
        {
            if (AssociatedObject?.ItemsPanelRoot is { } rootPanel) return rootPanel;

            foreach (var child in control.GetVisualChildren())
            {
                if (child is Panel panel && (panel.GetType().Name.Contains("StackPanel") || panel.GetType().Name.Contains("WrapPanel")))
                    return panel;

                if (child is { } vchild)
                {
                    var result = FindItemsPanel(vchild);
                    if (result != null) return result;
                }
            }

            return null;
        }
    }
}
