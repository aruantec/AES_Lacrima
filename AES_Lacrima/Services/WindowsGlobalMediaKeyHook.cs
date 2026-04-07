using Avalonia.Threading;
using log4net;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AES_Lacrima.Services;

public enum GlobalMediaKey
{
    PlayPause,
    Next,
    Previous
}

/// <summary>
/// Captures media hardware keys globally on Windows using a low-level keyboard hook.
/// </summary>
public sealed class WindowsGlobalMediaKeyHook : IDisposable
{
    private static readonly ILog Log = AES_Core.Logging.LogHelper.For<WindowsGlobalMediaKeyHook>();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    private readonly HookProc _hookCallback;
    private IntPtr _hookHandle;
    private bool _disposed;

    public event Action<GlobalMediaKey>? MediaKeyPressed;

    public WindowsGlobalMediaKeyHook()
    {
        _hookCallback = HookProcedure;
    }

    public void Start()
    {
        if (_disposed || _hookHandle != IntPtr.Zero)
            return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero;

        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install global media key hook.");
        }

        Log.Info("Global media key hook installed.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_hookHandle))
            {
                Log.Warn("Failed to uninstall global media key hook.");
            }

            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookProcedure(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var key = data.VkCode;

            GlobalMediaKey? mappedKey = key switch
            {
                VK_MEDIA_NEXT_TRACK => GlobalMediaKey.Next,
                VK_MEDIA_PREV_TRACK => GlobalMediaKey.Previous,
                VK_MEDIA_PLAY_PAUSE => GlobalMediaKey.PlayPause,
                _ => null
            };

            if (mappedKey.HasValue)
            {
                Dispatcher.UIThread.Post(() => MediaKeyPressed?.Invoke(mappedKey.Value), DispatcherPriority.Input);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}