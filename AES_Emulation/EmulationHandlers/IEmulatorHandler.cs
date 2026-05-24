using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AES_Emulation.Controls;

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

    bool IsLauncherPathValid(string? launcherPath);

    string? NormalizeLauncherPath(string? launcherPath);

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

    /// <summary>
    /// When true, hide/move/opacity run only after capture attaches (PCSX2-style), not during HWND resolution.
    /// </summary>
    bool DeferWindowHidingUntilCaptured { get; }

    bool ForceUseTargetClientAreaCapture { get; }

    bool EnableCapturePillarboxCrop { get; }

    bool UsesRetroArchCores { get; }

    int ClientAreaCropLeftInset { get; }

    int ClientAreaCropTopInset { get; }

    int ClientAreaCropRightInset { get; }

    int ClientAreaCropBottomInset { get; }

    /// <summary>
    /// When set, the embedded capture window is sized to this width/height ratio before capture (prep only).
    /// </summary>
    double? CaptureWindowAspectRatio { get; }

    int CaptureStartupDelayMs { get; }
    
    bool IsWindowEmbeddingSupported { get; }

    EmulatorCaptureMode PreferredCaptureMode { get; }

    ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null);

    void PrepareProcessForCapture(Process process);

    void PrepareWindowForCapture(IntPtr hwnd);

    /// <summary>Decorations off + aspect resize only; used when deferred capture attaches.</summary>
    void PrepareWindowForCaptureAttach(IntPtr hwnd);

    IntPtr FindPreferredWindowHandle(Process process);

    bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle);

    Task<Process?> ResolveRuntimeProcessAsync(Process process, CancellationToken cancellationToken);

    Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken);
}
