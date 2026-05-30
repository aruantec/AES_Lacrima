using System;
using System.Linq;
using AES_Controls.Composition;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AES_Lacrima.Views.Navigation;

public partial class EmulationListView : UserControl
{
    private const int ScrollIdleMs = 180;

    public static readonly DirectProperty<EmulationListView, bool> IsAlbumListScrollingProperty =
        AvaloniaProperty.RegisterDirect<EmulationListView, bool>(
            nameof(IsAlbumListScrolling),
            o => o.IsAlbumListScrolling);

    private bool _isAlbumListScrolling;
    private ScrollViewer? _scrollViewer;
    private ListBox? _albumList;
    private DispatcherTimer? _scrollIdleTimer;

    public bool IsAlbumListScrolling
    {
        get => _isAlbumListScrolling;
        private set
        {
            if (_isAlbumListScrolling == value)
                return;

            FolderCompositionTileControl.SetAlbumListScrollFrozen(value);
            SetAndRaise(IsAlbumListScrollingProperty, ref _isAlbumListScrolling, value);
        }
    }

    public EmulationListView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (this.FindControl<ListBox>("AlbumList") is not { } list)
            return;

        _albumList = list;
        list.AttachedToVisualTree += OnAlbumListAttachedToVisualTree;
        list.AddHandler(InputElement.PointerWheelChangedEvent, OnAlbumListPointerWheel, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        TryHookScrollViewer(list);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_albumList != null)
        {
            _albumList.AttachedToVisualTree -= OnAlbumListAttachedToVisualTree;
            _albumList.RemoveHandler(InputElement.PointerWheelChangedEvent, OnAlbumListPointerWheel);
        }

        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= OnAlbumListScrollChanged;
            _scrollViewer.PointerPressed -= OnAlbumListScrollViewerPointerPressed;
        }

        _albumList = null;
        _scrollViewer = null;
        _scrollIdleTimer?.Stop();
        _scrollIdleTimer = null;
        IsAlbumListScrolling = false;
    }

    private void OnAlbumListAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ListBox list)
            TryHookScrollViewer(list);
    }

    private void TryHookScrollViewer(ListBox list)
    {
        if (_scrollViewer != null)
            return;

        _scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer == null)
            return;

        _scrollViewer.ScrollChanged += OnAlbumListScrollChanged;
        _scrollViewer.PointerPressed += OnAlbumListScrollViewerPointerPressed;
    }

    private void OnAlbumListPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta == default)
            return;

        BeginScrollInteraction();
    }

    private void OnAlbumListScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_scrollViewer == null)
            return;

        var hit = _scrollViewer.GetVisualAt(e.GetPosition(_scrollViewer));
        if (IsInsideScrollBar(hit))
            BeginScrollInteraction();
    }

    private static bool IsInsideScrollBar(Visual? visual)
    {
        while (visual != null)
        {
            if (visual is ScrollBar)
                return true;

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private void BeginScrollInteraction()
    {
        if (!IsAlbumListScrolling)
            IsAlbumListScrolling = true;

        RestartScrollIdleTimer();
    }

    private void OnAlbumListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta == default)
            return;

        BeginScrollInteraction();
    }

    private void RestartScrollIdleTimer()
    {
        _scrollIdleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollIdleMs) };
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Tick -= OnAlbumListScrollIdle;
        _scrollIdleTimer.Tick += OnAlbumListScrollIdle;
        _scrollIdleTimer.Start();
    }

    private void OnAlbumListScrollIdle(object? sender, EventArgs e)
    {
        _scrollIdleTimer?.Stop();
        IsAlbumListScrolling = false;
    }
}
