using AES_Core.DI;
using AES_Core.Interfaces;
using AES_Core.Services;
using AES_Lacrima.ViewModels;
using AES_Lacrima.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using log4net;
using System;
using System.Linq;

namespace AES_Lacrima
{
    /// <summary>
    /// Application entry point for the Avalonia UI. Responsible for
    /// configuring dependency injection, creating the main window and
    /// performing application-level initialization tasks.
    /// </summary>
    public partial class App : Application
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(App));
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
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();
                //Initialize DI Locator
                DiLocator.ConfigureContainer(builder =>
                {
                    // Custom registrations can be added here if needed
                });
                // Create the main window and set its DataContext to the resolved MainWindowViewModel
                desktop.MainWindow = new MainWindow
                {
                    DataContext = DiLocator.ResolveViewModel<MainWindowViewModel>()
                };
                // Attach closing handler to perform cleanup/save on exit
                desktop.MainWindow.Closing += MainWindow_Closing;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        /// <summary>
        /// Handler invoked when the main window is closing. Attempts to save
        /// application settings via the <see cref="ISettingsService"/> and
        /// disposes the DI scope to ensure graceful shutdown of services.
        /// </summary>
        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            try
            {
                // Try to resolve the settings service and save settings if present.
                DiLocator.ResolveViewModel<SettingsService>()?.SaveSettings();
                Logger.Info("Settings saved successfully during shutdown");
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving settings during shutdown", ex);
            }
            finally
            {
                // Dispose DI scope to release resources
                try
                {
                    DiLocator.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error("Error disposing DI locator during shutdown", ex);
                }
            }
        }
    }
}