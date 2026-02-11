using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Serialization;

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
        private bool _isAlbumlistOpen;

        // Path to the JSON settings file for this view-model.
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "Playlist.json");

        // Supported audio file types for folder scanning and playback.
        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

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
        /// Indicates whether the album/folder list panel is currently open.
        /// </summary>
        [XmlIgnore]
        [JsonIgnore]
        public bool IsAlbumlistOpen
        {
            get => _isAlbumlistOpen;
            set => SetProperty(ref _isAlbumlistOpen, value);
        }

        /// <summary>
        /// Prepare and initialize the view-model. Loads persisted settings
        /// and resolves required services (audio player).
        /// </summary>
        public override void Prepare()
        {
            //Load settings
            LoadSettings();
            //Get fresh player instances
            AudioPlayer = DiLocator.ResolveViewModel<AudioPlayer>();
            //Setup equalizer
            var equalizer = new Equalizer(AudioPlayer!);
            //Set equalizer bands from the settings if available
            equalizer.InitializeBands();
            // Start metadata scrappers for any folders loaded from settings
            StartMetadataScrappersForLoadedFolders();
            //Set main spectrum
            _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
        }

        /// <summary>
        /// Triggers on selected index change.
        /// </summary>
        /// <param name="value">Index</param>
        partial void OnSelectedIndexChanged(int value)
        {
            if (CoverItems != null && CoverItems.Count > value
                && CoverItems[SelectedIndex] is MediaItem highlighted)
            {
                HighlightedItem = highlighted;
            }
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
        /// Open the selected album/folder and populate the cover items with its children.
        /// </summary>
        [RelayCommand]
        private void OpenSelectedFolder()
        {
            //Set cover items to the children of the selected album, or an empty list if null
            CoverItems = SelectedAlbum?.Children ?? [];
            //Reset selected index to ensure the first item is highlighted when opening a new folder
            SelectedIndex = 0;
            //Trigger selection change to update the highlighted item
            OnSelectedIndexChanged(0);
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
        private void TogglePlay()
        {
            if (AudioPlayer == null) return;
            if (AudioPlayer.IsPlaying)
                AudioPlayer.Pause();
            else
                AudioPlayer.Play();
        }

        /// <summary>
        /// Toggle repeat/looping mode for the audio player.
        /// </summary>
        [RelayCommand]
        private void ToggleRepeat()
        {
            AudioPlayer?.Loop = !AudioPlayer.Loop;
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
            // Show a native folder picker on desktop platforms.
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
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
                        CoverBitmap = DefaultFolderCover ??= GenerateDefaultFolderCover(),
                        FileName = path,
                        Title = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
                    };
                    // Scan the folder for supported audio files and add them as children.
                    if (System.IO.Directory.Exists(path))
                    {
                        var mediaItems = _supportedTypes
                            .SelectMany(pattern => System.IO.Directory.EnumerateFiles(path, pattern))
                            // Filter out macOS resource fork files (names that start with "._") and other dot-files
                            .Where(file => {
                                var name = System.IO.Path.GetFileName(file);
                                return !(string.IsNullOrEmpty(name) || name.StartsWith("._") || name.StartsWith("."));
                            })
                            .Select(file => new MediaItem
                            {
                                CoverBitmap = DefaultFolderCover ??= GenerateDefaultFolderCover(),
                                FileName = file,
                                Title = System.IO.Path.GetFileName(file)
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
            IsAlbumlistOpen = ReadBoolSetting(section, nameof(IsAlbumlistOpen), false);
            // Load persisted album list (folders and their children)
            AlbumList = ReadCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);

            // Ensure runtime-only properties (bitmaps, actions) are initialized
            if (DefaultFolderCover == null)
                DefaultFolderCover = GenerateDefaultFolderCover();

            foreach (var folder in AlbumList)
            {
                if (folder.CoverBitmap == null)
                    folder.CoverBitmap = DefaultFolderCover;

                // Initialize children list elements
                if (folder.Children == null)
                    folder.Children = new AvaloniaList<MediaItem>();

                foreach (var child in folder.Children)
                {
                    if (child.CoverBitmap == null)
                        child.CoverBitmap = DefaultFolderCover;
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
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
            // Persist AlbumList (folders and children). We only store the serializable model data.
            WriteCollectionSetting(section, nameof(AlbumList), "FolderMediaItem", AlbumList);
        }
    }
}