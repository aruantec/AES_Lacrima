using AES_Controls.Helpers;
using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Code.Models;
using AES_Core.DI;
using AES_Core.IO;
using AES_Lacrima.Settings;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using log4net;
using TagLib;


namespace AES_Lacrima.ViewModels
{
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
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
            Log.Info($"MusicViewModel.Prepare starting. IsVideoMode={IsVideoMode}.");

            // Ensure inherited [AutoResolve] dependencies are resolved even for derived types
            // (e.g. VideoViewModel). The DI generator only walks directly-declared members on
            // the activated type, so inherited fields/properties can remain null.
            SettingsViewModel ??= DiLocator.ResolveViewModel<SettingsViewModel>();
            _mainWindowViewModel ??= DiLocator.ResolveViewModel<MainWindowViewModel>();
            EqualizerService ??= DiLocator.ResolveViewModel<EqualizerService>();
            MetadataService ??= DiLocator.ResolveViewModel<MetadataService>();
            _mediaUrlService ??= DiLocator.ResolveViewModel<MediaUrlService>();

            // Manual initialization of the AudioPlayer to control its lifecycle and avoid early DLL locking
            var audioPlayerInitStopwatch = Stopwatch.StartNew();
            Log.Info("MusicViewModel.InitializeAudioPlayer starting.");
            InitializeAudioPlayer();
            audioPlayerInitStopwatch.Stop();
            Log.Info($"MusicViewModel.InitializeAudioPlayer completed in {audioPlayerInitStopwatch.ElapsedMilliseconds} ms.");

            // Offload heavy initialization including equalizer and playlist snapshot loading to a
            // background thread to ensure the UI remains responsive when the shell is first composed.
            _ = Task.Run(async () =>
            {
                // Initialize equalizer and load the persisted playlist snapshot off-thread.
                if (EqualizerService != null && AudioPlayer != null) await EqualizerService.InitializeAsync(AudioPlayer);
                var loadSettingsStopwatch = Stopwatch.StartNew();
                var playlistSnapshot = await LoadPersistedPlaylistSnapshotAsync();
                loadSettingsStopwatch.Stop();

                // Marshal UI state updates and filters back to the dispatcher
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyPersistedPlaylistSnapshot(playlistSnapshot);
                    Log.Info(
                        $"MusicViewModel.LoadPersistedPlaylistSnapshotAsync completed in {loadSettingsStopwatch.ElapsedMilliseconds} ms. " +
                        $"Albums={AlbumList.Count}, Items={GetAlbumItemCount()}, LoadedAlbum='{LoadedAlbum?.Title ?? "<none>"}'.");

                    _mainWindowViewModel?.Spectrum = AudioPlayer?.Spectrum;
                    MetadataService?.PropertyChanged += MetadataService_PropertyChanged;

                    ReduceCoverResidency(LoadedAlbum);
                    Log.Info(
                        $"MusicViewModel startup metadata decision. Albums={AlbumList.Count}, " +
                        $"Items={GetAlbumItemCount()}, LoadedAlbum='{LoadedAlbum?.Title ?? "<none>"}'.");
                    if (LoadedAlbum != null)
                    {
                        Log.Info($"MusicViewModel queuing loaded album metadata during startup for '{LoadedAlbum.Title}'.");
                        QueueLoadedAlbumCoverLoad(LoadedAlbum);
                        QueueOpenedAlbumStreamDurationScan(LoadedAlbum);
                    }
                    else
                    {
                        Log.Info("MusicViewModel skipping eager library metadata warmup during startup because no album is loaded.");
                        EnsureCurrentMediaCoverIsLoaded();
                        EnsureMediaItemCoverIsLoaded(SelectedMediaItem);
                    }
                    ApplyAlbumFilter();
                    ApplyFilter();
                    IsPrepared = true;
                    QueueDeferredPersistedAlbumRestore();
                    Log.Info($"MusicViewModel.Prepare completed. IsVideoMode={IsVideoMode}, IsPrepared={IsPrepared}.");
                });
            });
        }

        public override void OnViewFullyVisible()
        {
            base.OnViewFullyVisible();
            _isMusicViewVisible = true;
            if (_mainWindowViewModel != null)
            {
                _mainWindowViewModel.IsShaderToyRenderingPaused = true;
            }

            StartDeferredLibraryMetadataWarmupIfNeeded();
        }

        public override void OnLeaveViewModel()
        {
            base.OnLeaveViewModel();
            _isMusicViewVisible = false;
            if (_mainWindowViewModel != null)
            {
                _mainWindowViewModel.IsShaderToyRenderingPaused = false;
            }
        }

        private void InitializeAudioPlayer()
        {
            // Dispose any existing instance to ensure native handles are released
            AudioPlayer?.Dispose();

            // Create a fresh instance manually
            // We resolve the managers from DI to pass them into the player
            var ffmpegManager = DiLocator.ResolveViewModel<FFmpegManager>();
            var mpvManager = DiLocator.ResolveViewModel<MpvLibraryManager>();

            //AES_Mpv.Native.MpvNativeLibrary.SearchDirectory = "/home/aruan/Dokumente/Test/";
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
                        if (GetCurrentIndex(out var currentIndex))
                        {
                            var hasNext = currentIndex < PlaybackQueue.Count - 1;
                            var willRepeatAll = AudioPlayer?.RepeatMode == RepeatMode.All;

                            if (!hasNext && !willRepeatAll)
                            {
                                if (IsVideoMode)
                                    IsVideoViewportDismissed = true;
                                return;
                            }
                        }

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
            catch (Exception ex)
            {
                Log.Warn("MPRIS startup failed; Fn/media keys may not work when the app is unfocused on Linux.", ex);
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
    }
}
