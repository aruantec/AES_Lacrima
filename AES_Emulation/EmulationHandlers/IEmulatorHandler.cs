using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AES_Emulation.EmulationHandlers;

public interface IEmulatorHandler : INotifyPropertyChanged
{
    string HandlerId { get; }

    string SectionKey { get; }

    string SectionTitle { get; }

    string DisplayName { get; }

    string? LauncherPath { get; set; }

    string LauncherDisplayPath { get; }

    bool HasLauncherPath { get; }

    ICommand? BrowseLauncherCommand { get; set; }

    ICommand? ClearLauncherCommand { get; set; }

    bool IsActive { get; }

    bool IsPrepared { get; }

    void Prepare();

    void OnShowViewModel();

    void OnViewFullyVisible();

    void OnLeaveViewModel();

    void SaveSettings();

    void LoadSettings();

    Task LoadSettingsAsync();

    bool CanHandleAlbumTitle(string? albumTitle);

    bool HideUntilCaptured { get; }

    bool ForceUseTargetClientAreaCapture { get; }

    int ClientAreaCropLeftInset { get; }

    int ClientAreaCropTopInset { get; }

    int ClientAreaCropRightInset { get; }

    int ClientAreaCropBottomInset { get; }

    int CaptureStartupDelayMs { get; }

    ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null);

    void PrepareProcessForCapture(Process process);

    void PrepareWindowForCapture(IntPtr hwnd);

    IntPtr FindPreferredWindowHandle(Process process);

    bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle);

    Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken);
}
