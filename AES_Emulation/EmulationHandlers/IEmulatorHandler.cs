using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public interface IEmulatorHandler : INotifyPropertyChanged
{
    string HandlerId { get; }

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

    ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen);

    void PrepareProcessForCapture(Process process);

    void PrepareWindowForCapture(IntPtr hwnd);

    IntPtr FindPreferredWindowHandle(Process process);

    bool CanAssignWindow(IntPtr hwnd, IntPtr mainWindowHandle);
}
