using System;
using System.ComponentModel;
using AES_Controls.Composition;
using AES_Controls.Helpers;
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
    private int _renameOverlayLayoutRetries;
    private ContextMenu? _backgroundContextMenu;
    private ContextMenu? _albumRowContextMenu;

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

    public void RefreshAlbumTileCovers()
    {
        if (_viewModel != null)
        {
            foreach (var folder in _viewModel.FilteredAlbumList)
                folder.RebuildPreviewItems(useFirstItemCover: true);
        }

        this.FindControl<CompositionAlbumRowControl>("AlbumList")?.RefreshAllTileCovers();
    }

    public void ResetAlbumListScroll() =>
        _albumList?.ResetScrollPositionOnViewShown();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _albumList = this.FindControl<CompositionAlbumRowControl>("AlbumList");
        _renameTextBox = this.FindControl<TextBox>("RenameTextBox");

        if (_albumList != null)
        {
            _albumList.LayoutUpdated += OnAlbumListLayoutUpdated;
            _albumList.RenameOverlayLayoutRequested += OnAlbumListRenameOverlayLayoutRequested;
            _albumRowContextMenu = _albumList.ContextMenu;
            if (_albumRowContextMenu != null)
                _albumRowContextMenu.Opening += OnAlbumListContextMenuOpening;
        }

        if (Content is Grid grid)
        {
            _backgroundContextMenu = grid.ContextMenu;
            if (_backgroundContextMenu != null)
                _backgroundContextMenu.Opening += OnAlbumListContextMenuOpening;
        }

        RefreshAlbumTileCovers();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnsubscribeViewModel();
        if (_albumList != null)
        {
            _albumList.LayoutUpdated -= OnAlbumListLayoutUpdated;
            _albumList.RenameOverlayLayoutRequested -= OnAlbumListRenameOverlayLayoutRequested;
        }

        if (_albumRowContextMenu != null)
            _albumRowContextMenu.Opening -= OnAlbumListContextMenuOpening;
        if (_backgroundContextMenu != null)
            _backgroundContextMenu.Opening -= OnAlbumListContextMenuOpening;

        SubscribeRenamingAlbum(null);
        _albumList = null;
        _renameTextBox = null;
        _albumRowContextMenu = null;
        _backgroundContextMenu = null;
    }

    private void OnAlbumListContextMenuOpening(object? sender, CancelEventArgs e)
    {
        // Avalonia's light-dismiss does not reliably close programmatically-opened
        // menus from the cover/items area; close siblings before this one opens.
        if (sender is ContextMenu opening)
            ContextMenuHelper.CloseOpenContextMenus(this, except: opening);
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
        if (e.PropertyName == nameof(ViewModels.MusicViewModel.IsActive) && _viewModel?.IsActive == true)
            ResetAlbumListScroll();
        else if (e.PropertyName is nameof(ViewModels.MusicViewModel.SelectedAlbum)
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
        if (e.PropertyName != nameof(FolderMediaItem.IsRenaming))
            return;

        _renameOverlayLayoutRetries = 0;
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRenameOverlay, Avalonia.Threading.DispatcherPriority.Loaded);
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRenameOverlay, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void OnAlbumListLayoutUpdated(object? sender, EventArgs e) => UpdateRenameOverlay();

    private void OnAlbumListRenameOverlayLayoutRequested(object? sender, EventArgs e) => UpdateRenameOverlay();

    private void UpdateRenameOverlay()
    {
        if (_renameTextBox == null)
            return;

        bool renaming = _viewModel?.SelectedAlbum?.IsRenaming == true && _viewModel.SelectedAlbumIndex >= 0;
        _renameTextBox.IsHitTestVisible = renaming;

        if (_albumList != null)
            _albumList.RenamingIndex = renaming ? _viewModel!.SelectedAlbumIndex : -1;

        if (!renaming || _albumList == null || _viewModel == null)
        {
            _renameOverlayLayoutRetries = 0;
            _renameTextBox.IsVisible = false;
            return;
        }

        int renameIndex = _viewModel.SelectedAlbumIndex;
        var titleBar = _albumList.GetTileTitleBarBounds(renameIndex);
        if (titleBar.Width <= 0 ||
            titleBar.Right < 1 ||
            titleBar.X > _albumList.Bounds.Width - 1)
        {
            if (_renameOverlayLayoutRetries < 4)
            {
                _renameOverlayLayoutRetries++;
                _albumList.EnsureSelectedItemVisible(animate: false);
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    UpdateRenameOverlay,
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }

            _renameTextBox.IsVisible = false;
            return;
        }

        _renameOverlayLayoutRetries = 0;

        const double inputHeight = 34;
        double inputTop = titleBar.Y + Math.Max(0, (titleBar.Height - inputHeight) * 0.5);
        var topLeft = _albumList.TranslatePoint(new Point(titleBar.X, inputTop), this);
        if (topLeft == null)
        {
            if (_renameOverlayLayoutRetries < 4)
            {
                _renameOverlayLayoutRetries++;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    UpdateRenameOverlay,
                    Avalonia.Threading.DispatcherPriority.Loaded);
            }

            _renameTextBox.IsVisible = false;
            return;
        }

        _renameTextBox.IsVisible = true;
        _renameTextBox.Margin = new Thickness(topLeft.Value.X, topLeft.Value.Y, 0, 0);
        _renameTextBox.Width = titleBar.Width;
        _renameTextBox.Height = Math.Min(inputHeight, titleBar.Height);
    }
}
