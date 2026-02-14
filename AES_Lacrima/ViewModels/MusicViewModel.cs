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
        // Path to the JSON settings file for this view-model.
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "Playlist.json");

        // Supported audio file types for folder scanning and playback.
        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        /// <summary>
        /// Gets or sets a value indicating whether the equalizer is visible.
        /// </summary>
        [ObservableProperty]
        private bool _isEqualizerVisible;

        /// <summary>
        /// Indicates whether the album/folder list panel is currently open.
        /// </summary>
        [ObservableProperty]
        private bool _isAlbumlistOpen;

        /// <summary>
        /// Default cover bitmap used for folders and media items that do not have a specific cover image.
        /// </summary>
        [ObservableProperty]
        private Bitmap? _defaultFolderCover;

        /// <summary>
        /// Selected index for the album list. This property can be used to track the currently selected album
        /// </summary>
        [ObservableProperty]
        private int _selectedIndex;

        /// <summary>
        /// Index of the item currently being pointed at (e.g. via right-click for context menu).
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeletePointedItemCommand))]
        private int _pointedIndex = -1;

        /// <summary>
        /// The folder currently being pointed at (e.g. via mouse hover) in the album list.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFolderPointed))]
        private FolderMediaItem? _pointedFolder;

        /// <summary>
        /// Gets a value indicating whether a folder is currently being pointed at.
        /// </summary>
        public bool IsFolderPointed => PointedFolder != null;

        /// <summary>
        /// Gets or sets the media item associated with this instance.
        /// </summary>
        /// <remarks>This property is observable. Changes to its value will raise property change
        /// notifications, allowing data binding clients to react to updates. Ensure that the media item is properly
        /// initialized before accessing its members.</remarks>
        [ObservableProperty]
        private MediaItem? _selectedMediaItem;

        /// <summary>
        /// Gets or sets the currently highlighted media item.
        /// </summary>
        [ObservableProperty]
        private MediaItem? _highlightedItem;

        /// <summary>
        /// Collection of folders (albums) shown in the music UI. Each
        /// <see cref="FolderMediaItem"/> may contain child <see cref="MediaItem"/> entries.
        /// </summary>
        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _albumList = [];

        /// <summary>
        /// Gets or sets the collection of media items used as cover items.
        /// </summary>
        /// <remarks>The collection is observable, enabling automatic UI updates when items are added,
        /// removed, or modified.</remarks>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AddItemsCommand))]
        private AvaloniaList<MediaItem> _coverItems = [];

        /// <summary>
        /// Gets or sets the currently selected album in the media library.
        /// </summary>
        /// <remarks>This property is observable, meaning that changes to its value will notify any
        /// listeners. It is important to ensure that the selected album is not null before performing operations that
        /// depend on it.</remarks>
        [ObservableProperty]
        private FolderMediaItem? _selectedAlbum;

        /// <summary>
        /// The currently loaded album whose children are displayed in the cover items.
        /// </summary>
        private FolderMediaItem? _loadedAlbum;

        /// <summary>
        /// The text used to filter the current album's media items in the carousel.
        /// </summary>
        [ObservableProperty]
        private string? _searchText;

        /// <summary>
        /// Resolved settings view-model instance (injected via DI).
        /// </summary>
        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        /// <summary>
        /// The active audio player implementation used for playback control and
        /// analysis (spectrum, waveform, etc.). Resolved from the DI container.
        /// </summary>
        [ObservableProperty]
        private AudioPlayer? _audioPlayer;

        /// <summary>
        /// Reference to the main window view-model (auto-resolved). Used to
        /// forward spectrum data and other global UI state.
        /// </summary>
        [AutoResolve]
        private MainWindowViewModel? _mainWindowViewModel;

        /// <summary>
        /// Gets or sets the EqualizerService instance used for audio equalization.
        /// </summary>
        [AutoResolve]
        [ObservableProperty]
        private EqualizerService? _equalizerService;

        /// <summary>
        /// Gets or sets the metadata service used for retrieving metadata information.
        /// </summary>
        [AutoResolve]
        [ObservableProperty]
        private MetadataService? _metadataService;

        /// <summary>
        /// Tooltip text describing the repeat mode that will be activated when the
        /// repeat button is clicked (i.e. the "next" repeat state).
        /// </summary>
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

        /// <summary>
        /// Prepare and initialize the view-model. Loads persisted settings
        /// and resolves required services (audio player).
        /// </summary>
        public override void Prepare()
        {
            //Get fresh player instances
            AudioPlayer = DiLocator.ResolveViewModel<AudioPlayer>();
            // Listen for changes on the audio player so UI-bound helper properties
            // (like tooltip text for repeat) get refreshed when RepeatMode changes.
            AudioPlayer?.PropertyChanged += AudioPlayer_PropertyChanged;
            //Subscribe to the EndReached event to automatically play the next item.
            AudioPlayer?.EndReached += async (_, _) =>
            {
                // Play the next item when the current one finishes.
                PlayNext();
            };
            //Load settings
            LoadSettings();
            //Setup equalizer
            EqualizerService?.Initialize(AudioPlayer!);
            // Start metadata scrappers for any folders loaded from settings
            StartMetadataScrappersForLoadedFolders();
            //Set main spectrum
            _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
            // Listen for metadata service changes to update UI state
            MetadataService?.PropertyChanged += MetadataService_PropertyChanged;
        }

        private void MetadataService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataService.IsMetadataLoaded))
            {
                OnPropertyChanged(nameof(IsTagIconDimmed));
            }
        }

        private void AudioPlayer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // When the player's repeat mode changes, notify bindings for the tooltip
            // that shows which mode will be activated when the user clicks the button.
            if (e.PropertyName == nameof(AudioPlayer.RepeatMode) || e.PropertyName == nameof(AudioPlayer.Loop) || e.PropertyName == nameof(AudioPlayer.IsRepeatOne))
            {
                OnPropertyChanged(nameof(NextRepeatToolTip));
            }
        }

        /// <summary>
        /// True when the tag editor icon should be dimmed (DimGray). This is when
        /// there is no highlighted item OR the metadata editor overlay is open.
        /// </summary>
        public bool IsTagIconDimmed => HighlightedItem == null || MetadataService?.IsMetadataLoaded == true;

        /// <summary>
        /// Triggers on selected index change.
        /// </summary>
        /// <param name="value">Index</param>
        partial void OnSelectedIndexChanged(int value)
        {
            if (value >= 0 && CoverItems != null && CoverItems.Count > value
                && CoverItems[SelectedIndex] is MediaItem highlighted)
            {
                HighlightedItem = highlighted;
            }
        }

        partial void OnHighlightedItemChanged(MediaItem? value)
        {
            // Notify the dependent computed property so bindings update
            OnPropertyChanged(nameof(IsTagIconDimmed));
        }

        /// <summary>
        /// Start MetadataScrapper for each folder that was loaded from settings.
        /// Executed after the AudioPlayer has been resolved so the scrapper can use it.
        /// </summary>
        private void StartMetadataScrappersForLoadedFolders()
        {
            if (AudioPlayer == null) return;
            if (AlbumList == null || AlbumList.Count == 0) return;

            var agentInfo = "AES_Lacrima/1.0 (contact: email@gmail.com)";

            foreach (var folder in AlbumList)
            {
                if (folder == null) continue;
                if (folder.Children == null || folder.Children.Count == 0) continue;
                _ = new MetadataScrapper(folder.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
            }
        }

        /// <summary>
        /// Generates a default cover bitmap with a musical note icon.
        /// </summary>
        private static Bitmap GenerateDefaultFolderCover()
        {
            var size = new PixelSize(400, 400);
            var renderTarget = new RenderTargetBitmap(size, new Vector(96, 96));

            using (var context = renderTarget.CreateDrawingContext())
            {
                // Background Radial Gradient
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

                // Musical Note icon (Double eighth note)
                var noteBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
                var noteWidth = 200.0;
                var noteLeft = 110.0;
                var noteXOffset = (size.Width - noteWidth) / 2.0 - noteLeft;

                // Note heads (slightly tilted ellipses)
                context.DrawEllipse(noteBrush, null, new Rect(110 + noteXOffset, 260, 80, 60));
                context.DrawEllipse(noteBrush, null, new Rect(230 + noteXOffset, 240, 80, 60));

                // Stems
                context.DrawRectangle(noteBrush, null, new Rect(175 + noteXOffset, 110, 15, 170));
                context.DrawRectangle(noteBrush, null, new Rect(295 + noteXOffset, 90, 15, 170));

                // Beam (tilted rectangle using geometry)
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

        /// <summary>
        /// Determines whether the currently pointed item can be deleted.
        /// </summary>
        private bool CanDeletePointedItem() => PointedIndex != -1 && PointedIndex < CoverItems.Count;

        /// <summary>
        /// Deletes the currently pointed or highlighted item from the cover items list. Stops playback if the item
        /// being deleted is currently selected for playback.
        /// </summary>
        /// <remarks>If a valid pointed index is set, the item at that index is deleted; otherwise, the
        /// highlighted item is removed. If the item to be deleted is currently playing, playback is stopped and the
        /// selection is cleared before removal. This command can only be executed when deletion is permitted, as
        /// determined by the CanDeletePointedItem method.</remarks>
        [RelayCommand(CanExecute = nameof(CanDeletePointedItem))]
        private void DeletePointedItem()
        {
            var itemToDelete = PointedIndex != -1 && CoverItems.Count > PointedIndex 
                ? CoverItems[PointedIndex] 
                : HighlightedItem;

            if (itemToDelete == null) return;
            // If the item to delete is currently playing, stop playback before removing it.
            if (itemToDelete == SelectedMediaItem)
            {
                AudioPlayer?.Stop();
                AudioPlayer?.ClearMedia();
                SelectedMediaItem = null;
            }
            // Remove the item from the cover items list.
            CoverItems.Remove(itemToDelete);
            // Select previous item if possible, otherwise clear selection
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

        /// <summary>
        /// Allows adding new items to the cover items list.
        /// </summary>
        /// <returns></returns>
        private bool CanAddItems() => _loadedAlbum != null;

        /// <summary>
        /// Adds audio files to the collection of cover items by prompting the user to select one or more files from the
        /// file system.
        /// </summary>
        /// <remarks>This method opens a file picker dialog allowing the user to select multiple audio
        /// files. The selected files are added to the CoverItems collection and, if applicable, to the currently loaded
        /// album's children. Metadata for the newly added items is scanned asynchronously after they are added. This
        /// command can only execute if the CanAddItems condition is met.</remarks>
        /// <returns>A task that represents the asynchronous operation.</returns>
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
                        var item = new MediaItem
                        {
                            FileName = localPath,
                            Title = Path.GetFileName(localPath)
                        };
                        newMediaItems.Add(item);

                        CoverItems.Add(item);

                        // If we are currently filtered, add to the original album children too so they persist
                        if (_loadedAlbum?.Children != null && !ReferenceEquals(CoverItems, _loadedAlbum.Children))
                        {
                            _loadedAlbum.Children.Add(item);
                        }
                    }

                    // Scan metadata for the newly added items
                    _ = new MetadataScrapper(newMediaItems, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                }
            }
        }

        /// <summary>
        /// Creates a new empty album and adds it to the album list.
        /// </summary>
        [RelayCommand]
        private void CreateAlbum()
        {
            var newAlbum = new FolderMediaItem
            {
                Title = "New Album",
                Children = []
            };
            AlbumList.Add(newAlbum);
            SelectedAlbum = newAlbum;
            // Ensure the shared cover is initialized if it's the first time
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
        }

        /// <summary>
        /// Starts the renaming process for the specified album.
        /// </summary>
        [RelayCommand]
        private void RenameFolder(FolderMediaItem? folder)
        {
            if (folder == null) return;
            // Finish any other renaming
            foreach (var album in AlbumList)
                album.IsRenaming = false;

            folder.IsRenaming = true;
        }

        /// <summary>
        /// Ends the renaming process for the specified album.
        /// </summary>
        [RelayCommand]
        private void EndRename(FolderMediaItem? folder)
        {
            if (folder != null)
                folder.IsRenaming = false;
        }

        /// <summary>
        /// Deletes the specified folder or the currently pointed folder from the album list.
        /// </summary>
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

        /// <summary>
        /// Opens the metadata editor overlay for the currently highlighted item, loading the associated metadata if it
        /// is not already loaded.
        /// </summary>
        /// <remarks>If the equalizer is currently visible, it will be closed to ensure that only one
        /// overlay is displayed at a time. This method does not perform any action if the metadata is already
        /// loaded.</remarks>
        [RelayCommand]
        private void OpenMetadata()
        {
            // If the equalizer is open, close it to ensure only one overlay is visible at a time.
            if (IsEqualizerVisible) IsEqualizerVisible = false;
            // If there is no highlighted item or metadata is already loaded, do nothing.
            if (MetadataService != null && MetadataService.IsMetadataLoaded) return;
            // Load metadata for the currently highlighted item, which will trigger the metadata editor overlay to open.
            MetadataService?.LoadMetadataAsync(HighlightedItem!);
        }

        /// <summary>
        /// Toggles the visibility of the equalizer interface.
        /// </summary>
        [RelayCommand]
        private void ToggleEqualizer()
        {
            // If the metadata editor is open, close it to ensure only one overlay is visible at a time.
            if (MetadataService != null && MetadataService.IsMetadataLoaded)
                MetadataService.IsMetadataLoaded = false;
            // Toggle the equalizer visibility state.
            IsEqualizerVisible = !IsEqualizerVisible;
        }

        /// <summary>
        /// Set the current playback position (seconds or normalized position
        /// depending on the player implementation).
        /// </summary>
        [RelayCommand]
        private async Task SetPosition(double position)
        {
            AudioPlayer?.SetPosition(position);
        }

        /// <summary>
        /// Toggle the visibility of the album list panel.
        /// </summary>
        [RelayCommand]
        private void ToggleAlbumlist()
        {
            IsAlbumlistOpen = !IsAlbumlistOpen;
        }

        /// <summary>
        /// Stop playback.
        /// </summary>
        [RelayCommand]
        private void Stop()
        {
            AudioPlayer?.Stop();
        }

        /// <summary>
        /// Play the next media item in the current album/folder.
        /// </summary>
        [RelayCommand]
        private void PlayNext()
        {
            // Get the current index of the selected media item in the cover items list
            GetCurrentIndex(out int currentIndex);
            // Move to the next index
            currentIndex++;
            // If the current index is invalid or already at the end of the list
            if (currentIndex > CoverItems.Count - 1)
            {
                // If Repeat All is enabled, wrap around to the first item
                if (AudioPlayer?.RepeatMode == RepeatMode.All)
                {
                    currentIndex = 0;
                }
                else
                {
                    // Otherwise, just stop or stay at the last item
                    return;
                }
            }
            // Set the selected media item to the next item in the list and update the selected index
            PlayIndexSelection(currentIndex);
        }

        /// <summary>
        /// Plays the previous item in the current album/folder.
        /// </summary>
        [RelayCommand]
        private void PlayPrevious()
        {
            // Get the current index of the selected media item in the cover items list
            GetCurrentIndex(out int currentIndex);
            // Move to the next index
            currentIndex--;
            // If the current index is invalid or already at the end of the list, do nothing
            if (currentIndex < 0)
                return;
            // Set the selected media item to the next item in the list and update the selected index
            PlayIndexSelection(currentIndex);
        }

        /// <summary>
        /// Plays the media item at the specified index in the cover items list.
        /// </summary>
        /// <param name="currentIndex"></param>
        private void PlayIndexSelection(int currentIndex)
        {
            // Set the selected media item to the next item in the list and update the selected index
            SelectedMediaItem = CoverItems[currentIndex];
            // Set the selected index to the new current index to update the UI highlight
            SelectedIndex = currentIndex;
            // Move to the next index and play the corresponding media item
            AudioPlayer?.PlayFile(SelectedMediaItem);
        }

        /// <summary>
        /// Determines the index of the currently selected media item within the cover items list.
        /// </summary>
        /// <param name="currentIndex"></param>
        private bool GetCurrentIndex(out int currentIndex)
        {
            currentIndex = -1;
            // Ensure there is a selected media item and that the cover items list is valid
            if (SelectedMediaItem == null && CoverItems == null || CoverItems.Count == 0) return false;
            // Find the index of the currently selected media item in the cover items list
            currentIndex = CoverItems.IndexOf(SelectedMediaItem!);
            return true;
        }

        /// <summary>
        /// Open the selected album/folder and populate the cover items with its children.
        /// </summary>
        [RelayCommand]
        private void OpenSelectedFolder()
        {
            _loadedAlbum = SelectedAlbum;
            // Reset repeat mode when changing albums
            if (AudioPlayer != null)
                AudioPlayer.RepeatMode = RepeatMode.Off;
            ApplyFilter();
        }

        /// <summary>
        /// Clears the search text and resets the filter.
        /// </summary>
        [RelayCommand]
        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        /// <summary>
        /// Handles changes to the search text and triggers the filtering of results.
        /// </summary>
        partial void OnSearchTextChanged(string? value) => ApplyFilter();

        /// <summary>
        /// Filters the <see cref="CoverItems"/> based on the current <see cref="SearchText"/>.
        /// </summary>
        private void ApplyFilter()
        {
            if (_loadedAlbum?.Children == null)
            {
                CoverItems = [];
                SelectedIndex = 0;
                HighlightedItem = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchText))
            {
                CoverItems = _loadedAlbum.Children;
            }
            else
            {
                var filtered = _loadedAlbum.Children
                    .Where(item =>
                        (item.Title?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Artist?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (item.Album?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

                CoverItems = [.. filtered];
            }

            SelectedIndex = 0;
            // Force update highlighted item
            if (CoverItems.Count > 0)
                HighlightedItem = CoverItems[0];
            else
                HighlightedItem = null;
        }

        /// <summary>
        /// Opens the specified media index and plays its associated audio file if it is valid.
        /// </summary>
        [RelayCommand]
        private void OpenSelectedItem(int index)
        {
            if (CoverItems != null && CoverItems.Count > index
                && CoverItems[SelectedIndex] is MediaItem selectedItem)
            {
                SelectedMediaItem = selectedItem;
                AudioPlayer?.PlayFile(selectedItem);
            }
        }

        /// <summary>
        /// Dropped media item from the UI when the postion changes
        /// </summary>
        /// <param name="item"></param>
        [RelayCommand]
        private void Drop(FolderMediaItem item)
        {

        }

        /// <summary>
        /// Toggle play/pause state of the audio player.
        /// </summary>
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
                // If stopped (Duration is 0 typically means no file is loaded in mpv after a stop)
                if (AudioPlayer.Duration <= 0 && SelectedMediaItem != null)
                {
                    await AudioPlayer.PlayFile(SelectedMediaItem);
                }
                else
                {
                    AudioPlayer.Play();
                }
            }
        }

        /// <summary>
        /// Toggle repeat/looping mode for the audio player through 3 states: Off, One, All.
        /// </summary>
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

        /// <summary>
        /// Open a native folder picker and add the selected folder as a
        /// <see cref="FolderMediaItem"/>. Any files found directly inside the
        /// folder are added as child <see cref="MediaItem"/> entries.
        /// </summary>
        [RelayCommand]
        private async Task OpenFolder()
        {
            var agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
            if (DefaultFolderCover == null) DefaultFolderCover = GenerateDefaultFolderCover();
            // Show a native folder picker on desktop platforms.
            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var storageProvider = lifetime?.MainWindow?.StorageProvider;

            if (storageProvider != null)
            {
                var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select folder",
                    AllowMultiple = false,
                });
                // If a folder was selected, create a FolderMediaItem and scan for supported audio files.
                if (folders.Count > 0)
                {
                    var path = folders[0].Path.LocalPath;
                    // Add selected folder as a FolderMediaItem to the album list.
                    var folderItem = new FolderMediaItem
                    {
                        FileName = path,
                        Title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    };
                    // Scan the folder for supported audio files and add them as children.
                    if (Directory.Exists(path))
                    {
                        var mediaItems = _supportedTypes
                            .SelectMany(pattern => Directory.EnumerateFiles(path, pattern))
                            // Filter out macOS resource fork files (names that start with "._") and other dot-files
                            .Where(file => {
                                var name = Path.GetFileName(file);
                                return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                            })
                            .Select(file => new MediaItem
                            {
                                FileName = file,
                                Title = Path.GetFileName(file)
                            });
                        // Add found media items as children of the folder item.
                        folderItem.Children.AddRange(mediaItems);
                        _ = new MetadataScrapper(folderItem.Children, AudioPlayer!, DefaultFolderCover, agentInfo, 512);
                    }
                    // Only add the folder to the album list if it contains supported audio files.
                    if (folderItem.Children.Count > 0)
                        AlbumList.Add(folderItem);
                }
            }
        }

        /// <summary>
        /// Load persisted settings into the view-model.
        /// </summary>
        /// <param name="section">The JSON settings section for this view-model.</param>
        protected override void OnLoadSettings(JsonObject section)
        {
            // Load persisted volume setting if available, otherwise default to 70%.
            AudioPlayer?.Volume = ReadDoubleSetting(section, "Volume", 70.0);
            // Load persisted album list visibility state
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen), false);
            // Load persisted album list (folders and their children)
            AlbumList = ReadCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);

            // Ensure runtime-only properties (bitmaps, actions) are initialized
            if (DefaultFolderCover == null)
                DefaultFolderCover = GenerateDefaultFolderCover();

            foreach (var folder in AlbumList)
            {
                // Initialize children list elements
                if (folder.Children == null)
                    folder.Children = new AvaloniaList<MediaItem>();

                foreach (var child in folder.Children)
                {
                    // Provide a save action that persists cover images when used
                    child.SaveCoverBitmapAction ??= (mi) => { /* no-op in settings load */ };
                }
            }
        }

        /// <summary>
        /// Save view-model specific settings into the provided JSON section.
        /// </summary>
        /// <param name="section">The JSON settings section for this view-model.</param>
        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(AudioPlayer.Volume), AudioPlayer!.Volume);
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
            // Persist AlbumList (folders and children). We only store the serializable model data.
            WriteCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
        }
    }
}