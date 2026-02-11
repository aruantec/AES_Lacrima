using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Collections.Specialized;
using System.Windows.Input;

namespace AES_Controls.Composition;

/// <summary>
/// A control that displays a stack of fanned-out media items with interactive animations.
/// </summary>
public class FolderCompositionControl : Control
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCompositionControl"/> class.
    /// </summary>
    /// <summary>
    /// Defines the <see cref="Items"/> property.
    /// </summary>
    public static readonly StyledProperty<AvaloniaList<MediaItem>> ItemsProperty =
        AvaloniaProperty.Register<FolderCompositionControl, AvaloniaList<MediaItem>>(nameof(Items));

    /// <summary>
    /// Gets or sets the list of media items to display.
    /// </summary>
    public AvaloniaList<MediaItem> Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="MaxVisibleCovers"/> property.
    /// </summary>
    public static readonly StyledProperty<int> MaxVisibleCoversProperty =
        AvaloniaProperty.Register<FolderCompositionControl, int>(nameof(MaxVisibleCovers), 5);

    /// <summary>
    /// Gets or sets the maximum number of visible covers to show.
    /// </summary>
    public int MaxVisibleCovers
    {
        get => GetValue(MaxVisibleCoversProperty);
        set => SetValue(MaxVisibleCoversProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="Command"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FolderCompositionControl, ICommand?>(nameof(Command));

    /// <summary>
    /// Gets or sets the command to execute when the control is clicked.
    /// </summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="CommandParameter"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<FolderCompositionControl, object?>(nameof(CommandParameter));

    /// <summary>
    /// Gets or sets the parameter to pass to the <see cref="Command"/>.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCompositionControl"/> class.
    /// </summary>
    public FolderCompositionControl()
    {
        // Ensure a collection instance exists to allow bindings to add items
        Items = [];
        // Animation timer for smooth transitions
        _animTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, OnAnimationTick);
    }

    private Avalonia.Threading.DispatcherTimer? _animTimer;
    private double[] _curX = Array.Empty<double>();
    private double[] _curY = Array.Empty<double>();
    private double[] _tgtX = Array.Empty<double>();
    private double[] _tgtY = Array.Empty<double>();
    private double _curPress = 1.0;
    private double _tgtPress = 1.0;
    private bool _isPointerOver;
    private IDisposable? _pointerOverSubscription;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Items != null)
        {
            Items.CollectionChanged += OnItemsChanged;
            foreach (var item in Items)
                if (item != null) item.PropertyChanged += OnItemPropertyChanged;
        }
        // Subscribe to IsPointerOver changes to handle hover interactions
        try
        {
            _pointerOverSubscription = this.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(new SimpleObserver<bool>(over => {
                    _isPointerOver = over;
                    UpdateTargets(over);
                    StartAnimation();
                }));
        }
        catch { }
        // initialize positions
        UpdateTargets(_isPointerOver);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Items != null)
        {
            Items.CollectionChanged -= OnItemsChanged;
            foreach (var item in Items)
                if (item != null) item.PropertyChanged -= OnItemPropertyChanged;
        }
        _pointerOverSubscription?.Dispose();
        _pointerOverSubscription = null;
        _animTimer?.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _tgtPress = 0.94;
        StartAnimation();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_tgtPress < 1.0)
        {
            _tgtPress = 1.0;
            StartAnimation();
            if (this.IsPointerOver)
            {
                if (Command?.CanExecute(CommandParameter) == true)
                    Command.Execute(CommandParameter);
            }
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.Cast<MediaItem>())
                    if (item != null) item.PropertyChanged -= OnItemPropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.Cast<MediaItem>())
                    if (item != null) item.PropertyChanged += OnItemPropertyChanged;
            }

            UpdateTargets(_isPointerOver);
            StartAnimation();
            InvalidateVisual();
        });
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.CoverBitmap))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateTargets(_isPointerOver);
                StartAnimation();
                InvalidateVisual();
            });
        }
    }

    // Use IsPointerOver observable rather than pointer event overrides to
    // remain compatible with the Avalonia version in use.

    private void StartAnimation()
    {
        if (_animTimer == null) return;
        if (!_animTimer.IsEnabled) _animTimer.Start();
    }

    private void StopAnimation()
    {
        if (_animTimer == null) return;
        if (_animTimer.IsEnabled) _animTimer.Stop();
    }

    /// <summary>
    /// Called on each animation timer tick to interpolate positions and press scale.
    /// </summary>
    private void OnAnimationTick(object? sender, EventArgs e)
    {
        bool any = false;
        double speed = 0.08; // interpolation factor

        double dp = _tgtPress - _curPress;
        if (Math.Abs(dp) > 0.001)
        {
            _curPress += dp * speed;
            any = true;
        }
        else _curPress = _tgtPress;

        int len = Math.Min(_curX.Length, _tgtX.Length);
        for (int i = 0; i < len; i++)
        {
            double dx = _tgtX[i] - _curX[i];
            double dy = _tgtY[i] - _curY[i];
            if (Math.Abs(dx) > 0.25 || Math.Abs(dy) > 0.25)
            {
                any = true;
                _curX[i] += dx * speed;
                _curY[i] += dy * speed;
            }
            else
            {
                _curX[i] = _tgtX[i];
                _curY[i] = _tgtY[i];
            }
        }
        InvalidateVisual();
        if (!any) StopAnimation();
    }

    /// <summary>
    /// Ensures internal arrays used to store current and target positions are sized
    /// to hold entries for <paramref name="count"/> items.
    /// </summary>
    /// <param name="count">The number of items to support.</param>
    private void EnsureArraysSized(int count)
    {
        if (_curX.Length != count)
        {
            _curX = new double[count];
            _curY = new double[count];
            _tgtX = new double[count];
            _tgtY = new double[count];
        }
    }

    /// <summary>
    /// Updates target positions for each item depending on whether the control is
    /// in the spread (hover) state or stacked state.
    /// </summary>
    /// <param name="spread">If true, items will be spread (fan-out); otherwise they will be stacked.</param>
    private void UpdateTargets(bool spread)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var items = Items;
        if (items == null) return;

        var contentRect = new Rect(0, 0, bounds.Width, bounds.Height);

        int maxVisible = Math.Max(1, MaxVisibleCovers);

        // Fill control space uniformly (square covers) so bounds are covered
        double itemSize = Math.Max(contentRect.Width, contentRect.Height);

        int count = items.Count;
        EnsureArraysSized(count);

        int actualValidCount = 0;
        for (int i = 0; i < count; i++)
        {
            var it = items[i];
            var cover = it?.CoverBitmap;
            // Healthy check
            if (cover != null && cover.Format != null)
            {
                actualValidCount++;
            }
        }

        int effectiveCount = Math.Min(actualValidCount, maxVisible);
        int skipValid = Math.Max(0, actualValidCount - maxVisible);

        // Stacked position (right aligned)
        double baseX_stacked = contentRect.Right - itemSize;
        // Fanning margin on hover (offset per shard)
        double marginHover = itemSize * 0.18;

        int validSeen = 0;
        for (int i = 0; i < count; i++)
        {
            var item = items[i];
            var cover = item?.CoverBitmap;

            // A cover is valid when it's present and its Format is available.
            bool valid = cover != null && cover.Format != null;

            if (!valid)
            {
                _tgtX[i] = baseX_stacked;
                _tgtY[i] = contentRect.Y;
                continue;
            }

            if (validSeen < skipValid)
            {
                // Hidden/skipped items stay at the furthest back position 
                _tgtX[i] = baseX_stacked;
                _tgtY[i] = contentRect.Y;
                validSeen++;
                continue;
            }

            // Index among visible items (0 is first visible/backmost, effectiveCount-1 is last/frontmost)
            int visibleIdx = validSeen - skipValid;

            // Target X: 
            // Normal (spread==false): Stacked right-aligned.
            // Hover (spread==true): Backmost stays fixed at right, others move left to reveal.
            double targetX;
            if (spread)
            {
                // Expand to the LEFT. Frontmost (indexInFront=0) moves to the left boundary.
                targetX = baseX_stacked - (visibleIdx * marginHover);
            }
            else
            {
                // Uniformly centered
                targetX = baseX_stacked;
            }

            _tgtX[i] = targetX;
            _tgtY[i] = contentRect.Y;

            if (_curX[i] == 0 && _curY[i] == 0)
            {
                _curX[i] = baseX_stacked;
                _curY[i] = contentRect.Y;
            }
            validSeen++;
        }
        StartAnimation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is AvaloniaList<MediaItem> old)
            {
                old.CollectionChanged -= OnItemsChanged;
                foreach (var item in old)
                    if (item != null) item.PropertyChanged -= OnItemPropertyChanged;
            }
            if (change.NewValue is AvaloniaList<MediaItem> @new)
            {
                @new.CollectionChanged += OnItemsChanged;
                foreach (var item in @new)
                    if (item != null) item.PropertyChanged += OnItemPropertyChanged;
            }
            UpdateTargets(_isPointerOver);
            InvalidateVisual();
        }
        else if (change.Property == MaxVisibleCoversProperty)
        {
            UpdateTargets(_isPointerOver);
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var items = Items;
        if (items == null || items.Count == 0) return;

        // Ensure hit testing works everywhere
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, bounds.Width, bounds.Height));

        using (context.PushTransform(Matrix.CreateTranslation(-bounds.Width / 2, -bounds.Height / 2) * 
                                   Matrix.CreateScale(_curPress, _curPress) * 
                                   Matrix.CreateTranslation(bounds.Width / 2, bounds.Height / 2)))
        {
            var contentRect = new Rect(0, 0, bounds.Width, bounds.Height);

            int maxVisible = Math.Max(1, MaxVisibleCovers);
            double itemSize = Math.Max(contentRect.Width, contentRect.Height);
            double baseX_stacked = contentRect.Right - itemSize;

            // Collect valid covers once to avoid issues if items or their properties change during rendering.
            // Includes the Format check requested to avoid Size property exceptions.
            var visibleCovers = new List<(Bitmap bitmap, Size size, int originalIndex)>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var cover = item?.CoverBitmap;
                if (cover != null && cover.Format != null)
                {
                    var s = cover.Size;
                    if (s != default && s.Width > 0 && s.Height > 0)
                        visibleCovers.Add((cover, s, i));
                }
            }

            int actualValidCount = visibleCovers.Count;
            if (actualValidCount == 0) return;

            int effectiveCount = Math.Min(actualValidCount, maxVisible);
            int skipValid = Math.Max(0, actualValidCount - maxVisible);

            // Draw stack from back to front
            int visibleCoversCount = visibleCovers.Count;
            int validSeen = 0;
            for (int i = 0; i < visibleCoversCount; i++)
            {
                var (cover, coverSize, originalIndex) = visibleCovers[i];
                if (cover == null) continue;

                if (validSeen < skipValid)
                {
                    validSeen++;
                    continue;
                }

                double x = (originalIndex < _curX.Length) ? _curX[originalIndex] : baseX_stacked;
                double y = (originalIndex < _curY.Length) ? _curY[originalIndex] : contentRect.Y;

                // Progress relative to the subset of items we are showing (back item is 0, front is 1.0)
                int visibleIdx = validSeen - skipValid;

                if (visibleIdx == 0)
                {
                    x = baseX_stacked;
                }

                double progress = (effectiveCount > 1) ? (double)visibleIdx / (effectiveCount - 1) : 1.0;
                progress = Math.Clamp(progress, 0.0, 1.0);

                double scale = 1.0;
                double opacity = 1.0;

                double drawWidth = itemSize * scale;
                double drawHeight = itemSize * scale;

                // Vertically center
                double drawX = x;
                double drawY = y + (itemSize - drawHeight) / 2.0;

                var dest = new Rect(drawX, drawY, drawWidth, drawHeight);
                var src = new Rect(0, 0, coverSize.Width, coverSize.Height);

                try
                {
                    using (context.PushOpacity(opacity))
                    {
                        context.DrawImage(cover, src, dest);
                    }
                }
                catch { /* Safely handle middle-of-render disposal */ }
                validSeen++;
            }
        }
    }
}
