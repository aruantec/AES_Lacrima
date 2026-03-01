using AES_Controls.Helpers;
using AES_Controls.Player;
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
        #region Private fields
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        private string _agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
        private Bitmap _defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();
        private AvaloniaList<MediaItem>? _mediaItemsSubscribed;
        private bool _isSwitchingExtension;
        private BitmapColorHelper _colorHelper = new();

        #endregion

        #region Static / readonly fields
        private static readonly ILog Log = LogManager.GetLogger(typeof(MinViewModel));
        private static readonly Random _random = new();
        private static readonly TimeSpan ExtensionTransitionDelay = TimeSpan.FromMilliseconds(620);

        #endregion

        #region Observable / AutoResolve properties
        // Settings path
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "CustomPlaylist.json");

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
            UpdateFilteredItems();
        }

        // filtered view of MediaItems based on SearchText; bound to the ListBox
        [ObservableProperty]
        private AvaloniaList<MediaItem>? _filteredMediaItems;

        [ObservableProperty]
        private MediaItem? _selectedMediaItem;

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

        [ObservableProperty]
        private bool _isVisualizerActive;

        [ObservableProperty]
        private bool _isEqualizerActive;

        // supported types
        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        #endregion

        #region Public properties
        public double DisplayDuration => LoadedMediaItem?.Duration ?? SelectedMediaItem?.Duration ?? 0.0;

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
            if (MediaItems == null) return;
            if (MediaItems.Count > 0)
            {
                MusicViewModel?.AudioPlayer?.Stop();
                SelectedMediaItem = null;
                LoadedMediaItem = null;
                MediaItems.Clear();
            }
            else
                await AddFolders();
        }

        [RelayCommand]
        private void Drop()
        {
            try
            {
                UpdateItemIndices();
            }
            catch (Exception ex)
            {
                Log.Warn("Drop: UpdateItemIndices failed", ex);
            }
        }

        [RelayCommand]
        private void ToggleSettings() => SettingsVisible = !SettingsVisible;

        [RelayCommand]
        private void ClearSearch()
        {
            SearchText = string.Empty;
            UpdateFilteredItems();
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
        private void PlaySelectedMediaItem()
        {
            if (SelectedMediaItem == null) return;
            MusicViewModel?.AudioPlayer?.PlayFile(SelectedMediaItem);
            LoadedMediaItem = SelectedMediaItem;
            UpdateLoadedBrush(SelectedMediaItem);
        }

        [RelayCommand]
        private void PlayPause()
        {
            if (MusicViewModel == null || MusicViewModel.AudioPlayer == null) return;
            if (MusicViewModel.AudioPlayer.IsPlaying)
                MusicViewModel.AudioPlayer.Pause();
            else if (MusicViewModel.AudioPlayer.CurrentMediaItem == null && MediaItems != null && MediaItems.Count > 0)
            {
                if (SelectedMediaItem == null && MediaItems.FirstOrDefault() is MediaItem firstItem)
                    SelectedMediaItem = firstItem;
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
            if (MusicViewModel.AudioPlayer.RepeatMode == RepeatMode.Shuffle)
            {
                int nextIndex = index;
                if (MediaItems.Count == 1) nextIndex = 0;
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
        private void Stop() => MusicViewModel?.AudioPlayer?.Stop();

        [RelayCommand]
        private void SetPosition(double position) => MusicViewModel?.AudioPlayer?.SetPosition(position);

        [RelayCommand]
        private void DeleteSelectedItems()
        {
            if (SelectedItems == null || MediaItems == null) return;
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
            SaveSettings();
        }

        [RelayCommand]
        private async Task AddFolders()
        {
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;
            if (storageProvider == null) return;
            var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select folder", AllowMultiple = false });
            if (folders.Count == 0) return;
            var path = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            var mediaItems = _supportedTypes.SelectMany(pattern => Directory.EnumerateFiles(path, pattern))
                .Where(file => { var name = Path.GetFileName(file); return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith(".")); })
                .Select(file => new MediaItem { FileName = file, Title = Path.GetFileName(file), CoverBitmap = _defaultCover }).ToList();
            if (MediaItems != null && LoadedMediaItem != null && MediaItems.Contains(LoadedMediaItem)) { MusicViewModel?.AudioPlayer?.Stop(); LoadedMediaItem = null; }
            MediaItems = [.. mediaItems];
            try { SaveSettings(); } catch (Exception ex) { Log.Warn("AddFolders: SaveSettings failed", ex); }
            UpdateItemIndices();
            if (MediaItems != null && MediaItems.Count > 0 && MusicViewModel?.AudioPlayer != null)
                _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
        }

        [RelayCommand]
        private async Task AddFilesAsync()
        {
            if (MediaItems == null) return;
            var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;
            if (storageProvider == null) return;
            var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Add Audio Files", AllowMultiple = true, FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Audio Files") { Patterns = _supportedTypes } } });
            if (files.Count == 0) return;
            var existing = new HashSet<string>(MediaItems.Select(m => (m?.FileName ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
            var seenInPicker = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var localPath = file.Path.LocalPath; if (string.IsNullOrWhiteSpace(localPath)) continue; localPath = localPath.Trim();
                if (existing.Contains(localPath)) continue;
                if (seenInPicker.Contains(localPath)) continue;
                seenInPicker.Add(localPath);
                var item = new MediaItem { FileName = localPath, Title = Path.GetFileName(localPath), CoverBitmap = _defaultCover };
                MediaItems.Add(item);
            }
            try { SaveSettings(); } catch (Exception ex) { Log.Warn("AddFilesAsync: SaveSettings failed", ex); }
            if (MediaItems.Count > 0 && MusicViewModel?.AudioPlayer != null)
                _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
        }

        #endregion

        #region Constructor / Prepare
        public override void Prepare()
        {
            ExtensionView = DiLocator.ResolveViewModel<MiniEqualizerViewModel>();
            MusicViewModel?.AudioPlayer?.EnableWaveform = false;
            LoadSettings();
            if (MediaItems == null || MediaItems.Count == 0)
            {
                MediaItems = [.. MusicViewModel?.AlbumList?.SelectMany(s => s.Children) ?? []];
                UpdateItemIndices();
            }
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
        }

        partial void OnSelectedMediaItemChanged(MediaItem? value)
        {
            OnPropertyChanged(nameof(DisplayDuration));
        }

        partial void OnMusicViewModelChanged(MusicViewModel? value)
        {
            try { if (value?.AudioPlayer != null) AttachAudioPlayerHandlers(value.AudioPlayer); }
            catch (Exception ex) { Log.Warn("OnMusicViewModelChanged failed to attach audio handlers", ex); }
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
            LoadedBrush = null; IsCoverPlaceholder = true;
            if (item?.CoverBitmap != null && item.CoverBitmap != _defaultCover)
            {
                LoadedBrush = new SolidColorBrush(BitmapColorHelper.GetDominantColor(item.CoverBitmap));
                IsCoverPlaceholder = false;
            }
            ControlsBrush = LoadedBrush;
            ColorGradientBrush = _colorHelper.GetColorGradient(item?.CoverBitmap! != _defaultCover ? item?.CoverBitmap! : null!);
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
                UpdateFilteredItems();
            }
            catch (Exception ex) { Log.Warn("MediaItems_CollectionChanged failed", ex); }
        }

        private void MediaItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MediaItem.Duration)) { UpdateTotalDuration(); OnPropertyChanged(nameof(DisplayDuration)); }
        }

        private void UpdateTotalDuration()
        {
            try { TotalDuration = MediaItems?.Sum(m => m?.Duration ?? 0.0) ?? 0.0; }
            catch (Exception ex) { Log.Warn("UpdateTotalDuration failed", ex); TotalDuration = 0.0; }
        }

        private void AttachAudioPlayerHandlers(AudioPlayer? player)
        {
            if (player == null) return;
            try { player.EndReached -= OnAudioPlayerEndReached; player.PropertyChanged -= Player_PropertyChanged; }
            catch (Exception ex) { Log.Warn("AttachAudioPlayerHandlers: defensive unsubscribe failed", ex); }
            try { player.EndReached += OnAudioPlayerEndReached; player.PropertyChanged += Player_PropertyChanged; }
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
            else if (e.PropertyName == nameof(AudioPlayer.RepeatMode))
            {
                OnPropertyChanged(nameof(ShuffleMode));
            }
        }

        private void OnAudioPlayerEndReached(object? sender, EventArgs e)
        {
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    if (MusicViewModel?.AudioPlayer?.RepeatMode == RepeatMode.All && MediaItems != null && MediaItems.Count > 0 && LoadedMediaItem != null && MediaItems.IndexOf(LoadedMediaItem) == MediaItems.Count - 1 && MediaItems.FirstOrDefault() is MediaItem firstItem)
                    {
                        SelectedMediaItem = firstItem; PlaySelectedMediaItem();
                    }
                    else Next();
                }
                catch (Exception ex) { Log.Warn("OnAudioPlayerEndReached: Next() failed", ex); }
            });
        }

        private void UpdateItemIndices()
        {
            try
            {
                if (MediaItems == null) return;
                for (int i = 0; i < MediaItems.Count; i++) { var item = MediaItems[i]; if (item != null) item.Index = i + 1; }
            }
            catch (Exception ex) { Log.Warn("UpdateItemIndices failed", ex); }
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
            var last = ReadStringSetting(section, "LastPlayedFile", null);
            if (!string.IsNullOrEmpty(last) && MediaItems != null)
            {
                var found = MediaItems.FirstOrDefault(m => string.Equals(m.FileName, last, StringComparison.OrdinalIgnoreCase));
                if (found != null) { SelectedMediaItem = found; LoadedMediaItem = found; }
            }
            // Restore visualizer state if previously active
            var visActive = ReadBoolSetting(section, "IsVisualizerActive", false);
            if (visActive)
            {
                var desiredVm = DiLocator.ResolveViewModel<VisualizerViewModel>();
                if (desiredVm != null)
                {
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