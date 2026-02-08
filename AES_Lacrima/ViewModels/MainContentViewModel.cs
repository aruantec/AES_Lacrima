using AES_Core.DI;
using AES_Lacrima.Services;
using CommunityToolkit.Mvvm.Input;

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
        /// <summary>
        /// Provides access to the navigation service used for managing
        /// navigation within the application. Resolved by the DI container.
        /// </summary>
        [AutoResolve]
        private NavigationService? _navigationService;

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
    }
}