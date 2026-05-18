using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels.SectionHandlers
{
    /// <summary>
    /// Base implementation for emulator section handlers.
    /// Provides common property management and state tracking.
    /// </summary>
    public abstract class EmulationSectionHandlerBase : ObservableObject, IEmulationSectionHandler
    {
        private string _status = "Select a section to manage updates.";
        private bool _isUpdateAvailable;
        private bool _isBusy;
        private bool _isDownloading;
        private double _downloadProgress;
        private string? _emulatorPath;
        private string? _updatePath;
        private string? _currentVersion;
        private string? _latestVersion;
        private string? _selectedVersion;
        private AvaloniaList<string> _availableVersions = [];

        public abstract string EmulatorName { get; }

        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetProperty(ref _isUpdateAvailable, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetProperty(ref _isBusy, value);
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            private set => SetProperty(ref _isDownloading, value);
        }

        public double DownloadProgress
        {
            get => _downloadProgress;
            private set => SetProperty(ref _downloadProgress, value);
        }

        public string? EmulatorPath
        {
            get => _emulatorPath;
            private set => SetProperty(ref _emulatorPath, value);
        }

        public string? UpdatePath
        {
            get => _updatePath;
            private set => SetProperty(ref _updatePath, value);
        }

        public string? CurrentVersion
        {
            get => _currentVersion;
            private set => SetProperty(ref _currentVersion, value);
        }

        public string? LatestVersion
        {
            get => _latestVersion;
            private set => SetProperty(ref _latestVersion, value);
        }

        public string? SelectedVersion
        {
            get => _selectedVersion;
            private set => SetProperty(ref _selectedVersion, value);
        }

        public AvaloniaList<string> AvailableVersions
        {
            get => _availableVersions;
            private set => SetProperty(ref _availableVersions, value);
        }

        public void SetStatus(string message) => Status = message;
        public void SetUpdateAvailable(bool value) => IsUpdateAvailable = value;
        public void SetBusy(bool value) => IsBusy = value;
        public void SetDownloading(bool value) => IsDownloading = value;
        public void SetDownloadProgress(double value) => DownloadProgress = value;
        public void SetEmulatorPath(string? path) => EmulatorPath = path;
        public void SetUpdatePath(string? path) => UpdatePath = path;
        public void SetCurrentVersion(string? version) => CurrentVersion = version;
        public void SetLatestVersion(string? version) => LatestVersion = version;
        public void SetSelectedVersion(string? version) => SelectedVersion = version;

        public abstract Task RefreshInfoAsync();
        public abstract Task DownloadOrUpdateAsync();
    }
}
