using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using TagLib;

namespace AES_Lacrima.ViewModels
{
    /// <summary>
    /// Marker interface for the music view-model used by the view locator and
    /// dependency injection container.
    /// </summary>
    public interface IMusicViewModel { }

    /// <summary>
    /// View-model responsible for music playback and album/folder management
    /// within the application's music view.
    /// </summary>
    [AutoRegister]
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel
    {
        #region Private fields
        // Private fields
        private readonly string[] _supportedTypes = new[] { "*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4" };
        private static readonly HttpClient FastThumbnailClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim FastThumbnailThrottle = new(4);
        private const int FastThumbnailDecodeWidth = 512;
        private const int FolderPreviewCoverCount = 4;
        private const int MetadataScrapperCacheEntries = 80;
        private const int MetadataStaggerDelayMs = 120;

        private TaskbarButton[]? _taskbarButtons;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;

        [ObservableProperty]
        private Action<TaskbarButtonId>? _taskbarAction;

        [ObservableProperty]
        private bool _isEqualizerVisible;

        // last window handle we added thumbnail buttons to; used to re‑initialize after a mode switch
        private IntPtr _taskbarHwnd = IntPtr.Zero;
        private MprisService? _mprisService;

        [ObservableProperty]
        private bool _isAlbumlistOpen;

        [ObservableProperty]
        private Bitmap? _defaultFolderCover;

        [ObservableProperty]
        private int _selectedIndex;

        [ObservableProperty]
        private int _selectedAlbumIndex = -1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeletePointedItemCommand))]
        [NotifyPropertyChangedFor(nameof(IsItemPointed))]
        private int _pointedIndex = -1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFolderPointed))]
        private FolderMediaItem? _pointedFolder;

        [ObservableProperty]
        private MediaItem? _selectedMediaItem;

        [ObservableProperty]
        private MediaItem? _highlightedItem;

        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _albumList = new AvaloniaList<FolderMediaItem>();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddItemsCommand))]
        private AvaloniaList<MediaItem> _coverItems = new AvaloniaList<MediaItem>();

        [ObservableProperty]
        private FolderMediaItem? _selectedAlbum;

        [ObservableProperty]
        private AvaloniaList<MediaItem> _playbackQueue = new AvaloniaList<MediaItem>();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddItemsCommand))]
        [NotifyCanExecuteChangedFor(nameof(AddUrlCommand))]
        private FolderMediaItem? _loadedAlbum;

        [ObservableProperty]
        private bool _isNoAlbumLoadedVisible = true;

        [ObservableProperty]
        private string? _searchText;

        [ObservableProperty]
        private string? _searchAlbumText;

        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _filteredAlbumList = new AvaloniaList<FolderMediaItem>();

        [ObservableProperty]
        private bool _isAddUrlPopupOpen;

        [ObservableProperty]
        private bool _isAddPlaylistPopupOpen;

        [ObservableProperty]
        private string? _addUrlText;

        [ObservableProperty]
        private string? _addPlaylistText;

        [ObservableProperty]
        private bool _isAddingPlaylist;

        private string? _originalFolderTitle;
        private readonly HashSet<FolderMediaItem> _subscribedFolders = [];
        private readonly HashSet<MediaItem> _subscribedAlbumChildren = [];
        private readonly Dictionary<FolderMediaItem, AvaloniaList<MediaItem>> _folderChildrenCollections = [];
        private bool _isSyncingAlbumSelection;
        private bool _scanMissingStreamDurationsOnLoadedAlbum;

        [ObservableProperty]
        private AudioPlayer? _audioPlayer;
        private AudioPlayer? _subscribedAudioPlayer;

        public bool ShuffleMode
        {
            get => AudioPlayer?.RepeatMode == RepeatMode.Shuffle;
            set
            {
                if (AudioPlayer == null) return;
                AudioPlayer.RepeatMode = value ? RepeatMode.Shuffle : RepeatMode.Off;
                OnPropertyChanged(nameof(ShuffleMode));
                OnPropertyChanged(nameof(NextRepeatToolTip));
            }
        }
        #endregion

        #region Public properties
        // Public properties
        public bool IsItemPointed => PointedIndex != -1 && PointedIndex < CoverItems.Count;

        public bool IsFolderPointed => PointedFolder != null;

        public string NextRepeatToolTip
        {
            get
            {
                if (AudioPlayer == null) return "Repeat";
                return AudioPlayer.RepeatMode switch
                {
                    RepeatMode.Off => "Repeat One",
                    RepeatMode.One => "Repeat All",
                    RepeatMode.All => "Shuffle",
                    RepeatMode.Shuffle => "Turn Repeat Off",
                    _ => "Repeat",
                };
            }
        }

        partial void OnAudioPlayerChanged(AudioPlayer? value)
        {
            // Unsubscribe previous
            if (_subscribedAudioPlayer != null)
                _subscribedAudioPlayer.PropertyChanged -= AudioPlayer_PropertyChanged;

            _subscribedAudioPlayer = value;

            if (_subscribedAudioPlayer != null)
            {
                _subscribedAudioPlayer.PropertyChanged += AudioPlayer_PropertyChanged;

                // Sync initial volume settings from persistsed config
                if (SettingsViewModel != null)
                {
                    _subscribedAudioPlayer.SmoothVolumeChange = SettingsViewModel.SmoothVolumeChange;
                    _subscribedAudioPlayer.LogarithmicVolumeControl = SettingsViewModel.LogarithmicVolumeControl;
                    _subscribedAudioPlayer.LoudnessCompensatedVolume = SettingsViewModel.LoudnessCompensatedVolume;
                    _subscribedAudioPlayer.SilenceAdvanceDelayMs = SettingsViewModel.SilenceAdvanceDelayMs;

                    // Sync initial ReplayGain options directly to player's memory cache
                    _ = _subscribedAudioPlayer.RecomputeReplayGainForCurrentAsync(
                        SettingsViewModel.ReplayGainEnabled,
                        SettingsViewModel.ReplayGainUseTags,
                        SettingsViewModel.ReplayGainAnalyzeOnTheFly,
                        SettingsViewModel.ReplayGainPreampDb,
                        SettingsViewModel.ReplayGainTagsPreampDb,
                        SettingsViewModel.ReplayGainTagSource);
                }

                // Initialize taskbar progress
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    if (_subscribedAudioPlayer.IsPlaying)
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.Normal);
                    else if (_subscribedAudioPlayer.Position > 0)
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.Paused);

                    TaskbarProgressHelper.SetProgressValue(_subscribedAudioPlayer.Position, _subscribedAudioPlayer.Duration);
                }
            }

            OnPropertyChanged(nameof(ShuffleMode));
            OnPropertyChanged(nameof(NextRepeatToolTip));
        }

        private IntPtr GetCurrentWindowHandle()
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                return desktop.MainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private void AudioPlayer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioPlayer.RepeatMode))
            {
                OnPropertyChanged(nameof(ShuffleMode));
                OnPropertyChanged(nameof(NextRepeatToolTip));
            }

            // Sync taskbar progress indicator on Windows
            if (AudioPlayer != null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var hwnd = GetCurrentWindowHandle();
                if (_taskbarButtons == null || _taskbarHwnd != hwnd)
                {
                    InitializeTaskbarButtons();
                }

                if (e.PropertyName == nameof(AudioPlayer.Position) || e.PropertyName == nameof(AudioPlayer.Duration))
                {
                    TaskbarProgressHelper.SetProgressValue(AudioPlayer.Position, AudioPlayer.Duration);
                }
                else if (e.PropertyName == nameof(AudioPlayer.IsPlaying))
                {
                    UpdateTaskbarButtons();

                    if (AudioPlayer.IsPlaying)
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.Normal);
                    else if (AudioPlayer.Position > 0 && AudioPlayer.Position < AudioPlayer.Duration - 1.5)
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.Paused);
                    else
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.NoProgress);
                }
                else if (e.PropertyName == nameof(AudioPlayer.IsBuffering))
                {
                    if (AudioPlayer.IsBuffering)
                        TaskbarProgressHelper.SetProgressState(TaskbarProgressBarState.Indeterminate);
                    else
                        TaskbarProgressHelper.SetProgressState(AudioPlayer.IsPlaying ? TaskbarProgressBarState.Normal : TaskbarProgressBarState.Paused);
                }
            }

            if (AudioPlayer != null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _mprisService?.NotifyStateChanged(e.PropertyName);
            }
        }

        public void InitializeTaskbarButtons()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow == null)
                return;

            var hwnd = desktop.MainWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
                return;

            // create buttons only once, but always re‑apply them if the window handle has changed
            if (_taskbarButtons == null)
            {
                // Character codes for Segoe MDL2 Assets
                const string prevChar = "\xE892";
                const string playChar = "\xE768";
                const string pauseChar = "\xE769";
                const string nextChar = "\xE893";

                _playIcon = TaskbarProgressHelper.CreateHIconFromCharacter(playChar, Colors.White);
                _pauseIcon = TaskbarProgressHelper.CreateHIconFromCharacter(pauseChar, Colors.White);

                _taskbarButtons =
                [
                    new TaskbarButton { Id = TaskbarButtonId.Previous, HIcon = TaskbarProgressHelper.CreateHIconFromCharacter(prevChar, Colors.White), Tooltip = "Previous", Flags = THUMBBUTTONFLAGS.Enabled },
                    new TaskbarButton { Id = TaskbarButtonId.PlayPause, HIcon = _playIcon, Tooltip = "Play", Flags = THUMBBUTTONFLAGS.Enabled },
                    new TaskbarButton { Id = TaskbarButtonId.Next, HIcon = TaskbarProgressHelper.CreateHIconFromCharacter(nextChar, Colors.White), Tooltip = "Next", Flags = THUMBBUTTONFLAGS.Enabled }
                ];
            }

            // if the handle changed (e.g. mode switch) or buttons were never applied, add/rehook
            if (_taskbarHwnd != hwnd)
            {
                _taskbarHwnd = hwnd;
                TaskbarProgressHelper.SetThumbnailButtons(_taskbarButtons);

                TaskbarProgressHelper.HookWindow(desktop.MainWindow, (id) =>
                {
                    if (TaskbarAction != null)
                    {
                        TaskbarAction.Invoke(id);
                        return;
                    }
                    switch (id)
                    {
                        case TaskbarButtonId.Previous: PlayPreviousCommand.Execute(null); break;
                        case TaskbarButtonId.PlayPause: TogglePlayCommand.Execute(null); break;
                        case TaskbarButtonId.Next: PlayNextCommand.Execute(null); break;
                    }
                });
            }
        }

        private void UpdateTaskbarButtons()
        {
            if (_taskbarButtons == null || AudioPlayer == null) return;

            // Update Play/Pause button icon and tooltip based on state
            _taskbarButtons[1].HIcon = AudioPlayer.IsPlaying ? _pauseIcon : _playIcon;
            _taskbarButtons[1].Tooltip = AudioPlayer.IsPlaying ? "Pause" : "Play";

            TaskbarProgressHelper.UpdateThumbnailButtons(_taskbarButtons);
        }

        public bool IsTagIconDimmed => HighlightedItem == null || MetadataService?.IsMetadataLoaded == true;
        #endregion

        #region [AutoResolve] properties
        // [AutoResolve] properties
        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        [AutoResolve]
        private MainWindowViewModel? _mainWindowViewModel;

        [AutoResolve]
        [ObservableProperty]
        private EqualizerService? _equalizerService;

        [AutoResolve]
        [ObservableProperty]
        private MetadataService? _metadataService;

        [AutoResolve]
        private MediaUrlService? _mediaUrlService;
        #endregion

        #region Commands
        // Commands
        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private void AddUrl()
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;
            if (MetadataService != null && MetadataService.IsMetadataLoaded) 
                MetadataService.IsMetadataLoaded = false;

            AddUrlText = string.Empty;
            IsAddUrlPopupOpen = true;
        }

        [RelayCommand]
        private void SubmitAddUrl() => IsAddUrlPopupOpen = false;

        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private void AddPlaylist()
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;
            if (MetadataService != null && MetadataService.IsMetadataLoaded) 
                MetadataService.IsMetadataLoaded = false;

            AddPlaylistText = string.Empty;
            IsAddPlaylistPopupOpen = true;
        }

        [RelayCommand]
        private void SubmitAddPlaylist() => IsAddPlaylistPopupOpen = false;

        [RelayCommand(CanExecute = nameof(CanDeletePointedItem))]
        private void DeletePointedItem()
        {
            var itemToDelete = PointedIndex != -1 && CoverItems.Count > PointedIndex 
                ? CoverItems[PointedIndex] 
                : HighlightedItem;

            if (itemToDelete == null) return;
            if (itemToDelete == SelectedMediaItem)
            {
                AudioPlayer?.Stop();
                AudioPlayer?.ClearMedia();
                SelectedMediaItem = null;
            }
            CoverItems.Remove(itemToDelete);
            if (CoverItems.Count > 0)
            {
                var newIndex = Math.Max(0, PointedIndex - 1);
                HighlightedItem = CoverItems[newIndex];
                SelectedIndex = newIndex;
            }
            else
            {
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                SelectedIndex = -1;
            }
        }

        [RelayCommand(CanExecute = nameof(CanAddItems))]
        private async Task AddItems()
        {
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Add Audio Files",
                    AllowMultiple = true,
                    FileTypeFilter = new[] 
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Audio Files")
                        {
                            Patterns = _supportedTypes
                        }
                    }
                });

                if (files.Count > 0)
                {
                    var newMediaItems = new AvaloniaList<MediaItem>();
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        if (IsMediaDuplicate(localPath, out _)) continue;

                        var item = new MediaItem
                        {
                            FileName = localPath,
                            Title = Path.GetFileName(localPath),
                            CoverBitmap = DefaultFolderCover
                        };
                        newMediaItems.Add(item);
                        CoverItems.Add(item);

                        if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                        {
                            if (!LoadedAlbum.Children.Any(c => c.FileName == item.FileName))
                                LoadedAlbum.Children.Add(item);
                        }
                    }

                    if (newMediaItems.Count > 0)
                        _ = new MetadataScrapper(newMediaItems, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                }
            }
        }

        [RelayCommand]
        private void CreateAlbum()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var baseName = "New Album";
            var uniqueName = baseName;
            int counter = 1;
            while (AlbumList.Any(a => string.Equals(a.Title, uniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                uniqueName = $"{baseName} ({counter++})";
            }

            var newAlbum = new FolderMediaItem
            {
                Title = uniqueName,
                Children = new AvaloniaList<MediaItem>(),
                CoverBitmap = DefaultFolderCover
            };
            AlbumList.Add(newAlbum);
            SelectedAlbum = newAlbum;
            OpenSelectedFolder();
            RenameFolder(newAlbum);
        }

        [RelayCommand]
        private void SortAlbumsAscending() => SortAlbums(alphabeticalAscending: true);

        [RelayCommand]
        private void SortAlbumsDescending() => SortAlbums(alphabeticalAscending: false);

        [RelayCommand]
        private void RenameFolder(FolderMediaItem? folder)
        {
            if (folder == null) return;
            foreach (var album in AlbumList)
            {
                if (album.IsRenaming && album.IsNameInvalid)
                    album.Title = _originalFolderTitle;
                album.IsRenaming = false;
            }

            _originalFolderTitle = folder.Title;
            folder.IsNameInvalid = false;
            folder.NameInvalidMessage = null;
            folder.IsRenaming = true;
        }

        [RelayCommand]
        private void EndRename(FolderMediaItem? folder)
        {
            if (folder != null)
            {
                ValidateFolderTitle(folder);
                if (folder.IsNameInvalid) return;
                folder.IsRenaming = false;
                _originalFolderTitle = null;
            }
        }

        [RelayCommand]
        private void CancelRename(FolderMediaItem? folder)
        {
            if (folder != null)
            {
                folder.Title = _originalFolderTitle;
                folder.IsNameInvalid = false;
                folder.NameInvalidMessage = null;
                folder.IsRenaming = false;
                _originalFolderTitle = null;
            }
        }

        [RelayCommand]
        private void DeleteFolder(FolderMediaItem? folder)
        {
            var target = folder ?? PointedFolder;
            if (target != null)
            {
                // If a song from this album is currently playing, stop the player
                if (AudioPlayer?.CurrentMediaItem != null && target.Children.Contains(AudioPlayer.CurrentMediaItem))
                {
                    AudioPlayer.Stop();
                }

                AlbumList.Remove(target);
                if (target == PointedFolder)
                {
                    PointedFolder = null;
                }

                // If the deleted album was the one loaded in the view, clear the view
                if (target == LoadedAlbum)
                {
                    LoadedAlbum = null;
                    IsNoAlbumLoadedVisible = true;
                    ApplyFilter();
                }
            }
        }

        [RelayCommand]
        private void OpenMetadata(object? parameter)
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;

            MediaItem? target;
            if (parameter is MediaItem mi) target = mi;
            else if (parameter is int index && index >= 0 && index < CoverItems.Count) target = CoverItems[index];
            else target = SelectedMediaItem ?? HighlightedItem ?? AudioPlayer?.CurrentMediaItem;

            if (target == null) return;

            if (MetadataService != null && MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;

            MetadataService?.LoadMetadataAsync(target);
        }

        [RelayCommand]
        private async Task ReloadMetadata(object? parameter)
        {
            MediaItem? target;
            if (parameter is MediaItem mi) target = mi;
            else if (parameter is int index && index >= 0 && index < CoverItems.Count) target = CoverItems[index];
            else target = SelectedMediaItem ?? HighlightedItem ?? AudioPlayer?.CurrentMediaItem;

            if (target == null || AudioPlayer == null) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();

            // Use the scrapper to force a reload, which will bypass the cache and update the item
            var scrapper = new MetadataScrapper(new AvaloniaList<MediaItem>(), AudioPlayer, DefaultFolderCover, agentInfo, 512);
            await scrapper.EnqueueLoadForPublic(target);
        }

        [RelayCommand]
        private void ToggleEqualizer()
        {
            if (MetadataService != null && MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;
            IsEqualizerVisible = !IsEqualizerVisible;
        }

        [RelayCommand]
        private void SetPosition(double position)
        {
            AudioPlayer?.SetPosition(position);
        }

        [RelayCommand]
        private void ToggleAlbumlist() => IsAlbumlistOpen = !IsAlbumlistOpen;

        [RelayCommand]
        private void Stop() => AudioPlayer?.Stop();

        [RelayCommand]
        private async Task PlayNext()
        {
            if (!GetCurrentIndex(out int currentIndex)) return;
            currentIndex++;
            if (currentIndex > PlaybackQueue.Count - 1)
            {
                if (AudioPlayer?.RepeatMode == RepeatMode.All)
                    currentIndex = 0;
                else
                    return;
            }
            await PlayIndexSelection(currentIndex);
        }

        [RelayCommand]
        private async Task PlayPrevious()
        {
            if (!GetCurrentIndex(out int currentIndex)) return;
            currentIndex--;
            if (currentIndex < 0) return;
            await PlayIndexSelection(currentIndex);
        }

        [RelayCommand]
        private void OpenSelectedFolder()
        {
            _scanMissingStreamDurationsOnLoadedAlbum = true;
            LoadedAlbum = SelectedAlbum;
            IsNoAlbumLoadedVisible = false;
            if (AudioPlayer != null)
                AudioPlayer.RepeatMode = RepeatMode.Off;
            ApplyFilter();
        }

        [RelayCommand]
        private void ClearSearch() => SearchText = string.Empty;

        [RelayCommand]
        private void ClearSearchAlbum() => SearchAlbumText = string.Empty;

        [RelayCommand]
        private async Task OpenSelectedItem(int index)
        {
            if (CoverItems.Count > index && CoverItems[index] is { } selectedItem)
            {
                PlaybackQueue = CoverItems;
                SelectedMediaItem = selectedItem;
                await PlayMediaItemAsync(selectedItem);
            }
        }

        [RelayCommand]
        private void Drop(FolderMediaItem item)
        {
        }

        [RelayCommand]
        private async Task TogglePlay()
        {
            if (AudioPlayer == null || AudioPlayer.IsLoadingMedia) return;

            if (AudioPlayer.IsPlaying)
            {
                AudioPlayer.Pause();
            }
            else
            {
                // nothing loaded? just make sure player is stopped and bail out.
                if (SelectedMediaItem == null)
                {
                    AudioPlayer.Stop();
                    return;
                }

                // If we have an item but no duration yet (i.e. never played), load it.
                if (AudioPlayer.Duration <= 0)
                {
                    if (PlaybackQueue.Count == 0) PlaybackQueue = CoverItems;
                    await PlayMediaItemAsync(SelectedMediaItem);
                }
                else
                {
                    AudioPlayer.Play();
                }
            }
        }

        [RelayCommand]
        private void ToggleRepeat()
        {
            if (AudioPlayer == null) return;
            AudioPlayer.RepeatMode = AudioPlayer.RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.One,
                RepeatMode.One => RepeatMode.All,
                RepeatMode.All => RepeatMode.Shuffle,
                RepeatMode.Shuffle => RepeatMode.Off,
                _ => RepeatMode.Off
            };
        }

        [RelayCommand]
        private async Task OpenFolder()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select folder",
                    AllowMultiple = false,
                });
                if (folders.Count > 0)
                {
                    var path = folders[0].Path.LocalPath;
                    var existing = AlbumList.FirstOrDefault(a => a.FileName == path);
                    if (existing != null)
                    {
                        if (Directory.Exists(path))
                        {
                            var mediaItems = LoadMediaItemsWithTrackOrder(path).ToList();

                            var addedItems = new AvaloniaList<MediaItem>();
                            foreach (var item in mediaItems)
                            {
                                if (!existing.Children.Any(c => c.FileName == item.FileName))
                                {
                                    existing.Children.Add(item);
                                    addedItems.Add(item);
                                }
                            }

                            if (addedItems.Count > 0)
                            {
                                QueueAlbumCoverLoad(existing);
                            }
                        }
                        SelectedAlbum = existing;
                        OpenSelectedFolder();
                        return;
                    }

                    var folderItem = new FolderMediaItem
                    {
                        FileName = path,
                        Title = GetUniqueAlbumName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                        CoverBitmap = DefaultFolderCover
                    };
                    if (Directory.Exists(path))
                    {
                        var mediaItems = LoadMediaItemsWithTrackOrder(path);
                        folderItem.Children.AddRange(mediaItems);
                    }
                    if (folderItem.Children.Count > 0)
                    {
                        AlbumList.Add(folderItem);
                        SelectedAlbum = folderItem;
                        OpenSelectedFolder();
                    }
                }
            }
        }

        [RelayCommand]
        private async Task ScanFolders()
        {
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Folder to Scan",
                    AllowMultiple = false,
                });

                if (folders.Count > 0)
                {
                    var rootPath = folders[0].Path.LocalPath;
                    if (Directory.Exists(rootPath))
                    {
                        await Task.Run(() => 
                        {
                            var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories).ToList();
                            directories.Insert(0, rootPath); // Include root folder

                            foreach (var dir in directories)
                            {
                                var mediaFiles = _supportedTypes
                                    .SelectMany(pattern => Directory.EnumerateFiles(dir, pattern))
                                    .Where(file => 
                                    {
                                        var name = Path.GetFileName(file);
                                        return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                                    })
                                    .ToList();

                                if (mediaFiles.Count > 0)
                                {
                                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                                    {
                                        // Check if already in list
                                        if (AlbumList.Any(a => a.FileName == dir)) return;

                                        var folderItem = new FolderMediaItem
                                        {
                                            FileName = dir,
                                            Title = GetUniqueAlbumName(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                                            CoverBitmap = DefaultFolderCover
                                        };

                                        var mediaItems = mediaFiles.Select(file => new MediaItem
                                        {
                                            FileName = file,
                                            Title = Path.GetFileName(file),
                                            CoverBitmap = DefaultFolderCover
                                        }).ToList();

                                        folderItem.Children.AddRange(mediaItems);
                                        AlbumList.Add(folderItem);

                                        QueueAlbumCoverLoad(folderItem, maxItemsToLoad: FolderPreviewCoverCount);
                                    });
                                }
                            }
                        });
                    }
                }
            }
        }
        #endregion

        #region Constructor/Prepare
        // Constructor/Prepare
        public MusicViewModel()
        {
            // Ensure the initial AlbumList is registered for changes (CollectionChanged and PropertyChanged on items)
            OnAlbumListChanged(null, AlbumList);

            // Initialize selected/highlighted media items to avoid null reference bindings in the view
            SelectedMediaItem = new MediaItem
            {
                Title = "No File Loaded",
                Artist = string.Empty,
                Album = string.Empty
            };

            HighlightedItem = new MediaItem
            {
                Title = string.Empty,
                Artist = string.Empty,
                Album = string.Empty
            };
        }

        public override void Prepare()
        {
            // Manual initialization of the AudioPlayer to control its lifecycle and avoid early DLL locking
            InitializeAudioPlayer();

            // Offload heavy initialization including equalizer and settings loading to a background thread
            // to ensure the UI remains responsive when the music view is first opened.
            _ = Task.Run(async () =>
            {
                // Initialize equalizer and load settings off-thread
                if (EqualizerService != null && AudioPlayer != null) await EqualizerService.InitializeAsync(AudioPlayer);
                await LoadSettingsAsync();

                // Marshal UI state updates and filters back to the dispatcher
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
                    MetadataService?.PropertyChanged += MetadataService_PropertyChanged;

                    ReduceCoverResidency(LoadedAlbum);
                    StartMetadataScrappersForLoadedFolders();
                    ApplyAlbumFilter();
                    ApplyFilter();
                    IsNoAlbumLoadedVisible = LoadedAlbum == null;
                    IsPrepared = true;
                });
            });
        }

        private void InitializeAudioPlayer()
        {
            // Dispose any existing instance to ensure native handles are released
            AudioPlayer?.Dispose();

            // Create a fresh instance manually
            // We resolve the managers from DI to pass them into the player
            var ffmpegManager = DiLocator.ResolveViewModel<FFmpegManager>();
            var mpvManager = DiLocator.ResolveViewModel<MpvLibraryManager>();

            //LibMPVSharp.LibraryName.LibraryDirectory = "/home/aruan/Dokumente/Test/";
            AudioPlayer = new AudioPlayer(ffmpegManager, mpvManager)
            {
                AutoSkipTrailingSilence = true
            };
            // Re-subscribe to events
            AudioPlayer.PropertyChanged += AudioPlayer_PropertyChanged;
            
            bool isTransitioning = false;
            AudioPlayer.EndReached += async (_, _) => 
            {
                if (isTransitioning) return;
                isTransitioning = true;
                
                try
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => 
                    {
                        await PlayNext();
                    });
                }
                finally
                {
                    // Brief delay to prevent double-skipping while song is loading
                    _ = Task.Run(async () => 
                    {
                        await Task.Delay(1000);
                        isTransitioning = false;
                    });
                }
            };

            _ = InitializeLinuxMprisAsync();
        }

        private async Task InitializeLinuxMprisAsync()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return;

            if (_mprisService == null)
            {
                _mprisService = new MprisService(
                    getState: BuildMprisState,
                    playAsync: () => InvokeOnUiAsync(async () =>
                    {
                        if (AudioPlayer != null && !AudioPlayer.IsPlaying)
                            await TogglePlay();
                    }),
                    pauseAsync: () => InvokeOnUiAsync(async () =>
                    {
                        if (AudioPlayer != null && AudioPlayer.IsPlaying)
                            await TogglePlay();
                    }),
                    playPauseAsync: () => InvokeOnUiAsync(TogglePlay),
                    stopAsync: () => InvokeOnUiAsync(() => Stop()),
                    nextAsync: () => InvokeOnUiAsync(PlayNext),
                    previousAsync: () => InvokeOnUiAsync(PlayPrevious),
                    seekRelativeAsync: offsetUs => InvokeOnUiAsync(() =>
                    {
                        if (AudioPlayer == null) return;
                        var newPos = AudioPlayer.Position + (offsetUs / 1_000_000d);
                        AudioPlayer.SetPosition(Math.Max(0, newPos));
                    }),
                    setPositionAsync: positionUs => InvokeOnUiAsync(() =>
                    {
                        if (AudioPlayer == null) return;
                        AudioPlayer.SetPosition(Math.Max(0, positionUs / 1_000_000d));
                    }),
                    setVolumeAsync: volume => InvokeOnUiAsync(() =>
                    {
                        if (AudioPlayer == null) return;
                        AudioPlayer.Volume = Math.Clamp(volume, 0, 1) * 100d;
                    }),
                    setShuffleAsync: shuffle => InvokeOnUiAsync(() =>
                    {
                        if (AudioPlayer == null) return;
                        AudioPlayer.RepeatMode = shuffle ? RepeatMode.Shuffle : RepeatMode.Off;
                    }),
                    setLoopStatusAsync: loopStatus => InvokeOnUiAsync(() =>
                    {
                        if (AudioPlayer == null) return;
                        AudioPlayer.RepeatMode = loopStatus switch
                        {
                            "Track" => RepeatMode.One,
                            "Playlist" => RepeatMode.All,
                            _ => RepeatMode.Off
                        };
                    }),
                    raiseRequested: () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                            desktop.MainWindow != null)
                        {
                            desktop.MainWindow.Activate();
                        }
                    }),
                    quitRequested: () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.Shutdown();
                        }
                    }));
            }

            try
            {
                await _mprisService.StartAsync();
            }
            catch
            {
                // Ignore MPRIS startup errors so normal playback remains unaffected.
            }
        }

        private static Task InvokeOnUiAsync(Action action)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private static Task InvokeOnUiAsync(Func<Task> action)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                return action();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        private MprisState BuildMprisState()
        {
            var player = AudioPlayer;
            var item = SelectedMediaItem;

            var duration = item?.Duration > 0 ? item.Duration : player?.Duration ?? 0;
            var title = !string.IsNullOrWhiteSpace(item?.Title)
                ? item!.Title!
                : Path.GetFileNameWithoutExtension(item?.FileName ?? string.Empty);

            return new MprisState(
                IsPlaying: player?.IsPlaying == true,
                IsStopped: player == null || (!player.IsPlaying && player.Position <= 0.001 && duration <= 0.001),
                CanPlay: item != null,
                CanPause: player?.IsPlaying == true,
                CanSeek: duration > 0,
                CanGoNext: CanGoNextForMpris(),
                CanGoPrevious: CanGoPreviousForMpris(),
                Shuffle: player?.RepeatMode == RepeatMode.Shuffle,
                LoopStatus: player?.RepeatMode switch
                {
                    RepeatMode.One => "Track",
                    RepeatMode.All => "Playlist",
                    RepeatMode.Shuffle => "Playlist",
                    _ => "None"
                },
                Volume: Math.Clamp((player?.Volume ?? 70d) / 100d, 0d, 1d),
                PositionUs: (long)Math.Max(0, (player?.Position ?? 0) * 1_000_000d),
                LengthUs: (long)Math.Max(0, duration * 1_000_000d),
                TrackIdObjectPath: BuildMprisTrackId(item?.FileName),
                Title: title,
                Artist: item?.Artist ?? string.Empty,
                Album: item?.Album ?? string.Empty,
                ArtUrl: BuildMprisArtUrl(item));
        }

        private bool CanGoNextForMpris()
        {
            if (PlaybackQueue.Count == 0 || SelectedMediaItem == null)
                return false;

            var idx = PlaybackQueue.IndexOf(SelectedMediaItem);
            if (idx < 0)
                return false;

            return idx < PlaybackQueue.Count - 1 || AudioPlayer?.RepeatMode == RepeatMode.All;
        }

        private bool CanGoPreviousForMpris()
        {
            if (PlaybackQueue.Count == 0 || SelectedMediaItem == null)
                return false;

            var idx = PlaybackQueue.IndexOf(SelectedMediaItem);
            return idx > 0;
        }

        private static string BuildMprisTrackId(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "/org/mpris/MediaPlayer2/track/none";

            var bytes = Encoding.UTF8.GetBytes(key);
            var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            return $"/org/mpris/MediaPlayer2/track/{hash}";
        }

        private static string BuildMprisArtUrl(MediaItem? item)
        {
            var localPath = item?.LocalCoverPath;
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                var path = localPath;
                if (Path.IsPathRooted(path) && System.IO.File.Exists(path))
                {
                    return new Uri(path).AbsoluteUri;
                }
            }

            if (!string.IsNullOrWhiteSpace(item?.FileName) &&
                Uri.TryCreate(item.FileName, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.AbsoluteUri;
            }

            return string.Empty;
        }

        public void ShutdownPlatformIntegrations()
        {
            _mprisService?.Dispose();
            _mprisService = null;
        }
        #endregion

        #region Partial methods
        // Partial methods
        partial void OnSelectedMediaItemChanged(MediaItem? value)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _mprisService?.NotifyStateChanged(nameof(SelectedMediaItem));
            }
        }

        partial void OnAlbumListChanged(AvaloniaList<FolderMediaItem>? oldValue, AvaloniaList<FolderMediaItem> newValue)
        {
            if (oldValue != null)
            {
                oldValue.CollectionChanged -= AlbumList_CollectionChanged;
                foreach (var item in oldValue)
                    UnsubscribeFolder(item);
            }
            // Subscribe to changes in the new list
            newValue.CollectionChanged += AlbumList_CollectionChanged;
            foreach (var item in newValue)
                SubscribeFolder(item);
            ApplyAlbumFilter();
        }

        partial void OnLoadedAlbumChanged(FolderMediaItem? value)
        {
            ApplyFilter();
            IsNoAlbumLoadedVisible = value == null;
            ReduceCoverResidency(value);

            if (value != null && IsPrepared)
            {
                QueueAlbumCoverLoad(value);
                if (_scanMissingStreamDurationsOnLoadedAlbum)
                    QueueOpenedAlbumStreamDurationScan(value);
            }

            _scanMissingStreamDurationsOnLoadedAlbum = false;
        }

        partial void OnSearchTextChanged(string? value) => ApplyFilter();

        partial void OnSearchAlbumTextChanged(string? value) => ApplyAlbumFilter();

        partial void OnSelectedAlbumChanged(FolderMediaItem? value)
        {
            SyncSelectedAlbumIndexFromAlbum(value);
        }

        partial void OnFilteredAlbumListChanged(AvaloniaList<FolderMediaItem>? oldValue, AvaloniaList<FolderMediaItem> newValue)
        {
            SyncSelectedAlbumIndexFromAlbum(SelectedAlbum);
        }

        partial void OnIsAddUrlPopupOpenChanged(bool value)
        {
            if (!value)
            {
                if (!string.IsNullOrWhiteSpace(AddUrlText))
                {
                    var url = AddUrlText!.Trim();
                    if (IsMediaDuplicate(url, out var existing))
                    {
                        if (existing != null && CoverItems.Contains(existing))
                        {
                            SelectedIndex = CoverItems.IndexOf(existing);
                            HighlightedItem = existing;
                        }
                        AddUrlText = string.Empty;
                        return;
                    }
                    if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
                    var item = new MediaItem
                    {
                        FileName = url,
                        Title = YouTubeThumbnail.ExtractVideoId(url) ?? url,
                        CoverBitmap = DefaultFolderCover
                    };
                    CoverItems.Add(item);
                    if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                    {
                        if (!LoadedAlbum.Children.Any(c => c.FileName == item.FileName))
                            LoadedAlbum.Children.Add(item);
                    }
                    var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                    var scanList = new AvaloniaList<MediaItem> { item };
                    var scrapper = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                    _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(MetadataStaggerDelayMs);
                        await scrapper.EnqueueLoadForPublic(item, force: false);
                    });
                    if (CoverItems.Count == 1)
                    {
                        SelectedIndex = 0;
                        HighlightedItem = item;
                        IsNoAlbumLoadedVisible = false;
                        SearchText = string.Empty;
                    }
                }
                AddUrlText = string.Empty;
            }
        }
        partial void OnIsAddPlaylistPopupOpenChanged(bool value)
        {
            if (!value)
            {
                if (!string.IsNullOrWhiteSpace(AddPlaylistText))
                {
                    var playlistUrl = AddPlaylistText!.Trim();
                    AddPlaylistText = string.Empty;
                    IsAddingPlaylist = true;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var urls = await GetPlaylistVideoUrls(playlistUrl);
                            if (urls == null || urls.Count == 0) return;

                            if (DefaultFolderCover == null)
                            {
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    DefaultFolderCover = GenerateDefaultFolderCover();
                                });
                            }

                            bool firstItem = true;
                            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                            var addedItems = new List<MediaItem>();

                            foreach (var url in urls)
                            {
                                if (string.IsNullOrWhiteSpace(url)) continue;

                                // Skip YouTube Shorts
                                if (url.Contains("/shorts/") || url.Contains("shorts/")) continue;

                                bool isDuplicate = false;
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    isDuplicate = IsMediaDuplicate(url, out _);
                                });

                                if (isDuplicate) continue;

                                var item = new MediaItem
                                {
                                    FileName = url,
                                    Title = YouTubeThumbnail.ExtractVideoId(url) ?? url,
                                    CoverBitmap = DefaultFolderCover
                                };

                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    CoverItems.Add(item);

                                    if (LoadedAlbum?.Children != null && !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                                    {
                                        if (!LoadedAlbum.Children.Any(c => c.FileName == item.FileName))
                                            LoadedAlbum.Children.Add(item);
                                    }

                                    if (firstItem && CoverItems.Count == 1)
                                    {
                                        SelectedIndex = 0;
                                        HighlightedItem = item;
                                        IsNoAlbumLoadedVisible = false;
                                        SearchText = string.Empty;
                                    }
                                    firstItem = false;
                                });

                                addedItems.Add(item);
                                _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
                                _ = Task.Run(() => TryPopulateStreamMetadataAsync(item));
                            }

                            if (addedItems.Count > 0)
                            {
                                var scanList = new AvaloniaList<MediaItem>(addedItems);
                                var scrapper = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                                for (int i = 0; i < addedItems.Count; i++)
                                {
                                    var queuedItem = addedItems[i];
                                    var delayMs = i * MetadataStaggerDelayMs;
                                    _ = Task.Run(async () =>
                                    {
                                        if (delayMs > 0) await Task.Delay(delayMs);
                                        await scrapper.EnqueueLoadForPublic(queuedItem, force: false);
                                    });
                                }
                            }
                        }
                        finally
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                IsAddingPlaylist = false;
                            });
                        }
                    });
                }
                else
                {
                    AddPlaylistText = string.Empty;
                }
            }
        }
        partial void OnSelectedIndexChanged(int value)
        {
            // Use the incoming value (new SelectedIndex) to avoid referencing the property which may have changed
            if (value >= 0 && value < CoverItems.Count && CoverItems[value] is { } highlighted)
            {
                HighlightedItem = highlighted;
            }
        }

        partial void OnSelectedAlbumIndexChanged(int value)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextAlbum =
                value >= 0 && value < FilteredAlbumList.Count
                    ? FilteredAlbumList[value]
                    : null;

            if (ReferenceEquals(SelectedAlbum, nextAlbum))
                return;

            try
            {
                _isSyncingAlbumSelection = true;
                SelectedAlbum = nextAlbum;
            }
            finally
            {
                _isSyncingAlbumSelection = false;
            }
        }

        private void AlbumList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var folder in _subscribedFolders.ToArray())
                    UnsubscribeFolder(folder);

                foreach (var folder in AlbumList)
                    SubscribeFolder(folder);
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (var folder in e.OldItems.OfType<FolderMediaItem>())
                        UnsubscribeFolder(folder);
                }

                if (e.NewItems != null)
                {
                    foreach (var folder in e.NewItems.OfType<FolderMediaItem>())
                        SubscribeFolder(folder);
                }
            }

            ApplyAlbumFilter();
        }

        private void SubscribeFolder(FolderMediaItem folder)
        {
            if (_subscribedFolders.Add(folder))
                folder.PropertyChanged += Folder_PropertyChanged;

            AttachFolderChildren(folder, folder.Children);
        }

        private void UnsubscribeFolder(FolderMediaItem folder)
        {
            if (_subscribedFolders.Remove(folder))
                folder.PropertyChanged -= Folder_PropertyChanged;

            if (_folderChildrenCollections.Remove(folder, out var children))
            {
                children.CollectionChanged -= FolderChildren_CollectionChanged;
                foreach (var child in children)
                    UnsubscribeAlbumChild(child);
            }
        }

        private void AttachFolderChildren(FolderMediaItem folder, AvaloniaList<MediaItem> children)
        {
            if (_folderChildrenCollections.TryGetValue(folder, out var existingChildren))
            {
                if (ReferenceEquals(existingChildren, children))
                    return;

                existingChildren.CollectionChanged -= FolderChildren_CollectionChanged;
                foreach (var child in existingChildren)
                    UnsubscribeAlbumChild(child);
            }

            _folderChildrenCollections[folder] = children;
            children.CollectionChanged += FolderChildren_CollectionChanged;
            foreach (var child in children)
                SubscribeAlbumChild(child);
        }

        private void SubscribeAlbumChild(MediaItem child)
        {
            if (_subscribedAlbumChildren.Add(child))
                child.PropertyChanged += AlbumChild_PropertyChanged;
        }

        private void UnsubscribeAlbumChild(MediaItem child)
        {
            if (_subscribedAlbumChildren.Remove(child))
                child.PropertyChanged -= AlbumChild_PropertyChanged;
        }

        private static bool IsSearchRelevantProperty(string? propertyName) =>
            string.IsNullOrEmpty(propertyName) ||
            propertyName == nameof(MediaItem.Title) ||
            propertyName == nameof(MediaItem.Artist) ||
            propertyName == nameof(MediaItem.Album);

        private void RefreshAlbumFilterIfNeeded()
        {
            if (!string.IsNullOrWhiteSpace(SearchAlbumText) || !ReferenceEquals(FilteredAlbumList, AlbumList))
                ApplyAlbumFilter();
        }

        private void RefreshTrackFilterIfNeeded()
        {
            if (LoadedAlbum == null)
                return;

            if (!string.IsNullOrWhiteSpace(SearchText) || !ReferenceEquals(CoverItems, LoadedAlbum.Children))
                ApplyFilter();
        }

        private void FolderChildren_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (sender is not AvaloniaList<MediaItem> children)
                return;

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var folder in _subscribedFolders.ToArray())
                    AttachFolderChildren(folder, folder.Children);
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (var child in e.OldItems.OfType<MediaItem>())
                        UnsubscribeAlbumChild(child);
                }

                if (e.NewItems != null)
                {
                    foreach (var child in e.NewItems.OfType<MediaItem>())
                        SubscribeAlbumChild(child);
                }
            }

            RefreshAlbumFilterIfNeeded();
            if (ReferenceEquals(children, LoadedAlbum?.Children))
                RefreshTrackFilterIfNeeded();
        }

        private void AlbumChild_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MediaItem item || !IsSearchRelevantProperty(e.PropertyName))
                return;

            RefreshAlbumFilterIfNeeded();
            if (LoadedAlbum?.Children.Contains(item) == true)
                RefreshTrackFilterIfNeeded();
        }

        private void ApplyAlbumFilter()
        {
            var query = SearchAlbumText?.Trim();
            var previousSelection = SelectedAlbum;

            if (string.IsNullOrWhiteSpace(query))
            {
                FilteredAlbumList = AlbumList;
            }
            else
            {
                var filtered = AlbumList.Where(a =>
                    (a.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    // Children is never null (FolderMediaItem initializes it), so skip null check
                    a.Children.Any(c =>
                         (c.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    ).ToList();
                FilteredAlbumList = new AvaloniaList<FolderMediaItem>(filtered);
            }

            if (previousSelection != null && FilteredAlbumList.Contains(previousSelection))
                SelectedAlbum = previousSelection;
            else if (FilteredAlbumList.Count == 0)
                SelectedAlbum = null;

            SyncSelectedAlbumIndexFromAlbum(SelectedAlbum);
        }

        private void SyncSelectedAlbumIndexFromAlbum(FolderMediaItem? album)
        {
            if (_isSyncingAlbumSelection)
                return;

            var nextIndex = album == null ? -1 : FilteredAlbumList.IndexOf(album);
            if (SelectedAlbumIndex == nextIndex)
                return;

            try
            {
                _isSyncingAlbumSelection = true;
                SelectedAlbumIndex = nextIndex;
            }
            finally
            {
                _isSyncingAlbumSelection = false;
            }
        }

        private void SortAlbums(bool alphabeticalAscending)
        {
            if (AlbumList.Count < 2)
                return;

            var comparer = StringComparer.OrdinalIgnoreCase;
            var orderedAlbums = alphabeticalAscending
                ? AlbumList
                    .OrderBy(GetAlbumSortKey, comparer)
                    .ThenBy(album => album.FileName ?? string.Empty, comparer)
                    .ToList()
                : AlbumList
                    .OrderByDescending(GetAlbumSortKey, comparer)
                    .ThenByDescending(album => album.FileName ?? string.Empty, comparer)
                    .ToList();

            if (AlbumList.SequenceEqual(orderedAlbums))
                return;

            AlbumList = new AvaloniaList<FolderMediaItem>(orderedAlbums);
        }

        private static string GetAlbumSortKey(FolderMediaItem album)
        {
            if (!string.IsNullOrWhiteSpace(album.Title))
                return album.Title.Trim();

            if (!string.IsNullOrWhiteSpace(album.FileName))
                return Path.GetFileName(album.FileName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.Empty;
        }

        private void Folder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not FolderMediaItem folder)
                return;

            if (e.PropertyName == nameof(MediaItem.Title))
            {
                if (folder.IsRenaming)
                    ValidateFolderTitle(folder);

                RefreshAlbumFilterIfNeeded();
            }
            else if (e.PropertyName == nameof(FolderMediaItem.Children))
            {
                AttachFolderChildren(folder, folder.Children);
                RefreshAlbumFilterIfNeeded();
                if (ReferenceEquals(folder, LoadedAlbum))
                    RefreshTrackFilterIfNeeded();
            }
        }

        private void ValidateFolderTitle(FolderMediaItem folder)
        {
            if (string.IsNullOrWhiteSpace(folder.Title))
            {
                folder.IsNameInvalid = true;
                folder.NameInvalidMessage = "Title cannot be empty.";
                return;
            }
            var duplicate = AlbumList.Any(a => a != folder && string.Equals(a.Title, folder.Title, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                folder.IsNameInvalid = true;
                folder.NameInvalidMessage = $"Album name '{folder.Title}' already exists.";
            }
            else
            {
                folder.IsNameInvalid = false;
                folder.NameInvalidMessage = null;
            }
        }

        private bool IsMediaDuplicate(string path, out MediaItem? existing)
        {
            existing = null;
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool isYt = path.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || 
                        path.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
            var id = isYt ? YouTubeThumbnail.ExtractVideoId(path) : null;

            bool Matches(MediaItem m)
            {
                if (string.IsNullOrEmpty(m.FileName)) return false;
                if (m.FileName == path) return true;
                if (id != null)
                {
                    bool itemIsYt = m.FileName.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || 
                                   m.FileName.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
                    if (itemIsYt)
                        return YouTubeThumbnail.ExtractVideoId(m.FileName) == id;
                }
                return false;
            }

            // If we have a LoadedAlbum (specific album selected), only check that album
            if (LoadedAlbum != null)
            {
                existing = LoadedAlbum.Children.FirstOrDefault(Matches);
                return existing != null;
            }

            // Otherwise check the general/global list
            existing = CoverItems.FirstOrDefault(Matches);
            return existing != null;
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
                OnPropertyChanged(nameof(IsTagIconDimmed));
        }

        

        private void StartMetadataScrappersForLoadedFolders(bool forceUpdate = false)
        {
            if (AudioPlayer == null || AlbumList.Count == 0 || IsAddingPlaylist) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            IsAddingPlaylist = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    var albums = AlbumList.ToList();
                    var loadedAlbum = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => LoadedAlbum);
                    foreach (var folder in albums)
                    {
                        if (folder == null || folder.Children.Count == 0) continue;

                        int maxItemsToLoad = ReferenceEquals(folder, loadedAlbum) ? int.MaxValue : FolderPreviewCoverCount;
                        await LoadAlbumCoversAsync(folder, agentInfo, forceUpdate, maxItemsToLoad);
                    }
                }
                finally
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsAddingPlaylist = false;
                    });
                }
            });
        }

        private void QueueAlbumCoverLoad(FolderMediaItem folder, bool forceUpdate = false, int maxItemsToLoad = int.MaxValue)
        {
            if (AudioPlayer == null || folder.Children.Count == 0) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            _ = Task.Run(async () => await LoadAlbumCoversAsync(folder, agentInfo, forceUpdate, maxItemsToLoad));
        }

        private void ReduceCoverResidency(FolderMediaItem? fullyLoadedAlbum)
        {
            DefaultFolderCover ??= GenerateDefaultFolderCover();
            var defaultCover = DefaultFolderCover;
            if (defaultCover == null) return;

            var protectedSelected = SelectedMediaItem;
            var protectedHighlighted = HighlightedItem;
            var protectedPlaying = AudioPlayer?.CurrentMediaItem;

            foreach (var folder in AlbumList)
            {
                bool keepAllCovers = ReferenceEquals(folder, fullyLoadedAlbum);
                int previewStart = Math.Max(0, folder.Children.Count - FolderPreviewCoverCount);

                for (int i = 0; i < folder.Children.Count; i++)
                {
                    var child = folder.Children[i];
                    if (keepAllCovers
                        || i >= previewStart
                        || ReferenceEquals(child, protectedSelected)
                        || ReferenceEquals(child, protectedHighlighted)
                        || ReferenceEquals(child, protectedPlaying))
                    {
                        continue;
                    }

                    if (child.CoverBitmap != null && child.CoverBitmap != defaultCover)
                        child.CoverBitmap = defaultCover;

                    child.IsLoadingCover = false;
                }

                if (!keepAllCovers)
                    folder.IsLoadingCover = false;
            }
        }

        private bool NeedsCoverLoad(MediaItem item)
        {
            if (item.CoverBitmap == null) return true;
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            return item.CoverBitmap == DefaultFolderCover || string.IsNullOrWhiteSpace(item.Title);
        }

        private List<MediaItem> GetAlbumCoverLoadBatch(IReadOnlyList<MediaItem> items, int maxItemsToLoad)
        {
            var candidates = items.Where(NeedsCoverLoad).ToList();
            if (maxItemsToLoad >= candidates.Count) return candidates;

            return candidates
                .Skip(Math.Max(0, candidates.Count - maxItemsToLoad))
                .ToList();
        }

        private async Task LoadAlbumCoversAsync(FolderMediaItem folder, string agentInfo, bool forceUpdate, int maxItemsToLoad = int.MaxValue)
        {
            if (AudioPlayer == null) return;

            var albumLoadState = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var snapshot = folder.Children.ToList();
                var itemsToLoad = GetAlbumCoverLoadBatch(snapshot, maxItemsToLoad);

                foreach (var child in itemsToLoad)
                {
                    if (NeedsCoverLoad(child))
                    {
                        if (child.CoverBitmap == null)
                        {
                            DefaultFolderCover ??= GenerateDefaultFolderCover();
                            child.CoverBitmap = DefaultFolderCover;
                        }
                        child.IsLoadingCover = true;
                    }
                }

                folder.IsLoadingCover = itemsToLoad.Count > 0;
                return (Snapshot: snapshot, ItemsToLoad: itemsToLoad);
            });

            var albumItems = albumLoadState.Snapshot;
            var itemsToLoad = albumLoadState.ItemsToLoad;

            if (itemsToLoad.Count == 0)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    folder.IsLoadingCover = false;
                });
                return;
            }

            // Match Add Playlist/Add URL behavior: kick off fast direct YouTube thumbnail fetch
            // first, then let MetadataScrapper backfill/normalize metadata in the background.
            var fastThumbCandidates = itemsToLoad
                .Where(item => !string.IsNullOrWhiteSpace(item.FileName)
                               && !string.IsNullOrWhiteSpace(YouTubeThumbnail.ExtractVideoId(item.FileName)))
                .ToList();

            foreach (var item in fastThumbCandidates)
            {
                _ = Task.Run(() => TryLoadYouTubeThumbnailFastAsync(item));
            }

            var orderedItems = new AvaloniaList<MediaItem>(itemsToLoad.AsEnumerable().Reverse());
            _ = new MetadataScrapper(
                orderedItems,
                AudioPlayer!,
                DefaultFolderCover,
                agentInfo,
                maxThumbnailWidth: 512,
                maxCacheEntries: MetadataScrapperCacheEntries,
                forceUpdate: forceUpdate);

            await WaitForAlbumCoversAsync(itemsToLoad);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                folder.IsLoadingCover = false;
            });
        }

        private static async Task WaitForAlbumCoversAsync(IReadOnlyList<MediaItem> items)
        {
            if (items.Count == 0) return;

            // Allow the scrapper to flip IsLoadingCover before we start polling.
            await Task.Delay(50);

            while (items.Any(i => i.IsLoadingCover))
            {
                await Task.Delay(150);
            }
        }

        private void QueueOpenedAlbumStreamDurationScan(FolderMediaItem folder)
        {
            _ = Task.Run(async () =>
            {
                var streamItems = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    folder.Children
                        .Where(item => !string.IsNullOrWhiteSpace(item.FileName)
                                       && (item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                           || item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase))
                                       && item.Duration <= 0)
                        .ToList());

                await PopulateMissingStreamMetadataAsync(streamItems);
            });
        }

        private async Task PopulateMissingStreamMetadataAsync(IReadOnlyList<MediaItem> items)
        {
            foreach (var item in items)
            {
                if (item.Duration > 0)
                    continue;

                await TryPopulateStreamMetadataAsync(item);
                await Task.Delay(MetadataStaggerDelayMs);
            }
        }

        private static Bitmap GenerateDefaultFolderCover() => PlaceholderGenerator.GenerateMusicPlaceholder();

        private async Task TryPopulateStreamMetadataAsync(MediaItem item)
        {
            try
            {
                if (item.FileName == null)
                    return;

                var info = await YtDlpMetadata.GetMetaDataAsync(item.FileName);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrWhiteSpace(info.Title) && (string.IsNullOrWhiteSpace(item.Title) || item.Title == (YouTubeThumbnail.ExtractVideoId(item.FileName) ?? item.FileName)))
                        item.Title = info.Title;

                    if (!string.IsNullOrWhiteSpace(info.Artist) && string.IsNullOrWhiteSpace(item.Artist))
                        item.Artist = info.Artist;

                    if (!string.IsNullOrWhiteSpace(info.Album) && string.IsNullOrWhiteSpace(item.Album))
                        item.Album = info.Album;

                    if (info.DurationSeconds is > 0 && item.Duration <= 0)
                        item.Duration = info.DurationSeconds.Value;
                });
            }
            catch
            {
                // Leave the item as-is when yt-dlp metadata is unavailable.
            }
        }

        private async Task TryLoadYouTubeThumbnailFastAsync(MediaItem item)
        {
            try
            {
                if (item.FileName == null) return;

                // Let metadata cache win when we already have a persisted sidecar cover.
                var cacheId = BinaryMetadataHelper.GetCacheId(item.FileName);
                var cachePath = ApplicationPaths.GetCacheFile(cacheId + ".meta");
                if (System.IO.File.Exists(cachePath)) return;

                // Do not replace a cover that was already restored from local metadata cache.
                var shouldFetch = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var hasCover = item.CoverBitmap != null;
                    var hasCustomCover = hasCover && (DefaultFolderCover == null || item.CoverBitmap != DefaultFolderCover);
                    return !hasCustomCover;
                });
                if (!shouldFetch) return;

                var videoId = YouTubeThumbnail.ExtractVideoId(item.FileName);
                if (string.IsNullOrWhiteSpace(videoId)) return;

                var urls = new[]
                {
                    $"https://img.youtube.com/vi/{videoId}/maxresdefault.jpg",
                    $"https://img.youtube.com/vi/{videoId}/hqdefault.jpg",
                    $"https://img.youtube.com/vi/{videoId}/mqdefault.jpg"
                };

                await FastThumbnailThrottle.WaitAsync();
                try
                {
                    foreach (var url in urls)
                    {
                        try
                        {
                            using var response = await FastThumbnailClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (!response.IsSuccessStatusCode) continue;

                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes.Length == 0) continue;

                            using var stream = new MemoryStream(bytes);
                            var bitmap = Bitmap.DecodeToWidth(stream, FastThumbnailDecodeWidth);
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var hasCurrentCover = item.CoverBitmap != null;
                                var hasCustomCover = hasCurrentCover && (DefaultFolderCover == null || item.CoverBitmap != DefaultFolderCover);
                                if (hasCustomCover)
                                {
                                    bitmap.Dispose();
                                    return;
                                }

                                item.CoverBitmap = bitmap;
                                item.IsLoadingCover = false;
                            });
                            return;
                        }
                        catch
                        {
                            // Try the next fallback thumbnail quality.
                        }
                    }
                }
                finally
                {
                    FastThumbnailThrottle.Release();
                }
            }
            catch
            {
                // Ignore thumbnail errors to keep adding URLs resilient.
            }
        }

        private IEnumerable<MediaItem> LoadMediaItemsWithTrackOrder(string path)
        {
            var files = _supportedTypes
                .SelectMany(pattern => Directory.EnumerateFiles(path, pattern))
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                });

            var ordered = files
                .Select(file => new { File = file, Track = TryReadTrackNumber(file) })
                .OrderBy(x => x.Track.HasValue ? 0 : 1)
                .ThenBy(x => x.Track ?? uint.MaxValue)
                .ThenBy(x => Path.GetFileName(x.File), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ordered)
            {
                yield return new MediaItem
                {
                    FileName = entry.File,
                    Title = Path.GetFileName(entry.File),
                    CoverBitmap = DefaultFolderCover,
                    Track = entry.Track ?? 0
                };
            }
        }

        private static uint? TryReadTrackNumber(string file)
        {
            try
            {
                using var tagFile = TagLib.File.Create(file);
                var track = tagFile.Tag.Track;
                return track > 0 ? track : null;
            }
            catch
            {
                return null;
            }
        }

        private bool CanDeletePointedItem() => PointedIndex != -1 && PointedIndex < CoverItems.Count;

        private bool CanAddItems() => LoadedAlbum != null;

        private async Task PlayIndexSelection(int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= PlaybackQueue.Count) return;
            SelectedMediaItem = PlaybackQueue[currentIndex];

            // Sync UI selection if the item exists in the currently viewed collection
            int viewIndex = CoverItems.IndexOf(SelectedMediaItem);
            if (viewIndex != -1) SelectedIndex = viewIndex;

            await PlayMediaItemAsync(SelectedMediaItem);
        }

        /// <summary>
        /// Plays the specified media item asynchronously using the audio player.
        /// </summary>
        /// <remarks>If the media item's file name is a URL, the media URL service is used to open and
        /// play the item. Otherwise, the file is played directly by the audio player.</remarks>
        /// <param name="item">The media item to play. Must not be null and must have a valid file name.</param>
        /// <returns>A task that represents the asynchronous operation of playing the media item.</returns>
        private async Task PlayMediaItemAsync(MediaItem item)
        {
            // 'item' is non-nullable; only check other nullable dependencies and the file name
            if (AudioPlayer == null || item.FileName == null) return;
            // Check if the item is a URL and resolve it if necessary
            if (item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase) || item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (_mediaUrlService == null) return;
                await _mediaUrlService.OpenMediaItemAsync(AudioPlayer, item);
            }
            else
                await AudioPlayer.PlayFile(item);
        }

        private bool GetCurrentIndex(out int currentIndex)
        {
            currentIndex = -1;
            if (SelectedMediaItem == null || PlaybackQueue.Count == 0) return false;
            currentIndex = PlaybackQueue.IndexOf(SelectedMediaItem);
            return currentIndex != -1;
        }

        private void ApplyFilter()
        {
            if (LoadedAlbum?.Children == null)
            {
                CoverItems = new AvaloniaList<MediaItem>();
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                return;
            }

            var query = SearchText?.Trim();
            MediaItem? preferredItem = null;
            if (SelectedIndex >= 0 && SelectedIndex < CoverItems.Count)
                preferredItem = CoverItems[SelectedIndex];
            preferredItem ??= HighlightedItem;
            preferredItem ??= SelectedMediaItem;

            if (string.IsNullOrWhiteSpace(query))
                CoverItems = LoadedAlbum.Children;
            else
            {
                var filtered = LoadedAlbum.Children
                    .Where(item =>
                        (item.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Album?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                CoverItems = new AvaloniaList<MediaItem>(filtered);
            }

            if (CoverItems.Count == 0)
            {
                SelectedIndex = -1;
                PointedIndex = -1;
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                return;
            }

            int nextIndex = preferredItem != null ? CoverItems.IndexOf(preferredItem) : -1;
            if (nextIndex < 0)
                nextIndex = 0;

            SelectedIndex = nextIndex;
            if (PointedIndex >= CoverItems.Count)
                PointedIndex = -1;

            HighlightedItem = CoverItems[nextIndex];
        }

        private string GetUniqueAlbumName(string baseName)
        {
            if (!AlbumList.Any(a => string.Equals(a.Title, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;
            int i = 1;
            while (AlbumList.Any(a => string.Equals(a.Title, $"{baseName} ({i})", StringComparison.OrdinalIgnoreCase)))
                i++;
            return $"{baseName} ({i})";
        }
        #endregion

        #region Public methods
        
        /// <summary>
        /// Asynchronously retrieves a list of video IDs from a online playlist URL by fetching the page's HTML content and extracting video IDs using a regular expression.
        /// </summary>
        /// <param name="playlistUrl">Playlist URL</param>
        /// <returns>Playlist videos</returns>
        public async Task<List<string>> GetPlaylistVideoIds(string playlistUrl)
        {
            using var client = new HttpClient();
            // Setting a User-Agent makes the request look like a browser to avoid blocks
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            
            var html = await client.GetStringAsync(playlistUrl);
            
            // This regex looks for video IDs inside the page source
            var matches = Regex.Matches(html, @"\""videoId\"":\""([^\""]+)\""");
            
            var videoIds = new HashSet<string>();
            foreach (Match match in matches)
            {
                videoIds.Add(match.Groups[1].Value);
            }
            
            return [.. videoIds];
        }

        /// <summary>
        /// Asynchronously retrieves a list of video URLs from a online playlist URL by fetching the page's HTML content, extracting video IDs using a regular expression, and constructing full online URLs for each video ID.
        /// </summary>
        /// <param name="playlistUrl">Playlist URL</param>
        /// <returns>Playlist Urls</returns>
        public async Task<List<string>> GetPlaylistVideoUrls(string playlistUrl)
        {
            using var client = new HttpClient();
            // Headers mimic a browser to prevent being flagged as a bot
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            try 
            {
                var html = await client.GetStringAsync(playlistUrl);
                
                var videoUrls = new List<string>();
                var seenIds = new HashSet<string>();

                // 1. Target specifically the "playlistId" associated with the videoId.
                // This is the most robust way to ensure we only get items that belong to THE playlist.
                // Recommendations and Reels usually do not have a "playlistId" in their watchEndpoint.
                var playlistMatches = Regex.Matches(html, @"\""videoId\""\s*:\s*\""([^\""]+)\""\s*,\s*\""playlistId\""\s*:\s*\""([^\""]+)\""");
                
                string? targetPlaylistId = null;
                if (playlistUrl.Contains("list="))
                {
                    var uri = new Uri(playlistUrl);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    targetPlaylistId = query["list"];
                }

                if (playlistMatches.Count > 0)
                {
                    foreach (Match m in playlistMatches)
                    {
                        string id = m.Groups[1].Value;
                        string pid = m.Groups[2].Value;

                        // Only add if it belongs to the playlist we are interested in (if we know it)
                        // OR if we don't know it, at least it MUST have a playlistId context.
                        if (targetPlaylistId == null || pid == targetPlaylistId)
                        {
                            if (seenIds.Add(id)) videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                        }
                    }
                }

                // 2. Fallback to renderer-based search if playlistId matching found nothing
                if (videoUrls.Count == 0)
                {
                    var rendererMatches = Regex.Matches(html, @"\""(playlistVideoRenderer|playlistPanelVideoRenderer|playlistVideoListRenderer)\""\s*:\s*\{.*?\""videoId\""\s*:\s*\""([^\""]+)\""");
                    foreach (Match m in rendererMatches)
                    {
                        string id = m.Groups[2].Value;
                        if (seenIds.Add(id)) videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                    }
                }

                // 3. Strict exclusion for recommendations if we are still searching
                if (videoUrls.Count == 0)
                {
                    var matches = Regex.Matches(html, @"\""videoId\"":\""([^\""]+)\""");
                    foreach (Match match in matches)
                    {
                        string id = match.Groups[1].Value;
                        if (seenIds.Add(id))
                        {
                            int index = match.Index;
                            string context = html.Substring(Math.Max(0, index - 200), Math.Min(html.Length - index, 400));
                            
                            // EXCLUDE if it's clearly a recommendation or a short
                            if (context.Contains("compactVideoRenderer") || 
                                context.Contains("reelWatchEndpoint") || 
                                context.Contains("shortsLockupViewModel"))
                                continue;

                            // INCLUDE if it has playlist keywords
                            if (context.Contains("playlistVideoRenderer") || 
                                context.Contains("playlistPanelVideoRenderer") ||
                                context.Contains("playlistId"))
                            {
                                videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                            }
                        }
                    }
                }
                
                return videoUrls;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching playlist: {ex.Message}");
                return new List<string>();
            }
        }

        #endregion

        #region Everything Else
        // Everything Else
        protected override string SettingsFilePath => ApplicationPaths.GetSettingsFile("Playlist.json");

        protected override void OnLoadSettings(JsonObject section)
        {
            AudioPlayer?.Volume = ReadDoubleSetting(section, "Volume", 70.0);
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen));
            AlbumList = ReadCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
            DefaultFolderCover ??= GenerateDefaultFolderCover();
            foreach (var folder in AlbumList)
            {
                // Children is initialized by FolderMediaItem; ensure runtime safety
                foreach (var child in folder.Children) child.SaveCoverBitmapAction ??= _ => { };
            }
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            if (AudioPlayer != null) WriteSetting(section, "Volume", AudioPlayer.Volume);
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
            WriteCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
        }
        #endregion
    }
}
