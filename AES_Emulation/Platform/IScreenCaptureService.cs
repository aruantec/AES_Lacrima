using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.Platform;

/// <summary>
/// Defines an abstraction for screen capture operations across different platforms.
/// This service is used by emulator handlers to find and prepare target windows for capture.
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>
    /// Attempts to find the best window handle for a given process to be used as a capture target.
    /// </summary>
    IntPtr FindPreferredWindowHandle(Process process);

    /// <summary>
    /// Performs platform-specific preparations on a process before it can be captured.
    /// </summary>
    void PrepareProcessForCapture(Process process);

    /// <summary>
    /// Performs platform-specific preparations on a specific window handle before capture.
    /// </summary>
    void PrepareWindowForCapture(IntPtr hwnd);

    /// <summary>
    /// Resolves the final capture target handle asynchronously, accounting for window stability.
    /// </summary>
    Task<IntPtr> ResolveCaptureTargetAsync(Process process, AES_Emulation.EmulationHandlers.IEmulatorHandler handler, CancellationToken cancellationToken);
    
    /// <summary>
    /// Gets a value indicating whether the window should be hidden from the desktop view until it is captured.
    /// </summary>
    bool HideUntilCaptured { get; }
}
