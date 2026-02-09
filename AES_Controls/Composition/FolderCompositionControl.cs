using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using AES_Controls.Helpers;

namespace AES_Controls.Composition;

/// <summary>
/// Represents a media item with an optional cover bitmap.
/// </summary>
public class MediaItem
{
    /// <summary>
    /// Gets or sets the cover bitmap for the media item.
    /// </summary>
    public Bitmap? CoverBitmap { get; set; }
}

/// <summary>
/// A control that displays a stack of fanned-out media items with interactive animations.
/// </summary>
public class FolderCompositionControl : Control
{
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
        Items = new AvaloniaList<MediaItem>();
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
            Items.CollectionChanged += OnItemsChanged;
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
            Items.CollectionChanged -= OnItemsChanged;
        _pointerOverSubscription?.Dispose();
        _pointerOverSubscription = null;
        _animTimer?.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _tgtPress = 0.94;
        StartAnimation();
        e.Handled = true;
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
        e.Handled = true;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateTargets(_isPointerOver);
        StartAnimation();
        InvalidateVisual();
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

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        bool any = false;
        double speed = 0.18; // interpolation factor

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

    private void UpdateTargets(bool spread)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Use control bounds with small padding
        double pad = Math.Min(bounds.Width, bounds.Height) * 0.05;
        var contentRect = new Rect(pad, pad, bounds.Width - pad * 2, bounds.Height - pad * 2);

        // Main item size within content rect
        double itemSize = Math.Min(contentRect.Width, contentRect.Height) * 0.90;

        int count = Items?.Count ?? 0;
        EnsureArraysSized(count);

        int validCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (Items != null && Items[i]?.CoverBitmap != null) validCount++;
        }

        // vStackOffset determines how much they fan out vertically
        double vStackOffset = itemSize * 0.25;

        // Base coordinates for the front-most item (centered bottom)
        double baseX = contentRect.X + (contentRect.Width - itemSize) / 2.0;
        double baseY = contentRect.Y + contentRect.Height - itemSize;

        int currentIdx = 0;
        for (int i = 0; i < count; i++)
        {
            if (Items != null && Items[i]?.CoverBitmap == null)
            {
                _tgtX[i] = baseX;
                _tgtY[i] = baseY;
                continue;
            }

            // How many items are in front of this one (0 for the front-most)
            int itemsInFront = (validCount - 1) - currentIdx;

            double targetX = baseX;
            // Fan upwards when spread
            double targetY = baseY - (spread ? (itemsInFront * vStackOffset) : 0);

            _tgtX[i] = targetX;
            _tgtY[i] = targetY;

            // initialize current if zero to avoid jump
            if (_curX[i] == 0 && _curY[i] == 0)
            {
                _curX[i] = _tgtX[i];
                _curY[i] = _tgtY[i];
            }
            currentIdx++;
        }
        // start animation towards targets
        StartAnimation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is AvaloniaList<MediaItem> old)
                old.CollectionChanged -= OnItemsChanged;
            if (change.NewValue is AvaloniaList<MediaItem> @new)
            @new.CollectionChanged += OnItemsChanged;
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

        // Ensure hit testing works everywhere
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, bounds.Width, bounds.Height));

        using (context.PushTransform(Matrix.CreateTranslation(-bounds.Width / 2, -bounds.Height / 2) * 
                                   Matrix.CreateScale(_curPress, _curPress) * 
                                   Matrix.CreateTranslation(bounds.Width / 2, bounds.Height / 2)))
        {
            // Use control bounds with small padding
            double pad = Math.Min(bounds.Width, bounds.Height) * 0.05;
            var contentRect = new Rect(pad, pad, bounds.Width - pad * 2, bounds.Height - pad * 2);

            // If there are no valid cover bitmaps, we're done
            if (Items == null || Items.Count == 0 || !Items.Any(it => it?.CoverBitmap != null))
                return;

            // Compute item size used for layout boxes
            double itemSize = Math.Min(contentRect.Width, contentRect.Height) * 0.90;
            int validCount = Items.Count(it => it?.CoverBitmap != null);

            // vStackOffset matches UpdateTargets
            double vStackOffset = itemSize * 0.25;

            // Draw stack from back (idx 0) to front (idx validCount-1)
            int count = Items.Count;
            int idx = 0;
            for (int i = 0; i < count; i++)
            {
                var item = Items[i];
                if (item?.CoverBitmap == null) continue;

                double x = (i < _curX.Length) ? _curX[i] : (contentRect.X + (contentRect.Width - itemSize) / 2.0);
                double y = (i < _curY.Length) ? _curY[i] : (contentRect.Y + contentRect.Height - itemSize);

                // Progress from 0 (back) to 1 (front)
                double progress = (validCount > 1) ? (double)idx / (validCount - 1) : 1.0;

                // Depth effect: scale and opacity
                double scale = 0.70 + (0.30 * progress);
                double opacity = 0.4 + (0.6 * progress);

                // Vertical scale factor to simulate tilt (rotation)
                double vScale = 0.85 + (0.15 * progress);

                double drawWidth = itemSize * scale;
                double drawHeight = itemSize * scale * vScale;

                // Center horizontally, but align bottom edge
                double drawX = x + (itemSize - drawWidth) / 2.0;
                double drawY = y + (itemSize - drawHeight);

                var dest = new Rect(drawX, drawY, drawWidth, drawHeight);
                var src = new Rect(0, 0, item.CoverBitmap.Size.Width, item.CoverBitmap.Size.Height);

                using (context.PushOpacity(opacity))
                {
                    context.DrawImage(item.CoverBitmap, src, dest);
                }
                idx++;
            }
        }
    }
}
