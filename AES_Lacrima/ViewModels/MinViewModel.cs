using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Core.Interfaces;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using log4net;

namespace AES_Lacrima.ViewModels
{
    public interface IMinViewModel { }

    [AutoRegister]
    internal partial class MinViewModel : ViewModelBase, IMinViewModel
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(MinViewModel));
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        private string _agentInfo = "AES_Lacrima/1.0 (contact: aruantec@gmail.com)";
        private Bitmap _defaultCover = PlaceholderGenerator.GenerateMusicPlaceholder();

        private readonly string[] _supportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];

        // Override the settings file path to store playlist data separately. You can customize this path as needed.
        protected override string SettingsFilePath => Path.Combine(AppContext.BaseDirectory, "Settings", "CustomPlaylist.json");

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

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        public override void Prepare()
        {
            // Load saved playlist from settings first.
            LoadSettings();

            // If no saved items, fall back to the aggregated album list from MusicViewModel.
            if (MediaItems == null || MediaItems.Count == 0)
            {
                MediaItems = [.. MusicViewModel?.AlbumList?.SelectMany(s => s.Children) ?? []];
            }

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
                try { Next(); }
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
            }
        }

        [RelayCommand]
        private void PlayPause()
        {
            if (MusicViewModel == null || MusicViewModel.AudioPlayer == null) return;

            if (MusicViewModel.AudioPlayer.IsPlaying)
                MusicViewModel.AudioPlayer.Pause();
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

        // NOTE: Volume handling is done in OnIsMutedChanging to ensure the change
        // is applied before the UI updates. No need to duplicate here.

        [RelayCommand]
        private void DeleteSelectedItems()
        {
            if (SelectedItems == null || MediaItems == null) return;

            // Remove selected items safely
            var itemsToRemove = SelectedItems.ToList();
            foreach (var item in itemsToRemove)
            {
                MediaItems.Remove(item);
            }
            // Persist changes
            SaveSettings();
        }

        [RelayCommand]
        private async Task AddFilesAsync()
        {
            if (MediaItems == null) return;

            var lifetime = Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
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
            // Persist the last played file path so we can restore selection on next run
            var last = LoadedMediaItem?.FileName ?? SelectedMediaItem?.FileName;
            if (!string.IsNullOrEmpty(last))
                WriteSetting(section, "LastPlayedFile", last);
        }

        protected override void OnLoadSettings(System.Text.Json.Nodes.JsonObject section)
        {
            // Read persisted media items
            MediaItems = ReadCollectionSetting<MediaItem>(section, "MediaItems", "MediaItem", []);
            // Restore last played file selection if available
            var last = ReadStringSetting(section, "LastPlayedFile", null);
            if (!string.IsNullOrEmpty(last) && MediaItems != null)
            {
                var found = MediaItems.FirstOrDefault(m => string.Equals(m.FileName, last, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    SelectedMediaItem = found;
                    LoadedMediaItem = found;
                }
            }
            // If we have loaded items, we can start scrapping metadata.
            if (MusicViewModel != null && MusicViewModel.AudioPlayer != null && MediaItems != null && MediaItems.Count > 0)
                _ = new MetadataScrapper(MediaItems, MusicViewModel.AudioPlayer, _defaultCover, _agentInfo, 512);
        }
    }
}
