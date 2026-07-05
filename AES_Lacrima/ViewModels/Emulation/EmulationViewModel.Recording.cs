using AES_Core.DI;
using AES_Emulation.Controls;
using AES_Emulation.Linux;
using AES_Emulation.Platform;
using AES_Emulation.Services;
using AES_Lacrima.Services.Emulation;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace AES_Lacrima.ViewModels;

public partial class EmulationViewModel
{
    [AutoResolve]
    private GameplayRecorderService? _gameplayRecorder;

    [AutoResolve]
    private LinuxGameplayRecorderService? _linuxGameplayRecorder;

    [ObservableProperty]
    private bool _isGameplayRecording;

    [ObservableProperty]
    private string? _gameplayRecordingStatus;

    [ObservableProperty]
    private string _gameplayRecordingElapsedText = "00:00";

    [ObservableProperty]
    private double _gameplayRecordingCenterOpacity = 1.0;

    public bool CanShowGameplayRecording =>
        IsEmulatorRunning &&
        IsCompositionCaptureVisible &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
        !IsGameplayPreviewRecording;

    public bool CanToggleGameplayRecording => CanShowGameplayRecording && !IsEmulatorLaunchInProgress && !IsGameplayPreviewRecording;

    private DispatcherTimer? _recordingElapsedTimer;
    private DispatcherTimer? _recordingPulseTimer;
    private DateTime _recordingStartedUtc;

    internal void NotifyGameplayRecordingAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanShowGameplayRecording));
        OnPropertyChanged(nameof(CanToggleGameplayRecording));
        NotifyGameplayPreviewRecordingAvailabilityChanged();
    }

    public double CaptureChromeRightInset => 0;

    public Thickness CaptureChromeRightMargin =>
        CaptureChromeRightInset > 0 ? new Thickness(0, 0, CaptureChromeRightInset, 0) : default;

    internal void NotifyCaptureChromeMarginChanged()
    {
        OnPropertyChanged(nameof(CaptureChromeRightInset));
        OnPropertyChanged(nameof(CaptureChromeRightMargin));
    }

    [RelayCommand]
    private void ToggleGameplayRecording()
    {
        if (!CanToggleGameplayRecording)
        {
            GameplayRecordingStatus = ResolveRecordingUnavailableReason();
            return;
        }

        if (IsGameplayRecording)
            StopGameplayRecording();
        else
            StartGameplayRecording();
    }

    internal void ConfigureCaptureGameplayRecording(EmulatorCaptureHost? captureHost)
    {
        var recorder = ResolveGameplayRecorder();
        if (recorder == null || captureHost == null)
            return;

        if (!recorder.IsRecording)
        {
            captureHost.ConfigureGameplayRecording(null, SettingsViewModel?.GameplayRecordingFps ?? 60, GameplayRecordingResolutionCap.Native);
            return;
        }

        var fps = SettingsViewModel?.GameplayRecordingFps ?? 60;
        var resolutionCap = SettingsViewModel?.GameplayRecordingResolutionCap ?? GameplayRecordingResolutionCap.P1080;
        captureHost.ConfigureGameplayRecording(recorder.OnFrameFromCapture, fps, resolutionCap);
    }

    private void StartGameplayRecording()
    {
        var settings = SettingsViewModel ?? DiLocator.ResolveViewModel<SettingsViewModel>();
        if (settings == null)
        {
            GameplayRecordingStatus = "Recording services are not available.";
            return;
        }

        var outputDir = settings.GameplayRecordingOutputDirectory;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = GameplayRecorderService.GetDefaultOutputDirectory();

        if (!TryBeginGameplayRecordingSession(outputDir, settings, previewSession: false))
            return;

        var audioHint = settings.GameplayRecordingAudioSource switch
        {
            GameplayRecordingAudioSource.None => "Recording video only.",
            GameplayRecordingAudioSource.OutputDevice => "Recording with output device audio.",
            GameplayRecordingAudioSource.Application => "Recording with selected application audio.",
            _ => "Recording with emulator audio."
        };
        GameplayRecordingStatus = $"Waiting for frames… {audioHint}";
        NotifyGameplayRecordingAvailabilityChanged();

        Dispatcher.UIThread.Post(settings.SaveSettings, DispatcherPriority.Background);
    }

    private bool TryBeginGameplayRecordingSession(
        string outputDirectory,
        SettingsViewModel settings,
        bool previewSession)
    {
        var recorder = ResolveGameplayRecorder();
        if (recorder == null)
        {
            if (previewSession)
                GameplayPreviewRecordingStatus = "Recording services are not available.";
            else
                GameplayRecordingStatus = "Recording services are not available.";
            return false;
        }

        recorder.RecordingStateChanged -= OnGameplayRecordingStateChanged;
        recorder.RecordingFailed -= OnGameplayRecordingFailed;
        recorder.RecordingStateChanged += OnGameplayRecordingStateChanged;
        recorder.RecordingFailed += OnGameplayRecordingFailed;

        _isGameplayPreviewRecordingSession = previewSession;
        if (previewSession)
        {
            IsGameplayRecording = false;
            StopRecordingTimers();
        }

        settings.SuspendGameplayRecordingAudioLevelMonitor();

        if (!recorder.TryStart(
                outputDirectory,
                settings.GameplayRecordingContainer,
                settings.GameplayRecordingVideoCodec,
                settings.GameplayRecordingFps,
                settings.GameplayRecordingBitrateKbps,
                ResolveGameplayRecordingProcessId(),
                OperatingSystem.IsLinux() ? _linuxCompositorPid : 0))
        {
            _isGameplayPreviewRecordingSession = false;
            settings.ResumeGameplayRecordingAudioLevelMonitor();
            recorder.RecordingStateChanged -= OnGameplayRecordingStateChanged;
            recorder.RecordingFailed -= OnGameplayRecordingFailed;

            if (previewSession)
                CleanupGameplayPreviewRecordingSession("Could not start gameplay preview recording.");
            else
                GameplayRecordingStatus = "Could not start gameplay recording.";

            return false;
        }

        ConfigureCaptureGameplayRecording(_activeCaptureHost);
        return true;
    }

    private void StopGameplayRecording()
    {
        ResolveGameplayRecorder()?.Stop();
    }

    private void OnGameplayRecordingStateChanged(bool isRecording)
    {
        if (_isGameplayPreviewRecordingSession)
        {
            HandleGameplayPreviewRecordingStateChanged(isRecording);
            return;
        }

        IsGameplayRecording = isRecording;
        if (!isRecording)
        {
            StopRecordingTimers();
            GameplayRecordingStatus = "Recording saved.";
            ConfigureCaptureGameplayRecording(_activeCaptureHost);
            SettingsViewModel?.ResumeGameplayRecordingAudioLevelMonitor();
        }
        else
        {
            StartRecordingTimers();
            GameplayRecordingStatus = "Recording…";
        }

        NotifyGameplayRecordingAvailabilityChanged();
    }

    private void OnGameplayRecordingFailed(string message)
    {
        if (_isGameplayPreviewRecordingSession)
        {
            HandleGameplayPreviewRecordingFailed(message);
            return;
        }

        StopRecordingTimers();
        GameplayRecordingStatus = message;
        IsGameplayRecording = false;
        SettingsViewModel?.ResumeGameplayRecordingAudioLevelMonitor();
        NotifyGameplayRecordingAvailabilityChanged();
    }

    private IGameplayRecorder? ResolveGameplayRecorder()
    {
        if (OperatingSystem.IsLinux())
            return _linuxGameplayRecorder ?? DiLocator.ResolveViewModel<LinuxGameplayRecorderService>();

        return _gameplayRecorder ?? DiLocator.ResolveViewModel<GameplayRecorderService>();
    }

    private string ResolveRecordingUnavailableReason()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return "Gameplay recording is not supported on this platform.";

        if (!IsEmulatorRunning)
            return "Start a game before recording.";

        if (!IsCompositionCaptureVisible)
            return "Show the emulator viewport to record.";

        if (IsEmulatorLaunchInProgress)
            return "Wait for the emulator launch to finish.";

        return "Recording is not available right now.";
    }

    internal void NotifyGameplayRecordingSessionContext()
    {
        if (!OperatingSystem.IsLinux())
            return;

        SettingsViewModel?.SetGameplayRecordingSessionContext(
            _linuxCompositorPid,
            ResolveGameplayRecordingProcessId());
    }

    internal void RefreshGameplayRecordingSessionPids()
    {
        if (!OperatingSystem.IsLinux())
            return;

        SettingsViewModel?.ApplyGameplayRecordingSessionPids(
            _linuxCompositorPid,
            ResolveGameplayRecordingProcessId());
    }

    private int ResolveGameplayRecordingProcessId()
    {
        if (OperatingSystem.IsLinux() && _linuxCompositorPid > 0)
        {
            var compositorRoot = LinuxCompositorProcessHelper.ResolveCompositorRootPid(_linuxCompositorPid);
            var primaryEmulatorPid = LinuxCompositorProcessHelper.FindPrimaryEmulatorPid(compositorRoot);
            if (primaryEmulatorPid > 0)
                return primaryEmulatorPid;
        }

        return EmulatorTargetProcessId;
    }

    private void StartRecordingTimers()
    {
        _recordingStartedUtc = DateTime.UtcNow;
        GameplayRecordingElapsedText = "00:00";
        GameplayRecordingCenterOpacity = 1.0;

        _recordingElapsedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingElapsedTimer.Tick -= OnRecordingElapsedTimerTick;
        _recordingElapsedTimer.Tick += OnRecordingElapsedTimerTick;
        _recordingElapsedTimer.Start();

        _recordingPulseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _recordingPulseTimer.Tick -= OnRecordingPulseTimerTick;
        _recordingPulseTimer.Tick += OnRecordingPulseTimerTick;
        _recordingPulseTimer.Start();
    }

    private void StopRecordingTimers()
    {
        if (_recordingElapsedTimer != null)
        {
            _recordingElapsedTimer.Stop();
            _recordingElapsedTimer.Tick -= OnRecordingElapsedTimerTick;
        }

        if (_recordingPulseTimer != null)
        {
            _recordingPulseTimer.Stop();
            _recordingPulseTimer.Tick -= OnRecordingPulseTimerTick;
        }

        GameplayRecordingElapsedText = "00:00";
        GameplayRecordingCenterOpacity = 1.0;
    }

    private void OnRecordingElapsedTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _recordingStartedUtc;
        GameplayRecordingElapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void OnRecordingPulseTimerTick(object? sender, EventArgs e) =>
        GameplayRecordingCenterOpacity = GameplayRecordingCenterOpacity < 0.7 ? 1.0 : 0.35;

    private EmulatorCaptureHost? _activeCaptureHost;

    internal void SetActiveCaptureHostForRecording(EmulatorCaptureHost? host)
    {
        _activeCaptureHost = host;
        ConfigureCaptureGameplayRecording(host);
        ReloadArcadeLockedCropOnCaptureHost();
        NotifyArcadePillarboxCropCommandsChanged();
    }

    internal Task SuspendActiveCaptureSessionAsync()
        => _activeCaptureHost?.SuspendNativeCaptureAsync() ?? Task.CompletedTask;

    internal Task AbandonActiveCaptureAfterCompositorExitAsync()
        => _activeCaptureHost?.AbandonCaptureAfterCompositorExitAsync() ?? Task.CompletedTask;
}
