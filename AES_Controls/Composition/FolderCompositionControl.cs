using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using System.Collections.Specialized;
using System.Numerics;
using System.Windows.Input;
using log4net;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// A control that displays a stack of fanned-out media items with interactive animations.
/// </summary>
public class FolderCompositionControl : Control, IScaleExclusionRenderTarget
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<FolderCompositionControl>();

    private CompositionCustomVisual? _visual;
    private readonly FolderAnimationSyncState _animationSync = new();
    private readonly HashSet<MediaItem> _subscribedItems = new();
    private double _tgtPress = 1.0;
    private bool _isPointerOver;
    private IDisposable? _pointerOverSubscription;

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
    /// Defines the <see cref="CoverStretch"/> property.
    /// </summary>
    public static readonly StyledProperty<Stretch> CoverStretchProperty =
        AvaloniaProperty.Register<FolderCompositionControl, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);

    /// <summary>
    /// Gets or sets how covers are fitted into the square tile.
    /// </summary>
    public Stretch CoverStretch
    {
        get => GetValue(CoverStretchProperty);
        set => SetValue(CoverStretchProperty, value);
    }

    /// <summary>
    /// When true, stops compositor-side folder animation (layout is snapped immediately).
    /// </summary>
    public static readonly StyledProperty<bool> IsAnimationPausedProperty =
        AvaloniaProperty.Register<FolderCompositionControl, bool>(nameof(IsAnimationPaused));

    public bool IsAnimationPaused
    {
        get => GetValue(IsAnimationPausedProperty);
        set => SetValue(IsAnimationPausedProperty, value);
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
    /// Defines the <see cref="FolderCoverItem"/> property.
    /// </summary>
    public static readonly StyledProperty<MediaItem?> FolderCoverItemProperty =
        AvaloniaProperty.Register<FolderCompositionControl, MediaItem?>(nameof(FolderCoverItem));

    /// <summary>
    /// Gets or sets the media item (usually the folder itself) to use for the cover 
    /// when the <see cref="Items"/> collection is empty.
    /// </summary>
    public MediaItem? FolderCoverItem
    {
        get => GetValue(FolderCoverItemProperty);
        set => SetValue(FolderCoverItemProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCompositionControl"/> class.
    /// </summary>
    public FolderCompositionControl()
    {
        Items = [];
    }

    /// <summary>
    /// True while fan/stack motion is still running on the compositor.
    /// </summary>
    public bool IsFolderAnimating => _animationSync.IsAnimating;

    /// <summary>
    /// Sets fan spread without requiring a pointer-over transition (used by tile handoff).
    /// </summary>
    public void SetSpread(bool spread)
    {
        if (_isPointerOver == spread)
            return;

        _isPointerOver = spread;
        _visual?.SendHandlerMessage(new FolderSpreadMessage(spread));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Items != null)
            Items.CollectionChanged += OnItemsChanged;

        try
        {
            _pointerOverSubscription = this.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(new SimpleObserver<bool>(over =>
                {
                    _isPointerOver = over;
                    _visual?.SendHandlerMessage(new FolderSpreadMessage(over));
                }));
        }
        catch (Exception ex)
        {
            Log.Warn("Error subscribing to IsPointerOver", ex);
        }

        var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        if (compositor != null)
        {
            _visual = compositor.CreateCustomVisual(new FolderCompositionVisualHandler());
            ElementComposition.SetElementChildVisual(this, _visual);
            _visual.SendHandlerMessage(new FolderAttachSyncMessage(_animationSync));
            UpdateVisualSize();
            SubscribeVisibleItems();
            _visual.SendHandlerMessage(new FolderAnimationPausedMessage(IsAnimationPaused));
            PushLayout(snap: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Items != null)
            Items.CollectionChanged -= OnItemsChanged;

        UnsubscribeAllItems();
        _pointerOverSubscription?.Dispose();
        _pointerOverSubscription = null;

        if (_visual != null)
        {
            _visual.SendHandlerMessage(null!);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }

    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateVisualSize();
        PushLayout(snap: IsAnimationPaused);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _tgtPress = 0.94;
        e.Pointer.Capture(this);
        _visual?.SendHandlerMessage(new FolderPressTargetMessage(_tgtPress));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);

        if (_tgtPress < 1.0)
        {
            _tgtPress = 1.0;
            _visual?.SendHandlerMessage(new FolderPressTargetMessage(_tgtPress));
            if (IsPointerOver && Command?.CanExecute(CommandParameter) == true)
                Command.Execute(CommandParameter);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _tgtPress = 1.0;
        _visual?.SendHandlerMessage(new FolderPressTargetMessage(_tgtPress));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SubscribeVisibleItems();
            PushLayout(snap: IsAnimationPaused);
        });
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaItem.CoverBitmap) || sender is not MediaItem item)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var items = Items;
            int index = items?.IndexOf(item) ?? -1;
            if (index < 0)
            {
                PushLayout(snap: IsAnimationPaused);
                return;
            }

            int count = items?.Count ?? 0;
            int startIndex = Math.Max(0, count - Math.Max(1, MaxVisibleCovers));
            int snapshotIndex = index - startIndex;
            if (snapshotIndex < 0)
            {
                PushLayout(snap: IsAnimationPaused);
                return;
            }

            var sk = CompositionBitmapHelper.ToSkImage(item.CoverBitmap, CompositionBitmapHelper.FolderCoverMaxEdge);
            _visual?.SendHandlerMessage(new FolderItemCoverMessage(snapshotIndex, sk));
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsAnimationPausedProperty)
        {
            _visual?.SendHandlerMessage(new FolderAnimationPausedMessage(change.GetNewValue<bool>()));
            if (!change.GetNewValue<bool>())
                PushLayout(snap: false);
            else
                PushLayout(snap: true);
        }
        else if (change.Property == ItemsProperty)
        {
            if (change.OldValue is AvaloniaList<MediaItem> old)
                old.CollectionChanged -= OnItemsChanged;
            if (change.NewValue is AvaloniaList<MediaItem> @new)
                @new.CollectionChanged += OnItemsChanged;

            UnsubscribeAllItems();
            SubscribeVisibleItems();
            PushLayout(snap: IsAnimationPaused);
        }
        else if (change.Property == MaxVisibleCoversProperty ||
                 change.Property == CoverStretchProperty ||
                 change.Property == DefaultCoverProperty ||
                 change.Property == FolderCoverItemProperty)
        {
            PushLayout(snap: IsAnimationPaused);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
            context.FillRectangle(Brushes.Transparent, new Rect(bounds.Size));
    }

    public void RefreshExclusionRenderSize()
    {
        UpdateVisualSize();
        PushLayout(snap: IsAnimationPaused);
    }

    private void UpdateVisualSize()
    {
        if (_visual == null)
            return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        _visual.Size = new Vector2((float)bounds.Width, (float)bounds.Height);
        _visual.SendHandlerMessage(new Vector2((float)bounds.Width, (float)bounds.Height));
    }

    private void PushLayout(bool snap)
    {
        if (_visual == null)
            return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        bool uniformToFill = CoverStretch != Stretch.Uniform;
        var snapshots = BuildSnapshots();
        var defaultSk = CompositionBitmapHelper.ToSkImage(DefaultCover, CompositionBitmapHelper.FolderCoverMaxEdge);

        _visual.SendHandlerMessage(new FolderLayoutRebuildMessage(
            snapshots,
            defaultSk,
            _isPointerOver,
            MaxVisibleCovers,
            uniformToFill,
            snap));
    }

    private List<FolderItemSnapshot> BuildSnapshots()
    {
        var list = new List<FolderItemSnapshot>();
        var folderItem = FolderCoverItem;
        var items = Items;
        int count = items?.Count ?? 0;

        if (count == 0 && folderItem != null)
        {
            list.Add(new FolderItemSnapshot(null, true));
            return list;
        }

        if (items == null)
            return list;

        int maxVisible = Math.Max(1, MaxVisibleCovers);
        int startIndex = Math.Max(0, count - maxVisible);
        for (int i = startIndex; i < count; i++)
        {
            var it = items[i];
            var sk = CompositionBitmapHelper.ToSkImage(it.CoverBitmap, CompositionBitmapHelper.FolderCoverMaxEdge);
            list.Add(new FolderItemSnapshot(sk, false));
        }

        return list;
    }

    private void SubscribeVisibleItems()
    {
        var items = Items;
        if (items == null)
            return;

        int count = items.Count;
        int startIndex = Math.Max(0, count - Math.Max(1, MaxVisibleCovers));
        for (int i = startIndex; i < count; i++)
        {
            var it = items[i];
            if (_subscribedItems.Add(it))
                it.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void UnsubscribeAllItems()
    {
        foreach (var it in _subscribedItems)
            it.PropertyChanged -= OnItemPropertyChanged;
        _subscribedItems.Clear();
    }

}
