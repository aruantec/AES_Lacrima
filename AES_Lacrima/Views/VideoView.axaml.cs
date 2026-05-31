using AES_Emulation.Windows.API;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace AES_Lacrima.Views;

public partial class VideoView : UserControl
{
    private static readonly TimeSpan AlbumListTransitionDuration = TimeSpan.FromMilliseconds(550);

    public static readonly DirectProperty<VideoView, bool> IsAlbumListInteractiveProperty =
        AvaloniaProperty.RegisterDirect<VideoView, bool>(
            nameof(IsAlbumListInteractive),
            o => o.IsAlbumListInteractive,
            (o, v) => o.IsAlbumListInteractive = v);

    private MusicViewModel? _viewModel;
    private bool _isAlbumListInteractive = true;
    private int _albumListMaskVersion;
    private bool _isVideoCaptureFullscreen;
    private MainWindowCaptureFullscreenState? _savedMainWindowCaptureFullscreenState;
    private int _savedVideoViewportRowSpan = 1;
    private int _savedVideoViewportZIndex = 200;
    private FullscreenCursorAutoHideHelper? _fullscreenCursorAutoHide;
    private Grid? _videoViewport;

    public bool IsAlbumListInteractive
    {
        get => _isAlbumListInteractive;
        set => SetAndRaise(IsAlbumListInteractiveProperty, ref _isAlbumListInteractive, value);
    }

    public VideoView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(InputElement.KeyDownEvent, OnVideoViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnFullscreenCursorPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerPressedEvent, OnFullscreenCursorPointerActivity, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _videoViewport = this.FindControl<Grid>("VideoViewport");
        if (_videoViewport != null)
        {
            _videoViewport.AddHandler(
                InputElement.PointerPressedEvent,
                OnVideoViewportPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_videoViewport != null)
        {
            _videoViewport.RemoveHandler(InputElement.PointerPressedEvent, OnVideoViewportPointerPressed);
            _videoViewport = null;
        }

        RemoveHandler(InputElement.KeyDownEvent, OnVideoViewKeyDown);
        RemoveHandler(InputElement.PointerMovedEvent, OnFullscreenCursorPointerActivity);
        RemoveHandler(InputElement.PointerPressedEvent, OnFullscreenCursorPointerActivity);

        if (_isVideoCaptureFullscreen)
            ExitVideoCaptureFullscreen();

        base.OnDetachedFromVisualTree(e);
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MusicViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null)
            return;

        if (e.PropertyName == nameof(MusicViewModel.IsAlbumlistOpen))
            StartAlbumListTransitionMask();
        else if (e.PropertyName == nameof(MusicViewModel.IsVideoViewportDismissed) && _viewModel.IsVideoViewportDismissed)
            ExitVideoCaptureFullscreen();
        else if (e.PropertyName == nameof(MusicViewModel.IsActive) && !_viewModel.IsActive)
            ExitVideoCaptureFullscreen();
        else if (e.PropertyName == nameof(MusicViewModel.IsAddUrlPopupOpen) && _viewModel.IsAddUrlPopupOpen)
            FocusPopupTextBox(AddUrlTextBox);
        else if (e.PropertyName == nameof(MusicViewModel.IsAddPlaylistPopupOpen) && _viewModel.IsAddPlaylistPopupOpen)
            FocusPopupTextBox(AddPlaylistTextBox);
    }

    private void StartAlbumListTransitionMask()
    {
        var maskVersion = ++_albumListMaskVersion;
        IsAlbumListInteractive = false;

        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(AlbumListTransitionDuration).ConfigureAwait(true);
            if (maskVersion != _albumListMaskVersion)
                return;

            IsAlbumListInteractive = true;
        }, DispatcherPriority.Background);
    }

    private static void FocusPopupTextBox(TextBox? textBox)
    {
        if (textBox == null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnVideoViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TryHandleVideoDoubleClick(e);
    }

    private bool TryHandleVideoDoubleClick(PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2)
            return false;

        e.Handled = true;

        if (_viewModel is not { IsVideoViewportVisible: true })
            return true;

        if (_isVideoCaptureFullscreen)
            ExitVideoCaptureFullscreen();
        else
            EnterVideoCaptureFullscreen();

        return true;
    }

    private void OnVideoViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isVideoCaptureFullscreen)
            return;

        ExitVideoCaptureFullscreen();
        e.Handled = true;
    }

    private void OnFullscreenCursorPointerActivity(object? sender, PointerEventArgs e)
    {
        if (!_isVideoCaptureFullscreen)
            return;

        _fullscreenCursorAutoHide?.NotifyPointerActivity();
    }

    private void EnterVideoCaptureFullscreen()
    {
        if (_isVideoCaptureFullscreen)
            return;

        if (TopLevel.GetTopLevel(this) is not MainWindow mainWindow)
            return;

        if (_viewModel is { IsEqualizerVisible: true })
            _viewModel.IsEqualizerVisible = false;

        var bounds = GetScreenBounds(mainWindow);
        _savedMainWindowCaptureFullscreenState = mainWindow.EnterCaptureFullscreenMode(bounds);
        ApplyVideoFullscreenLayout(fullscreen: true);
        _isVideoCaptureFullscreen = true;
        StartFullscreenCursorAutoHide();

        Dispatcher.UIThread.Post(() =>
        {
            _videoViewport?.InvalidateArrange();
            _videoViewport?.InvalidateVisual();
        }, DispatcherPriority.Background);
    }

    private void ExitVideoCaptureFullscreen()
    {
        if (!_isVideoCaptureFullscreen)
            return;

        StopFullscreenCursorAutoHide();

        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow &&
            _savedMainWindowCaptureFullscreenState != null)
        {
            mainWindow.ExitCaptureFullscreenMode(_savedMainWindowCaptureFullscreenState);
            _savedMainWindowCaptureFullscreenState = null;
        }

        ApplyVideoFullscreenLayout(fullscreen: false);
        _isVideoCaptureFullscreen = false;
    }

    private void ApplyVideoFullscreenLayout(bool fullscreen)
    {
        var videoViewport = _videoViewport ?? this.FindControl<Grid>("VideoViewport");
        if (videoViewport == null)
            return;

        if (fullscreen)
        {
            _savedVideoViewportRowSpan = Grid.GetRowSpan(videoViewport);
            _savedVideoViewportZIndex = videoViewport.ZIndex;
            Grid.SetRowSpan(videoViewport, 4);
            videoViewport.ZIndex = 5000;
            return;
        }

        Grid.SetRowSpan(videoViewport, _savedVideoViewportRowSpan);
        videoViewport.ZIndex = _savedVideoViewportZIndex;
    }

    private void StartFullscreenCursorAutoHide()
    {
        if (_fullscreenCursorAutoHide != null)
            return;

        _fullscreenCursorAutoHide = new FullscreenCursorAutoHideHelper(this);
        _fullscreenCursorAutoHide.Start();
    }

    private void StopFullscreenCursorAutoHide()
    {
        _fullscreenCursorAutoHide?.Stop();
        _fullscreenCursorAutoHide = null;

        if (TopLevel.GetTopLevel(this) is Window mainWindow)
            mainWindow.Cursor = Cursor.Default;

        Cursor = Cursor.Default;
    }

    private static PixelRect GetScreenBounds(Window mainWindow)
    {
        var screen = mainWindow.Screens?.ScreenFromWindow(mainWindow) ?? mainWindow.Screens?.Primary;
        return screen?.Bounds ?? new PixelRect(0, 0, 0, 0);
    }
}
