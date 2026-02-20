using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;

namespace AES_Lacrima.ViewModels.Prompts
{
    /// <summary>
    /// View model for the restart prompt displayed when libmpv updates/installs/uninstalls require a restart.
    /// </summary>
    public partial class RestartPromptViewModel : ViewModelBase
    {
        /// <summary>
        /// Occurs when the prompt should be closed.
        /// </summary>
        public event Action? RequestClose;

        /// <summary>
        /// Gets the message to be displayed in the prompt.
        /// </summary>
        public string Message => "The application needs to restart to apply the library changes. \n\nSkipping will require a manual restart, and some functionality may be restricted or unavailable until the restart is complete.";

        /// <summary>
        /// Restarts the application immediately.
        /// </summary>
        [RelayCommand]
        private void Restart()
        {
            // Start a new instance of the current process and exit the current one.
            Process.Start(Environment.ProcessPath!);
            Environment.Exit(0);
        }

        /// <summary>
        /// Closes the prompt without restarting.
        /// </summary>
        [RelayCommand]
        private void Skip()
        {
            RequestClose?.Invoke();
        }
    }
}
