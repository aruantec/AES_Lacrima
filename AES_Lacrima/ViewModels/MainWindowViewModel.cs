using AES_Core.DI;
using CommunityToolkit.Mvvm.ComponentModel;
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
        /// <summary>
        /// Read-only greeting text shown in the UI.
        /// </summary>
        public string Greeting { get; } = "Welcome to AES Lacrima!";

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
        private object? _view;

        /// <summary>
        /// Prepare the view-model for use. This implementation loads
        /// persisted settings such as window size so the UI can be
        /// restored to the previous state.
        /// </summary>
        public override void Prepare()
        {
            LoadSettings();
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
        /// width and height so they can be restored on next startup.
        /// </summary>
        /// <param name="section">JSON object to populate with this view-model's settings.</param>
        protected override void OnSaveSettings(JsonObject section)
        {
            WriteSetting(section, "WindowWidth", WindowWidth);
            WriteSetting(section, "WindowHeight", WindowHeight);
        }
    }
}
