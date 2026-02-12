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
    private class ItemState
    {
        public MediaItem Item { get; }
        public double CurX { get; set; }
        public double CurY { get; set; }
        public double CurOpacity { get; set; }
        public double CoverFade { get; set; }
        public double TgtX { get; set; }
        public double TgtY { get; set; }
        public double TgtOpacity { get; set; }
        public int ZIndex { get; set; }
        public bool IsTarget { get; set; }
        private Bitmap? _lastBitmap;

        public ItemState(MediaItem item, double x, double y)
        {
            Item = item;
            CurX = x;
            CurY = y;
            TgtX = x;
            TgtY = y;
            CurOpacity = 0;
            TgtOpacity = 1;
            _lastBitmap = item.CoverBitmap;
            CoverFade = (_lastBitmap != null) ? 1.0 : 0.0;
        }

        public bool Update(double speed)
        {
            bool any = false;
            double dx = TgtX - CurX;
            double dy = TgtY - CurY;
            double dop = TgtOpacity - CurOpacity;

            if (Math.Abs(dx) > 0.1 || Math.Abs(dy) > 0.1)
            {
                CurX += dx * speed;
                CurY += dy * speed;
                any = true;
            }
            else
            {
                CurX = TgtX;
                CurY = TgtY;
            }

            if (Math.Abs(dop) > 0.005)
            {
                CurOpacity += dop * speed;
                any = true;
            }
            else
            {
                CurOpacity = TgtOpacity;
            }

            double targetFade = (Item.CoverBitmap != null) ? 1.0 : 0.0;

            // Detect bitmap change to trigger a fresh fade-in
            if (Item.CoverBitmap != _lastBitmap)
            {
                _lastBitmap = Item.CoverBitmap;
                if (CoverFade > 0.1) CoverFade = 0.0; // Reset for a nice fade-in
            }

            double df = targetFade - CoverFade;
            if (Math.Abs(df) > 0.005)
            {
                CoverFade += df * (speed * 0.75); // Slightly slower fade for covers
                any = true;
            }
            else
            {
                CoverFade = targetFade;
            }

            return any;
        }
    }

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
    /// Defines the <see cref="DefaultCover"/> property.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> DefaultCoverProperty =
        AvaloniaProperty.Register<FolderCompositionControl, Bitmap?>(nameof(DefaultCover));

    /// <summary>
    /// Gets or sets the default cover bitmap to show when an item has no cover.
    /// </summary>
    public Bitmap? DefaultCover
    {
        get => GetValue(DefaultCoverProperty);
        set => SetValue(DefaultCoverProperty, value);
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
    private readonly List<ItemState> _activeStates = new();
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
        }
        // Subscribe to IsPointerOver changes to handle hover interactions
        try
        {
            _pointerOverSubscription = this.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(new SimpleObserver<bool>(over => {
                    _isPointerOver = over;
                    UpdateTargets(over);
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
        }
        foreach (var state in _activeStates)
            state.Item.PropertyChanged -= OnItemPropertyChanged;
        _activeStates.Clear();

        _pointerOverSubscription?.Dispose();
        _pointerOverSubscription = null;
        _animTimer?.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _tgtPress = 0.94;
        e.Pointer.Capture(this);
        StartAnimation();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);

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

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _tgtPress = 1.0;
        StartAnimation();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            UpdateTargets(_isPointerOver);
        });
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.CoverBitmap))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                UpdateTargets(_isPointerOver);
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
        double speed = 0.12; // interpolation factor

        double dp = _tgtPress - _curPress;
        if (Math.Abs(dp) > 0.001)
        {
            _curPress += dp * speed;
            any = true;
        }
        else _curPress = _tgtPress;

        for (int i = _activeStates.Count - 1; i >= 0; i--)
        {
            var state = _activeStates[i];
            bool stateMoving = state.Update(speed);
            any |= stateMoving;

            if (!stateMoving && !state.IsTarget && state.CurOpacity <= 0.005)
            {
                state.Item.PropertyChanged -= OnItemPropertyChanged;
                _activeStates.RemoveAt(i);
            }
        }
        InvalidateVisual();
        if (!any) StopAnimation();
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
        if (items == null)
        {
             foreach (var state in _activeStates)
                state.Item.PropertyChanged -= OnItemPropertyChanged;
            _activeStates.Clear();
            return;
        }

        var contentRect = new Rect(0, 0, bounds.Width, bounds.Height);
        int maxVisible = Math.Max(1, MaxVisibleCovers);
        double itemSize = Math.Max(contentRect.Width, contentRect.Height);
        double baseX_stacked = contentRect.Right - itemSize;
        double marginHover = itemSize * 0.18;

        // Identifty items to show (up to maxVisible items from the end)
        var targetedItems = new List<MediaItem>();
        int count = items.Count;
        int startIndex = Math.Max(0, count - maxVisible);
        for (int i = startIndex; i < count; i++)
        {
            var it = items[i];
            if (it != null)
                targetedItems.Add(it);
        }

        foreach (var s in _activeStates) s.IsTarget = false;

        for (int i = 0; i < targetedItems.Count; i++)
        {
            var it = targetedItems[i];
            var state = _activeStates.Find(s => s.Item == it);

            double tx = spread ? (baseX_stacked - (i * marginHover)) : baseX_stacked;
            double ty = contentRect.Y;

            if (state == null)
            {
                state = new ItemState(it, baseX_stacked, ty) { IsTarget = true, ZIndex = i };
                state.Item.PropertyChanged += OnItemPropertyChanged;
                _activeStates.Add(state);
            }
            else
            {
                state.IsTarget = true;
                state.TgtX = tx;
                state.TgtY = ty;
                state.TgtOpacity = 1.0;
                state.ZIndex = i;
            }
        }

        foreach (var s in _activeStates)
        {
            if (!s.IsTarget) s.TgtOpacity = 0;
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
                foreach (var state in _activeStates)
                    state.Item.PropertyChanged -= OnItemPropertyChanged;
                _activeStates.Clear();
            }
            if (change.NewValue is AvaloniaList<MediaItem> @new)
            {
                @new.CollectionChanged += OnItemsChanged;
            }
            UpdateTargets(_isPointerOver);
        }
        else if (change.Property == MaxVisibleCoversProperty || change.Property == BoundsProperty || change.Property == DefaultCoverProperty)
        {
            UpdateTargets(_isPointerOver);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (_activeStates.Count == 0) return;

        // Ensure hit testing works everywhere
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, bounds.Width, bounds.Height));

        using (context.PushTransform(Matrix.CreateTranslation(-bounds.Width / 2, -bounds.Height / 2) * 
                                   Matrix.CreateScale(_curPress, _curPress) * 
                                   Matrix.CreateTranslation(bounds.Width / 2, bounds.Height / 2)))
        {
            var contentRect = new Rect(0, 0, bounds.Width, bounds.Height);
            double itemSize = Math.Max(contentRect.Width, contentRect.Height);
            var defaultCover = DefaultCover;

            // Draw stack from back to front based on ZIndex
            var sorted = _activeStates.OrderBy(s => s.ZIndex).ToList();
            foreach (var state in sorted)
            {
                if (state.CurOpacity <= 0.001) continue;

                double drawWidth = itemSize;
                double drawHeight = itemSize;
                double drawX = state.CurX;
                double drawY = state.CurY + (itemSize - drawHeight) / 2.0;
                var dest = new Rect(drawX, drawY, drawWidth, drawHeight);

                try
                {
                    using (context.PushOpacity(state.CurOpacity))
                    {
                        // 1. Draw Default Cover if available
                        if (defaultCover != null)
                        {
                            var defSize = defaultCover.Size;
                            var defSrc = new Rect(0, 0, defSize.Width, defSize.Height);
                            context.DrawImage(defaultCover, defSrc, dest);
                        }

                        // 2. Draw Actual Cover with fade
                        var cover = state.Item.CoverBitmap;
                        if (cover != null && cover.Format != null && state.CoverFade > 0.001)
                        {
                            var coverSize = cover.Size;
                            if (coverSize != default && coverSize.Width > 0 && coverSize.Height > 0)
                            {
                                var src = new Rect(0, 0, coverSize.Width, coverSize.Height);
                                using (context.PushOpacity(state.CoverFade))
                                {
                                    context.DrawImage(cover, src, dest);
                                }
                            }
                        }
                    }
                }
                catch { /* Safely handle middle-of-render disposal */ }
            }
        }
    }
}
