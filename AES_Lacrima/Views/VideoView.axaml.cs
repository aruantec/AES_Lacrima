using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AES_Lacrima.ViewModels;
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

    public bool IsAlbumListInteractive
    {
        get => _isAlbumListInteractive;
        set => SetAndRaise(IsAlbumListInteractiveProperty, ref _isAlbumListInteractive, value);
    }

    public VideoView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
