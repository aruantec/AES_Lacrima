using AES_Core.DI;
using AES_Lacrima.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AES_Lacrima.Services
{
    public interface INavigationService;

    [AutoRegister]
    public partial class NavigationService : ViewModelBase, INavigationService
    {
        private ViewModelBase? _previousViewModel;

        /// <summary>
        /// Backing field for the generated <c>View</c> property that holds
        /// the current view content displayed in the main window.
        /// </summary>
        [ObservableProperty]
        private ViewModelBase? _view;

        /// <summary>
        /// Shows or hides the settings overlay.
        /// </summary>
        [ObservableProperty]
        private bool _showSettingsOverlay;

        /// <summary>
        /// Gets or sets a value indicating whether the Back navigation is enabled.
        /// </summary>
        [ObservableProperty]
        private bool _isBackEnabled;

        /// <summary>
        /// Toggles the visibility of the settings overlay on changed.
        /// </summary>
        [RelayCommand]
        private void ToggleSettingsOverlay()
        {
            ShowSettingsOverlay = !ShowSettingsOverlay;
        }

        /// <summary>
        /// Navigates back to the previous view model if available.
        /// </summary>
        [RelayCommand]
        private void NavigateBack()
        {
            //Check if the settings overlay is open and close it
            if (ShowSettingsOverlay)
            {
                ShowSettingsOverlay = false;
                return;
            }
            //Check if the current view is a MusicViewModel and set it to inactive before navigating back to fade out
            if (View is MusicViewModel musicViewModel)
            {
                if (musicViewModel.MetadataService != null && 
                    musicViewModel.MetadataService.IsMetadataLoaded)
                {
                    musicViewModel.MetadataService.IsMetadataLoaded = false;
                    return;
                }
                else if (musicViewModel.IsEqualizerVisible)
                {
                    musicViewModel.IsEqualizerVisible = false;
                    return;
                }

                musicViewModel.IsActive = false;
            }
            //Set current view to previous view
            View = _previousViewModel;
            //Set back naviation
            IsBackEnabled = View is not MainContentViewModel;
        }

        /// <summary>
        /// Initial preparation of the navigation service
        /// </summary>
        public override void Prepare()
        {
            //Initial view values
            View = DiLocator.ResolveViewModel<MainContentViewModel>();
            //Set back naviation
            _previousViewModel = View;
        }

        /// <summary>
        /// Navigates to the view associated with the specified view model type.
        /// </summary>
        /// <remarks>This method resolves the view model of type <typeparamref name="T"/> using the
        /// dependency injection locator and sets it as the current view. If the resolved view model does not inherit
        /// from ViewModelBase, the navigation will not occur as expected.</remarks>
        /// <typeparam name="T">The type of the view model to navigate to. Must inherit from ViewModelBase.</typeparam>
        public void NavigateTo<T>()
        {
            //Set previous view
            _previousViewModel = View;
            //Set current view
            View = DiLocator.ResolveViewModel<T>() as ViewModelBase;
            //Set back naviation
            IsBackEnabled = View is not MainContentViewModel;
        }
    }
}
