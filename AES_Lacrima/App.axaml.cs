using AES_Controls.Helpers;
using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Core.Services;
using AES_Lacrima.Mini.Views;
using AES_Lacrima.Mini.ViewModels;
using AES_Lacrima.ViewModels;
using AES_Lacrima.Views;
using AES_Lacrima.Views.Mobile;
using AES_Lacrima.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using log4net;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace AES_Lacrima
{
    /// <summary>
    /// Application entry point for the Avalonia UI. Responsible for
    /// configuring dependency injection, creating the main window and
    /// performing application-level initialization tasks.
    /// </summary>
    public class App : Application
    {
        // when true the main window is being replaced as part of a mode switch.
        // the closing handler should avoid disposing the DI locator during this
        // transition because the application is still running and services are
        // needed by the newly‑created window.
        public static bool IsSwitchingMode { get; set; }
        public static bool IsSelfUpdating { get; set; }
        private WindowsGlobalMediaKeyHook? _globalMediaKeyHook;
        private DispatcherTimer? _startupUiProbeTimer;
        private Stopwatch? _startupUiProbeStopwatch;
        private long _startupUiProbeLastTickMs;
        private int _startupUiProbeWarnings;

        private static readonly ILog Logger = AES_Core.Logging.LogHelper.For<App>();
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Called when the Avalonia framework has finished initialization.
        /// This method configures the DI container, disables DataAnnotations
        /// validation (to avoid Avalonia's default behavior) and creates the
        /// main application window. It also attaches a closing handler to the
        /// main window to allow application-level cleanup such as saving
        /// settings and disposing the DI scope.
        /// </summary>
        public override async void OnFrameworkInitializationCompleted()
        {
            var frameworkInitSw = Stopwatch.StartNew();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Logger.Info("Desktop framework initialization started.");
                StartStartupUiProbe();

                //Initialize DI Locator
                var diSw = Stopwatch.StartNew();
                DiLocator.ConfigureContainer(builder =>
                {
                    //Register audio player for fresh instances
                    //builder.RegisterType<AudioPlayer>().As<AudioPlayer>().InstancePerDependency();
                });
                Logger.Info($"DI container configured in {diSw.ElapsedMilliseconds} ms.");

                // Create the main window.  The user can choose between the stock
                // AES or a Mini design via the settings.  
                // We need to resolve and prepare the SettingsViewModel here so the persisted
                // value is available before we construct the window.
                var settingsResolveSw = Stopwatch.StartNew();
                var settingsVm = DiLocator.ResolveViewModel<SettingsViewModel>();
                Logger.Info($"SettingsViewModel resolved in {settingsResolveSw.ElapsedMilliseconds} ms.");
                if (settingsVm != null)
                {
                    var settingsPrepareStopwatch = Stopwatch.StartNew();
                    settingsVm.Prepare();
                    var prepareMs = settingsPrepareStopwatch.ElapsedMilliseconds;
                    if (prepareMs >= 500)
                        Logger.Warn($"SettingsViewModel.Prepare was slow on UI thread: {prepareMs} ms.");
                    else
                        Logger.Info($"SettingsViewModel.Prepare completed in {prepareMs} ms.");
                }

                var windowCreateSw = Stopwatch.StartNew();
                if (settingsVm != null && settingsVm.AppMode == 1)
                    desktop.MainWindow = new CustomWindow();
                else
                    desktop.MainWindow = new MainWindow();
                Logger.Info($"Main window created in {windowCreateSw.ElapsedMilliseconds} ms. type={desktop.MainWindow.GetType().Name}");

                TryInitializeGlobalMediaKeys();

                // Attach closing handler to perform cleanup/save on exit
                desktop.MainWindow.Closing += MainWindow_Closing;

                // Finish heavier startup tasks after the window is already available
                // so release builds don't appear frozen before first render.
                _ = PerformPostStartupInitializationAsync(desktop.MainWindow);

                Logger.Info($"Desktop framework initialization finished in {frameworkInitSw.ElapsedMilliseconds} ms.");
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                DiLocator.ConfigureContainer();

                var settingsVm = DiLocator.ResolveViewModel<SettingsViewModel>();
                settingsVm?.Prepare();

                var mainVm = DiLocator.ResolveViewModel<MainWindowViewModel>();
                if (mainVm is { IsPrepared: false })
                {
                    mainVm.Prepare();
                    mainVm.IsPrepared = true;
                }

                singleView.MainView = new MobileMainView();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void StartStartupUiProbe()
        {
            if (_startupUiProbeTimer != null)
                return;

            _startupUiProbeStopwatch = Stopwatch.StartNew();
            _startupUiProbeLastTickMs = 0;
            _startupUiProbeWarnings = 0;

            _startupUiProbeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            _startupUiProbeTimer.Tick += (_, _) =>
            {
                if (_startupUiProbeStopwatch == null)
                    return;

                var nowMs = _startupUiProbeStopwatch.ElapsedMilliseconds;
                if (_startupUiProbeLastTickMs > 0)
                {
                    var delta = nowMs - _startupUiProbeLastTickMs;
                    if (delta > 1200 && _startupUiProbeWarnings < 8)
                    {
                        _startupUiProbeWarnings++;
                        Logger.Warn($"UI thread stall detected during startup. tickGap={delta} ms, uptime={nowMs} ms.");
                    }
                }

                _startupUiProbeLastTickMs = nowMs;

                // Keep probe only for early startup.
                if (nowMs > 30000)
                    StopStartupUiProbe("timeout");
            };

            _startupUiProbeTimer.Start();
            Logger.Info("Startup UI probe started.");
        }

        private void StopStartupUiProbe(string reason)
        {
            if (_startupUiProbeTimer == null)
                return;

            _startupUiProbeTimer.Stop();
            _startupUiProbeTimer = null;

            var elapsed = _startupUiProbeStopwatch?.ElapsedMilliseconds ?? 0;
            _startupUiProbeStopwatch = null;
            Logger.Info($"Startup UI probe stopped. reason={reason}, elapsed={elapsed} ms, warnings={_startupUiProbeWarnings}.");
        }

        private void TryInitializeGlobalMediaKeys()
        {
            if (!OperatingSystem.IsWindows())
                return;

            if (_globalMediaKeyHook != null)
                return;

            try
            {
                _globalMediaKeyHook = new WindowsGlobalMediaKeyHook();
                _globalMediaKeyHook.MediaKeyPressed += OnGlobalMediaKeyPressed;
                _globalMediaKeyHook.Start();
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to initialize global media key hook", ex);
                _globalMediaKeyHook = null;
            }
        }

        private void OnGlobalMediaKeyPressed(GlobalMediaKey key)
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow == null)
                return;

            // When the app is focused, let the window-level KeyDown handlers process media keys.
            // The global hook is primarily for unfocused/background control.
            if (desktop.MainWindow.IsActive)
                return;

            if (desktop.MainWindow.DataContext is MainWindowViewModel mainVm)
            {
                var music = mainVm.MusicViewModel;
                if (music == null)
                    return;

                switch (key)
                {
                    case GlobalMediaKey.Next:
                        if (music.PlayNextCommand.CanExecute(null))
                            music.PlayNextCommand.Execute(null);
                        break;
                    case GlobalMediaKey.Previous:
                        if (music.PlayPreviousCommand.CanExecute(null))
                            music.PlayPreviousCommand.Execute(null);
                        break;
                    case GlobalMediaKey.PlayPause:
                        if (music.TogglePlayCommand.CanExecute(null))
                            music.TogglePlayCommand.Execute(null);
                        break;
                }
                return;
            }

            if (desktop.MainWindow.DataContext is MinViewModel minVm)
            {
                switch (key)
                {
                    case GlobalMediaKey.Next:
                        if (minVm.NextCommand.CanExecute(null))
                            minVm.NextCommand.Execute(null);
                        break;
                    case GlobalMediaKey.Previous:
                        if (minVm.PreviousCommand.CanExecute(null))
                            minVm.PreviousCommand.Execute(null);
                        break;
                    case GlobalMediaKey.PlayPause:
                        if (minVm.PlayPauseCommand.CanExecute(null))
                            minVm.PlayPauseCommand.Execute(null);
                        break;
                }
            }
        }

        private async Task PerformPostStartupInitializationAsync(Window mainWindow)
        {
            // Let the window reach its first frame before any post-startup work runs.
            await Task.Yield();
            await Task.Delay(150);

            var startupStopwatch = Stopwatch.StartNew();

            try
            {
                var resourceSyncStopwatch = Stopwatch.StartNew();
                await Task.Run(Program.EnsureBundledResources);
                Logger.Info($"Bundled resource sync completed in {resourceSyncStopwatch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to synchronize bundled startup resources", ex);
            }

            try
            {
                // Process pending mpv updates or uninstalls WITHOUT starting automatic setup/download.
                var mpvValidationStopwatch = Stopwatch.StartNew();
                await Task.Run(() => MpvSetup.EnsureInstalled(autoInstall: false));
                Logger.Info($"mpv startup validation completed in {mpvValidationStopwatch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed during mpv startup validation", ex);
            }

            try
            {
                // Configure FFmpeg, libmpv and yt-dlp checks. Skip auto-installation on startup.
                // Run initial checks in background so app can render immediately.
                var toolChecksStopwatch = Stopwatch.StartNew();
                await PerformInitialToolChecksAsync(mainWindow);
                Logger.Info($"Initial tool checks completed in {toolChecksStopwatch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed during post-startup tool checks", ex);
            }

            Logger.Info($"Post-startup initialization completed in {startupStopwatch.ElapsedMilliseconds} ms.");
            StopStartupUiProbe("post-startup-complete");
        }

        private static async Task PerformInitialToolChecksAsync(Window mainWindow)
        {
            // Give the main window a moment to finish launching and the view model to be fully ready
            await Task.Delay(500);

            var settingsViewModel = DiLocator.ResolveViewModel<SettingsViewModel>();
            if (settingsViewModel == null)
                return;

            // Refresh current state for users to see accurate info in settings
            if (FFmpegLocator.FindFFmpegPath() is { } ffmpegPath)
            {
                settingsViewModel.FfmpegPath = ffmpegPath;
            }

            // Background checks for versions
            _ = settingsViewModel.RefreshFFmpegInfo();
            _ = settingsViewModel.RefreshMpvInfo();
            _ = settingsViewModel.RefreshYtDlpInfo();

            if (mainWindow.DataContext is MainWindowViewModel mainViewModel)
            {
                // Perform missing tool check. mpv and ffmpeg are critical.
                bool ffmpegMissing = !FFmpegLocator.IsFFmpegAvailable();
                bool mpvMissing = !(settingsViewModel.MpvManager?.IsLibraryInstalled() ?? false);

                if (ffmpegMissing || mpvMissing)
                {
                    mainViewModel.ShowSetupPrompt();
                }
            }

#if !DEBUG
            if (settingsViewModel.CheckForAppUpdatesOnStartup && settingsViewModel.AppUpdateService != null)
            {
                var release = await settingsViewModel.AppUpdateService.CheckForUpdatesAsync();
                if (release != null)
                {
                    if (mainWindow.DataContext is MainWindowViewModel desktopMainViewModel)
                    {
                        desktopMainViewModel.ShowAppUpdatePrompt(release);
                    }
                    else if (mainWindow.DataContext is MinViewModel minViewModel)
                    {
                        settingsViewModel.MiniSettingsSelectedTab = 2;
                        minViewModel.SettingsVisible = true;
                    }
                }
            }
#endif
        }

        /// <summary>
        /// Handler invoked when the main window is closing. Attempts to save
        /// application settings via the <see cref="ISettingsService"/> and
        /// disposes the DI scope to ensure graceful shutdown of services.
        /// </summary>
        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            if (IsSelfUpdating)
            {
                Logger.Info("Skipping normal shutdown cleanup because a self-update restart is in progress");
                return;
            }

            try
            {
                // Try to resolve the settings service and save settings if present.
                DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();
                Logger.Info("Settings saved successfully during shutdown");

                // Dispose platform integrations (MPRIS, etc.) before the DI graph is torn down.
                DiLocator.ResolveViewModel<MusicViewModel>()?.ShutdownPlatformIntegrations();
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving settings during shutdown", ex);
            }
            finally
            {
                // Dispose DI scope to release resources.  When a mode switch is
                // underway we do **not** tear down the DI container because a new
                // window will be created immediately after the old one closes.
                if (!IsSwitchingMode)
                {
                    StopStartupUiProbe("shutdown");
                    _globalMediaKeyHook?.Dispose();
                    _globalMediaKeyHook = null;

                    try
                    {
                        DiLocator.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error disposing DI locator during shutdown", ex);
                    }
                }
                else
                {
                    Logger.Info("Skipping DI disposal due to mode switch");
                }
            }
        }
    }
}
