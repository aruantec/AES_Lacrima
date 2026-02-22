using System.ComponentModel;
using AES_Core.DI;
using AES_Lacrima.Services;
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
        private bool _showClock;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayerInfoMenuText))]
        private bool _showPlayerInfo = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayerMenuText))]
        private bool _showPlayer;

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
        private SettingsViewModel? _settingsViewModel;

        public MainContentViewModel()
        {
            PropertyChanged += OnPropertyChanged;
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
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            PlayerInfoLeft = ReadDoubleSetting(section, nameof(PlayerInfoLeft), double.NaN);
            PlayerInfoTop = ReadDoubleSetting(section, nameof(PlayerInfoTop), double.NaN);
            PlayerInfoWidth = ReadDoubleSetting(section, nameof(PlayerInfoWidth), 300);
            PlayerInfoHeight = ReadDoubleSetting(section, nameof(PlayerInfoHeight), double.NaN);
            
            ClockLeft = ReadDoubleSetting(section, nameof(ClockLeft), double.NaN);
            ClockTop = ReadDoubleSetting(section, nameof(ClockTop), double.NaN);
            ClockWidth = ReadDoubleSetting(section, nameof(ClockWidth), 250);
            ClockHeight = ReadDoubleSetting(section, nameof(ClockHeight), 250);

            PlayerLeft = ReadDoubleSetting(section, nameof(PlayerLeft), double.NaN);
            PlayerTop = ReadDoubleSetting(section, nameof(PlayerTop), double.NaN);
            PlayerWidth = ReadDoubleSetting(section, nameof(PlayerWidth), 250);
            PlayerHeight = ReadDoubleSetting(section, nameof(PlayerHeight), 300);
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
        private void SaveWidgetSettings()
        {
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
    }
}