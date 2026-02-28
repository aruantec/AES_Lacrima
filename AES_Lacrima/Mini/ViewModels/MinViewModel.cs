using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Lacrima.ViewModels;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        private static readonly ILog Log = LogManager.GetLogger(typeof(MinViewModel));
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        private string _agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
        private Bitmap _defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();

        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        // Override the settings file path to store playlist data separately. You can customize this path as needed.
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "CustomPlaylist.json");

        [ObservableProperty]
        private bool _extensionAreaOpen;

        [ObservableProperty]
        private ObservableObject? _extensionView;

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
        private static readonly Random _random = new();

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
            // Initialize the extension view model for the extension area of the UI.
            ExtensionView = DiLocator.ResolveViewModel<MiniEqualizerViewModel>();
            // Disable waveform visualization in the mini player to optimize performance and reduce visual clutter.
            MusicViewModel?.AudioPlayer?.EnableWaveform = false;
            // Load saved playlist from settings first.
            LoadSettings();

            // If no saved items, fall back to the aggregated album list from MusicViewModel.
            if (MediaItems == null || MediaItems.Count == 0)
            {
                MediaItems = [.. MusicViewModel?.AlbumList?.SelectMany(s => s.Children) ?? []];
                // Ensure runtime indices are initialized for the fallback playlist
                UpdateItemIndices();
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
                player.PropertyChanged -= Player_PropertyChanged;
            }
            catch (Exception ex)
            {
                Log.Warn("AttachAudioPlayerHandlers: defensive unsubscribe failed", ex);
            }

            try
            {
                player.EndReached += OnAudioPlayerEndReached;
                player.PropertyChanged += Player_PropertyChanged;
            }
            catch (Exception ex)
            {
                Log.Warn("AttachAudioPlayerHandlers: subscribe failed", ex);
            }
        }

        private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AES_Controls.Player.AudioPlayer.Volume))
            {
                // Read the volume off the UI thread to avoid blocking the UI thread.
                // The AudioPlayer.Volume getter may synchronously marshal to the dedicated
                // MPV thread which can cause deadlocks if invoked from the UI thread.
                _ = Task.Run(() =>
                {
                    // Post update back to UI thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => IsMuted = MusicViewModel?.AudioPlayer?.Volume == 0);
                });
            }
            else if (e.PropertyName == nameof(AES_Controls.Player.AudioPlayer.RepeatMode))
            {
                // Reflect shuffle/repeat mode in the mini view bindings
                OnPropertyChanged(nameof(ShuffleMode));
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

        [RelayCommand]
        private async Task EjectAsync()
        {
            if (MediaItems == null) return;
            // If we have items in the current playlist, treat eject as "clear playlist".
            if (MediaItems.Count > 0)
            {
                MusicViewModel?.AudioPlayer?.Stop();
                SelectedMediaItem = null;
                LoadedMediaItem = null;
                MediaItems.Clear();
            }
            // If we don't have any items, treat eject as "add folders".
            else
                await AddFolders();
        }

        [RelayCommand]
        private void Drop()
        {
            try
            {
                // Ensure item indices reflect the current order after a drop.
                UpdateItemIndices();
            }
            catch (Exception ex)
            {
                Log.Warn("Drop: UpdateItemIndices failed", ex);
            }
        }

        /// <summary>
        /// Update the runtime-only Index property on each MediaItem to match
        /// the current ordering in the MediaItems collection.
        /// </summary>
        private void UpdateItemIndices()
        {
            try
            {
                if (MediaItems == null) return;
                for (int i = 0; i < MediaItems.Count; i++)
                {
                    var item = MediaItems[i];
                    if (item != null)
                        item.Index = i + 1; // 1-based for display
                }
            }
            catch (Exception ex)
            {
                Log.Warn("UpdateItemIndices failed", ex);
            }
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
            if (MediaItems == null || MediaItems.Count == 0 || LoadedMediaItem == null || MusicViewModel?.AudioPlayer == null) return;

            var index = MediaItems.IndexOf(LoadedMediaItem);
            if (index < 0) return;

            // If shuffle mode is active, pick a random next track
            if (MusicViewModel.AudioPlayer.RepeatMode == RepeatMode.Shuffle)
            {
                int nextIndex = index;
                if (MediaItems.Count == 1)
                {
                    nextIndex = 0;
                }
                else
                {
                    int attempts = 0;
                    while (nextIndex == index && attempts < 8)
                    {
                        nextIndex = _random.Next(0, MediaItems.Count);
                        attempts++;
                    }
                    if (nextIndex == index)
                        nextIndex = (index + 1) % MediaItems.Count;
                }

                var next = MediaItems[nextIndex];
                MusicViewModel?.AudioPlayer.PlayFile(next);
                LoadedMediaItem = next;
                SelectedMediaItem = next;
                return;
            }

            var nextIndexSequential = index + 1;
            if (nextIndexSequential >= MediaItems.Count) return;

            var nextSequential = MediaItems[nextIndexSequential];
            MusicViewModel?.AudioPlayer.PlayFile(nextSequential);
            LoadedMediaItem = nextSequential;
            SelectedMediaItem = nextSequential;
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
        private async Task AddFolders()
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select folder",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    var path = folders[0].Path.LocalPath;
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    {
                        // Enumerate supported media files in the chosen folder
                        var mediaItems = _supportedTypes
                            .SelectMany(pattern => Directory.EnumerateFiles(path, pattern))
                            .Where(file => {
                                var name = Path.GetFileName(file);
                                return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                            })
                            .Select(file => new MediaItem
                            {
                                FileName = file,
                                Title = Path.GetFileName(file),
                                CoverBitmap = _defaultCover
                            }).ToList();

                        // If the currently loaded media item is not in the new list, we can just replace the collection.
                        if (MediaItems != null && LoadedMediaItem != null && MediaItems.Contains(LoadedMediaItem))
                        {
                            MusicViewModel?.AudioPlayer?.Stop();
                            LoadedMediaItem = null;
                        }

                        // Replace entire MediaItems collection with files from the folder
                        MediaItems = [.. mediaItems];

                        // Persist updated playlist
                        try { SaveSettings(); } catch (Exception ex) { Log.Warn("AddFolders: SaveSettings failed", ex); }

                        // Ensure runtime indices are initialized for the fallback playlist
                        UpdateItemIndices();

                        // Kick off metadata scraping for the newly added items
                        if (MediaItems != null && MediaItems.Count > 0 && MusicViewModel?.AudioPlayer != null)
                        {
                            _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
                        }
                    }
                }
            }
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
                    // Build a quick lookup of already-added file paths (case-insensitive)
                    var existing = new HashSet<string>(MediaItems.Select(m => (m?.FileName ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
                    // Also guard against duplicate selection from the picker
                    var seenInPicker = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        if (string.IsNullOrWhiteSpace(localPath)) continue;
                        localPath = localPath.Trim();

                        // Skip if already present in playlist
                        if (existing.Contains(localPath))
                            continue;

                        // Skip duplicates selected in the same picker operation
                        if (seenInPicker.Contains(localPath))
                            continue;

                        seenInPicker.Add(localPath);

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
            WriteSetting(section, "RepeatMode", (int)(MusicViewModel?.AudioPlayer?.RepeatMode ?? RepeatMode.Off));
            // Persist the last played file path so we can restore selection on next run
            var last = LoadedMediaItem?.FileName ?? SelectedMediaItem?.FileName;
            if (!string.IsNullOrEmpty(last))
                WriteSetting(section, "LastPlayedFile", last);
        }

        protected override void OnLoadSettings(System.Text.Json.Nodes.JsonObject section)
        {
            // Read persisted media items
            MediaItems = ReadCollectionSetting<MediaItem>(section, "MediaItems", "MediaItem", []);
            // Ensure runtime indices are initialized after loading persisted collection
            UpdateItemIndices();
            // Read persisted window size or use defaults
            WindowHeight = ReadDoubleSetting(section, "WindowHeight", 486);
            WindowWidth = ReadDoubleSetting(section, "WindowWidth", 550);
            // Read persisted repeat mode or default to Off.
            switch (ReadIntSetting(section, "RepeatMode", 0))
            {       case 0:
                    MusicViewModel?.AudioPlayer?.RepeatMode = RepeatMode.Off;
                    break;
                case 1:
                    MusicViewModel?.AudioPlayer?.RepeatMode = RepeatMode.One;
                    break;
                case 2:
                    MusicViewModel?.AudioPlayer?.RepeatMode = RepeatMode.All;
                    break;
                case 3:
                    MusicViewModel?.AudioPlayer?.RepeatMode = RepeatMode.Shuffle;
                    break;
            }
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