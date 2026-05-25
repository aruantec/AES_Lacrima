using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AES_Emulation.Windows.API;

/// <summary>
/// Hides the mouse cursor after a period of inactivity while fullscreen, and restores it on movement.
/// Uses Win32 cursor polling so hiding works over native capture surfaces (airspace).
/// </summary>
public sealed class FullscreenCursorAutoHideHelper : IDisposable
{
    private static readonly TimeSpan IdleDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly InputElement? _cursorScope;

    private DispatcherTimer? _pollTimer;
    private Cursor? _savedCursor;
    private NativePoint _lastCursorPos;
    private DateTime _lastMovementUtc;
    private bool _isHidden;
    private bool _didHideSystemCursor;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    public FullscreenCursorAutoHideHelper(InputElement? cursorScope = null)
    {
        _cursorScope = cursorScope;
    }

    public void Start()
    {
        if (_pollTimer != null)
            return;

        _lastMovementUtc = DateTime.UtcNow;
        _isHidden = false;
        _didHideSystemCursor = false;
        _savedCursor = null;

        if (OperatingSystem.IsWindows())
            GetCursorPos(out _lastCursorPos);

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    public void NotifyPointerActivity()
    {
        _lastMovementUtc = DateTime.UtcNow;
        ShowCursorNow();
    }

    public void Stop()
    {
        if (_pollTimer != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }

        ShowCursorNow();
    }

    public void Dispose() => Stop();

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetCursorPos(out var pos))
                return;

            if (pos.X != _lastCursorPos.X || pos.Y != _lastCursorPos.Y)
            {
                _lastCursorPos = pos;
                _lastMovementUtc = DateTime.UtcNow;
                ShowCursorNow();
                return;
            }
        }

        if (!_isHidden && DateTime.UtcNow - _lastMovementUtc >= IdleDuration)
            HideCursorNow();
    }

    private void HideCursorNow()
    {
        if (_isHidden)
            return;

        _isHidden = true;
        HideSystemCursor();
        SetAvaloniaCursorHidden(true);
    }

    private void ShowCursorNow()
    {
        if (!_isHidden && !_didHideSystemCursor && _savedCursor == null)
            return;

        _isHidden = false;
        RestoreSystemCursor();
        SetAvaloniaCursorHidden(false);
    }

    private void HideSystemCursor()
    {
        if (!OperatingSystem.IsWindows() || _didHideSystemCursor)
            return;

        int displayCount;
        var guard = 0;
        do
        {
            displayCount = ShowCursor(false);
            guard++;
        } while (displayCount >= 0 && guard < 128);

        _didHideSystemCursor = true;
    }

    private void RestoreSystemCursor()
    {
        if (!OperatingSystem.IsWindows() || !_didHideSystemCursor)
            return;

        int displayCount;
        var guard = 0;
        do
        {
            displayCount = ShowCursor(true);
            guard++;
        } while (displayCount < 0 && guard < 128);

        _didHideSystemCursor = false;
    }

    private void SetAvaloniaCursorHidden(bool hidden)
    {
        if (_cursorScope == null)
            return;

        if (hidden)
        {
            _savedCursor ??= _cursorScope.Cursor;
            _cursorScope.Cursor = new Cursor(StandardCursorType.None);
            return;
        }

        _cursorScope.Cursor = _savedCursor ?? Cursor.Default;
        _savedCursor = null;
    }
}
