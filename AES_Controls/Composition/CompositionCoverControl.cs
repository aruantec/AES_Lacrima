using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Mpv.Player;
using SkiaSharp;

namespace AES_Controls.Composition;

/// <summary>
/// Unified host for carousel and card-grid cover layouts with cross-fade mode transitions.
/// </summary>
public class CompositionCoverControl : Panel, IScaleExclusionRenderTarget
{
    private const int GameplayPreviewRenderDimension = 720;

    private static readonly TimeSpan LayoutTransitionDuration = TimeSpan.FromMilliseconds(280);

    private readonly CompositionCarouselControl _carousel;
    private readonly CompositionCardGridControl _cardGrid;
    private readonly CompositionSharedCoverCache _sharedCoverCache = new();
    private readonly CompositionSharedCoverCache _cardDisplayCache = new();
    private CancellationTokenSource? _transitionCts;
    private DispatcherTimer? _coverVisualSyncTimer;
    private CoverLayoutMode _appliedLayoutMode = CoverLayoutMode.Carousel;
    private Rect _selectedItemBounds;
    private bool _suppressLayoutTransition;
    private bool _syncingSelectedIndexFromCarousel;
    private readonly VideoViewControl _gameplayPreviewVideo;
    private SKImage? _pendingGameplayPreviewFrame;
    private bool _gameplayPreviewFramePostScheduled;

    public static readonly StyledProperty<CoverLayoutMode> LayoutModeProperty =
        AvaloniaProperty.Register<CompositionCoverControl, CoverLayoutMode>(
            nameof(LayoutMode),
            CoverLayoutMode.Carousel,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<CompositionCoverControl, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<double> SelectedIndexProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(
            nameof(SelectedIndex),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double?> ViewportPreviewIndexProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double?>(
            nameof(ViewportPreviewIndex),
            defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    public static readonly StyledProperty<int> PointedItemIndexProperty =
        AvaloniaProperty.Register<CompositionCoverControl, int>(
            nameof(PointedItemIndex),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> PlayingItemIndexProperty =
        AvaloniaProperty.Register<CompositionCoverControl, int>(nameof(PlayingItemIndex), -1);

    public static readonly StyledProperty<string?> ImageBitmapPropertyProperty =
        AvaloniaProperty.Register<CompositionCoverControl, string?>(nameof(ImageBitmapProperty));

    public static readonly StyledProperty<string?> ImageFileNamePropertyProperty =
        AvaloniaProperty.Register<CompositionCoverControl, string?>(nameof(ImageFileNameProperty));

    public static readonly StyledProperty<string?> TitlePropertyProperty =
        AvaloniaProperty.Register<CompositionCoverControl, string?>(nameof(TitleProperty));

    public static readonly StyledProperty<ICommand?> ItemDoubleClickedCommandProperty =
        AvaloniaProperty.Register<CompositionCoverControl, ICommand?>(nameof(ItemDoubleClickedCommand));

    public static readonly StyledProperty<ICommand?> ItemSelectedCommandProperty =
        AvaloniaProperty.Register<CompositionCoverControl, ICommand?>(nameof(ItemSelectedCommand));

    public static readonly StyledProperty<bool> ShowCoverFoundOverlayProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(ShowCoverFoundOverlay));

    public static readonly StyledProperty<bool> PublishSelectedItemBoundsProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(PublishSelectedItemBounds));

    public static readonly StyledProperty<int> GameplayPreviewItemIndexProperty =
        AvaloniaProperty.Register<CompositionCoverControl, int>(nameof(GameplayPreviewItemIndex), -1);

    public static readonly StyledProperty<bool> IsGameplayPreviewVisibleProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(IsGameplayPreviewVisible));

    public static readonly StyledProperty<bool> IsGameplayPreviewVideoVisibleProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(IsGameplayPreviewVideoVisible));

    public static readonly StyledProperty<AesMpvPlayer?> GameplayPreviewPlayerProperty =
        AvaloniaProperty.Register<CompositionCoverControl, AesMpvPlayer?>(nameof(GameplayPreviewPlayer));

