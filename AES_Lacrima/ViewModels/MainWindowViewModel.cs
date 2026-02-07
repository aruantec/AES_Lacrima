using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Lacrima.Models;
using AES_Lacrima.ViewModels.Navigation;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels
{
    /// <summary>
    /// View-model for the main application window. Responsible for storing
    /// window size state and the currently displayed view. The class is
    /// registered for dependency injection via the <c>[AutoRegister]</c>
    /// attribute so it can be resolved by the application's DI locator.
    /// </summary>
    [AutoRegister]
    public partial class MainWindowViewModel : ViewModelBase
    {
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        /// <summary>
        /// Shows or hides the settings overlay.
        /// </summary>
        [ObservableProperty]
        private bool _showSettingsOverlay;

        /// <summary>
        /// List of window buttons.
        /// </summary>
        [ObservableProperty]
        private AvaloniaList<MenuItem>? _windowButtons;

        /// <summary>
        /// Backing field for the generated <c>WindowWidth</c> property.
        /// Represents the last persisted window width. Initialized to
        /// <see cref="double.NaN"/> to indicate an unspecified value.
        /// </summary>
        [ObservableProperty]
        private double _windowWidth = double.NaN;

        /// <summary>
        /// Backing field for the generated <c>WindowHeight</c> property.
        /// Represents the last persisted window height. Initialized to
        /// <see cref="double.NaN"/> to indicate an unspecified value.
        /// </summary>
        [ObservableProperty]
        private double _windowHeight = double.NaN;

        /// <summary>
        /// Backing field for the generated <c>View</c> property that holds
        /// the current view content displayed in the main window.
        /// </summary>
        [ObservableProperty]
        private ViewModelBase? _view;

        /// <summary>
        /// Gets or sets the current navigation view model.
        /// </summary>
        [ObservableProperty]
        private ViewModelBase? _navigationView;

        /// <summary>
        /// Prepare the view-model for use. This implementation loads
        /// persisted settings such as window size so the UI can be
        /// restored to the previous state.
        /// </summary>
        public override void Prepare()
        {
            //Set main navigation view
            NavigationView = DiLocator.ResolveViewModel<MainMenuViewModel>();
            //Load persisted settings
            LoadSettings();
            //Initialize window buttons with their respective icons and tooltips
            WindowButtons =
            [
                new MenuItem() { Command = ToggleSettingsOverlayCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "settingsgear.svg"), Tooltip = "Settings"},
                new MenuItem() { Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "fullscreen.svg"), Tooltip = "Go Fullscreen"},
                new MenuItem() { Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "maximize.svg"), Tooltip = "Maximize Window"},
                new MenuItem() { Command = MinimizeWindowCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "minimize.svg"), Tooltip = "Minimize Window"},
                new MenuItem() { Command = CloseApplicationCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "close.svg"), Tooltip = "Close Application"}
            ];
        }

        /// <summary>
        /// Toggles the visibility of the settings overlay on changed.
        /// </summary>
        [RelayCommand]
        private void ToggleSettingsOverlay()
        {
            ShowSettingsOverlay = !ShowSettingsOverlay;
        }

        /// <summary>
        /// Closes the application and performs necessary cleanup operations before shutdown.
        /// </summary>
        [RelayCommand]
        private void CloseApplication()
        {
            if (AppLifetime == null || DiLocator.ResolveViewModel<ISettingsService>() is not { } settingsService)
                return;
            //Save all settings
            settingsService.SaveSettings();
            //Dispose
            DiLocator.Dispose();
            //Shutdown application
            AppLifetime.Shutdown();
        }

        /// <summary>
        /// Minimizes the application.
        /// </summary>
        /// <remarks>This command has no effect if the main window is not available or is already
        /// minimized.</remarks>
        [RelayCommand]
        private void MinimizeWindow()
        {
            AppLifetime?.MainWindow?.WindowState = Avalonia.Controls.WindowState.Minimized;
        }

        /// <summary>
        /// Called by the settings infrastructure to populate this view-model
        /// from the provided JSON section. Reads and applies persisted
        /// window size values if present.
        /// </summary>
        /// <param name="section">JSON object containing saved settings for this view-model.</param>
        protected override void OnLoadSettings(JsonObject section)
        {
            WindowWidth = ReadDoubleSetting(section, "WindowWidth", WindowWidth);
            WindowHeight = ReadDoubleSetting(section, "WindowHeight", WindowHeight);
        }

        /// <summary>
        /// Called by the settings infrastructure to persist this view-model's
        /// state into the provided JSON section. Stores the current window
        /// width and height so they can be restored on the next startup.
        /// </summary>
        /// <param name="section">JSON object to populate with this view-model's settings.</param>
        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, "WindowWidth", WindowWidth);
            WriteSetting(section, "WindowHeight", WindowHeight);
        }
    }
}
