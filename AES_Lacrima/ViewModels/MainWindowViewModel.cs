using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Lacrima.Models;
using AES_Lacrima.Services;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace AES_Lacrima.ViewModels
{
    public interface IMainWindowViewModel;

    /// <summary>
    /// View-model for the main application window. Responsible for storing
    /// window size state and the currently displayed view. The class is
    /// registered for dependency injection via the <c>[AutoRegister]</c>
    /// attribute so it can be resolved by the application's DI locator.
    /// </summary>
    [AutoRegister]
    public partial class MainWindowViewModel : ViewModelBase, IMainWindowViewModel
    {
        private IClassicDesktopStyleApplicationLifetime? AppLifetime => Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        private Dictionary<MenuItem, Action<Avalonia.Controls.WindowState>> _registeredMenuItems = [];

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
        /// Gets or sets the view model that manages application settings.
        /// </summary>
        [AutoResolve]
        [ObservableProperty]
        private SettingsViewModel? _settingsViewModel;

        /// <summary>
        /// Provides access to the navigation service used for managing navigation within the application.
        /// </summary>
        [AutoResolve]
        [ObservableProperty]
        private NavigationService? _navigationService;

        /// <summary>
        /// Gets or sets the collection of spectrum data points.
        /// </summary>
        [ObservableProperty]
        private AvaloniaList<double>? _spectrum;


        /// <summary>
        /// Prepare the view-model for use. This implementation loads
        /// persisted settings such as window size so the UI can be
        /// restored to the previous state.
        /// </summary>
        public override void Prepare()
        {
            //Load persisted settings
            LoadSettings();
            //Initialize window buttons with their respective icons and tooltips
            WindowButtons =
            [
                new MenuItem() { Command = NavigationService?.ToggleSettingsOverlayCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "settingsgear.svg"), Tooltip = "Settings"},
                new MenuItem() { Command = FullScreenCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "fullscreen.svg"), Tooltip = "Go Fullscreen"},
                new MenuItem() { Command = MaximizeCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "maximize.svg"), Tooltip = "Maximize Window"},
                new MenuItem() { Command = MinimizeWindowCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "minimize.svg"), Tooltip = "Minimize Window"},
                new MenuItem() { Command = CloseApplicationCommand, Cover = Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "close.svg"), Tooltip = "Close Application"}
            ];
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
        /// Maximizes the window and updates the associated menu item's icon and tooltip to reflect the maximized state.
        /// </summary>
        [RelayCommand]
        private void Maximize(object item)
        {
            //Set icon according to the state
            if (item is MenuItem menuItem && !_registeredMenuItems.ContainsKey(menuItem))
            {
                _registeredMenuItems.Add(menuItem, (state) =>
                {
                    menuItem.Cover = state == Avalonia.Controls.WindowState.Maximized 
                        ? Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "maximizedexit.svg") 
                        : Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "maximize.svg");
                    menuItem.Tooltip = state == Avalonia.Controls.WindowState.Maximized ? "Normal Window" : "Maximize Window";
                });
            }
            //Change state
            SetWindowState(Avalonia.Controls.WindowState.Maximized);
        }

        /// <summary>
        /// Switches the application window to full-screen mode and updates the associated menu item's icon and tooltip
        /// to reflect the new state.
        /// </summary>
        [RelayCommand]
        private void FullScreen(object item)
        {
            //Set icon according to the state
            if (item is MenuItem menuItem && !_registeredMenuItems.ContainsKey(menuItem))
            {
                _registeredMenuItems.Add(menuItem, (state) =>
                {
                    menuItem.Cover = state == Avalonia.Controls.WindowState.FullScreen 
                        ? Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "fullscreenexit.svg") 
                        : Path.Combine(AppContext.BaseDirectory, "Assets", "Main", "fullscreen.svg");
                    menuItem.Tooltip = state == Avalonia.Controls.WindowState.FullScreen ? "Exit Fullscreen" : "Go Fullscreen";
                });
            }
            SetWindowState(Avalonia.Controls.WindowState.FullScreen);
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

        /// <summary>
        /// Sets the window state of the application's main window to the specified value, or restores it to normal if
        /// it is already in that state.
        /// </summary>
        /// <param name="state">The desired window state to apply to the main window. If the main window is already in this state, it will
        /// be set to normal instead.</param>
        private void SetWindowState(Avalonia.Controls.WindowState state)
        {
            if (AppLifetime?.MainWindow is { } mainWindow)
            {
                //Set the window state
                mainWindow.WindowState = mainWindow.WindowState == state ? Avalonia.Controls.WindowState.Normal : state;
                //Invoke registered menu items actions
                foreach (var menuItem in _registeredMenuItems)
                    menuItem.Value.Invoke(mainWindow.WindowState);
            }
        }
    }
}
