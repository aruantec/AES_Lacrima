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

    /// <summary>
    /// The view model whose player should drive shared UI when no explicit active session exists.
    /// </summary>
    public MusicViewModel? ResolvePlaybackViewModel()
    {
        if (ActiveViewModel != null)
            return ActiveViewModel;

        if (DiLocator.ResolveViewModel<VideoViewModel>() is { AudioPlayer.CurrentMediaItem: not null } video)
            return video;

        return DiLocator.ResolveViewModel<MusicViewModel>();
    }

    /// <summary>
    /// Marks <paramref name="owner"/> as the active playback source and stops any other media session.
    /// </summary>
    public void ClaimPlaybackSession(MusicViewModel owner)
    {
        if (owner == null)
            return;

        StopOtherPlayers(owner);

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
        if (e.PropertyName == nameof(MusicViewModel.SelectedMediaItem)
            || e.PropertyName == nameof(MusicViewModel.AudioPlayer))
        {
            if (e.PropertyName == nameof(MusicViewModel.AudioPlayer)
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
        if (e.PropertyName == nameof(AudioPlayer.CurrentMediaItem))
            AttachMediaItemListener(_subscribedPlayer?.CurrentMediaItem);

        if (e.PropertyName is nameof(AudioPlayer.CurrentMediaItem)
            or nameof(AudioPlayer.Position)
            or nameof(AudioPlayer.Duration)
            or nameof(AudioPlayer.IsPlaying)
            or nameof(AudioPlayer.IsLoadingMedia))
        {
            NotifyPlaybackBindingProperties();
        }
    }

    private void OnActiveMediaItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaItem.CoverBitmap))
            NotifyPlaybackBindingProperties();
    }

    private void NotifyPlaybackBindingProperties()
    {
        OnPropertyChanged(nameof(PlaybackViewModel));
        OnPropertyChanged(nameof(ActivePlayer));
        OnPropertyChanged(nameof(ActiveSelectedMediaItem));
        OnPropertyChanged(nameof(ActiveCoverBitmap));
    }

    private static void StopOtherPlayers(MusicViewModel except)
    {
        foreach (var viewModel in EnumerateMediaViewModels())
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

    private static IEnumerable<MusicViewModel> EnumerateMediaViewModels()
    {
        MusicViewModel? music = DiLocator.ResolveViewModel<MusicViewModel>();
        if (music != null)
            yield return music;

        if (DiLocator.ResolveViewModel<VideoViewModel>() is MusicViewModel video
            && !ReferenceEquals(video, music))
        {
            yield return video;
        }
    }
}
