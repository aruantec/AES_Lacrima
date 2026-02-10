using AES_Controls.Player.Interfaces;
using AES_Controls.Player.Models;
using AES_Core.DI;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        /// <summary>
        /// Collection of folders (albums) shown in the music UI. Each
        /// <see cref="FolderMediaItem"/> may contain child <see cref="MediaItem"/> entries.
        /// </summary>
        [ObservableProperty]
        private AvaloniaList<FolderMediaItem> _albumList = [];

        /// <summary>
        /// Indicates whether the album/folder list panel is currently open.
        /// </summary>
        [ObservableProperty]
        private bool _isAlbumlistOpen;

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
        private IMediaInterface? _audioPlayer;

        /// <summary>
        /// Reference to the main window view-model (auto-resolved). Used to
        /// forward spectrum data and other global UI state.
        /// </summary>
        [AutoResolve]
        private MainWindowViewModel? _mainWindowViewModel;

        /// <summary>
        /// Prepare and initialize the view-model. Loads persisted settings
        /// and resolves required services (audio player).
        /// </summary>
        public override void Prepare()
        {
            //Load settings
            LoadSettings();
            //Get fresh player instances
            AudioPlayer = DiLocator.ResolveViewModel<IMediaInterface>();
            //Set main spectrum
            _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
            // PlayFile may be null if resolution fails; invoke only when available.
            //_ = AudioPlayer?.PlayFile(@"C:\Users\Admin\Music\WE DANCED THE NIGHT AWAY.mp3");
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
            // Show a native folder picker on desktop platforms.
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
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
                    // Add selected folder as a FolderMediaItem to the album list.
                    var folderItem = new FolderMediaItem
                    {
                        FileName = path,
                        Title = System.IO.Path.GetFileName(path)
                    };

                    if (System.IO.Directory.Exists(path))
                    {
                        foreach (var file in System.IO.Directory.EnumerateFiles(path))
                        {
                            folderItem.Children.Add(new MediaItem
                            {
                                FileName = file,
                                Title = System.IO.Path.GetFileName(file)
                            });
                        }
                    }

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
        }

        /// <summary>
        /// Save view-model specific settings into the provided JSON section.
        /// </summary>
        /// <param name="section">The JSON settings section for this view-model.</param>
        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(IsAlbumlistOpen), IsAlbumlistOpen);
        }
    }
}