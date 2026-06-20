using System;
using System.ComponentModel;
using AES_Controls.Composition;
using AES_Controls.Widgets;
using AES_Core.DI;
using AES_Lacrima.Services;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels
{
    /// <summary>
    /// Marker interface for the main content view model used by the view
    /// locator and dependency injection container.
    /// </summary>
    public interface IMainContentViewModel;

    /// <summary>
    /// View model that exposes commands for navigating the main application
    /// content (emulation, music, video) and toggling the settings overlay.
    /// </summary>
    [AutoRegister]
    internal partial class MainContentViewModel : ViewModelBase, IMainContentViewModel
    {
        public const double MainMenuHeight = 160;
        private const double DefaultPlayerInfoHeight = 160;

        private double _lastWidgetContainerWidth;
        private double _lastWidgetContainerHeight;

        [ObservableProperty]
        private double _playerInfoLeft = double.NaN;

        [ObservableProperty]
        private double _playerInfoTop = double.NaN;

        [ObservableProperty]
        private double _playerInfoWidth = 500;

        [ObservableProperty]
        private double _playerInfoHeight = double.NaN;

        [ObservableProperty]
        private double _clockLeft = double.NaN;

        [ObservableProperty]
        private double _clockTop = double.NaN;

        [ObservableProperty]
        private double _clockWidth = 250;

        [ObservableProperty]
        private double _clockHeight = 250;

        [ObservableProperty]
        private double _playerLeft = double.NaN;

        [ObservableProperty]
        private double _playerTop = double.NaN;

        [ObservableProperty]
        private double _playerWidth = 250;

        [ObservableProperty]
        private double _playerHeight = 300;

        [ObservableProperty]
        private bool _playerShowControls;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ClockMenuText))]
        private bool _showClock = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayerInfoMenuText))]
        private bool _showPlayerInfo = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayerMenuText))]
        private bool _showPlayer = true;

        /// <summary>
        /// True when no custom widget coordinates have been saved yet.
        /// </summary>
        public bool UsesDefaultWidgetLayout =>
            double.IsNaN(ClockLeft) && double.IsNaN(ClockTop) &&
            double.IsNaN(PlayerLeft) && double.IsNaN(PlayerTop) &&
            double.IsNaN(PlayerInfoLeft) && double.IsNaN(PlayerInfoTop);

        /// <summary>
        /// Applies the initial home-screen widget layout for first launch or reset.
        /// </summary>
        public void ApplyDefaultWidgetLayout(double containerWidth, double containerHeight)
        {
            if (containerWidth <= 0 || containerHeight <= 0)
                return;

            _lastWidgetContainerWidth = containerWidth;
            _lastWidgetContainerHeight = containerHeight;
            MainContentWidgetLayout.Apply(this, containerWidth, containerHeight, MainMenuHeight);
        }

        /// <summary>
        /// Re-applies layout anchors after load or container resize without persisting pinned responsive drift.
        /// </summary>
        public void ReconcileWidgetLayout(double containerWidth, double containerHeight)
        {
            if (containerWidth <= 0 || containerHeight <= 0)
                return;

            _lastWidgetContainerWidth = containerWidth;
            _lastWidgetContainerHeight = containerHeight;

            if (UsesAnchoredPlayerInfoLayout)
                ApplyFullWidthPlayerInfoLayout(containerWidth, containerHeight);

            if (ShouldKeepPlayerCentered(containerWidth, containerHeight))
            {
                PlayerWidth = Math.Max(180, containerWidth * MainContentWidgetLayout.PlayerWidthRatio);
                PlayerHeight = Math.Max(200, containerHeight * MainContentWidgetLayout.PlayerHeightRatio);
                ApplyCenteredPlayerLayout(containerWidth, containerHeight);
            }
        }

        private bool UsesAnchoredPlayerInfoLayout => PlayerInfoLeft <= 1.0;

        private bool ShouldKeepPlayerCentered(double containerWidth, double containerHeight)
        {
            if (PlayerWidth <= 0 || PlayerHeight <= 0 ||
                double.IsNaN(PlayerLeft) || double.IsNaN(PlayerTop))
            {
                return false;
            }

            var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(new Size(PlayerWidth, PlayerHeight));
            var centerX = PlayerLeft + discCenter.X;
            var centerY = PlayerTop + discCenter.Y;
            var targetX = containerWidth * 0.5;
            var targetY = containerHeight * 0.5;

            // Re-anchor turntable widgets that are still roughly centered (including drifted defaults).
            return Math.Abs(centerX - targetX) < 320 && Math.Abs(centerY - targetY) < 320;
        }

        private void ApplyCenteredPlayerLayout(double containerWidth, double containerHeight)
        {
            var discCenter = PlayerCompositionControl.GetDiscCenterInBounds(new Size(PlayerWidth, PlayerHeight));
            PlayerLeft = (containerWidth * 0.5) - discCenter.X;
            PlayerTop = (containerHeight * 0.5) - discCenter.Y;
        }

        private void ApplyFullWidthPlayerInfoLayout(double containerWidth, double containerHeight)
        {
            var playerInfoHeight = PlayerInfoHeight > 0 ? PlayerInfoHeight : DefaultPlayerInfoHeight;
            PlayerInfoLeft = 0;
            PlayerInfoTop = containerHeight - MainMenuHeight - playerInfoHeight;
            PlayerInfoWidth = containerWidth;
            PlayerInfoHeight = playerInfoHeight;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EditModeMenuText))]
        private bool _isWidgetEditMode;

        /// <summary>
        /// Gets the menu text for the clock toggle based on current visibility.
        /// </summary>
        public string ClockMenuText => ShowClock ? "Hide Clock" : "Show Clock";

        /// <summary>
        /// Gets the menu text for the player info toggle based on current visibility.
        /// </summary>
        public string PlayerInfoMenuText => ShowPlayerInfo ? "Hide Player Info" : "Show Player Info";

        /// <summary>
        /// Gets the menu text for the player toggle based on current visibility.
        /// </summary>
        public string PlayerMenuText => ShowPlayer ? "Hide Player" : "Show Player";

        /// <summary>
        /// Gets the menu text for the widget edit mode toggle.
        /// </summary>
        public string EditModeMenuText => IsWidgetEditMode ? "Exit Edit Mode" : "Edit Widget Layout";

        /// <summary>
        /// Provides access to the navigation service used for managing
        /// navigation within the application. Resolved by the DI container.
        /// </summary>
        [AutoResolve]
        private NavigationService? _navigationService;

        [AutoResolve]
        [ObservableProperty]
        private MusicViewModel? _musicViewModel;

        [AutoResolve]
        [ObservableProperty]
        private VideoViewModel? _videoViewModel;

        [AutoResolve]
        private MediaPlaybackCoordinator? _mediaPlaybackCoordinator;

        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        [AutoResolve]
        private MainWindowViewModel? _mainWindowViewModel;

        /// <summary>
        /// The music or video view model that currently owns playback for the main player widget.
        /// </summary>
        public MusicViewModel? PlaybackViewModel => _mediaPlaybackCoordinator?.PlaybackViewModel;

        public MediaPlaybackCoordinator? PlaybackCoordinator => _mediaPlaybackCoordinator;

        public MainContentViewModel()
        {
            PropertyChanged += OnPropertyChanged;
        }

        partial void OnMusicViewModelChanged(MusicViewModel? value) => SubscribePlaybackCoordinator();

        partial void OnVideoViewModelChanged(VideoViewModel? value) => SubscribePlaybackCoordinator();

        private bool _playbackCoordinatorSubscribed;

        private void SubscribePlaybackCoordinator()
        {
            if (_mediaPlaybackCoordinator == null)
                return;

            if (!_playbackCoordinatorSubscribed)
            {
                _mediaPlaybackCoordinator.PropertyChanged += OnPlaybackCoordinatorPropertyChanged;
                _playbackCoordinatorSubscribed = true;
            }

            OnPropertyChanged(nameof(PlaybackViewModel));
            OnPropertyChanged(nameof(PlaybackCoordinator));
        }

        private void OnPlaybackCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is MediaPlaybackCoordinator.BindingPropertyNames.PlaybackViewModel
                or MediaPlaybackCoordinator.BindingPropertyNames.ActivePlayer
                or MediaPlaybackCoordinator.BindingPropertyNames.ActiveCoverBitmap
                or MediaPlaybackCoordinator.BindingPropertyNames.ActiveSelectedMediaItem
                or MediaPlaybackCoordinator.BindingPropertyNames.ActiveViewModel)
            {
                OnPropertyChanged(nameof(PlaybackViewModel));
                OnPropertyChanged(nameof(PlaybackCoordinator));
            }
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayerShowControls))
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// Command that navigates to the emulation view.
        /// </summary>
        [RelayCommand]
        public void ShowEmulation()
        {
            _navigationService?.NavigateTo<EmulationViewModel>();
        }

        /// <summary>
        /// Command that navigates to the music view.
        /// </summary>
        [RelayCommand]
        public void ShowMusic()
        {
            _navigationService?.NavigateTo<MusicViewModel>();
        }

        /// <summary>
        /// Command that navigates to the video view.
        /// </summary>
        [RelayCommand]
        public void ShowVideo()
        {
            _navigationService?.NavigateTo<VideoViewModel>();
        }

        /// <summary>
        /// Command that toggles the visibility of the settings overlay.
        /// </summary>
        [RelayCommand]
        private void ShowSettings()
        {
            _navigationService?.ToggleSettingsOverlayCommand.Execute(null);
        }

        public override void Prepare()
        {
            base.Prepare();
            //Set initial IsActive according to the settings
            if (SettingsViewModel != null)
                IsActive = SettingsViewModel.ShowShaderToy;
            //Load settings
            LoadSettings();
            SubscribePlaybackCoordinator();
        }

        public override void OnViewFullyVisible()
        {
            base.OnViewFullyVisible();
            if (_mainWindowViewModel != null)
            {
                _mainWindowViewModel.IsShaderToyRenderingPaused = false;
            }
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            var playerInfoLeft = ReadDoubleSetting(section, nameof(PlayerInfoLeft), double.NaN);
            var playerInfoTop = ReadDoubleSetting(section, nameof(PlayerInfoTop), double.NaN);
            var playerInfoWidth = ReadDoubleSetting(section, nameof(PlayerInfoWidth), 300);
            var playerInfoHeight = ReadDoubleSetting(section, nameof(PlayerInfoHeight), double.NaN);

            var clockLeft = ReadDoubleSetting(section, nameof(ClockLeft), double.NaN);
            var clockTop = ReadDoubleSetting(section, nameof(ClockTop), double.NaN);
            var clockWidth = ReadDoubleSetting(section, nameof(ClockWidth), 250);
            var clockHeight = ReadDoubleSetting(section, nameof(ClockHeight), 250);

            var playerLeft = ReadDoubleSetting(section, nameof(PlayerLeft), double.NaN);
            var playerTop = ReadDoubleSetting(section, nameof(PlayerTop), double.NaN);
            var playerWidth = ReadDoubleSetting(section, nameof(PlayerWidth), 250);
            var playerHeight = ReadDoubleSetting(section, nameof(PlayerHeight), 300);

            var scaleFactor = SettingsViewModel?.ScaleFactor ?? 1.0;
            var windowWidth = _mainWindowViewModel?.WindowWidth ?? MainContentWidgetLayout.ReferenceContainerWidth;
            MainContentWidgetLayout.NormalizeLegacyAbsoluteValues(
                ref playerInfoLeft,
                ref playerInfoTop,
                ref playerInfoWidth,
                ref playerInfoHeight,
                ref clockLeft,
                ref clockTop,
                ref clockWidth,
                ref clockHeight,
                ref playerLeft,
                ref playerTop,
                ref playerWidth,
                ref playerHeight,
                scaleFactor,
                windowWidth);

            PlayerInfoLeft = playerInfoLeft;
            PlayerInfoTop = playerInfoTop;
            PlayerInfoWidth = playerInfoWidth;
            PlayerInfoHeight = playerInfoHeight;

            ClockLeft = clockLeft;
            ClockTop = clockTop;
            ClockWidth = clockWidth;
            ClockHeight = clockHeight;

            PlayerLeft = playerLeft;
            PlayerTop = playerTop;
            PlayerWidth = playerWidth;
            PlayerHeight = playerHeight;
            PlayerShowControls = ReadBoolSetting(section, nameof(PlayerShowControls));

            ShowClock = ReadBoolSetting(section, nameof(ShowClock), true);
            ShowPlayerInfo = ReadBoolSetting(section, nameof(ShowPlayerInfo), true);
            ShowPlayer = ReadBoolSetting(section, nameof(ShowPlayer), true);
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(PlayerInfoLeft), PlayerInfoLeft);
            WriteSetting(section, nameof(PlayerInfoTop), PlayerInfoTop);
            WriteSetting(section, nameof(PlayerInfoWidth), PlayerInfoWidth);
            WriteSetting(section, nameof(PlayerInfoHeight), PlayerInfoHeight);
            
            WriteSetting(section, nameof(ClockLeft), ClockLeft);
            WriteSetting(section, nameof(ClockTop), ClockTop);
            WriteSetting(section, nameof(ClockWidth), ClockWidth);
            WriteSetting(section, nameof(ClockHeight), ClockHeight);

            WriteSetting(section, nameof(PlayerLeft), PlayerLeft);
            WriteSetting(section, nameof(PlayerTop), PlayerTop);
            WriteSetting(section, nameof(PlayerWidth), PlayerWidth);
            WriteSetting(section, nameof(PlayerHeight), PlayerHeight);
            WriteSetting(section, nameof(PlayerShowControls), PlayerShowControls);
            
            WriteSetting(section, nameof(ShowClock), ShowClock);
            WriteSetting(section, nameof(ShowPlayerInfo), ShowPlayerInfo);
            WriteSetting(section, nameof(ShowPlayer), ShowPlayer);
        }

        [RelayCommand]
        private void SaveWidgetSettings(WidgetMoveResizeEndedArgs? args)
        {
            if (args?.Result is { } result)
            {
                switch (args.SettingsKey)
                {
                    case "Clock":
                        ClockLeft = result.Left;
                        ClockTop = result.Top;
                        ClockWidth = result.Width;
                        ClockHeight = result.Height;
                        break;
                    case "Player":
                        PlayerLeft = result.Left;
                        PlayerTop = result.Top;
                        PlayerWidth = result.Width;
                        PlayerHeight = result.Height;
                        break;
                    case "PlayerInfo":
                        PlayerInfoLeft = result.Left;
                        PlayerInfoTop = result.Top;
                        PlayerInfoWidth = result.Width;
                        PlayerInfoHeight = result.Height;
                        break;
                }
            }

            SaveSettings();
        }

        [RelayCommand]
        private void ToggleClockVisibility()
        {
            ShowClock = !ShowClock;
            SaveSettings();
        }

        [RelayCommand]
        private void TogglePlayerInfoVisibility()
        {
            ShowPlayerInfo = !ShowPlayerInfo;
            SaveSettings();
        }

        [RelayCommand]
        private void TogglePlayerVisibility()
        {
            ShowPlayer = !ShowPlayer;
            SaveSettings();
        }

        [RelayCommand]
        private void ToggleWidgetEditMode()
        {
            IsWidgetEditMode = !IsWidgetEditMode;
        }

        [RelayCommand]
        private void ResetWidgetLayout()
        {
            if (_lastWidgetContainerWidth > 0 && _lastWidgetContainerHeight > 0)
                ApplyDefaultWidgetLayout(_lastWidgetContainerWidth, _lastWidgetContainerHeight);
            else
                MainContentWidgetLayout.Apply(
                    this,
                    MainContentWidgetLayout.ReferenceContainerWidth,
                    MainContentWidgetLayout.ReferenceContainerHeight,
                    MainMenuHeight);

            SaveSettings();
        }
    }
}