    public static readonly StyledProperty<bool> PauseLoadingSpinnerAnimationProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(PauseLoadingSpinnerAnimation));

    public static readonly StyledProperty<bool> IsContentLoadingProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(IsContentLoading));

    public static readonly StyledProperty<double> GlobalOpacityProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(GlobalOpacity), 1.0);

    public static readonly StyledProperty<double> GridOpacityMultiplierProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(GridOpacityMultiplier), 1.0);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(ItemSpacing), 0.93);

    public static readonly StyledProperty<double> ItemScaleProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(ItemScale), 1.0);

    public static readonly StyledProperty<double> VerticalOffsetProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(VerticalOffset));

    public static readonly StyledProperty<double> SideTranslationProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(SideTranslation));

    public static readonly StyledProperty<double> StackSpacingProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(StackSpacing));

    public static readonly StyledProperty<bool> UseFullCoverSizeProperty =
        AvaloniaProperty.Register<CompositionCoverControl, bool>(nameof(UseFullCoverSize));

    public static readonly StyledProperty<double> CardSpacingProperty =
        AvaloniaProperty.Register<CompositionCoverControl, double>(nameof(CardSpacing), 16.0);

    public static readonly StyledProperty<IBrush?> GridBackgroundProperty =
        AvaloniaProperty.Register<CompositionCoverControl, IBrush?>(nameof(GridBackground));

    public static readonly DirectProperty<CompositionCoverControl, Rect> SelectedItemBoundsProperty =
        AvaloniaProperty.RegisterDirect<CompositionCoverControl, Rect>(
            nameof(SelectedItemBounds),
            o => o.SelectedItemBounds);

    static CompositionCoverControl()
    {
        LayoutModeProperty.Changed.AddClassHandler<CompositionCoverControl>((control, e) =>
            control.OnLayoutModeChanged(e));

        ItemsSourceProperty.Changed.AddClassHandler<CompositionCoverControl>((control, e) =>
            control.ApplyItemsSourceToActiveLayout());

        SelectedIndexProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.OnSelectedIndexChangedFromBinding());

        PointedItemIndexProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        PlayingItemIndexProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        ImageBitmapPropertyProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        ImageFileNamePropertyProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        TitlePropertyProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        ItemDoubleClickedCommandProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        ItemSelectedCommandProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        ShowCoverFoundOverlayProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        PublishSelectedItemBoundsProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.ApplyPublishSelectedItemBounds());

        GameplayPreviewItemIndexProperty.Changed.AddClassHandler<CompositionCoverControl>((control, e) =>
        {
            int oldIndex = e.GetOldValue<int>();
            int newIndex = e.GetNewValue<int>();
            if (oldIndex != newIndex && newIndex >= 0)
                control.ClearGameplayPreviewDisplayedFrame();
            control.ApplyPublishGameplayPreviewBounds();
        });

        IsGameplayPreviewVisibleProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.ApplyPublishGameplayPreviewBounds());

        IsGameplayPreviewVideoVisibleProperty.Changed.AddClassHandler<CompositionCoverControl>((control, e) =>
            control.UpdateGameplayPreviewVideoVisibility(e.GetNewValue<bool>()));

        GameplayPreviewPlayerProperty.Changed.AddClassHandler<CompositionCoverControl>((control, e) =>
        {
            control._gameplayPreviewVideo.Player = e.NewValue as AesMpvPlayer;
            if (control.IsGameplayPreviewVideoVisible)
                control._gameplayPreviewVideo.KickRender();
        });

        PauseLoadingSpinnerAnimationProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        IsContentLoadingProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncSharedProperties());

        GlobalOpacityProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
        {
            control.SyncSharedProperties();
            control.SyncGridOpacity();
        });

        GridOpacityMultiplierProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncGridOpacity());

        ItemSpacingProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncCarouselProperties());

        ItemScaleProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncLayoutScaleProperties());

        VerticalOffsetProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncCarouselProperties());

        SideTranslationProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncCarouselProperties());

        StackSpacingProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncCarouselProperties());

        UseFullCoverSizeProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncCarouselProperties());

        CardSpacingProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncGridProperties());

        GridBackgroundProperty.Changed.AddClassHandler<CompositionCoverControl>((control, _) =>
            control.SyncGridProperties());
    }

    public CompositionCoverControl()
    {
        _carousel = new CompositionCarouselControl
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        _cardGrid = new CompositionCardGridControl
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsVisible = false,
        };

        _carousel.PropertyChanged += OnCarouselPropertyChanged;
        _cardGrid.PropertyChanged += OnCardGridPropertyChanged;
        _carousel.BindSharedCoverCache(_sharedCoverCache);
        _cardGrid.BindSharedCoverCache(_sharedCoverCache);
        _cardGrid.BindCardDisplayCache(_cardDisplayCache);
        _carousel.CoverVisualSyncRequested = ScheduleSiblingCoverHydrate;

        _gameplayPreviewVideo = new VideoViewControl
        {
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false,
            Opacity = 0,
            IsVisible = true,
            Width = GameplayPreviewRenderDimension,
            Height = GameplayPreviewRenderDimension,
            ExportFramesForComposition = false,
            ReferenceViewportWidth = GameplayPreviewRenderDimension,
            ReferenceViewportHeight = GameplayPreviewRenderDimension,
            UseCustomHeartbeat = false,
        };
        _gameplayPreviewVideo.FrameCaptured += OnGameplayPreviewFrameCaptured;

        Children.Add(_gameplayPreviewVideo);
        Children.Add(_carousel);
        Children.Add(_cardGrid);

        ApplyLayoutModeImmediate(LayoutMode);
    }

    public CompositionCarouselControl CarouselPart => _carousel;

    public CompositionCardGridControl GridPart => _cardGrid;

    public CoverLayoutMode LayoutMode
    {
        get => GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double? ViewportPreviewIndex
    {
        get => GetValue(ViewportPreviewIndexProperty);
        set => SetValue(ViewportPreviewIndexProperty, value);
    }

    public int PointedItemIndex
    {
        get => GetValue(PointedItemIndexProperty);
        set => SetValue(PointedItemIndexProperty, value);
    }

    public int PlayingItemIndex
    {
        get => GetValue(PlayingItemIndexProperty);
        set => SetValue(PlayingItemIndexProperty, value);
    }

    public string? ImageBitmapProperty
    {
        get => GetValue(ImageBitmapPropertyProperty);
        set => SetValue(ImageBitmapPropertyProperty, value);
    }

    public string? ImageFileNameProperty
    {
        get => GetValue(ImageFileNamePropertyProperty);
        set => SetValue(ImageFileNamePropertyProperty, value);
    }

    public string? TitleProperty
    {
        get => GetValue(TitlePropertyProperty);
        set => SetValue(TitlePropertyProperty, value);
    }

    public ICommand? ItemDoubleClickedCommand
    {
        get => GetValue(ItemDoubleClickedCommandProperty);
        set => SetValue(ItemDoubleClickedCommandProperty, value);
    }

    public ICommand? ItemSelectedCommand
    {
        get => GetValue(ItemSelectedCommandProperty);
        set => SetValue(ItemSelectedCommandProperty, value);
    }

    public bool ShowCoverFoundOverlay
    {
        get => GetValue(ShowCoverFoundOverlayProperty);
        set => SetValue(ShowCoverFoundOverlayProperty, value);
    }

    public bool PublishSelectedItemBounds
    {
        get => GetValue(PublishSelectedItemBoundsProperty);
        set => SetValue(PublishSelectedItemBoundsProperty, value);
    }

    public int GameplayPreviewItemIndex
    {
        get => GetValue(GameplayPreviewItemIndexProperty);
        set => SetValue(GameplayPreviewItemIndexProperty, value);
    }

    public bool IsGameplayPreviewVisible
    {
        get => GetValue(IsGameplayPreviewVisibleProperty);
        set => SetValue(IsGameplayPreviewVisibleProperty, value);
    }

    public bool IsGameplayPreviewVideoVisible
    {
        get => GetValue(IsGameplayPreviewVideoVisibleProperty);
        set => SetValue(IsGameplayPreviewVideoVisibleProperty, value);
    }

    public AesMpvPlayer? GameplayPreviewPlayer
    {
        get => GetValue(GameplayPreviewPlayerProperty);
        set => SetValue(GameplayPreviewPlayerProperty, value);
    }

    public bool PauseLoadingSpinnerAnimation
    {
        get => GetValue(PauseLoadingSpinnerAnimationProperty);
        set => SetValue(PauseLoadingSpinnerAnimationProperty, value);
    }

    public bool IsContentLoading
    {
        get => GetValue(IsContentLoadingProperty);
        set => SetValue(IsContentLoadingProperty, value);
    }

    public double GlobalOpacity
    {
        get => GetValue(GlobalOpacityProperty);
        set => SetValue(GlobalOpacityProperty, value);
    }

    public double GridOpacityMultiplier
    {
        get => GetValue(GridOpacityMultiplierProperty);
        set => SetValue(GridOpacityMultiplierProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double ItemScale
    {
        get => GetValue(ItemScaleProperty);
        set => SetValue(ItemScaleProperty, value);
    }

    public double VerticalOffset
    {
        get => GetValue(VerticalOffsetProperty);
        set => SetValue(VerticalOffsetProperty, value);
    }

    public double SideTranslation
    {
        get => GetValue(SideTranslationProperty);
        set => SetValue(SideTranslationProperty, value);
    }

    public double StackSpacing
    {
        get => GetValue(StackSpacingProperty);
        set => SetValue(StackSpacingProperty, value);
    }

    public bool UseFullCoverSize
    {
        get => GetValue(UseFullCoverSizeProperty);
        set => SetValue(UseFullCoverSizeProperty, value);
    }

    public double CardSpacing
    {
        get => GetValue(CardSpacingProperty);
        set => SetValue(CardSpacingProperty, value);
    }

    public IBrush? GridBackground
    {
        get => GetValue(GridBackgroundProperty);
        set => SetValue(GridBackgroundProperty, value);
    }

    public Rect SelectedItemBounds
    {
        get => _selectedItemBounds;
        private set => SetAndRaise(SelectedItemBoundsProperty, ref _selectedItemBounds, value);
    }

    /// <summary>
    /// Applies the requested layout mode immediately (no cross-fade), matching a settings change.
    /// </summary>
    public void ApplyLayoutMode(CoverLayoutMode mode)
    {
        _suppressLayoutTransition = true;
        try
        {
            SetCurrentValue(LayoutModeProperty, mode);
            ApplyLayoutModeImmediate(mode);
        }
        finally
        {
            _suppressLayoutTransition = false;
        }
    }

    public void RefreshExclusionRenderSize()
    {
        _carousel.RefreshExclusionRenderSize();
        _cardGrid.RefreshExclusionRenderSize();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control active = _appliedLayoutMode == CoverLayoutMode.Carousel ? _carousel : _cardGrid;
        active.Measure(availableSize);
        return active.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(finalSize);
        Control active = _appliedLayoutMode == CoverLayoutMode.Carousel ? _carousel : _cardGrid;
        active.Arrange(bounds);

        double previewSize = GameplayPreviewRenderDimension;
        _gameplayPreviewVideo.Arrange(new Rect(0, 0, previewSize, previewSize));

        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncAllProperties();
        ApplyLayoutModeImmediate(LayoutMode);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pendingGameplayPreviewFrame?.Dispose();
        _pendingGameplayPreviewFrame = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutModeChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not CoverLayoutMode mode)
            return;

        if (_suppressLayoutTransition || VisualRoot == null)
        {
            ApplyLayoutModeImmediate(mode);
            return;
        }

        if (mode == _appliedLayoutMode)
            return;

        StartLayoutTransition(mode);
    }

    private void ApplyLayoutModeImmediate(CoverLayoutMode mode)
    {
        bool layoutModeChanged = mode != _appliedLayoutMode;
        _appliedLayoutMode = mode;
        var useCarousel = mode == CoverLayoutMode.Carousel;
        var source = ItemsSource;

        _transitionCts?.Cancel();
        ApplyVisibilityForLayoutMode(mode);
        SyncSharedProperties();

        if (useCarousel)
        {
            _cardGrid.SetCoverLoadingActive(false);
            _carousel.SetCoverLoadingActive(true);
            if (!ReferenceEquals(_carousel.ItemsSource, source))
                _carousel.ItemsSource = source;
            else if (source != null)
                _carousel.SyncItemsSourceLightweight(source, forceRebuild: true);
            _cardGrid.SyncItemsSourceMetadataOnly(source);
            if (layoutModeChanged)
                _carousel.HydrateCoverImagesFrom(_cardGrid);
        }
        else
        {
            _carousel.SetCoverLoadingActive(false);
            _cardGrid.SetCoverLoadingActive(true);
            if (layoutModeChanged)
                _cardGrid.HydrateCoverImagesFrom(_carousel);
            else
                _cardGrid.SyncItemsSourceLightweight(source);
            _carousel.SyncItemsSourceMetadataOnly(source);
        }

        ApplyPublishSelectedItemBounds();
        ApplyPublishGameplayPreviewBounds();
        SyncGridOpacity();
        RefreshSelectedItemBoundsFromActiveChild();
    }

    private void ApplyItemsSourceToActiveLayout()
    {
        SyncSharedProperties();

        var source = ItemsSource;
        if (_appliedLayoutMode == CoverLayoutMode.Carousel)
        {
            _carousel.SetCoverLoadingActive(true);
            _cardGrid.SetCoverLoadingActive(false);
            if (!ReferenceEquals(_carousel.ItemsSource, source))
                _carousel.ItemsSource = source;
            else if (source != null)
                _carousel.RefreshItemsFromCurrentSource();
            _cardGrid.SyncItemsSourceMetadataOnly(source);
        }
        else
        {
            _cardGrid.SetCoverLoadingActive(true);
            _carousel.SetCoverLoadingActive(false);
            var synced = _cardGrid.SyncItemsSourceLightweight(source);
            if (!synced && source != null)
                _cardGrid.RefreshItemsFromCurrentSource();
            _carousel.SyncItemsSourceMetadataOnly(source);
        }

        _carousel.SnapToSelectedIndex();
        _cardGrid.SnapToSelectedIndex();
    }

    private void ApplyVisibilityForLayoutMode(CoverLayoutMode mode)
    {
        var useCarousel = mode == CoverLayoutMode.Carousel;
        _carousel.IsVisible = useCarousel;
        _carousel.Opacity = 1;
        _carousel.IsHitTestVisible = useCarousel;
        _carousel.ClipToBounds = false;

        _cardGrid.IsVisible = !useCarousel;
        _cardGrid.Opacity = 1;
        _cardGrid.IsHitTestVisible = !useCarousel;
        _cardGrid.ClipToBounds = true;
        _cardGrid.HorizontalScrollEnabled = mode == CoverLayoutMode.HorizontalGrid;
        Background = useCarousel ? null : GridBackground;
    }

    private async void StartLayoutTransition(CoverLayoutMode targetMode)
    {
        _transitionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _transitionCts = cts;
        var token = cts.Token;

        var toCarousel = targetMode == CoverLayoutMode.Carousel;
        Control outgoing = toCarousel ? _cardGrid : _carousel;
        Control incoming = toCarousel ? _carousel : _cardGrid;

        _appliedLayoutMode = targetMode;
        incoming.IsVisible = true;
        incoming.IsHitTestVisible = false;
        incoming.Opacity = 0;

        if (incoming == _cardGrid)
        {
            _cardGrid.HorizontalScrollEnabled = targetMode == CoverLayoutMode.HorizontalGrid;
            _cardGrid.SetImageRevealHold(true);
        }

        PrepareIncomingLayoutLight(targetMode);
        Dispatcher.UIThread.Post(() => PrepareIncomingLayoutHeavy(targetMode), DispatcherPriority.Background);
        ApplyPublishSelectedItemBounds();
        ApplyPublishGameplayPreviewBounds();

        const int steps = 14;
        var stepDelay = (int)(LayoutTransitionDuration.TotalMilliseconds / steps);

        try
        {
            for (var i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                var eased = Math.Sin((i / (double)steps) * Math.PI * 0.5);
                outgoing.Opacity = 1 - eased;
                incoming.Opacity = eased;
                await Task.Delay(stepDelay).ConfigureAwait(true);
            }

            outgoing.IsVisible = false;
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            incoming.IsHitTestVisible = true;
            outgoing.IsHitTestVisible = false;

            ApplyPublishSelectedItemBounds();
            ApplyPublishGameplayPreviewBounds();
            RefreshSelectedItemBoundsFromActiveChild();
            FinalizeIncomingLayout(targetMode);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested)
            {
                if (targetMode != CoverLayoutMode.Carousel)
                    _cardGrid.SetImageRevealHold(false);
                return;
            }

            throw;
        }
    }

    private void PrepareIncomingLayoutLight(CoverLayoutMode targetMode)
    {
        var source = ItemsSource;
        if (targetMode == CoverLayoutMode.Carousel)
        {
            _carousel.SetCoverLoadingActive(true);
            if (!ReferenceEquals(_carousel.ItemsSource, source))
                _carousel.ItemsSource = source;
            else if (source != null)
                _carousel.SyncItemsSourceLightweight(source, forceRebuild: true);
            _cardGrid.SyncItemsSourceMetadataOnly(source);
        }
        else
        {
            _cardGrid.SetCoverLoadingActive(true);
            _cardGrid.SyncItemsSourceLightweight(source);
            _carousel.SyncItemsSourceMetadataOnly(source);
        }
    }

    private void PrepareIncomingLayoutHeavy(CoverLayoutMode targetMode)
    {
        if (targetMode != _appliedLayoutMode)
            return;

        if (targetMode == CoverLayoutMode.Carousel)
        {
            _carousel.HydrateCoverImagesFrom(_cardGrid);
            _carousel.SnapToSelectedIndex();
        }
        else
        {
            _cardGrid.HydrateCoverImagesFrom(_carousel);
            _cardGrid.SnapToSelectedIndex();
        }
    }

    private void FinalizeIncomingLayout(CoverLayoutMode targetMode)
    {
        ApplyVisibilityForLayoutMode(targetMode);
        SyncGridOpacity();
        if (targetMode != CoverLayoutMode.Carousel)
            _cardGrid.SetImageRevealHold(false);

        if (targetMode == CoverLayoutMode.Carousel)
            _cardGrid.SetCoverLoadingActive(false);
        else
            _carousel.SetCoverLoadingActive(false);

        ApplyPublishGameplayPreviewBounds();
    }

    private void ScheduleSiblingCoverHydrate()
    {
        if (_appliedLayoutMode != CoverLayoutMode.Carousel)
            return;

        _coverVisualSyncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(48) };
        _coverVisualSyncTimer.Stop();
        _coverVisualSyncTimer.Tick -= CoverVisualSyncTimer_Tick;
        _coverVisualSyncTimer.Tick += CoverVisualSyncTimer_Tick;
        _coverVisualSyncTimer.Start();
    }

    private void CoverVisualSyncTimer_Tick(object? sender, EventArgs e)
    {
        _coverVisualSyncTimer?.Stop();
        if (_appliedLayoutMode != CoverLayoutMode.Carousel)
            return;

        _carousel.HydrateCoverImagesFrom(_cardGrid);
    }

    private void SyncAllProperties()
    {
        SyncSharedProperties();
        SyncCarouselProperties();
        SyncGridProperties();
        SyncLayoutScaleProperties();
        SyncGridOpacity();
        ApplyPublishSelectedItemBounds();
        ApplyVisibilityForLayoutMode(_appliedLayoutMode);
    }

    private void SyncSharedProperties()
    {
        SyncCarouselSelectedIndex();
        _cardGrid.SelectedIndex = SelectedIndex;
        _carousel.PointedItemIndex = PointedItemIndex;
        _cardGrid.PointedItemIndex = PointedItemIndex;
        _carousel.PlayingItemIndex = PlayingItemIndex;
        _carousel.GameplayPreviewItemIndex = GameplayPreviewItemIndex;
        _cardGrid.GameplayPreviewItemIndex = GameplayPreviewItemIndex;
        _carousel.ImageBitmapProperty = ImageBitmapProperty;
        _cardGrid.ImageBitmapProperty = ImageBitmapProperty;
        _carousel.ImageFileNameProperty = ImageFileNameProperty;
        _cardGrid.ImageFileNameProperty = ImageFileNameProperty;
        _cardGrid.TitleProperty = TitleProperty;
        _carousel.ItemDoubleClickedCommand = ItemDoubleClickedCommand;
        _cardGrid.ItemDoubleClickedCommand = ItemDoubleClickedCommand;
        _carousel.ItemSelectedCommand = ItemSelectedCommand;
        _cardGrid.ItemSelectedCommand = ItemSelectedCommand;
        _carousel.ShowCoverFoundOverlay = ShowCoverFoundOverlay;
        _cardGrid.ShowCoverFoundOverlay = ShowCoverFoundOverlay;
        _carousel.PauseLoadingSpinnerAnimation = PauseLoadingSpinnerAnimation;
        _cardGrid.PauseLoadingSpinnerAnimation = PauseLoadingSpinnerAnimation;
        _cardGrid.IsContentLoading = IsContentLoading;
        _carousel.GlobalOpacity = GlobalOpacity;
    }

    private void SyncCarouselProperties()
    {
        _carousel.ItemSpacing = ItemSpacing;
        _carousel.VerticalOffset = VerticalOffset;
        _carousel.SideTranslation = SideTranslation;
        _carousel.StackSpacing = StackSpacing;
        _carousel.UseFullCoverSize = UseFullCoverSize;
    }

    private void SyncGridProperties()
    {
        _cardGrid.CardSpacing = CardSpacing;
        _cardGrid.Background = GridBackground;
        if (_appliedLayoutMode != CoverLayoutMode.Carousel)
            Background = GridBackground;
    }

    private void SyncLayoutScaleProperties()
    {
        _carousel.ItemScale = ItemScale;
        _cardGrid.CardScale = ItemScale;
    }

    private void SyncGridOpacity()
    {
        _cardGrid.Opacity = GlobalOpacity * GridOpacityMultiplier;
    }

    /// <summary>
    /// Forces the active layout child to re-read cover bitmaps from bound items.
    /// Call after background cover hydration completes.
    /// </summary>
    public void ReloadCoverImages(bool forceFullRescan = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReloadCoverImages(forceFullRescan), DispatcherPriority.Normal);
            return;
        }

        var useCarousel = _appliedLayoutMode == CoverLayoutMode.Carousel;

        if (useCarousel)
        {
            _carousel.SetCoverLoadingActive(true);
            _cardGrid.SetCoverLoadingActive(false);
            _carousel.RefreshMissingCoverSlots(forceFullRescan);
            _carousel.HydrateCoverImagesFrom(_cardGrid);
        }
        else
        {
            _carousel.SetCoverLoadingActive(false);
            _cardGrid.SetCoverLoadingActive(true);
            _cardGrid.RequestCoverDisplayRefresh(forceFullRescan);
        }
    }

    public void RefreshSelectedItemBounds()
    {
        if (_appliedLayoutMode == CoverLayoutMode.Carousel)
            _carousel.RefreshSelectedItemBounds();
        else
            _cardGrid.RefreshSelectedItemBounds();

        RefreshSelectedItemBoundsFromActiveChild();
    }

    /// <summary>
    /// Re-publishes gameplay preview compositor state after carousel layout or cover reload.
    /// </summary>
    public void RefreshGameplayPreviewPresentation()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshGameplayPreviewPresentation, DispatcherPriority.Loaded);
            return;
        }

        ApplyPublishGameplayPreviewBounds();
        if (IsGameplayPreviewVideoVisible)
            _gameplayPreviewVideo.KickRender();
    }

    private void ApplyPublishSelectedItemBounds()
    {
        var publish = PublishSelectedItemBounds;
        var useCarousel = _appliedLayoutMode == CoverLayoutMode.Carousel;
        _carousel.PublishSelectedItemBounds = publish && useCarousel;
        _cardGrid.PublishSelectedItemBounds = publish && !useCarousel;
        if (publish)
            RefreshSelectedItemBounds();
        else
            RefreshSelectedItemBoundsFromActiveChild();
    }

    private void ApplyPublishGameplayPreviewBounds()
    {
        var publish = IsGameplayPreviewVisible && GameplayPreviewItemIndex >= 0;
        var showVideo = publish && IsGameplayPreviewVideoVisible;
        var useCarousel = _appliedLayoutMode == CoverLayoutMode.Carousel;
        _carousel.GameplayPreviewItemIndex = GameplayPreviewItemIndex;
        _cardGrid.GameplayPreviewItemIndex = GameplayPreviewItemIndex;

        if (useCarousel)
        {
            _carousel.PostGameplayPreviewVisualState(GameplayPreviewItemIndex, publish);
            if (publish && !showVideo)
                _carousel.PostGameplayPreviewFrame(null);
        }
        else
        {
            _cardGrid.PostGameplayPreviewVisualState(GameplayPreviewItemIndex, publish);
            if (publish && !showVideo)
                _cardGrid.PostGameplayPreviewFrame(null);
        }

        _gameplayPreviewVideo.ExportFramesForComposition = showVideo;
        if (showVideo)
            _gameplayPreviewVideo.KickRender();
    }

    private void OnGameplayPreviewFrameCaptured(SKImage frame)
    {
        if (!IsGameplayPreviewVideoVisible || GameplayPreviewItemIndex < 0)
        {
            frame.Dispose();
            return;
        }

        _pendingGameplayPreviewFrame?.Dispose();
        _pendingGameplayPreviewFrame = frame;

        if (_gameplayPreviewFramePostScheduled)
            return;

        _gameplayPreviewFramePostScheduled = true;
        var layoutMode = _appliedLayoutMode;
        Dispatcher.UIThread.Post(() => PublishPendingGameplayPreviewFrame(layoutMode), DispatcherPriority.Background);
    }

    private void PublishPendingGameplayPreviewFrame(CoverLayoutMode layoutMode)
    {
        _gameplayPreviewFramePostScheduled = false;
        var frame = _pendingGameplayPreviewFrame;
        _pendingGameplayPreviewFrame = null;

        if (frame == null)
            return;

        if (!IsGameplayPreviewVideoVisible || GameplayPreviewItemIndex < 0)
        {
            frame.Dispose();
            return;
        }

        if (layoutMode == CoverLayoutMode.Carousel)
            _carousel.PostGameplayPreviewFrame(frame);
        else
            _cardGrid.PostGameplayPreviewFrame(frame);
    }

    private void OnSelectedIndexChangedFromBinding()
    {
        if (!_syncingSelectedIndexFromCarousel)
            ClearViewportPreview();

        SyncSharedProperties();
    }

    private void ClearViewportPreview()
    {
        _carousel.ClearViewportPreview();
        if (ViewportPreviewIndex.HasValue)
            ViewportPreviewIndex = null;
    }

    private void SyncCarouselSelectedIndex()
    {
        if (_carousel.IsWheelScrolling || ViewportPreviewIndex.HasValue)
            return;

        if (Math.Abs(_carousel.SelectedIndex - SelectedIndex) > 0.0001)
            _carousel.SelectedIndex = SelectedIndex;
    }

    private void OnCarouselPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_appliedLayoutMode != CoverLayoutMode.Carousel)
            return;

        if (e.Property == CompositionCarouselControl.ViewportPreviewIndexProperty)
        {
            ViewportPreviewIndex = _carousel.ViewportPreviewIndex;
            return;
        }

        if (e.Property == CompositionCarouselControl.SelectedIndexProperty)
        {
            if (_carousel.IsWheelScrolling)
                return;

            if (Math.Abs(SelectedIndex - _carousel.SelectedIndex) > 0.0001)
            {
                _syncingSelectedIndexFromCarousel = true;
                try
                {
                    SelectedIndex = _carousel.SelectedIndex;
                }
                finally
                {
                    _syncingSelectedIndexFromCarousel = false;
                }
            }

            return;
        }

        if (e.Property == CompositionCarouselControl.PointedItemIndexProperty)
        {
            if (PointedItemIndex != _carousel.PointedItemIndex)
                PointedItemIndex = _carousel.PointedItemIndex;
            return;
        }

        if (e.Property == CompositionCarouselControl.SelectedItemBoundsProperty)
            SelectedItemBounds = _carousel.SelectedItemBounds;
    }

    private void OnCardGridPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_appliedLayoutMode == CoverLayoutMode.Carousel)
            return;

        if (e.Property == CompositionCardGridControl.SelectedIndexProperty)
        {
            if (Math.Abs(SelectedIndex - _cardGrid.SelectedIndex) > 0.0001)
                SelectedIndex = _cardGrid.SelectedIndex;
            return;
        }

        if (e.Property == CompositionCardGridControl.PointedItemIndexProperty)
        {
            if (PointedItemIndex != _cardGrid.PointedItemIndex)
                PointedItemIndex = _cardGrid.PointedItemIndex;
            return;
        }

        if (e.Property == CompositionCardGridControl.SelectedItemBoundsProperty)
            SelectedItemBounds = _cardGrid.SelectedItemBounds;
    }

    private void RefreshSelectedItemBoundsFromActiveChild()
    {
        SelectedItemBounds = _appliedLayoutMode == CoverLayoutMode.Carousel
            ? _carousel.SelectedItemBounds
            : _cardGrid.SelectedItemBounds;
    }

    private void ClearGameplayPreviewDisplayedFrame()
    {
        _pendingGameplayPreviewFrame?.Dispose();
        _pendingGameplayPreviewFrame = null;
        _gameplayPreviewFramePostScheduled = false;

        if (_appliedLayoutMode == CoverLayoutMode.Carousel)
            _carousel.PostGameplayPreviewFrame(null);
        else
            _cardGrid.PostGameplayPreviewFrame(null);
    }

    private void UpdateGameplayPreviewVideoVisibility(bool visible)
    {
        if (!visible)
        {
            _gameplayPreviewVideo.IsRenderingPaused = true;
            _pendingGameplayPreviewFrame?.Dispose();
            _pendingGameplayPreviewFrame = null;
            _gameplayPreviewFramePostScheduled = false;
        }
        else
        {
            _gameplayPreviewVideo.IsRenderingPaused = false;
            _gameplayPreviewVideo.KickRender();
        }

        ApplyPublishGameplayPreviewBounds();
    }

}
