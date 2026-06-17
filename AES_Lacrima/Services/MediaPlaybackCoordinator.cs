using AES_Controls.Player;
using AES_Controls.Player.Models;
using AES_Core.DI;
using AES_Lacrima.ViewModels;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.ComponentModel;

namespace AES_Lacrima.Services;

public interface IMediaPlaybackCoordinator;

/// <summary>
/// Tracks which music or video view model owns the single active media player session.
/// </summary>
[AutoRegister(DependencyLifetime.Singleton)]
public partial class MediaPlaybackCoordinator : ObservableObject, IMediaPlaybackCoordinator
{
    private MusicViewModel? _subscribedViewModel;
    private AudioPlayer? _subscribedPlayer;
    private MediaItem? _subscribedMediaItem;

    private MusicViewModel? _musicViewModel;
    private VideoViewModel? _videoViewModel;

    [ObservableProperty]
    private MusicViewModel? _activeViewModel;

    /// <summary>
    /// The view model whose player should drive the main player widget and background visuals.
    /// </summary>
    public MusicViewModel? PlaybackViewModel => ResolvePlaybackViewModel();

    public AudioPlayer? ActivePlayer => PlaybackViewModel?.AudioPlayer;

    public MediaItem? ActiveSelectedMediaItem =>
        PlaybackViewModel?.SelectedMediaItem ?? ActivePlayer?.CurrentMediaItem;

    public Bitmap? ActiveCoverBitmap =>
        ActivePlayer?.CurrentMediaItem?.CoverBitmap
        ?? PlaybackViewModel?.SelectedMediaItem?.CoverBitmap;

    private MusicViewModel? ResolvePlaybackViewModel()
    {
        if (ActiveViewModel != null)
            return ActiveViewModel;

        if (_videoViewModel?.AudioPlayer?.CurrentMediaItem != null)
            return _videoViewModel;

        return _musicViewModel;
    }

    /// <summary>
    /// Registers the music view model used for shared playback UI resolution.
    /// </summary>
    internal void RegisterMusicViewModel(MusicViewModel musicViewModel)
    {
        _musicViewModel = musicViewModel;
    }

    /// <summary>
    /// Registers the video view model used for shared playback UI resolution.
    /// </summary>
    internal void RegisterVideoViewModel(VideoViewModel videoViewModel)
    {
        _videoViewModel = videoViewModel;
    }

    /// <summary>
    /// Marks <paramref name="owner"/> as the active playback source and stops any other media session.
    /// </summary>
    public void ClaimPlaybackSession(MusicViewModel owner)
    {
        if (owner == null)
            return;

        StopOtherPlayers(owner, _musicViewModel, _videoViewModel);

        if (!ReferenceEquals(ActiveViewModel, owner))
            ActiveViewModel = owner;
        else
            NotifyPlaybackBindingProperties();
    }

    partial void OnActiveViewModelChanged(MusicViewModel? value)
    {
        DetachPlaybackListeners();
        AttachPlaybackListeners(value);
        NotifyPlaybackBindingProperties();
    }

    private void AttachPlaybackListeners(MusicViewModel? viewModel)
    {
        _subscribedViewModel = viewModel;
        if (viewModel is INotifyPropertyChanged vmNotifier)
            vmNotifier.PropertyChanged += OnActiveViewModelPropertyChanged;

        _subscribedPlayer = viewModel?.AudioPlayer;
        if (_subscribedPlayer is INotifyPropertyChanged playerNotifier)
            playerNotifier.PropertyChanged += OnActivePlayerPropertyChanged;

        AttachMediaItemListener(_subscribedPlayer?.CurrentMediaItem);
    }

    private void DetachPlaybackListeners()
    {
        if (_subscribedViewModel is INotifyPropertyChanged vmNotifier)
            vmNotifier.PropertyChanged -= OnActiveViewModelPropertyChanged;

        if (_subscribedPlayer is INotifyPropertyChanged playerNotifier)
            playerNotifier.PropertyChanged -= OnActivePlayerPropertyChanged;

        AttachMediaItemListener(null);

        _subscribedViewModel = null;
        _subscribedPlayer = null;
    }

    private void AttachMediaItemListener(MediaItem? item)
    {
        if (_subscribedMediaItem is INotifyPropertyChanged oldNotifier)
            oldNotifier.PropertyChanged -= OnActiveMediaItemPropertyChanged;

        _subscribedMediaItem = item;

        if (item is INotifyPropertyChanged newNotifier)
            newNotifier.PropertyChanged += OnActiveMediaItemPropertyChanged;
    }

