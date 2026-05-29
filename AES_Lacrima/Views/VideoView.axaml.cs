using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AES_Emulation.Windows.API;
using AES_Lacrima.ViewModels;
using System;
using System.ComponentModel;

namespace AES_Lacrima.Views;

public partial class VideoView : UserControl
{
    private MusicViewModel? _viewModel;
    private bool _isVideoFullscreen;
    private MainWindowCaptureFullscreenState? _savedFullscreenState;
    private int _savedVideoViewportZIndex = 200;
    private int _savedVideoViewportRowSpan = 1;
    private FullscreenCursorAutoHideHelper? _cursorAutoHide;

    public VideoView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MusicViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null) return;

        if (e.PropertyName == nameof(MusicViewModel.IsAddUrlPopupOpen) && _viewModel.IsAddUrlPopupOpen)
        {
            FocusPopupTextBox(AddUrlTextBox);
        }
        else if (e.PropertyName == nameof(MusicViewModel.IsAddPlaylistPopupOpen) && _viewModel.IsAddPlaylistPopupOpen)
        {
            FocusPopupTextBox(AddPlaylistTextBox);
        }
        else if (e.PropertyName == nameof(MusicViewModel.IsFullscreen))
        {
            if (TopLevel.GetTopLevel(this) is not MainWindow mainWindow)
                return;

            if (_viewModel.IsFullscreen && !_isVideoFullscreen)
                EnterFullscreen(mainWindow);
            else if (!_viewModel.IsFullscreen && _isVideoFullscreen)
                ExitFullscreen();
        }
    }

    private void EnterFullscreen(MainWindow mainWindow)
    {
        var screenBounds = mainWindow.Screens?.Primary?.Bounds
            ?? new PixelRect(0, 0, (int)mainWindow.ClientSize.Width, (int)mainWindow.ClientSize.Height);

        _savedFullscreenState = mainWindow.EnterCaptureFullscreenMode(screenBounds);
        _isVideoFullscreen = true;

        var videoViewport = this.FindControl<Grid>("VideoViewport");
        if (videoViewport != null)
        {
            _savedVideoViewportZIndex = videoViewport.ZIndex;
            _savedVideoViewportRowSpan = Grid.GetRowSpan(videoViewport);
            videoViewport.ZIndex = 5000;
            Grid.SetRowSpan(videoViewport, 4);
        }

        _cursorAutoHide = new FullscreenCursorAutoHideHelper(this);
        _cursorAutoHide.Start();
    }

    private void ExitFullscreen()
    {
        _cursorAutoHide?.Dispose();
        _cursorAutoHide = null;

        var videoViewport = this.FindControl<Grid>("VideoViewport");
        if (videoViewport != null)
        {
            videoViewport.ZIndex = _savedVideoViewportZIndex;
            Grid.SetRowSpan(videoViewport, _savedVideoViewportRowSpan);
        }

        if (_savedFullscreenState != null && TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            mainWindow.ExitCaptureFullscreenMode(_savedFullscreenState);
            _savedFullscreenState = null;
        }

        _isVideoFullscreen = false;
    }

    private static void FocusPopupTextBox(TextBox? textBox)
    {
        if (textBox == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void VideoViewport_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel?.ToggleFullscreenCommand.Execute(null);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _cursorAutoHide?.Dispose();
        _cursorAutoHide = null;
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
