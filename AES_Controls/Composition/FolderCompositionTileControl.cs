using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Input;
using log4net;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Shows a cheap static folder snapshot by default; mounts a live <see cref="FolderCompositionControl"/>
/// only while the pointer is over the tile (at most one compositor per hovered album).
/// </summary>
public class FolderCompositionTileControl : Grid
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<FolderCompositionTileControl>();

    private static FolderCompositionTileControl? _activeLiveTile;
    private static bool _albumListScrollFrozen;

    private const int ClosePollMs = 16;
    private const int CloseTimeoutMs = 750;

    private readonly Image _previewImage;
    private readonly FolderCompositionControl _liveFolder;
    private readonly HashSet<MediaItem> _subscribedItems = new();

    private bool _isLiveActive;
    private bool _isClosing;
    private bool _snapshotQueued;
    private int _snapshotGeneration;
    private int _liveActivationGeneration;
    private MediaItem? _subscribedFolderCoverItem;
    private IDisposable? _pointerOverSubscription;
    private DispatcherTimer? _closePollTimer;
    private long _closeStartedTicks;

    public static readonly StyledProperty<AvaloniaList<MediaItem>> ItemsProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, AvaloniaList<MediaItem>>(nameof(Items));

    public AvaloniaList<MediaItem> Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public static readonly StyledProperty<int> MaxVisibleCoversProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, int>(nameof(MaxVisibleCovers), 5);

    public int MaxVisibleCovers
    {
        get => GetValue(MaxVisibleCoversProperty);
        set => SetValue(MaxVisibleCoversProperty, value);
    }

    public static readonly StyledProperty<Stretch> CoverStretchProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, Stretch>(nameof(CoverStretch), Stretch.UniformToFill);

    public Stretch CoverStretch
    {
        get => GetValue(CoverStretchProperty);
        set => SetValue(CoverStretchProperty, value);
    }

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, ICommand?>(nameof(Command));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, object?>(nameof(CommandParameter));

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public static readonly StyledProperty<Bitmap?> DefaultCoverProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, Bitmap?>(nameof(DefaultCover));

    public Bitmap? DefaultCover
    {
        get => GetValue(DefaultCoverProperty);
        set => SetValue(DefaultCoverProperty, value);
    }

    public static readonly StyledProperty<MediaItem?> FolderCoverItemProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, MediaItem?>(nameof(FolderCoverItem));

    public MediaItem? FolderCoverItem
    {
        get => GetValue(FolderCoverItemProperty);
        set => SetValue(FolderCoverItemProperty, value);
    }

    /// <summary>
    /// When true, keeps the static snapshot even while the pointer is over the tile
    /// (e.g. while the ROM carousel is scrolling).
    /// </summary>
    public static readonly StyledProperty<bool> ForceStaticPreviewProperty =
        AvaloniaProperty.Register<FolderCompositionTileControl, bool>(nameof(ForceStaticPreview));

    public bool ForceStaticPreview
    {
        get => GetValue(ForceStaticPreviewProperty);
        set => SetValue(ForceStaticPreviewProperty, value);
    }

    /// <summary>
    /// Synchronous scroll lock set by the album list before layout/pointer-over updates.
    /// </summary>
    public static void SetAlbumListScrollFrozen(bool frozen)
    {
        if (_albumListScrollFrozen == frozen)
            return;

        _albumListScrollFrozen = frozen;
        if (frozen)
            _activeLiveTile?.ForceStaticImmediate();
    }

    private static bool IsHoverLiveSuppressed => _albumListScrollFrozen;

    public FolderCompositionTileControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _previewImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        _liveFolder = new FolderCompositionControl
        {
            IsVisible = false,
            ClipToBounds = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsAnimationPaused = false
        };

        Children.Add(_previewImage);
        Items = [];
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Items != null)
            Items.CollectionChanged += OnItemsCollectionChanged;

        SubscribeFolderCoverItem(FolderCoverItem);
        SubscribeVisibleItems();
        QueueSnapshotRefresh();

        try
        {
            _pointerOverSubscription = this.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(new SimpleObserver<bool>(UpdateLiveState));
        }
        catch (Exception ex)
        {
            Log.Warn("Error subscribing to IsPointerOver", ex);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (Items != null)
            Items.CollectionChanged -= OnItemsCollectionChanged;

        UnsubscribeAllItems();
        UnsubscribeFolderCoverItem();
        _pointerOverSubscription?.Dispose();
        _pointerOverSubscription = null;

        CancelScheduledDeactivate();
        CompleteDeactivate(force: true);
        _snapshotGeneration++;

        if (_activeLiveTile == this)
            _activeLiveTile = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        QueueSnapshotRefresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ForceStaticPreviewProperty)
        {
            if (change.GetNewValue<bool>())
                ForceStaticImmediate();
            else
                UpdateLiveState(IsPointerOver);
            return;
        }

        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is AvaloniaList<MediaItem> old)
                old.CollectionChanged -= OnItemsCollectionChanged;
            if (change.NewValue is AvaloniaList<MediaItem> @new)
                @new.CollectionChanged += OnItemsCollectionChanged;

            UnsubscribeAllItems();
            SubscribeVisibleItems();
            QueueSnapshotRefresh();
            SyncLiveFolderProperties();
            return;
        }

        if (change.Property == FolderCoverItemProperty)
        {
            SubscribeFolderCoverItem(change.NewValue as MediaItem);
            QueueSnapshotRefresh();
            SyncLiveFolderProperties();
            return;
        }

        if (change.Property == MaxVisibleCoversProperty ||
            change.Property == CoverStretchProperty ||
            change.Property == DefaultCoverProperty ||
            change.Property == CommandProperty ||
            change.Property == CommandParameterProperty)
        {
            QueueSnapshotRefresh();
            SyncLiveFolderProperties();
        }
    }

    private bool IsLiveInteractionSuppressed =>
        ForceStaticPreview || IsHoverLiveSuppressed;

    private void UpdateLiveState(bool isPointerOver)
    {
        if (IsLiveInteractionSuppressed)
        {
            ForceStaticImmediate();
            return;
        }

        if (isPointerOver)
        {
            CancelScheduledDeactivate();
            ActivateLiveFolder();
            return;
        }

        if (_isLiveActive || _isClosing)
            BeginCloseAnimation();
    }

    private void ForceStaticImmediate()
    {
        _liveActivationGeneration++;
        CancelScheduledDeactivate();

        if (_activeLiveTile == this)
            _activeLiveTile = null;

        if (!_isLiveActive)
            return;

        _isLiveActive = false;
        _isClosing = false;
        _liveFolder.SetSpread(false);

        if (Children.Contains(_liveFolder))
            Children.Remove(_liveFolder);

        _liveFolder.IsVisible = false;
        _liveFolder.Opacity = 1;
        _previewImage.IsVisible = true;
        _previewImage.Opacity = 1;
    }

    private void ActivateLiveFolder()
    {
        if (IsLiveInteractionSuppressed)
        {
            ForceStaticImmediate();
            return;
        }

        if (_activeLiveTile != null && _activeLiveTile != this)
            _activeLiveTile.BeginCloseAnimation();

        _activeLiveTile = this;
        CancelScheduledDeactivate();
        _isClosing = false;

        if (!_isLiveActive)
        {
            _isLiveActive = true;
            SyncLiveFolderProperties();

            if (!Children.Contains(_liveFolder))
                Children.Add(_liveFolder);

            _liveFolder.Opacity = 1;
            _liveFolder.IsVisible = true;
            _previewImage.IsVisible = false;

            // Start stacked, then fan open on the next frame so open/close use the same motion path.
            _liveFolder.SetSpread(false);
            int generation = ++_liveActivationGeneration;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (generation != _liveActivationGeneration ||
                        IsLiveInteractionSuppressed ||
                        !_isLiveActive)
                        return;

                    _liveFolder.SetSpread(true);
                },
                DispatcherPriority.Render);
        }
        else
        {
            _liveFolder.SetSpread(true);
        }
    }

    private void BeginCloseAnimation()
    {
        if (IsLiveInteractionSuppressed)
        {
            ForceStaticImmediate();
            return;
        }

        if (!_isLiveActive || _isClosing)
            return;

        _isClosing = true;
        _closeStartedTicks = Stopwatch.GetTimestamp();
        _liveFolder.SetSpread(false);
        StartClosePoll();
    }

    private void StartClosePoll()
    {
        _closePollTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClosePollMs) };
        _closePollTimer.Tick -= OnClosePollTick;
        _closePollTimer.Tick += OnClosePollTick;
        if (!_closePollTimer.IsEnabled)
            _closePollTimer.Start();
    }

    private void CancelScheduledDeactivate()
    {
        _closePollTimer?.Stop();
        _isClosing = false;
    }

    private void OnClosePollTick(object? sender, EventArgs e)
    {
        if (IsLiveInteractionSuppressed)
        {
            ForceStaticImmediate();
            return;
        }

        if (IsPointerOver)
        {
            CancelScheduledDeactivate();
            ActivateLiveFolder();
            return;
        }

        double elapsedMs = (Stopwatch.GetTimestamp() - _closeStartedTicks) * 1000.0 / Stopwatch.Frequency;
        if (!_liveFolder.IsFolderAnimating || elapsedMs >= CloseTimeoutMs)
        {
            CancelScheduledDeactivate();
            CompleteDeactivate(force: false);
        }
    }

    private void CompleteDeactivate(bool force)
    {
        if (!_isLiveActive && !force)
            return;

        _isLiveActive = false;
        _isClosing = false;

        if (Children.Contains(_liveFolder))
            Children.Remove(_liveFolder);

        _liveFolder.IsVisible = false;
        _liveFolder.Opacity = 1;
        _previewImage.IsVisible = true;
        _previewImage.Opacity = 1;

        if (_activeLiveTile == this)
            _activeLiveTile = null;

        QueueSnapshotRefresh();
    }

    private void SyncLiveFolderProperties()
    {
        if (!_isLiveActive)
            return;

        _liveFolder.Items = Items;
        _liveFolder.MaxVisibleCovers = MaxVisibleCovers;
        _liveFolder.CoverStretch = CoverStretch;
        _liveFolder.DefaultCover = DefaultCover;
        _liveFolder.FolderCoverItem = FolderCoverItem;
        _liveFolder.Command = Command;
        _liveFolder.CommandParameter = CommandParameter;
        _liveFolder.IsAnimationPaused = false;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SubscribeVisibleItems();
            QueueSnapshotRefresh();
            SyncLiveFolderProperties();
        });
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.CoverBitmap))
            QueueSnapshotRefresh();
    }

    private void OnFolderCoverItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.CoverBitmap))
            QueueSnapshotRefresh();
    }

    private void QueueSnapshotRefresh()
    {
        if (_snapshotQueued)
            return;

        _snapshotQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _snapshotQueued = false;
            await RefreshSnapshotAsync().ConfigureAwait(true);
        }, DispatcherPriority.Background);
    }

    private async Task RefreshSnapshotAsync()
    {
        if (VisualRoot == null)
            return;

        var bounds = Bounds;
        int width = bounds.Width > 0 ? (int)Math.Round(bounds.Width) : 240;
        int height = bounds.Height > 0 ? (int)Math.Round(bounds.Height) : 170;
        if (width <= 0 || height <= 0)
            return;

        int generation = ++_snapshotGeneration;

        var items = Items;
        var folderCoverItem = FolderCoverItem;
        var defaultCover = DefaultCover;
        int maxVisible = MaxVisibleCovers;
        bool uniformToFill = CoverStretch != Stretch.Uniform;

        var capturedLayers = await Dispatcher.UIThread.InvokeAsync(() =>
            FolderStackSnapshotRenderer.CaptureLayers(items, folderCoverItem, defaultCover, maxVisible));

        if (generation != _snapshotGeneration || capturedLayers == null)
            return;

        SKBitmap? rendered = await Task.Run(() =>
            FolderStackSnapshotRenderer.RenderCaptured(capturedLayers, uniformToFill, width, height))
            .ConfigureAwait(false);

        if (generation != _snapshotGeneration || rendered == null)
        {
            rendered?.Dispose();
            return;
        }

        try
        {
            var bitmap = ConvertToBitmap(rendered);
            if (generation != _snapshotGeneration)
                return;

            await Dispatcher.UIThread.InvokeAsync(() => _previewImage.Source = bitmap);
        }
        finally
        {
            rendered.Dispose();
        }
    }

    private static Bitmap ConvertToBitmap(SKBitmap skBmp)
    {
        using var image = SKImage.FromBitmap(skBmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return Bitmap.DecodeToWidth(stream, skBmp.Width);
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

    private void SubscribeFolderCoverItem(MediaItem? item)
    {
        UnsubscribeFolderCoverItem();
        _subscribedFolderCoverItem = item;
        if (item != null)
            item.PropertyChanged += OnFolderCoverItemPropertyChanged;
    }

    private void UnsubscribeFolderCoverItem()
    {
        if (_subscribedFolderCoverItem != null)
            _subscribedFolderCoverItem.PropertyChanged -= OnFolderCoverItemPropertyChanged;
        _subscribedFolderCoverItem = null;
    }
}
