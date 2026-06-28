using AES_Controls.Helpers;
using AES_Controls.Player.Models;
using AES_Emulation.Services;
using AES_Lacrima.Services.Emulation;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    private const int MaxGameplayPreviewRecordingDurationSeconds = 300;

    private bool _isGameplayPreviewRecordingSession;
    private string? _previewRecordingRomPath;
    private string? _previewRecordingTempDirectory;
    private DispatcherTimer? _previewRecordingCountdownTimer;
    private DateTime _previewRecordingStartedUtc;
    private int _previewRecordingDurationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowGameplayRecording))]
    [NotifyPropertyChangedFor(nameof(CanToggleGameplayRecording))]
    [NotifyPropertyChangedFor(nameof(CanRecordGameplayPreview))]
    [NotifyPropertyChangedFor(nameof(CanStopGameplayPreviewRecording))]
    [NotifyPropertyChangedFor(nameof(GameplayPreviewRecordingMenuHeader))]
    [NotifyPropertyChangedFor(nameof(CanClearGameplayPreview))]
    [NotifyPropertyChangedFor(nameof(ShowClearGameplayPreviewMenuItem))]
    private bool _isGameplayPreviewRecording;

    [ObservableProperty]
    private string? _gameplayPreviewRecordingStatus;

    public bool CanRecordGameplayPreview =>
        IsEmulatorRunning &&
        IsCompositionCaptureVisible &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
        HighlightedItem != null &&
        !string.IsNullOrWhiteSpace(HighlightedItem.FileName) &&
        !IsEmulatorLaunchInProgress &&
        !IsGameplayRecording &&
        !IsGameplayPreviewRecording;

    public bool CanStopGameplayPreviewRecording => IsGameplayPreviewRecording;

    public bool CanClearGameplayPreview =>
        HighlightedItem != null &&
        !string.IsNullOrWhiteSpace(HighlightedItem.FileName) &&
        EmulationPreviewCacheHelper.HasPreview(HighlightedItem.FileName) &&
        !IsGameplayPreviewRecording &&
        !_isGameplayPreviewRecordingSession;

    public bool ShowClearGameplayPreviewMenuItem =>
        PointedIndex >= 0 &&
        PointedIndex < CoverItems.Count &&
        CanClearGameplayPreviewForItem(PointedIndex);

    public bool CanClearGameplayPreviewForItem(int index) =>
        index >= 0 &&
        index < CoverItems.Count &&
        !string.IsNullOrWhiteSpace(CoverItems[index].FileName) &&
        EmulationPreviewCacheHelper.HasPreview(CoverItems[index].FileName) &&
        !IsGameplayPreviewRecording &&
        !_isGameplayPreviewRecordingSession;

    public string GameplayPreviewRecordingMenuHeader =>
        IsGameplayPreviewRecording
            ? "Stop Gameplay Preview Recording"
            : "Record Gameplay Preview";

    internal void NotifyGameplayPreviewRecordingAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanRecordGameplayPreview));
        OnPropertyChanged(nameof(CanStopGameplayPreviewRecording));
        OnPropertyChanged(nameof(CanClearGameplayPreview));
        OnPropertyChanged(nameof(ShowClearGameplayPreviewMenuItem));
        OnPropertyChanged(nameof(CanShowGameplayRecording));
        OnPropertyChanged(nameof(CanToggleGameplayRecording));
        OnPropertyChanged(nameof(GameplayPreviewRecordingMenuHeader));
        RecordGameplayPreviewCommand.NotifyCanExecuteChanged();
        StopGameplayPreviewRecordingMenuCommand.NotifyCanExecuteChanged();
        ClearGameplayPreviewCommand.NotifyCanExecuteChanged();
        ClearGameplayPreviewForItemCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRecordGameplayPreview))]
    private void RecordGameplayPreview()
    {
        if (!CanRecordGameplayPreview)
        {
            GameplayPreviewRecordingStatus = ResolveGameplayPreviewRecordingUnavailableReason();
            return;
        }

        StartGameplayPreviewRecording();
    }

    [RelayCommand(CanExecute = nameof(CanStopGameplayPreviewRecording))]
    private void StopGameplayPreviewRecordingMenu() => StopGameplayPreviewRecording();

    [RelayCommand(CanExecute = nameof(CanClearGameplayPreview))]
    private void ClearGameplayPreview() => ClearGameplayPreviewCore(HighlightedItem);

    [RelayCommand(CanExecute = nameof(CanClearGameplayPreviewForItem))]
    private void ClearGameplayPreviewForItem(int index)
    {
        if (index < 0 || index >= CoverItems.Count)
            return;

        ClearGameplayPreviewCore(CoverItems[index]);
    }

    private void ClearGameplayPreviewCore(MediaItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FileName))
            return;

        if (!EmulationPreviewCacheHelper.TryDeletePreviewSidecar(item.FileName))
        {
            GameplayPreviewRecordingStatus = "Could not clear the local gameplay preview.";
            return;
        }

        GameplayPreviewRecordingStatus = "Local gameplay preview cleared.";
        if (_isGameplayPreviewActive &&
            string.Equals(_activeGameplayPreviewItemPath, item.FileName, StringComparison.OrdinalIgnoreCase))
        {
            StopGameplayPreview();
        }

        if (IsGameplayPreviewAvailable &&
            HighlightedItem != null &&
            string.Equals(HighlightedItem.FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
        {
            QueueGameplayPreview(item, immediate: true);
        }

        NotifyGameplayPreviewRecordingAvailabilityChanged();
    }

    internal void StopGameplayPreviewRecording()
    {
        if (!_isGameplayPreviewRecordingSession && !IsGameplayPreviewRecording)
            return;

        StopPreviewRecordingCountdown();
        ResolveGameplayRecorder()?.Stop();
    }

    private void StartGameplayPreviewRecording()
    {
        var settings = SettingsViewModel;
        var item = HighlightedItem;
        if (settings == null || item == null || string.IsNullOrWhiteSpace(item.FileName))
        {
            GameplayPreviewRecordingStatus = "Recording services are not available.";
            return;
        }

        if (FFmpegLocator.FindFFmpegPath() == null)
        {
            GameplayPreviewRecordingStatus = "FFmpeg was not found. Install it from Settings → Components.";
            return;
        }

        _previewRecordingRomPath = item.FileName;
        _previewRecordingTempDirectory = Path.Combine(
            Path.GetTempPath(),
            "AES_Lacrima_preview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_previewRecordingTempDirectory);
        _previewRecordingDurationSeconds = MaxGameplayPreviewRecordingDurationSeconds;
        IsGameplayPreviewRecording = true;

        if (!TryBeginGameplayRecordingSession(_previewRecordingTempDirectory, settings, previewSession: true))
        {
            IsGameplayPreviewRecording = false;
            return;
        }

        StartPreviewRecordingCountdown();
        NotifyGameplayPreviewRecordingAvailabilityChanged();
        NotifyGameplayRecordingAvailabilityChanged();
    }

    private void StartPreviewRecordingCountdown()
    {
        StopPreviewRecordingCountdown();
        _previewRecordingStartedUtc = DateTime.UtcNow;
        UpdatePreviewRecordingCountdownStatus();

        _previewRecordingCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _previewRecordingCountdownTimer.Tick += OnPreviewRecordingCountdownTick;
        _previewRecordingCountdownTimer.Start();
    }

    private void OnPreviewRecordingCountdownTick(object? sender, EventArgs e)
    {
        var elapsedSeconds = (int)(DateTime.UtcNow - _previewRecordingStartedUtc).TotalSeconds;
        if (elapsedSeconds >= _previewRecordingDurationSeconds)
        {
            GameplayPreviewRecordingStatus = "Finalizing gameplay preview…";
            StopPreviewRecordingCountdown();
            ResolveGameplayRecorder()?.Stop();
            return;
        }

        UpdatePreviewRecordingCountdownStatus(_previewRecordingDurationSeconds - elapsedSeconds);
    }

    private void UpdatePreviewRecordingCountdownStatus(int? remainingSeconds = null)
    {
        remainingSeconds ??= _previewRecordingDurationSeconds;
        GameplayPreviewRecordingStatus = $"Recording gameplay preview… {Math.Max(0, remainingSeconds.Value)}s left (max {MaxGameplayPreviewRecordingDurationSeconds}s)";
    }

    private void StopPreviewRecordingCountdown()
    {
        if (_previewRecordingCountdownTimer == null)
            return;

        _previewRecordingCountdownTimer.Stop();
        _previewRecordingCountdownTimer.Tick -= OnPreviewRecordingCountdownTick;
        _previewRecordingCountdownTimer = null;
    }

    private void HandleGameplayPreviewRecordingStateChanged(bool isRecording)
    {
        if (isRecording)
        {
            IsGameplayRecording = false;
            StopRecordingTimers();
            ConfigureCaptureGameplayRecording(_activeCaptureHost);
            return;
        }

        StopPreviewRecordingCountdown();
        IsGameplayPreviewRecording = false;
        IsGameplayRecording = false;
        StopRecordingTimers();

        var recorder = ResolveGameplayRecorder();
        var expectedOutputPath = recorder?.ActiveOutputPath;
        var romPath = _previewRecordingRomPath;
        var tempDirectory = _previewRecordingTempDirectory;

        _isGameplayPreviewRecordingSession = false;
        _previewRecordingRomPath = null;
        _previewRecordingTempDirectory = null;

        recorder?.RecordingStateChanged -= OnGameplayRecordingStateChanged;
        recorder?.RecordingFailed -= OnGameplayRecordingFailed;

        ConfigureCaptureGameplayRecording(_activeCaptureHost);
        SettingsViewModel?.ResumeGameplayRecordingAudioLevelMonitor();
        NotifyGameplayPreviewRecordingAvailabilityChanged();
        NotifyGameplayRecordingAvailabilityChanged();

        GameplayPreviewRecordingStatus = "Finalizing gameplay preview…";

        _ = Task.Run(() =>
        {
            recorder?.WaitForPendingFinalize();
            var recordedPath = ResolvePreviewRecordingOutputPath(expectedOutputPath, tempDirectory);
            var success = !string.IsNullOrWhiteSpace(recordedPath) &&
                          !string.IsNullOrWhiteSpace(romPath) &&
                          EmulationPreviewCacheHelper.TryCommitPreviewFile(romPath, recordedPath);

            if (!string.IsNullOrWhiteSpace(recordedPath))
                TryDeleteFile(recordedPath);
            TryDeleteDirectory(tempDirectory);

            Dispatcher.UIThread.Post(() =>
            {
                GameplayPreviewRecordingStatus = success
                    ? "Gameplay preview saved."
                    : "Gameplay preview recording failed to save.";
                if (success)
                    RefreshGameplayPreviewForCurrentSelection(immediate: true);
            });
        });
    }

    private void HandleGameplayPreviewRecordingFailed(string message)
    {
        StopPreviewRecordingCountdown();
        IsGameplayPreviewRecording = false;
        IsGameplayRecording = false;
        StopRecordingTimers();
        GameplayPreviewRecordingStatus = message;

        var tempDirectory = _previewRecordingTempDirectory;
        var expectedOutputPath = ResolveGameplayRecorder()?.ActiveOutputPath;

        _isGameplayPreviewRecordingSession = false;
        _previewRecordingRomPath = null;
        _previewRecordingTempDirectory = null;

        var recorder = ResolveGameplayRecorder();
        recorder?.RecordingStateChanged -= OnGameplayRecordingStateChanged;
        recorder?.RecordingFailed -= OnGameplayRecordingFailed;

        ConfigureCaptureGameplayRecording(_activeCaptureHost);
        SettingsViewModel?.ResumeGameplayRecordingAudioLevelMonitor();
        NotifyGameplayPreviewRecordingAvailabilityChanged();
        NotifyGameplayRecordingAvailabilityChanged();

        _ = Task.Run(() =>
        {
            recorder?.WaitForPendingFinalize();
            var recordedPath = ResolvePreviewRecordingOutputPath(expectedOutputPath, tempDirectory);
            if (!string.IsNullOrWhiteSpace(recordedPath))
                TryDeleteFile(recordedPath);
            TryDeleteDirectory(tempDirectory);
        });
    }

    private static string? ResolvePreviewRecordingOutputPath(string? activeOutputPath, string? tempDirectory)
    {
        if (!string.IsNullOrWhiteSpace(activeOutputPath) &&
            File.Exists(activeOutputPath) &&
            IsPreviewRecordingMainOutput(activeOutputPath))
        {
            return activeOutputPath;
        }

        if (string.IsNullOrWhiteSpace(tempDirectory) || !Directory.Exists(tempDirectory))
            return null;

        return Directory.EnumerateFiles(tempDirectory, "AES_Recording_*")
            .Where(IsPreviewRecordingMainOutput)
            .Where(path => new FileInfo(path).Length > 0)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool IsPreviewRecordingMainOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fileName = Path.GetFileName(path);
        if (fileName.Contains(".audio.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".video.", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".optimized.mp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupGameplayPreviewRecordingSession(string message)
    {
        StopPreviewRecordingCountdown();
        _isGameplayPreviewRecordingSession = false;
        IsGameplayPreviewRecording = false;
        IsGameplayRecording = false;
        StopRecordingTimers();
        GameplayPreviewRecordingStatus = message;

        var recorder = ResolveGameplayRecorder();
        recorder?.RecordingStateChanged -= OnGameplayRecordingStateChanged;
        recorder?.RecordingFailed -= OnGameplayRecordingFailed;

        var tempDirectory = _previewRecordingTempDirectory;
        _previewRecordingRomPath = null;
        _previewRecordingTempDirectory = null;

        ConfigureCaptureGameplayRecording(_activeCaptureHost);
        SettingsViewModel?.ResumeGameplayRecordingAudioLevelMonitor();
        NotifyGameplayPreviewRecordingAvailabilityChanged();
        NotifyGameplayRecordingAvailabilityChanged();

        _ = Task.Run(() => TryDeleteDirectory(tempDirectory));
    }

    private string ResolveGameplayPreviewRecordingUnavailableReason()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return "Gameplay preview recording is not supported on this platform.";

        if (!IsEmulatorRunning)
            return "Start a game before recording a preview.";

        if (!IsCompositionCaptureVisible)
            return "Show the emulator viewport to record a preview.";

        if (HighlightedItem == null || string.IsNullOrWhiteSpace(HighlightedItem.FileName))
            return "Select a game to record a preview for.";

        if (IsGameplayRecording)
            return "Stop the full gameplay recording first.";

        if (IsGameplayPreviewRecording)
            return "A gameplay preview is already being recorded.";

        if (IsEmulatorLaunchInProgress)
            return "Wait for the emulator launch to finish.";

        return "Gameplay preview recording is not available right now.";
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
