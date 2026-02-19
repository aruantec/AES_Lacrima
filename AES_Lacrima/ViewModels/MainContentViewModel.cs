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
            LoadSettings();
        }

        protected override void OnLoadSettings(JsonObject section)
        {
            PlayerInfoLeft = ReadDoubleSetting(section, nameof(PlayerInfoLeft), double.NaN);
            PlayerInfoTop = ReadDoubleSetting(section, nameof(PlayerInfoTop), double.NaN);
            PlayerInfoWidth = ReadDoubleSetting(section, nameof(PlayerInfoWidth), 300);
            PlayerInfoHeight = ReadDoubleSetting(section, nameof(PlayerInfoHeight), double.NaN);
        }

        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, nameof(PlayerInfoLeft), PlayerInfoLeft);
            WriteSetting(section, nameof(PlayerInfoTop), PlayerInfoTop);
            WriteSetting(section, nameof(PlayerInfoWidth), PlayerInfoWidth);
            WriteSetting(section, nameof(PlayerInfoHeight), PlayerInfoHeight);
        }

        [RelayCommand]
        private void SaveWidgetSettings()
        {
            SaveSettings();
        }
    }
}