    private void OnActiveViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == PlaybackPropertyNames.SelectedMediaItem
            || e.PropertyName == PlaybackPropertyNames.ViewModelAudioPlayer)
        {
            if (e.PropertyName == PlaybackPropertyNames.ViewModelAudioPlayer
                && sender is MusicViewModel vm)
            {
                if (_subscribedPlayer is INotifyPropertyChanged oldPlayer)
                    oldPlayer.PropertyChanged -= OnActivePlayerPropertyChanged;

                _subscribedPlayer = vm.AudioPlayer;
                if (_subscribedPlayer is INotifyPropertyChanged newPlayer)
                    newPlayer.PropertyChanged += OnActivePlayerPropertyChanged;

                AttachMediaItemListener(_subscribedPlayer?.CurrentMediaItem);
            }

            NotifyPlaybackBindingProperties();
        }
    }

    private void OnActivePlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == PlaybackPropertyNames.CurrentMediaItem)
            AttachMediaItemListener(_subscribedPlayer?.CurrentMediaItem);

        if (e.PropertyName is PlaybackPropertyNames.CurrentMediaItem
            or PlaybackPropertyNames.Position
            or PlaybackPropertyNames.Duration
            or PlaybackPropertyNames.IsPlaying
            or PlaybackPropertyNames.IsLoadingMedia)
        {
            NotifyPlaybackBindingProperties();
        }
    }

    private void OnActiveMediaItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == PlaybackPropertyNames.CoverBitmap)
            NotifyPlaybackBindingProperties();
    }

    private void NotifyPlaybackBindingProperties()
    {
        OnPropertyChanged(BindingPropertyNames.PlaybackViewModel);
        OnPropertyChanged(BindingPropertyNames.ActivePlayer);
        OnPropertyChanged(BindingPropertyNames.ActiveSelectedMediaItem);
        OnPropertyChanged(BindingPropertyNames.ActiveCoverBitmap);
    }

    private static void StopOtherPlayers(
        MusicViewModel except,
        MusicViewModel? musicViewModel,
        VideoViewModel? videoViewModel)
    {
        foreach (var viewModel in EnumerateMediaViewModels(musicViewModel, videoViewModel))
        {
            if (ReferenceEquals(viewModel, except))
                continue;

            var player = viewModel.AudioPlayer;
            if (player == null)
                continue;

            if (!player.IsPlaying && player.CurrentMediaItem == null)
                continue;

            player.Stop();

            if (viewModel.IsVideoMode)
                viewModel.IsVideoViewportDismissed = true;
        }
    }

    private static IEnumerable<MusicViewModel> EnumerateMediaViewModels(
        MusicViewModel? musicViewModel,
        VideoViewModel? videoViewModel)
    {
        if (musicViewModel != null)
            yield return musicViewModel;

        if (videoViewModel != null && !ReferenceEquals(videoViewModel, musicViewModel))
            yield return videoViewModel;
    }

    private static class PlaybackPropertyNames
    {
        public const string SelectedMediaItem = nameof(MusicViewModel.SelectedMediaItem);
        public const string ViewModelAudioPlayer = nameof(MusicViewModel.AudioPlayer);
        public const string CurrentMediaItem = nameof(AudioPlayer.CurrentMediaItem);
        public const string Position = nameof(AudioPlayer.Position);
        public const string Duration = nameof(AudioPlayer.Duration);
        public const string IsPlaying = nameof(AudioPlayer.IsPlaying);
        public const string IsLoadingMedia = nameof(AudioPlayer.IsLoadingMedia);
        public const string CoverBitmap = nameof(MediaItem.CoverBitmap);
    }

    /// <summary>
    /// Compile-time property names raised by <see cref="NotifyPlaybackBindingProperties"/>.
    /// </summary>
    internal static class BindingPropertyNames
    {
        public const string PlaybackViewModel = nameof(MediaPlaybackCoordinator.PlaybackViewModel);
        public const string ActivePlayer = nameof(MediaPlaybackCoordinator.ActivePlayer);
        public const string ActiveCoverBitmap = nameof(MediaPlaybackCoordinator.ActiveCoverBitmap);
        public const string ActiveSelectedMediaItem = nameof(MediaPlaybackCoordinator.ActiveSelectedMediaItem);
        public const string ActiveViewModel = nameof(MediaPlaybackCoordinator.ActiveViewModel);
    }
}
