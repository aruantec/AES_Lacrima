using AES_Emulation.Windows.API;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Emulation.EmulationHandlers;

public sealed class AresHandler : EmulatorHandlerBase
{
    public static AresHandler Instance { get; } = new();

    private AresHandler()
    {
    }

    public override string HandlerId => "ares";

    public override string SectionKey => "ARES";

    public override string SectionTitle => "Ares";

    public override string DisplayName => "Ares";

    public override bool CanHandleAlbumTitle(string? albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return false;

        return string.Equals(albumTitle, SectionTitle, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, SectionKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "SNES", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Super Nintendo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Super Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "NES", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo Entertainment System", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "N64", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(albumTitle, "Nintendo 64", StringComparison.OrdinalIgnoreCase);
    }

    public override ProcessStartInfo BuildStartInfo(string launcherPath, string romPath, bool startFullscreen, string? sectionTitle = null, string? selectedRetroArchCore = null)
    {
        var startInfo = base.BuildStartInfo(launcherPath, romPath, startFullscreen, sectionTitle);

        if (startFullscreen)
            startInfo.ArgumentList.Insert(0, "--fullscreen");

        return startInfo;
    }

    public override int CaptureStartupDelayMs => 1500;

    public override async Task<IntPtr> ResolveCaptureTargetAsync(Process process, CancellationToken cancellationToken)
    {
        await Task.Delay(CaptureStartupDelayMs, cancellationToken).ConfigureAwait(false);

        await CancelN64DdFileDialogAsync(process, cancellationToken).ConfigureAwait(false);

        var targetHwnd = await base.ResolveCaptureTargetAsync(process, cancellationToken).ConfigureAwait(false);
        if (targetHwnd != IntPtr.Zero)
        {
            // Ares requires a borderless capture target for WGC to work reliably.
            Win32API.RemoveWindowDecorations(targetHwnd);
        }

        return targetHwnd;
    }

    private static bool TryGetWindowArea(IntPtr hwnd, out long area)
    {
        area = 0;
        if (hwnd == IntPtr.Zero)
            return false;

        if (!GetWindowRect(hwnd, out var rect))
            return false;

        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        area = (long)width * height;
        return area > 0;
    }

    private static IntPtr FindLargestVisibleChildWindow(IntPtr parentHwnd)
    {
        if (parentHwnd == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr bestHwnd = IntPtr.Zero;
        long bestArea = 0;

        EnumChildWindows(parentHwnd, (child, _) =>
        {
            if (child == IntPtr.Zero)
                return true;

            if (!IsWindowVisible(child))
                return true;

            if (!GetWindowRect(child, out var rect))
                return true;

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            var area = (long)width * height;
            if (area <= 0)
                return true;

            if (area > bestArea)
            {
                bestArea = area;
                bestHwnd = child;
            }

            return true;
        }, IntPtr.Zero);

        return bestHwnd;
    }

    private static async Task CancelN64DdFileDialogAsync(Process process, CancellationToken cancellationToken)
    {
        const int pollIntervalMs = 100;
        const int initialTimeoutMs = 2500;
        const int stableTimeoutMs = 1000;

        var deadline = Environment.TickCount + initialTimeoutMs;
        var stableUntil = 0;
        var canceled = false;

        while (Environment.TickCount < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dialogHwnd = FindN64DdDialogWindow(process);
            if (dialogHwnd != IntPtr.Zero)
            {
                canceled = true;
                TryCancelDialog(dialogHwnd);
                stableUntil = Environment.TickCount + stableTimeoutMs;
            }
            else if (canceled && Environment.TickCount >= stableUntil)
            {
                return;
            }

            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        if (canceled)
            await Task.Delay(stableTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private static IntPtr FindN64DdDialogWindow(Process process)
    {
        foreach (var hwnd in EnumerateProcessTopLevelWindows(process, includeHiddenWindows: true))
        {
            if (hwnd == IntPtr.Zero)
                continue;

            var title = GetWindowTitle(hwnd).Trim();
            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (title.Contains("Load Nintendo 64DD Game", StringComparison.OrdinalIgnoreCase))
                return hwnd;
        }

        return IntPtr.Zero;
    }

    private static void TryCancelDialog(IntPtr dialogHwnd)
    {
        if (TryClickDialogCancelButton(dialogHwnd))
            return;

        if (!SendCloseCommand(dialogHwnd))
            PostMessage(dialogHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool TryClickDialogCancelButton(IntPtr dialogHwnd)
    {
        var clicked = false;

        EnumChildWindows(dialogHwnd, (child, _) =>
        {
            if (clicked)
                return false;

            var className = GetWindowClassName(child);
            if (!string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase))
                return true;

            var buttonText = GetWindowTitle(child).Trim();
            if (string.IsNullOrWhiteSpace(buttonText))
                return true;

            if (buttonText.Equals("Cancel", StringComparison.OrdinalIgnoreCase) ||
                buttonText.Equals("Abort", StringComparison.OrdinalIgnoreCase) ||
                buttonText.Equals("No", StringComparison.OrdinalIgnoreCase))
            {
                SendMessage(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                clicked = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return clicked;
    }

    private static bool SendCloseCommand(IntPtr dialogHwnd)
    {
        const int IDCANCEL = 0x0002;
        const int BN_CLICKED = 0;
        var result = SendMessage(dialogHwnd, WM_COMMAND, new IntPtr((BN_CLICKED << 16) | IDCANCEL), IntPtr.Zero);
        return result != IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint BM_CLICK = 0x00F5;
}
