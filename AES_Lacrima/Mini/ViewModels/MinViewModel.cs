using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Core.IO;
using AES_Lacrima.ViewModels;
using AES_Core.Services;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima.Mini.ViewModels
{
    /// <summary>
    /// Marker interface for the minimized player view model.
    /// Implementations provide the data/context required by the minimized view UI.
    /// </summary>
    public interface IMinViewModel { }

    /// <summary>
    /// View model for the minimized player view.
    /// Provides playlist management, playback control commands and visual brushes
    /// used by the minimized UI (for example <see cref="ControlsBrush"/> and
    /// <see cref="LoadedBrush"/>).
    /// </summary>
    /// <remarks>
    /// This class is registered automatically for dependency injection via the
    /// <see cref="AutoRegisterAttribute"/> applied to the class.
    /// </remarks>
    [AutoRegister]
    public partial class MinViewModel : ViewModelBase, IMinViewModel
    {
        #region Private fields
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        private string _agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
        private Bitmap _defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();
        private AvaloniaList<MediaItem>? _mediaItemsSubscribed;
        private MusicViewModel? _musicViewModelSubscribed;
        private bool _isSwitchingExtension;
        private bool _isSyncingSearchText;
        private bool _isRestoringPersistedMiniState;
        private bool _isWaitingForPendingMiniStateRestore;
        private bool _hasPendingMiniStateRestore;
        private string? _pendingOpenedAlbumFileName;
        private string? _pendingOpenedAlbumTitle;
        private string? _pendingSelectedItemFileName;
        private int _pendingSelectedItemIndex = -1;
        private string? _pendingLastPlayedFileName;
        private BitmapColorHelper _colorHelper = new();

        #endregion

        #region Static / readonly fields
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MinViewModel>();
        private static readonly Random _random = new();
        private static readonly TimeSpan ExtensionTransitionDelay = TimeSpan.FromMilliseconds(620);

        #endregion

        #region Observable / AutoResolve properties
        // Settings path
        protected override string SettingsFilePath => ApplicationPaths.GetSettingsFile("CustomPlaylist.json");

        [ObservableProperty]
        private bool _extensionAreaOpen;

        [ObservableProperty]
        private ObservableObject? _extensionView;

        [ObservableProperty]
        private bool _settingsVisible;

        [ObservableProperty]
        private AvaloniaList<MediaItem>? _mediaItems;

        // text entered into the footer search bar; drives filtering of the playlist
        [ObservableProperty]
        private string _searchText = string.Empty;

        partial void OnSearchTextChanged(string value)
        {
            if (_isSyncingSearchText)
                return;

            if (MusicViewModel == null)
                return;

            if (ShowPlaylist)
                MusicViewModel.SearchAlbumText = value;
            else
                MusicViewModel.SearchText = value;
        }

        // filtered view of MediaItems based on SearchText; bound to the ListBox
        [ObservableProperty]
        private AvaloniaList<MediaItem>? _filteredMediaItems;

        [ObservableProperty]
        private MediaItem? _selectedMediaItem;

        [ObservableProperty]
        private int _selectedPlaylistIndex = -1;

        [ObservableProperty]
        private FolderMediaItem? _selectedAlbum;

        [ObservableProperty]
        private int _selectedAlbumIndex = -1;

        [ObservableProperty]
        private MediaItem? _loadedMediaItem;

        [ObservableProperty]
        private AvaloniaList<MediaItem>? _selectedItems = [];

        [ObservableProperty]
        private bool _isMuted;

        [ObservableProperty]
        private bool _showPlaylist = false;

        [ObservableProperty]
        private double _windowWidth = 550;

        [ObservableProperty]
        private double _windowHeight = 486;

        [ObservableProperty]
        private IBrush? _selectionBrush = new SolidColorBrush(Color.Parse("#005CFE"));

        [ObservableProperty]
        private IBrush? _loadedBrush;

        [ObservableProperty]
        private IBrush? _controlsBrush;

        [ObservableProperty]
        private IBrush? _colorGradientBrush;

        [ObservableProperty]
        private bool _isCoverPlaceholder = true;

        [ObservableProperty]
        private double _totalDuration;

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        [AutoResolve]
        private MediaUrlService? _mediaUrlService;

        [ObservableProperty]
        private bool _isVisualizerActive;

        [ObservableProperty]
        private bool _isEqualizerActive;

        [ObservableProperty]
        private bool _isTrackLoadPending;

        // supported types
        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];
        private MediaItem? _pendingTrackLoadItem;

        #endregion

        #region Public properties
        public double DisplayDuration => LoadedMediaItem?.Duration ?? SelectedMediaItem?.Duration ?? 0.0;

        public Bitmap LoadedCoverBitmap => LoadedMediaItem?.CoverBitmap ?? _defaultCover;

        public bool ShuffleMode
        {
            get => MusicViewModel?.AudioPlayer?.RepeatMode == RepeatMode.Shuffle;
            set
            {
                if (MusicViewModel?.AudioPlayer == null) return;
                MusicViewModel.AudioPlayer.RepeatMode = value ? RepeatMode.Shuffle : RepeatMode.Off;
                OnPropertyChanged(nameof(ShuffleMode));
            }
        }

        #endregion

        #region Commands
        [RelayCommand]
        private void Drop(object? param)
        {
            var playlistItems = GetCurrentPlaylistItems();
            if (string.IsNullOrWhiteSpace(SearchText) && playlistItems != null)
            {
                try
                {
                    UnsubscribeFromCollection(playlistItems);
                    UpdateItemIndices();
                }
                finally
                {
                    SubscribeToCollection(playlistItems);
                }

                UpdateItemIndices();
                UpdateTotalDuration();
            }
        }

        /// <summary>
        /// Switches back to the full AES main window and remembers the preference.
        /// </summary>
        [RelayCommand]
        private void SwitchMode()
        {
            if (AppLifetime == null)
                return;

            if (MusicViewModel != null)
                MusicViewModel.ResetPlaybackOnAlbumSwitch = false;

            AES_Lacrima.App.IsSwitchingMode = true;

            if (AppLifetime.MainWindow?.DataContext is IViewModelBase vm)
            {
                try { vm.SaveSettings(); } catch { }
            }
            DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();

            if (SettingsViewModel != null)
            {
                SettingsViewModel.AppMode = 0;
                SettingsViewModel.SaveSettings();
            }

            var newWindow = new AES_Lacrima.Views.MainWindow();
            newWindow.Closing += (s, e) =>
            {
                DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();
                if (!AES_Lacrima.App.IsSwitchingMode)
                {
                    try { DiLocator.Dispose(); } catch { }
                }
            };

            var oldWindow = AppLifetime.MainWindow;
            AppLifetime.MainWindow = newWindow;
            newWindow.Show();

            // refresh taskbar buttons now that the main window has changed
            var musicVm = DiLocator.ResolveViewModel<MusicViewModel>();
            musicVm?.InitializeTaskbarButtons();

            oldWindow?.Close();

            AES_Lacrima.App.IsSwitchingMode = false;
        }

        [RelayCommand]
        private async Task ToggleEqualizer()
        {
            if (_isSwitchingExtension) return;

            var desiredVm = DiLocator.ResolveViewModel<MiniEqualizerViewModel>();

            if (ExtensionAreaOpen && ExtensionView != null && ExtensionView.GetType() != desiredVm?.GetType())
            {
                _isSwitchingExtension = true;
                ExtensionAreaOpen = false;
                await Task.Delay(ExtensionTransitionDelay);
                ExtensionView = desiredVm;
                ExtensionAreaOpen = true;
                _isSwitchingExtension = false;
                return;
            }

            if (!ExtensionAreaOpen)
            {
                ExtensionView = desiredVm;
                ExtensionAreaOpen = true;
                return;
            }

            if (ExtensionAreaOpen && ExtensionView != null && ExtensionView.GetType() == desiredVm?.GetType())
            {
                ExtensionAreaOpen = false;
                return;
            }
        }

        [RelayCommand]
        private async Task ToggleVisualizer()
        {
            if (_isSwitchingExtension) return;

            var desiredVm = DiLocator.ResolveViewModel<VisualizerViewModel>();
            if (desiredVm != null)
            {
                desiredVm.MinViewModel = this;
                desiredVm.MusicViewModel = MusicViewModel;
                desiredVm.SettingsViewModel = SettingsViewModel;
            }

            if (ExtensionAreaOpen && ExtensionView != null && ExtensionView.GetType() != desiredVm?.GetType())
            {
                _isSwitchingExtension = true;
                ExtensionAreaOpen = false;
                await Task.Delay(ExtensionTransitionDelay);
                ExtensionView = desiredVm;
                ExtensionAreaOpen = true;
                _isSwitchingExtension = false;
                return;
            }

            if (!ExtensionAreaOpen)
            {
                ExtensionView = desiredVm;
                ExtensionAreaOpen = true;
                return;
            }

            if (ExtensionAreaOpen && ExtensionView != null && ExtensionView.GetType() == desiredVm?.GetType())
            {
                ExtensionAreaOpen = false;
                return;
            }
        }

        [RelayCommand]
        private async Task EjectAsync()
        {
            var musicViewModel = MusicViewModel;
            if (musicViewModel?.LoadedAlbum != null)
            {
                musicViewModel.AudioPlayer?.Stop();
                musicViewModel.LoadedAlbum = null;
                musicViewModel.SelectedAlbum = null;
                SelectedMediaItem = null;
                LoadedMediaItem = null;
                ShowPlaylist = true;
            }
            else
                await AddFolders();
        }

        [RelayCommand]
        private void ToggleSettings() => SettingsVisible = !SettingsVisible;

        [RelayCommand]
        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        [RelayCommand]
        private void MinimizeWindow() => AppLifetime?.MainWindow?.WindowState = Avalonia.Controls.WindowState.Minimized;

        [RelayCommand]
        private void CloseApplication()
        {
            if (AppLifetime == null || DiLocator.ResolveViewModel<ISettingsService>() is not { } settingsService)
                return;
            SaveSettings();
            AppLifetime.Shutdown();
        }

        [RelayCommand]
        private async Task PlaySelectedMediaItem()
        {
            if (SelectedMediaItem == null || MusicViewModel == null) return;

            var playlistItems = GetCurrentPlaylistItems();
            if (playlistItems != null)
                MusicViewModel.PlaybackQueue = playlistItems;

            MusicViewModel.SelectedMediaItem = SelectedMediaItem;
            var index = MusicViewModel.CoverItems.IndexOf(SelectedMediaItem);
            if (index >= 0)
            {
                MusicViewModel.SelectedIndex = index;
                MusicViewModel.PointedIndex = index;
                MusicViewModel.HighlightedItem = SelectedMediaItem;
            }
            await PlayMediaItemAsync(SelectedMediaItem);
            LoadedMediaItem = SelectedMediaItem;
            UpdateLoadedBrush(SelectedMediaItem);
        }

        [RelayCommand]
        private void OpenSelectedAlbum()
        {
            if (MusicViewModel == null || SelectedAlbum == null)
                return;

            MusicViewModel.ResetPlaybackOnAlbumSwitch = true;
            MusicViewModel.SelectedAlbum = SelectedAlbum;
            MusicViewModel.OpenSelectedFolderCommand.Execute(null);
            ShowPlaylist = false;
            SyncSearchTextFromVisibleCollection();
            SubscribeToCurrentPlaylist();
            EnsureFirstPlaylistItemSelected();
        }

        private async Task PlayMediaItemAsync(MediaItem item)
        {
            if (MusicViewModel?.AudioPlayer == null || string.IsNullOrWhiteSpace(item.FileName)) return;

            _pendingTrackLoadItem = item;
            IsTrackLoadPending = true;
            MusicViewModel.AudioPlayer.IsLoadingMedia = true;
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);

            if (item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                if (_mediaUrlService != null)
                    await _mediaUrlService.OpenMediaItemAsync(MusicViewModel.AudioPlayer, item);
                else
                    await MusicViewModel.AudioPlayer.PlayFile(item);
            }
            else
            {
                await MusicViewModel.AudioPlayer.PlayFile(item);
            }
        }

        [RelayCommand]
        private void PlayPause()
        {
            MusicViewModel?.TogglePlayCommand.Execute(null);
        }

        [RelayCommand]
        private void Next()
        {
            MusicViewModel?.PlayNextCommand.Execute(null);
        }

        [RelayCommand]
        private void Previous()
        {
            MusicViewModel?.PlayPreviousCommand.Execute(null);
        }

        [RelayCommand]
        private void Stop() => MusicViewModel?.AudioPlayer?.Stop();

        [RelayCommand]
        private void SetPosition(double position) => MusicViewModel?.AudioPlayer?.SetPosition(position);

        [RelayCommand]
        private void DeleteSelectedItems()
        {
            var playlistItems = GetCurrentPlaylistItems();
            var loadedAlbum = MusicViewModel?.LoadedAlbum;
            if (playlistItems == null || loadedAlbum == null) return;

            var itemsToRemove = SelectedItems?.Count > 0
                ? SelectedItems.ToList()
                : SelectedMediaItem != null
                    ? [SelectedMediaItem]
                    : [];

            if (itemsToRemove.Count == 0)
                return;

            foreach (var item in itemsToRemove)
            {
                if (item == LoadedMediaItem)
                {
                    MusicViewModel?.AudioPlayer?.Stop();
                    LoadedMediaItem = null;
                }

                playlistItems.Remove(item);
                if (!ReferenceEquals(playlistItems, loadedAlbum.Children))
                    loadedAlbum.Children.Remove(item);
            }

            SelectedItems = [];
            UpdateTotalDuration();
        }

        [RelayCommand]
        private async Task AddFolders()
        {
            if (MusicViewModel == null)
                return;

            MusicViewModel.OpenFolderCommand.Execute(null);
            ShowPlaylist = MusicViewModel.LoadedAlbum == null;
            SyncSearchTextFromVisibleCollection();
        }

        [RelayCommand]
        private async Task AddFilesAsync()
        {
            MusicViewModel?.AddItemsCommand.Execute(null);
            SubscribeToCurrentPlaylist();
        }

        [RelayCommand]
        private void AddUrl() => MusicViewModel?.AddUrlCommand.Execute(null);

        [RelayCommand]
        private void AddPlaylist() => MusicViewModel?.AddPlaylistCommand.Execute(null);

        [RelayCommand]
        private void ShowAlbumActions()
        {
            ShowPlaylist = true;
            SyncSearchTextFromVisibleCollection();
        }

        [RelayCommand]
        private void EndRenameAlbum(FolderMediaItem? album) => MusicViewModel?.EndRenameCommand.Execute(album);

        [RelayCommand]
        private void CancelRenameAlbum(FolderMediaItem? album) => MusicViewModel?.CancelRenameCommand.Execute(album);

        [RelayCommand]
        private void ReloadPlaylistItemMetadata(MediaItem? item)
        {
            if (!TryTargetPlaylistItem(item, out var targetItem))
                return;

            MusicViewModel?.ReloadMetadataCommand.Execute(targetItem);
        }

        [RelayCommand]
        private void DeletePlaylistItem(MediaItem? item)
        {
            if (!TryTargetPlaylistItem(item, out _))
                return;

            MusicViewModel?.DeletePointedItemCommand.Execute(null);
            SyncPlaybackStateFromMusicViewModel();
            UpdateTotalDuration();
        }

        #endregion

        #region Constructor / Prepare
        public override void Prepare()
        {
            ExtensionView = DiLocator.ResolveViewModel<MiniEqualizerViewModel>();
            MusicViewModel?.AudioPlayer?.EnableWaveform = true;
            MusicViewModel?.AudioPlayer?.AutoSkipTrailingSilence = true;
            LoadSettings();
            SchedulePendingMiniStateRestore();
            ShowPlaylist = MusicViewModel?.LoadedAlbum == null;
            SyncSearchTextFromVisibleCollection();
            SubscribeToCurrentPlaylist();
            SyncPlaybackStateFromMusicViewModel();
            MusicViewModel?.RefreshLoadedAlbumMetadata();
            if (LoadedMediaItem == null)
                ResetStoppedDisplay();
            UpdateTotalDuration();
            try { if (MusicViewModel?.AudioPlayer != null) AttachAudioPlayerHandlers(MusicViewModel.AudioPlayer); }
            catch (Exception ex) { Log.Warn("Prepare: failed to attach audio player handlers", ex); }
        }
        #endregion

        #region Partial methods
        partial void OnMediaItemsChanged(AvaloniaList<MediaItem>? value)
        {
            try
            {
                UnsubscribeFromCollection(_mediaItemsSubscribed);
                SubscribeToCollection(value);
                _mediaItemsSubscribed = value;
                UpdateTotalDuration();
                UpdateFilteredItems(); // refresh filtered list when the underlying collection changes
            }
            catch (Exception ex)
            {
                Log.Warn("OnMediaItemsChanged: subscription handling failed", ex);
            }
        }

        partial void OnLoadedMediaItemChanged(MediaItem? value)
        {
            UpdateLoadedBrush(value);
            OnPropertyChanged(nameof(DisplayDuration));
            OnPropertyChanged(nameof(LoadedCoverBitmap));
        }

        partial void OnSelectedMediaItemChanged(MediaItem? value)
        {
            if (value != null && MusicViewModel != null)
            {
                var index = MusicViewModel.CoverItems.IndexOf(value);
                if (index >= 0)
                {
                    if (SelectedPlaylistIndex != index)
                        SelectedPlaylistIndex = index;
                }
            }
            else if (value == null && SelectedPlaylistIndex != -1)
            {
                SelectedPlaylistIndex = -1;
            }

            OnPropertyChanged(nameof(DisplayDuration));
        }

        partial void OnSelectedPlaylistIndexChanged(int value)
        {
            if (MusicViewModel == null)
                return;

            if (value < 0 || value >= MusicViewModel.CoverItems.Count)
            {
                if (SelectedMediaItem != null)
                    SelectedMediaItem = null;
                return;
            }

            var selectedItem = MusicViewModel.CoverItems[value];
            if (!ReferenceEquals(SelectedMediaItem, selectedItem))
                SelectedMediaItem = selectedItem;
        }

        partial void OnSelectedAlbumChanged(FolderMediaItem? value)
        {
            if (MusicViewModel == null)
                return;

            var index = value == null ? -1 : MusicViewModel.FilteredAlbumList.IndexOf(value);
            if (SelectedAlbumIndex != index)
                SelectedAlbumIndex = index;
        }

        partial void OnSelectedAlbumIndexChanged(int value)
        {
            if (MusicViewModel == null)
                return;

            if (value < 0 || value >= MusicViewModel.FilteredAlbumList.Count)
            {
                if (SelectedAlbum != null)
                    SelectedAlbum = null;
                return;
            }

            var selectedAlbum = MusicViewModel.FilteredAlbumList[value];
            if (!ReferenceEquals(SelectedAlbum, selectedAlbum))
                SelectedAlbum = selectedAlbum;
        }

        partial void OnMusicViewModelChanged(MusicViewModel? value)
        {
            try
            {
                if (_musicViewModelSubscribed != null)
                {
                    _musicViewModelSubscribed.PropertyChanged -= MusicViewModel_PropertyChanged;
                    _musicViewModelSubscribed.ResetPlaybackOnAlbumSwitch = false;
                }

                _musicViewModelSubscribed = value;

                if (_musicViewModelSubscribed != null)
                {
                    _musicViewModelSubscribed.PropertyChanged += MusicViewModel_PropertyChanged;
                    _musicViewModelSubscribed.ResetPlaybackOnAlbumSwitch = true;
                }

                if (value?.AudioPlayer != null)
                    AttachAudioPlayerHandlers(value.AudioPlayer);

                SyncSearchTextFromVisibleCollection();
                SubscribeToCurrentPlaylist();
                SyncSelectedAlbumFromMusicViewModel();
                SyncPlaybackStateFromMusicViewModel();
                TryRestorePendingMiniState();
                SchedulePendingMiniStateRestore();
            }
            catch (Exception ex) { Log.Warn("OnMusicViewModelChanged failed to attach audio handlers", ex); }
        }

        partial void OnShowPlaylistChanged(bool value)
        {
            SyncSearchTextFromVisibleCollection();
            OnPropertyChanged(nameof(VisibleItemCount));
        }

        // Called whenever SearchText or MediaItems change to rebuild the filtered collection
        private void UpdateFilteredItems()
        {
            if (MediaItems == null)
            {
                FilteredMediaItems = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                // copy to avoid binding to original list which may be modified concurrently
                FilteredMediaItems = new AvaloniaList<MediaItem>(MediaItems);
            }
            else
            {
                var lower = SearchText.Trim().ToLowerInvariant();
                FilteredMediaItems = new AvaloniaList<MediaItem>(MediaItems.Where(mi => !string.IsNullOrEmpty(mi.Title) && mi.Title.ToLowerInvariant().Contains(lower)));
            }
        }

        partial void OnExtensionAreaOpenChanged(bool value)
        {
            // Update active flags when extension area visibility changes
            IsVisualizerActive = value && ExtensionView is VisualizerViewModel;
            IsEqualizerActive = value && ExtensionView is MiniEqualizerViewModel;
        }

        partial void OnExtensionViewChanged(ObservableObject? value)
        {
            // Keep active flags in sync when the view model changes
            IsVisualizerActive = ExtensionAreaOpen && value is VisualizerViewModel;
            IsEqualizerActive = ExtensionAreaOpen && value is MiniEqualizerViewModel;
        }

        #endregion

        #region Public methods
        // (none additional beyond commands)

        #endregion

        #region Private methods
        private void UpdateLoadedBrush(MediaItem? item)
        {
            LoadedBrush = null;
            IsCoverPlaceholder = true;

            var coverBitmap = item?.CoverBitmap;
            var hasCustomCover = coverBitmap != null && coverBitmap != _defaultCover;

            if (hasCustomCover)
            {
                LoadedBrush = new SolidColorBrush(BitmapColorHelper.GetDominantColor(coverBitmap));
                IsCoverPlaceholder = false;
            }

            SelectionBrush = new SolidColorBrush(Color.Parse("#005CFE"));
            ControlsBrush = LoadedBrush;
            ColorGradientBrush = _colorHelper.GetColorGradient(hasCustomCover ? coverBitmap : null);
        }

        private void SubscribeToCollection(AvaloniaList<MediaItem>? list)
        {
            if (list == null) return;
            if (list is INotifyCollectionChanged incc) incc.CollectionChanged += MediaItems_CollectionChanged;
            foreach (var item in list) if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged += MediaItem_PropertyChanged;
        }

        private void UnsubscribeFromCollection(AvaloniaList<MediaItem>? list)
        {
            if (list == null) return;
            if (list is INotifyCollectionChanged incc) incc.CollectionChanged -= MediaItems_CollectionChanged;
            foreach (var item in list) if (item is INotifyPropertyChanged inpc) inpc.PropertyChanged -= MediaItem_PropertyChanged;
        }

        private void MediaItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.NewItems != null) foreach (var ni in e.NewItems.Cast<MediaItem>()) if (ni is INotifyPropertyChanged inpc) inpc.PropertyChanged += MediaItem_PropertyChanged;
                if (e.OldItems != null) foreach (var oi in e.OldItems.Cast<MediaItem>()) if (oi is INotifyPropertyChanged inpc) inpc.PropertyChanged -= MediaItem_PropertyChanged;
                UpdateTotalDuration();
                OnPropertyChanged(nameof(VisibleItemCount));
            }
            catch (Exception ex) { Log.Warn("MediaItems_CollectionChanged failed", ex); }
        }

        private void MediaItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MediaItem.Duration))
            {
                UpdateTotalDuration();
                OnPropertyChanged(nameof(DisplayDuration));
            }
            else if (sender is MediaItem item
                && ReferenceEquals(item, LoadedMediaItem)
                && e.PropertyName == nameof(MediaItem.CoverBitmap))
            {
                UpdateLoadedBrush(item);
                OnPropertyChanged(nameof(LoadedCoverBitmap));
            }
        }

        private void UpdateTotalDuration()
        {
            try { TotalDuration = GetCurrentPlaylistItems()?.Sum(m => m?.Duration ?? 0.0) ?? 0.0; }
            catch (Exception ex) { Log.Warn("UpdateTotalDuration failed", ex); TotalDuration = 0.0; }
        }

        private void AttachAudioPlayerHandlers(AudioPlayer? player)
        {
            if (player == null) return;
            try { player.Stopped -= OnAudioPlayerStopped; player.PropertyChanged -= Player_PropertyChanged; }
            catch (Exception ex) { Log.Warn("AttachAudioPlayerHandlers: defensive unsubscribe failed", ex); }
            try { player.Stopped += OnAudioPlayerStopped; player.PropertyChanged += Player_PropertyChanged; }
            catch (Exception ex) { Log.Warn("AttachAudioPlayerHandlers: subscribe failed", ex); }

            MusicViewModel?.TaskbarAction = (TaskbarButtonId id) =>
            {
                switch(id)
                {
                    case TaskbarButtonId.Previous: PreviousCommand.Execute(null); break;
                    case TaskbarButtonId.PlayPause: PlayPauseCommand.Execute(null); break;
                    case TaskbarButtonId.Next: NextCommand.Execute(null); break;
                }
            };
        }

        private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioPlayer.Volume))
            {
                _ = Task.Run(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsMuted = MusicViewModel?.AudioPlayer?.Volume == 0));
            }
            else if (e.PropertyName == nameof(AudioPlayer.IsPlaying) ||
                     e.PropertyName == nameof(AudioPlayer.IsLoadingMedia) ||
                     e.PropertyName == nameof(AudioPlayer.CurrentMediaItem))
            {
                UpdateTrackLoadPendingState();
                SyncPlaybackStateFromMusicViewModel();
            }
            else if (e.PropertyName == nameof(AudioPlayer.RepeatMode))
            {
                OnPropertyChanged(nameof(ShuffleMode));
            }
        }

        private void UpdateTrackLoadPendingState()
        {
            var player = MusicViewModel?.AudioPlayer;
            if (player == null)
            {
                _pendingTrackLoadItem = null;
                IsTrackLoadPending = false;
                return;
            }

            if (!IsTrackLoadPending)
                return;

            if (_pendingTrackLoadItem == null)
            {
                IsTrackLoadPending = player.IsLoadingMedia || player.IsBuffering;
                return;
            }

            var requestedTrackIsCurrent = ReferenceEquals(player.CurrentMediaItem, _pendingTrackLoadItem) ||
                                          string.Equals(player.CurrentMediaItem?.FileName, _pendingTrackLoadItem.FileName, StringComparison.Ordinal);

            if (requestedTrackIsCurrent && !player.IsLoadingMedia && !player.IsBuffering)
            {
                _pendingTrackLoadItem = null;
                IsTrackLoadPending = false;
            }
        }

        private void OnAudioPlayerStopped(object? sender, EventArgs e)
        {
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    _pendingTrackLoadItem = null;
                    IsTrackLoadPending = false;
                    ResetStoppedDisplay();
                }
                catch (Exception ex) { Log.Warn("OnAudioPlayerStopped failed", ex); }
            });
        }

        private void UpdateItemIndices()
        {
            try
            {
                var playlistItems = GetCurrentPlaylistItems();
                if (playlistItems == null) return;
                for (int i = 0; i < playlistItems.Count; i++) { var item = playlistItems[i]; if (item != null) item.Index = i + 1; }
            }
            catch (Exception ex) { Log.Warn("UpdateItemIndices failed", ex); }
        }

        private AvaloniaList<MediaItem>? GetCurrentPlaylistItems() => MusicViewModel?.CoverItems;

        public int VisibleItemCount => ShowPlaylist
            ? MusicViewModel?.FilteredAlbumList?.Count ?? 0
            : GetCurrentPlaylistItems()?.Count ?? 0;

        private void SyncSearchTextFromVisibleCollection()
        {
            _isSyncingSearchText = true;
            SearchText = ShowPlaylist
                ? MusicViewModel?.SearchAlbumText ?? string.Empty
                : MusicViewModel?.SearchText ?? string.Empty;
            _isSyncingSearchText = false;
        }

        private void SyncPlaybackStateFromMusicViewModel()
        {
            if (MusicViewModel == null)
                return;

            var activeItem = MusicViewModel.AudioPlayer?.CurrentMediaItem ?? MusicViewModel.SelectedMediaItem;
            if (activeItem == null)
                return;

            var activeItemIndex = MusicViewModel.CoverItems.IndexOf(activeItem);
            var activeItemIsInCurrentPlaylist = activeItemIndex >= 0;

            var previousLoadedItem = LoadedMediaItem;
            var loadedItemChanged = !ReferenceEquals(previousLoadedItem, activeItem);
            if (loadedItemChanged)
                LoadedMediaItem = activeItem;

            var shouldFollowActiveSelection =
                SelectedMediaItem == null ||
                ReferenceEquals(SelectedMediaItem, previousLoadedItem) ||
                SelectedPlaylistIndex < 0;

            if (shouldFollowActiveSelection && activeItemIsInCurrentPlaylist && !ReferenceEquals(SelectedMediaItem, activeItem))
                SelectedMediaItem = activeItem;

            if (loadedItemChanged && shouldFollowActiveSelection && activeItemIsInCurrentPlaylist)
            {
                if (SelectedPlaylistIndex != activeItemIndex)
                    SelectedPlaylistIndex = activeItemIndex;
            }
        }

        private void SyncSelectedAlbumFromMusicViewModel()
        {
            if (MusicViewModel == null)
                return;

            var album = MusicViewModel.LoadedAlbum ?? MusicViewModel.SelectedAlbum;
            if (!ReferenceEquals(SelectedAlbum, album))
                SelectedAlbum = album;
        }

        private FolderMediaItem? FindPersistedAlbum(string? albumFileName, string? albumTitle)
        {
            var albums = MusicViewModel?.AlbumList;
            if (albums == null || albums.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(albumFileName))
            {
                var byFileName = albums.FirstOrDefault(album =>
                    string.Equals(album.FileName, albumFileName, StringComparison.OrdinalIgnoreCase));
                if (byFileName != null)
                    return byFileName;
            }

            if (!string.IsNullOrWhiteSpace(albumTitle))
            {
                return albums.FirstOrDefault(album =>
                    string.Equals(album.Title, albumTitle, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private MediaItem? FindPersistedPlaylistItem(AvaloniaList<MediaItem>? playlistItems, string? itemFileName, int selectedIndex)
        {
            if (playlistItems == null || playlistItems.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(itemFileName))
            {
                var byFileName = playlistItems.FirstOrDefault(item =>
                    string.Equals(item.FileName, itemFileName, StringComparison.OrdinalIgnoreCase));
                if (byFileName != null)
                    return byFileName;
            }

            if (selectedIndex >= 0 && selectedIndex < playlistItems.Count)
                return playlistItems[selectedIndex];

            return null;
        }

        private MediaItem? FindMediaItemByFileName(IEnumerable<MediaItem>? items, string? fileName)
        {
            if (items == null || string.IsNullOrWhiteSpace(fileName))
                return null;

            return items.FirstOrDefault(item =>
                string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        }

        private void ApplySelectedPlaylistItem(MediaItem item)
        {
            if (MusicViewModel == null)
                return;

            var playlistItems = GetCurrentPlaylistItems();
            if (playlistItems == null)
                return;

            var index = playlistItems.IndexOf(item);
            if (index < 0)
                return;

            SelectedMediaItem = item;
            if (SelectedPlaylistIndex != index)
                SelectedPlaylistIndex = index;

            MusicViewModel.SelectedMediaItem = item;
            MusicViewModel.SelectedIndex = index;
            MusicViewModel.PointedIndex = index;
            MusicViewModel.HighlightedItem = item;
        }

        private void RestoreOpenedAlbumAndSelection(System.Text.Json.Nodes.JsonObject section)
        {
            _pendingOpenedAlbumFileName = ReadStringSetting(section, "OpenedAlbumFileName", null);
            _pendingOpenedAlbumTitle = ReadStringSetting(section, "OpenedAlbumTitle", null);
            _pendingSelectedItemFileName = ReadStringSetting(section, "SelectedItemFileName", null);
            _pendingSelectedItemIndex = ReadIntSetting(section, "SelectedItemIndex", -1);
            _pendingLastPlayedFileName = ReadStringSetting(section, "LastPlayedFile", null);

            _hasPendingMiniStateRestore =
                !string.IsNullOrWhiteSpace(_pendingOpenedAlbumFileName) ||
                !string.IsNullOrWhiteSpace(_pendingOpenedAlbumTitle) ||
                !string.IsNullOrWhiteSpace(_pendingSelectedItemFileName) ||
                _pendingSelectedItemIndex >= 0 ||
                !string.IsNullOrWhiteSpace(_pendingLastPlayedFileName);

            TryRestorePendingMiniState();
            SchedulePendingMiniStateRestore();
        }

        private void TryRestorePendingMiniState()
        {
            if (!_hasPendingMiniStateRestore || _isRestoringPersistedMiniState || MusicViewModel == null)
                return;

            var hasPersistedAlbum = !string.IsNullOrWhiteSpace(_pendingOpenedAlbumFileName) || !string.IsNullOrWhiteSpace(_pendingOpenedAlbumTitle);
            if (hasPersistedAlbum && MusicViewModel.AlbumList.Count == 0)
                return;

            _isRestoringPersistedMiniState = true;
            try
            {
                var album = FindPersistedAlbum(_pendingOpenedAlbumFileName, _pendingOpenedAlbumTitle);
                if (album != null)
                {
                    MusicViewModel.ResetPlaybackOnAlbumSwitch = true;
                    MusicViewModel.SelectedAlbum = album;
                    SelectedAlbum = album;
                    MusicViewModel.OpenSelectedFolderCommand.Execute(null);
                    ShowPlaylist = false;
                    SyncSearchTextFromVisibleCollection();
                    SubscribeToCurrentPlaylist();

                    var playlistItems = GetCurrentPlaylistItems();
                    var selectedItem = FindPersistedPlaylistItem(playlistItems, _pendingSelectedItemFileName, _pendingSelectedItemIndex);
                    if (selectedItem != null)
                        ApplySelectedPlaylistItem(selectedItem);

                    var loadedItem = FindMediaItemByFileName(playlistItems, _pendingLastPlayedFileName)
                        ?? FindMediaItemByFileName(MediaItems, _pendingLastPlayedFileName);
                    if (loadedItem != null)
                        LoadedMediaItem = loadedItem;

                    _hasPendingMiniStateRestore = false;
                    return;
                }

                if (!hasPersistedAlbum && !string.IsNullOrWhiteSpace(_pendingLastPlayedFileName) && MediaItems != null)
                {
                    var found = MediaItems.FirstOrDefault(m => string.Equals(m.FileName, _pendingLastPlayedFileName, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        SelectedMediaItem = found;
                        LoadedMediaItem = found;
                    }

                    _hasPendingMiniStateRestore = false;
                }
            }
            finally
            {
                _isRestoringPersistedMiniState = false;
            }
        }

        private void SchedulePendingMiniStateRestore()
        {
            if (!_hasPendingMiniStateRestore || _isWaitingForPendingMiniStateRestore)
                return;

            _isWaitingForPendingMiniStateRestore = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 0; attempt < 120; attempt++)
                    {
                        var restoreComplete = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            TryRestorePendingMiniState();
                            return !_hasPendingMiniStateRestore;
                        });

                        if (restoreComplete)
                            return;

                        await Task.Delay(100);
                    }
                }
                finally
                {
                    _isWaitingForPendingMiniStateRestore = false;
                }
            });
        }

        private void ResetStoppedDisplay()
        {
            LoadedMediaItem = CreateStoppedPlaceholder();
            OnPropertyChanged(nameof(DisplayDuration));
        }

        private MediaItem CreateStoppedPlaceholder() => new()
        {
            Title = "No File Loaded",
            Artist = string.Empty,
            Album = string.Empty,
            Duration = 0,
            Index = 0,
            CoverBitmap = _defaultCover
        };

        private bool TryTargetPlaylistItem(MediaItem? item, out MediaItem? targetItem)
        {
            targetItem = item ?? SelectedMediaItem;
            if (targetItem == null || MusicViewModel == null)
                return false;

            var index = MusicViewModel.CoverItems.IndexOf(targetItem);
            if (index < 0)
                return false;

            SelectedMediaItem = targetItem;
            MusicViewModel.PointedIndex = index;
            MusicViewModel.HighlightedItem = targetItem;
            return true;
        }

        private void EnsureFirstPlaylistItemSelected()
        {
            if (MusicViewModel == null)
                return;

            var playlistItems = GetCurrentPlaylistItems();
            if (playlistItems == null || playlistItems.Count == 0)
                return;

            if (SelectedMediaItem != null && playlistItems.Contains(SelectedMediaItem))
                return;

            var firstItem = playlistItems[0];
            SelectedMediaItem = firstItem;
            if (SelectedPlaylistIndex != 0)
                SelectedPlaylistIndex = 0;

            MusicViewModel.SelectedMediaItem = firstItem;
            MusicViewModel.SelectedIndex = 0;
            MusicViewModel.PointedIndex = 0;
            MusicViewModel.HighlightedItem = firstItem;
        }

        private void SubscribeToCurrentPlaylist()
        {
            try
            {
                var playlistItems = GetCurrentPlaylistItems();
                if (ReferenceEquals(_mediaItemsSubscribed, playlistItems))
                {
                    UpdateItemIndices();
                    UpdateTotalDuration();
                    return;
                }

                UnsubscribeFromCollection(_mediaItemsSubscribed);
                SubscribeToCollection(playlistItems);
                _mediaItemsSubscribed = playlistItems;
                SelectedItems = [];

                if (SelectedMediaItem != null && (playlistItems == null || !playlistItems.Contains(SelectedMediaItem)))
                {
                    SelectedPlaylistIndex = -1;
                    SelectedMediaItem = null;
                }

                UpdateItemIndices();
                UpdateTotalDuration();
                OnPropertyChanged(nameof(VisibleItemCount));
                EnsureFirstPlaylistItemSelected();
            }
            catch (Exception ex)
            {
                Log.Warn("SubscribeToCurrentPlaylist failed", ex);
            }
        }

        private void MusicViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MusicViewModel.SelectedMediaItem))
            {
                SyncPlaybackStateFromMusicViewModel();
            }
            else if (e.PropertyName == nameof(MusicViewModel.AlbumList) ||
                     e.PropertyName == nameof(MusicViewModel.IsPrepared))
            {
                TryRestorePendingMiniState();
                SchedulePendingMiniStateRestore();
            }
            else if (e.PropertyName == nameof(MusicViewModel.CoverItems))
            {
                SubscribeToCurrentPlaylist();
                SyncPlaybackStateFromMusicViewModel();
                TryRestorePendingMiniState();
            }
            else if (e.PropertyName == nameof(MusicViewModel.SearchText) && !ShowPlaylist)
            {
                SyncSearchTextFromVisibleCollection();
            }
            else if (e.PropertyName == nameof(MusicViewModel.SearchAlbumText) && ShowPlaylist)
            {
                SyncSearchTextFromVisibleCollection();
            }
            else if (e.PropertyName == nameof(MusicViewModel.SelectedAlbum))
            {
                if (MusicViewModel?.LoadedAlbum == null)
                    SyncSelectedAlbumFromMusicViewModel();
            }
            else if (e.PropertyName == nameof(MusicViewModel.LoadedAlbum))
            {
                ShowPlaylist = MusicViewModel?.LoadedAlbum == null;
                SyncSelectedAlbumFromMusicViewModel();
                SubscribeToCurrentPlaylist();
                OnPropertyChanged(nameof(VisibleItemCount));
                TryRestorePendingMiniState();
            }
            else if (e.PropertyName == nameof(MusicViewModel.FilteredAlbumList))
            {
                SyncSelectedAlbumFromMusicViewModel();
                OnPropertyChanged(nameof(VisibleItemCount));
                TryRestorePendingMiniState();
                SchedulePendingMiniStateRestore();
            }
        }

        #endregion

        #region Settings persistence
        protected override void OnSaveSettings(System.Text.Json.Nodes.JsonObject section)
        {
            WriteCollectionSetting(section, "MediaItems", "MediaItem", MediaItems);
            WriteSetting(section, "WindowWidth", WindowWidth);
            WriteSetting(section, "WindowHeight", WindowHeight);
            WriteSetting(section, "RepeatMode", (int)(MusicViewModel?.AudioPlayer?.RepeatMode ?? RepeatMode.Off));
            // Persist visualizer toggle so the mini player restores it on next run
            WriteSetting(section, "IsVisualizerActive", IsVisualizerActive);
            var openedAlbum = MusicViewModel?.LoadedAlbum ?? SelectedAlbum;
            if (openedAlbum != null)
            {
                if (!string.IsNullOrWhiteSpace(openedAlbum.FileName))
                    WriteSetting(section, "OpenedAlbumFileName", openedAlbum.FileName);
                if (!string.IsNullOrWhiteSpace(openedAlbum.Title))
                    WriteSetting(section, "OpenedAlbumTitle", openedAlbum.Title);
            }

            var selectedItem = SelectedMediaItem;
            if (selectedItem != null)
            {
                if (!string.IsNullOrWhiteSpace(selectedItem.FileName))
                    WriteSetting(section, "SelectedItemFileName", selectedItem.FileName);
                WriteSetting(section, "SelectedItemIndex", SelectedPlaylistIndex);
            }

            var last = LoadedMediaItem?.FileName ?? SelectedMediaItem?.FileName;
            if (!string.IsNullOrEmpty(last)) WriteSetting(section, "LastPlayedFile", last);
        }

        protected override void OnLoadSettings(System.Text.Json.Nodes.JsonObject section)
        {
            MediaItems = ReadCollectionSetting<MediaItem>(section, "MediaItems", "MediaItem", []);
            UpdateItemIndices();
            WindowHeight = ReadDoubleSetting(section, "WindowHeight", 486);
            WindowWidth = ReadDoubleSetting(section, "WindowWidth", 550);
            switch (ReadIntSetting(section, "RepeatMode", 0))
            {
                case 0: if (MusicViewModel?.AudioPlayer != null) MusicViewModel.AudioPlayer.RepeatMode = RepeatMode.Off; break;
                case 1: if (MusicViewModel?.AudioPlayer != null) MusicViewModel.AudioPlayer.RepeatMode = RepeatMode.One; break;
                case 2: if (MusicViewModel?.AudioPlayer != null) MusicViewModel.AudioPlayer.RepeatMode = RepeatMode.All; break;
                case 3: if (MusicViewModel?.AudioPlayer != null) MusicViewModel.AudioPlayer.RepeatMode = RepeatMode.Shuffle; break;
            }
            RestoreOpenedAlbumAndSelection(section);
            TryRestorePendingMiniState();
            // Restore visualizer state if previously active
            var visActive = ReadBoolSetting(section, "IsVisualizerActive", false);
            if (visActive)
            {
                var desiredVm = DiLocator.ResolveViewModel<VisualizerViewModel>();
                if (desiredVm != null)
                {
                    desiredVm.MinViewModel = this;
                    desiredVm.MusicViewModel = MusicViewModel;
                    desiredVm.SettingsViewModel = SettingsViewModel;
                    ExtensionView = desiredVm;
                    ExtensionAreaOpen = true;
                }
            }
            if (MusicViewModel != null && MusicViewModel.AudioPlayer != null && MediaItems != null && MediaItems.Count > 0)
                _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
        }
        #endregion
    }
}
