using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AES_Core.DI;
using AES_Core.Logging;
using AES_Emulation.EmulationHandlers;
using AES_Emulation.Windows.API;
using log4net;

namespace AES_Emulation.Windows.Platform;

[SupportedOSPlatform("windows")]
[AutoRegister(DependencyLifetime.Singleton)]
public class WindowsScreenCaptureService : AES_Emulation.Platform.IScreenCaptureService
{
    private static readonly ILog SLog = LogHelper.For<WindowsScreenCaptureService>();

    public bool HideUntilCaptured => true; // Or make this configurable

    public IntPtr FindPreferredWindowHandle(Process process)
    {
        return FindBestProcessWindowHandle(process, preferSpecificRenderWindow: false, allowHiddenWindows: false, isPreferredRenderWindow: null);
    }

    public void PrepareProcessForCapture(Process process)
    {
        HideProcessWindowsForCapture(process);
    }

    public void PrepareWindowForCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Win32API.RemoveWindowDecorations(hwnd);
        Win32API.MoveAway(hwnd);
        Win32API.SetWindowOpacity(hwnd, 0);
    }

    public async Task<IntPtr> ResolveCaptureTargetAsync(Process process, IEmulatorHandler handler, CancellationToken cancellationToken)
    {
        const int maxAttempts = 300;
        const int delayMs = 100;
        const int stableAttemptsBeforeAssign = 2;
        const int stableAttemptsBeforeStop = 6;

        IntPtr observedHwnd = IntPtr.Zero;
        var observedStableAttempts = 0;
        IntPtr assignedHwnd = IntPtr.Zero;
        var assignedStableAttempts = 0;
        var hasAssignedHandle = false;

        TryWaitForInputIdle(process, 500);

        if (handler.CaptureStartupDelayMs > 0)
        {
            try
            {
                await Task.Delay(handler.CaptureStartupDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HideUntilCaptured)
                handler.PrepareProcessForCapture(process);

            var hwnd = handler.FindPreferredWindowHandle(process);
            if (hwnd != IntPtr.Zero)
            {
                if (HideUntilCaptured)
                    handler.PrepareWindowForCapture(hwnd);

                if (!handler.CanAssignWindow(hwnd, process.MainWindowHandle))
                {
                    observedHwnd = IntPtr.Zero;
                    observedStableAttempts = 0;
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (hwnd == observedHwnd)
                {
                    observedStableAttempts++;
                }
                else
                {
                    observedHwnd = hwnd;
                    observedStableAttempts = 1;
                }

                if (hwnd != assignedHwnd && observedStableAttempts >= stableAttemptsBeforeAssign)
                {
                    assignedHwnd = hwnd;
                    assignedStableAttempts = observedStableAttempts;
                    hasAssignedHandle = true;
                }
                else if (hwnd == assignedHwnd)
                {
                    assignedStableAttempts = observedStableAttempts;
                }

                if (hasAssignedHandle && assignedStableAttempts >= stableAttemptsBeforeStop)
                    break;
            }
            else
            {
                observedHwnd = IntPtr.Zero;
                observedStableAttempts = 0;
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return assignedHwnd;
    }

    private static void TryWaitForInputIdle(Process process, int timeoutMs)
    {
        try { process.WaitForInputIdle(timeoutMs); } catch { }
    }

    private static void HideProcessWindowsForCapture(Process process)
    {
        process.Refresh();
        uint processId = (uint)process.Id;

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
                return true;

            if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId)
                return true;

            Win32API.RemoveWindowDecorations(hwnd);
            Win32API.MoveAway(hwnd);
            Win32API.SetWindowOpacity(hwnd, 0);
            return true;
        }, IntPtr.Zero);
    }

    private static IntPtr FindBestProcessWindowHandle(Process process, bool preferSpecificRenderWindow, bool allowHiddenWindows, Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow)
    {
        process.Refresh();
        uint processId = (uint)process.Id;
        IntPtr mainWindowHandle = process.MainWindowHandle;

        IntPtr bestHandle = IntPtr.Zero;
        long bestScore = long.MinValue;

        EnumWindows((hwnd, _) =>
        {
            var score = ScoreProcessWindowCandidate(hwnd, processId, mainWindowHandle, preferSpecificRenderWindow, allowHiddenWindows, isPreferredRenderWindow);
            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        return bestHandle != IntPtr.Zero ? bestHandle : mainWindowHandle;
    }

    private static long ScoreProcessWindowCandidate(IntPtr hwnd, uint processId, IntPtr mainWindowHandle, bool preferSpecificRenderWindow, bool allowHiddenWindows, Func<IntPtr, IntPtr, bool>? isPreferredRenderWindow)
    {
        if (hwnd == IntPtr.Zero) return long.MinValue;
        var isVisible = IsWindowVisible(hwnd);
        if (!isVisible && !allowHiddenWindows) return long.MinValue;
        if (GetWindowThreadProcessId(hwnd, out uint windowPid) == 0 || windowPid != processId) return long.MinValue;
        if (!GetWindowRect(hwnd, out RECT windowRect)) return long.MinValue;

        var width = Math.Max(0, windowRect.Right - windowRect.Left);
        var height = Math.Max(0, windowRect.Bottom - windowRect.Top);
        if (width <= 0 || height <= 0) return long.MinValue;

        long score = (long)width * height * 10;
        score += isVisible ? 100_000 : -100_000;
        if (GetWindow(hwnd, 4) == IntPtr.Zero) score += 1_000_000; // GW_OWNER
        if (hwnd == mainWindowHandle) score += 750_000;
        if (width >= 640 && height >= 360) score += 250_000;

        return score;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
