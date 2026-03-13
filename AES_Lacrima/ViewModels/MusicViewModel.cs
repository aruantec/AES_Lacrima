using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
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
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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

        private TaskbarButton[]? _taskbarButtons;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;

        [ObservableProperty]
        private Action<TaskbarButtonId>? _taskbarAction;

        [ObservableProperty]
        private bool _isEqualizerVisible;

        // last window handle we added thumbnail buttons to; used to re‑initialize after a mode switch
        private IntPtr _taskbarHwnd = IntPtr.Zero;

        [ObservableProperty]
        private bool _isAlbumlistOpen;

        [ObservableProperty]
        private Bitmap? _defaultFolderCover;

        [ObservableProperty]
        private int _selectedIndex;

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

        private string? _originalFolderTitle;

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
        }

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
            else target = HighlightedItem;

            if (target == null) return;

            if (MetadataService != null && MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;

            MetadataService?.LoadMetadataAsync(target);
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
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
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
                                    CoverBitmap = DefaultFolderCover
                                }).ToList();

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
                                _ = new MetadataScrapper(addedItems, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
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
                                CoverBitmap = DefaultFolderCover
                            });
                        folderItem.Children.AddRange(mediaItems);
                        _ = new MetadataScrapper(folderItem.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
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
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
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
                                        
                                        _ = new MetadataScrapper(folderItem.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
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

            AudioPlayer = new AudioPlayer(ffmpegManager, mpvManager);
            AudioPlayer.AutoSkipTrailingSilence = true;
            // Re-subscribe to events
            AudioPlayer.PropertyChanged += AudioPlayer_PropertyChanged;
            AudioPlayer.EndReached += async (_, _) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(PlayNext);
        }
        #endregion

        #region Partial methods
        // Partial methods
        partial void OnAlbumListChanged(AvaloniaList<FolderMediaItem>? oldValue, AvaloniaList<FolderMediaItem> newValue)
        {
            if (oldValue != null)
            {
                oldValue.CollectionChanged -= AlbumList_CollectionChanged;
                foreach (var item in oldValue) item.PropertyChanged -= Folder_PropertyChanged;
            }
            // Subscribe to changes in the new list
            newValue.CollectionChanged += AlbumList_CollectionChanged;
            foreach (var item in newValue) item.PropertyChanged += Folder_PropertyChanged;
            ApplyAlbumFilter();
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
                        Title = Path.GetFileName(url),
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
                    _ = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
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
                    _ = Task.Run(async () =>
                    {
                        var urls = await GetPlaylistVideoUrls(playlistUrl);
                        if (urls == null || urls.Count == 0) return;

                        var newMediaItems = new List<MediaItem>();
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
                            
                            foreach (var url in urls)
                            {
                                if (IsMediaDuplicate(url, out _)) continue;

                                var item = new MediaItem
                                {
                                    FileName = url,
                                    Title = Path.GetFileName(url),
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
                            {
                                var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
                                var scanList = new AvaloniaList<MediaItem>(newMediaItems);
                                _ = new MetadataScrapper(scanList, AudioPlayer!, DefaultFolderCover, agentInfo, 512);

                                if (CoverItems.Count == newMediaItems.Count)
                                {
                                    SelectedIndex = 0;
                                    HighlightedItem = CoverItems[0];
                                    IsNoAlbumLoadedVisible = false;
                                    SearchText = string.Empty;
                                }
                            }
                        });
                    });
                }
                AddPlaylistText = string.Empty;
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

        private void AlbumList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // To avoid nullable/analysis issues with the event args, detach and re-attach handlers for all current items.
            foreach (var itm in AlbumList)
                itm.PropertyChanged -= Folder_PropertyChanged;
            foreach (var itm in AlbumList)
                itm.PropertyChanged += Folder_PropertyChanged;

            ApplyAlbumFilter();
        }

        private void ApplyAlbumFilter()
        {
            if (string.IsNullOrWhiteSpace(SearchAlbumText))
            {
                FilteredAlbumList = AlbumList;
            }
            else
            {
                var filtered = AlbumList.Where(a =>
                    (a.Title?.Contains(SearchAlbumText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    // Children is never null (FolderMediaItem initializes it), so skip null check
                    a.Children.Any(c =>
                         (c.Title?.Contains(SearchAlbumText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Artist?.Contains(SearchAlbumText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                         (c.Album?.Contains(SearchAlbumText, StringComparison.OrdinalIgnoreCase) ?? false))
                    ).ToList();
                FilteredAlbumList = new AvaloniaList<FolderMediaItem>(filtered);
            }
        }

        private void Folder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is FolderMediaItem folder && e.PropertyName == nameof(MediaItem.Title) && folder.IsRenaming)
                ValidateFolderTitle(folder);
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
                folder.NameInvalidMessage = $"Another folder named '{folder.Title}' already exists.";
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
            foreach (var album in AlbumList)
            {
                // Children is initialized and never null
                existing = album.Children.FirstOrDefault(Matches);
                if (existing != null) return true;
            }
            existing = CoverItems.FirstOrDefault(Matches);
            if (existing != null) return true;
            return false;
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
                OnPropertyChanged(nameof(IsTagIconDimmed));
        }

        

        private void StartMetadataScrappersForLoadedFolders()
        {
            if (AudioPlayer == null || AlbumList.Count == 0) return;
            var agentInfo = "AES_Lacrima/1.0 (contact: email@gmail.com)";
            foreach (var folder in AlbumList)
            {
                if (folder == null || folder.Children.Count == 0) continue;
                _ = new MetadataScrapper(folder.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
            }
        }

        private static Bitmap GenerateDefaultFolderCover() => PlaceholderGenerator.GenerateMusicPlaceholder();

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
            if (AudioPlayer == null || _mediaUrlService == null || item.FileName == null) return;
            // Check if the item is a URL and resolve it if necessary
            if (item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase) || item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                await _mediaUrlService.OpenMediaItemAsync(AudioPlayer, item);
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
                SelectedIndex = 0;
                HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
                return;
            }
            if (string.IsNullOrWhiteSpace(SearchText))
                CoverItems = LoadedAlbum.Children;
            else
            {
                var filtered = LoadedAlbum.Children
                    .Where(item =>
                        (item.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Artist?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                CoverItems = new AvaloniaList<MediaItem>(filtered);
            }
            SelectedIndex = 0;
            if (CoverItems.Count > 0) HighlightedItem = CoverItems[0];
            else HighlightedItem = new MediaItem { Title = string.Empty, Artist = string.Empty, Album = string.Empty };
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
                
                // Regex looks for "videoId":"[ID]" in the page source
                var matches = Regex.Matches(html, @"\""videoId\"":\""([^\""]+)\""");
                
                var videoUrls = new HashSet<string>();
                foreach (Match match in matches)
                {
                    string id = match.Groups[1].Value;
                    // Standard YouTube URL format
                    videoUrls.Add($"https://www.youtube.com/watch?v={id}");
                }
                
                return [.. videoUrls];
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
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "Playlist.json");

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





























































