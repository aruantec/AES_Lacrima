using System;
using System.ComponentModel;
using AES_Controls.Composition;
using AES_Controls.Player.Models;
using Avalonia;
using Avalonia.Controls;

namespace AES_Lacrima.Views.Navigation;

public partial class MusicListView : UserControl
{
    private CompositionAlbumRowControl? _albumList;
    private TextBox? _renameTextBox;
    private ViewModels.MusicViewModel? _viewModel;
    private FolderMediaItem? _renamingAlbum;

    public MusicListView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeViewModel();
        _viewModel = DataContext as ViewModels.MusicViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeRenamingAlbum(_viewModel?.SelectedAlbum);
        UpdateRenameOverlay();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _albumList = this.FindControl<CompositionAlbumRowControl>("AlbumList");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");

        if (_albumList != null)
            _albumList.LayoutUpdated += OnAlbumListLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnsubscribeViewModel();
        if (_albumList != null)
            _albumList.LayoutUpdated -= OnAlbumListLayoutUpdated;
        SubscribeRenamingAlbum(null);
        _albumList = null;
        _renameTextBox = null;
    }

    private void UnsubscribeViewModel()
    {
        if (_viewModel == null)
            return;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.MusicViewModel.SelectedAlbum)
            or nameof(ViewModels.MusicViewModel.SelectedAlbumIndex))
        {
            SubscribeRenamingAlbum(_viewModel?.SelectedAlbum);
            UpdateRenameOverlay();
        }
    }

    private void SubscribeRenamingAlbum(FolderMediaItem? album)
    {
        if (ReferenceEquals(_renamingAlbum, album))
            return;

        if (_renamingAlbum != null)
            _renamingAlbum.PropertyChanged -= OnRenamingAlbumPropertyChanged;

        _renamingAlbum = album;

        if (_renamingAlbum != null)
            _renamingAlbum.PropertyChanged += OnRenamingAlbumPropertyChanged;
    }

    private void OnRenamingAlbumPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderMediaItem.IsRenaming))
            UpdateRenameOverlay();
    }

    private void OnAlbumListLayoutUpdated(object? sender, EventArgs e) => UpdateRenameOverlay();

    private void UpdateRenameOverlay()
    {
        if (_renameTextBox == null)
            return;

        bool renaming = _viewModel?.SelectedAlbum?.IsRenaming == true && _viewModel.SelectedAlbumIndex >= 0;
        _renameTextBox.IsVisible = renaming;
        _renameTextBox.IsHitTestVisible = renaming;

        if (_albumList != null)
            _albumList.RenamingIndex = renaming ? _viewModel!.SelectedAlbumIndex : -1;

        if (!renaming || _albumList == null || _viewModel == null)
            return;

        var titleBar = _albumList.GetTileTitleBarBounds(_viewModel.SelectedAlbumIndex);
        const double inputHeight = 34;
        double inputTop = titleBar.Y + Math.Max(0, (titleBar.Height - inputHeight) * 0.5);
        var topLeft = _albumList.TranslatePoint(new Point(titleBar.X, inputTop), this);
        if (topLeft == null)
            return;

        _renameTextBox.Margin = new Thickness(topLeft.Value.X, topLeft.Value.Y, 0, 0);
        _renameTextBox.Width = titleBar.Width;
        _renameTextBox.Height = Math.Min(inputHeight, titleBar.Height);
    }
}
