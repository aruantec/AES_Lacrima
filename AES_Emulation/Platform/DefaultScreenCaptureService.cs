using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;

namespace AES_Emulation.Platform;

/// <summary>
/// Default no-op or basic implementation of IScreenCaptureService for platforms 
/// where advanced window capture/preparation is not yet supported or needed.
/// </summary>
[AutoRegister(DependencyLifetime.Singleton)]
public class DefaultScreenCaptureService : IScreenCaptureService
{
    public bool HideUntilCaptured => false;

    public IntPtr FindPreferredWindowHandle(Process process) => process.MainWindowHandle;

    public void PrepareProcessForCapture(Process process) { }

    public void PrepareWindowForCapture(IntPtr hwnd) { }

    public Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        return Task.FromResult(process.MainWindowHandle);
    }
}
