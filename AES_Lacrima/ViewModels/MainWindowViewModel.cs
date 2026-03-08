using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Lacrima.Models;
using AES_Lacrima.Services;
using AES_Core.Services;
using AES_Lacrima.ViewModels.Prompts;
using AES_Controls.Helpers;
using Avalonia.Collections;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MaximizeTooltip))]
        private bool _isMaximized;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FullScreenTooltip))]
        private bool _isFullScreen;

        public string MaximizeTooltip => IsMaximized ? "Normal Window" : "Maximize Window";
        public string FullScreenTooltip => IsFullScreen ? "Exit Fullscreen" : "Go Fullscreen";

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

        partial void OnSettingsViewModelChanged(SettingsViewModel? value)
        {
            if (value != null)
            {
                value.PropertyChanged += OnSettingsViewModelPropertyChanged;
                SubscribeToMpvManager(value.MpvManager);
            }
        }

        private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.MpvManager))
            {
                SubscribeToMpvManager(SettingsViewModel?.MpvManager);
            }
        }

        private void SubscribeToMpvManager(MpvLibraryManager? manager)
        {
            if (manager == null) return;

            // Avoid duplicate subscriptions
            manager.PropertyChanged -= OnMpvManagerPropertyChanged;
            manager.PropertyChanged += OnMpvManagerPropertyChanged;

            if (manager.IsPendingRestart)
            {
                ShowRestartPrompt();
            }
        }

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
        /// Gets or sets the prompt view model associated with the current context.
        /// </summary>
        [ObservableProperty]
        private ViewModelBase? _promptView;

        /// <summary>
        /// Prepare the view-model for use. This implementation loads
        /// persisted settings such as window size so the UI can be
        /// restored to the previous state.
        /// </summary>
        public override void Prepare()
        {
            //Load persisted settings
            LoadSettings();

            // Hook up MpvManager and YtDlpManager if SettingsViewModel is already available
            if (SettingsViewModel != null)
            {
                SubscribeToMpvManager(SettingsViewModel.MpvManager);
            }
        }

        /// <summary>
        /// Handles changes to the MpvLibraryManager properties to detect pending restart triggers.
        /// </summary>
        private void OnMpvManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MpvLibraryManager.IsPendingRestart))
            {
                if (SettingsViewModel?.MpvManager != null && SettingsViewModel.MpvManager.IsPendingRestart)
                {
                    ShowRestartPrompt();
                }
            }
        }

        /// <summary>
        /// Displays the restart prompt in the application's overlay.
        /// </summary>
        public void ShowRestartPrompt()
        {
            if (PromptView is RestartPromptViewModel) return;

            var prompt = new RestartPromptViewModel();
            prompt.RequestClose += () => { if (PromptView == prompt) PromptView = null; };
            PromptView = prompt;
        }

        /// <summary>
        /// Displays the initial setup prompt for missing components.
        /// </summary>
        public void ShowSetupPrompt()
        {
            // Do not show the setup prompt if a restart is pending for libmpv, as that implies it's already "installed" or staged.
            if (SettingsViewModel?.MpvManager != null && SettingsViewModel.MpvManager.IsPendingRestart)
            {
                ShowRestartPrompt();
                return;
            }

            if (PromptView is ComponentSetupPromptViewModel || PromptView is RestartPromptViewModel) return;

            var ffmpeg = DiLocator.ResolveViewModel<FFmpegManager>();
            var mpv = DiLocator.ResolveViewModel<MpvLibraryManager>();
            var ytdlp = DiLocator.ResolveViewModel<YtDlpManager>();

            var prompt = new ComponentSetupPromptViewModel(ffmpeg, mpv, ytdlp, () => NavigationService?.NavigateToSettings(3));
            prompt.RequestClose += () => { if (PromptView == prompt) PromptView = null; };
            PromptView = prompt;
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
        /// Maximizes the window.
        /// </summary>
        [RelayCommand]
        private void Maximize()
        {
            //Change state
            SetWindowState(Avalonia.Controls.WindowState.Maximized);
        }

        /// <summary>
        /// Switches the application window to full-screen mode.
        /// </summary>
        [RelayCommand]
        private void FullScreen()
        {
            SetWindowState(Avalonia.Controls.WindowState.FullScreen);
        }

        /// <summary>
        /// Switches the application into the mini/CustomWindow mode and persists the
        /// selected mode to settings.
        /// </summary>
        [RelayCommand]
        private void SwitchMode()
        {
            if (AppLifetime == null)
                return;

            // indicate to the app that a mode transition is in progress so
            // the closing handler won't dispose the DI container.
            App.IsSwitchingMode = true;

            // save any transient main‑window state before we blow it away.
            if (AppLifetime.MainWindow?.DataContext is IViewModelBase vm)
            {
                try { vm.SaveSettings(); } catch { }
            }
            DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();

            // update persistent setting
            if (SettingsViewModel != null)
            {
                SettingsViewModel.AppMode = 1;
                SettingsViewModel.SaveSettings();
            }

            // construct new window and make it the lifetime's main window
            var newWindow = new Mini.Views.CustomWindow();
            newWindow.Closing += (s, e) =>
            {
                // Save everything and clean up DI if the app is really shutting down.
                DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();
                if (!App.IsSwitchingMode)
                {
                    try { DiLocator.Dispose(); } catch { }
                }
            };

            var oldWindow = AppLifetime.MainWindow;
            AppLifetime.MainWindow = newWindow;
            newWindow.Show();

            // refresh the taskbar buttons for the music view-model so that thumbnail
            // buttons are re‑added to the new window handle.
            var musicVm = DiLocator.ResolveViewModel<MusicViewModel>();
            musicVm?.InitializeTaskbarButtons();

            // close old window *after* we've shown the new one, then clear flag
            oldWindow?.Close();
            App.IsSwitchingMode = false;
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
                IsMaximized = mainWindow.WindowState == Avalonia.Controls.WindowState.Maximized;
                IsFullScreen = mainWindow.WindowState == Avalonia.Controls.WindowState.FullScreen;
            }
        }
    }
}
