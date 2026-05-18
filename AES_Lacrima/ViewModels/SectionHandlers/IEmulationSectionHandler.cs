using Avalonia.Collections;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Interface for emulator section-specific handlers.
    /// Abstracts away repetitive logic for different emulators/sections.
    /// </summary>
    public interface IEmulationSectionHandler : INotifyPropertyChanged
    {
        /// <summary>Gets the emulator name (RetroArch, Cemu, Dolphin, etc.)</summary>
        string EmulatorName { get; }

        /// <summary>Gets the status message</summary>
        string Status { get; }
        void SetStatus(string message);

        /// <summary>Gets whether an update is available</summary>
        bool IsUpdateAvailable { get; }
        void SetUpdateAvailable(bool value);

        /// <summary>Gets whether the handler is busy</summary>
        bool IsBusy { get; }
        void SetBusy(bool value);

        /// <summary>Gets whether download is in progress</summary>
        bool IsDownloading { get; }
        void SetDownloading(bool value);

        /// <summary>Gets the download progress (0.0 - 1.0)</summary>
        double DownloadProgress { get; }
        void SetDownloadProgress(double value);

        /// <summary>Gets the emulator path</summary>
        string? EmulatorPath { get; }
        void SetEmulatorPath(string? path);

        /// <summary>Gets the update path</summary>
        string? UpdatePath { get; }
        void SetUpdatePath(string? path);

        /// <summary>Gets the current version</summary>
        string? CurrentVersion { get; }
        void SetCurrentVersion(string? version);

        /// <summary>Gets the latest available version</summary>
        string? LatestVersion { get; }
        void SetLatestVersion(string? version);

        /// <summary>Gets available versions</summary>
        AvaloniaList<string> AvailableVersions { get; }

        /// <summary>Gets the selected version</summary>
        string? SelectedVersion { get; }
        void SetSelectedVersion(string? version);

        /// <summary>Refresh info (versions, paths, etc.)</summary>
        Task RefreshInfoAsync();

        /// <summary>Download or update the emulator</summary>
        Task DownloadOrUpdateAsync();
    }
}
