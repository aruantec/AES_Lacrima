using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.Interfaces;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
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
    internal partial class MinViewModel : ViewModelBase, IMinViewModel
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MinViewModel));
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        private string _agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
        private Bitmap _defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();

        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        // Override the settings file path to store playlist data separately. You can customize this path as needed.
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "CustomPlaylist.json");

        [ObservableProperty]
        private bool _settingsVisible;

        [ObservableProperty]
        private AvaloniaList<MediaItem>? _mediaItems;

        [ObservableProperty]
        private MediaItem? _selectedMediaItem;

        [ObservableProperty]
        private MediaItem? _loadedMediaItem;

        [ObservableProperty]
        private AvaloniaList<MediaItem>? _selectedItems = [];

        [ObservableProperty]
        private bool _isMuted;

        [ObservableProperty]
        private bool _showPlaylist = true;

        [ObservableProperty]
        private double _windowWidth = 550;

        [ObservableProperty]
        private double _windowHeight = 486;

        // Brush used for list selection/highlight.
        [ObservableProperty]
        private IBrush? _selectionBrush = new SolidColorBrush(Color.Parse("#005CFE"));

        [ObservableProperty]
        private IBrush? _loadedBrush;

        [ObservableProperty]
        private IBrush? _controlsBrush;

        [ObservableProperty]
        private bool _isCoverPlaceholder = true;

        // Total duration (seconds) of all items in the current MediaItems list
        [ObservableProperty]
        private double _totalDuration;

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        // Keep track of the currently-subscribed collection so we can unsubscribe
        private AvaloniaList<MediaItem>? _mediaItemsSubscribed;

        // Subscribe to collection and item changes so TotalDuration stays up-to-date
        partial void OnMediaItemsChanged(AvaloniaList<MediaItem>? value)
        {
            try
            {
                // Unsubscribe previous collection/items
                UnsubscribeFromCollection(_mediaItemsSubscribed);

                // Subscribe new
                SubscribeToCollection(value);
                _mediaItemsSubscribed = value;

                // Update total duration immediately
                UpdateTotalDuration();
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
        }

        partial void OnSelectedMediaItemChanged(MediaItem? value)
        {
            OnPropertyChanged(nameof(DisplayDuration));
        }

        private void UpdateLoadedBrush(MediaItem? item)
        {
            LoadedBrush = null;
            IsCoverPlaceholder = true;

            // If the loaded media item has a cover bitmap and it's not the default placeholder, extract the dominant color and create a brush from it.
            if (item?.CoverBitmap != null && item.CoverBitmap != _defaultCover)
            {
                LoadedBrush = new SolidColorBrush(BitmapColorHelper.GetDominantColor(item.CoverBitmap));
                IsCoverPlaceholder = false;
            }

            // If we have a loaded brush, use it for controls; otherwise set to null.
            if (LoadedBrush != null)
                ControlsBrush = LoadedBrush;
            else 
                ControlsBrush = null;
        }

        private void SubscribeToCollection(AvaloniaList<MediaItem>? list)
        {
            if (list == null) return;

            if (list is INotifyCollectionChanged incc)
                incc.CollectionChanged += MediaItems_CollectionChanged;

            foreach (var item in list)
            {
                if (item is INotifyPropertyChanged inpc)
                    inpc.PropertyChanged += MediaItem_PropertyChanged;
            }
        }

        private void UnsubscribeFromCollection(AvaloniaList<MediaItem>? list)
        {
            if (list == null) return;

            if (list is INotifyCollectionChanged incc)
                incc.CollectionChanged -= MediaItems_CollectionChanged;

            foreach (var item in list)
            {
                if (item is INotifyPropertyChanged inpc)
                    inpc.PropertyChanged -= MediaItem_PropertyChanged;
            }
        }

        private void MediaItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                // Attach/detach item property changed handlers as items are added/removed
                if (e.NewItems != null)
                {
                    foreach (var ni in e.NewItems.Cast<MediaItem>())
                    {
                        if (ni is INotifyPropertyChanged inpc)
                            inpc.PropertyChanged += MediaItem_PropertyChanged;
                    }
                }

                if (e.OldItems != null)
                {
                    foreach (var oi in e.OldItems.Cast<MediaItem>())
                    {
                        if (oi is INotifyPropertyChanged inpc)
                            inpc.PropertyChanged -= MediaItem_PropertyChanged;
                    }
                }

                // Recalculate total duration after any collection change
                UpdateTotalDuration();
            }
            catch (Exception ex)
            {
                Log.Warn("MediaItems_CollectionChanged failed", ex);
            }
        }

        private void MediaItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MediaItem.Duration))
            {
                UpdateTotalDuration();
                // Ensure UI bound to the effective/visible duration updates when an item's duration changes
                OnPropertyChanged(nameof(DisplayDuration));
            }
        }

        /// <summary>
        /// Duration to display in the minimized view. Falls back from LoadedMediaItem to SelectedMediaItem
        /// and finally to 0.0 when neither is available.
        /// </summary>
        public double DisplayDuration => LoadedMediaItem?.Duration ?? SelectedMediaItem?.Duration ?? 0.0;

        private void UpdateTotalDuration()
        {
            try
            {
                TotalDuration = MediaItems?.Sum(m => m?.Duration ?? 0.0) ?? 0.0;
            }
            catch (Exception ex)
            {
                Log.Warn("UpdateTotalDuration failed", ex);
                TotalDuration = 0.0;
            }
        }

        public override void Prepare()
        {
            // Load saved playlist from settings first.
            LoadSettings();

            // If no saved items, fall back to the aggregated album list from MusicViewModel.
            if (MediaItems == null || MediaItems.Count == 0)
            {
                MediaItems = [.. MusicViewModel?.AlbumList?.SelectMany(s => s.Children) ?? []];
            }

            // Ensure total duration is calculated for the current list
            UpdateTotalDuration();

            // Subscribe to AudioPlayer duration changes so we can persist the duration
            try
            {
                if (MusicViewModel?.AudioPlayer != null)
                {
                    AttachAudioPlayerHandlers(MusicViewModel.AudioPlayer);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Prepare: failed to attach audio player handlers", ex);
            }
        }

        partial void OnMusicViewModelChanged(MusicViewModel? value)
        {
            try
            {
                if (value?.AudioPlayer != null)
                    AttachAudioPlayerHandlers(value.AudioPlayer);
            }
            catch (Exception ex)
            {
                Log.Warn("OnMusicViewModelChanged failed to attach audio handlers", ex);
            }
        }

        private void AttachAudioPlayerHandlers(AES_Controls.Player.AudioPlayer? player)
        {
            if (player == null) return;
            try
            {
                // Defensive: unsubscribe first to avoid duplicate subscriptions
                player.EndReached -= OnAudioPlayerEndReached;
            }
            catch (Exception ex)
            {
                Log.Warn("AttachAudioPlayerHandlers: defensive unsubscribe failed", ex);
            }

            try
            {
                player.EndReached += OnAudioPlayerEndReached;
            }
            catch (Exception ex)
            {
                Log.Warn("AttachAudioPlayerHandlers: subscribe failed", ex);
            }
        }

        private void OnAudioPlayerEndReached(object? sender, EventArgs e)
        {
            // Invoke Next on the UI thread to maintain same behavior as MusicViewModel
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try 
                {
                    if (MusicViewModel?.AudioPlayer?.RepeatMode == RepeatMode.All
                        && MediaItems != null && MediaItems.Count > 0
                        && LoadedMediaItem != null
                        && MediaItems.IndexOf(LoadedMediaItem) == MediaItems.Count -1
                        && MediaItems.FirstOrDefault() is MediaItem firstItem)
                    {
                        SelectedMediaItem = firstItem;
                        PlaySelectedMediaItem();
                    }
                    else
                        Next();
                }
                catch (Exception ex)
                {
                    Log.Warn("OnAudioPlayerEndReached: Next() failed", ex);
                }
            });
        }

        /// <summary>
        /// Handles changes to the muted state of the audio player by updating the volume accordingly.
        /// </summary>
        partial void OnIsMutedChanging(bool value)
        {
            MusicViewModel?.AudioPlayer?.Volume = value ? 0.0 : 100.0;
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            SettingsVisible = !SettingsVisible;
        }

        /// <summary>
        /// Minimizes the application.
        /// </summary>
        [RelayCommand]
        private void MinimizeWindow()
        {
            AppLifetime?.MainWindow?.WindowState = Avalonia.Controls.WindowState.Minimized;
        }

        /// <summary>
        /// Closes the application and performs necessary cleanup operations before shutdown.
        /// </summary>
        [RelayCommand]
        private void CloseApplication()
        {
            if (AppLifetime == null || DiLocator.ResolveViewModel<ISettingsService>() is not { } settingsService)
                return;
            //Save settings
            SaveSettings();
            //Shutdown application
            AppLifetime.Shutdown();
        }

        [RelayCommand]
        private void PlaySelectedMediaItem()
        {
            if (SelectedMediaItem != null)
            {
                MusicViewModel?.AudioPlayer?.PlayFile(SelectedMediaItem);
                LoadedMediaItem = SelectedMediaItem;
                // Update the loaded brush to match the newly loaded media item
                UpdateLoadedBrush(SelectedMediaItem);
            }
        }

        [RelayCommand]
        private void PlayPause()
        {
            if (MusicViewModel == null || MusicViewModel.AudioPlayer == null) return;

            if (MusicViewModel.AudioPlayer.IsPlaying)
                MusicViewModel.AudioPlayer.Pause();
            // If we have a loaded media item, resume playing it.
            else if (MusicViewModel.AudioPlayer.CurrentMediaItem == null && MediaItems != null && MediaItems.Count > 0)
            {
                // If we don't have a currently loaded media item, start playing the selected one.
                if (SelectedMediaItem == null && MediaItems.FirstOrDefault() is MediaItem firstItem)
                    SelectedMediaItem = firstItem;
                // If we have a selected media item, play it; otherwise do nothing.
                PlaySelectedMediaItem();
            }
            else
                MusicViewModel.AudioPlayer.Play();
        }

        [RelayCommand]
        private void Next()
        {
            if (MediaItems == null || LoadedMediaItem == null || MusicViewModel?.AudioPlayer == null) return;

            var index = MediaItems.IndexOf(LoadedMediaItem);
            if (index < 0) return;

            var nextIndex = index + 1;
            if (nextIndex >= MediaItems.Count) return;

            var next = MediaItems[nextIndex];
            MusicViewModel?.AudioPlayer.PlayFile(next);
            LoadedMediaItem = next;
            SelectedMediaItem = next;
        }

        [RelayCommand]
        private void Previous()
        {
            if (MediaItems == null || LoadedMediaItem == null || MusicViewModel?.AudioPlayer == null) return;

            var index = MediaItems.IndexOf(LoadedMediaItem);
            if (index <= 0) return;

            var prevIndex = index - 1;
            if (prevIndex < 0) return;

            var prev = MediaItems[prevIndex];
            MusicViewModel?.AudioPlayer.PlayFile(prev);
            LoadedMediaItem = prev;
            SelectedMediaItem = prev;
        }

        [RelayCommand]
        private void Stop()
        {
            MusicViewModel?.AudioPlayer?.Stop();
        }

        [RelayCommand]
        private void SetPosition(double position)
        {
            MusicViewModel?.AudioPlayer?.SetPosition(position);
        }

        [RelayCommand]
        private void DeleteSelectedItems()
        {
            if (SelectedItems == null || MediaItems == null) return;

            // Remove selected items safely
            var itemsToRemove = SelectedItems.ToList();
            foreach (var item in itemsToRemove)
            {
                if (item == LoadedMediaItem)
                {
                    MusicViewModel?.AudioPlayer?.Stop();
                    LoadedMediaItem = null;
                }
                MediaItems.Remove(item);
            }
            // Persist changes
            SaveSettings();
        }

        [RelayCommand]
        private void AddFolders()
        {
            // Logic not implemented per user request
        }

        [RelayCommand]
        private async Task AddFilesAsync()
        {
            if (MediaItems == null) return;

            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Add Audio Files",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("Audio Files")
                        {
                            Patterns = _supportedTypes
                        }
                    ]
                });

                if (files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        var item = new MediaItem
                        {
                            FileName = localPath,
                            Title = Path.GetFileName(localPath),
                            CoverBitmap = _defaultCover
                        };
                        MediaItems.Add(item);
                    }

                    // Persist updated playlist
                    try { SaveSettings(); }
                    catch (Exception ex) { Log.Warn("AddFilesAsync: SaveSettings failed", ex); }

                    if (MediaItems.Count > 0 && MusicViewModel?.AudioPlayer != null)
                        _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
                }
            }
        }

        protected override void OnSaveSettings(System.Text.Json.Nodes.JsonObject section)
        {
            // Persist the media items list
            WriteCollectionSetting(section, "MediaItems", "MediaItem", MediaItems);
            // Persist window size for restoration on next run
            WriteSetting(section, "WindowWidth", WindowWidth);
            WriteSetting(section, "WindowHeight", WindowHeight);
            // Persist the last played file path so we can restore selection on next run
            var last = LoadedMediaItem?.FileName ?? SelectedMediaItem?.FileName;
            if (!string.IsNullOrEmpty(last))
                WriteSetting(section, "LastPlayedFile", last);
        }

        protected override void OnLoadSettings(System.Text.Json.Nodes.JsonObject section)
        {
            // Read persisted media items
            MediaItems = ReadCollectionSetting<MediaItem>(section, "MediaItems", "MediaItem", []);
            // Read persisted window size or use defaults
            WindowHeight = ReadDoubleSetting(section, "WindowHeight", 486);
            WindowWidth = ReadDoubleSetting(section, "WindowWidth", 550);
            // Restore last played file selection if available
            var last = ReadStringSetting(section, "LastPlayedFile", null);
            if (!string.IsNullOrEmpty(last) && MediaItems != null)
            {
                var found = MediaItems.FirstOrDefault(m => string.Equals(m.FileName, last, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    SelectedMediaItem = found;
                    LoadedMediaItem = found;
                    //// Load the media item
                    //PlaySelectedMediaItem();
                    //// Pause immediately to restore selection without starting playback
                    //MusicViewModel?.AudioPlayer?.Pause();
                }
            }
            // If we have loaded items, we can start scrapping metadata.
            if (MusicViewModel != null && MusicViewModel.AudioPlayer != null && MediaItems != null && MediaItems.Count > 0)
                _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
        }
    }
}