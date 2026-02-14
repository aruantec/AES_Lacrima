using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels
{
    /// <summary>
    /// Marker interface for the music view-model used by the view locator and
    /// dependency injection container.
    /// </summary>
    public interface IMusicViewModel;

    /// <summary>
    /// View-model responsible for music playback and album/folder management
    /// within the application's music view.
    /// </summary>
    [AutoRegister]
    internal partial class MusicViewModel : ViewModelBase, IMusicViewModel
    {
        #region Private fields
        // Private fields
        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        [ObservableProperty]
        private bool _isEqualizerVisible;

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
        private AvaloniaList<FolderMediaItem> _albumList = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddItemsCommand))]
        private AvaloniaList<MediaItem> _coverItems = [];

        [ObservableProperty]
        private FolderMediaItem? _selectedAlbum;

        [ObservableProperty]
        private AvaloniaList<MediaItem> _playbackQueue = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddItemsCommand))]
        [NotifyCanExecuteChangedFor(nameof(AddUrlCommand))]
        private FolderMediaItem? _loadedAlbum;

        [ObservableProperty]
        private bool _isNoAlbumLoadedVisible = true;

        [ObservableProperty]
        private string? _searchText;

        [ObservableProperty]
        private bool _isAddUrlPopupOpen;

        [ObservableProperty]
        private string? _addUrlText;

        private string? _originalFolderTitle;

        [ObservableProperty]
        private AudioPlayer? _audioPlayer;
        #endregion

        #region Static fields
        // Static fields
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
                    RepeatMode.All => "Turn Repeat Off",
                    _ => "Repeat",
                };
            }
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
                HighlightedItem = null;
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

            if (storageProvider != null && CoverItems != null)
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

                if (files != null && files.Count > 0)
                {
                    var newMediaItems = new AvaloniaList<MediaItem>();
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        if (IsMediaDuplicate(localPath, out _)) continue;

                        var item = new MediaItem
                        {
                            FileName = localPath,
                            Title = Path.GetFileName(localPath)
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
                Children = []
            };
            AlbumList.Add(newAlbum);
            SelectedAlbum = newAlbum;
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
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
                AlbumList.Remove(target);
                if (target == PointedFolder)
                {
                    PointedFolder = null;
                }
            }
        }

        [RelayCommand]
        private void OpenMetadata(object? parameter)
        {
            if (IsEqualizerVisible) IsEqualizerVisible = false;

            MediaItem? target = null;
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
        private async Task SetPosition(double position)
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
        private async Task OpenSelectedItem(int index)
        {
            if (CoverItems != null && CoverItems.Count > index
                && CoverItems[index] is MediaItem selectedItem)
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
                if (AudioPlayer.Duration <= 0 && SelectedMediaItem != null)
                {
                    if (PlaybackQueue == null || PlaybackQueue.Count == 0) PlaybackQueue = CoverItems;
                    await PlayMediaItemAsync(SelectedMediaItem);
                }
                else
                    AudioPlayer.Play();
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
                RepeatMode.All => RepeatMode.Off,
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
                                    Title = Path.GetFileName(file)
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
                        Title = GetUniqueAlbumName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
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
                                Title = Path.GetFileName(file)
                            });
                        folderItem.Children.AddRange(mediaItems);
                        _ = new MetadataScrapper(folderItem.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                    }
                    if (folderItem.Children.Count > 0)
                        AlbumList.Add(folderItem);
                }
            }
        }
        #endregion

        #region Constructor/Prepare
        // Constructor/Prepare
        public override void Prepare()
        {
            AudioPlayer = DiLocator.ResolveViewModel<AudioPlayer>();
            AudioPlayer?.PropertyChanged += AudioPlayer_PropertyChanged;
            AudioPlayer?.EndReached += async (_, _) => 
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(PlayNext);
            LoadSettings();
            EqualizerService?.Initialize(AudioPlayer!);
            StartMetadataScrappersForLoadedFolders();
            _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
            MetadataService?.PropertyChanged += MetadataService_PropertyChanged;
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
            if (newValue != null)
            {
                newValue.CollectionChanged += AlbumList_CollectionChanged;
                foreach (var item in newValue) item.PropertyChanged += Folder_PropertyChanged;
            }
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
                    var item = new MediaItem
                    {
                        FileName = url,
                        Title = Path.GetFileName(url)
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

        partial void OnSelectedIndexChanged(int value)
        {
            if (value >= 0 && CoverItems != null && CoverItems.Count > value
                && CoverItems[SelectedIndex] is MediaItem highlighted)
            {
                HighlightedItem = highlighted;
            }
        }

        partial void OnHighlightedItemChanged(MediaItem? value) => OnPropertyChanged(nameof(IsTagIconDimmed));

        partial void OnSearchTextChanged(string? value) => ApplyFilter();
        #endregion

        #region Private methods
        // Private methods
        private void AlbumList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (FolderMediaItem item in e.OldItems) item.PropertyChanged -= Folder_PropertyChanged;
            if (e.NewItems != null)
                foreach (FolderMediaItem item in e.NewItems) item.PropertyChanged += Folder_PropertyChanged;
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
                if (album.Children != null)
                {
                    existing = album.Children.FirstOrDefault(Matches);
                    if (existing != null) return true;
                }
            if (CoverItems != null)
            {
                existing = CoverItems.FirstOrDefault(Matches);
                if (existing != null) return true;
            }
            return false;
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
                OnPropertyChanged(nameof(IsTagIconDimmed));
        }

        private void AudioPlayer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AudioPlayer.RepeatMode) || e.PropertyName == nameof(AudioPlayer.Loop) || e.PropertyName == nameof(AudioPlayer.IsRepeatOne))
                OnPropertyChanged(nameof(NextRepeatToolTip));
        }

        private void StartMetadataScrappersForLoadedFolders()
        {
            if (AudioPlayer == null || AlbumList == null || AlbumList.Count == 0) return;
            var agentInfo = "AES_Lacrima/1.0 (contact: email@gmail.com)";
            foreach (var folder in AlbumList)
            {
                if (folder == null || folder.Children == null || folder.Children.Count == 0) continue;
                _ = new MetadataScrapper(folder.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
            }
        }

        private static Bitmap GenerateDefaultFolderCover()
        {
            var size = new PixelSize(400, 400);
            var renderTarget = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var context = renderTarget.CreateDrawingContext())
            {
                var brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
                    GradientStops =
                    [
                        new GradientStop(Color.Parse("#E0E0E0"), 0),
                        new GradientStop(Color.Parse("#A0A0A0"), 1)
                    ]
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, size.Width, size.Height));
                var noteBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
                var noteWidth = 200.0;
                var noteLeft = 110.0;
                var noteXOffset = (size.Width - noteWidth) / 2.0 - noteLeft;
                context.DrawEllipse(noteBrush, null, new Rect(110 + noteXOffset, 260, 80, 60));
                context.DrawEllipse(noteBrush, null, new Rect(230 + noteXOffset, 240, 80, 60));
                context.DrawRectangle(noteBrush, null, new Rect(175 + noteXOffset, 110, 15, 170));
                context.DrawRectangle(noteBrush, null, new Rect(295 + noteXOffset, 90, 15, 170));
                var stream = new StreamGeometry();
                using (var ctx = stream.Open())
                {
                    ctx.BeginFigure(new Point(175 + noteXOffset, 110), true);
                    ctx.LineTo(new Point(310 + noteXOffset, 90));
                    ctx.LineTo(new Point(310 + noteXOffset, 140));
                    ctx.LineTo(new Point(175 + noteXOffset, 160));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(noteBrush, null, stream);
            }
            return renderTarget;
        }

        private bool CanDeletePointedItem() => PointedIndex != -1 && PointedIndex < CoverItems.Count;

        private bool CanAddItems() => LoadedAlbum != null;

        private async Task PlayIndexSelection(int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= PlaybackQueue.Count) return;
            SelectedMediaItem = PlaybackQueue[currentIndex];

            // Sync UI selection if the item exists in the currently viewed collection
            if (CoverItems != null)
            {
                int viewIndex = CoverItems.IndexOf(SelectedMediaItem);
                if (viewIndex != -1) SelectedIndex = viewIndex;
            }

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
            if (AudioPlayer == null || item == null || item.FileName == null || _mediaUrlService == null) return;
            // Check if the item is a URL and resolve it if necessary
            if (item.FileName.Contains("http", StringComparison.OrdinalIgnoreCase) || item.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    await _mediaUrlService.OpenMediaItemAsync(AudioPlayer, item);
            // Play file
            else
                await AudioPlayer.PlayFile(item);
        }

        private bool GetCurrentIndex(out int currentIndex)
        {
            currentIndex = -1;
            if (SelectedMediaItem == null || PlaybackQueue == null || PlaybackQueue.Count == 0) return false;
            currentIndex = PlaybackQueue.IndexOf(SelectedMediaItem!);
            return true;
        }

        private void ApplyFilter()
        {
            if (LoadedAlbum?.Children == null)
            {
                CoverItems = [];
                SelectedIndex = 0;
                HighlightedItem = null;
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
                CoverItems = [.. filtered];
            }
            SelectedIndex = 0;
            if (CoverItems.Count > 0) HighlightedItem = CoverItems[0];
            else HighlightedItem = null;
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
        // Public methods
        #endregion

        #region Everything Else
        // Everything Else
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "Playlist.json");

        protected override void OnLoadSettings(JsonObject section)
        {
            AudioPlayer?.Volume = ReadDoubleSetting(section, "Volume", 70.0);
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen), false);
            AlbumList = ReadCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            foreach (var folder in AlbumList)
            {
                if (folder.Children == null) folder.Children = new AvaloniaList<MediaItem>();
                foreach (var child in folder.Children) child.SaveCoverBitmapAction ??= (_) => { };
            }
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, "Volume", AudioPlayer!.Volume);
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
            WriteCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
        }
        #endregion
    }
}