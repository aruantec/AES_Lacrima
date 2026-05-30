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
    [AutoRegister]
    public partial class MusicViewModel : ViewModelBase, IMusicViewModel 
    {
        #region Private fields
        // Private fields
        private static readonly ILog Log = AES_Core.Logging.LogHelper.For<MusicViewModel>();
        protected static readonly string[] MusicSupportedTypes = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.mp4"];
        protected static readonly string[] VideoSupportedTypes = ["*.mp4", "*.m4v", "*.mkv", "*.avi", "*.mov", "*.webm", "*.wmv"];
        private static readonly HttpClient FastThumbnailClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly SemaphoreSlim FastThumbnailThrottle = new(OperatingSystem.IsMacOS() ? 2 : 4);
        private const int FastThumbnailDecodeWidth = 512;
        private const int FolderPreviewCoverCount = 4;
        private const int MetadataScrapperCacheEntries = 80;
        private const int MetadataStaggerDelayMs = 120;
        private const int PlaylistUiAddBatchSize = 12;
        private const double DefaultPersistedVolume = 70.0;

        private TaskbarButton[]? _taskbarButtons;
        private IntPtr _playIcon;
        private IntPtr _pauseIcon;

        [ObservableProperty]
        private Action<TaskbarButtonId>? _taskbarAction;

        [ObservableProperty]
        private bool _isEqualizerVisible;

        // last window handle we added thumbnail buttons to; used to re-initialize after a mode switch
        private IntPtr _taskbarHwnd = IntPtr.Zero;
        private MprisService? _mprisService;

        [ObservableProperty]
        private bool _isAlbumlistOpen;

        [ObservableProperty]
        private bool _isTrackLoadPending;

        [ObservableProperty]
        private Bitmap? _defaultFolderCover;

        [ObservableProperty]
        private double _selectedIndex = -1;

        [ObservableProperty]
        private int _selectedAlbumIndex = -1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeletePointedItemCommand))]
        [NotifyPropertyChangedFor(nameof(IsItemPointed))]
        [NotifyPropertyChangedFor(nameof(IsMetadataEditorVisible))]
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
        private readonly HashSet<FolderMediaItem> _activeAlbumCoverLoads = [];
        private readonly object _albumCoverLoadGate = new();
        private bool _isSyncingAlbumSelection;
        private bool _scanMissingStreamDurationsOnLoadedAlbum;
        private bool _isApplyingDeferredAlbumList;
        private bool _hasQueuedDeferredAlbumListRestore;
        private bool _isMusicViewVisible;
        private bool _hasDeferredLibraryMetadataWarmupStarted;
        private int _pendingPersistedAlbumRestoreIndex;
        private AvaloniaList<FolderMediaItem>? _pendingPersistedAlbumList;
        private CancellationTokenSource? _loadedAlbumCoverCts;
        private MediaItem? _pendingTrackLoadItem;

        private sealed record PersistedPlaylistSnapshot(
            double Volume,
            bool IsAlbumListOpen,
            AvaloniaList<FolderMediaItem> Albums);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVideoViewportVisible))]
        private AudioPlayer? _audioPlayer;
        private AudioPlayer? _subscribedAudioPlayer;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVideoViewportVisible))]
        private bool _isVideoViewportDismissed;

        public bool ResetPlaybackOnAlbumSwitch { get; set; }

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
        public bool HasAlbums => AlbumList.Count > 0;
        public bool ShowEmptyAlbumListHint => !HasAlbums;
        public bool CanSortAlbums => AlbumList.Count > 1;
        public bool HasLoadedAlbumItems => LoadedAlbum?.Children.Count > 0;
        public bool ShowEmptyLoadedAlbumHint => LoadedAlbum != null && !HasLoadedAlbumItems;
        public bool HasCurrentMediaLoaded => AudioPlayer?.CurrentMediaItem != null;
        public string EmptyAlbumListMessage => "Right-click to open a folder, create an album or scan folders";
        public string EmptyLoadedAlbumMessage =>
            LoadedAlbum != null
                ? "Right-click to add files, URL or playlist"
                : "No album loaded";

        public virtual bool IsVideoMode => false;

        public virtual bool IsMetadataEditorVisible => IsItemPointed;

        public bool IsVideoViewportVisible => IsVideoMode && !IsVideoViewportDismissed && AudioPlayer?.CurrentMediaItem != null;

        protected virtual bool AllowOnlineCoverLookup => true;

        protected virtual bool ShouldScanLocalMediaMetadata => true;

        protected virtual string FilePickerTitle => "Add Audio Files";

        protected virtual string FilePickerTypeName => "Audio Files";

        protected virtual IReadOnlyList<string> SupportedTypes => MusicSupportedTypes;

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
                    _subscribedAudioPlayer.RestartOnResumeEnabled = SettingsViewModel.RestartOnResumeEnabled;
                    _subscribedAudioPlayer.RestartOnResumeThresholdSeconds = SettingsViewModel.RestartOnResumeThresholdSeconds;
                    _subscribedAudioPlayer.PauseWhenVolumeIsZero = SettingsViewModel.PauseWhenVolumeIsZero;

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
            OnPropertyChanged(nameof(HasCurrentMediaLoaded));
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
            else if (e.PropertyName == nameof(AudioPlayer.CurrentMediaItem))
            {
                if (IsVideoMode && AudioPlayer?.CurrentMediaItem != null)
                    IsVideoViewportDismissed = false;

                UpdateTrackLoadPendingState();
                EnsureCurrentMediaCoverIsLoaded();
                OnPropertyChanged(nameof(IsVideoViewportVisible));
                OnPropertyChanged(nameof(HasCurrentMediaLoaded));
            }
            else if (e.PropertyName == nameof(AudioPlayer.IsLoadingMedia) ||
                     e.PropertyName == nameof(AudioPlayer.IsBuffering) ||
                     e.PropertyName == nameof(AudioPlayer.IsPlaying))
            {
                UpdateTrackLoadPendingState();
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

        private void UpdateTrackLoadPendingState()
        {
            if (AudioPlayer == null)
            {
                _pendingTrackLoadItem = null;
                IsTrackLoadPending = false;
                return;
            }

            if (!IsTrackLoadPending)
                return;

            if (_pendingTrackLoadItem == null)
            {
                IsTrackLoadPending = AudioPlayer.IsLoadingMedia || AudioPlayer.IsBuffering;
                return;
            }

            var requestedTrackIsCurrent = ReferenceEquals(AudioPlayer.CurrentMediaItem, _pendingTrackLoadItem) ||
                                          string.Equals(AudioPlayer.CurrentMediaItem?.FileName, _pendingTrackLoadItem.FileName, StringComparison.Ordinal);

            if (requestedTrackIsCurrent && !AudioPlayer.IsLoadingMedia && !AudioPlayer.IsBuffering)
            {
                _pendingTrackLoadItem = null;
                IsTrackLoadPending = false;
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

            // if the handle changed (e.g. mode switch) or buttons were never applied, add/rehook
            if (_taskbarHwnd != hwnd)
            {
                _taskbarHwnd = hwnd;

                // Re-create icons to ensure handles are fresh for the new window handle.
                const string prevChar = "\xE892";
                const string playChar = "\xE768";
                const string pauseChar = "\xE769";
                const string nextChar = "\xE893";

                _playIcon = TaskbarProgressHelper.CreateHIconFromCharacter(playChar, Colors.White);
                _pauseIcon = TaskbarProgressHelper.CreateHIconFromCharacter(pauseChar, Colors.White);

                _taskbarButtons =
                [
                    new TaskbarButton { Id = TaskbarButtonId.Previous, HIcon = TaskbarProgressHelper.CreateHIconFromCharacter(prevChar, Colors.White), Tooltip = "Previous", Flags = THUMBBUTTONFLAGS.Enabled },
                    new TaskbarButton { Id = TaskbarButtonId.PlayPause, HIcon = AudioPlayer?.IsPlaying == true ? _pauseIcon : _playIcon, Tooltip = AudioPlayer?.IsPlaying == true ? "Pause" : "Play", Flags = THUMBBUTTONFLAGS.Enabled },
                    new TaskbarButton { Id = TaskbarButtonId.Next, HIcon = TaskbarProgressHelper.CreateHIconFromCharacter(nextChar, Colors.White), Tooltip = "Next", Flags = THUMBBUTTONFLAGS.Enabled }
                ];

                if (desktop.MainWindow is Window window && window.WindowDecorations == Avalonia.Controls.WindowDecorations.None)
                {
                    // For borderless windows, Windows requires WS_CAPTION or WS_THICKFRAME to show thumbnail toolbar.
                    const int GWL_STYLE = -16;
                    const uint WS_CAPTION = 0x00C00000;
                    const uint WS_THICKFRAME = 0x00040000;
                    const uint WS_MINIMIZEBOX = 0x00020000;
                    const uint WS_MAXIMIZEBOX = 0x00010000;
                    const uint WS_SYSMENU = 0x00080000;

                    var style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                    // We need a caption and a sysmenu/minimize/maximize for the shell to treat it as a top-level app window
                    SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME));

                    // Force a frame change update
                    const uint SWP_FRAMECHANGED = 0x0020;
                    const uint SWP_NOMOVE = 0x0002;
                    const uint SWP_NOSIZE = 0x0001;
                    const uint SWP_NOZORDER = 0x0004;
                    const uint SWP_NOACTIVATE = 0x0010;
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }

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
    }
}
